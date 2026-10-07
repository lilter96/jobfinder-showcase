using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableReplyProcessingTests
{
    private async Task<(long EventId, InboundReply Corrected)> EmailSourceAsync(DateTimeOffset? at = null)
    {
        var raw = await IncomingAsync("Original truncated mail");
        raw = raw with { Channel = EventChannel.Email, Author = "recruiter@synthetic.invalid", At = at ?? raw.At };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = new ApplicationEvent { ApplicationId = raw.ApplicationId, Kind = EventKind.MessageIn, Channel = raw.Channel,
            Author = raw.Author, Text = raw.Text, ExternalId = raw.ExternalId, At = raw.At, Draft = "Original draft",
            DraftStatus = DraftStatus.Pending, Revision = 7, Classification = ReplyClass.Other };
        context.ApplicationEvents.Add(source);
        await context.SaveChangesAsync(Ct);
        return (source.Id, raw with { Text = "Corrected complete mail", Subject = "Corrected subject" });
    }

    private static Task RunReplyStageAsync(ReplyProcessingWorkflow workflow, Guid id, ReplyProcessingStage stage, int deepAttempt = 0) =>
        workflow.RunAsync(new ProcessReply { RequestId = id, Stage = stage, DeepAttempt = deepAttempt }, Guid.NewGuid(), Ct);

    [Fact]
    public async Task Email_correction_retains_identity_preimage_and_command_and_publishes_once_without_deleting_history()
    {
        var steps = new Steps { Classification = "question" };
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var (eventId, corrected) = await EmailSourceAsync();
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        var requests = host.Services.GetRequiredService<ReplyProcessingRequests>();
        var ticket = await requests.ReprocessAsync(captured, corrected, Ct);
        Assert.Equal(ticket, await requests.ReprocessAsync(captured, corrected, Ct));
        Assert.Empty(steps.Calls);
        var root = await RootAsync(ticket.RequestId);
        var input = ReplyProcessingRequests.ReadInput(root);
        Assert.Equal(2, input.Version);
        Assert.Equal(ReplyDraftSources.SourceJson(captured.Event), input.OriginalEventJson);
        Assert.Equal(corrected.Message(), input.Message);
        Assert.DoesNotContain("Original truncated mail", input.Conversation);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal("Original truncated mail", (await context.ApplicationEvents.SingleAsync(Ct)).Text);
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_replies").SingleAsync(Ct));
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.WaitAsync(ticket.RequestId, cancelled.Token));
        }
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Fast);
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Publishing);
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Publishing);
        Assert.Single(steps.Calls);
        var repeated = await requests.ReprocessAsync(captured, corrected, Ct);
        Assert.Equal(ticket.RequestId, repeated.RequestId);
        Assert.True(repeated.AlreadyPublished);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await final.ApplicationEvents.SingleAsync(row => row.Kind == EventKind.MessageIn, Ct);
        Assert.Equal(eventId, saved.Id);
        Assert.Equal(8, saved.Revision);
        Assert.Equal(corrected.Message(), saved.Text);
        Assert.Equal(corrected.ExternalId, saved.ExternalId);
        Assert.Equal(corrected.Author, saved.Author);
        Assert.Equal(corrected.At.UtcTicks / 10, saved.At.UtcTicks / 10);
        Assert.Equal(ReplyClass.Question, saved.Classification);
        Assert.Equal(ApplicationStage.Replied, (await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Equal(input.OriginalEventJson, ReplyProcessingRequests.ReadInput(await RootAsync(ticket.RequestId)).OriginalEventJson);
        foreach (var notice in await final.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(final, notice, Ct);
        Assert.Empty(await final.DeliveryAttempts.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("author")]
    [InlineData("date")]
    [InlineData("channel")]
    public async Task Email_correction_refuses_a_different_mail_before_admission(string mismatch)
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        var (eventId, corrected) = await EmailSourceAsync();
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        corrected = mismatch switch
        {
            "identity" => corrected with { ExternalId = "another-mail" },
            "author" => corrected with { Author = "another@synthetic.invalid" },
            "date" => corrected with { At = corrected.At.AddSeconds(1) },
            _ => corrected with { Channel = EventChannel.Telegram },
        };
        await Assert.ThrowsAsync<ReplyProcessingSupersededException>(() => host.Services.GetRequiredService<ReplyProcessingRequests>().ReprocessAsync(captured, corrected, Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.ReplyProcessingRequests.ToListAsync(Ct));
        Assert.Equal("Original truncated mail", (await final.ApplicationEvents.SingleAsync(Ct)).Text);
        Assert.Empty(steps.Calls);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("paid")]
    [InlineData("publication")]
    public async Task Email_correction_preserves_a_concurrently_changed_event_at_every_boundary(string boundary)
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var (eventId, corrected) = await EmailSourceAsync();
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        async Task ChangeAsync()
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.ApplicationEvents.Where(row => row.Id == eventId).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Text, "Concurrent source correction").SetProperty(row => row.Revision, 8L), Ct);
        }
        var requests = host.Services.GetRequiredService<ReplyProcessingRequests>();
        if (boundary == "admission")
        {
            await ChangeAsync();
            await Assert.ThrowsAsync<ReplyProcessingSupersededException>(() => requests.ReprocessAsync(captured, corrected, Ct));
        }
        else
        {
            var ticket = await requests.ReprocessAsync(captured, corrected, Ct);
            if (boundary == "paid") await ChangeAsync();
            else steps.BeforeResult = ChangeAsync;
            await RunReplyStageAsync(host.Services.GetRequiredService<ReplyProcessingWorkflow>(), ticket.RequestId, ReplyProcessingStage.Fast);
            Assert.Equal(ReplyProcessingStage.Cancelled, (await RootAsync(ticket.RequestId)).Stage);
            Assert.NotNull(ReplyProcessingRequests.ReadInput(await RootAsync(ticket.RequestId)).OriginalEventJson);
        }
        Assert.Equal(boundary == "publication" ? 1 : 0, steps.Calls.Count);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal("Concurrent source correction", (await final.ApplicationEvents.SingleAsync(Ct)).Text);
        Assert.Empty(await final.EmployerMessages.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(DraftStatus.Sent)]
    [InlineData(DraftStatus.Sending)]
    [InlineData(DraftStatus.OutcomeUnknown)]
    public async Task Email_correction_refuses_sent_or_uncertain_source_even_with_a_captured_witness(DraftStatus status)
    {
        var host = await HostAsync(false, new Steps());
        var (eventId, corrected) = await EmailSourceAsync();
        var sources = host.Services.GetRequiredService<ReplyDraftSources>();
        var captured = Assert.IsType<CapturedReplyDraft>(await sources.CaptureAsync(eventId, Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.ApplicationEvents.Where(row => row.Id == eventId).ExecuteUpdateAsync(update => update.SetProperty(row => row.DraftStatus, status), Ct);
        Assert.Null(await sources.CaptureAsync(eventId, Ct));
        await Assert.ThrowsAsync<ReplyProcessingSupersededException>(() => host.Services.GetRequiredService<ReplyProcessingRequests>().ReprocessAsync(captured, corrected, Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.ReplyProcessingRequests.ToListAsync(Ct));
        Assert.Equal(status, (await final.ApplicationEvents.SingleAsync(Ct)).DraftStatus);
    }

    [Fact]
    public async Task Email_correction_review_outage_retains_original_event_and_resumes_only_saved_deep_analysis()
    {
        var steps = new Steps { Considered = true, DeepUnavailable = true };
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var (eventId, corrected) = await EmailSourceAsync();
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().ReprocessAsync(captured, corrected, Ct);
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Fast);
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Deep, 1);
        var paused = await RootAsync(ticket.RequestId);
        Assert.NotNull(paused.BestJson);
        Assert.NotNull(paused.RetryAt);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal(7, (await context.ApplicationEvents.SingleAsync(Ct)).Revision);
            Assert.Equal("Original truncated mail", (await context.ApplicationEvents.SingleAsync(Ct)).Text);
            await context.ReplyProcessingRequests.Where(row => row.Id == ticket.RequestId).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        }
        steps.DeepUnavailable = false;
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Deep, 1);
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Publishing, 1);
        Assert.Equal(ReplyProcessingStage.Completed, (await RootAsync(ticket.RequestId)).Stage);
        Assert.Single(steps.Calls, call => !call.Deep);
        Assert.Equal(2, steps.Calls.Count(call => call.Deep));
        Assert.NotNull(steps.Calls.Last().Best);
    }

    [Fact]
    public async Task Email_correction_final_rollback_retains_event_and_reuses_paid_checkpoint_for_card_and_interview()
    {
        var when = DateTimeOffset.UtcNow.AddDays(2);
        var steps = new Steps { Classification = "invitation", InterviewAt = when.ToString("O"), InterviewKind = "technical" };
        var fault = new CheckpointFault(ReplyProcessingStage.Completed);
        var host = await HostAsync(false, steps, fault);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var (eventId, corrected) = await EmailSourceAsync();
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().ReprocessAsync(captured, corrected, Ct);
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Fast);
        await Assert.ThrowsAsync<IOException>(() => RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Publishing));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal("Original truncated mail", (await context.ApplicationEvents.SingleAsync(Ct)).Text);
            Assert.Equal(7, (await context.ApplicationEvents.SingleAsync(Ct)).Revision);
            Assert.Empty(await context.Interviews.ToListAsync(Ct));
            Assert.Empty(await context.Notifications.ToListAsync(Ct));
        }
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Publishing);
        Assert.Single(steps.Calls);
        Assert.Equal(1, fault.Failures);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(eventId, (await final.ApplicationEvents.SingleAsync(row => row.Kind == EventKind.MessageIn, Ct)).Id);
        Assert.Equal(ApplicationStage.TechInterview, (await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Equal(when.UtcTicks / 10, (await final.Interviews.SingleAsync(Ct)).ScheduledAt!.Value.UtcTicks / 10);
        foreach (var notice in await final.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(final, notice, Ct);
    }

    [Theory]
    [InlineData(EmployerMessageStatus.Pending)]
    [InlineData(EmployerMessageStatus.Ready)]
    [InlineData(EmployerMessageStatus.Sending)]
    [InlineData(EmployerMessageStatus.OutcomeUnknown)]
    public async Task Email_correction_refuses_an_active_delivery_added_after_capture_and_admission(EmployerMessageStatus status)
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var (eventId, corrected) = await EmailSourceAsync();
        var sources = host.Services.GetRequiredService<ReplyDraftSources>();
        var captured = Assert.IsType<CapturedReplyDraft>(await sources.CaptureAsync(eventId, Ct));
        var requests = host.Services.GetRequiredService<ReplyProcessingRequests>();
        var ticket = await requests.ReprocessAsync(captured, corrected, Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.EmployerMessages.Add(new EmployerMessage { Id = Guid.NewGuid(), BusinessKey = Guid.NewGuid().ToString("N"),
                VacancyId = captured.Snapshot.VacancyId, ApplicationId = corrected.ApplicationId, InputJson = "{}", Status = status, CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        Assert.Null(await sources.CaptureAsync(eventId, Ct));
        await Assert.ThrowsAsync<ReplyProcessingSupersededException>(() => requests.ReprocessAsync(captured, corrected, Ct));
        await RunReplyStageAsync(host.Services.GetRequiredService<ReplyProcessingWorkflow>(), ticket.RequestId, ReplyProcessingStage.Fast);
        Assert.Equal(ReplyProcessingStage.Cancelled, (await RootAsync(ticket.RequestId)).Stage);
        Assert.Empty(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal("Original truncated mail", (await final.ApplicationEvents.SingleAsync(Ct)).Text);
        Assert.Equal(status, (await final.EmployerMessages.SingleAsync(Ct)).Status);
        Assert.Empty(await final.DeliveryAttempts.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("interview")]
    public async Task Email_correction_refuses_changed_candidate_or_interview_before_paid_work(string changed)
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var (eventId, corrected) = await EmailSourceAsync();
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().ReprocessAsync(captured, corrected, Ct);
        if (changed == "candidate")
            await (await pg.CandidateAsync()).PublishProfileAsync(new CandidateProfile { Name = "New captured candidate" }, 0, "correction-test", Ct);
        else
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            context.Interviews.Add(new Interview { ApplicationId = corrected.ApplicationId, Status = InterviewStatus.Scheduled,
                Kind = InterviewKind.Technical, ScheduledAt = DateTimeOffset.UtcNow.AddDays(2) });
            await context.SaveChangesAsync(Ct);
        }
        await RunReplyStageAsync(host.Services.GetRequiredService<ReplyProcessingWorkflow>(), ticket.RequestId, ReplyProcessingStage.Fast);
        Assert.Equal(ReplyProcessingStage.Cancelled, (await RootAsync(ticket.RequestId)).Stage);
        Assert.Empty(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal("Original truncated mail", (await final.ApplicationEvents.SingleAsync(Ct)).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Email_correction_retains_automatic_reply_policy_and_never_certifies_delivery(bool yourChat)
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(3));
        var steps = new Steps { Routine = true };
        var host = await HostAsync(false, steps, time: new FixedTime(now));
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var publisher = await pg.CandidateAsync();
        await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true }, 0, "correction-test", Ct);
        await publisher.PublishFactsAsync(new CandidateFacts(), 0, "correction-test", Ct);
        var (eventId, corrected) = await EmailSourceAsync(now);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().ReprocessAsync(captured, corrected with { YourChat = yourChat }, Ct);
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Fast);
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Publishing);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ReplyProcessingStage.Completed, (await final.ReplyProcessingRequests.SingleAsync(Ct)).Stage);
        Assert.Equal(DraftStatus.Pending, (await final.ApplicationEvents.SingleAsync(Ct)).DraftStatus);
        var notice = await final.Notifications.SingleAsync(Ct);
        if (yourChat) Assert.Empty(await final.EmployerMessages.ToListAsync(Ct));
        else
        {
            Assert.Equal(EmployerMessageStatus.Pending, (await final.EmployerMessages.SingleAsync(Ct)).Status);
            Assert.Contains("доставка ещё не подтверждена", notice.Text);
        }
        Assert.Empty(await final.DeliveryAttempts.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(final, notice, Ct);
    }

    [Fact]
    public async Task Email_correction_damaged_preimage_is_quarantined_before_paid_work_and_v1_shape_is_unchanged()
    {
        Assert.DoesNotContain("originalEventJson", ReplyProcessingRequests.Serialize(new ReplyProcessingInput()));
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var (eventId, corrected) = await EmailSourceAsync();
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().ReprocessAsync(captured, corrected, Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var root = await context.ReplyProcessingRequests.SingleAsync(Ct);
            var input = ReplyProcessingRequests.ReadInput(root);
            input.OriginalEventJson = "{}";
            input.DraftSnapshot!.SourceHash = Fingerprint.Of(input.OriginalEventJson);
            root.InputJson = ReplyProcessingRequests.Serialize(input);
            root.InputHash = CanonicalJson.Hash(root.InputJson);
            await context.SaveChangesAsync(Ct);
        }
        await Assert.ThrowsAsync<UnsupportedReplyProcessingException>(() => RunReplyStageAsync(host.Services.GetRequiredService<ReplyProcessingWorkflow>(), ticket.RequestId, ReplyProcessingStage.Fast));
        Assert.Equal(ReplyProcessingStage.Failed, (await RootAsync(ticket.RequestId)).Stage);
        Assert.Empty(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal("Original truncated mail", (await final.ApplicationEvents.SingleAsync(Ct)).Text);
        var requests = host.Services.GetRequiredService<ReplyProcessingRequests>();
        var retry = await requests.ReprocessAsync(captured, corrected, Ct);
        Assert.NotEqual(ticket.RequestId, retry.RequestId);
        Assert.Equal(retry.RequestId, (await requests.ReprocessAsync(captured, corrected, Ct)).RequestId);
        Assert.Equal(ticket.RequestId, (await RootAsync(retry.RequestId)).RetryOfId);
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        await RunReplyStageAsync(workflow, retry.RequestId, ReplyProcessingStage.Fast);
        await RunReplyStageAsync(workflow, retry.RequestId, ReplyProcessingStage.Publishing);
        Assert.Equal(ReplyProcessingStage.Completed, (await RootAsync(retry.RequestId)).Stage);
        Assert.Equal(ReplyProcessingStage.Failed, (await RootAsync(ticket.RequestId)).Stage);
        Assert.Single(steps.Calls);
    }

    [Fact]
    public async Task Email_correction_has_a_separate_native_key_and_keeps_the_completed_incoming_root()
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var raw = await IncomingAsync("Original native mail");
        raw = raw with { Channel = EventChannel.Email, Author = "recruiter@synthetic.invalid" };
        var requests = host.Services.GetRequiredService<ReplyProcessingRequests>();
        var original = await requests.IncomingAsync(raw, Ct);
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        await RunReplyStageAsync(workflow, original.RequestId, ReplyProcessingStage.Capturing);
        await RunReplyStageAsync(workflow, original.RequestId, ReplyProcessingStage.Fast);
        await RunReplyStageAsync(workflow, original.RequestId, ReplyProcessingStage.Publishing);
        var before = await RootAsync(original.RequestId);
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(before.EventId!.Value, Ct));
        var corrected = raw with { Text = "Corrected native mail" };
        var ticket = await requests.ReprocessAsync(captured, corrected, Ct);
        Assert.NotEqual(original.RequestId, ticket.RequestId);
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Fast);
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Publishing);
        Assert.Equal(original.RequestId, (await requests.IncomingAsync(corrected, Ct)).RequestId);
        var after = await RootAsync(original.RequestId);
        Assert.Equal(before.RawJson, after.RawJson);
        Assert.Equal(before.InputJson, after.InputJson);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
        Assert.Equal(2, steps.Calls.Count);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var source = await final.ApplicationEvents.SingleAsync(Ct);
        Assert.Equal(before.EventId, source.Id);
        Assert.Equal(corrected.Text, source.Text);
        Assert.Equal(2, await final.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Email_correction_delivery_admission_after_paid_checkpoint_fences_final_publication()
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var (eventId, corrected) = await EmailSourceAsync();
        var captured = Assert.IsType<CapturedReplyDraft>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, Ct));
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().ReprocessAsync(captured, corrected, Ct);
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Fast);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.EmployerMessages.Add(new EmployerMessage { Id = Guid.NewGuid(), BusinessKey = Guid.NewGuid().ToString("N"),
                VacancyId = captured.Snapshot.VacancyId, ApplicationId = corrected.ApplicationId, InputJson = "{}",
                Status = EmployerMessageStatus.OutcomeUnknown, CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        await RunReplyStageAsync(workflow, ticket.RequestId, ReplyProcessingStage.Publishing);
        Assert.Equal(ReplyProcessingStage.Cancelled, (await RootAsync(ticket.RequestId)).Stage);
        Assert.Single(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal("Original truncated mail", (await final.ApplicationEvents.SingleAsync(Ct)).Text);
        Assert.Equal(EmployerMessageStatus.OutcomeUnknown, (await final.EmployerMessages.SingleAsync(Ct)).Status);
        Assert.Empty(await final.DeliveryAttempts.ToListAsync(Ct));
    }
}
