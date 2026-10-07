using System.Net;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Core.Policies;
using JobFinder.Data;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using RichardSzalay.MockHttp;

namespace JobFinder.Tests.Pipeline;

public sealed class SearchPolicyIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SearchPolicyStore Store(FilterOptions? filter = null) => new(pg.Policies, filter ?? new FilterOptions { RequiredMarkers = [".NET"] },
        new PrefilterOptions { TechMarkers = [".NET"], VacancyMarkers = ["ваканс"] },
        new HhOptions { Searches = [new HhSearch { Text = ".NET", Name = "effective" }] },
        new HabrOptions { Queries = [".NET"], Filters = [""] }, new ThreadsOptions());

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.CapturesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(document => document.Key.StartsWith("policy.")).ExecuteDeleteAsync(Ct);
        await context.PolicyRevisions.Where(revision => revision.Key.StartsWith("policy.")).ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Notifications.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Concurrent_import_is_once_only_and_bootstrap_is_not_a_second_owner()
    {
        var values = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Store().ReadAsync(Ct)));
        Assert.All(values, snapshot => Assert.Equal(1, snapshot.Version));
        Assert.Equal([".NET"], (await Store(new FilterOptions { RequiredMarkers = ["new-bootstrap"] }).ReadAsync(Ct)).Value.Filter.RequiredMarkers);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal("json", await context.Database.SqlQuery<string>($"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'app_documents' AND column_name = 'content'").SingleAsync(Ct));
    }

    [Fact]
    public async Task Publication_is_atomic_and_other_processes_observe_one_winner()
    {
        var store = Store();
        var previous = await store.ReadAsync(Ct);
        var reader = Store();
        await reader.ReadAsync(Ct);
        async Task<bool> Publish(string marker)
        {
            var value = SearchPolicyCompiler.Clone(previous.Value);
            value.Filter.RequiredMarkers = [marker];
            try { await store.PublishAsync(value, previous.Version, "test", Ct); return true; }
            catch (PolicyConflictException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Publish("C#"), Publish("dotnet")), success => success);
        Assert.Equal(2, (await reader.ReadAsync(Ct)).Version);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.PolicyRevisions.CountAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task An_operation_snapshot_and_cached_policy_are_detached_from_caller_changes()
    {
        var store = Store();
        var operation = await store.ReadAsync(Ct);
        var edited = await store.ReadAsync(Ct);
        edited.Value.Filter.RequiredMarkers = ["Java"];
        await store.PublishAsync(edited.Value, edited.Version, "test", Ct);
        Assert.Equal([".NET"], operation.Value.Filter.RequiredMarkers);
        Assert.Equal(["Java"], (await store.ReadAsync(Ct)).Value.Filter.RequiredMarkers);
        operation.Value.HeadHunter.Searches.Clear();
        Assert.Single((await store.ReadAsync(Ct)).Value.HeadHunter.Searches);
    }

    [Fact]
    public async Task Invalid_publication_has_no_history_or_outbox_side_effects()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.HeadHunter.IntervalMinutes = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => store.PublishAsync(snapshot.Value, 1, "test", Ct));
        Assert.Equal(45, (await store.ReadAsync(Ct)).Value.HeadHunter.IntervalMinutes);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Publication_rolls_back_compare_and_swap_when_history_outbox_save_fails()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        await using var existing = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, existing.Database.GetConnectionString()!);
        options.AddInterceptors(new FailSave());
        var failing = new SearchPolicyStore(new(new Factory(options.Options), TimeProvider.System, await pg.NotificationsAsync()), new(), new(), new(), new(), new());
        snapshot.Value.HeadHunter.IntervalMinutes = 60;
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PublishAsync(snapshot.Value, 1, "test", Ct));
        Assert.Equal(1, (await store.ReadAsync(Ct)).Version);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
    }

    [Fact]
    public async Task Current_corrupt_policy_fails_closed_instead_of_using_cache_or_bootstrap()
    {
        var store = Store();
        await store.ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(document => document.Key == SearchPolicyStore.Key).ExecuteUpdateAsync(update =>
            update.SetProperty(document => document.Content, "{}").SetProperty(document => document.Version, document => document.Version + 1), Ct);
        await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(Ct));
        await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(Ct));
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("policy.search")]
    [InlineData("policy.llm")]
    [InlineData("policy.automation")]
    public async Task Deleting_a_previously_published_policy_never_reimports_bootstrap(string key)
    {
        var search = Store();
        var llm = new LlmPolicyStore(pg.Policies, Llm.LlmRuntimePolicyTests.Bootstrap());
        var automation = new AutomationPolicyStore(pg.Policies, new DecisionOptions());
        async Task Read()
        {
            if (key == SearchPolicyStore.Key) await search.ReadAsync(Ct);
            else if (key == LlmPolicyStore.Key) await llm.ReadAsync(Ct);
            else await automation.ReadAsync(Ct);
        }
        await Read();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(document => document.Key == key).ExecuteDeleteAsync(Ct);
        await Assert.ThrowsAsync<JsonException>(Read);
        Assert.False(await context.Documents.AnyAsync(document => document.Key == key, Ct));
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(SearchPolicyStore.Key)]
    [InlineData(JobFinder.Pipeline.Modules.Engagement.EngagementPolicyStore.Key)]
    public async Task Invalid_feature_policy_stops_host_before_collector_startup(string key)
    {
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Documents.Add(new AppDocument { Key = key, Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var later = new LaterService();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(Store());
            services.AddSingleton(new LlmPolicyStore(pg.Policies, Llm.LlmRuntimePolicyTests.Bootstrap()));
            services.AddSingleton(new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies));
            services.AddSingleton(new JobFinder.Pipeline.Modules.Engagement.EngagementPolicyStore(pg.Policies, new ThreadsOptions()));
            services.AddHostedService<PolicyInitializer>();
            services.AddSingleton<IHostedService>(later);
        }).Build();
        await Assert.ThrowsAsync<JsonException>(() => host.StartAsync(Ct));
        Assert.False(later.Started);
    }

    [Fact]
    public async Task Search_editor_renders_the_authoritative_published_policy_not_bootstrap()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.Filter.OfficeCities = ["published-city"];
        snapshot.Value.Filter.TitleStopWords = ["published-stop"];
        await store.PublishAsync(snapshot.Value, snapshot.Version, "test", Ct);
        using var services = new ServiceCollection().AddLogging().AddSingleton(store).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<JobFinder.App.Components.Shared.SearchPolicyEditor>(ParameterView.Empty);
            return output.ToHtmlString();
        });
        Assert.Contains("published-city", html);
        Assert.Contains("published-stop", html);
        Assert.Contains("headHunter", html);
        Assert.DoesNotContain("config/appsettings.json", html);
    }

    [Fact]
    public async Task Habr_collection_uses_published_selectors_and_markers_not_old_bootstrap()
    {
        var store = Store();
        var policy = await store.ReadAsync(Ct);
        policy.Value.Habr.Queries = ["published-query"];
        policy.Value.Filter.RequiredMarkers = ["Java"];
        policy.Value.Habr.JitterMinutes = 0;
        await store.PublishAsync(policy.Value, policy.Version, "test", Ct);
        using var web = new MockHttpMessageHandler();
        web.Expect("https://career.habr.com/api/frontend/vacancies?q=published-query&type=all&sort=date&page=1")
            .Respond("application/json", """{"list":[{"id":9988,"title":"Senior Java developer","skills":[{"title":"Java"}]}],"meta":{"totalPages":1}}""");
        web.Expect("https://career.habr.com/vacancies/9988").Respond("text/html", """<script type="application/ld+json">{"description":"Java backend tasks"}</script>""");
        web.Fallback.Throw(new InvalidOperationException("unexpected collection request"));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var client = new HttpClient(web);
        var http = new PoliteHttp(client, new ScrapeOptions { MinDelayMs = 0, MaxDelayMs = 1 }, TimeProvider.System);
        var triggers = new JobTriggers();
        var notifier = new Notifier(pg.Db, time, await pg.NotificationsAsync());
        var ingestor = new VacancyIngestor(pg.Db, new HhClient(http), new HabrOptions(), time, NullLogger<VacancyIngestor>.Instance, await pg.CapturesAsync());
        using var job = new HabrSearchJob(store, new HabrClient(http), ingestor, triggers, pg.Db, notifier, time, NullLogger<HabrSearchJob>.Instance);
        await job.StartAsync(Ct);
        await Eventually(async () => triggers.Names.Contains(JobNames.HabrSearch));
        triggers.Trigger(JobNames.HabrSearch);
        await Eventually(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.JobRuns.AnyAsync(run => run.Job == JobNames.HabrSearch && run.Status == JobRunStatus.Ok, Ct);
        });
        await job.StopAsync(Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = await read.Vacancies.SingleAsync(vacancy => vacancy.Title == "Senior Java developer", Ct);
        Assert.Equal("Java backend tasks", vacancy.Description);
        Assert.Equal(VacancyStatus.New, vacancy.Status);
        web.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task Disabled_job_remains_live_and_can_be_enabled_without_restart()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.Habr.Enabled = false;
        await store.PublishAsync(snapshot.Value, snapshot.Version, "test", Ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var triggers = new JobTriggers();
        using var job = new ProbeJob(store, pg.Db, triggers, time, await pg.NotificationsAsync());
        await job.StartAsync(Ct);
        await Eventually(() => Task.FromResult(job.Revision == 2));
        Assert.Equal(0, job.Runs);
        var edited = await store.ReadAsync(Ct);
        edited.Value.Habr.Enabled = true;
        await store.PublishAsync(edited.Value, edited.Version, "test", Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await Eventually(() => Task.FromResult(job.Runs == 1));
        await job.StopAsync(Ct);
        Assert.Equal(3, job.Revision);
    }

    [Fact]
    public async Task Schedule_refresh_does_not_turn_a_configuration_poll_into_a_run()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.Habr.IntervalMinutes = 10;
        snapshot.Value.Habr.JitterMinutes = 0;
        await store.PublishAsync(snapshot.Value, snapshot.Version, "test", Ct);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var triggers = new JobTriggers();
        using var job = new ProbeJob(store, pg.Db, triggers, time, await pg.NotificationsAsync());
        await job.StartAsync(Ct);
        await Eventually(() => Task.FromResult(job.Runs == 1));
        await Eventually(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.JobRuns.AnyAsync(run => run.Job == job.Name && run.Status == JobRunStatus.Ok, Ct);
        });
        await Eventually(() => Task.FromResult(job.ScheduleReads > 0));
        var edited = await store.ReadAsync(Ct);
        edited.Value.Habr.IntervalMinutes = 5;
        await store.PublishAsync(edited.Value, edited.Version, "test", Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await Eventually(() => Task.FromResult(job.Revision == 3));
        Assert.Equal(1, job.Runs);
        time.Advance(TimeSpan.FromMinutes(4));
        await Eventually(() => Task.FromResult(job.Runs == 2));
        await job.StopAsync(Ct);
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < until, "background job did not reach the expected state");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class ProbeJob(SearchPolicyStore store, IDbContextFactory<JobFinderDbContext> db, JobTriggers triggers, TimeProvider time, NotificationDeliveryRequests notifications)
        : SearchScheduledJob(store, db, triggers, new Notifier(db, time, notifications), time, NullLogger.Instance)
    {
        private int _runs;
        private int _scheduleReads;
        public int Runs => Volatile.Read(ref _runs);
        public int ScheduleReads => Volatile.Read(ref _scheduleReads);
        public long Revision => Snapshot?.Version ?? 0;
        public override string Name => "search-policy-probe";
        protected override bool Enabled => Snapshot.Value.Habr.Enabled;
        protected override TimeSpan FirstDelay => TimeSpan.Zero;
        protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt)
        {
            Interlocked.Increment(ref _scheduleReads);
            return finishedAt.AddMinutes(Snapshot.Value.Habr.IntervalMinutes);
        }
        protected override Task<JobOutcome> RunOnceAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(new JobOutcome(1));
        }
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("injected publication failure");
    }

    private sealed class LaterService : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }
}
