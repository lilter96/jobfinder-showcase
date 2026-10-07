using System.Reflection;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed class FollowUpSourceIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly LetterDraft Draft = new("Добрый день! Подскажите, пожалуйста, есть ли новости по моему отклику?", [], []);
    private IHost? _host;
    private long _nextSourceId = 730;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private async Task<FollowUpSources> SourcesAsync(SaveChangesInterceptor? fault = null, bool consumer = false,
        Action<IServiceCollection>? configure = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: configure);
        await _host.StartAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        return _host.Services.GetRequiredService<FollowUpSources>();
    }

    private async Task<Application> SeedAsync(ApplicationChannel channel = ApplicationChannel.TelegramDm, bool proposed = false)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var application = new Application
        {
            Revision = 3, Channel = channel, Mode = ApplicationMode.Approved, Status = ApplicationStatus.Sent,
            Stage = ApplicationStage.Sent, CreatedAt = now.AddDays(-30), SentAt = now.AddDays(-30), StageChangedAt = now.AddDays(-30),
            LastOutboundAt = now.AddDays(-30), FollowUpSentAt = proposed ? now.AddDays(-14) : null,
            Target = channel == ApplicationChannel.Email ? "synthetic@example.invalid" : "@synthetic_recruiter",
            TargetPeerId = 731, EmailMessageId = "synthetic-message@example.invalid", CoverLetter = "Synthetic initial application",
            Vacancy = new Vacancy
            {
                Title = "Synthetic .NET Developer", CompanyName = "Synthetic Company", Description = "Synthetic .NET vacancy",
                Url = "https://example.invalid/jobs/1", Fingerprint = Guid.NewGuid().ToString("N"), Status = VacancyStatus.Applied,
                FirstSeenAt = now.AddDays(-31), UpdatedAt = now.AddDays(-30),
                Sources = [new VacancySource { Source = SourceKind.HeadHunter, ExternalId = (++_nextSourceId).ToString(System.Globalization.CultureInfo.InvariantCulture), Url = "https://example.invalid/jobs/1" }],
            },
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        return await ReadAsync(application.Id);
    }

    private async Task<Application> ReadAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Applications.AsNoTracking().Include(row => row.Vacancy).ThenInclude(row => row!.Company)
            .Include(row => row.Vacancy).ThenInclude(row => row!.Sources).SingleAsync(row => row.Id == id, Ct);
    }

    [Theory]
    [InlineData(ApplicationChannel.TelegramDm, EventChannel.Telegram)]
    [InlineData(ApplicationChannel.Email, EventChannel.Email)]
    [InlineData(ApplicationChannel.HeadHunter, EventChannel.HeadHunter)]
    public async Task Draft_flag_event_and_exact_card_commit_with_one_native_command(ApplicationChannel channel, EventChannel eventChannel)
    {
        var application = await SeedAsync(channel);
        var sources = await SourcesAsync();
        var snapshot = FollowUpSnapshot.Capture(application);
        Assert.True(await sources.DraftAsync(snapshot, Draft, 5, Ct));
        Assert.False(await sources.DraftAsync(snapshot, Draft, 5, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await ReadAsync(application.Id);
        Assert.NotNull(saved.FollowUpSentAt);
        Assert.Equal(application.Revision + 1, saved.Revision);
        var source = Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Equal(EventKind.FollowUp, source.Kind);
        Assert.Equal(eventChannel, source.Channel);
        Assert.Equal(Draft.Text, source.Draft);
        Assert.Equal(DraftStatus.Pending, source.DraftStatus);
        Assert.Equal(channel == ApplicationChannel.HeadHunter ? null : application.Target, source.Author);
        Assert.Equal(channel == ApplicationChannel.Email ? application.EmailMessageId : null, source.ExternalId);
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Contains(source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), notice.Buttons[0][0]);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Equal(1, await NotificationCommandsAsync(context));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_draft_publication_keeps_one_proposal_and_one_native_card()
    {
        var application = await SeedAsync();
        var sources = await SourcesAsync();
        var snapshot = FollowUpSnapshot.Capture(application);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sources.DraftAsync(snapshot, Draft, 5, Ct)));
        Assert.Single(results, result => result);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, Assert.Single(await context.Notifications.ToListAsync(Ct)), Ct);
        Assert.Equal(1, await NotificationCommandsAsync(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Draft_admission_failure_preserves_the_source_and_reuses_the_existing_text(bool failAtEvent)
    {
        var application = await SeedAsync();
        var fault = new PublicationFault { FailAtEvent = failAtEvent };
        var sources = await SourcesAsync(fault);
        var snapshot = FollowUpSnapshot.Capture(application);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => sources.DraftAsync(snapshot, Draft, 5, Ct));
        Assert.Equal(!failAtEvent, fault.SourceSaveCompleted);
        Assert.True(snapshot.Matches(await ReadAsync(application.Id)));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await AssertNoPublicationAsync(context);
        fault.Active = false;
        Assert.True(await sources.DraftAsync(snapshot, Draft, 5, Ct));
        Assert.Equal(Draft.Text, (await context.ApplicationEvents.SingleAsync(Ct)).Draft);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("inbound")]
    [InlineData("stage")]
    [InlineData("outbound")]
    [InlineData("proposal")]
    [InlineData("revision")]
    [InlineData("target")]
    [InlineData("letter")]
    [InlineData("vacancy")]
    [InlineData("operation")]
    public async Task Stale_source_cannot_publish_a_draft_or_close_the_application(string change)
    {
        var application = await SeedAsync();
        var snapshot = FollowUpSnapshot.Capture(application);
        var sources = await SourcesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var current = await context.Applications.Include(row => row.Vacancy).SingleAsync(Ct);
        switch (change)
        {
            case "inbound": current.LastInboundAt = DateTimeOffset.UtcNow; break;
            case "stage": current.Stage = ApplicationStage.Replied; break;
            case "outbound": current.LastOutboundAt = DateTimeOffset.UtcNow; break;
            case "proposal": current.FollowUpSentAt = DateTimeOffset.UtcNow; break;
            case "revision": current.Revision++; break;
            case "target": current.Target = "@replacement_recipient"; break;
            case "letter": current.CoverLetter = "Replacement letter"; break;
            case "vacancy": current.Vacancy!.Description = "Replacement vacancy"; break;
            case "operation": current.OperationId = Guid.NewGuid(); break;
        }
        await context.SaveChangesAsync(Ct);
        if (change != "revision")
            await context.Applications.Where(row => row.Id == application.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, application.Revision), Ct);
        Assert.False(await sources.DraftAsync(snapshot, Draft, 5, Ct));
        Assert.False(await sources.CloseAsync(snapshot, "Synthetic no-response closure", Ct));
        await AssertNoPublicationAsync(context);
    }

    [Theory]
    [InlineData(EmployerMessageStatus.Sending, false)]
    [InlineData(EmployerMessageStatus.Sending, true)]
    [InlineData(EmployerMessageStatus.OutcomeUnknown, false)]
    [InlineData(EmployerMessageStatus.OutcomeUnknown, true)]
    public async Task Unresolved_employer_delivery_refuses_a_follow_up_and_automatic_closure(EmployerMessageStatus status, bool linked)
    {
        var application = await SeedAsync();
        var sources = await SourcesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.EmployerMessages.Add(new EmployerMessage
        {
            Id = Guid.NewGuid(), BusinessKey = Guid.NewGuid().ToString("N"), InputJson = "{}", Status = status,
            ApplicationId = linked ? application.Id : null, VacancyId = application.VacancyId, CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        var snapshot = FollowUpSnapshot.Capture(application);
        Assert.False(await sources.DraftAsync(snapshot, Draft, 5, Ct));
        Assert.False(await sources.CloseAsync(snapshot, "Synthetic no-response closure", Ct));
        Assert.True(snapshot.Matches(await ReadAsync(application.Id)));
        await AssertNoPublicationAsync(context);
    }

    [Theory]
    [InlineData(ApplicationStatus.Sending)]
    [InlineData(ApplicationStatus.OutcomeUnknown)]
    public async Task An_unresolved_initial_application_blocks_follow_up_and_closure_of_another_sent_copy(ApplicationStatus status)
    {
        var application = await SeedAsync();
        var sources = await SourcesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Applications.Add(new Application
        {
            VacancyId = application.VacancyId, Channel = ApplicationChannel.TelegramDm, Status = status,
            Target = application.Target, CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        var snapshot = FollowUpSnapshot.Capture(application);
        Assert.False(await sources.DraftAsync(snapshot, Draft, 5, Ct));
        Assert.False(await sources.CloseAsync(snapshot, "Synthetic no-response closure", Ct));
        Assert.True(snapshot.Matches(await ReadAsync(application.Id)));
        await AssertNoPublicationAsync(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_response_closure_and_notice_commit_once_under_the_original_source_witness(bool manual)
    {
        var application = await SeedAsync(manual ? ApplicationChannel.Manual : ApplicationChannel.TelegramDm, proposed: !manual);
        var sources = await SourcesAsync();
        var snapshot = FollowUpSnapshot.Capture(application);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sources.CloseAsync(snapshot, "Synthetic no-response closure", Ct)));
        Assert.Single(results, result => result);
        Assert.Equal(ApplicationStage.NoResponse, (await ReadAsync(application.Id)).Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(EventKind.StageChanged, (await context.ApplicationEvents.SingleAsync(Ct)).Kind);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(1, await NotificationCommandsAsync(context));
    }

    [Fact]
    public async Task Closure_admission_failure_preserves_the_stage_and_history_until_a_checked_retry()
    {
        var application = await SeedAsync(ApplicationChannel.Manual);
        var fault = new PublicationFault();
        var sources = await SourcesAsync(fault);
        var snapshot = FollowUpSnapshot.Capture(application);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => sources.CloseAsync(snapshot, "Synthetic no-response closure", Ct));
        Assert.True(fault.SourceSaveCompleted);
        Assert.True(snapshot.Matches(await ReadAsync(application.Id)));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await AssertNoPublicationAsync(context);
        fault.Active = false;
        Assert.True(await sources.CloseAsync(snapshot, "Synthetic no-response closure", Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_actual_timer_uses_the_Hiring_owner_for_due_no_response_closure_without_paid_work(bool manual)
    {
        var application = await SeedAsync(manual ? ApplicationChannel.Manual : ApplicationChannel.TelegramDm, proposed: !manual);
        await SourcesAsync();
        var publisher = _host!.Services.GetRequiredService<ICandidatePublisher>();
        var profile = await _host.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct);
        await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true }, profile?.Version ?? 0, "synthetic-test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        var job = ActivatorUtilities.CreateInstance<FollowUpJob>(_host.Services);
        var method = typeof(FollowUpJob).GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var outcome = await (Task<JobOutcome>)method.Invoke(job, [Ct])!;
        Assert.Equal(1, outcome.Items);
        Assert.Equal(ApplicationStage.NoResponse, (await ReadAsync(application.Id)).Stage);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
    }

    [Fact]
    public async Task The_actual_timer_reuses_its_completed_native_letter_after_card_admission_failure_without_redrafting_or_reviewing()
    {
        var application = await SeedAsync();
        var fault = new PublicationFault();
        var steps = new LetterSteps();
        var clock = new LocalClock(new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero)), "Europe/Minsk");
        await SourcesAsync(fault, consumer: true, configure: services =>
        {
            services.AddSingleton(clock);
            services.AddSingleton<ILetterPreparationSteps>(steps);
        });
        var publisher = _host!.Services.GetRequiredService<ICandidatePublisher>();
        var profile = await _host.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct);
        var facts = await _host.Services.GetRequiredService<FactsProvider>().GetSnapshotAsync(Ct);
        await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true }, profile?.Version ?? 0, "synthetic-test", Ct);
        await publisher.PublishFactsAsync(new CandidateFacts(), facts?.Version ?? 0, "synthetic-test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        var job = ActivatorUtilities.CreateInstance<FollowUpJob>(_host.Services);
        var method = typeof(FollowUpJob).GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<JobOutcome> RunAsync() => ((Task<JobOutcome>)method.Invoke(job, [Ct])!).WaitAsync(TimeSpan.FromSeconds(90), Ct);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        Assert.True(fault.SourceSaveCompleted);
        Assert.Null((await ReadAsync(application.Id)).FollowUpSentAt);
        await AssertNoPublicationAsync(context);
        var root = Assert.Single(await context.LetterPreparationRequests.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(LetterPreparationStage.Completed, root.Stage);
        Assert.True(steps.Drafts > 0);
        Assert.True(steps.Reviews > 0);
        var drafts = steps.Drafts;
        var reviews = steps.Reviews;
        fault.Active = false;
        Assert.Equal(1, (await RunAsync()).Items);
        Assert.Equal(drafts, steps.Drafts);
        Assert.Equal(reviews, steps.Reviews);
        Assert.Equal(root.Id, (await context.LetterPreparationRequests.AsNoTracking().SingleAsync(Ct)).Id);
        Assert.Equal(Draft.Text, (await context.ApplicationEvents.SingleAsync(Ct)).Draft);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.NotNull((await ReadAsync(application.Id)).FollowUpSentAt);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_manual_channel_or_rejected_or_empty_draft_does_not_consume_the_proposal_flag()
    {
        var application = await SeedAsync();
        var manual = await SeedAsync(ApplicationChannel.Manual);
        var unsupported = await SeedAsync((ApplicationChannel)999);
        var sources = await SourcesAsync();
        Assert.False(await sources.DraftAsync(FollowUpSnapshot.Capture(manual), Draft, 5, Ct));
        Assert.False(await sources.DraftAsync(FollowUpSnapshot.Capture(unsupported), Draft, 5, Ct));
        var snapshot = FollowUpSnapshot.Capture(application);
        Assert.False(await sources.DraftAsync(snapshot, Draft with { Problems = ["synthetic failed content gate"] }, 5, Ct));
        Assert.False(await sources.DraftAsync(snapshot, Draft with { Text = " " }, 5, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sources.DraftAsync(snapshot, Draft, -1, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.True(snapshot.Matches(await ReadAsync(application.Id)));
        await AssertNoPublicationAsync(context);
    }

    private static Task<int> NotificationCommandsAsync(JobFinderDbContext context) => context.Database.SqlQuery<int>($"""
        SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
        """).SingleAsync(Ct);

    private static async Task AssertNoPublicationAsync(JobFinderDbContext context)
    {
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await NotificationCommandsAsync(context));
    }

    private sealed class PublicationFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public bool FailAtEvent { get; set; }
        public bool SourceSaveCompleted { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Active) return ValueTask.FromResult(result);
            var context = eventData.Context!;
            if (FailAtEvent ? context.ChangeTracker.Entries<ApplicationEvent>().Any(entry => entry.State == EntityState.Added)
                : context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                SourceSaveCompleted = context.ChangeTracker.Entries<ApplicationEvent>().Any(entry => entry.State == EntityState.Unchanged && entry.Entity.Id > 0)
                    && context.ChangeTracker.Entries<Notification>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Id > 0);
                throw new InvalidOperationException("synthetic follow-up publication failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class LetterSteps : ILetterPreparationSteps
    {
        public int Drafts;
        public int Reviews;

        public void EnsureCompatible(LetterPreparationInput input)
        {
            Assert.False(input.Brief.First);
            Assert.False(input.Brief.AttachesResume);
            Assert.True(input.Profile.Reviewed);
            Assert.True(input.FactsVersion > 0);
            Assert.Contains("Synthetic initial application", input.Brief.Conversation);
        }

        public Task<LetterCandidate> DraftAsync(LetterPreparationInput input, LetterCandidate? best, CancellationToken ct)
        {
            EnsureCompatible(input);
            Interlocked.Increment(ref Drafts);
            return Task.FromResult(new LetterCandidate { Text = Draft.Text, Written = Draft.Text });
        }

        public Task<LetterCandidate> ReviewAsync(LetterPreparationInput input, LetterCandidate draft, CancellationToken ct)
        {
            EnsureCompatible(input);
            Interlocked.Increment(ref Reviews);
            return Task.FromResult(draft with
            {
                Score = 10,
                Review = new ApplicationLetterReview
                {
                    TextHash = Fingerprint.Of(draft.Text), ContextHash = input.Recipe.Hash, Recipe = input.Recipe,
                    Score = 10, CheckedAt = DateTimeOffset.UtcNow,
                },
            });
        }
    }
}
