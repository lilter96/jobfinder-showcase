using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableVacancyAssessmentIntegrationTests
{
    private async Task<Vacancy> AnnotatedSeedAsync(VacancyStatus status = VacancyStatus.Scored)
    {
        var seed = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.Vacancies.SingleAsync(row => row.Id == seed.Id, Ct);
        source.Status = status;
        source.Score = 91;
        source.LlmScore = 93;
        source.SalaryAdjustment = -2;
        source.Decision = Decision.AutoApply;
        source.Legitimacy = Legitimacy.Unknown;
        source.Evaluation = new VacancyEvaluation
        {
            Score = 93, Summary = "Historical evaluation", Model = "historical-model", Strengths = ["Historical candidate fact"],
            RedFlags = ["Historical risk"], SalaryNote = "Historical salary", Language = "ru",
        };
        source.CoverLetter = "Historical approved letter";
        source.LetterRevision = 7;
        source.AssessmentRevision = 3;
        source.Attempts = 2;
        source.LastError = "Historical error";
        source.Notified = true;
        source.RejectReason = "Historical note";
        source.ScoredAt = DateTimeOffset.UtcNow.AddDays(-2);
        if (status == VacancyStatus.Applied) context.Applications.Add(new Application
        {
            VacancyId = source.Id, Status = ApplicationStatus.Sent, Channel = ApplicationChannel.HeadHunter,
            CoverLetter = "Actually sent historical text", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), Revision = 4,
        });
        await context.SaveChangesAsync(Ct);
        return await VacancyAsync(seed.Id);
    }

    private static void AssertAnnotationPreserved(Vacancy before, Vacancy after)
    {
        var expected = AssessmentAnnotationSnapshot.From(before);
        var current = AssessmentAnnotationSnapshot.From(after) with { Evaluation = expected.Evaluation, Legitimacy = expected.Legitimacy };
        Assert.Equal(VacancyAssessments.AnnotationHash(expected), VacancyAssessments.AnnotationHash(current));
        var original = AssessmentVacancy.From(before);
        var updated = AssessmentVacancy.From(after) with { UpdatedAt = original.UpdatedAt, Revision = original.Revision };
        Assert.Equal(VacancyAssessments.Witness(original), VacancyAssessments.Witness(updated));
        Assert.Equal(before.AssessmentRevision + 1, after.AssessmentRevision);
        var evaluation = before.Evaluation! with
        {
            Keywords = after.Evaluation!.Keywords, Language = after.Evaluation.Language,
            LegitimacyReasons = after.Evaluation.LegitimacyReasons, MarketNote = after.Evaluation.MarketNote,
        };
        Assert.Equal(CanonicalJson.Hash(JsonSerializer.Serialize(evaluation, VacancyAssessmentJsonContext.Default.VacancyEvaluation)),
            CanonicalJson.Hash(JsonSerializer.Serialize(after.Evaluation, VacancyAssessmentJsonContext.Default.VacancyEvaluation)));
    }

    [Theory]
    [InlineData(VacancyStatus.Scored)]
    [InlineData(VacancyStatus.Applied)]
    public async Task Annotation_duplicate_producers_and_native_completion_preserve_assessment_and_sent_history(VacancyStatus status)
    {
        var before = await AnnotatedSeedAsync(status);
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var requests = producer.Services.GetRequiredService<VacancyAssessments>();
        var ids = await Task.WhenAll(requests.RequestAnnotationsAsync(before.Id, Ct), requests.RequestAnnotationsAsync(before.Id, Ct));
        Assert.NotNull(ids[0]);
        Assert.Equal(ids[0], ids[1]);
        Assert.Empty(steps.Calls);
        var root = await RequestAsync(ids[0]!.Value);
        Assert.Equal(2, Input(root).Version);
        Assert.NotNull(Input(root).Annotation);
        Assert.False(Input(root).CanAutoApply);
        await StopAsync(producer);
        var consumer = await HostAsync(true, steps);
        var completed = await consumer.Services.GetRequiredService<VacancyAssessments>().WaitAnnotationsAsync(root.Id, Ct).WaitAsync(Timeout, Ct);
        Assert.Equal(VacancyAssessmentStage.Completed, completed.Stage);
        var after = await VacancyAsync(before.Id);
        AssertAnnotationPreserved(before, after);
        Assert.Equal(["C#", "PostgreSQL"], after.Evaluation!.Keywords);
        Assert.Equal(VacancyAssessmentStage.Scoring, Assert.Single(steps.Calls).Stage);
        Assert.Empty(await CardsAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var applications = await context.Applications.ToListAsync(Ct);
        if (status == VacancyStatus.Applied)
        {
            var sent = Assert.Single(applications);
            Assert.Equal(ApplicationStatus.Sent, sent.Status);
            Assert.Equal("Actually sent historical text", sent.CoverLetter);
            Assert.Equal(4, sent.Revision);
            Assert.Null(sent.DeliveryRequestId);
        }
        else Assert.Empty(applications);
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Null(await requests.RequestAnnotationsAsync(before.Id, Ct));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("evaluation")]
    [InlineData("score")]
    [InlineData("candidate")]
    [InlineData("delivery")]
    public async Task Annotation_changed_capture_is_cancelled_before_paid_work_and_does_not_rebase(string change)
    {
        var before = await AnnotatedSeedAsync();
        var steps = new Steps();
        var host = await HostAsync(false, steps, owned: true);
        var id = (await host.Services.GetRequiredService<VacancyAssessments>().RequestAnnotationsAsync(before.Id, Ct))!.Value;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var source = await context.Vacancies.SingleAsync(row => row.Id == before.Id, Ct);
            if (change == "source") source.Description = "New source text";
            if (change == "evaluation") source.Evaluation = source.Evaluation! with { Summary = "New evaluation" };
            if (change == "score") source.Score = 37;
            if (change == "delivery") context.EmployerMessages.Add(new EmployerMessage
            {
                Id = Guid.NewGuid(), BusinessKey = "synthetic-unknown", VacancyId = before.Id, InputJson = "{}",
                Status = EmployerMessageStatus.OutcomeUnknown, CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }
        if (change == "candidate")
        {
            var publisher = host.Services.GetRequiredService<ICandidatePublisher>();
            Assert.Equal(2, (await publisher.PublishProfileAsync(_profile with { Summary = "New candidate proof" }, 1, "synthetic-change", Ct)).Version);
        }
        await host.Services.GetRequiredService<VacancyAssessmentWorkflow>().RunAsync(id, 1, VacancyAssessmentStage.Scoring, Guid.NewGuid(), Ct);
        Assert.Equal(VacancyAssessmentStage.Cancelled, (await RequestAsync(id)).Stage);
        Assert.Empty(steps.Calls);
        Assert.Equal(before.Status, (await VacancyAsync(before.Id)).Status);
        Assert.Empty((await VacancyAsync(before.Id)).Evaluation!.Keywords);
        Assert.Empty(await CardsAsync());
    }

    [Fact]
    public async Task Annotation_restart_from_the_saved_score_never_repeats_paid_scoring_or_creates_a_letter()
    {
        var before = await AnnotatedSeedAsync(VacancyStatus.Applied);
        var first = new Steps();
        var producer = await HostAsync(false, first, owned: true);
        var id = (await producer.Services.GetRequiredService<VacancyAssessments>().RequestAnnotationsAsync(before.Id, Ct))!.Value;
        await producer.Services.GetRequiredService<VacancyAssessmentWorkflow>().RunAsync(id, 1, VacancyAssessmentStage.Scoring, Guid.NewGuid(), Ct);
        var saved = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Finishing, saved.Stage);
        Assert.NotNull(saved.ScoreJson);
        Assert.Null(saved.LetterJson);
        Assert.Single(first.Calls);
        await StopAsync(producer);
        var replacement = new Steps();
        await HostAsync(true, replacement);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        Assert.Empty(replacement.Calls);
        Assert.Equal(saved.ScoreJson, (await RequestAsync(id)).ScoreJson);
        AssertAnnotationPreserved(before, await VacancyAsync(before.Id));
    }

    [Fact]
    public async Task Annotation_terminal_provider_failure_never_changes_historical_status_attempts_or_evaluation()
    {
        var before = await AnnotatedSeedAsync(VacancyStatus.Applied);
        var steps = new Steps { OnStage = (_, _, _) => throw new InvalidOperationException("synthetic invalid provider output") };
        var host = await HostAsync(false, steps, owned: true);
        var id = (await host.Services.GetRequiredService<VacancyAssessments>().RequestAnnotationsAsync(before.Id, Ct))!.Value;
        var workflow = host.Services.GetRequiredService<VacancyAssessmentWorkflow>();
        for (var attempt = 0; attempt < 3; attempt++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(id, 1, VacancyAssessmentStage.Scoring, Guid.NewGuid(), Ct));
        await workflow.RunAsync(id, 1, VacancyAssessmentStage.Scoring, Guid.NewGuid(), Ct);
        var failed = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Failed, failed.Stage);
        Assert.Equal(4, failed.Failures);
        Assert.Equal(VacancyAssessments.AnnotationHash(AssessmentAnnotationSnapshot.From(before)),
            VacancyAssessments.AnnotationHash(AssessmentAnnotationSnapshot.From(await VacancyAsync(before.Id))));
        Assert.Equal(before.Status, (await VacancyAsync(before.Id)).Status);
        Assert.Equal(before.AssessmentRevision, (await VacancyAsync(before.Id)).AssessmentRevision);
        Assert.Empty(await CardsAsync());
    }

    [Fact]
    public async Task Annotation_unavailability_preserves_the_request_and_cancellation_only_stops_the_wait()
    {
        var before = await AnnotatedSeedAsync();
        var steps = new Steps { OnStage = (_, _, _) => throw new LlmUnavailableException("synthetic provider pause") };
        var host = await HostAsync(false, steps, owned: true);
        var requests = host.Services.GetRequiredService<VacancyAssessments>();
        var id = (await requests.RequestAnnotationsAsync(before.Id, Ct))!.Value;
        var workflow = host.Services.GetRequiredService<VacancyAssessmentWorkflow>();
        await workflow.RunAsync(id, 1, VacancyAssessmentStage.Scoring, Guid.NewGuid(), Ct);
        var paused = await requests.WaitAnnotationsAsync(id, Ct);
        Assert.Equal(VacancyAssessmentStage.Scoring, paused.Stage);
        Assert.True(paused.RetryAt > DateTimeOffset.UtcNow);
        Assert.Equal(0, paused.Failures);
        Assert.Equal(id, await requests.RequestAnnotationsAsync(before.Id, Ct));
        steps.OnStage = null;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var root = await context.VacancyAssessmentRequests.SingleAsync(row => row.Id == id, Ct);
            root.RetryAt = null;
            await context.SaveChangesAsync(Ct);
        }
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.WaitAnnotationsAsync(id, cancellation.Token));
        Assert.Equal(VacancyAssessmentStage.Scoring, (await RequestAsync(id)).Stage);
        await workflow.RunAsync(id, 1, VacancyAssessmentStage.Scoring, Guid.NewGuid(), Ct);
        await workflow.RunAsync(id, 1, VacancyAssessmentStage.Finishing, Guid.NewGuid(), Ct);
        Assert.Equal(VacancyAssessmentStage.Completed, (await RequestAsync(id)).Stage);
        AssertAnnotationPreserved(before, await VacancyAsync(before.Id));
    }

    [Fact]
    public async Task Annotation_final_commit_fault_rolls_back_source_and_native_notice_then_reuses_the_saved_score()
    {
        var before = await AnnotatedSeedAsync(VacancyStatus.Applied);
        var retry = Barrier();
        var fault = new FailCommit(VacancyAssessmentStage.Completed, retry);
        var steps = new Steps();
        var host = await HostAsync(true, steps, fault);
        var id = (await host.Services.GetRequiredService<VacancyAssessments>().RequestAnnotationsAsync(before.Id, Ct))!.Value;
        await retry.Entered.Task.WaitAsync(Timeout, Ct);
        var saved = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Finishing, saved.Stage);
        Assert.Empty((await VacancyAsync(before.Id)).Evaluation!.Keywords);
        Assert.Equal(before.AssessmentRevision, (await VacancyAsync(before.Id)).AssessmentRevision);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) Assert.Empty(await context.Notifications.ToListAsync(Ct));
        retry.Release.TrySetResult();
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        AssertAnnotationPreserved(before, await VacancyAsync(before.Id));
        Assert.Equal(saved.ScoreJson, (await RequestAsync(id)).ScoreJson);
        Assert.Single(steps.Calls);
        await using var finished = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.JoinedAsync(finished, await finished.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("candidate")]
    public async Task Annotation_changed_source_during_paid_work_cannot_publish_or_overwrite_the_new_witness(string change)
    {
        var before = await AnnotatedSeedAsync();
        var hold = Barrier();
        var steps = new Steps { OnStage = (_, _, token) => hold.WaitAsync(token) };
        var host = await HostAsync(true, steps);
        var id = (await host.Services.GetRequiredService<VacancyAssessments>().RequestAnnotationsAsync(before.Id, Ct))!.Value;
        await hold.Entered.Task.WaitAsync(Timeout, Ct);
        if (change == "candidate")
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(_profile with { Summary = "Changed during paid work" }, 1, "synthetic-change", Ct);
        else
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.Vacancies.Where(row => row.Id == before.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Score, 12), Ct);
        }
        hold.Release.TrySetResult();
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Cancelled);
        Assert.Single(steps.Calls);
        var after = await VacancyAsync(before.Id);
        Assert.Equal(change == "score" ? 12 : before.Score, after.Score);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.AssessmentRevision, after.AssessmentRevision);
        Assert.Empty(after.Evaluation!.Keywords);
        Assert.Empty(await CardsAsync());
    }

    [Fact]
    public async Task Annotation_admission_fault_rolls_back_root_and_native_command_without_changing_the_source()
    {
        var before = await AnnotatedSeedAsync();
        var steps = new Steps();
        var host = await HostAsync(false, steps, new FailCommit(VacancyAssessmentStage.Scoring));
        var requests = host.Services.GetRequiredService<VacancyAssessments>();
        await Assert.ThrowsAsync<IOException>(() => requests.RequestAnnotationsAsync(before.Id, Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
        Assert.Equal(0, await NativePendingAsync());
        Assert.Equal(VacancyAssessments.AnnotationHash(AssessmentAnnotationSnapshot.From(before)),
            VacancyAssessments.AnnotationHash(AssessmentAnnotationSnapshot.From(await VacancyAsync(before.Id))));
        Assert.NotNull(await requests.RequestAnnotationsAsync(before.Id, Ct));
        Assert.Empty(steps.Calls);
    }

    [Fact]
    public async Task Annotation_unresolved_delivery_at_finish_preserves_the_checkpoint_and_historical_source()
    {
        var before = await AnnotatedSeedAsync(VacancyStatus.Applied);
        var steps = new Steps();
        var host = await HostAsync(false, steps, owned: true);
        var id = (await host.Services.GetRequiredService<VacancyAssessments>().RequestAnnotationsAsync(before.Id, Ct))!.Value;
        var workflow = host.Services.GetRequiredService<VacancyAssessmentWorkflow>();
        await workflow.RunAsync(id, 1, VacancyAssessmentStage.Scoring, Guid.NewGuid(), Ct);
        var saved = await RequestAsync(id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.EmployerMessages.Add(new EmployerMessage
            {
                Id = Guid.NewGuid(), BusinessKey = "synthetic-late-unknown", VacancyId = before.Id, InputJson = "{}",
                Status = EmployerMessageStatus.OutcomeUnknown, CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }
        await workflow.RunAsync(id, 1, VacancyAssessmentStage.Finishing, Guid.NewGuid(), Ct);
        Assert.Equal(VacancyAssessmentStage.Cancelled, (await RequestAsync(id)).Stage);
        Assert.Equal(saved.ScoreJson, (await RequestAsync(id)).ScoreJson);
        Assert.Equal(VacancyAssessments.AnnotationHash(AssessmentAnnotationSnapshot.From(before)),
            VacancyAssessments.AnnotationHash(AssessmentAnnotationSnapshot.From(await VacancyAsync(before.Id))));
        Assert.Single(steps.Calls);
        Assert.Empty(await CardsAsync());
    }
}
