using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Wolverine;

namespace JobFinder.MessagingProbe;

public static class InterviewProbe
{
    public const string Marker = "Synthetic interview process probe";

    public static string DataPath(Guid id) => Path.GetFullPath(Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(),
        "jobfinder-interview-probe", id.ToString("N"), "data"));

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Guid.TryParseExact(args[1], "N", out var id) || !Enum.TryParse<InterviewPreparationStage>(args[2], out var phase)
            || phase is not (InterviewPreparationStage.Planning or InterviewPreparationStage.Answering or InterviewPreparationStage.Printing or InterviewPreparationStage.Completed)
            || args[3] is not ("before-effect" or "after-effect" or "before-checkpoint" or "after-checkpoint")
            || phase == InterviewPreparationStage.Completed && args[3] is not ("before-checkpoint" or "after-checkpoint")) return 2;
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
                ["Paths:Data"] = DataPath(id), ["Paths:Profile"] = Path.Combine(DataPath(id), "probe-profile"),
                ["Llm:ApiKey"] = "synthetic-interview-probe-key", ["Llm:Proxy"] = "", ["Llm:Endpoint"] = "https://provider.invalid/completions",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                options.AddInterceptors(new Checkpoint(id, phase, args[3]));
            });
            builder.Services.RemoveAll<IInterviewPreparationSteps>();
            builder.Services.AddSingleton<IInterviewPreparationSteps>(provider => new InterviewProbeSteps(
                provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), provider.GetRequiredService<PathsOptions>(), id, phase, args[3]));
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
            builder.Services.RemoveAll<ICommentDeliveryTransport>();
            builder.Services.AddSingleton<ICommentDeliveryTransport>(provider => new CommentDeliveryProbeTransport(provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>()));
            builder.AddJobFinderMessaging(consumer: true);
            builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
            builder.Services.AddJobFinderInitialization();
            using var host = builder.Build();
            await using (var context = await host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync())
            {
                var request = await context.InterviewPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id);
                if (request is null || !await context.Interviews.AnyAsync(row => row.Id == request.InterviewId && row.Application!.Vacancy!.CompanyName == Marker)) return 2;
            }
            await host.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }

    private sealed class Checkpoint(Guid id, InterviewPreparationStage phase, string boundary) : DbTransactionInterceptor
    {
        private int _armed = 1;
        private readonly InterviewPreparationStage _next = phase switch
        {
            InterviewPreparationStage.Planning => InterviewPreparationStage.Answering,
            InterviewPreparationStage.Answering => InterviewPreparationStage.Printing,
            InterviewPreparationStage.Printing => InterviewPreparationStage.Publishing,
            _ => InterviewPreparationStage.Completed,
        };

        private Task PauseAsync(DbContext context, CancellationToken ct) =>
            context.ChangeTracker.Entries<InterviewPreparationRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == _next)
                && Interlocked.Exchange(ref _armed, 0) == 1 ? CommentDeliveryProbeTransport.PauseAsync(ct) : Task.CompletedTask;

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
                throw new InvalidOperationException("interview process probe refuses external HTTP");
        }
    }
}

public sealed class InterviewProbeSteps(IDbContextFactory<JobFinderDbContext> db, PathsOptions paths, Guid id,
    InterviewPreparationStage? phase = null, string? boundary = null) : IInterviewPreparationSteps
{
    public static string CounterKey(Guid id, InterviewPreparationStage stage) => "probe.interview." + id.ToString("N") + "." + stage;

    private async Task CountAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewPreparationStage stage, CancellationToken ct)
    {
        if (seed.Operation.OperationId != id || input.AnswerSource.Vacancy.CompanyName != InterviewProbe.Marker) throw new UnsupportedInterviewPreparationException();
        if (phase == stage && boundary == "before-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id, stage);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now())
            ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()
            """, ct);
        if (phase == stage && boundary == "after-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
    }

    public async Task<InterviewPlanCheckpoint> PlanAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, CancellationToken ct)
    {
        await CountAsync(seed, input, InterviewPreparationStage.Planning, ct);
        return new() { Model = "synthetic", Plan = new() { Role = "Backend", OverallRisk = "low",
            Questions = Enumerable.Range(1, 6).Select(index => new PlannedQuestion { Id = "question-" + index, Question = "Question " + index, Category = "hr" }).ToList() } };
    }

    public async Task<InterviewCopilot> AnswerAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewPlanCheckpoint plan, CancellationToken ct)
    {
        await CountAsync(seed, input, InterviewPreparationStage.Answering, ct);
        var (questions, _) = CopilotRules.Normalize(plan.Plan, input.AnswerSource.Catalog);
        var items = questions.Select(question => new PrepItem { Q = question, A = new() { Start = "Synthetic start", Core = "Synthetic core", EvidenceLevel = "verified" } }).ToList();
        return new() { Version = InterviewCopilot.CurrentVersion, GeneratedAt = seed.RenderedAt, Role = plan.Plan.Role, OverallRisk = plan.Plan.OverallRisk,
            Preparation = items.Where(item => item.Q.Id != CopilotRules.SalaryId).ToList(), Salary = items.Single(item => item.Q.Id == CopilotRules.SalaryId) };
    }

    public async Task<InterviewPrintCheckpoint> PrintAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewCopilot copilot, CancellationToken ct)
    {
        await CountAsync(seed, input, InterviewPreparationStage.Printing, ct);
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddText("Synthetic interview crash recovery pack", 12, new PdfPoint(40, 800), builder.AddStandard14Font(Standard14Font.Helvetica));
        var bytes = builder.Build();
        var path = Path.GetFullPath(Path.Combine(paths.Data, "interviews", seed.Source.Id.ToString(CultureInfo.InvariantCulture), id.ToString("N"), "pack.pdf"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return new() { Path = StoredPath.ToStored(path), Hash = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }
}
