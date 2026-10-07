using System.Data.Common;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Resume;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;

namespace JobFinder.MessagingProbe;

public static class ResumeProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!Guid.TryParseExact(args[1], "N", out var id) || !Enum.TryParse<ResumePreparationStage>(args[2], out var phase)
            || phase is not (ResumePreparationStage.Printing or ResumePreparationStage.Reviewing or ResumePreparationStage.Finalizing)
            || args[3] is not ("before-effect" or "after-effect" or "after-checkpoint")) return 2;
        var connectionString = Environment.GetEnvironmentVariable("JOBFINDER_DISPOSABLE_TEST_DATABASE");
        var artifacts = Environment.GetEnvironmentVariable("JOBFINDER_DISPOSABLE_TEST_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(artifacts)) return 2;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "postgres" || connection.Port is 5432 or 5433) return 2;
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString,
                ["Paths:Prompts"] = Path.Combine(AppContext.BaseDirectory, "prompts"),
                ["Paths:Profile"] = Path.Combine(artifacts, "probe-profile"), ["Paths:Data"] = Path.Combine(artifacts, "probe-data"),
                ["Llm:ApiKey"] = "synthetic-disposable-probe-key",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                if (args[3] == "after-checkpoint") options.AddInterceptors(new PauseAfterCommit(id, phase));
            });
            builder.Services.RemoveAll<IResumePreparationSteps>();
            builder.Services.AddSingleton<IResumePreparationSteps>(provider => new ResumeProbeSteps(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, phase, args[3]));
            builder.AddJobFinderMessaging(consumer: true);
            builder.Services.ConfigureWolverine(FastRecovery);
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

    public static void FastRecovery(WolverineOptions options)
    {
        options.Durability.OrphanedMessageSweepPollingTime = TimeSpan.FromMilliseconds(200);
        options.Durability.NodeReassignmentPollingTime = TimeSpan.FromMilliseconds(200);
        options.Durability.HealthCheckPollingTime = TimeSpan.FromMilliseconds(200);
        options.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(1);
    }

    private sealed class PauseAfterCommit(Guid id, ResumePreparationStage phase) : DbTransactionInterceptor
    {
        private int _armed = 1;
        private readonly ResumePreparationStage _next = phase switch
        {
            ResumePreparationStage.Printing => ResumePreparationStage.Reviewing,
            ResumePreparationStage.Reviewing => ResumePreparationStage.Finalizing,
            ResumePreparationStage.Finalizing => ResumePreparationStage.Completed,
            _ => throw new ArgumentException("unsupported pause phase"),
        };

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ResumePreparationRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == _next)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Console.WriteLine("READY");
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }
    }
}

public sealed class ResumeProbeSteps(IDbContextFactory<JobFinderDbContext> db, Guid id,
    ResumePreparationStage? pauseAt = null, string? boundary = null) : IResumePreparationSteps
{
    public static string CounterKey(Guid id, ResumePreparationStage phase) => $"probe.resume.{id:N}.{phase}";

    private async Task CalledAsync(ResumePreparationStage phase, CancellationToken ct)
    {
        if (pauseAt == phase && boundary == "before-effect") await PauseAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id, phase);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now())
            ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()
            """, ct);
        if (pauseAt == phase && boundary == "after-effect") await PauseAsync(ct);
    }

    private static async Task PauseAsync(CancellationToken ct)
    {
        Console.WriteLine("READY");
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    public async Task<ResumeRound> DraftAsync(ResumePreparationInput input, int round, ResumeRound? best, CancellationToken ct)
    {
        await CalledAsync(ResumePreparationStage.Drafting, ct);
        return new ResumeRound { Number = round, Model = "synthetic-process-writer",
            Document = new ResumeDocument { Language = "en", Headline = "Senior .NET Developer", Summary = "Synthetic process test document" } };
    }

    public async Task<ResumeRound> PrintAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct)
    {
        await CalledAsync(ResumePreparationStage.Printing, ct);
        return current with { Pages = 1, PdfPath = "synthetic-process.pdf", PdfHash = "synthetic-process-hash", PageTexts = ["Synthetic process test document"] };
    }

    public async Task<ResumeRound> ReviewAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct)
    {
        await CalledAsync(ResumePreparationStage.Reviewing, ct);
        return current with { Review = new ResumeReview { Checks = ResumeReview.Criteria.Keys.Select(key => new ReviewCheck { Id = key, Passed = true }).ToList() }.Scored() };
    }

    public async Task<TailoredResume> FinishAsync(ResumePreparationInput input, ResumeRound best, CancellationToken ct)
    {
        await CalledAsync(ResumePreparationStage.Finalizing, ct);
        return new TailoredResume
        {
            VacancyId = input.Vacancy.Id, Language = best.Document.Language, Model = best.Model, CreatedAt = DateTimeOffset.UtcNow,
            ContentJson = JsonSerializer.Serialize(best.Document, ResumeJsonContext.Default.ResumeDocument), PdfPath = best.PdfPath!, FileName = "Synthetic_Process_CV.pdf",
            ReportJson = JsonSerializer.Serialize(new ResumeReport { Ready = best.Ready, Review = best.Review, Pages = best.Pages,
                InputHash = input.Recipe.Hash, Recipe = input.Recipe, PdfHash = best.PdfHash! }, ResumeJsonContext.Default.ResumeReport),
        };
    }
}
