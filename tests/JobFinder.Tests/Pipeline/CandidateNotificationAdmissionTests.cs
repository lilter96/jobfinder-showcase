using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JobFinder.Tests.Pipeline;

public sealed partial class CandidateIntegrationTests
{
    [Fact]
    public async Task Profile_facts_and_generated_facts_each_commit_one_native_admission_without_a_configured_target()
    {
        await Publisher.PublishProfileAsync(Profile(), 0, "native-candidate", Ct);
        Assert.True(await Publisher.EnsureFactsAsync(Ct));
        Assert.False(await Publisher.EnsureFactsAsync(Ct));
        await Publisher.PublishFactsAsync(new CandidateFacts(), 1, "native-facts", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notices = await context.Notifications.AsNoTracking().ToListAsync(Ct);
        var roots = await context.NotificationDeliveryRequests.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(3, notices.Count);
        Assert.Equal(3, roots.Count);
        Assert.Equal(3, await context.CandidateRevisions.CountAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        foreach (var notice in notices)
        {
            var root = Assert.Single(roots, row => row.Id == notice.DeliveryRequestId && row.NotificationId == notice.Id);
            Assert.Equal(NotificationDeliveryStage.AwaitingTarget, root.Stage);
            Assert.NotNull(root.AdmissionJson);
            Assert.Equal(CanonicalJson.Hash(root.AdmissionJson), root.AdmissionHash);
            var input = NotificationDeliveryInputs.Input(root);
            Assert.Null(input.Target);
            Assert.Equal(notice.Text, input.Text);
        }
    }

    [Fact]
    public async Task Native_admission_failure_after_the_profile_save_rolls_back_legacy_preimage_backup_and_all_outputs()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var original = ProfileProvider.ToJson(Profile("Legacy original"));
        context.Documents.Add(new() { Key = ProfileProvider.Key, Content = original, Version = 7, UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinder.Data.JobFinderDbContext>();
        JobFinder.Data.JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        var fault = new AdmissionFault();
        options.AddInterceptors(fault);
        var publisher = await pg.CandidateAsync(new Factory(options.Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishProfileAsync(Profile("Rejected"), 7,
            "resume-import", Ct, keepPrevious: true));
        Assert.True(fault.SourceWasSaved);
        var current = (await Profiles.GetSnapshotAsync(Ct))!;
        Assert.Equal(7, current.Version);
        Assert.Equal("Legacy original", current.Value.Name);
        Assert.Null(await Documents.GetAsync(ProfileProvider.PreviousKey, Ct));
        Assert.Empty(await context.CandidateRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await NativeCommandsAsync());
    }

    [Fact]
    public async Task Generated_facts_admission_failure_preserves_the_profile_and_a_retry_publishes_facts_once()
    {
        await Profiles.SaveAsync(Profile(), 0, "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinder.Data.JobFinderDbContext>();
        JobFinder.Data.JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        var fault = new AdmissionFault();
        options.AddInterceptors(fault);
        var publisher = await pg.CandidateAsync(new Factory(options.Options));
        var commands = await NativeCommandsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.EnsureFactsAsync(Ct));
        Assert.True(fault.SourceWasSaved);
        Assert.Null(await Documents.GetAsync(FactsProvider.Key, Ct));
        Assert.Single(await context.CandidateRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Single(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(commands, await NativeCommandsAsync());
        Assert.True(await Publisher.EnsureFactsAsync(Ct));
        Assert.False(await Publisher.EnsureFactsAsync(Ct));
        Assert.Equal(1, (await Documents.GetAsync(FactsProvider.Key, Ct))!.Version);
        Assert.Equal(2, await context.CandidateRevisions.CountAsync(Ct));
        Assert.Equal(2, await context.NotificationDeliveryRequests.CountAsync(Ct));
    }

    private async Task<int> NativeCommandsAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_notifications)
                  + (SELECT count(*) FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.notifications.%'))::int AS "Value"
            """).SingleAsync(Ct);
    }

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public bool SourceWasSaved { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            if (context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                SourceWasSaved = context.ChangeTracker.Entries<AppDocument>().Any(entry => entry.State == EntityState.Unchanged);
                throw new InvalidOperationException("synthetic native candidate admission fault");
            }
            return ValueTask.FromResult(result);
        }
    }
}
