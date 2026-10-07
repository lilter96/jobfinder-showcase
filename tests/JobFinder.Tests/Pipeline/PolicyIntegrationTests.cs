using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Data;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JobFinder.Tests.Pipeline;

public sealed partial class PolicyIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AutomationPolicyStore Store() => new(pg.Policies, new DecisionOptions());

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.CapturesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(document => document.Key.StartsWith("policy.")).ExecuteDeleteAsync(Ct);
        await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(setting => AutomationPolicyStore.LegacyKeys.Contains(setting.Key)).ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Notifications.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Effective_policy_preview_validates_legacy_overrides_without_publishing_or_consuming_them()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new AppSetting { Key = RuntimeSettings.AutoApplyThreshold, Value = "91", UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await Store().PreviewAsync(Ct);
        Assert.Empty(await context.Documents.Where(row => row.Key == AutomationPolicyStore.Key).ToListAsync(Ct));
        Assert.Empty(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Equal("91", (await context.Settings.SingleAsync(row => row.Key == RuntimeSettings.AutoApplyThreshold, Ct)).Value);
        var published = await Store().ReadAsync(Ct);
        Assert.Equal(91, published.Value.AutoApplyThreshold);
        Assert.Equal(1, published.Version);
    }

    [Fact]
    public async Task Invalid_effective_policy_preview_preserves_the_unimported_source_and_does_not_publish()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new AppSetting { Key = RuntimeSettings.DailyAutoApplyLimit, Value = "999", UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().PreviewAsync(Ct));
        Assert.Empty(await context.Documents.Where(row => row.Key == AutomationPolicyStore.Key).ToListAsync(Ct));
        Assert.Empty(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Equal("999", (await context.Settings.SingleAsync(row => row.Key == RuntimeSettings.DailyAutoApplyLimit, Ct)).Value);
    }

    [Fact]
    public async Task Existing_effective_settings_are_imported_once_with_history_and_without_state_files()
    {
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Settings.AddRange(
                new AppSetting { Key = RuntimeSettings.AutoApplyThreshold, Value = "91", UpdatedAt = DateTimeOffset.UtcNow },
                new AppSetting { Key = RuntimeSettings.AutoApplyPaused, Value = "true", UpdatedAt = DateTimeOffset.UtcNow },
                new AppSetting { Key = RuntimeSettings.ApplyDryRun, Value = "true", UpdatedAt = DateTimeOffset.UtcNow },
                new AppSetting { Key = RuntimeSettings.ApplyBlockedUntil, Value = DateTimeOffset.UtcNow.AddHours(1).ToString("O"), UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var store = Store();
        var first = await store.ReadAsync(Ct);
        Assert.Equal(91, first.Value.AutoApplyThreshold);
        Assert.True(first.Value.AutoApplyPaused);
        Assert.True(first.Value.ApplyDryRun);
        Assert.Equal(1, first.Version);
        Assert.Equal(first, await store.ReadAsync(Ct));
        await using var verify = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await verify.Settings.AnyAsync(setting => AutomationPolicyStore.LegacyKeys.Contains(setting.Key), Ct));
        Assert.True(await verify.Settings.AnyAsync(setting => setting.Key == RuntimeSettings.ApplyBlockedUntil, Ct));
        Assert.Single(await verify.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await verify.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_initializers_create_one_policy_revision()
    {
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Store().ReadAsync(Ct)));
        Assert.All(snapshots, snapshot => Assert.Equal(1, snapshot.Version));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_dashboard_snapshot_cannot_overwrite_a_pause_changed_by_the_bot()
    {
        var store = Store();
        var dashboard = await store.ReadAsync(Ct);
        var runtime = new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies);
        await runtime.SetAsync(RuntimeSettings.AutoApplyPaused, true, Ct);
        await Assert.ThrowsAsync<PolicyConflictException>(() => store.PublishAsync(dashboard.Value with { AutoApplyThreshold = 99 }, dashboard.Version, "dashboard", Ct));
        var current = await store.ReadAsync(Ct);
        Assert.True(current.Value.AutoApplyPaused);
        Assert.Equal(dashboard.Value.AutoApplyThreshold, current.Value.AutoApplyThreshold);
        Assert.Equal(2, current.Version);
    }

    [Fact]
    public async Task Publishing_all_form_fields_is_one_audited_atomic_operation()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        var changed = snapshot.Value with
        {
            AutoApplyThreshold = 95, ApprovalThreshold = 70, DailyAutoApplyLimit = 8,
            AutoApplyPaused = true, ApplyDryRun = true, CommentsPaused = true,
        };
        var published = await store.PublishAsync(changed, snapshot.Version, "dashboard", Ct);
        Assert.Equal(changed, (await store.ReadAsync(Ct)).Value);
        Assert.Equal(2, published.Version);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var revision = await context.PolicyRevisions.SingleAsync(revision => revision.Version == 2, Ct);
        Assert.Equal("dashboard", revision.Actor);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Invalid_policy_never_changes_the_document_history_or_outbox()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PublishAsync(snapshot.Value with { DailyAutoApplyLimit = 201 }, snapshot.Version, "test", Ct));
        Assert.Equal(snapshot, await store.ReadAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Independent_runtime_field_updates_preserve_each_other_across_process_caches()
    {
        var first = new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies);
        var second = new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies);
        await first.Automation.ReadAsync(Ct);
        await Task.WhenAll(first.SetAsync(RuntimeSettings.ApplyDryRun, true, Ct), second.SetAsync(RuntimeSettings.CommentsPaused, true, Ct));
        Assert.True(await first.GetBoolAsync(RuntimeSettings.ApplyDryRun, false, Ct));
        Assert.True(await first.GetBoolAsync(RuntimeSettings.CommentsPaused, false, Ct));
        Assert.True(await second.GetBoolAsync(RuntimeSettings.ApplyDryRun, false, Ct));
        Assert.Equal(3, (await first.Automation.ReadAsync(Ct)).Version);
    }

    [Fact]
    public async Task Mutating_a_read_snapshot_cannot_mutate_the_cached_policy()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.AutoApplyThreshold = 1;
        Assert.NotEqual(1, (await store.ReadAsync(Ct)).Value.AutoApplyThreshold);
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("history/outbox failure");
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Audit_or_outbox_failure_rolls_back_every_field_and_the_version()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(new FailSave());
        var broken = new PgPolicyStore(new Factory(options.Options), TimeProvider.System, await pg.NotificationsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.PublishAsync(AutomationPolicyStore.Key,
            snapshot.Value with { AutoApplyPaused = true, CommentsPaused = true, DailyAutoApplyLimit = 9 }, snapshot.Version,
            AutomationPolicyJsonContext.Default.AutomationPolicy, value => value.Validate(), "test", Ct));
        Assert.Equal(snapshot, await store.ReadAsync(Ct));
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_corrupt_new_version_is_not_presented_as_a_valid_editable_old_snapshot()
    {
        var store = Store();
        await store.ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(document => document.Key == AutomationPolicyStore.Key)
            .ExecuteUpdateAsync(update => update.SetProperty(document => document.Content, "{\"autoApplyThreshold\":\"invalid\"}")
                .SetProperty(document => document.Version, document => document.Version + 1), Ct);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.ReadAsync(Ct));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.ReadAsync(Ct));
    }
}
