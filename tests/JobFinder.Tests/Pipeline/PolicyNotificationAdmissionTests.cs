using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;

namespace JobFinder.Tests.Pipeline;

public sealed partial class PolicyIntegrationTests
{
    [Theory]
    [InlineData(AutomationPolicyStore.Key)]
    [InlineData(JobFinder.Pipeline.Settings.LlmPolicyStore.Key)]
    [InlineData(SearchPolicyStore.Key)]
    [InlineData(EngagementPolicyStore.Key)]
    public async Task Every_policy_facade_imports_and_edits_with_native_admission_without_a_configured_target(string key)
    {
        await ChangePolicyAsync(key, pg.Policies, edit: true);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, (await context.Documents.SingleAsync(row => row.Key == key, Ct)).Version);
        Assert.Equal(2, await context.PolicyRevisions.CountAsync(row => row.Key == key, Ct));
        var notices = await context.Notifications.ToListAsync(Ct);
        var roots = await context.NotificationDeliveryRequests.ToListAsync(Ct);
        Assert.Equal(2, notices.Count);
        Assert.Equal(2, roots.Count);
        foreach (var notice in notices)
        {
            var root = Assert.Single(roots, row => row.Id == notice.DeliveryRequestId && row.NotificationId == notice.Id);
            Assert.Equal(NotificationDeliveryStage.AwaitingTarget, root.Stage);
            Assert.NotNull(root.AdmissionJson);
            Assert.Equal(CanonicalJson.Hash(root.AdmissionJson), root.AdmissionHash);
            Assert.Null(NotificationDeliveryInputs.Input(root).Target);
        }
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(2, await NotificationCommandsAsync());
    }

    [Theory]
    [InlineData(AutomationPolicyStore.Key, false)]
    [InlineData(AutomationPolicyStore.Key, true)]
    [InlineData(JobFinder.Pipeline.Settings.LlmPolicyStore.Key, false)]
    [InlineData(JobFinder.Pipeline.Settings.LlmPolicyStore.Key, true)]
    [InlineData(SearchPolicyStore.Key, false)]
    [InlineData(SearchPolicyStore.Key, true)]
    [InlineData(EngagementPolicyStore.Key, false)]
    [InlineData(EngagementPolicyStore.Key, true)]
    public async Task Native_policy_admission_failure_rolls_back_saved_document_history_notice_and_legacy_consumption(string key, bool edit)
    {
        if (edit) await ChangePolicyAsync(key, pg.Policies, edit: false);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new AppSetting { Key = RuntimeSettings.AutoApplyThreshold, Value = "91", UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        var before = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == key, Ct);
        var commands = await NotificationCommandsAsync();
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        var fault = new PolicyAdmissionFault();
        options.AddInterceptors(fault);
        var broken = new PgPolicyStore(new Factory(options.Options), TimeProvider.System, await pg.NotificationsAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ChangePolicyAsync(key, broken, edit));
        Assert.Equal("synthetic native policy admission fault", error.Message);
        Assert.True(fault.SourceWasSaved);
        var after = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == key, Ct);
        Assert.Equal(before?.Version, after?.Version);
        Assert.Equal(before?.Content, after?.Content);
        Assert.Equal(edit ? 1 : 0, await context.PolicyRevisions.CountAsync(row => row.Key == key, Ct));
        Assert.Equal(edit ? 1 : 0, await context.Notifications.CountAsync(Ct));
        Assert.Equal(edit ? 1 : 0, await context.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(commands, await NotificationCommandsAsync());
        Assert.Equal("91", (await context.Settings.SingleAsync(row => row.Key == RuntimeSettings.AutoApplyThreshold, Ct)).Value);
    }

    [Fact]
    public async Task An_unstarted_policy_publisher_cannot_partially_import_the_source_or_consume_legacy_overrides()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new AppSetting { Key = RuntimeSettings.AutoApplyThreshold, Value = "91", UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        using var host = await PostProcessingTestHost.BuildAsync(pg, consumer: false);
        await Assert.ThrowsAsync<WolverineHasNotStartedException>(() => host.Services.GetRequiredService<RuntimeSettings>().Automation.ReadAsync(Ct));
        Assert.Empty(await context.Documents.Where(row => row.Key.StartsWith("policy.")).ToListAsync(Ct));
        Assert.Empty(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await NotificationCommandsAsync());
        Assert.Equal("91", (await context.Settings.SingleAsync(row => row.Key == RuntimeSettings.AutoApplyThreshold, Ct)).Value);
    }

    private static async Task ChangePolicyAsync(string key, PgPolicyStore policies, bool edit)
    {
        switch (key)
        {
            case AutomationPolicyStore.Key:
                var automation = new AutomationPolicyStore(policies, new DecisionOptions());
                var automatic = await automation.ReadAsync(Ct);
                if (edit) await automation.PublishAsync(automatic.Value with { AutoApplyPaused = true }, automatic.Version, "native-policy-test", Ct);
                break;
            case JobFinder.Pipeline.Settings.LlmPolicyStore.Key:
                var llm = new JobFinder.Pipeline.Settings.LlmPolicyStore(policies, Llm.LlmRuntimePolicyTests.Bootstrap());
                var route = await llm.ReadAsync(Ct);
                if (edit) await llm.PublishAsync(route.Value with { DailyBudgetUsd = route.Value.DailyBudgetUsd + 1 }, route.Version, "native-policy-test", Ct);
                break;
            case SearchPolicyStore.Key:
                var search = new SearchPolicyStore(policies, new FilterOptions(), new PrefilterOptions(), new HhOptions(), new HabrOptions(), new ThreadsOptions());
                var collection = await search.ReadAsync(Ct);
                if (edit) await search.PublishAsync(collection.Value, collection.Version, "native-policy-test", Ct);
                break;
            case EngagementPolicyStore.Key:
                var engagement = new EngagementPolicyStore(policies, new ThreadsOptions());
                var comments = await engagement.ReadAsync(Ct);
                if (edit) await engagement.PublishAsync(comments.Value with { AutoPublish = false }, comments.Version, "native-policy-test", Ct);
                break;
            default: throw new ArgumentException("unknown synthetic policy", nameof(key));
        }
    }

    private async Task<int> NotificationCommandsAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_notifications)
                  + (SELECT count(*) FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.notifications.%'))::int AS "Value"
            """).SingleAsync(Ct);
    }

    private sealed class PolicyAdmissionFault : SaveChangesInterceptor
    {
        public bool SourceWasSaved { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            if (context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                SourceWasSaved = context.ChangeTracker.Entries<PolicyRevision>().Any(entry => entry.State == EntityState.Unchanged);
                throw new InvalidOperationException("synthetic native policy admission fault");
            }
            return ValueTask.FromResult(result);
        }
    }
}
