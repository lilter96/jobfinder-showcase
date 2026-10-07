using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine;

namespace JobFinder.MessagingProbe;

public static class ChannelMembershipProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!Guid.TryParseExact(args[1], "N", out var id) || args[2] is not ("before-effect" or "after-effect" or "before-checkpoint" or "after-checkpoint")) return 2;
        var connectionString = Environment.GetEnvironmentVariable("JOBFINDER_DISPOSABLE_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) return 2;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "postgres" || connection.Port is 5432 or 5433) return 2;
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString, ["Paths:Prompts"] = Path.Combine(AppContext.BaseDirectory, "prompts"),
                ["Llm:ApiKey"] = "synthetic-probe-key", ["Telegram:Enabled"] = "false",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                options.AddInterceptors(new Checkpoint(id, args[2]));
            });
            builder.Services.RemoveAll<IChannelMembershipGateway>();
            builder.Services.AddSingleton<IChannelMembershipGateway>(provider => new ChannelMembershipProbeGateway(provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, args[2]));
            builder.AddJobFinderMessaging(true);
            builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
            builder.Services.AddJobFinderInitialization();
            using var host = builder.Build();
            await using (var context = await host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync())
            {
                var root = await context.ChannelMembershipRequests.SingleOrDefaultAsync(row => row.Id == id);
                if (root is null || !await context.TelegramChannels.AnyAsync(row => row.Id == root.ChannelId && row.Username == "synthetic")) return 2;
            }
            await host.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }

    private sealed class Checkpoint(Guid id, string boundary) : DbTransactionInterceptor
    {
        private int _armed = 1;
        private Task PauseAsync(DbContext context, CancellationToken ct) =>
            context.ChangeTracker.Entries<ChannelMembershipRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == ChannelMembershipStage.Muting)
                && Interlocked.Exchange(ref _armed, 0) == 1 ? CommentDeliveryProbeTransport.PauseAsync(ct) : Task.CompletedTask;
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            if (boundary == "before-checkpoint") await PauseAsync(eventData.Context!, ct);
            return result;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) =>
            boundary == "after-checkpoint" ? PauseAsync(eventData.Context!, ct) : Task.CompletedTask;
    }
}

public sealed class ChannelMembershipProbeGateway(IDbContextFactory<JobFinderDbContext> db, Guid id, string? boundary = null) : IChannelMembershipGateway
{
    public static string CounterKey(Guid id) => "probe.membership." + id.ToString("N");
    public long? AccountId => 42;
    public Task<ChannelMembershipChat?> ResolveAsync(ChannelMembershipInput input, long accountId, CancellationToken ct) =>
        Task.FromResult<ChannelMembershipChat?>(new() { Id = 123, AccessHash = 456, Title = "Synthetic", Username = "synthetic", Kind = ChannelKind.Channel });
    public async Task ApplyAsync(ChannelMembershipStage stage, ChannelMembershipChat chat, string folder, long accountId, Func<CancellationToken, Task> verify, CancellationToken ct)
    {
        await verify(ct);
        if (stage != ChannelMembershipStage.Joining) return;
        if (boundary == "before-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now())
            ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()
            """, ct);
        if (boundary == "after-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
    }
    public Task<ChannelMembershipHistory> HistoryAsync(ChannelMembershipChat chat, long accountId, CancellationToken ct) => Task.FromResult(new ChannelMembershipHistory());
}
