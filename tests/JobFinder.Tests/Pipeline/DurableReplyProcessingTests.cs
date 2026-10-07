using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableReplyProcessingTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.ReplyProcessingRequests.ExecuteDeleteAsync(Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.InterviewPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_hiring_replies, jobfinder_queues.wolverine_queue_hiring_replies_scheduled, jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Dispose(); }
        }
    }

    private async Task<IHost> HostAsync(bool consumer, Steps steps, SaveChangesInterceptor? fault = null, TimeProvider? time = null)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: services =>
        {
            services.RemoveAll<IReplyProcessingSteps>();
            services.AddSingleton<IReplyProcessingSteps>(steps);
            if (time is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(time);
            }
        });
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<InboundReply> IncomingAsync(string text = "Synthetic incoming message")
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var application = new Application
        {
            Status = ApplicationStatus.Sent, CreatedAt = DateTimeOffset.UtcNow,
            Vacancy = new Vacancy
            {
                Title = "Synthetic backend", CompanyName = "Synthetic", Url = "https://vacancy.invalid/1",
                Description = "Synthetic C# vacancy", Fingerprint = Guid.NewGuid().ToString("N"), FirstSeenAt = DateTimeOffset.UtcNow,
            },
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        return new() { ApplicationId = application.Id, Channel = EventChannel.Telegram, Author = "@synthetic",
            Text = text, ExternalId = Guid.NewGuid().ToString("N"), At = DateTimeOffset.UtcNow, YourChat = true };
    }

    private async Task<ReplyProcessingRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.ReplyProcessingRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<ReplyProcessingRequest> WaitAsync(Guid id, Func<ReplyProcessingRequest, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            var root = await RootAsync(id);
            if (predicate(root)) return root;
            await Task.Delay(100, timeout.Token);
        }
    }

    [Fact]
    public async Task Producer_commits_raw_input_and_command_without_paid_work_and_a_new_consumer_resumes_once()
    {
        var steps = new Steps();
        var raw = await IncomingAsync();
        var producer = await HostAsync(false, steps);
        var requests = producer.Services.GetRequiredService<ReplyProcessingRequests>();
        var ticket = await requests.IncomingAsync(raw, Ct);
        Assert.Equal(ticket, await requests.IncomingAsync(raw with { Text = "Duplicate observation" }, Ct));
        var saved = await RootAsync(ticket.RequestId);
        Assert.Equal(ReplyProcessingStage.Capturing, saved.Stage);
        Assert.Equal(raw.Text, ReplyProcessingRequests.ReadRaw(saved).Text);
        Assert.Equal(CanonicalJson.Hash(saved.RawJson), saved.RawHash);
        Assert.Empty(steps.Calls);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_replies").SingleAsync(Ct));
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.WaitAsync(ticket.RequestId, cancelled.Token));
        }
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(true, steps);
        await WaitAsync(ticket.RequestId, row => row.Stage == ReplyProcessingStage.Completed);
        Assert.Single(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var source = Assert.Single(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Equal(raw.Text, source.Text);
        Assert.Equal(raw.ExternalId, source.ExternalId);
        Assert.Empty(await final.EmployerMessages.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(final, await final.Notifications.SingleAsync(Ct), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Services.GetRequiredService<ReplyProcessingRequests>().RetryAsync(ticket.RequestId, Ct));
    }

    [Fact]
    public async Task Two_messages_for_one_application_are_captured_in_order_and_the_second_sees_the_first()
    {
        var steps = new Steps();
        var raw = await IncomingAsync("First synthetic message");
        var producer = await HostAsync(false, steps);
        var requests = producer.Services.GetRequiredService<ReplyProcessingRequests>();
        var first = await requests.IncomingAsync(raw, Ct);
        var second = await requests.IncomingAsync(raw with { Text = "Second synthetic message", ExternalId = "second", At = raw.At.AddMinutes(1) }, Ct);
        await producer.StopAsync(Ct);
        await HostAsync(true, steps);
        await WaitAsync(first.RequestId, row => row.Stage == ReplyProcessingStage.Completed);
        await WaitAsync(second.RequestId, row => row.Stage == ReplyProcessingStage.Completed);
        Assert.Equal(2, steps.Calls.Count);
        var secondInput = Assert.Single(steps.Calls, call => call.Input.Message == "Second synthetic message").Input;
        Assert.Contains("First synthetic message", secondInput.Conversation);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.ApplicationEvents.CountAsync(Ct));
        Assert.Equal(raw.At.AddMinutes(1).UtcTicks / 10, (await context.Applications.SingleAsync(Ct)).LastInboundAt!.Value.UtcTicks / 10);
    }

    [Fact]
    public async Task Deep_outage_retains_fast_checkpoint_and_resumes_deep_without_repeating_fast_analysis()
    {
        var steps = new Steps { DeepUnavailable = true, Considered = true };
        var host = await HostAsync(true, steps);
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(await IncomingAsync(), Ct);
        var paused = await WaitAsync(ticket.RequestId, row => row.Stage == ReplyProcessingStage.Deep && row.RetryAt is not null);
        Assert.NotNull(paused.BestJson);
        Assert.Equal(1, steps.Calls.Count(call => !call.Deep));
        Assert.Equal(1, steps.Calls.Count(call => call.Deep));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var bytes = Encoding.UTF8.GetBytes(ticket.RequestId.ToString());
            Assert.Equal(1, await context.Database.SqlQuery<int>($"""
                SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_hiring_replies_scheduled WHERE position({bytes} in body) > 0)
                  + (SELECT count(*) FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND position({bytes} in body) > 0))::int AS "Value"
                """).SingleAsync(Ct));
        }
        steps.DeepUnavailable = false;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.ReplyProcessingRequests.Where(row => row.Id == ticket.RequestId).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await host.Services.GetRequiredService<ReplyProcessingWorkflow>().RunAsync(new ProcessReply
            { RequestId = ticket.RequestId, Stage = ReplyProcessingStage.Deep, DeepAttempt = paused.DeepAttempt }, Guid.NewGuid(), Ct);
        await WaitAsync(ticket.RequestId, row => row.Stage == ReplyProcessingStage.Completed);
        Assert.Equal(1, steps.Calls.Count(call => !call.Deep));
        Assert.Equal(2, steps.Calls.Count(call => call.Deep));
        Assert.NotNull(steps.Calls.Last().Best);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("version")]
    [InlineData("mode")]
    public async Task Damaged_raw_or_protocol_is_quarantined_once_without_paid_work(string damage)
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var ticket = await producer.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(await IncomingAsync(), Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var root = await context.ReplyProcessingRequests.SingleAsync(Ct);
            if (damage == "raw") root.RawHash = "damaged";
            if (damage == "version")
            {
                var raw = ReplyProcessingRequests.ReadRaw(root) with { Version = 99 };
                root.RawJson = ReplyProcessingRequests.Serialize(raw);
                root.RawHash = CanonicalJson.Hash(root.RawJson);
            }
            if (damage == "mode") root.Mode = ReplyProcessingMode.Preview;
            await context.SaveChangesAsync(Ct);
        }
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(true, steps);
        await WaitAsync(ticket.RequestId, row => row.Stage == ReplyProcessingStage.Failed);
        await consumer.Services.GetRequiredService<ReplyProcessingWorkflow>().RunAsync(new ProcessReply
            { RequestId = ticket.RequestId, Stage = ReplyProcessingStage.Capturing }, Guid.NewGuid(), Ct);
        Assert.Empty(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Single(await final.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
    }

    [Fact]
    public async Task Source_change_cancels_publication_but_retains_raw_and_explicit_retry_captures_the_new_source()
    {
        var steps = new Steps();
        var host = await HostAsync(true, steps);
        steps.BeforeResult = async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.Description, "New synthetic source"), Ct);
            steps.BeforeResult = null;
        };
        var requests = host.Services.GetRequiredService<ReplyProcessingRequests>();
        var ticket = await requests.IncomingAsync(await IncomingAsync(), Ct);
        var retired = await WaitAsync(ticket.RequestId, row => row.Stage == ReplyProcessingStage.Cancelled);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        var retry = await requests.RetryAsync(ticket.RequestId, Ct);
        var completed = await WaitAsync(retry.RequestId, row => row.Stage == ReplyProcessingStage.Completed);
        Assert.Equal(retired.Id, completed.RetryOfId);
        Assert.Equal(retired.RawJson, completed.RawJson);
        Assert.Equal(retired.RawHash, completed.RawHash);
        Assert.Equal("New synthetic source", steps.Calls.Last().Input.Vacancy.Description);
        Assert.Equal(2, steps.Calls.Count);
    }

    [Fact]
    public async Task Legacy_committed_external_message_is_not_analyzed_or_certified_again()
    {
        var steps = new Steps();
        var raw = await IncomingAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.ApplicationEvents.Add(new() { ApplicationId = raw.ApplicationId, Kind = EventKind.MessageIn,
                Channel = raw.Channel, ExternalId = raw.ExternalId, Text = raw.Text, At = raw.At });
            await context.SaveChangesAsync(Ct);
        }
        var host = await HostAsync(false, steps);
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(raw, Ct);
        Assert.True(ticket.AlreadyPublished);
        Assert.Equal(Guid.Empty, ticket.RequestId);
        Assert.Empty(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.ReplyProcessingRequests.ToListAsync(Ct));
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Pure_preview_reuses_completed_preparation_without_source_or_card_publication()
    {
        var steps = new Steps();
        var raw = await IncomingAsync();
        long eventId;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var source = new ApplicationEvent { ApplicationId = raw.ApplicationId, Kind = EventKind.MessageIn, Channel = raw.Channel,
                Author = raw.Author, Text = raw.Text, At = raw.At, Draft = "Original synthetic draft", DraftStatus = DraftStatus.Pending, Revision = 7 };
            context.ApplicationEvents.Add(source);
            await context.SaveChangesAsync(Ct);
            eventId = source.Id;
        }
        var host = await HostAsync(true, steps);
        var owner = host.Services.GetRequiredService<ReplyDraftSources>();
        var requests = host.Services.GetRequiredService<ReplyProcessingRequests>();
        var capture = Assert.IsType<CapturedReplyDraft>(await owner.CaptureAsync(eventId, Ct));
        var first = await requests.RedraftAsync(capture, true, Ct);
        await requests.WaitAsync(first.RequestId, Ct).WaitAsync(TimeSpan.FromSeconds(45), Ct);
        var second = await requests.RedraftAsync(Assert.IsType<CapturedReplyDraft>(await owner.CaptureAsync(eventId, Ct)), true, Ct);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Single(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var unchanged = await final.ApplicationEvents.SingleAsync(Ct);
        Assert.Equal(7, unchanged.Revision);
        Assert.Equal("Original synthetic draft", unchanged.Draft);
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
        Assert.Empty(await final.EmployerMessages.ToListAsync(Ct));
    }

    private sealed class Steps : IReplyProcessingSteps
    {
        public ConcurrentQueue<(ReplyProcessingInput Input, ReplyProcessingResult? Best, bool Deep)> Calls { get; } = new();
        public bool DeepUnavailable { get; set; }
        public bool Considered { get; set; }
        public bool Routine { get; set; }
        public string Classification { get; set; } = "other";
        public string? InterviewAt { get; set; }
        public string? InterviewKind { get; set; }
        public Func<Task>? BeforeResult { get; set; }
        public void EnsureCompatible(ReplyProcessingInput input) { }
        public async Task<ReplyProcessingResult> AnalyzeAsync(ReplyProcessingInput input, ReplyProcessingResult? best, bool deep, CancellationToken ct)
        {
            Calls.Enqueue((input, best, deep));
            if (deep && DeepUnavailable) throw new LlmUnavailableException("synthetic deep analysis outage");
            if (BeforeResult is { } callback) await callback();
            return new() { Analysis = new ReplyAnalysis { Classification = Classification, Summary = "Synthetic analysis",
                InterviewAt = InterviewAt, InterviewKind = InterviewKind,
                Intent = Considered ? "technical" : "small_talk", Counterpart = "human",
                Routine = Routine, Draft = Considered ? "Спасибо, расскажу о решении на встрече." : Routine ? "Спасибо, буду ждать ответа." : null } };
        }
    }

    [Theory]
    [InlineData(ReplyProcessingStage.Publishing)]
    [InlineData(ReplyProcessingStage.Completed)]
    public async Task Checkpoint_or_final_admission_rollback_does_not_repeat_paid_analysis(ReplyProcessingStage failingStage)
    {
        var steps = new Steps();
        var fault = new CheckpointFault(failingStage);
        var host = await HostAsync(true, steps, fault);
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(await IncomingAsync(), Ct);
        await WaitAsync(ticket.RequestId, row => row.Stage == ReplyProcessingStage.Completed);
        Assert.Equal(1, fault.Failures);
        Assert.Single(steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_handoff_is_atomic_and_the_card_distinguishes_queued_from_delivered(bool yourChat)
    {
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(3));
        var steps = new Steps { Routine = true };
        var host = await HostAsync(false, steps, time: new FixedTime(now));
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var publisher = await pg.CandidateAsync();
        await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true }, 0, "reply-test", Ct);
        await publisher.PublishFactsAsync(new CandidateFacts(), 0, "reply-test", Ct);
        var raw = await IncomingAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(raw with { At = now, YourChat = yourChat }, Ct);
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        foreach (var stage in new[] { ReplyProcessingStage.Capturing, ReplyProcessingStage.Fast, ReplyProcessingStage.Publishing })
            await workflow.RunAsync(new ProcessReply { RequestId = ticket.RequestId, Stage = stage }, Guid.NewGuid(), Ct);
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

    [Theory]
    [InlineData("hash")]
    [InlineData("message")]
    [InlineData("snapshot")]
    [InlineData("version")]
    [InlineData("operation-version")]
    [InlineData("operation-policy")]
    public async Task Damaged_captured_input_is_rejected_before_the_paid_stage(string damage)
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var ticket = await host.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(await IncomingAsync(), Ct);
        var workflow = host.Services.GetRequiredService<ReplyProcessingWorkflow>();
        await workflow.RunAsync(new ProcessReply { RequestId = ticket.RequestId, Stage = ReplyProcessingStage.Capturing }, Guid.NewGuid(), Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var root = await context.ReplyProcessingRequests.SingleAsync(Ct);
            var input = JsonNode.Parse(root.InputJson!)!;
            if (damage == "message") input["message"] = "Different message";
            if (damage == "snapshot") input["draftSnapshot"] = new JsonObject { ["eventId"] = 1 };
            if (damage == "version") input["version"] = 99;
            if (damage == "operation-version") input["operation"]!["schemaVersion"] = 99;
            if (damage == "operation-policy") input["operation"]!["policy"] = null;
            root.InputJson = input.ToJsonString();
            root.InputHash = damage == "hash" ? "damaged" : CanonicalJson.Hash(root.InputJson);
            await context.SaveChangesAsync(Ct);
        }
        await Assert.ThrowsAsync<UnsupportedReplyProcessingException>(() => workflow.RunAsync(new ProcessReply
            { RequestId = ticket.RequestId, Stage = ReplyProcessingStage.Fast }, Guid.NewGuid(), Ct));
        Assert.Equal(ReplyProcessingStage.Failed, (await RootAsync(ticket.RequestId)).Stage);
        Assert.Empty(steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_refuses_retained_roots_and_orphaned_native_commands(bool orphaned)
    {
        var host = await HostAsync(false, new Steps());
        await host.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(await IncomingAsync(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (orphaned) await context.ReplyProcessingRequests.ExecuteDeleteAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003021921_DigestRunWitness", Ct));
        Assert.Contains("Cannot roll back durable replies", error.MessageText);
        Assert.Contains("20261003133115_DurableReplyProcessing", await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    [Theory]
    [InlineData("incoming", "jobfinder.hiring.process-reply.v1")]
    [InlineData("incoming", "jobfinder.hiring.process-reply.v99")]
    [InlineData("outgoing", "jobfinder.hiring.process-reply.v1")]
    [InlineData("outgoing", "jobfinder.hiring.process-reply.v99")]
    [InlineData("dead-letter", "jobfinder.hiring.process-reply.v1")]
    [InlineData("dead-letter", "jobfinder.hiring.process-reply.v99")]
    public async Task Downgrade_refuses_orphaned_current_and_future_protocol_envelopes(string store, string messageType)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var id = Guid.NewGuid();
        if (store == "incoming")
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_incoming_envelopes (id,status,owner_id,body,message_type) VALUES ({id},'Scheduled',0,{Array.Empty<byte>()},{messageType})", Ct);
        else if (store == "outgoing")
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_outgoing_envelopes (id,owner_id,destination,body,message_type) VALUES ({id},0,'postgresql://hiring_replies/',{Array.Empty<byte>()},{messageType})", Ct);
        else
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_dead_letters (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{messageType})", Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003021921_DigestRunWitness", Ct));
        Assert.Contains("Cannot roll back durable replies", error.MessageText);
        Assert.Contains("20261003133115_DurableReplyProcessing", await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    private sealed class CheckpointFault(ReplyProcessingStage stage) : SaveChangesInterceptor
    {
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Failures == 0 && eventData.Context is JobFinderDbContext context
                && context.ChangeTracker.Entries<ReplyProcessingRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == stage))
            {
                Failures++;
                throw new IOException("synthetic reply checkpoint/admission rollback");
            }
            return ValueTask.FromResult(result);
        }
    }
}
