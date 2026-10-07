using System.Data.Common;
using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Maintenance;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed class OperationalRetentionTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private IHost? _host;
    private OperationalRetention Retention => _host!.Services.GetRequiredService<OperationalRetention>();

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.PostProcessingRequests.ExecuteDeleteAsync(Ct);
        await context.RawPosts.ExecuteDeleteAsync(Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("bot.edit.")).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        _host = await HostAsync();
    }

    private async Task<IHost> HostAsync(IInterceptor? fault = null)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, false, fault: fault, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);
        });
        await host.StartAsync(Ct);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private RawPost Post(RawPostStatus status = RawPostStatus.Skipped, int age = 40) => new()
    {
        Source = SourceKind.Telegram, ChatRef = "synthetic", ExternalId = Guid.NewGuid().ToString("N"), Text = "Synthetic retention post",
        FetchedAt = _time.GetUtcNow().AddDays(-age), Status = status,
    };
    private JobRun Run(JobRunStatus status = JobRunStatus.Ok, int age = 40, string job = "synthetic-diagnostic") => new()
    {
        Job = job, StartedAt = _time.GetUtcNow().AddDays(-age), Status = status,
    };

    [Fact]
    public async Task Bounded_sweep_filters_protected_rows_before_the_limit_and_leaves_live_runs_and_billing_history_intact()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var protectedPost = Post(age: 200);
        protectedPost.ProcessingRevision = 1;
        var damagedPost = Post(age: 200);
        damagedPost.Error = "unsupported legacy source retained";
        var missingMarker = Post(age: 200);
        var first = Post(age: 80);
        var second = Post(RawPostStatus.NotVacancy, 60);
        context.RawPosts.AddRange(protectedPost, damagedPost, missingMarker, first, second, Post(RawPostStatus.New, 100), Post(age: 30));
        var running = Run(JobRunStatus.Running, 200);
        var digest = Run(age: 200, job: JobNames.Digest);
        var announced = Run(age: 200);
        var stale = Run(age: 80);
        context.JobRuns.AddRange(running, digest, announced, stale, Run(age: 30), Run((JobRunStatus)99, 200));
        context.LlmCalls.AddRange(new LlmCall { Purpose = "application", Model = "synthetic", At = _time.GetUtcNow().AddDays(-400), CostUsd = .03m, Ok = false },
            new LlmCall { Purpose = "resume", Model = "synthetic", At = _time.GetUtcNow().AddDays(-400), OperationId = Guid.NewGuid(), CallId = Guid.NewGuid(),
                AttemptNumber = 1, RequestedModel = "synthetic-requested", Recipe = new ArtifactRecipe(), CostUsd = .04m, Ok = true });
        await context.SaveChangesAsync(Ct);
        context.PostProcessingRequests.Add(new() { Id = Guid.NewGuid(), PostId = missingMarker.Id, Stage = PostProcessingStage.Completed, CreatedAt = _time.GetUtcNow().AddDays(-200) });
        context.Notifications.Add(new() { Kind = NotificationKind.Info, Text = "retained source notice", EntityId = announced.Id, CreatedAt = _time.GetUtcNow().AddDays(-200) });
        await context.SaveChangesAsync(Ct);
        var swept = await Retention.SweepAsync(1, Ct);
        Assert.Equal(1, swept.Posts);
        Assert.Equal(1, swept.Runs);
        Assert.False(await context.RawPosts.AnyAsync(row => row.Id == first.Id, Ct));
        Assert.True(await context.RawPosts.AnyAsync(row => row.Id == second.Id, Ct));
        Assert.False(await context.JobRuns.AnyAsync(row => row.Id == stale.Id, Ct));
        Assert.Equal(JobRunStatus.Running, (await context.JobRuns.AsNoTracking().SingleAsync(row => row.Id == running.Id, Ct)).Status);
        Assert.True(await context.JobRuns.AnyAsync(row => row.Id == digest.Id, Ct));
        Assert.True(await context.JobRuns.AnyAsync(row => row.Id == announced.Id, Ct));
        Assert.Equal(.07m, await context.LlmCalls.SumAsync(row => row.CostUsd, Ct));
        Assert.Equal(2, await context.LlmCalls.CountAsync(Ct));
        var next = await Retention.SweepAsync(1, Ct);
        Assert.Equal(1, next.Posts);
        Assert.Equal(0, next.Runs);
        Assert.Equal(0, (await Retention.SweepAsync(1, Ct)).Total);
    }

    [Fact]
    public async Task Locked_source_admission_is_not_waited_on_or_deleted_and_other_noise_can_progress()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var held = Post(age: 200);
        var available = Post(age: 100);
        context.RawPosts.AddRange(held, available);
        await context.SaveChangesAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM raw_posts WHERE id = {held.Id} FOR UPDATE", Ct);
        var swept = await Retention.SweepAsync(1, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1, swept.Posts);
        held.ProcessingRequestId = Guid.NewGuid();
        held.ProcessingRevision = 1;
        context.PostProcessingRequests.Add(new() { Id = held.ProcessingRequestId.Value, PostId = held.Id, Revision = 1, Stage = PostProcessingStage.Completed,
            CreatedAt = _time.GetUtcNow() });
        await context.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        Assert.True(await context.RawPosts.AnyAsync(row => row.Id == held.Id, Ct));
        Assert.False(await context.RawPosts.AnyAsync(row => row.Id == available.Id, Ct));
        Assert.Equal(0, (await Retention.SweepAsync(1, Ct)).Total);
    }

    [Fact]
    public async Task Parallel_sweeps_have_disjoint_bounded_batches()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.RawPosts.AddRange(Enumerable.Range(0, 6).Select(_ => Post()));
        context.JobRuns.AddRange(Enumerable.Range(0, 6).Select(_ => Run()));
        await context.SaveChangesAsync(Ct);
        var results = await Task.WhenAll(Retention.SweepAsync(2, Ct), Retention.SweepAsync(2, Ct));
        Assert.All(results, result => { Assert.Equal(2, result.Posts); Assert.Equal(2, result.Runs); });
        Assert.Equal(2, await context.RawPosts.CountAsync(Ct));
        Assert.Equal(2, await context.JobRuns.CountAsync(Ct));
    }

    [Fact]
    public async Task Sweep_failure_or_ownership_loss_rolls_back_the_entire_batch()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.RawPosts.Add(Post());
        context.JobRuns.Add(Run());
        await context.SaveChangesAsync(Ct);
        using var broken = await HostAsync(new CommitFault());
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => broken.Services.GetRequiredService<OperationalRetention>().SweepAsync(1, Ct)); }
        finally { await broken.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct); }
        Assert.Single(await context.RawPosts.ToListAsync(Ct));
        Assert.Single(await context.JobRuns.ToListAsync(Ct));
        _host!.Services.GetRequiredService<ServerOwnership>().Lost();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Retention.SweepAsync(1, Ct));
        Assert.Single(await context.RawPosts.ToListAsync(Ct));
        Assert.Single(await context.JobRuns.ToListAsync(Ct));
    }

    [Fact]
    public async Task Completed_edit_tombstone_and_unresolved_typed_draft_survive_age_and_a_late_reply()
    {
        var edits = _host!.Services.GetRequiredService<BotEdits>();
        var snapshot = new LetterEditSnapshot { VacancyId = 42, Text = "Synthetic original", Status = VacancyStatus.Scored };
        var completed = await edits.BeginAsync(123456, 777, new() { Kind = "letter", SourceId = 42, Letter = snapshot }, Ct);
        completed = await edits.BindAsync(123456, 777, completed.Value.Id, 10, Ct);
        completed = Assert.IsType<CapturedBotEdit>(await edits.RecordReplyAsync(completed, 11, "Synthetic user reply", Ct));
        Assert.True(await edits.CompleteAsync(completed, Ct));
        var pending = await edits.BeginAsync(123456, 777, new() { Kind = "letter", SourceId = 42, Letter = snapshot }, Ct);
        pending = await edits.BindAsync(123456, 777, pending.Value.Id, 20, Ct);
        pending = Assert.IsType<CapturedBotEdit>(await edits.RecordReplyAsync(pending, 21, "Retained unresolved editor text", Ct));
        _time.Advance(TimeSpan.FromDays(400));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var before = await context.Documents.AsNoTracking().Where(row => row.Key.StartsWith("bot.edit.")).OrderBy(row => row.Key).Select(row => new { row.Key, row.Content, row.Version, row.UpdatedAt }).ToListAsync(Ct);
        Assert.Equal(0, (await Retention.SweepAsync(1, Ct)).Total);
        Assert.Equal(before, await context.Documents.AsNoTracking().Where(row => row.Key.StartsWith("bot.edit.")).OrderBy(row => row.Key).Select(row => new { row.Key, row.Content, row.Version, row.UpdatedAt }).ToListAsync(Ct));
        var late = Assert.IsType<CapturedBotEdit>(await edits.FindAsync(123456, 777, 10, null, 123456, Ct));
        Assert.True(late.Value.Completed);
        Assert.Null(await edits.RecordReplyAsync(late, 12, "Late duplicate", Ct));
        var unresolved = Assert.IsType<CapturedBotEdit>(await edits.FindAsync(123456, 777, 20, null, 123456, Ct));
        Assert.Equal(pending.Json, unresolved.Json);
        Assert.Equal("Retained unresolved editor text", unresolved.Value.Draft);
    }

    [Fact]
    public async Task Native_notice_attempts_keyboard_lineage_cursor_control_and_opaque_documents_are_retained_unchanged()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var old = _time.GetUtcNow().AddDays(-400);
        var parent = new Notification { Kind = NotificationKind.Digest, Text = "Synthetic native parent", Status = NotificationStatus.OutcomeUnknown, CreatedAt = old };
        context.Notifications.Add(parent);
        await context.SaveChangesAsync(Ct);
        var root = new NotificationDeliveryRequest { Id = Guid.NewGuid(), NotificationId = parent.Id, Stage = NotificationDeliveryStage.OutcomeUnknown,
            CreatedAt = old, InputJson = "{\"version\":99}", InputHash = "immutable-source-hash", AdmissionJson = "{\"version\":99}", AdmissionHash = "immutable-admission-hash" };
        parent.DeliveryRequestId = root.Id;
        context.NotificationDeliveryRequests.Add(root);
        context.NotificationDeliveryAttempts.Add(new() { Id = Guid.NewGuid(), RequestId = root.Id, Number = 1,
            Status = DeliveryAttemptStatus.OutcomeUnknown, CreatedAt = old, EffectJson = "{\"version\":99}" });
        context.Notifications.Add(new() { Kind = NotificationKind.Info, Text = "Synthetic keyboard edit", EditParentNotificationId = parent.Id, CreatedAt = old });
        context.BotControlRequests.Add(new() { Id = Guid.NewGuid(), BotId = 123456, UpdateId = 10, Stage = BotControlStage.OutcomeUnknown,
            CreatedAt = old, InputJson = "{\"version\":99}", InputHash = "retained-control-hash" });
        context.Settings.Add(new() { Key = "bot.poll.123456", Value = "{\"version\":99}", UpdatedAt = old });
        context.Documents.Add(new() { Key = "bot.edit.intent.unknown", Content = "{\"version\":99}", Version = 4, UpdatedAt = old });
        await context.SaveChangesAsync(Ct);
        var noticeBefore = JsonSerializer.Serialize(await context.Notifications.AsNoTracking().OrderBy(row => row.Id).ToListAsync(Ct));
        var rootBefore = JsonSerializer.Serialize(await context.NotificationDeliveryRequests.AsNoTracking().ToListAsync(Ct));
        var attemptBefore = JsonSerializer.Serialize(await context.NotificationDeliveryAttempts.AsNoTracking().ToListAsync(Ct));
        var controlBefore = JsonSerializer.Serialize(await context.BotControlRequests.AsNoTracking().ToListAsync(Ct));
        var cursorBefore = JsonSerializer.Serialize(await context.Settings.AsNoTracking().SingleAsync(row => row.Key == "bot.poll.123456", Ct));
        var documentBefore = JsonSerializer.Serialize(await context.Documents.AsNoTracking().SingleAsync(row => row.Key == "bot.edit.intent.unknown", Ct));
        Assert.Equal(0, (await Retention.SweepAsync(1, Ct)).Total);
        Assert.Equal(noticeBefore, JsonSerializer.Serialize(await context.Notifications.AsNoTracking().OrderBy(row => row.Id).ToListAsync(Ct)));
        Assert.Equal(rootBefore, JsonSerializer.Serialize(await context.NotificationDeliveryRequests.AsNoTracking().ToListAsync(Ct)));
        Assert.Equal(attemptBefore, JsonSerializer.Serialize(await context.NotificationDeliveryAttempts.AsNoTracking().ToListAsync(Ct)));
        Assert.Equal(controlBefore, JsonSerializer.Serialize(await context.BotControlRequests.AsNoTracking().ToListAsync(Ct)));
        Assert.Equal(cursorBefore, JsonSerializer.Serialize(await context.Settings.AsNoTracking().SingleAsync(row => row.Key == "bot.poll.123456", Ct)));
        Assert.Equal(documentBefore, JsonSerializer.Serialize(await context.Documents.AsNoTracking().SingleAsync(row => row.Key == "bot.edit.intent.unknown", Ct)));
    }

    private sealed class CommitFault : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default) => throw new InvalidOperationException("synthetic retention commit failure");
    }
}
