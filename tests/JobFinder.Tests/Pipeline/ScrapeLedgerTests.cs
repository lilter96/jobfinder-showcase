using System.Net;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Http;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed class ScrapeLedgerTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 18, 0, 0, TimeSpan.Zero));
    private IHost? _host;
    private readonly ScrapeOptions _options = new()
    {
        MinDelayMs = 1, MaxDelayMs = 1, BlockCooldownMinutes = 1,
        Sites = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hh"] = new() { MinDelayMs = 1, MaxDelayMs = 1, MaxCooldownMinutes = 16, BackgroundShare = 0.5, Limits = [new() { Minutes = 1, Max = 6 }] },
        },
    };
    private ScrapeStateStore Store => (ScrapeStateStore)_host!.Services.GetRequiredService<IScrapeStateStore>();

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith("scrape.")).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private async Task StartAsync(SaveFault? fault = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);
            services.RemoveAll<ScrapeOptions>();
            services.AddSingleton(_options);
        });
        await _host.StartAsync(Ct);
    }

    private ScrapeStateStore AnotherStore() => ActivatorUtilities.CreateInstance<ScrapeStateStore>(_host!.Services);
    private PoliteHttp Client(Handler handler, HttpClient? proxy = null) => new(new HttpClient(handler), _options, _time, store: Store, proxied: proxy);
    private static Uri Page(int id = 1) => new("https://rabota.by/vacancy/" + id);

    private async Task LegacyAsync(DateTimeOffset pause, int streak = 1, params DateTimeOffset[] requests)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.AddRange(new AppSetting { Key = ScrapeStateStore.PauseKey("hh"), Value = pause.ToString("O"), UpdatedAt = _time.GetUtcNow() },
            new AppSetting { Key = ScrapeStateStore.BudgetKey("hh"), Value = streak + "|" + string.Join(',', requests.Select(at => at.ToUnixTimeSeconds())), UpdatedAt = _time.GetUtcNow() });
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Legacy_pause_and_budget_are_read_without_mutation_then_imported_as_one_projection()
    {
        await StartAsync();
        await LegacyAsync(_time.GetUtcNow().AddMinutes(-1), 2, _time.GetUtcNow().AddSeconds(-10));
        var legacy = await Store.LoadAsync("rabota.by", Ct);
        Assert.Equal(2, legacy.BlockStreak);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Settings.CountAsync(row => row.Key.StartsWith("scrape."), Ct));
        var admission = await Store.ReserveAsync("HH", ScrapePriority.Normal, Ct);
        Assert.Null(admission.RetryAt);
        Assert.Equal(2, admission.State.Requests.Count);
        var recovered = await Store.RecoverAsync("hh", admission.BlockRevision, Ct);
        Assert.Null(recovered.PausedUntil);
        Assert.Equal(0, recovered.BlockStreak);
        Assert.Equal(2, recovered.Requests.Count);
        Assert.False(await context.Settings.AnyAsync(row => row.Key == ScrapeStateStore.PauseKey("hh"), Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Independent_producers_share_one_atomic_window_and_never_overwrite_each_others_reservations()
    {
        _options.Sites["hh"].Limits[0].Max = 3;
        await StartAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => AnotherStore().ReserveAsync("hh.ru", ScrapePriority.Normal, Ct)));
        Assert.Equal(3, results.Count(result => result.RetryAt is null));
        Assert.All(results.Where(result => result.RetryAt is not null), result => Assert.Equal(_time.GetUtcNow().AddMinutes(1), result.RetryAt));
        Assert.Equal(3, (await AnotherStore().LoadAsync("gomel.rabota.by", Ct)).Requests.Count);
        Assert.NotNull((await AnotherStore().ReserveAsync("hh", ScrapePriority.Normal, Ct)).RetryAt);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null((await AnotherStore().ReserveAsync("hh", ScrapePriority.Normal, Ct)).RetryAt);
    }

    [Fact]
    public async Task Background_share_counts_background_requests_without_consuming_the_foreground_reserve()
    {
        await StartAsync();
        for (var index = 0; index < 2; index++) Assert.Null((await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct)).RetryAt);
        for (var index = 0; index < 3; index++) Assert.Null((await Store.ReserveAsync("hh", ScrapePriority.Low, Ct)).RetryAt);
        Assert.NotNull((await Store.ReserveAsync("hh", ScrapePriority.Low, Ct)).RetryAt);
        Assert.Null((await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct)).RetryAt);
        Assert.NotNull((await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct)).RetryAt);
    }

    [Fact]
    public async Task Every_window_is_checked_and_retry_names_the_last_exhausted_window()
    {
        _options.Sites["hh"].Limits = [new() { Minutes = 1, Max = 2 }, new() { Minutes = 60, Max = 2 }];
        await StartAsync();
        Assert.Null((await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct)).RetryAt);
        Assert.Null((await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct)).RetryAt);
        Assert.Equal(_time.GetUtcNow().AddHours(1), (await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct)).RetryAt);
    }

    [Fact]
    public async Task High_priority_bypasses_the_budget_but_never_the_current_pause()
    {
        _options.Sites["hh"].Limits[0].Max = 1;
        await StartAsync();
        await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct);
        Assert.Null((await Store.ReserveAsync("hh", ScrapePriority.High, Ct)).RetryAt);
        Assert.Single((await Store.LoadAsync("hh", Ct)).Requests);
        var blocked = await Store.BlockAsync("hh", Ct);
        var high = await AnotherStore().ReserveAsync("hh", ScrapePriority.High, Ct);
        Assert.True(high.Paused);
        Assert.Equal(blocked.PausedUntil, high.RetryAt);
    }

    [Fact]
    public async Task Concurrent_block_publications_have_one_streak_pause_and_native_notice()
    {
        await StartAsync();
        var states = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AnotherStore().BlockAsync("hh", Ct)));
        Assert.All(states, state => Assert.Equal(1, state.BlockStreak));
        Assert.All(states, state => Assert.Equal(_time.GetUtcNow().AddMinutes(1), state.PausedUntil));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Late_success_cannot_clear_a_newer_block_even_after_its_pause_expires()
    {
        await StartAsync();
        var initial = await Store.ReserveAsync("hh", ScrapePriority.High, Ct);
        await Store.BlockAsync("hh", Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        var first = await Store.ReserveAsync("hh", ScrapePriority.High, Ct);
        await AnotherStore().BlockAsync("hh", Ct);
        Assert.Equal(2, (await Store.RecoverAsync("hh", initial.BlockRevision, Ct)).BlockStreak);
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, (await Store.RecoverAsync("hh", first.BlockRevision, Ct)).BlockStreak);
        var current = await Store.ReserveAsync("hh", ScrapePriority.High, Ct);
        Assert.Equal(0, (await Store.RecoverAsync("hh", current.BlockRevision, Ct)).BlockStreak);
        Assert.Null((await Store.LoadAsync("hh", Ct)).PausedUntil);
    }

    [Fact]
    public async Task Block_native_notice_failure_rolls_back_all_pause_budget_and_owner_rows()
    {
        var fault = new SaveFault();
        await StartAsync(fault);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.BlockAsync("hh", Ct));
        Assert.Null((await Store.LoadAsync("hh", Ct)).PausedUntil);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith("scrape.")).ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("owner")]
    [InlineData("budget")]
    [InlineData("pause")]
    [InlineData("version")]
    public async Task Missing_or_damaged_native_evidence_refuses_before_any_network_call(string damage)
    {
        await StartAsync();
        await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = damage switch { "root" or "version" => ScrapeStateStore.StatePrefix + "hh", "owner" => ScrapeStateStore.OwnerPrefix + "hh", _ => ScrapeStateStore.BudgetKey("hh") };
        if (damage is "root" or "owner") await context.Settings.Where(row => row.Key == key).ExecuteDeleteAsync(Ct);
        else if (damage == "pause")
        {
            context.Settings.Add(new() { Key = ScrapeStateStore.PauseKey("hh"), Value = _time.GetUtcNow().ToString("O"), UpdatedAt = _time.GetUtcNow() });
            await context.SaveChangesAsync(Ct);
        }
        else
        {
            var row = await context.Settings.SingleAsync(row => row.Key == key, Ct);
            if (damage == "version") { var node = JsonNode.Parse(row.Value)!; node["version"] = 99; row.Value = node.ToJsonString(); }
            else row.Value = "0|";
            await context.SaveChangesAsync(Ct);
        }
        var handler = new Handler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).GetStringAsync(Page(), Ct));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("scrape.state.hh")]
    [InlineData("scrape.owner.hh")]
    [InlineData("scrape.pausedUntil.hh")]
    [InlineData("scrape.budget.hh")]
    public async Task Generic_writes_and_old_snapshot_save_cannot_bypass_the_operational_owner(string key)
    {
        await StartAsync();
        var settings = _host!.Services.GetRequiredService<RuntimeSettings>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(key, "value", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(key, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync("hh", ScrapeState.Empty, Ct));
    }

    [Fact]
    public async Task Warm_http_client_reads_a_block_created_by_another_producer_before_sending()
    {
        await StartAsync();
        var handler = new Handler();
        var client = Client(handler);
        Assert.Equal("synthetic", await client.GetStringAsync(Page(), Ct));
        await AnotherStore().BlockAsync("hh", Ct);
        var error = await Assert.ThrowsAsync<SourceBlockedException>(() => client.GetStringAsync(Page(2), Ct, priority: ScrapePriority.High));
        Assert.False(error.Fresh);
        Assert.Equal(1, handler.Calls);
        Assert.NotNull(await client.PausedUntilAsync("hh.ru", Ct));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Direct_and_proxy_attempts_each_reserve_and_proxy_cannot_exceed_the_shared_budget(int limit)
    {
        _options.Sites["hh"].Limits[0].Max = limit;
        await StartAsync();
        var direct = new Handler { Read = _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "synthetic connection failure") };
        var proxy = new Handler();
        var client = Client(direct, new HttpClient(proxy));
        if (limit == 1)
        {
            var error = await Assert.ThrowsAsync<SourceBlockedException>(() => client.GetStringAsync(Page(), Ct));
            Assert.True(error.Budget);
        }
        else Assert.Equal("synthetic", await client.GetStringAsync(Page(), Ct));
        Assert.Equal(1, direct.Calls);
        Assert.Equal(limit - 1, proxy.Calls);
        Assert.Equal(limit, (await Store.LoadAsync("hh", Ct)).Requests.Count);
    }

    [Fact]
    public async Task Actual_http_block_recovery_and_repeated_half_open_probes_use_the_owned_streak()
    {
        await StartAsync();
        var handler = new Handler { Status = HttpStatusCode.Forbidden };
        var client = Client(handler);
        for (var streak = 1; streak <= 3; streak++)
        {
            var error = await Assert.ThrowsAsync<SourceBlockedException>(() => client.GetStringAsync(Page(streak), Ct));
            Assert.True(error.Fresh);
            Assert.True(error.Announced);
            var duration = TimeSpan.FromMinutes(Math.Pow(2, streak - 1));
            Assert.Equal(_time.GetUtcNow() + duration, error.Until);
            Assert.Equal(streak, (await Store.LoadAsync("hh", Ct)).BlockStreak);
            _time.Advance(duration);
        }
        handler.Status = HttpStatusCode.OK;
        Assert.Equal("synthetic", await client.GetStringAsync(Page(4), Ct));
        Assert.Equal(0, (await Store.LoadAsync("hh", Ct)).BlockStreak);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(4, await context.Notifications.CountAsync(Ct));
        foreach (var notice in await context.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Fact]
    public async Task In_flight_success_keeps_a_block_published_while_its_request_was_running()
    {
        await StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { Read = async request => { started.TrySetResult(); await release.Task.WaitAsync(Ct); return Handler.Response(request, HttpStatusCode.OK); } };
        var client = Client(handler);
        var reading = client.GetStringAsync(Page(), Ct);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await AnotherStore().BlockAsync("hh", Ct);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("synthetic", await reading);
        Assert.Equal(1, (await Store.LoadAsync("hh", Ct)).BlockStreak);
        Assert.NotNull((await Store.LoadAsync("hh", Ct)).PausedUntil);
    }

    [Fact]
    public async Task Ordinary_unbudgeted_hosts_do_not_create_a_row_per_crawled_page()
    {
        await StartAsync();
        Assert.Null((await Store.ReserveAsync("vacancy.invalid", ScrapePriority.Normal, Ct)).RetryAt);
        await Store.RecoverAsync("vacancy.invalid", 0, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith("scrape.")).ToListAsync(Ct));
    }

    [Fact]
    public async Task Migration_downgrade_refuses_retained_native_state_and_model_has_no_pending_changes()
    {
        await StartAsync();
        await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(context.Database.HasPendingModelChanges());
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004064437_OwnedApplicationBlocks", Ct));
        Assert.Contains("Cannot downgrade retained owned scrape ledger", error.MessageText);
    }

    [Fact]
    public async Task Persisted_budget_wait_is_bounded_by_the_requested_wait_not_the_http_timeout()
    {
        _options.Sites["hh"].Limits[0].Max = 1;
        await StartAsync();
        await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct);
        var handler = new Handler();
        var gate = new GateLedger(Store);
        var client = new PoliteHttp(new HttpClient(handler), _options, _time, store: gate);
        var reading = client.GetStringAsync(Page(), Ct, maxWait: TimeSpan.FromMinutes(2));
        await gate.Denied.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        for (var step = 0; step < 6 && handler.Calls == 0 && !reading.IsCompleted; step++)
        {
            await Task.Delay(40, Ct);
            _time.Advance(TimeSpan.FromSeconds(20));
        }
        Assert.Equal("synthetic", await reading.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Persisted_budget_beyond_the_wait_bound_refuses_without_a_wire_call()
    {
        _options.Sites["hh"].Limits[0].Max = 1;
        await StartAsync();
        await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct);
        var handler = new Handler();
        var error = await Assert.ThrowsAsync<SourceBlockedException>(() => Client(handler).GetStringAsync(Page(), Ct, maxWait: TimeSpan.FromSeconds(5)));
        Assert.True(error.Budget);
        Assert.False(error.Fresh);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Cancelling_a_persisted_budget_wait_never_sends_or_reserves_another_attempt()
    {
        _options.Sites["hh"].Limits[0].Max = 1;
        await StartAsync();
        await Store.ReserveAsync("hh", ScrapePriority.Normal, Ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var gate = new GateLedger(Store);
        var handler = new Handler();
        var client = new PoliteHttp(new HttpClient(handler), _options, _time, store: gate);
        var reading = client.GetStringAsync(Page(), stop.Token, maxWait: TimeSpan.FromMinutes(2));
        await gate.Denied.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(0, handler.Calls);
        Assert.Single((await Store.LoadAsync("hh", Ct)).Requests);
    }

    [Theory]
    [InlineData("scrape.pausedUntil.hh", "not-an-instant")]
    [InlineData("scrape.budget.hh", "")]
    [InlineData("scrape.budget.hh", "1")]
    [InlineData("scrape.budget.hh", "-1|")]
    [InlineData("scrape.budget.hh", "1|not-a-time")]
    public async Task Damaged_legacy_state_never_becomes_a_fresh_empty_budget(string key, string value)
    {
        await StartAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = key, Value = value, UpdatedAt = _time.GetUtcNow() });
        await context.SaveChangesAsync(Ct);
        var handler = new Handler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).GetStringAsync(Page(), Ct));
        Assert.Equal(0, handler.Calls);
        Assert.Single(await context.Settings.Where(row => row.Key.StartsWith("scrape.")).ToListAsync(Ct));
    }

    [Fact]
    public async Task Failed_budget_reservation_commits_no_snapshot_and_calls_no_network()
    {
        var fault = new SaveFault { Reservations = true };
        await StartAsync(fault);
        fault.Active = true;
        var handler = new Handler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).GetStringAsync(Page(), Ct));
        Assert.Equal(0, handler.Calls);
        Assert.Empty((await Store.LoadAsync("hh", Ct)).Requests);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith("scrape.")).ToListAsync(Ct));
    }

    [Fact]
    public async Task Legacy_only_pause_and_budget_survive_safe_migration_down_and_up()
    {
        await LegacyAsync(_time.GetUtcNow().AddHours(1), 2, _time.GetUtcNow());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var before = await context.Settings.Where(row => row.Key.StartsWith("scrape.")).OrderBy(row => row.Key).Select(row => row.Value).ToListAsync(Ct);
        await context.GetService<IMigrator>().MigrateAsync("20261004064437_OwnedApplicationBlocks", Ct);
        await context.Database.MigrateAsync(Ct);
        Assert.Equal(before, await context.Settings.Where(row => row.Key.StartsWith("scrape.")).OrderBy(row => row.Key).Select(row => row.Value).ToListAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private sealed class GateLedger(IScrapeRequestLedger inner) : IScrapeRequestLedger
    {
        public TaskCompletionSource Denied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ScrapeState> LoadAsync(string family, CancellationToken ct) => inner.LoadAsync(family, ct);
        public Task SaveAsync(string family, ScrapeState state, CancellationToken ct) => inner.SaveAsync(family, state, ct);
        public Task<ScrapeState> BlockAsync(string family, CancellationToken ct) => inner.BlockAsync(family, ct);
        public Task<ScrapeState> RecoverAsync(string family, long revision, CancellationToken ct) => inner.RecoverAsync(family, revision, ct);
        public async Task<ScrapeAdmission> ReserveAsync(string family, ScrapePriority priority, CancellationToken ct)
        {
            var result = await inner.ReserveAsync(family, priority, ct);
            if (result.RetryAt is not null) Denied.TrySetResult();
            return result;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Read { get; set; }
        public static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status) => new(status) { RequestMessage = request, Content = new StringContent("synthetic") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Read?.Invoke(request) ?? Task.FromResult(Response(request, Status));
        }
    }

    private sealed class SaveFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public bool Reservations { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context is JobFinderDbContext context && (context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added)
                || Reservations && context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.State == EntityState.Added && entry.Entity.Key.StartsWith(ScrapeStateStore.StatePrefix))))
                throw new InvalidOperationException("synthetic scrape notice admission failure");
            return ValueTask.FromResult(result);
        }
    }
}
