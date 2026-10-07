using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
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

public static class ReplyProbe
{
    public const string Marker = "Synthetic incoming reply process probe";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Guid.TryParseExact(args[1], "N", out var id) || !Enum.TryParse<ReplyProcessingStage>(args[2], out var phase)
            || phase is not (ReplyProcessingStage.Capturing or ReplyProcessingStage.Fast or ReplyProcessingStage.Deep or ReplyProcessingStage.Publishing)
            || args[3] is not ("before-checkpoint" or "after-checkpoint" or "before-effect" or "after-effect")
            || args[3].EndsWith("effect", StringComparison.Ordinal) && phase is not (ReplyProcessingStage.Fast or ReplyProcessingStage.Deep)) return 2;
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
                ["Paths:Data"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-reply-probe", id.ToString("N")),
                ["Paths:Profile"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-reply-probe", id.ToString("N"), "probe-profile"),
                ["Llm:ApiKey"] = "synthetic-reply-probe-key", ["Llm:Proxy"] = "", ["Llm:Endpoint"] = "https://provider.invalid/completions",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                options.AddInterceptors(new Boundary(id, phase, args[3]));
            });
            builder.Services.RemoveAll<IReplyProcessingSteps>();
            builder.Services.AddSingleton<IReplyProcessingSteps>(provider => new ReplyProbeSteps(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, phase, args[3]));
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
            builder.AddJobFinderMessaging(consumer: true);
            builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
            builder.Services.AddJobFinderInitialization();
            using var host = builder.Build();
            await using (var context = await host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync())
            {
                var root = await context.ReplyProcessingRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id);
                if (root is null || root.Mode != ReplyProcessingMode.Incoming || !root.RawJson.Contains(Marker, StringComparison.Ordinal)) return 2;
            }
            await host.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }

    private sealed class Boundary(Guid id, ReplyProcessingStage phase, string boundary) : DbTransactionInterceptor
    {
        private int _armed = 1;
        private Task PauseAsync(DbContext context, CancellationToken ct)
        {
            var next = phase switch
            {
                ReplyProcessingStage.Capturing => ReplyProcessingStage.Fast, ReplyProcessingStage.Fast => ReplyProcessingStage.Deep,
                ReplyProcessingStage.Deep => ReplyProcessingStage.Publishing, _ => ReplyProcessingStage.Completed,
            };
            return context.ChangeTracker.Entries<ReplyProcessingRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == next)
                && Interlocked.Exchange(ref _armed, 0) == 1 ? CommentDeliveryProbeTransport.PauseAsync(ct) : Task.CompletedTask;
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (boundary == "before-checkpoint") await PauseAsync(eventData.Context!, ct);
            return result;
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) =>
            boundary == "after-checkpoint" ? PauseAsync(eventData.Context!, ct) : Task.CompletedTask;
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());
        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("reply process probe refuses external HTTP");
        }
    }
}

public sealed class ReplyProbeSteps(IDbContextFactory<JobFinderDbContext> db, Guid id, ReplyProcessingStage? phase = null, string? boundary = null) : IReplyProcessingSteps
{
    public static string CounterKey(Guid requestId, bool deep) => "probe.reply." + requestId.ToString("N") + (deep ? ".deep" : ".fast");
    public void EnsureCompatible(ReplyProcessingInput input)
    {
        if (input.Version != 1 || input.Message != ReplyProbe.Marker) throw new InvalidOperationException("synthetic reply input refused");
    }

    public async Task<ReplyProcessingResult> AnalyzeAsync(ReplyProcessingInput input, ReplyProcessingResult? best, bool deep, CancellationToken ct)
    {
        EnsureCompatible(input);
        var pausedStage = phase == (deep ? ReplyProcessingStage.Deep : ReplyProcessingStage.Fast);
        if (pausedStage && boundary == "before-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id, deep);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key,value,updated_at) VALUES ({key},'1',now()) ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()", ct);
        if (pausedStage && boundary == "after-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
        return new() { Analysis = new ReplyAnalysis { Classification = "other", Summary = "Synthetic process analysis", Intent = "technical",
            Counterpart = "human", Draft = "Спасибо, расскажу о решении на встрече." } };
    }
}
