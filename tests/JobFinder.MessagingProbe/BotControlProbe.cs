using System.Data.Common;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Controls;
using JobFinder.Pipeline.Messaging;
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

public static class BotControlProbe
{
    public const string Marker = "Synthetic bot control process probe";
    public static string Payload => JsonSerializer.Serialize(new { marker = Marker });

    public static async Task<int> RunAsync(string[] args)
    {
        var producer = args[0] == "--bot-control-producer";
        if (!Guid.TryParseExact(args[1], "N", out var id) || (producer ? args[2] is not ("before-admission-commit" or "after-admission-commit")
            : args[2] is not ("before-claim-commit" or "after-claim-commit" or "before-effect" or "after-effect" or "before-completion-commit" or "after-completion-commit"))) return 2;
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
                ["Paths:Data"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-bot-control-probe", id.ToString("N")),
                ["Paths:Profile"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-bot-control-probe", id.ToString("N"), "probe-profile"),
                ["Llm:ApiKey"] = "synthetic-bot-control-probe-key", ["Llm:Proxy"] = "", ["Llm:Endpoint"] = "https://provider.invalid/completions",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                options.AddInterceptors(new Boundary(id, args[2]));
            });
            builder.Services.RemoveAll<IBotControlExecutor>();
            builder.Services.AddSingleton<IBotControlExecutor>(provider => new BotControlProbeExecutor(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, args[2]));
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
            builder.AddJobFinderMessaging(consumer: !producer);
            builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
            builder.Services.AddJobFinderInitialization();
            using var host = builder.Build();
            await using (var context = await host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync())
            {
                if (producer)
                {
                    if (await context.BotControlRequests.AnyAsync() || await context.Settings.AnyAsync(row => row.Key == "bot.poll.123456")) return 2;
                }
                else
                {
                    var root = await context.BotControlRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id);
                    if (root is null || root.BotId != 123456 || root.UpdateId != 10 || !root.InputJson.Contains(Marker, StringComparison.Ordinal)) return 2;
                }
            }
            await host.StartAsync();
            if (producer) await host.Services.GetRequiredService<BotControls>().AdmitAsync(123456, 777, null, [new() { UpdateId = 10, PayloadJson = Payload }], CancellationToken.None);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }

    private sealed class Boundary(Guid id, string boundary) : DbTransactionInterceptor
    {
        private int _armed = 1;
        private Task PauseAsync(DbContext context, CancellationToken ct)
        {
            var stage = boundary.Contains("admission", StringComparison.Ordinal) ? BotControlStage.Pending
                : boundary.Contains("claim", StringComparison.Ordinal) ? BotControlStage.Executing : BotControlStage.Completed;
            return context.ChangeTracker.Entries<BotControlRequest>().Any(entry => entry.Entity.Stage == stage
                && (boundary.Contains("admission", StringComparison.Ordinal) ? entry.Entity.InputJson.Contains(Marker, StringComparison.Ordinal) : entry.Entity.Id == id))
                && Interlocked.Exchange(ref _armed, 0) == 1 ? CommentDeliveryProbeTransport.PauseAsync(ct) : Task.CompletedTask;
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (boundary.StartsWith("before-", StringComparison.Ordinal) && boundary.EndsWith("-commit", StringComparison.Ordinal)) await PauseAsync(eventData.Context!, ct);
            return result;
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) =>
            boundary.StartsWith("after-", StringComparison.Ordinal) && boundary.EndsWith("-commit", StringComparison.Ordinal) ? PauseAsync(eventData.Context!, ct) : Task.CompletedTask;
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());
        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("bot process probe refuses external HTTP");
        }
    }
}

public sealed class BotControlProbeExecutor(IDbContextFactory<JobFinderDbContext> db, Guid id, string? boundary = null) : IBotControlExecutor
{
    public static string CounterKey(Guid requestId) => "probe.bot-control." + requestId.ToString("N");
    public bool CanExecute(BotControlInput input) => input.Version == 1 && input.BotId == 123456 && input.OwnerId == 777 && input.UpdateId == 10 && input.PayloadJson == BotControlProbe.Payload;

    public async Task ExecuteAsync(BotControlInput input, CancellationToken ct)
    {
        if (!CanExecute(input)) throw new InvalidOperationException("synthetic bot control permission refused");
        if (boundary == "before-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key,value,updated_at) VALUES ({key},'1',now()) ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()", ct);
        if (boundary == "after-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
    }
}
