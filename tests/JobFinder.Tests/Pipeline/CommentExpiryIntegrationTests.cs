using System.Reflection;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed class CommentExpiryIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.CommentDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.CommentSourceRequests.ExecuteDeleteAsync(Ct);
        await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        await context.PostProcessingRequests.ExecuteDeleteAsync(Ct);
        await context.RawPosts.ExecuteDeleteAsync(Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == CommentExpiry.CursorKey).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private async Task<CommentExpiry> ExpiryAsync(IInterceptor? fault = null, int age = 48)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);
        });
        await _host.StartAsync(Ct);
        var policies = _host.Services.GetRequiredService<SearchPolicyStore>();
        var snapshot = await policies.ReadAsync(Ct);
        snapshot.Value.Threads.MaxPostAgeHours = age;
        await policies.PublishAsync(snapshot.Value, snapshot.Version, "synthetic-expiry", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        return _host.Services.GetRequiredService<CommentExpiry>();
    }

    private async Task<CommentDraft> DraftAsync(bool old = true, CommentStatus status = CommentStatus.Pending)
    {
        var draft = new CommentDraft
        {
            PostId = "synthetic-" + Guid.NewGuid().ToString("N"), PostUrl = "https://threads.invalid/post/synthetic",
            Author = "synthetic-author", PostText = "synthetic text", CreatedAt = _time.GetUtcNow(),
            PostPublishedAt = _time.GetUtcNow().AddHours(old ? -49 : -1), Status = status, Revision = 3,
        };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        return draft;
    }

    private async Task<CommentSourceRequest> SourceAsync(CommentDraft draft, DateTimeOffset? expiry)
    {
        var profile = new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true };
        var input = new CommentSourceInput
        {
            ProfileVersion = 1, ProfileHash = CanonicalJson.Hash(ProfileProvider.ToJson(profile)),
            Preparation = new CommentPreparationInput { Profile = profile, Author = draft.Author, PostText = draft.PostText },
            PostId = draft.PostId, PostUrl = draft.PostUrl, PublishedAt = draft.PostPublishedAt, ExpiresAt = expiry, PolicyVersion = 1,
        };
        var json = JsonSerializer.Serialize(input, CommentSourceJsonContext.Default.CommentSourceInput);
        var root = new CommentSourceRequest
        {
            Id = Guid.NewGuid(), PostId = draft.PostId, Author = draft.Author, SourceKey = "synthetic-" + draft.PostId,
            InputJson = json, InputHash = CanonicalJson.Hash(json), Stage = CommentSourceStage.Completed,
            DraftId = draft.Id, CreatedAt = _time.GetUtcNow(), CompletedAt = _time.GetUtcNow(),
        };
        draft.SourceRequestId = root.Id;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.CommentSourceRequests.Add(root);
        context.CommentDrafts.Update(draft);
        await context.SaveChangesAsync(Ct);
        return root;
    }

    [Fact]
    public async Task Old_post_with_a_fresh_draft_expires_once_with_revision_and_native_notice()
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync();
        var witness = CommentExpirySnapshot.Capture(draft);
        Assert.True(await expiry.ExpireAsync(witness, Ct));
        Assert.False(await expiry.ExpireAsync(witness, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.CommentDrafts.SingleAsync(Ct);
        Assert.Equal(CommentStatus.Expired, saved.Status);
        Assert.Equal(4, saved.Revision);
        Assert.Equal(_time.GetUtcNow(), saved.DecidedAt);
        Assert.Contains("устарел", saved.Error!);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Empty(await context.CommentDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task Legacy_draft_without_post_time_uses_creation_time_and_the_exact_deadline()
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync();
        draft.PostPublishedAt = null;
        draft.CreatedAt = _time.GetUtcNow().AddHours(-48);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.CommentDrafts.Update(draft);
        await context.SaveChangesAsync(Ct);
        Assert.True(await expiry.ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
    }

    [Fact]
    public async Task Fresh_pending_draft_is_not_modified_or_announced()
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync(old: false);
        Assert.False(await expiry.ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(3, (await context.CommentDrafts.SingleAsync(Ct)).Revision);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(96, -1, -1, true)]
    [InlineData(24, -25, 10, true)]
    [InlineData(48, -1, 1, false)]
    public async Task Frozen_deadline_cannot_be_extended_and_current_policy_can_only_tighten_it(int age, int publishedHours, int expiresHours, bool expected)
    {
        var expiry = await ExpiryAsync(age: age);
        var draft = await DraftAsync(old: false);
        draft.PostPublishedAt = _time.GetUtcNow().AddHours(publishedHours);
        var source = await SourceAsync(draft, _time.GetUtcNow().AddHours(expiresHours));
        Assert.Equal(expected, await expiry.ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(source.InputHash, (await context.CommentSourceRequests.SingleAsync(Ct)).InputHash);
        Assert.Equal(CommentSourceStage.Completed, (await context.CommentSourceRequests.SingleAsync(Ct)).Stage);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("status")]
    [InlineData("post")]
    [InlineData("created")]
    [InlineData("source")]
    [InlineData("delivery")]
    [InlineData("variant")]
    public async Task Changed_source_witness_is_refused_without_overwriting_or_notifying(string field)
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync();
        var witness = CommentExpirySnapshot.Capture(draft);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.CommentDrafts.SingleAsync(Ct);
        switch (field)
        {
            case "revision": saved.Revision++; break;
            case "status": saved.Status = CommentStatus.Approved; break;
            case "post": saved.PostText += " changed"; break;
            case "created": saved.CreatedAt = saved.CreatedAt.AddMinutes(1); break;
            case "source": saved.SourceRequestId = Guid.NewGuid(); break;
            case "delivery": saved.DeliveryRequestId = Guid.NewGuid(); break;
            case "variant": saved.Variants.Add("changed"); break;
        }
        await context.SaveChangesAsync(Ct);
        Assert.False(await expiry.ExpireAsync(witness, Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        await context.Entry(saved).ReloadAsync(Ct);
        Assert.NotEqual(CommentStatus.Expired, saved.Status);
    }

    [Theory]
    [InlineData(CommentStatus.Approved)]
    [InlineData(CommentStatus.Posted)]
    [InlineData(CommentStatus.Sending)]
    [InlineData(CommentStatus.OutcomeUnknown)]
    [InlineData(CommentStatus.Rejected)]
    [InlineData(CommentStatus.Expired)]
    [InlineData(CommentStatus.Failed)]
    public async Task Expiry_never_reauthorizes_or_changes_non_pending_work(CommentStatus status)
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync(status: status);
        Assert.False(await expiry.ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
        Assert.Equal(0, await expiry.ExpireBatchAsync(10, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(3, (await context.CommentDrafts.SingleAsync(Ct)).Revision);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(CommentDeliveryStage.Preparing)]
    [InlineData(CommentDeliveryStage.Ready)]
    [InlineData(CommentDeliveryStage.Sending)]
    [InlineData(CommentDeliveryStage.OutcomeUnknown)]
    public async Task An_unlinked_active_delivery_root_fences_even_a_pending_draft(CommentDeliveryStage stage)
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.CommentDeliveryRequests.Add(new CommentDeliveryRequest
        {
            Id = Guid.NewGuid(), DraftId = draft.Id, Revision = draft.Revision, Stage = stage, CreatedAt = _time.GetUtcNow(),
        });
        await context.SaveChangesAsync(Ct);
        Assert.False(await expiry.ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
        Assert.Equal(0, await expiry.ExpireBatchAsync(1, Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("json")]
    [InlineData("version")]
    [InlineData("foreign")]
    [InlineData("unfinished")]
    [InlineData("no-deadline")]
    public async Task Damaged_source_is_preserved_and_does_not_starve_healthy_later_work(string damage)
    {
        var expiry = await ExpiryAsync();
        var damaged = await DraftAsync();
        var source = await SourceAsync(damaged, damage == "no-deadline" ? null : _time.GetUtcNow().AddMinutes(-1));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = await context.CommentSourceRequests.SingleAsync(Ct);
        switch (damage)
        {
            case "missing": context.CommentSourceRequests.Remove(root); break;
            case "hash": root.InputHash = "damaged"; break;
            case "json": root.InputJson = "[]"; root.InputHash = CanonicalJson.Hash(root.InputJson); break;
            case "version":
                var input = JsonSerializer.Deserialize(root.InputJson, CommentSourceJsonContext.Default.CommentSourceInput)!;
                input.Version = 2;
                root.InputJson = JsonSerializer.Serialize(input, CommentSourceJsonContext.Default.CommentSourceInput);
                root.InputHash = CanonicalJson.Hash(root.InputJson);
                break;
            case "foreign": root.DraftId++; break;
            case "unfinished": root.Stage = CommentSourceStage.Waiting; break;
        }
        await context.SaveChangesAsync(Ct);
        var healthy = await DraftAsync();
        Assert.Equal(0, await expiry.ExpireBatchAsync(1, Ct));
        Assert.Equal(1, await expiry.ExpireBatchAsync(1, Ct));
        Assert.Equal(0, await expiry.ExpireBatchAsync(1, Ct));
        Assert.Equal(CommentStatus.Pending, (await context.CommentDrafts.AsNoTracking().SingleAsync(row => row.Id == damaged.Id, Ct)).Status);
        Assert.Equal(CommentStatus.Expired, (await context.CommentDrafts.AsNoTracking().SingleAsync(row => row.Id == healthy.Id, Ct)).Status);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        if (damage != "missing")
        {
            var after = await context.CommentSourceRequests.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct);
            Assert.Equal(CanonicalJson.Normalize(root.InputJson), CanonicalJson.Normalize(after.InputJson));
            Assert.Equal(root.InputHash, after.InputHash);
            Assert.Equal(root.Stage, after.Stage);
        }
    }

    [Fact]
    public async Task Rotating_bounded_scan_revisits_fresh_low_ids_without_starving_old_high_ids()
    {
        var expiry = await ExpiryAsync();
        var fresh = await DraftAsync(old: false);
        var old = await DraftAsync();
        Assert.Equal(0, await expiry.ExpireBatchAsync(1, Ct));
        Assert.Equal(1, await expiry.ExpireBatchAsync(1, Ct));
        _time.Advance(TimeSpan.FromHours(48));
        Assert.Equal(1, await expiry.ExpireBatchAsync(1, Ct));
        Assert.Equal(0, await expiry.ExpireBatchAsync(1, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.All(await context.CommentDrafts.ToListAsync(Ct), draft => Assert.Equal(CommentStatus.Expired, draft.Status));
        Assert.Equal("0", (await context.Settings.SingleAsync(row => row.Key == CommentExpiry.CursorKey, Ct)).Value);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
        Assert.True(fresh.Id < old.Id);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-cursor")]
    [InlineData("9223372036854775808")]
    public async Task Unsupported_scan_cursor_fails_closed(string value)
    {
        var expiry = await ExpiryAsync();
        await DraftAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new AppSetting { Key = CommentExpiry.CursorKey, Value = value, UpdatedAt = _time.GetUtcNow() });
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => expiry.ExpireBatchAsync(10, Ct));
        Assert.Equal(CommentStatus.Pending, (await context.CommentDrafts.SingleAsync(Ct)).Status);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_batch_and_snapshot_expiry_publish_one_source_revision_and_notice()
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync();
        var witness = CommentExpirySnapshot.Capture(draft);
        await Task.WhenAll(expiry.ExpireBatchAsync(10, Ct), expiry.ExpireAsync(witness, Ct), expiry.ExpireAsync(witness, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(4, (await context.CommentDrafts.SingleAsync(Ct)).Revision);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_and_cursor_roll_back_if_native_admission_or_cursor_save_fails(bool atCursor)
    {
        var fault = new ExpiryFault { AtCursor = atCursor };
        var expiry = await ExpiryAsync(fault);
        await DraftAsync();
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => expiry.ExpireBatchAsync(10, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(CommentStatus.Pending, (await context.CommentDrafts.SingleAsync(Ct)).Status);
        Assert.Empty(await context.Settings.Where(row => row.Key == CommentExpiry.CursorKey).ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync(context));
        fault.Active = false;
        Assert.Equal(1, await expiry.ExpireBatchAsync(10, Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Original_pending_card_keeps_its_immutable_native_admission_for_late_receipt_retirement()
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync();
        var notifier = _host!.Services.GetRequiredService<Notifier>();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        notifier.Add(context, NotificationKind.CommentDraft, "synthetic card", entityId: draft.Id, buttons: [["Approve|pick:1:0"]]);
        await notifier.SaveAndFlushAsync(context, Ct);
        var card = await context.Notifications.SingleAsync(Ct);
        var root = await context.NotificationDeliveryRequests.SingleAsync(Ct);
        Assert.True(await expiry.ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
        var after = await context.NotificationDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == root.Id, Ct);
        Assert.Equal(root.AdmissionHash, after.AdmissionHash);
        Assert.Equal(CanonicalJson.Normalize(root.AdmissionJson!), CanonicalJson.Normalize(after.AdmissionJson!));
        await context.Entry(card).ReloadAsync(Ct);
        Assert.Equal(NotificationStatus.Pending, card.Status);
        Assert.Equal("synthetic card", card.Text);
        Assert.Equal("Approve|pick:1:0", card.Buttons!.Single().Single());
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Existing_message_id_creates_parent_bound_retirement_instead_of_guessing_a_destination()
    {
        var expiry = await ExpiryAsync();
        var draft = await DraftAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var card = new Notification
        {
            Kind = NotificationKind.CommentDraft, Text = "legacy card", EntityId = draft.Id,
            Status = NotificationStatus.Sent, CreatedAt = _time.GetUtcNow().AddDays(-1), TelegramMessageId = 777,
        };
        context.Notifications.Add(card);
        await context.SaveChangesAsync(Ct);
        Assert.True(await expiry.ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
        var edit = await context.Notifications.SingleAsync(row => row.EditParentNotificationId == card.Id, Ct);
        Assert.Equal(777, edit.EditKeyboardMessageId);
        Assert.Contains("noop", edit.Buttons!.Single().Single());
        var editRoot = await context.NotificationDeliveryRequests.SingleAsync(row => row.Id == edit.DeliveryRequestId, Ct);
        Assert.Equal(NotificationDeliveryStage.Failed, editRoot.Stage);
        Assert.True(NotificationDeliveryInputs.Input(editRoot).Matches(edit));
        Assert.Null(NotificationDeliveryInputs.Input(editRoot).Target);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(NotificationStatus.Sent, (await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == card.Id, Ct)).Status);
    }

    [Fact]
    public async Task Housekeeping_preserves_native_evidence_and_uncertain_legacy_notices_and_cleans_only_unowned_noise()
    {
        var expiry = await ExpiryAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var old = _time.GetUtcNow().AddDays(-40);
        var posts = Enumerable.Range(0, 6).Select(index => new RawPost
        {
            Source = SourceKind.Telegram, ChatRef = "synthetic", ExternalId = "noise-" + index, Text = "noise", FetchedAt = old,
            Status = index == 5 ? RawPostStatus.New : RawPostStatus.Skipped,
        }).ToList();
        posts[1].ProcessingRequestId = Guid.NewGuid();
        posts[2].ProcessingRevision = 1;
        posts[4].VacancyId = 555;
        context.RawPosts.AddRange(posts);
        context.JobRuns.AddRange(new JobRun { Job = "old-ok", StartedAt = old, Status = JobRunStatus.Ok },
            new JobRun { Job = "old-running", StartedAt = old, Status = JobRunStatus.Running });
        foreach (var status in new[] { NotificationStatus.Pending, NotificationStatus.Sending, NotificationStatus.Sent, NotificationStatus.Failed })
            context.Notifications.Add(new Notification { Kind = NotificationKind.Info, Text = "legacy evidence", Status = status, CreatedAt = old, Attempts = 0 });
        await context.SaveChangesAsync(Ct);
        context.PostProcessingRequests.Add(new PostProcessingRequest { Id = Guid.NewGuid(), PostId = posts[3].Id, Stage = PostProcessingStage.Completed, CreatedAt = old });
        await context.SaveChangesAsync(Ct);
        _host!.Services.GetRequiredService<JobFinder.Pipeline.ServerOwnership>().Acquired();
        var job = new HousekeepingJob(expiry, new JobTriggers(), pg.Db, _host.Services.GetRequiredService<Notifier>(), _time, NullLogger<HousekeepingJob>.Instance,
            _host.Services.GetRequiredService<JobFinder.Pipeline.Maintenance.OperationalRetention>());
        var outcome = await RunAsync(job);
        Assert.Contains("1 noise posts deleted", outcome.Message);
        Assert.Equal(5, await context.RawPosts.CountAsync(Ct));
        Assert.False(await context.RawPosts.AnyAsync(row => row.Id == posts[0].Id, Ct));
        Assert.Single(await context.PostProcessingRequests.ToListAsync(Ct));
        Assert.Equal("old-running", (await context.JobRuns.SingleAsync(Ct)).Job);
        Assert.Equal(4, await context.Notifications.CountAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
    }

    private static Task<JobOutcome> RunAsync(HousekeepingJob job) => (Task<JobOutcome>)typeof(HousekeepingJob)
        .GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, [Ct])!;

    [Fact]
    public async Task Housekeeping_does_not_clean_evidence_when_expiry_admission_rolls_back()
    {
        var fault = new ExpiryFault();
        var expiry = await ExpiryAsync(fault);
        await DraftAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.RawPosts.Add(new RawPost
        {
            Source = SourceKind.Telegram, ChatRef = "synthetic", ExternalId = "rollback-noise", Text = "noise",
            FetchedAt = _time.GetUtcNow().AddDays(-40), Status = RawPostStatus.Skipped,
        });
        context.Notifications.Add(new Notification
        {
            Kind = NotificationKind.Info, Text = "legacy uncertain", Status = NotificationStatus.Pending,
            CreatedAt = _time.GetUtcNow().AddDays(-40), Attempts = 0,
        });
        await context.SaveChangesAsync(Ct);
        fault.Active = true;
        _host!.Services.GetRequiredService<JobFinder.Pipeline.ServerOwnership>().Acquired();
        var job = new HousekeepingJob(expiry, new JobTriggers(), pg.Db, _host.Services.GetRequiredService<Notifier>(), _time, NullLogger<HousekeepingJob>.Instance,
            _host.Services.GetRequiredService<JobFinder.Pipeline.Maintenance.OperationalRetention>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(job));
        Assert.Single(await context.RawPosts.ToListAsync(Ct));
        Assert.Equal(CommentStatus.Pending, (await context.CommentDrafts.SingleAsync(Ct)).Status);
        Assert.Equal("legacy uncertain", (await context.Notifications.SingleAsync(Ct)).Text);
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.Settings.Where(row => row.Key == CommentExpiry.CursorKey).ToListAsync(Ct));
    }

    [Fact]
    public async Task Producer_restart_resumes_the_saved_bounded_scan_instead_of_rescanning_a_fresh_first_row()
    {
        var expiry = await ExpiryAsync();
        await DraftAsync(old: false);
        var old = await DraftAsync();
        Assert.Equal(0, await expiry.ExpireBatchAsync(1, Ct));
        await _host!.StopAsync(Ct);
        _host.Dispose();
        _host = null;
        expiry = await ExpiryAsync();
        Assert.Equal(1, await expiry.ExpireBatchAsync(1, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(CommentStatus.Expired, (await context.CommentDrafts.SingleAsync(row => row.Id == old.Id, Ct)).Status);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    private static Task<int> CommandsAsync(JobFinderDbContext context) => context.Database.SqlQuery<int>($"""
        SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_notifications) +
          (SELECT count(*) FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.notifications.%'))::int AS "Value"
        """).SingleAsync(Ct);

    private sealed class ExpiryFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public bool AtCursor { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context is JobFinderDbContext context && (AtCursor
                ? context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.Entity.Key == CommentExpiry.CursorKey && entry.State is EntityState.Added or EntityState.Modified)
                : context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added)))
                throw new InvalidOperationException("synthetic expiry admission failure");
            return ValueTask.FromResult(result);
        }
    }
}
