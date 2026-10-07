using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed partial class HhNegotiationSourceIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;
    private long _nextSourceId = 910;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.InterviewPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.ReplyProcessingRequests.ExecuteDeleteAsync(Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.HhResponses.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith(HhNegotiationSources.UnreadPrefix)).ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == EmailInboxSources.LegacyKey || row.Key.StartsWith(EmailInboxSources.Prefix)).ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith(HhChatHistorySources.ProgressPrefix) || row.Key.StartsWith(HhResponseBackfillSources.UnavailablePrefix)).ExecuteDeleteAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled, jobfinder_queues.wolverine_queue_hiring_replies, jobfinder_queues.wolverine_queue_hiring_replies_scheduled", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); _host = null; }
    }

    private async Task<HhNegotiationSources> SourcesAsync(SaveChangesInterceptor? fault = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault);
        await _host.StartAsync(Ct);
        return _host.Services.GetRequiredService<HhNegotiationSources>();
    }

    private async Task<Application> SeedAsync(ApplicationStatus status = ApplicationStatus.Sent)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var sourceId = ++_nextSourceId;
        var application = new Application
        {
            Status = status, Mode = ApplicationMode.Approved, Channel = ApplicationChannel.HeadHunter,
            CreatedAt = now.AddDays(-8), SentAt = status == ApplicationStatus.Sent ? now.AddDays(-7) : null,
            Stage = status == ApplicationStatus.Sent ? ApplicationStage.Sent : null,
            StageChangedAt = status == ApplicationStatus.Sent ? now.AddDays(-7) : null,
            CoverLetter = "Synthetic approved application", Revision = 3,
            Vacancy = new Vacancy
            {
                Title = "Synthetic .NET Developer", CompanyName = "Synthetic Company", Description = "Synthetic vacancy",
                Url = $"https://rabota.by/vacancy/{sourceId}", Fingerprint = Guid.NewGuid().ToString("N"),
                Status = VacancyStatus.Scored, Decision = Decision.Approval, FirstSeenAt = now.AddDays(-8), UpdatedAt = now.AddDays(-8),
                Sources = [new VacancySource { Source = SourceKind.HeadHunter, ExternalId = sourceId.ToString(CultureInfo.InvariantCulture), Url = $"https://rabota.by/vacancy/{sourceId}" }],
            },
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        return await ReadAsync(application.Id);
    }

    private async Task<Application> ReadAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Applications.AsNoTracking().Include(row => row.Vacancy).ThenInclude(row => row!.Sources).SingleAsync(row => row.Id == id, Ct);
    }

    private static HhNegotiation Observation(Application application, string state = "response", bool unread = false, long? chatId = null,
        DateTimeOffset? updatedAt = null) => new(long.Parse(application.Vacancy!.Sources.Single().ExternalId, CultureInfo.InvariantCulture),
            state, unread, "synthetic-topic", application.Vacancy.Title, application.Vacancy.CompanyName,
            application.CreatedAt, updatedAt ?? DateTimeOffset.UtcNow, ChatId: chatId);

    [Theory]
    [InlineData("discard", ApplicationStage.Rejected, true)]
    [InlineData("offer", ApplicationStage.Offer, true)]
    [InlineData("hired", ApplicationStage.Accepted, true)]
    [InlineData("response", ApplicationStage.Viewed, false)]
    public async Task Stage_history_and_card_publish_together_without_duplicate_events(string state, ApplicationStage expected, bool card)
    {
        var application = await SeedAsync();
        var sources = await SourcesAsync();
        var observation = Observation(application, state) with { ViewedByEmployer = true };
        var observedAt = DateTimeOffset.UtcNow;
        Assert.Equal(1, (await sources.PublishAsync([observation], observedAt, Ct)).Changed);
        Assert.Equal(0, (await sources.PublishAsync([observation], observedAt, Ct)).Changed);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await ReadAsync(application.Id);
        Assert.Equal(expected, saved.Stage);
        Assert.Equal(application.Revision + 1, saved.Revision);
        Assert.Equal(expected, (await context.ApplicationEvents.SingleAsync(Ct)).Stage);
        Assert.Equal(state, (await context.HhResponses.SingleAsync(Ct)).State);
        Assert.Equal(card ? 1 : 0, await context.Notifications.CountAsync(Ct));
        await AssertNoticesJoinedAsync(context);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(ApplicationStatus.Pending)]
    [InlineData(ApplicationStatus.NeedsManual)]
    [InlineData(ApplicationStatus.DryRun)]
    public async Task Imported_history_dequeues_the_duplicate_and_publishes_one_summary(ApplicationStatus status)
    {
        var application = await SeedAsync(status);
        var sources = await SourcesAsync();
        var observation = Observation(application);
        var observedAt = DateTimeOffset.UtcNow;
        var publication = await sources.PublishAsync([observation], observedAt, Ct);
        Assert.Equal(1, publication.Imported);
        Assert.Equal(1, publication.Dequeued);
        Assert.Empty(publication.Chats);
        Assert.Equal(0, (await sources.PublishAsync([observation], observedAt, Ct)).Imported);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStatus.Cancelled, (await ReadAsync(application.Id)).Status);
        var imported = await context.Applications.SingleAsync(row => row.Id != application.Id, Ct);
        Assert.Equal(ApplicationMode.External, imported.Mode);
        Assert.Equal(ApplicationStatus.Sent, imported.Status);
        Assert.Equal(ApplicationStage.Sent, imported.Stage);
        Assert.Equal(VacancyStatus.Applied, (await context.Vacancies.SingleAsync(Ct)).Status);
        Assert.Contains("1 из них", (await context.Notifications.SingleAsync(Ct)).Text);
        Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
        await AssertNoticesJoinedAsync(context);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Imported_invitation_prepares_only_a_recent_interview_in_the_same_transaction(bool recent)
    {
        var application = await SeedAsync(ApplicationStatus.Pending);
        var sources = await SourcesAsync();
        var observation = Observation(application, "invitation", updatedAt: DateTimeOffset.UtcNow.AddDays(recent ? -1 : -30));
        Assert.Equal(1, (await sources.PublishAsync([observation], DateTimeOffset.UtcNow, Ct)).Imported);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var imported = await context.Applications.SingleAsync(row => row.Id != application.Id, Ct);
        Assert.Equal(ApplicationStage.HrScreen, imported.Stage);
        Assert.Equal(recent ? 1 : 0, await context.Interviews.CountAsync(Ct));
        Assert.Equal(recent ? 1 : 0, await context.InterviewPreparationRequests.CountAsync(Ct));
        if (recent)
        {
            var interview = await context.Interviews.SingleAsync(Ct);
            var root = await context.InterviewPreparationRequests.SingleAsync(Ct);
            Assert.Equal(root.Id, interview.PreparationRequestId);
            Assert.Equal(interview.Id, root.InterviewId);
            Assert.Equal(InterviewPreparationStage.Capturing, root.Stage);
        }
        await AssertNoticesJoinedAsync(context);
    }

    [Fact]
    public async Task Competing_publications_create_one_stage_event_and_notice()
    {
        var application = await SeedAsync();
        var sources = await SourcesAsync();
        var observation = Observation(application, "discard");
        var observedAt = DateTimeOffset.UtcNow;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sources.PublishAsync([observation], observedAt, Ct)));
        Assert.Equal(1, outcomes.Sum(row => row.Changed));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        await AssertNoticesJoinedAsync(context);
    }

    [Theory]
    [InlineData(ApplicationStage.NoResponse)]
    [InlineData(ApplicationStage.Rejected)]
    [InlineData(ApplicationStage.Accepted)]
    public async Task A_new_site_observation_does_not_reopen_a_manually_closed_application(ApplicationStage closed)
    {
        var application = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Applications.Where(row => row.Id == application.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Stage, closed), Ct);
        var sources = await SourcesAsync();
        Assert.Equal(0, (await sources.PublishAsync([Observation(application, "invitation")], DateTimeOffset.UtcNow, Ct)).Changed);
        Assert.Equal(closed, (await ReadAsync(application.Id)).Stage);
        Assert.Equal("invitation", (await context.HhResponses.SingleAsync(Ct)).State);
        Assert.Empty(await context.Interviews.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_failure_preserves_the_first_import_and_queued_application_including_interview_handoff(bool invitation)
    {
        var application = await SeedAsync(ApplicationStatus.Pending);
        var fault = new PublicationFault();
        var sources = await SourcesAsync(fault);
        var observation = Observation(application, invitation ? "invitation" : "response");
        var observedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(() => sources.PublishAsync([observation], observedAt, Ct));
        Assert.True(fault.SourceSaveCompleted);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Applications.ToListAsync(Ct));
        var unchanged = await ReadAsync(application.Id);
        Assert.Equal(ApplicationStatus.Pending, unchanged.Status);
        Assert.Equal(application.Revision, unchanged.Revision);
        Assert.Equal(VacancyStatus.Scored, unchanged.Vacancy!.Status);
        Assert.Empty(await context.HhResponses.ToListAsync(Ct));
        Assert.Empty(await context.Interviews.ToListAsync(Ct));
        Assert.Empty(await context.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync(context));
        fault.Active = false;
        Assert.Equal(1, (await sources.PublishAsync([observation], observedAt, Ct)).Imported);
        Assert.Equal(ApplicationStatus.Cancelled, (await ReadAsync(application.Id)).Status);
        await AssertNoticesJoinedAsync(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_admission_failure_rolls_back_response_stage_cursor_and_every_intermediate_save(bool invitation)
    {
        var application = await SeedAsync();
        var fault = new PublicationFault();
        var sources = await SourcesAsync(fault);
        var observation = Observation(application, invitation ? "invitation" : "discard", unread: true);
        var observedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(() => sources.PublishAsync([observation], observedAt, Ct));
        Assert.True(fault.SourceSaveCompleted);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var unchanged = await ReadAsync(application.Id);
        Assert.Equal(application.Stage, unchanged.Stage);
        Assert.Equal(application.Revision, unchanged.Revision);
        Assert.Empty(await context.HhResponses.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Interviews.ToListAsync(Ct));
        Assert.Empty(await context.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith(HhNegotiationSources.UnreadPrefix)).ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync(context));
        fault.Active = false;
        Assert.Equal(1, (await sources.PublishAsync([observation], observedAt, Ct)).Changed);
        await AssertNoticesJoinedAsync(context);
    }

    [Fact]
    public async Task Unread_notice_marker_survives_a_real_publisher_restart_and_rearms_after_read()
    {
        var application = await SeedAsync();
        var sources = await SourcesAsync();
        var unread = Observation(application, unread: true);
        var observedAt = DateTimeOffset.UtcNow;
        await sources.PublishAsync([unread], observedAt, Ct);
        await DisposeAsync();
        sources = await SourcesAsync();
        await sources.PublishAsync([unread], observedAt.AddSeconds(1), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Single(await context.Settings.Where(row => row.Key.StartsWith(HhNegotiationSources.UnreadPrefix)).ToListAsync(Ct));
        await sources.PublishAsync([unread with { HasUnread = false }], observedAt.AddSeconds(2), Ct);
        Assert.Empty(await context.Settings.AsNoTracking().Where(row => row.Key.StartsWith(HhNegotiationSources.UnreadPrefix)).ToListAsync(Ct));
        await sources.PublishAsync([unread], observedAt.AddSeconds(3), Ct);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
        await AssertNoticesJoinedAsync(context);
    }

    [Fact]
    public async Task Older_capture_or_older_site_revision_cannot_undo_newer_source_or_consume_unread_marker()
    {
        var application = await SeedAsync();
        var sources = await SourcesAsync();
        var newer = Observation(application, "discard", unread: true);
        var observedAt = DateTimeOffset.UtcNow;
        await sources.PublishAsync([newer], observedAt, Ct);
        await sources.PublishAsync([newer with { State = "response", HasUnread = false }], observedAt.AddSeconds(-1), Ct);
        await sources.PublishAsync([newer with { State = "response", HasUnread = false, UpdatedAt = newer.UpdatedAt!.Value.AddSeconds(-1) }], observedAt.AddSeconds(1), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var response = await context.HhResponses.SingleAsync(Ct);
        Assert.Equal("discard", response.State);
        Assert.True(response.HasUnread);
        Assert.Equal(ApplicationStage.Rejected, (await ReadAsync(application.Id)).Stage);
        Assert.Single(await context.Settings.Where(row => row.Key.StartsWith(HhNegotiationSources.UnreadPrefix)).ToListAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
        await AssertNoticesJoinedAsync(context);
    }

    [Theory]
    [InlineData(ApplicationStatus.Sending)]
    [InlineData(ApplicationStatus.OutcomeUnknown)]
    public async Task An_unresolved_initial_delivery_is_not_reconciled_by_response_history(ApplicationStatus status)
    {
        var application = await SeedAsync(status);
        var sources = await SourcesAsync();
        var publication = await sources.PublishAsync([Observation(application, "invitation", unread: true)], DateTimeOffset.UtcNow, Ct);
        Assert.Equal(0, publication.Imported);
        Assert.Equal(0, publication.Changed);
        Assert.Empty(publication.Chats);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(status, (await ReadAsync(application.Id)).Status);
        Assert.Single(await context.HhResponses.ToListAsync(Ct));
        Assert.Single(await context.Applications.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.Interviews.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(EmployerMessageStatus.Sending)]
    [InlineData(EmployerMessageStatus.OutcomeUnknown)]
    public async Task An_unlinked_unresolved_employer_message_fences_stage_changes_and_chat_reading(EmployerMessageStatus status)
    {
        var application = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.EmployerMessages.Add(new EmployerMessage
        {
            Id = Guid.NewGuid(), VacancyId = application.VacancyId, Status = status, CreatedAt = DateTimeOffset.UtcNow,
            BusinessKey = Guid.NewGuid().ToString("N"), InputJson = "{}",
        });
        await context.SaveChangesAsync(Ct);
        var sources = await SourcesAsync();
        var publication = await sources.PublishAsync([Observation(application, "discard", unread: true, chatId: 91)], DateTimeOffset.UtcNow, Ct);
        Assert.Equal(0, publication.Changed);
        Assert.Empty(publication.Chats);
        Assert.Equal(ApplicationStage.Sent, (await ReadAsync(application.Id)).Stage);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Single(await context.HhResponses.ToListAsync(Ct));
    }

    [Fact]
    public async Task Chat_reads_and_fallback_announcements_keep_their_separate_five_item_budgets()
    {
        var observations = new List<HhNegotiation>();
        for (var index = 0; index < 12; index++) observations.Add(Observation(await SeedAsync(), unread: true, chatId: index < 6 ? index + 90 : null));
        var sources = await SourcesAsync();
        var publication = await sources.PublishAsync(observations, DateTimeOffset.UtcNow, Ct);
        Assert.Equal(5, publication.Chats.Count);
        Assert.Equal(observations.Take(5).Select(row => row.ChatId!.Value), publication.Chats.Select(row => row.ChatId));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(5, await context.Notifications.CountAsync(Ct));
        Assert.Equal(5, await context.Settings.CountAsync(row => row.Key.StartsWith(HhNegotiationSources.UnreadPrefix), Ct));
        await AssertNoticesJoinedAsync(context);
    }

    [Fact]
    public async Task Unknown_states_and_uncollected_responses_keep_history_without_inventing_a_funnel_stage()
    {
        var application = await SeedAsync();
        var sources = await SourcesAsync();
        var unknown = Observation(application, "unknown-synthetic-state");
        var missing = unknown with { VacancyId = 90000, State = "response" };
        var publication = await sources.PublishAsync([unknown, missing], DateTimeOffset.UtcNow, Ct);
        Assert.Equal([unknown.State], publication.UnknownStates);
        Assert.Equal(0, publication.Imported);
        Assert.Equal(0, publication.Changed);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.HhResponses.CountAsync(Ct));
        Assert.Equal(ApplicationStage.Sent, (await ReadAsync(application.Id)).Stage);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
    }

    private static Task<int> CommandsAsync(JobFinderDbContext context) => context.Database.SqlQuery<int>($"""
        SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
        """).SingleAsync(Ct);

    private static async Task AssertNoticesJoinedAsync(JobFinderDbContext context)
    {
        var notices = await context.Notifications.AsNoTracking().ToListAsync(Ct);
        foreach (var notice in notices) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Equal(notices.Count, await CommandsAsync(context));
    }

    private sealed class PublicationFault : SaveChangesInterceptor
    {
        public bool Active { get; set; } = true;
        public bool SourceSaveCompleted { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context is JobFinderDbContext context && context.ChangeTracker.Entries<NotificationDeliveryRequest>()
                .Any(entry => entry.State == EntityState.Added))
            {
                SourceSaveCompleted = context.ChangeTracker.Entries<HhResponse>().Any(entry => entry.State == EntityState.Unchanged)
                    && context.ChangeTracker.Entries<ApplicationEvent>().Any(entry => entry.State == EntityState.Unchanged && entry.Entity.Id > 0);
                throw new InvalidOperationException("synthetic hh publication failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
