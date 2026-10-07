using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Storage;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class ApplicationQualityGateTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private IHost? _host;
    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.CapturesAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Quote = "Устранил риск двойных списаний и отрицательного баланса при конкурентных ставках.";
    private const string Letter = "Здравствуйте!\n\nВ игровом backend устранил риск двойных списаний и отрицательного баланса при конкурентных ставках. " +
        "Для вашей платформы, где важна надёжность платёжных операций, это мой самый близкий опыт: отвечал за конкурентное изменение баланса и проверку операций.\n\n" +
        "Подскажите, пожалуйста, какой следующий этап отбора?";

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Usage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Reviewer(bool available = true, bool claims = true) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Models { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Models.Add(body.RootElement.GetProperty("model").GetString()!);
            if (!available) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var check = new MessageCheck
            {
                HasReason = true, Natural = true, ProjectCases = 1,
                Claims = claims ? [new CheckedClaim { Claim = Quote, Quote = Quote, Supported = true, Kind = "experience" }] : [],
                Verdict = new RecruiterVerdict
                {
                    WouldReply = true,
                    Checks = RecruiterVerdict.Criteria.Keys.Select(id => new VerdictCheck { Id = id, Passed = true }).ToList(),
                },
            };
            var completion = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = JsonSerializer.Serialize(check, CareerJsonContext.Default.MessageCheck) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion.ToJsonString()) };
        }
    }

    private async Task<(LetterWriter Writer, ProfileProvider Profiles)> WriterAsync(Reviewer handler)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var documents = new PgDocumentStore(pg.Db, TimeProvider.System);
        var candidate = await pg.CandidateAsync();
        var profiles = new ProfileProvider(documents, new PathsOptions(), NullLogger<ProfileProvider>.Instance, candidate);
        var version = (await profiles.GetSnapshotAsync(Ct))?.Version ?? 0;
        await profiles.SaveAsync(new CandidateProfile { Name = "Иван Иванов", Reviewed = true, Title = "Backend developer", Summary = Quote }, version, "test", Ct);
        var facts = new FactsProvider(documents, NullLogger<FactsProvider>.Instance, candidate);
        await facts.GetSnapshotAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var config = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Llm:ApiKey"] = "synthetic-test", ["Llm:MaxAttempts"] = "1", ["Llm:Endpoint"] = "https://example.invalid/llm",
        };
        foreach (var purpose in LlmPurpose.All)
        {
            config[$"Llm:Routes:{purpose}:0:Name"] = purpose == LlmPurpose.Application ? "application-check" : "reply-check";
            config[$"Llm:Routes:{purpose}:0:Reasoning:Effort"] = "xhigh";
        }
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddJobFinderCore(builder.Configuration);
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        builder.Services.AddSingleton(pg.Db);
        builder.Services.RemoveAll<IHttpClientFactory>();
        builder.Services.AddSingleton<IHttpClientFactory>(new Factory(handler));
        builder.Services.RemoveAll<ILlmUsageSink>();
        builder.Services.AddSingleton<ILlmUsageSink>(new Usage());
        builder.AddJobFinderMessaging(consumer: true);
        _host = builder.Build();
        _host.Services.GetRequiredService<ServerOwnership>().Acquired();
        await _host.StartAsync(Ct);
        return (_host.Services.GetRequiredService<LetterWriter>(), profiles);
    }

    private static Vacancy Vacancy(string text = Letter) => new()
    {
        Title = "Senior .NET", Description = "Платёжная платформа, надёжность операций", CompanyName = "Acme", Url = "https://rabota.by/vacancy/12345678",
        Fingerprint = Guid.NewGuid().ToString("N"), CoverLetter = text, Status = VacancyStatus.Scored, Decision = Decision.Approval,
        Sources = [new VacancySource { Source = SourceKind.HeadHunter, ExternalId = Random.Shared.NextInt64(1000000000, 9000000000).ToString(System.Globalization.CultureInfo.InvariantCulture), Host = "rabota.by", Url = "https://rabota.by/vacancy/12345678" }],
    };

    [Fact]
    public async Task A_review_survives_postgres_round_trip_and_exact_text_reuses_it()
    {
        using var handler = new Reviewer();
        var (writer, _) = await WriterAsync(handler);
        var vacancy = Vacancy();
        vacancy.LetterReview = await writer.ReviewResponseAsync(vacancy, ApplicationChannel.HeadHunter, null, Letter, Ct);
        Assert.Empty(vacancy.LetterReview.Problems);
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            ctx.Vacancies.Add(vacancy);
            await ctx.SaveChangesAsync(Ct);
        }
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var restored = await read.Vacancies.AsNoTracking().Include(row => row.Company).Include(row => row.Sources).FirstAsync(v => v.Id == vacancy.Id, Ct);
        Assert.NotNull(restored.LetterReview!.Recipe);
        Assert.Equal(1, restored.LetterReview.Recipe.PolicyRevision);
        Assert.Equal(restored.LetterReview.ContextHash, restored.LetterReview.Recipe.Hash);
        Assert.DoesNotContain("Иван Иванов", System.Text.Json.JsonSerializer.Serialize(restored.LetterReview.Recipe,
            JobFinder.Core.Artifacts.RecipeJsonContext.Default.ArtifactRecipe));
        var review = await writer.ReviewResponseAsync(restored, ApplicationChannel.HeadHunter, null, Letter, Ct);
        Assert.Equal(10, review.Score);
        Assert.Equal(1, handler.Calls);
        await writer.ReviewResponseAsync(restored, ApplicationChannel.HeadHunter, null, Letter + "\n\nИван", Ct);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void Replacing_a_pdf_invalidates_its_quality_report()
    {
        var path = Path.Combine(Path.GetTempPath(), "jf-pdf-proof-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllText(path, "original pdf");
            var report = new ResumeReport
            {
                Ready = true, Pages = 2,
                PdfHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
                Review = new ResumeReview { Checks = ResumeReview.Criteria.Keys.Select(id => new ReviewCheck { Id = id, Passed = true }).ToList() },
            };
            var resume = new TailoredResume { Language = "ru", PdfPath = path, ContentJson = "{}", FileName = "cv.pdf", ReportJson = JsonSerializer.Serialize(report, ResumeJsonContext.Default.ResumeReport) };
            Assert.True(ResumeTailor.Current(resume));
            File.AppendAllText(path, "modified");
            Assert.False(ResumeTailor.Current(resume));
            File.Delete(path);
            Assert.False(ResumeTailor.Current(resume));
            resume.ReportJson = "not json";
            Assert.False(ResumeTailor.Current(resume));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Approving_an_unchecked_bad_letter_does_not_queue_an_application()
    {
        using var handler = new Reviewer();
        var (writer, profiles) = await WriterAsync(handler);
        var vacancy = Vacancy("[имя] я подхожу вам");
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            ctx.Vacancies.Add(vacancy);
            await ctx.SaveChangesAsync(Ct);
        }
        var triggers = new JobTriggers();
        var approvals = new ApprovalService(pg.Db, writer, profiles, new EmailSender(new EmailOptions()),
        new FunnelService(null!, TimeProvider.System, NullLogger<FunnelService>.Instance),
            new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies), triggers, TimeProvider.System, NullLogger<ApprovalService>.Instance, await pg.CapturesAsync(), _host!.Services.GetRequiredService<ApplicationDeliveryRequests>(), _host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.CommentDecisions>(), _host.Services.GetRequiredService<Notifier>(), _host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.ChannelDecisions>());
        Assert.False((await approvals.ApplyAsync(vacancy.Id, Ct)).Ok);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await read.Applications.AnyAsync(a => a.VacancyId == vacancy.Id, Ct));
        Assert.NotEmpty((await read.Vacancies.FirstAsync(v => v.Id == vacancy.Id, Ct)).LetterReview!.Problems);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task An_unreviewed_profile_stops_approval_without_a_request_or_an_application()
    {
        using var handler = new Reviewer();
        var (writer, profiles) = await WriterAsync(handler);
        var profile = (await profiles.GetSnapshotAsync(Ct))!;
        profile.Value.Reviewed = false;
        await profiles.SaveAsync(profile.Value, profile.Version, "synthetic-test", Ct);
        var vacancy = Vacancy();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Vacancies.Add(vacancy);
            await context.SaveChangesAsync(Ct);
        }
        var triggers = new JobTriggers();
        var approvals = new ApprovalService(pg.Db, writer, profiles, new EmailSender(new EmailOptions()),
        new FunnelService(null!, TimeProvider.System, NullLogger<FunnelService>.Instance),
            new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies), triggers, TimeProvider.System,
            NullLogger<ApprovalService>.Instance, await pg.CapturesAsync(), _host!.Services.GetRequiredService<ApplicationDeliveryRequests>(), _host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.CommentDecisions>(), _host.Services.GetRequiredService<Notifier>(), _host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.ChannelDecisions>());
        var result = await approvals.ApplyAsync(vacancy.Id, Ct);
        Assert.False(result.Ok);
        Assert.Contains("проверка письма остановлена", result.Message);
        Assert.Equal(0, handler.Calls);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await read.Applications.AnyAsync(row => row.VacancyId == vacancy.Id, Ct));
        Assert.False(await read.LetterPreparationRequests.AnyAsync(row => row.VacancyId == vacancy.Id, Ct));
    }

    [Fact]
    public async Task Concurrent_approvals_create_one_application_after_review()
    {
        using var handler = new Reviewer();
        var (writer, profiles) = await WriterAsync(handler);
        var vacancy = Vacancy();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Vacancies.Add(vacancy);
            await context.SaveChangesAsync(Ct);
        }
        var triggers = new JobTriggers();
        var approvals = new ApprovalService(pg.Db, writer, profiles, new EmailSender(new EmailOptions()),
        new FunnelService(null!, TimeProvider.System, NullLogger<FunnelService>.Instance),
            new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies), triggers, TimeProvider.System, NullLogger<ApprovalService>.Instance, await pg.CapturesAsync(), _host!.Services.GetRequiredService<ApplicationDeliveryRequests>(), _host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.CommentDecisions>(), _host.Services.GetRequiredService<Notifier>(), _host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.ChannelDecisions>());
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => approvals.ApplyAsync(vacancy.Id, Ct)));
        Assert.Single(results, result => result.Ok);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await read.Applications.Where(application => application.VacancyId == vacancy.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task An_unavailable_reviewer_cannot_pass_a_letter()
    {
        using var handler = new Reviewer(available: false);
        var (writer, _) = await WriterAsync(handler);
        var ticket = await writer.RequestReviewAsync(LetterWriter.ResponseBrief(Vacancy(), ApplicationChannel.HeadHunter, null), Letter, Ct);
        Assert.Null(ticket.Review);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var lane = await context.LetterPreparationLanes.SingleAsync(row => row.RequestId == ticket.RequestId, Ct);
            if (lane.RetryAt is not null)
            {
                Assert.Equal(LetterLaneStage.Reviewing, lane.Stage);
                Assert.Equal(0, lane.Failures);
                Assert.Null(await writer.GetReviewAsync(ticket.RequestId!.Value, Ct));
                break;
            }
            Assert.True(DateTimeOffset.UtcNow < deadline, "unavailable review was not durably deferred");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task An_empty_claims_list_cannot_make_a_first_message_verified()
    {
        using var handler = new Reviewer(claims: false);
        var (writer, _) = await WriterAsync(handler);
        var vacancy = Vacancy();
        var review = await writer.ReviewResponseAsync(vacancy, ApplicationChannel.HeadHunter, null, Letter, Ct);
        Assert.NotEmpty(review.Problems);
        vacancy.LetterReview = review;
        Assert.NotEmpty((await writer.ReviewResponseAsync(vacancy, ApplicationChannel.HeadHunter, null, Letter, Ct)).Problems);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task First_message_verification_uses_the_maximum_effort_application_route()
    {
        using var handler = new Reviewer();
        var (writer, _) = await WriterAsync(handler);
        await writer.ReviewResponseAsync(Vacancy(), ApplicationChannel.HeadHunter, null, Letter, Ct);
        Assert.Equal(["application-check"], handler.Models);
    }

    [Fact]
    public async Task Changing_the_message_situation_invalidates_a_cached_review()
    {
        using var handler = new Reviewer();
        var (writer, _) = await WriterAsync(handler);
        var vacancy = Vacancy();
        var brief = new LetterBrief(vacancy, LetterChannel.Hh, "рекрутер", "Первый отклик", false, "", true, true, false);
        vacancy.LetterReview = await writer.ReviewMessageAsync(brief, Letter, Ct);
        await writer.ReviewMessageAsync(brief, Letter, Ct);
        Assert.Equal(1, handler.Calls);
        await writer.ReviewMessageAsync(brief with { Situation = "Повторный отклик после отказа" }, Letter, Ct);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void A_high_number_alone_is_not_a_resume_gate()
    {
        var report = new ResumeReport { Ready = true, Pages = 2, Review = new ResumeReview { Score = 10 } };
        Assert.False(ResumeTailor.Ready(report));
        report.Review.Checks = ResumeReview.Criteria.Keys.Select(id => new ReviewCheck { Id = id, Passed = true }).ToList();
        Assert.True(ResumeTailor.Ready(report));
        Assert.False(ResumeTailor.Ready(report with { Layout = ["split job"] }));
        Assert.False(ResumeTailor.Ready(report with { Pages = 3 }));
        Assert.False(ResumeTailor.Ready(report with { Unshown = ["PostgreSQL"] }));
        Assert.False(ResumeTailor.Ready(report with { Violations = ["invented metric"] }));
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(100, false)]
    public void Cached_letter_checks_apply_only_to_exact_text_and_context(int score, bool ready)
    {
        var report = new ApplicationLetterReview { Score = score, TextHash = Fingerprint.Of("letter"), ContextHash = "context" };
        Assert.Equal(ready, LetterWriter.Matches(report, "letter", "context"));
        Assert.False(LetterWriter.Matches(report, "edited", "context"));
        Assert.False(LetterWriter.Matches(report, "letter", "new facts"));
        Assert.False(LetterWriter.Matches(report with { Problems = ["weak"] }, "letter", "context"));
    }

    [Theory]
    [InlineData(null, "Сохранённое письмо вакансии")]
    [InlineData("Текст очереди", "Текст очереди")]
    [InlineData("", "")]
    public void All_application_channels_receive_the_same_resolved_and_checked_text(string? queued, string expected)
    {
        var application = new Application { CoverLetter = queued, Vacancy = Vacancy("Сохранённое письмо вакансии") };
        Assert.Equal(expected, JobFinder.Pipeline.Workers.ApplyWorker.LetterOf(application));
    }
}
