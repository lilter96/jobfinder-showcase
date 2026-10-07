using System.Text.Json;
using System.Net;
using System.Reflection;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm;
using JobFinder.Integrations.Http;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableVacancyAssessmentIntegrationTests
{
    private static AssessmentVacancy PreviewVacancy(string title = "Senior .NET Developer") => new()
    {
        Title = title, CompanyName = "Synthetic Preview Company", Description = "Build synthetic .NET backend services.",
        Url = "https://hh.ru/vacancy/900009999", Fingerprint = "", WorkFormats = WorkFormat.Remote,
    };

    private static Task RunPreviewAsync(IServiceProvider services, Guid id, VacancyAssessmentStage stage) =>
        services.GetRequiredService<VacancyAssessmentWorkflow>().RunAsync(id, 1, stage, Guid.NewGuid(), Ct, preview: true);

    private async Task AssertPreviewDidNotPublishBusinessAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Vacancies.ToListAsync(Ct));
        Assert.Empty(await context.Applications.ToListAsync(Ct));
        Assert.Empty(await context.LetterPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.Where(row => row.Kind == NotificationKind.VacancyApproval).ToListAsync(Ct));
    }

    [Fact]
    public async Task Preview_admission_is_read_only_idempotent_and_does_not_bootstrap_missing_candidate_facts()
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(row => row.Key == FactsProvider.Key).ExecuteDeleteAsync(Ct);
        var before = await context.CandidateRevisions.CountAsync(Ct);
        var requests = host.Services.GetRequiredService<VacancyAssessments>();
        var ids = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => requests.RequestPreviewAsync(PreviewVacancy(), Ct)));
        Assert.Single(ids.Distinct());
        var root = await RequestAsync(ids[0]);
        var input = VacancyAssessments.ReadInput(root);
        Assert.Equal(3, input.Version);
        Assert.Equal(0, input.FactsVersion);
        Assert.Equal(0, root.VacancyId);
        Assert.Equal(root.PreviewKey, input.Preview!.Key);
        Assert.Empty(steps.Calls);
        Assert.Single(await context.VacancyAssessmentRequests.ToListAsync(Ct));
        Assert.False(await context.Documents.AnyAsync(row => row.Key == FactsProvider.Key, Ct));
        Assert.Equal(before, await context.CandidateRevisions.CountAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    [Fact]
    public async Task Independent_previews_share_the_executor_without_the_zero_vacancy_unique_index_serializing_them()
    {
        var host = await HostAsync(false, new Steps());
        var requests = host.Services.GetRequiredService<VacancyAssessments>();
        var ids = await Task.WhenAll(requests.RequestPreviewAsync(PreviewVacancy("Synthetic first role"), Ct),
            requests.RequestPreviewAsync(PreviewVacancy("Synthetic second role"), Ct));
        Assert.Equal(2, ids.Distinct().Count());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.VacancyAssessmentRequests.CountAsync(Ct));
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    [Fact]
    public async Task Preview_real_native_producer_before_consumer_restart_completes_one_score_and_warm_reuses_it()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var id = await producer.Services.GetRequiredService<VacancyAssessments>().RequestPreviewAsync(PreviewVacancy(), Ct);
        Assert.Empty(steps.Calls);
        await StopAsync(producer);
        var consumer = await HostAsync(true, steps);
        var requests = consumer.Services.GetRequiredService<VacancyAssessments>();
        var (input, score) = await requests.WaitPreviewAsync(id, Ct).WaitAsync(Timeout, Ct);
        Assert.Equal(0.004m, score.CostUsd);
        Assert.Equal("synthetic-scorer", score.Evaluation!.Model);
        Assert.Equal(_profile.Name, input.Profile.Name);
        Assert.Equal(id, await requests.RequestPreviewAsync(PreviewVacancy(), Ct));
        Assert.Single(steps.Calls);
        Assert.Equal(VacancyAssessmentStage.Scoring, steps.Calls.Single().Stage);
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    [Fact]
    public async Task Preview_saved_score_finishes_after_host_recreation_and_keeps_original_candidate_inputs()
    {
        var steps = new Steps();
        var first = await HostAsync(false, steps, owned: true);
        var id = await first.Services.GetRequiredService<VacancyAssessments>().RequestPreviewAsync(PreviewVacancy(), Ct);
        await RunPreviewAsync(first.Services, id, VacancyAssessmentStage.Scoring);
        var checkpoint = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Finishing, checkpoint.Stage);
        Assert.NotNull(checkpoint.ScoreHash);
        await first.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(_profile with { Name = "Newer Candidate" }, 1, "synthetic", Ct);
        await StopAsync(first);
        var second = await HostAsync(false, steps, owned: true);
        await RunPreviewAsync(second.Services, id, VacancyAssessmentStage.Finishing);
        var (input, score) = await second.Services.GetRequiredService<VacancyAssessments>().WaitPreviewAsync(id, Ct);
        Assert.Equal(_profile.Name, input.Profile.Name);
        Assert.Equal(checkpoint.ScoreHash, (await RequestAsync(id)).ScoreHash);
        Assert.Equal(0.004m, score.CostUsd);
        Assert.Single(steps.Calls);
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    [Fact]
    public async Task Preview_outage_and_cancelled_producer_wait_resume_the_same_snapshot_without_business_failures()
    {
        var unavailable = true;
        var steps = new Steps { OnStage = (_, _, _) => unavailable ? Task.FromException(new LlmUnavailableException("synthetic outage")) : Task.CompletedTask };
        var host = await HostAsync(false, steps, owned: true);
        var requests = host.Services.GetRequiredService<VacancyAssessments>();
        var id = await requests.RequestPreviewAsync(PreviewVacancy(), Ct);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Scoring);
        var paused = await RequestAsync(id);
        Assert.NotNull(paused.RetryAt);
        Assert.Equal(0, paused.Failures);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.WaitPreviewAsync(id, cancelled.Token));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.VacancyAssessmentRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        unavailable = false;
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Scoring);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Finishing);
        Assert.Equal(paused.InputHash, (await RequestAsync(id)).InputHash);
        Assert.Equal(2, steps.Calls.Count);
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    [Theory]
    [InlineData(VacancyAssessmentStage.Scoring)]
    [InlineData(VacancyAssessmentStage.Finishing)]
    [InlineData(VacancyAssessmentStage.Completed)]
    public async Task Preview_admission_checkpoint_and_completion_faults_roll_back_without_publishing_or_repaying(VacancyAssessmentStage boundary)
    {
        var fault = new FailCommit(boundary);
        var steps = new Steps();
        var host = await HostAsync(false, steps, fault, owned: true);
        var requests = host.Services.GetRequiredService<VacancyAssessments>();
        if (boundary == VacancyAssessmentStage.Scoring)
        {
            await Assert.ThrowsAsync<IOException>(() => requests.RequestPreviewAsync(PreviewVacancy(), Ct));
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
            Assert.Equal(0, await NativePendingAsync());
        }
        var id = await requests.RequestPreviewAsync(PreviewVacancy(), Ct);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Scoring);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Finishing);
        Assert.Equal(VacancyAssessmentStage.Completed, (await RequestAsync(id)).Stage);
        Assert.True(fault.Fired);
        Assert.False(fault.SawScoredVacancy);
        Assert.False(fault.SawApprovalCard);
        Assert.Single(steps.Calls);
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    [Fact]
    public async Task Preview_exhausted_score_save_keeps_the_paid_result_failed_and_never_automatically_repays()
    {
        var fault = new PreviewCheckpointFault();
        var steps = new Steps();
        var host = await HostAsync(false, steps, fault, owned: true);
        var id = await host.Services.GetRequiredService<VacancyAssessments>().RequestPreviewAsync(PreviewVacancy(), Ct);
        await Assert.ThrowsAsync<VacancyAssessmentCheckpointException>(() => RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Scoring));
        var root = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Failed, root.Stage);
        Assert.Equal(0.004m, VacancyAssessments.ReadPreviewScore(root).CostUsd);
        Assert.Equal(3, fault.Failures);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Scoring);
        Assert.Single(steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_damaged_input_or_score_is_quarantined_once_without_mutating_vacancies(bool score)
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps, owned: true);
        var id = await host.Services.GetRequiredService<VacancyAssessments>().RequestPreviewAsync(PreviewVacancy(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (score)
        {
            await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Scoring);
            await context.VacancyAssessmentRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ScoreJson, "{}"), Ct);
        }
        else await context.VacancyAssessmentRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.InputJson, "{}"), Ct);
        var stage = score ? VacancyAssessmentStage.Finishing : VacancyAssessmentStage.Scoring;
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => RunPreviewAsync(host.Services, id, stage));
        await RunPreviewAsync(host.Services, id, stage);
        Assert.Equal(VacancyAssessmentStage.Failed, (await RequestAsync(id)).Stage);
        Assert.Equal(score ? 1 : 0, steps.Calls.Count);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    [Theory]
    [InlineData("root")]
    [InlineData("queue")]
    [InlineData("scheduled")]
    [InlineData("incoming")]
    [InlineData("outgoing")]
    [InlineData("dead-letter")]
    public async Task Preview_downgrade_refuses_saved_previews_and_orphaned_future_commands(string evidence)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (evidence == "root")
        {
            var host = await HostAsync(false, new Steps());
            await host.Services.GetRequiredService<VacancyAssessments>().RequestPreviewAsync(PreviewVacancy(), Ct);
        }
        else
        {
            var id = Guid.NewGuid();
            const string type = "jobfinder.discovery.vacancy-preview-finish.v99";
            if (evidence == "queue") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_queues.wolverine_queue_scoring (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{type})", Ct);
            else if (evidence == "scheduled") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_queues.wolverine_queue_scoring_scheduled (id,body,message_type,execution_time) VALUES ({id},{Array.Empty<byte>()},{type},{DateTimeOffset.UtcNow.AddDays(1)})", Ct);
            else if (evidence == "incoming") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_incoming_envelopes (id,status,owner_id,body,message_type) VALUES ({id},'Scheduled',0,{Array.Empty<byte>()},{type})", Ct);
            else if (evidence == "outgoing") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_outgoing_envelopes (id,owner_id,body,message_type,destination) VALUES ({id},0,{Array.Empty<byte>()},{type},{"postgresql://scoring"})", Ct);
            else await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_dead_letters (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{type})", Ct);
        }
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004002414_DurableStoryPreparation", Ct));
        Assert.Contains("Cannot downgrade retained vacancy previews", error.MessageText);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Preview_of_an_existing_applied_vacancy_preserves_its_source_letter_score_and_sent_history()
    {
        var source = await AnnotatedSeedAsync(VacancyStatus.Applied);
        var steps = new Steps();
        var host = await HostAsync(false, steps, owned: true);
        var id = await host.Services.GetRequiredService<VacancyAssessments>().RequestPreviewAsync(AssessmentVacancy.From(source), Ct);
        Assert.Equal(0, VacancyAssessments.ReadInput(await RequestAsync(id)).Vacancy.Id);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Scoring);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Finishing);
        var after = await VacancyAsync(source.Id);
        Assert.Equal(VacancyAssessments.Witness(AssessmentVacancy.From(source)), VacancyAssessments.Witness(AssessmentVacancy.From(after)));
        Assert.Equal(VacancyAssessments.AnnotationHash(AssessmentAnnotationSnapshot.From(source)), VacancyAssessments.AnnotationHash(AssessmentAnnotationSnapshot.From(after)));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Vacancies.ToListAsync(Ct));
        var application = Assert.Single(await context.Applications.ToListAsync(Ct));
        Assert.Equal(ApplicationStatus.Sent, application.Status);
        Assert.Equal("Actually sent historical text", application.CoverLetter);
        Assert.Equal(4, application.Revision);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Single(steps.Calls);
    }

    private static Task<int> ScoreEntryAsync(IServiceProvider services, string url) => (Task<int>)typeof(CliHost)
        .GetMethod("ScoreAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [services, url, Ct])!;
    private static Task<int> SavedPreviewEntryAsync(IServiceProvider services, Guid id) => (Task<int>)typeof(CliHost)
        .GetMethod("ScorePreviewResultAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [services, id, Ct])!;

    [Fact]
    public async Task Actual_score_CLI_and_saved_preview_resume_use_native_score_and_frozen_maximum_letter_without_a_second_hh_read()
    {
        using var http = new PreviewHttp();
        var steps = new Steps();
        var letters = new PreviewLetters();
        var host = await HostAsync(true, steps, configure: services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(http);
            services.RemoveAll<ILetterPreparationSteps>();
            services.AddSingleton<ILetterPreparationSteps>(letters);
            services.RemoveAll<PoliteHttp>();
            services.AddSingleton(new PoliteHttp(http.CreateClient("synthetic-preview"), new ScrapeOptions { MinDelayMs = 0, MaxDelayMs = 1 }, TimeProvider.System));
        });
        Assert.Equal(0, await ScoreEntryAsync(host.Services, "https://hh.ru/vacancy/900009999").WaitAsync(Timeout, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = Assert.Single(await context.VacancyAssessmentRequests.ToListAsync(Ct));
        var initial = VacancyAssessments.ReadInput(root);
        var calls = letters.Drafts;
        Assert.True(calls > 0);
        Assert.True(letters.Reviews > 0);
        Assert.Equal(0, await SavedPreviewEntryAsync(host.Services, root.Id).WaitAsync(Timeout, Ct));
        Assert.Equal(calls, letters.Drafts);
        Assert.Single(steps.Calls);
        Assert.Equal(1, http.Reads);
        var captured = Assert.Single(letters.Inputs.Select(input => input.Operation.OperationId).Distinct());
        Assert.Equal(initial.Operation.OperationId, captured);
        foreach (var input in letters.Inputs)
        {
            Assert.Equal(initial.Profile.Name, input.Profile.Name);
            Assert.Equal(initial.Facts.ForPrompt(), input.Facts.ForPrompt());
            Assert.Equal(initial.Vacancy.Url, input.Anchor.Url);
            Assert.Equal(0, input.Anchor.Id);
            Assert.Equal("max", input.Operation.Policy.Routes[LlmPurpose.Application].Steps[0].Reasoning!.Effort);
        }
        Assert.Single(await context.LetterPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await context.Vacancies.ToListAsync(Ct));
        Assert.Empty(await context.Applications.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("unreviewed")]
    [InlineData("missing-facts")]
    public async Task Saved_preview_CLI_shows_the_score_but_does_not_prepare_unchecked_employer_text_or_create_candidate_documents(string prerequisite)
    {
        var host = await HostAsync(false, new Steps(), owned: true);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (prerequisite == "missing-facts") await context.Documents.Where(row => row.Key == FactsProvider.Key).ExecuteDeleteAsync(Ct);
        else await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(_profile with { Reviewed = false }, 1, "synthetic", Ct);
        var documents = await context.Documents.CountAsync(Ct);
        var revisions = await context.CandidateRevisions.CountAsync(Ct);
        var id = await host.Services.GetRequiredService<VacancyAssessments>().RequestPreviewAsync(PreviewVacancy(), Ct);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Scoring);
        await RunPreviewAsync(host.Services, id, VacancyAssessmentStage.Finishing);
        Assert.Equal(0, await SavedPreviewEntryAsync(host.Services, id));
        Assert.Equal(documents, await context.Documents.CountAsync(Ct));
        Assert.Equal(revisions, await context.CandidateRevisions.CountAsync(Ct));
        await AssertPreviewDidNotPublishBusinessAsync();
    }

    private sealed class PreviewHttp : HttpMessageHandler, IHttpClientFactory
    {
        public int Reads;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("hh.ru", request.RequestUri!.Host);
            Interlocked.Increment(ref Reads);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture.Read("hh_vacancy_byn.html")) });
        }
    }

    private sealed class PreviewLetters : ILetterPreparationSteps
    {
        public int Drafts;
        public int Reviews;
        public System.Collections.Concurrent.ConcurrentQueue<LetterPreparationInput> Inputs { get; } = new();
        public void EnsureCompatible(LetterPreparationInput input) { }
        public Task<LetterCandidate> DraftAsync(LetterPreparationInput input, LetterCandidate? best, CancellationToken ct)
        {
            Interlocked.Increment(ref Drafts);
            Inputs.Enqueue(input);
            return Task.FromResult(new LetterCandidate { Text = "Synthetic reviewed preview letter", Written = "Synthetic reviewed preview letter" });
        }
        public Task<LetterCandidate> ReviewAsync(LetterPreparationInput input, LetterCandidate draft, CancellationToken ct)
        {
            Interlocked.Increment(ref Reviews);
            Inputs.Enqueue(input);
            return Task.FromResult(new LetterCandidate
            {
                Text = draft.Text, Written = draft.Written, Score = 10,
                Review = new ApplicationLetterReview { Score = 10, TextHash = Fingerprint.Of(draft.Text), ContextHash = input.Recipe.Hash,
                    Recipe = input.Recipe, CheckedAt = DateTimeOffset.UtcNow },
            });
        }
    }

    private sealed class PreviewCheckpointFault : SaveChangesInterceptor
    {
        public int Failures;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Failures < 3 && data.Context?.ChangeTracker.Entries<VacancyAssessmentRequest>().Any(entry => entry.Entity.PreviewKey is not null
                    && entry.Entity.Stage == VacancyAssessmentStage.Finishing) == true)
            {
                Failures++;
                throw new IOException("synthetic preview checkpoint failure");
            }
            return base.SavingChangesAsync(data, result, ct);
        }
    }
}
