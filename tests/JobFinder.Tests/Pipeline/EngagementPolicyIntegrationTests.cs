using System.Text.Json;
using JobFinder.Core.Policies;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JobFinder.Tests.Pipeline;

public sealed partial class EngagementPolicyIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private EngagementPolicyStore Store(ThreadsOptions? bootstrap = null) => new(pg.Policies, bootstrap ?? new());

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.CapturesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("policy.")).ExecuteDeleteAsync(Ct);
        await context.PolicyRevisions.Where(row => row.Key.StartsWith("policy.")).ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Notifications.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled", Ct);
        await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        await context.CommentPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.CommentSourceRequests.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_engagement; DELETE FROM jobfinder_queues.wolverine_queue_engagement_scheduled; DELETE FROM jobfinder_queues.wolverine_queue_preparation; DELETE FROM jobfinder_queues.wolverine_queue_preparation_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Concurrent_import_preserves_effective_values_once_and_bootstrap_is_not_a_second_owner()
    {
        var options = new ThreadsOptions { AutoPublish = false, MaxDraftsPerDay = 3, AuthorCooldownDays = 29 };
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Store(options).ReadAsync(Ct)));
        Assert.All(snapshots, snapshot =>
        {
            Assert.Equal(1, snapshot.Version);
            Assert.False(snapshot.Value.AutoPublish);
            Assert.Equal(3, snapshot.Value.MaxDraftsPerDay);
            Assert.Equal(29, snapshot.Value.AuthorCooldownDays);
        });
        Assert.Equal(snapshots[0].Value, (await Store().ReadAsync(Ct)).Value);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal("json", await context.Database.SqlQuery<string>($"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'app_documents' AND column_name = 'content'").SingleAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_publications_commit_one_revision_history_and_notice_without_mutating_captured_inputs()
    {
        var store = Store();
        var before = await store.ReadAsync(Ct);
        async Task<bool> PublishAsync(int limit)
        {
            try { await store.PublishAsync(before.Value with { MaxDraftsPerDay = limit }, before.Version, "synthetic-test", Ct); return true; }
            catch (PolicyConflictException) { return false; }
        }
        var results = await Task.WhenAll(PublishAsync(4), PublishAsync(7));
        Assert.Equal(1, results.Count(result => result));
        var current = await store.ReadAsync(Ct);
        Assert.Equal(2, current.Version);
        Assert.Equal(10, before.Value.MaxDraftsPerDay);
        Assert.Contains(current.Value.MaxDraftsPerDay, new[] { 4, 7 });
        current.Value.MaxDraftsPerDay = 99;
        Assert.NotEqual(99, (await store.ReadAsync(Ct)).Value.MaxDraftsPerDay);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.PolicyRevisions.CountAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task A_failed_publication_rolls_back_the_document_history_and_notice_together()
    {
        var before = await Store().ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(new FailPublication());
        var failing = new EngagementPolicyStore(new(new Factory(options.Options), TimeProvider.System, await pg.NotificationsAsync()), new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PublishAsync(before.Value with { AutoPublish = false }, before.Version, "synthetic-test", Ct));
        Assert.Equal(before, await Store().ReadAsync(Ct));
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(-1, 7)]
    [InlineData(101, 7)]
    [InlineData(10, -1)]
    [InlineData(10, 366)]
    public async Task Invalid_limits_are_rejected_before_any_publication(int drafts, int cooldown)
    {
        var store = Store();
        var before = await store.ReadAsync(Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PublishAsync(before.Value with { MaxDraftsPerDay = drafts, AuthorCooldownDays = cooldown }, before.Version, "synthetic-test", Ct));
        Assert.Equal(before, await store.ReadAsync(Ct));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"autoPublish":true,"maxDraftsPerDay":10}""")]
    [InlineData("""{"schemaVersion":1,"autoPublish":null,"maxDraftsPerDay":10,"authorCooldownDays":7}""")]
    public async Task Corrupt_or_incomplete_current_policies_never_return_cached_or_bootstrap_preferences(string json)
    {
        var store = Store();
        await store.ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(row => row.Key == EngagementPolicyStore.Key)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Content, json).SetProperty(row => row.Version, row => row.Version + 1), Ct);
        await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(Ct));
    }

    [Fact]
    public async Task A_deleted_previously_published_policy_is_not_reimported_from_bootstrap()
    {
        await Store().ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(row => row.Key == EngagementPolicyStore.Key).ExecuteDeleteAsync(Ct);
        await Assert.ThrowsAsync<JsonException>(() => Store().ReadAsync(Ct));
        Assert.Empty(await context.Documents.Where(row => row.Key == EngagementPolicyStore.Key).ToListAsync(Ct));
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
    }

    [Fact]
    public async Task Invalid_effective_bootstrap_is_not_published_as_a_bad_current_policy()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(new ThreadsOptions { MaxDraftsPerDay = -1 }).ReadAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Documents.Where(row => row.Key == EngagementPolicyStore.Key).ToListAsync(Ct));
        Assert.Empty(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("policy.automation")]
    [InlineData("policy.llm")]
    [InlineData("policy.search")]
    [InlineData("policy.engagement")]
    [InlineData("policy.future-feature")]
    public async Task Generic_document_writes_cannot_bypass_policy_validation_revision_history_or_publication(string key)
    {
        await Store().ReadAsync(Ct);
        var documents = new JobFinder.Pipeline.Storage.PgDocumentStore(pg.Db, TimeProvider.System);
        var before = await documents.GetAsync(key, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => documents.SaveAsync(key, "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => documents.AddAsync(key, "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => documents.DeleteAsync(key, Ct));
        Assert.Equal(before, await documents.GetAsync(key, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    private sealed class FailPublication : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken ct = default) => throw new InvalidOperationException("synthetic publication fault");
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }
}
