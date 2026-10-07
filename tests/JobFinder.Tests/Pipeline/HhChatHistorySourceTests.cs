using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class HhNegotiationSourceIntegrationTests
{
    private async Task<(Application Application, HhChatHistorySources Owner, HhHistorySnapshot Snapshot)> HistoryAsync(SaveChangesInterceptor? fault = null)
    {
        var application = await SeedAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.HhResponses.Add(new() { HhVacancyId = long.Parse(application.Vacancy!.Sources.Single().ExternalId, CultureInfo.InvariantCulture),
                ChatId = 987, State = "response", SyncedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        await SourcesAsync(fault);
        var owner = _host!.Services.GetRequiredService<HhChatHistorySources>();
        var snapshot = Assert.IsType<HhHistorySnapshot>(await owner.CaptureAsync(application.Id, 987,
            long.Parse(application.Vacancy!.Sources.Single().ExternalId, CultureInfo.InvariantCulture), Ct));
        return (application, owner, snapshot);
    }

    private static List<HhChatMessage> HistoryMessages() => [new("10", "Synthetic Recruiter", "Synthetic interview invitation", "10:00", false),
        new("11", null, "Synthetic candidate reply", "10:01", true)];

    [Fact]
    public async Task History_is_published_once_with_exact_native_notice_without_analysis_stage_changes_or_delivery()
    {
        var (application, owner, snapshot) = await HistoryAsync();
        Assert.True(await owner.PublishAsync(snapshot, HistoryMessages(), Ct));
        Assert.False(await owner.PublishAsync(snapshot, HistoryMessages(), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var events = await context.ApplicationEvents.OrderBy(row => row.At).ThenBy(row => row.Id).ToListAsync(Ct);
        Assert.Equal(3, events.Count);
        Assert.Single(events, row => row.Kind == EventKind.MessageIn);
        Assert.Single(events, row => row.Kind == EventKind.MessageOut);
        Assert.Single(events, row => row.Kind == EventKind.Note);
        Assert.All(events, row => Assert.Equal(DraftStatus.None, row.DraftStatus));
        var unchanged = await context.Applications.SingleAsync(Ct);
        Assert.Equal(application.Revision, unchanged.Revision);
        Assert.Equal(application.Stage, unchanged.Stage);
        Assert.Equal(application.Status, unchanged.Status);
        Assert.Null(unchanged.LastInboundAt);
        Assert.Empty(await context.ReplyProcessingRequests.ToListAsync(Ct));
        Assert.Empty(await context.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Null(await owner.CaptureAsync(application.Id, snapshot.ChatId, snapshot.HhVacancyId, Ct));
    }

    [Fact]
    public async Task History_deduplicates_the_observed_set_against_saved_ids_and_email_text()
    {
        var (application, owner, initial) = await HistoryAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.ApplicationEvents.Add(new() { ApplicationId = application.Id, Kind = EventKind.MessageIn, Channel = EventChannel.Email,
            Text = "Synthetic interview invitation", At = application.SentAt!.Value });
        await context.SaveChangesAsync(Ct);
        Assert.False(await owner.PublishAsync(initial, HistoryMessages(), Ct));
        var snapshot = Assert.IsType<HhHistorySnapshot>(await owner.CaptureAsync(application.Id, initial.ChatId, initial.HhVacancyId, Ct));
        var messages = HistoryMessages();
        messages.Add(messages[1]);
        messages.Add(new("empty", null, " ", null, false));
        Assert.True(await owner.PublishAsync(snapshot, messages, Ct));
        Assert.Equal(3, await context.ApplicationEvents.CountAsync(Ct));
        Assert.Single(await context.ApplicationEvents.Where(row => row.ExternalId == "hhchat:11").ToListAsync(Ct));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("stage")]
    [InlineData("recipient")]
    [InlineData("binding")]
    [InlineData("unresolved")]
    [InlineData("incoming")]
    public async Task Changed_or_unresolved_history_witness_refuses_publication(string change)
    {
        var (application, owner, snapshot) = await HistoryAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (change == "revision") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        if (change == "stage") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Stage, ApplicationStage.Rejected), Ct);
        if (change == "recipient") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Target, "different"), Ct);
        if (change == "binding") await context.HhResponses.ExecuteUpdateAsync(update => update.SetProperty(row => row.ChatId, 123), Ct);
        if (change == "unresolved")
        {
            context.EmployerMessages.Add(new() { Id = Guid.NewGuid(), VacancyId = application.VacancyId, BusinessKey = "synthetic unresolved",
                Status = EmployerMessageStatus.OutcomeUnknown, InputJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        if (change == "incoming")
            await _host!.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(new()
            { ApplicationId = application.Id, Channel = EventChannel.HeadHunter, Author = "Synthetic Recruiter", Text = "Fresh question", ExternalId = "hhchat:fresh", At = DateTimeOffset.UtcNow }, Ct);
        Assert.False(await owner.PublishAsync(snapshot, HistoryMessages(), Ct));
        Assert.False(await owner.RecordFailureAsync(snapshot, "Stale failure", Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith(HhChatHistorySources.ProgressPrefix)).ToListAsync(Ct));
    }

    [Fact]
    public async Task Failed_read_count_survives_owner_recreation_and_replayed_failure_does_not_retire_a_chat()
    {
        var (application, owner, first) = await HistoryAsync();
        Assert.True(await owner.RecordFailureAsync(first, "First synthetic failure", Ct));
        Assert.False(await owner.RecordFailureAsync(first, "Replay", Ct));
        owner = ActivatorUtilities.CreateInstance<HhChatHistorySources>(_host!.Services);
        var second = Assert.IsType<HhHistorySnapshot>(await owner.CaptureAsync(application.Id, first.ChatId, first.HhVacancyId, Ct));
        Assert.True(await owner.RecordFailureAsync(second, "Second synthetic failure", Ct));
        Assert.False(await owner.RecordFailureAsync(second, "Replay", Ct));
        Assert.Null(await owner.CaptureAsync(application.Id, first.ChatId, first.HhVacancyId, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
        var progress = JsonSerializer.Deserialize((await context.Settings.SingleAsync(row => row.Key.StartsWith(HhChatHistorySources.ProgressPrefix), Ct)).Value,
            HhHistoryJsonContext.Default.HhHistoryProgress)!;
        Assert.Equal(2, progress.Failures);
        Assert.True(progress.Retired);
        var notice = await context.Notifications.SingleAsync(Ct);
        Assert.Contains("не подтверждение доставки", notice.Text);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_events_marker_progress_and_native_notice_roll_back_together(bool failure)
    {
        var fault = new HistoryFault();
        var (application, owner, snapshot) = await HistoryAsync(fault);
        if (failure)
        {
            Assert.True(await owner.RecordFailureAsync(snapshot, "First failure", Ct));
            snapshot = Assert.IsType<HhHistorySnapshot>(await owner.CaptureAsync(application.Id, snapshot.ChatId, snapshot.HhVacancyId, Ct));
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var before = await context.Settings.Where(row => row.Key.StartsWith(HhChatHistorySources.ProgressPrefix)).Select(row => row.Value).SingleOrDefaultAsync(Ct);
        fault.Active = true;
        await Assert.ThrowsAsync<IOException>(() => failure ? owner.RecordFailureAsync(snapshot, "Second failure", Ct) : owner.PublishAsync(snapshot, HistoryMessages(), Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(before, await context.Settings.Where(row => row.Key.StartsWith(HhChatHistorySources.ProgressPrefix)).Select(row => row.Value).SingleOrDefaultAsync(Ct));
        fault.Active = false;
        Assert.True(failure ? await owner.RecordFailureAsync(snapshot, "Second failure", Ct) : await owner.PublishAsync(snapshot, HistoryMessages(), Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task History_never_reparents_a_message_already_owned_by_another_application()
    {
        var (_, owner, snapshot) = await HistoryAsync();
        var other = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.ApplicationEvents.Add(new() { ApplicationId = other.Id, Kind = EventKind.MessageIn, Channel = EventChannel.HeadHunter,
            ExternalId = "hhchat:10", Text = "Foreign application history", At = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        Assert.False(await owner.PublishAsync(snapshot, HistoryMessages(), Ct));
        Assert.Equal(other.Id, (await context.ApplicationEvents.SingleAsync(Ct)).ApplicationId);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("{\"version\":99}")]
    [InlineData("{\"failures\":-1}")]
    [InlineData("{\"failures\":2}")]
    [InlineData("null")]
    public async Task Damaged_history_progress_refuses_capture_without_erasing_evidence(string json)
    {
        var (application, owner, snapshot) = await HistoryAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = HhChatHistorySources.ProgressPrefix + application.Id + "." + snapshot.ChatId, Value = json, UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<JsonException>(() => owner.CaptureAsync(application.Id, snapshot.ChatId, snapshot.HhVacancyId, Ct));
        Assert.Equal(json, (await context.Settings.SingleAsync(row => row.Key.StartsWith(HhChatHistorySources.ProgressPrefix), Ct)).Value);
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
    }

    private sealed class HistoryFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context is JobFinder.Data.JobFinderDbContext context
                && context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
                throw new IOException("synthetic hh history native admission rollback");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Live_incoming_admission_waits_for_the_history_source_transaction_instead_of_losing_its_raw_input()
    {
        var barrier = new HistoryBarrier();
        var (application, owner, snapshot) = await HistoryAsync(barrier);
        var publishing = owner.PublishAsync(snapshot, HistoryMessages(), Ct);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var admitting = _host!.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(new()
        {
            ApplicationId = application.Id, Channel = EventChannel.HeadHunter, Author = "Synthetic Recruiter",
            Text = "New question after history", ExternalId = "hhchat:late", At = DateTimeOffset.UtcNow,
        }, Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var context = await pg.Db.CreateDbContextAsync(timeout.Token);
            while (await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%SELECT id FROM applications%' ").SingleAsync(timeout.Token) == 0)
            {
                Assert.False(admitting.IsCompleted);
                await Task.Delay(25, timeout.Token);
            }
            Assert.False(admitting.IsCompleted);
            Assert.Empty(await context.ReplyProcessingRequests.ToListAsync(timeout.Token));
            Assert.Empty(await context.ApplicationEvents.ToListAsync(timeout.Token));
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.True(await publishing.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        var ticket = await admitting.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var root = await final.ReplyProcessingRequests.SingleAsync(Ct);
        Assert.Equal(ticket.RequestId, root.Id);
        Assert.Equal(ReplyProcessingStage.Capturing, root.Stage);
        Assert.Equal("New question after history", ReplyProcessingRequests.ReadRaw(root).Text);
        Assert.Equal(3, await final.ApplicationEvents.CountAsync(Ct));
        Assert.Empty(await final.EmployerMessages.ToListAsync(Ct));
    }

    private sealed class HistoryBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is JobFinder.Data.JobFinderDbContext context
                && context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
