using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Storage;
using JobFinder.Data;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace JobFinder.Tests.Pipeline;

public sealed partial class CandidateIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CandidateDocuments Publisher { get; set; } = null!;
    private PgDocumentStore Documents => new(pg.Db, TimeProvider.System);
    private ProfileProvider Profiles => new(Documents, new PathsOptions(), NullLogger<ProfileProvider>.Instance, Publisher);
    private FactsProvider Facts => new(Documents, NullLogger<FactsProvider>.Instance, Publisher);
    private static CandidateProfile Profile(string name = "Synthetic Candidate") => new() { Name = name, Title = "Senior .NET Developer", Skills = [new() { Name = "C#" }] };

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        Publisher = await pg.CandidateAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.ExecuteDeleteAsync(Ct);
        await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Notifications.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Concurrent_first_profile_publications_have_one_winner_and_one_atomic_notice()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try { await Publisher.PublishProfileAsync(Profile($"Candidate {index}"), 0, "test", Ct); return true; }
            catch (CandidateConflictException) { return false; }
        }));
        Assert.Equal(1, results.Count(passed => passed));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, Assert.Single(await context.CandidateRevisions.ToListAsync(Ct)).Version);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(1, (await Profiles.GetSnapshotAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Stale_profile_edit_cannot_overwrite_or_confirm_a_newer_profile()
    {
        await Profiles.SaveAsync(Profile() with { Reviewed = false }, 0, "test", Ct);
        var old = (await Profiles.GetSnapshotAsync(Ct))!;
        await Profiles.SaveAsync(Profile("New candidate") with { Reviewed = false, Preferences = ["new preference"] }, old.Version, "new-editor", Ct);
        await Assert.ThrowsAsync<CandidateConflictException>(() => Profiles.SaveAsync(old.Value with { Reviewed = true }, old.Version, "old-editor", Ct));
        var current = (await Profiles.GetSnapshotAsync(Ct))!;
        Assert.Equal("New candidate", current.Value.Name);
        Assert.False(current.Value.Reviewed);
        Assert.Equal(["new preference"], current.Value.Preferences);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.CandidateRevisions.CountAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_independent_answers_rebase_without_losing_other_confirmed_facts()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Facts.UpsertAsync($"fact-{index}", $"Question {index}", $"Answer {index}", Ct)));
        var snapshot = await Facts.GetSnapshotAsync(Ct);
        Assert.Equal(12, snapshot.Version);
        for (var index = 0; index < 12; index++) Assert.True(snapshot.Value.IsConfirmed($"fact-{index}"));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(12, await context.CandidateRevisions.CountAsync(Ct));
        Assert.Equal(12, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Whole_facts_editor_conflicts_instead_of_overwriting_a_new_answer()
    {
        await Facts.UpsertAsync("salary", "Salary", "4000 USD", Ct);
        var old = await Facts.GetSnapshotAsync(Ct);
        old.Value.Get("salary")!.Answer = "stale answer";
        await Facts.UpsertAsync("notice", "Notice", "two weeks", Ct);
        await Assert.ThrowsAsync<CandidateConflictException>(() => Facts.SaveAsync(old.Value, old.Version, "old-form", Ct));
        var current = await Facts.GetSnapshotAsync(Ct);
        Assert.Equal("4000 USD", current.Value.Get("salary")!.Answer);
        Assert.Equal("two weeks", current.Value.Get("notice")!.Answer);
    }

    [Fact]
    public async Task Concurrent_updates_to_the_same_fact_conflict_instead_of_rebasing_a_stale_answer()
    {
        await Facts.UpsertAsync("salary", "Salary", "Original", Ct);
        var barrier = new CompetingAnswers(Publisher);
        var facts = new FactsProvider(Documents, NullLogger<FactsProvider>.Instance, barrier);
        var results = await Task.WhenAll(new[] { "First", "Second" }.Select(async answer =>
        {
            try { await facts.UpsertAsync("salary", "Salary", answer, Ct); return true; }
            catch (CandidateConflictException) { return false; }
        }));
        Assert.Equal(1, results.Count(passed => passed));
        Assert.Equal(2, barrier.Calls);
        var current = await Facts.GetSnapshotAsync(Ct);
        Assert.Equal(2, current.Version);
        Assert.Contains(current.Value.Get("salary")!.Answer, new[] { "First", "Second" });
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.CandidateRevisions.CountAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_drafting_uses_current_profile_once_and_never_confirms_answers()
    {
        await Profiles.SaveAsync(Profile() with { Location = "Synthetic city" }, 0, "test", Ct);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Publisher.EnsureFactsAsync(Ct)));
        var snapshot = await Facts.GetSnapshotAsync(Ct);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal("Synthetic city", snapshot.Value.Get("location")!.Answer);
        Assert.All(snapshot.Value.Facts, fact => Assert.False(fact.Confirmed));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
        Assert.Equal("profile-draft", Assert.Single(await context.CandidateRevisions.Where(row => row.Key == FactsProvider.Key).ToListAsync(Ct)).Actor);
    }

    [Fact]
    public async Task A_confirmed_facts_publication_wins_over_automatic_drafting()
    {
        await Profiles.SaveAsync(Profile(), 0, "test", Ct);
        await Facts.SaveAsync(new CandidateFacts { Facts = [new() { Key = "salary", Question = "Salary", Answer = "confirmed", Confirmed = true }] }, 0, "editor", Ct);
        Assert.False(await Publisher.EnsureFactsAsync(Ct));
        Assert.Equal("confirmed", (await Facts.GetSnapshotAsync(Ct)).Value.Get("salary")!.Answer);
        Assert.Equal(1, (await Facts.GetSnapshotAsync(Ct)).Version);
    }

    [Fact]
    public async Task Reimport_keeps_the_exact_previous_profile_in_the_same_publication()
    {
        var profiles = Profiles;
        await profiles.SaveAsync(Profile("Previous"), 0, "test", Ct);
        var previous = (await Documents.GetAsync(ProfileProvider.Key, Ct))!;
        await profiles.SaveAsync(Profile("Imported") with { Reviewed = false }, 1, "resume-import", Ct, keepPrevious: true);
        Assert.Equal(previous.Json, (await Documents.GetAsync(ProfileProvider.PreviousKey, Ct))!.Json);
        Assert.False((await profiles.GetSnapshotAsync(Ct))!.Value.Reviewed);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var history = await context.CandidateRevisions.OrderBy(row => row.Version).ToListAsync(Ct);
        Assert.Equal([1L, 2L], history.Select(row => row.Version));
        Assert.Equal("Previous", ProfileProvider.FromJson(history[0].Content).Name);
        Assert.Equal("resume-import", history[1].Actor);
    }

    [Fact]
    public async Task A_profile_change_during_import_preserves_current_profile_history_and_backup()
    {
        await Profiles.SaveAsync(Profile("Original"), 0, "test", Ct);
        var captured = (await Profiles.GetSnapshotAsync(Ct))!;
        await Profiles.SaveAsync(Profile("User edit"), captured.Version, "dashboard", Ct);
        await Assert.ThrowsAsync<CandidateConflictException>(() => Profiles.SaveAsync(Profile("Slow import"), captured.Version, "resume-import", Ct, keepPrevious: true));
        Assert.Equal("User edit", (await Profiles.GetSnapshotAsync(Ct))!.Value.Name);
        Assert.Null(await Documents.GetAsync(ProfileProvider.PreviousKey, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.CandidateRevisions.CountAsync(Ct));
    }

    [Fact]
    public async Task Failed_save_rolls_back_profile_preimage_backup_history_and_notice()
    {
        await Profiles.SaveAsync(Profile("Original"), 0, "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(new FailSave());
        var faulty = await pg.CandidateAsync(new Factory(options.Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => faulty.PublishProfileAsync(Profile("Failed"), 1, "resume-import", Ct, keepPrevious: true));
        Assert.Equal("Original", (await Profiles.GetSnapshotAsync(Ct))!.Value.Name);
        Assert.Null(await Documents.GetAsync(ProfileProvider.PreviousKey, Ct));
        Assert.Single(await context.CandidateRevisions.AsNoTracking().ToListAsync(Ct));
        Assert.Single(await context.Notifications.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task First_edit_of_a_legacy_document_preserves_its_real_version_and_text()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var legacy = ProfileProvider.ToJson(Profile("Legacy"));
        context.Documents.Add(new AppDocument { Key = ProfileProvider.Key, Content = legacy, Version = 7, UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await Profiles.SaveAsync(Profile("Updated"), 7, "dashboard", Ct);
        var revisions = await context.CandidateRevisions.OrderBy(row => row.Version).ToListAsync(Ct);
        Assert.Equal([7L, 8L], revisions.Select(row => row.Version));
        Assert.Equal(legacy, revisions[0].Content);
        Assert.Equal("legacy-import", revisions[0].Actor);
        Assert.Equal("dashboard", revisions[1].Actor);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal("json", await context.Database.SqlQuery<string>($"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'app_documents' AND column_name = 'content'").SingleAsync(Ct));
    }

    [Fact]
    public async Task A_legacy_writer_race_is_a_conflict_and_rolls_back_publication_and_backup()
    {
        await Profiles.SaveAsync(Profile("Original"), 0, "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(new LegacyWriter(pg.Db));
        var publisher = await pg.CandidateAsync(new Factory(options.Options));
        await Assert.ThrowsAsync<CandidateConflictException>(() => publisher.PublishProfileAsync(Profile("Overwritten"), 1, "new-editor", Ct, keepPrevious: true));
        Assert.Equal("Legacy writer", (await Profiles.GetSnapshotAsync(Ct))!.Value.Name);
        Assert.Equal(2, (await Profiles.GetSnapshotAsync(Ct))!.Version);
        Assert.Null(await Documents.GetAsync(ProfileProvider.PreviousKey, Ct));
        Assert.Single(await context.CandidateRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(ProfileProvider.Key)]
    [InlineData(FactsProvider.Key)]
    public async Task Generic_document_writes_cannot_bypass_candidate_ownership(string key)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Documents.SaveAsync(key, "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Documents.AddAsync(key, "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Documents.DeleteAsync(key, Ct));
        Assert.Null(await Documents.GetAsync(key, Ct));
    }

    [Theory]
    [InlineData(ProfileProvider.Key)]
    [InlineData(FactsProvider.Key)]
    public async Task Deleted_published_documents_cannot_silently_restart_their_history(string key)
    {
        if (key == ProfileProvider.Key) await Profiles.SaveAsync(Profile(), 0, "test", Ct);
        else await Facts.SaveAsync(new CandidateFacts(), 0, "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(document => document.Key == key).ExecuteDeleteAsync(Ct);
        if (key == ProfileProvider.Key) await Assert.ThrowsAsync<JsonException>(() => Profiles.SaveAsync(Profile(), 0, "re-import", Ct));
        else
        {
            await Assert.ThrowsAsync<JsonException>(() => Facts.SaveAsync(new CandidateFacts(), 0, "re-import", Ct));
            await Assert.ThrowsAsync<JsonException>(() => Publisher.EnsureFactsAsync(Ct));
        }
        Assert.Single(await context.CandidateRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Published_snapshots_are_detached_from_the_callers_and_other_reads()
    {
        var profile = Profile();
        var saved = await Profiles.SaveAsync(profile, 0, "test", Ct);
        profile.Skills[0].Name = "caller mutation";
        saved.Value.Skills[0].Name = "returned snapshot mutation";
        Assert.Equal("C#", (await Profiles.GetSnapshotAsync(Ct))!.Value.Skills[0].Name);
        var facts = new CandidateFacts { Facts = [new() { Key = "salary", Question = "Salary", Answer = "Original", Confirmed = true }] };
        var result = await Facts.SaveAsync(facts, 0, "test", Ct);
        facts.Facts[0].Answer = "caller mutation";
        result.Value.Facts[0].Answer = "returned mutation";
        Assert.Equal("Original", (await Facts.GetSnapshotAsync(Ct)).Value.Get("salary")!.Answer);
    }

    [Fact]
    public async Task Invalid_facts_are_rejected_without_history_or_outbox_changes()
    {
        foreach (var facts in new[]
        {
            new CandidateFacts { Facts = [new() { Key = "salary", Question = "Salary", Confirmed = true }] },
            new CandidateFacts { Facts = [new() { Key = "duplicate", Question = "First" }, new() { Key = "duplicate", Question = "Second" }] },
            new CandidateFacts { Facts = [new() { Question = "No key" }] },
        }) await Assert.ThrowsAsync<ArgumentException>(() => Facts.SaveAsync(facts, 0, "test", Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Documents.ToListAsync(Ct));
        Assert.Empty(await context.CandidateRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Populated_candidate_history_refuses_destructive_migration_rollback()
    {
        await Profiles.SaveAsync(Profile(), 0, "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930101805_DurableResumePreparation", Ct));
            Assert.Equal("P0001", error.SqlState);
            Assert.Single(await context.CandidateRevisions.ToListAsync(Ct));
            Assert.NotNull(await Profiles.GetSnapshotAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(Ct); }
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
            throw new InvalidOperationException("synthetic candidate commit fault");
    }

    private sealed class LegacyWriter(IDbContextFactory<JobFinderDbContext> db) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var json = ProfileProvider.ToJson(Profile("Legacy writer"));
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE app_documents SET content = {json}::json, version = version + 1 WHERE key = {ProfileProvider.Key}", ct);
            return result;
        }
    }

    private sealed class CompetingAnswers(ICandidatePublisher inner) : ICandidatePublisher
    {
        private int _calls;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => _calls;

        public Task<VersionedDocument<CandidateProfile>> PublishProfileAsync(CandidateProfile profile, long expectedVersion, string actor,
            CancellationToken ct, bool keepPrevious = false) => inner.PublishProfileAsync(profile, expectedVersion, actor, ct, keepPrevious);

        public Task<bool> EnsureFactsAsync(CancellationToken ct) => inner.EnsureFactsAsync(ct);

        public async Task<VersionedDocument<CandidateFacts>> PublishFactsAsync(CandidateFacts facts, long expectedVersion, string actor, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return await inner.PublishFactsAsync(facts, expectedVersion, actor, ct);
        }
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }
}
