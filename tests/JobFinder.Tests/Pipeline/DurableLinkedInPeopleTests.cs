using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.LinkedIn;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class DurableLinkedInPeopleTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.LinkedInPeopleRequests.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith(LinkedInSearchQuota.Prefix)).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_hiring_replies, jobfinder_queues.wolverine_queue_hiring_replies_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Dispose(); }
        }
    }

    private async Task<IHost> HostAsync(bool consumer, Steps steps, SaveChangesInterceptor? fault = null)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: services =>
        {
            services.RemoveAll<ILinkedInPeopleSteps>();
            services.AddSingleton<ILinkedInPeopleSteps>(steps);
        });
        _hosts.Add(host);
        await host.StartAsync(Ct);
        if (!consumer) host.Services.GetRequiredService<ServerOwnership>().Acquired();
        return host;
    }

    private static LinkedInSearchPage Page() => new([new("https://www.linkedin.com/in/synthetic-person", "Anna Synthetic | Recruiter at Example | Minsk")], []);

    private async Task<LinkedInPeopleRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.LinkedInPeopleRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    [Fact]
    public async Task Competing_commands_cannot_start_a_second_paid_reading_while_the_first_is_claimed()
    {
        var steps = new Steps { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(false, steps);
        var id = await host.Services.GetRequiredService<LinkedInPeopleRequests>().RequestAsync("Example", Page(), Ct);
        var workflow = host.Services.GetRequiredService<LinkedInPeopleWorkflow>();
        var first = workflow.RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        try
        {
            await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await workflow.RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
            Assert.Equal(1, steps.Calls);
            Assert.Equal(LinkedInPeopleStage.Reading, (await RootAsync(id)).Stage);
        }
        finally { steps.Release.TrySetResult(); }
        await first;
        Assert.Equal(LinkedInPeopleStage.Completed, (await RootAsync(id)).Stage);
    }

    [Theory]
    [InlineData("incoming")]
    [InlineData("outgoing")]
    [InlineData("dead-letter")]
    public async Task Downgrade_refuses_orphaned_future_protocol_envelopes(string store)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var id = Guid.NewGuid();
        var messageType = "jobfinder.hiring.read-linkedin-people.v99";
        if (store == "incoming")
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_incoming_envelopes (id,status,owner_id,body,message_type) VALUES ({id},'Scheduled',0,{Array.Empty<byte>()},{messageType})", Ct);
        else if (store == "outgoing")
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_outgoing_envelopes (id,owner_id,destination,body,message_type) VALUES ({id},0,'postgresql://hiring_replies/',{Array.Empty<byte>()},{messageType})", Ct);
        else
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_dead_letters (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{messageType})", Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003133115_DurableReplyProcessing", Ct));
        Assert.Contains("Cannot roll back LinkedIn people preparation", error.MessageText);
        Assert.Contains("20261003152702_DurableLinkedInPeople", await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Admission_failure_does_not_leave_a_root_or_native_command()
    {
        var host = await HostAsync(false, new Steps(), new AdmissionFault());
        await Assert.ThrowsAsync<IOException>(() => host.Services.GetRequiredService<LinkedInPeopleRequests>().RequestAsync("Example", Page(), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.LinkedInPeopleRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_replies").SingleAsync(Ct));
    }

    [Fact]
    public async Task Reuse_returns_pending_or_recent_compatible_preparation_without_another_search_budget()
    {
        var host = await HostAsync(false, new Steps());
        var requests = host.Services.GetRequiredService<LinkedInPeopleRequests>();
        var id = await requests.RequestAsync("Example", Page(), Ct);
        Assert.Equal(id, await requests.ReusableAsync("Example", Ct));
        Assert.Null(await requests.ReusableAsync("Another company", Ct));
        await host.Services.GetRequiredService<LinkedInPeopleWorkflow>().RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(id, await requests.ReusableAsync("Example", Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.LinkedInPeopleRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)), Ct);
        Assert.Null(await requests.ReusableAsync("Example", Ct));
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith(LinkedInSearchQuota.Prefix)).ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_admission_is_once_and_page_is_captured_before_the_producer_returns()
    {
        var host = await HostAsync(false, new Steps());
        var requests = host.Services.GetRequiredService<LinkedInPeopleRequests>();
        var page = Page();
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => requests.RequestAsync("Example", page, Ct)));
        Assert.Single(ids.Distinct());
        page.Cards.Clear();
        var root = await RootAsync(ids[0]);
        Assert.Equal(LinkedInPeopleStage.Reading, root.Stage);
        Assert.Single(LinkedInPeopleRequests.ReadInput(root).Cards);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.LinkedInPeopleRequests.ToListAsync(Ct));
        var commands = await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_replies").SingleAsync(Ct);
        Assert.Equal(1, commands);
    }

    [Fact]
    public async Task Producer_before_consumer_restart_and_warm_reuse_do_not_repeat_paid_reading()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var requests = producer.Services.GetRequiredService<LinkedInPeopleRequests>();
        var id = await requests.RequestAsync("Example", Page(), Ct);
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(true, steps);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var saved = await consumer.Services.GetRequiredService<LinkedInPeopleRequests>().WaitAsync(id, timeout.Token);
        Assert.Equal("Example", saved.Input.Company);
        Assert.Single(LinkedInPeopleReader.Accept(saved.Input.Company, saved.Input.Page().Cards, saved.Result.Reading!));
        Assert.Equal(id, await requests.RequestAsync("Example", Page(), Ct));
        var root = await RootAsync(id);
        await consumer.Services.GetRequiredService<LinkedInPeopleWorkflow>().RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(CanonicalJson.Hash(root.ResultJson!), root.ResultHash);
    }

    [Fact]
    public async Task Outage_preserves_page_and_resumes_only_reading_after_workflow_recreation()
    {
        var steps = new Steps { Unavailable = true };
        var host = await HostAsync(false, steps);
        var requests = host.Services.GetRequiredService<LinkedInPeopleRequests>();
        var id = await requests.RequestAsync("Example", Page(), Ct);
        var workflow = host.Services.GetRequiredService<LinkedInPeopleWorkflow>();
        await workflow.RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        var paused = await RootAsync(id);
        Assert.Equal(LinkedInPeopleStage.Reading, paused.Stage);
        Assert.NotNull(paused.RetryAt);
        Assert.Null(paused.ClaimEnvelopeId);
        Assert.Null(paused.ResultJson);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.LinkedInPeopleRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
            Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith(LinkedInSearchQuota.Prefix)).ToListAsync(Ct));
            Assert.True(await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Scheduled'").SingleAsync(Ct) > 0);
        }
        steps.Unavailable = false;
        var second = await HostAsync(false, steps);
        await second.Services.GetRequiredService<LinkedInPeopleWorkflow>().RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(paused.InputHash, (await RootAsync(id)).InputHash);
        Assert.Equal(LinkedInPeopleStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(2, steps.Calls);
    }

    [Fact]
    public async Task Cancelling_a_wait_does_not_cancel_the_durable_request()
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        var requests = host.Services.GetRequiredService<LinkedInPeopleRequests>();
        var id = await requests.RequestAsync("Example", Page(), Ct);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.WaitAsync(id, cancelled.Token));
        Assert.Equal(LinkedInPeopleStage.Reading, (await RootAsync(id)).Stage);
        await host.Services.GetRequiredService<LinkedInPeopleWorkflow>().RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.NotNull((await requests.WaitAsync(id, Ct)).Result.Reading);
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Result_checkpoint_failure_retries_saved_paid_result_without_another_call()
    {
        var steps = new Steps();
        var fault = new ResultFault();
        var host = await HostAsync(false, steps, fault);
        var id = await host.Services.GetRequiredService<LinkedInPeopleRequests>().RequestAsync("Example", Page(), Ct);
        await host.Services.GetRequiredService<LinkedInPeopleWorkflow>().RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(1, fault.Failures);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(LinkedInPeopleStage.Completed, (await RootAsync(id)).Stage);
    }

    [Fact]
    public async Task Changed_saved_input_is_quarantined_before_paid_work_and_notice_joins_native_admission()
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        var id = await host.Services.GetRequiredService<LinkedInPeopleRequests>().RequestAsync("Example", Page(), Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.LinkedInPeopleRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.InputJson, "{}"), Ct);
        var workflow = host.Services.GetRequiredService<LinkedInPeopleWorkflow>();
        await Assert.ThrowsAsync<UnsupportedLinkedInPeopleException>(() => workflow.RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct));
        Assert.Equal(0, steps.Calls);
        Assert.Equal(LinkedInPeopleStage.Failed, (await RootAsync(id)).Stage);
        await workflow.RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.JoinedAsync(final, await final.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Damaged_completed_result_is_refused_instead_of_returned_from_warm_cache()
    {
        var host = await HostAsync(false, new Steps());
        var requests = host.Services.GetRequiredService<LinkedInPeopleRequests>();
        var id = await requests.RequestAsync("Example", Page(), Ct);
        await host.Services.GetRequiredService<LinkedInPeopleWorkflow>().RunAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.LinkedInPeopleRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ResultJson, "{}"), Ct);
        await Assert.ThrowsAsync<UnsupportedLinkedInPeopleException>(() => requests.WaitAsync(id, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_refuses_roots_and_orphaned_native_commands(bool orphaned)
    {
        var host = await HostAsync(false, new Steps());
        await host.Services.GetRequiredService<LinkedInPeopleRequests>().RequestAsync("Example", Page(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (orphaned) await context.LinkedInPeopleRequests.ExecuteDeleteAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003133115_DurableReplyProcessing", Ct));
        Assert.Contains("Cannot roll back LinkedIn people preparation", error.MessageText);
        Assert.Contains("20261003152702_DurableLinkedInPeople", await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Quota_is_atomic_persistent_and_rotates_at_Minsk_midnight_not_UTC_midnight()
    {
        var time = new Clock { Now = new DateTimeOffset(2026, 10, 3, 20, 59, 0, TimeSpan.Zero) };
        var options = new LinkedInOptions { Enabled = true, MaxSearchesPerDay = 3 };
        var owner = new LinkedInSearchQuota(pg.Db, new LocalClock(time, "Europe/Minsk"), options);
        var admitted = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => owner.TakeAsync(Ct)));
        Assert.Equal(3, admitted.Count(value => value));
        var recreated = new LinkedInSearchQuota(pg.Db, new LocalClock(time, "Europe/Minsk"), options);
        Assert.False(await recreated.TakeAsync(Ct));
        time.Now = time.Now.AddMinutes(2);
        Assert.True(await recreated.TakeAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal("3", (await context.Settings.SingleAsync(row => row.Key == LinkedInSearchQuota.Prefix + "2026-10-03", Ct)).Value);
        Assert.Equal("1", (await context.Settings.SingleAsync(row => row.Key == LinkedInSearchQuota.Prefix + "2026-10-04", Ct)).Value);
    }

    [Theory]
    [InlineData("not-a-count")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public async Task Corrupt_quota_fails_closed_and_generic_settings_cannot_reset_it(string count)
    {
        var host = await HostAsync(false, new Steps());
        var time = new Clock { Now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero) };
        var key = LinkedInSearchQuota.Prefix + "2026-10-03";
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = key, Value = count });
        await context.SaveChangesAsync(Ct);
        var owner = new LinkedInSearchQuota(pg.Db, new LocalClock(time, "Europe/Minsk"), new LinkedInOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.TakeAsync(Ct));
        var settings = host.Services.GetRequiredService<RuntimeSettings>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(key, "0", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(key, Ct));
        Assert.Equal(count, (await context.Settings.AsNoTracking().SingleAsync(row => row.Key == key, Ct)).Value);
    }

    private sealed class Steps : ILinkedInPeopleSteps
    {
        public int Calls;
        public bool Unavailable { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; set; }
        public void EnsureCompatible(LinkedInPeopleInput input) { }
        public async Task<LinkedInPeopleResult> ReadAsync(LinkedInPeopleInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Unavailable) throw new LlmUnavailableException("synthetic LinkedIn outage");
            Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(ct);
            return new LinkedInPeopleResult { Reading = new() { People = [new() { Index = 1, IsResult = true,
                WorksAtTarget = true, Name = "Anna Synthetic", CurrentCompany = "Example", CurrentRole = "Recruiter", IsRecruiter = true }] } };
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ResultFault : SaveChangesInterceptor
    {
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Failures == 0 && eventData.Context!.ChangeTracker.Entries<LinkedInPeopleRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == LinkedInPeopleStage.Completed))
            {
                Failures++;
                throw new IOException("synthetic result checkpoint failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LinkedInPeopleRequest>().Any(entry => entry.State == EntityState.Added))
                throw new IOException("synthetic admission failure");
            return ValueTask.FromResult(result);
        }
    }
}
