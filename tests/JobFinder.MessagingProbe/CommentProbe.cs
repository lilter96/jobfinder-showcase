using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;

namespace JobFinder.MessagingProbe;

public static class CommentProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var source = args[0] == "--comment-source-consumer";
        var delivery = args[0] == "--comment-delivery-consumer";
        if (!Guid.TryParseExact(args[1], "N", out var id)) return 2;
        var phase = CommentPreparationStage.Drafting;
        var sourcePhase = CommentSourceStage.Queued;
        var deliveryPhase = CommentDeliveryStage.Sending;
        if (delivery)
        {
            if (!Enum.TryParse(args[2], out deliveryPhase) || deliveryPhase is not (CommentDeliveryStage.Sending or CommentDeliveryStage.Completed)
                || args[3] is not ("before-checkpoint" or "after-checkpoint" or "before-effect" or "after-effect")) return 2;
        }
        else if (source)
        {
            if (!Enum.TryParse(args[2], out sourcePhase) || sourcePhase is not (CommentSourceStage.Queued or CommentSourceStage.Waiting)
                || args[3] is not ("before-checkpoint" or "after-checkpoint")) return 2;
        }
        else if (!Enum.TryParse(args[2], out phase) || phase is not (CommentPreparationStage.Drafting or CommentPreparationStage.Checking)
            || args[3] is not ("before-effect" or "after-effect" or "after-checkpoint")) return 2;
        var connectionString = Environment.GetEnvironmentVariable("JOBFINDER_DISPOSABLE_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) return 2;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "postgres" || connection.Port is 5432 or 5433) return 2;
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.ClearProviders();
            var root = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-comment-probe", id.ToString("N"));
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString, ["Paths:Prompts"] = Path.Combine(AppContext.BaseDirectory, "prompts"),
                ["Paths:Data"] = Path.Combine(root, "data"), ["Paths:Profile"] = Path.Combine(root, "profile"), ["Llm:ApiKey"] = "synthetic-probe-key",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                if (delivery) options.AddInterceptors(new CommentDeliveryProbeCheckpoint(id, deliveryPhase, args[3]));
                else if (source) options.AddInterceptors(new PauseSourceCheckpoint(id, sourcePhase, args[3]));
                else if (args[3] == "after-checkpoint") options.AddInterceptors(new PauseAfterCommit(id, phase));
            });
            builder.Services.RemoveAll<ICommentPreparationSteps>();
            builder.Services.AddSingleton<ICommentPreparationSteps>(provider => new CommentProbeSteps(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, source ? null : phase, args[3], source));
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
            builder.Services.RemoveAll<ICommentDeliveryTransport>();
            builder.Services.AddSingleton<ICommentDeliveryTransport>(provider => new CommentDeliveryProbeTransport(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), delivery ? id : null, args[3]));
            builder.AddJobFinderMessaging(consumer: true);
            builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
            builder.Services.AddJobFinderInitialization();
            using var host = builder.Build();
            await host.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString().Replace(connectionString, "<test database>").Replace(connection.Password ?? "<unset>", "<redacted>"));
            return 1;
        }
    }

    private sealed class PauseSourceCheckpoint(Guid id, CommentSourceStage phase, string boundary) : DbTransactionInterceptor
    {
        private int _armed = 1;

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (boundary == "before-checkpoint") await PauseAsync(eventData.Context!, ct);
            return result;
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) =>
            boundary == "after-checkpoint" ? PauseAsync(eventData.Context!, ct) : Task.CompletedTask;

        private async Task PauseAsync(DbContext context, CancellationToken ct)
        {
            var next = phase == CommentSourceStage.Queued ? CommentSourceStage.Waiting : CommentSourceStage.Completed;
            if (context.ChangeTracker.Entries<CommentSourceRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == next)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Console.WriteLine("READY");
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }
    }

    private sealed class PauseAfterCommit(Guid id, CommentPreparationStage phase) : DbTransactionInterceptor
    {
        private int _armed = 1;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            var next = phase == CommentPreparationStage.Drafting ? CommentPreparationStage.Checking : CommentPreparationStage.Completed;
            if (eventData.Context!.ChangeTracker.Entries<CommentPreparationRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == next)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Console.WriteLine("READY");
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("process-kill comment probe cannot contact a provider or recipient");
    }
}

public sealed class CommentProbeSteps(IDbContextFactory<JobFinderDbContext> db, Guid id, CommentPreparationStage? phase = null,
    string? boundary = null, bool source = false) : ICommentPreparationSteps
{
    public static string CounterKey(Guid id, CommentPreparationStage phase) => $"probe.comment.{id:N}.{phase}";

    public async Task<CommentPlan> DraftAsync(CommentPreparationInput input, string? remarks, CancellationToken ct)
    {
        if (!source && input.Operation.OperationId != id || remarks is not null) throw new UnsupportedCommentPreparationException();
        if (phase == CommentPreparationStage.Drafting && boundary == "before-effect") await PauseAsync(ct);
        await CountAsync(CommentPreparationStage.Drafting, ct);
        if (phase == CommentPreparationStage.Drafting && boundary == "after-effect") await PauseAsync(ct);
        return new CommentPlan
        {
            Audience = "recruiter", WorthCommenting = true, Reason = "Короткий вопрос о процессе",
            Variants = ["А какой срок ответа вы обычно называете кандидату?", "Я бы заранее договаривался о дате следующего разговора."],
        };
    }

    public CommentPreparationRound Check(CommentPreparationInput input, CommentPlan plan)
    {
        if (!source && input.Operation.OperationId != id) throw new UnsupportedCommentPreparationException();
        if (phase == CommentPreparationStage.Checking && boundary == "before-effect") PauseAsync(CancellationToken.None).GetAwaiter().GetResult();
        CountAsync(CommentPreparationStage.Checking, CancellationToken.None).GetAwaiter().GetResult();
        if (phase == CommentPreparationStage.Checking && boundary == "after-effect") PauseAsync(CancellationToken.None).GetAwaiter().GetResult();
        var (good, remarks) = CommentPlanner.Review(plan, input.ExampleShingles.ToHashSet(StringComparer.Ordinal));
        return new() { Plan = plan, Good = good, Remarks = remarks };
    }

    private async Task CountAsync(CommentPreparationStage stage, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id, stage);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now())
            ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()
            """, ct);
    }

    private static async Task PauseAsync(CancellationToken ct)
    {
        Console.WriteLine("READY");
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
