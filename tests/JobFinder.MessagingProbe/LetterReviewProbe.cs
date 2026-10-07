using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;

namespace JobFinder.MessagingProbe;

public static class LetterReviewProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!Guid.TryParseExact(args[1], "N", out var id)
            || args[2] is not ("before-effect" or "after-effect" or "after-checkpoint")) return 2;
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
                ["Database:ConnectionString"] = connectionString,
                ["Paths:Prompts"] = Path.Combine(AppContext.BaseDirectory, "prompts"),
                ["Llm:ApiKey"] = "synthetic-disposable-probe-key",
                ["HeadHunter:Enabled"] = "false", ["Habr:Enabled"] = "false", ["Threads:Enabled"] = "false",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                if (args[2] == "after-checkpoint") options.AddInterceptors(new PauseAfterCommit(id));
            });
            builder.Services.RemoveAll<ILetterPreparationSteps>();
            builder.Services.AddSingleton<ILetterPreparationSteps>(provider => new LetterReviewProbeSteps(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, args[2]));
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

    private sealed class PauseAfterCommit(Guid id) : DbTransactionInterceptor
    {
        private int _armed = 1;

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LetterPreparationRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == LetterPreparationStage.Completed)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Console.WriteLine("READY");
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }
    }
}

public sealed class LetterReviewProbeSteps(IDbContextFactory<JobFinderDbContext> db, Guid id, string? boundary = null) : ILetterPreparationSteps
{
    public static string CounterKey(Guid id) => $"probe.letter-review.{id:N}";

    public void EnsureCompatible(LetterPreparationInput input)
    {
        if (input.Version != 2 || input.ExactText is null || string.IsNullOrWhiteSpace(input.ReviewContextHash))
            throw new UnsupportedLetterPreparationException();
    }

    public Task<LetterCandidate> DraftAsync(LetterPreparationInput input, LetterCandidate? best, CancellationToken ct) =>
        throw new InvalidOperationException("exact review must never call the writer");

    public async Task<LetterCandidate> ReviewAsync(LetterPreparationInput input, LetterCandidate draft, CancellationToken ct)
    {
        EnsureCompatible(input);
        if (input.ExactText != draft.Text) throw new UnsupportedLetterPreparationException();
        if (boundary == "before-effect") await PauseAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now())
            ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()
            """, ct);
        if (boundary == "after-effect") await PauseAsync(ct);
        return draft with
        {
            Score = 10,
            Review = new ApplicationLetterReview
            {
                Score = 10, TextHash = Fingerprint.Of(draft.Text), ContextHash = input.ReviewContextHash, CheckedAt = DateTimeOffset.UtcNow,
            },
        };
    }

    private static async Task PauseAsync(CancellationToken ct)
    {
        Console.WriteLine("READY");
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
