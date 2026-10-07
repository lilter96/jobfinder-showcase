using System.Net;
using JobFinder.Integrations.Http;
using JobFinder.Pipeline.Jobs;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Sources;

public sealed class PoliteHttpTests
{
    private sealed class ScriptedHandler(params HttpStatusCode[] script) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        /// <summary>A host that answers as if the site redirected there (what the redirect handler reports as the final URI).</summary>
        public Dictionary<string, string> Redirects { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            var status = script.Length == 0 ? HttpStatusCode.OK : script[Math.Min(Seen.Count - 1, script.Length - 1)];
            var final = Redirects.TryGetValue(request.RequestUri!.Host, out var to)
                ? new HttpRequestMessage(HttpMethod.Get, new UriBuilder(request.RequestUri) { Host = to }.Uri)
                : request;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = final, Content = new StringContent("page") });
        }
    }

    private sealed class MemoryState : IScrapeStateStore
    {
        public Dictionary<string, ScrapeState> Saved { get; } = [];

        public Task<ScrapeState> LoadAsync(string family, CancellationToken ct) => Task.FromResult(Saved.GetValueOrDefault(family, ScrapeState.Empty));

        public Task SaveAsync(string family, ScrapeState state, CancellationToken ct)
        {
            Saved[family] = state;
            return Task.CompletedTask;
        }
    }

    private static ScrapeOptions Options(params ScrapeLimit[] limits) => new()
    {
        MinDelayMs = 0, MaxDelayMs = 1, BlockCooldownMinutes = 120,
        Sites = { ["hh"] = new ScrapeSiteOptions { MinDelayMs = 0, MaxDelayMs = 1, Limits = [.. limits], BackgroundShare = 0.5, MaxCooldownMinutes = 1440 } },
    };

    private static Uri Page(int n, string host = "rabota.by") => new($"https://{host}/vacancy/{n}");

    [Fact]
    public async Task A_block_pauses_the_whole_hh_family_without_touching_the_network()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, HttpStatusCode.Forbidden);
        var http = new PoliteHttp(new HttpClient(handler), Options(), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("page", await http.GetStringAsync(Page(1), ct));
        var blocked = await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetStringAsync(Page(2), ct));
        Assert.True(blocked.Fresh);
        Assert.NotNull(blocked.Until);

        // hh.ru is the same site behind the same anti-bot: refused locally, not asked.
        var refused = await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetStringAsync(Page(3, "hh.ru"), ct));
        Assert.False(refused.Fresh);
        Assert.Equal(2, handler.Seen.Count);
        Assert.NotNull(http.BlockedUntil("spb.hh.ru"));
        Assert.Null(http.BlockedUntil("career.habr.com"));
    }

    [Fact]
    public async Task A_fresh_process_stops_at_its_first_block_and_goes_on_with_the_streak_it_found()
    {
        var store = new MemoryState { Saved = { ["hh"] = new ScrapeState(null, 1, []) } };
        var handler = new ScriptedHandler(HttpStatusCode.Forbidden);
        var http = new PoliteHttp(new HttpClient(handler), Options(), TimeProvider.System, null, store);
        var ct = TestContext.Current.CancellationToken;

        var blocked = await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetStringAsync(Page(1), ct));
        Assert.InRange(blocked.Until!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(239), TimeSpan.FromMinutes(240));
        Assert.False((await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetStringAsync(Page(2), ct))).Fresh);
        Assert.Single(handler.Seen);
        Assert.Equal(2, store.Saved["hh"].BlockStreak);
    }

    [Fact]
    public async Task Blocks_in_a_row_double_the_pause()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new MemoryState();
        var handler = new ScriptedHandler(HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.OK);
        var http = new PoliteHttp(new HttpClient(handler), Options(), time, null, store);
        var ct = TestContext.Current.CancellationToken;

        await http.GetStringAsync(Page(1), ct);
        var first = await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetStringAsync(Page(2), ct));
        Assert.Equal(time.GetUtcNow().AddHours(2), first.Until);

        // The pause is over, the probe is blocked again: twice as long, and the streak is kept for a restart.
        time.Advance(TimeSpan.FromMinutes(121));
        var second = await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetStringAsync(Page(3), ct));
        Assert.Equal(time.GetUtcNow().AddHours(4), second.Until);
        Assert.Equal(2, store.Saved["hh"].BlockStreak);

        time.Advance(TimeSpan.FromMinutes(241));
        Assert.Equal("page", await http.GetStringAsync(Page(4), ct));
        Assert.Equal(0, store.Saved["hh"].BlockStreak);
        Assert.Null(http.BlockedUntil("rabota.by"));
    }

    [Fact]
    public async Task The_budget_keeps_a_share_for_new_vacancies_and_a_users_link_goes_outside_it()
    {
        var http = new PoliteHttp(new HttpClient(new ScriptedHandler()), Options(new ScrapeLimit { Minutes = 10, Max = 2 }), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;

        await http.GetStringAsync(Page(1), ct, priority: ScrapePriority.Low);
        // Background work gets half of the window: the second backfill page waits for the next run.
        var low = await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetStringAsync(Page(2), ct, priority: ScrapePriority.Low));
        Assert.True(low is { Budget: true, Fresh: false });
        Assert.NotNull(low.Until);

        await http.GetStringAsync(Page(3), ct);
        await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetStringAsync(Page(4), ct));
        Assert.Equal("page", await http.GetStringAsync(Page(5), ct, priority: ScrapePriority.High));
    }

    [Fact]
    public async Task The_pause_and_the_budget_survive_a_restart_and_the_flagged_cookies_are_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryState();
        var cookies = new CookieContainer();
        cookies.Add(new Cookie("__ddg", "flagged", "/", ".rabota.by"));
        cookies.Add(new Cookie("session", "ok", "/", "career.habr.com"));
        var blocked = new PoliteHttp(new HttpClient(new ScriptedHandler(HttpStatusCode.OK, HttpStatusCode.Forbidden)), Options(), TimeProvider.System, cookies, store);
        await blocked.GetStringAsync(Page(1), ct);
        await Assert.ThrowsAsync<SourceBlockedException>(() => blocked.GetStringAsync(Page(2), ct));
        Assert.Empty(cookies.GetCookies(new Uri("https://rabota.by/")));
        Assert.Single(cookies.GetCookies(new Uri("https://career.habr.com/")));

        // A new process with the same store: refused locally, the site is not asked.
        var handler = new ScriptedHandler();
        var restarted = new PoliteHttp(new HttpClient(handler), Options(), TimeProvider.System, null, store);
        var e = await Assert.ThrowsAsync<SourceBlockedException>(() => restarted.GetStringAsync(Page(3, "hh.ru"), ct));
        Assert.False(e.Fresh);
        Assert.Empty(handler.Seen);

        // The requests of the previous process count against the budget of the next one.
        var budgeted = new MemoryState { Saved = { ["hh"] = new ScrapeState(null, 0, [DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow]) } };
        var fresh = new PoliteHttp(new HttpClient(handler), Options(new ScrapeLimit { Minutes = 10, Max = 2 }), TimeProvider.System, null, budgeted);
        Assert.True((await Assert.ThrowsAsync<SourceBlockedException>(() => fresh.GetStringAsync(Page(4), ct))).Budget);
    }

    [Fact]
    public async Task A_regional_redirect_is_asked_directly_next_time()
    {
        var handler = new ScriptedHandler { Redirects = { ["rabota.by"] = "gomel.rabota.by" } };
        var http = new PoliteHttp(new HttpClient(handler), Options(), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        await http.GetStringAsync(Page(1), ct);
        await http.GetStringAsync(Page(2), ct);
        Assert.Equal(["rabota.by", "gomel.rabota.by"], handler.Seen.Select(u => u.Host));
    }

    [Theory]
    [InlineData(1, 120)]
    [InlineData(2, 240)]
    [InlineData(4, 960)]
    [InlineData(6, 1440)]
    public void The_pause_grows_with_the_streak_up_to_a_day(int streak, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), ScrapeCooldown.After(streak, 120, 1440));

    [Theory]
    [InlineData("rabota.by", "hh")]
    [InlineData("spb.hh.ru", "hh")]
    [InlineData("hh.ru", "hh")]
    [InlineData("career.habr.com", "career.habr.com")]
    public void Hh_hosts_are_one_family(string host, string family) => Assert.Equal(family, PoliteHttp.Family(host));

    [Theory]
    [InlineData("2026-09-28T21:20:00Z", "2026-09-28T21:20:00Z")] // 00:20 Minsk: still allowed
    [InlineData("2026-09-28T21:40:00Z", "2026-09-29T04:00:00Z")] // 00:40 Minsk → 07:00
    [InlineData("2026-09-28T04:30:00Z", "2026-09-28T04:30:00Z")] // 07:30 Minsk: allowed
    [InlineData("2026-09-28T03:59:00Z", "2026-09-28T04:00:00Z")] // 06:59 Minsk → 07:00
    public void Searches_skip_the_quiet_hours(string due, string expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");
        var at = DateTimeOffset.Parse(due, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), QuietHours.NextAllowed(at, "00:30-07:00", zone));
    }
}
