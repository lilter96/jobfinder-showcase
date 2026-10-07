using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Data;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Settings;
using JobFinder.Tests.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class LlmPolicyIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private LlmPolicyStore Store(LlmOptions? deployment = null) => new(pg.Policies, deployment ?? LlmRuntimePolicyTests.Bootstrap());

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.CapturesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(document => document.Key == LlmPolicyStore.Key).ExecuteDeleteAsync(Ct);
        await context.PolicyRevisions.Where(revision => revision.Key == LlmPolicyStore.Key).ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Notifications.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Concurrent_imports_capture_effective_values_once_with_history_outbox_and_no_secrets()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Store().ReadAsync(Ct)));
        Assert.All(results, snapshot => Assert.Equal(1, snapshot.Version));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var document = await context.Documents.SingleAsync(document => document.Key == LlmPolicyStore.Key, Ct);
        Assert.DoesNotContain("synthetic-secret-not-for-policy", document.Content);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        var changedBootstrap = LlmRuntimePolicyTests.Bootstrap();
        changedBootstrap.DailyBudgetUsd = 999;
        changedBootstrap.Routes[LlmPurpose.Application][0].Reasoning!.Effort = "none";
        Assert.Equal(3.5m, (await Store(changedBootstrap).ReadAsync(Ct)).Value.DailyBudgetUsd);
    }

    [Fact]
    public async Task Concurrent_publications_have_one_atomic_winner_and_other_readers_see_the_new_revision()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        var otherReader = Store();
        await otherReader.ReadAsync(Ct);
        async Task<bool> Publish(decimal budget)
        {
            try { await store.PublishAsync(snapshot.Value with { DailyBudgetUsd = budget }, snapshot.Version, "test", Ct); return true; }
            catch (PolicyConflictException) { return false; }
        }
        var results = await Task.WhenAll(Publish(8), Publish(9));
        Assert.Single(results, success => success);
        Assert.Equal(2, (await otherReader.ReadAsync(Ct)).Version);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.PolicyRevisions.CountAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task A_caller_cannot_mutate_the_cached_or_published_policy_through_a_snapshot()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.Models.Values.Single().Name = "not-published";
        Assert.Equal("shared-model", (await store.ReadAsync(Ct)).Value.Models.Values.Single().Name);
        var current = await store.ReadAsync(Ct);
        var published = await store.PublishAsync(current.Value with { DailyBudgetUsd = 8 }, current.Version, "test", Ct);
        published.Value.Routes[LlmPurpose.Application].Steps.Clear();
        Assert.Single((await store.ReadAsync(Ct)).Value.Routes[LlmPurpose.Application].Steps);
    }

    [Fact]
    public async Task Quality_failure_has_no_policy_history_or_notification_side_effects()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.Routes[LlmPurpose.Application].Steps[0].Reasoning!.Effort = "none";
        await Assert.ThrowsAsync<LlmPolicyException>(() => store.PublishAsync(snapshot.Value, snapshot.Version, "test", Ct));
        Assert.Equal(1, (await store.ReadAsync(Ct)).Version);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task An_outbox_save_failure_rolls_back_the_preceding_compare_and_swap_update()
    {
        var store = Store();
        var snapshot = await store.ReadAsync(Ct);
        await using var existing = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, existing.Database.GetConnectionString()!);
        options.AddInterceptors(new FailSave());
        var failing = new LlmPolicyStore(new(new Factory(options.Options), TimeProvider.System, await pg.NotificationsAsync()), LlmRuntimePolicyTests.Bootstrap());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PublishAsync(snapshot.Value with { DailyBudgetUsd = 8 }, 1, "test", Ct));
        Assert.Equal(1, (await store.ReadAsync(Ct)).Version);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_preparation_operation_keeps_its_route_snapshot_across_a_policy_publication()
    {
        var deployment = LlmRuntimePolicyTests.Bootstrap();
        var store = Store(deployment);
        using var http = new FakeTransport();
        var usage = new Usage();
        var root = new LlmClient(new OpenRouterTransport(http, deployment), new ModelHealth(TimeProvider.System), deployment, usage, NullLogger<LlmClient>.Instance, store);
        Assert.Throws<InvalidOperationException>(() => root.RouteVersion(LlmPurpose.Application));
        var operation = await root.ForOperationAsync(Ct);
        Assert.Equal(1, operation.PolicyRevision);
        var version = operation.RouteVersion(LlmPurpose.Application);
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.Models.Values.Single().Name = "new-model";
        await store.PublishAsync(snapshot.Value, snapshot.Version, "test", Ct);
        await new JobFinderLlm(operation, new PromptCatalog(Fixture.PromptsDir)).RecruiterMessageAsync(
            new JobFinder.Core.Profile.CandidateProfile { Name = "Test" }, new Vacancy { Title = "Role", Url = "https://example.com", Fingerprint = "f" },
            "", "Test", "First", "Telegram", false, "", DateTimeOffset.UtcNow, true, Ct);
        Assert.Equal(["shared-model"], http.Models);
        Assert.Equal(version, operation.RouteVersion(LlmPurpose.Application));
        var fresh = await root.ForOperationAsync(Ct);
        Assert.Equal(2, fresh.PolicyRevision);
        Assert.NotEqual(version, fresh.RouteVersion(LlmPurpose.Application));
        Assert.Equal("new-model", fresh.Route(LlmPurpose.Application)[0].Name);
        var attempt = Assert.Single(usage.Attempts);
        Assert.Equal(operation.OperationId, attempt.OperationId);
        Assert.Equal(1, attempt.Recipe!.PolicyRevision);
        Assert.Equal(version, Assert.Single(attempt.Recipe.Tasks).RouteVersion);
        Assert.NotEqual(operation.OperationId, fresh.OperationId);
        Assert.Same(operation, await operation.ForOperationAsync(Ct));
    }

    [Fact]
    public async Task A_corrupt_persisted_policy_is_never_replaced_with_bootstrap_defaults_or_cached_old_settings()
    {
        var store = Store();
        await store.ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(document => document.Key == LlmPolicyStore.Key).ExecuteUpdateAsync(
            update => update.SetProperty(document => document.Content, "{}").SetProperty(document => document.Version, document => document.Version + 1), Ct);
        await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(Ct));
        await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(Ct));
        Assert.Single(await context.PolicyRevisions.ToListAsync(Ct));
        Assert.Equal("{}", (await context.Documents.SingleAsync(document => document.Key == LlmPolicyStore.Key, Ct)).Content);
    }

    [Fact]
    public async Task Invalid_policy_stops_host_initialization_before_later_background_services()
    {
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Documents.Add(new AppDocument { Key = LlmPolicyStore.Key, Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var later = new LaterService();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(Store());
            services.AddSingleton(new JobFinder.Pipeline.Modules.Discovery.SearchPolicyStore(pg.Policies, new FilterOptions(),
                new JobFinder.Core.Filtering.PrefilterOptions(), new JobFinder.Integrations.HeadHunter.HhOptions(),
                new JobFinder.Integrations.Habr.HabrOptions(), new JobFinder.Integrations.Threads.ThreadsOptions()));
            services.AddSingleton(new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies));
            services.AddSingleton(new JobFinder.Pipeline.Modules.Engagement.EngagementPolicyStore(pg.Policies, new JobFinder.Integrations.Threads.ThreadsOptions()));
            services.AddHostedService<PolicyInitializer>();
            services.AddSingleton<IHostedService>(later);
        }).Build();
        await Assert.ThrowsAsync<JsonException>(() => host.StartAsync(Ct));
        Assert.False(later.Started);
    }

    private sealed class LaterService : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("injected publication failure");
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }

    private sealed class Usage : ILlmUsageSink
    {
        public List<LlmAttempt> Attempts { get; } = [];
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class FakeTransport : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Models { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Models.Add(body.RootElement.GetProperty("model").GetString()!);
            var completion = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = """{"hook":"API","needs":[],"facts":[],"greeting":"Здравствуйте!","paragraphs":["Работал над API"],"closing":"Обсудим задачу?"}""" },
                    ["finish_reason"] = "stop",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion.ToJsonString()) };
        }
    }
}
