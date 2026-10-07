using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using Polly;
using Polly.CircuitBreaker;

namespace JobFinder.Integrations.Http;

public sealed class ScrapeOptions
{
    public const string Section = "Scrape";

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    /// <summary>Minimum pause between two requests to the same site; the actual pause is random in [Min, Max].</summary>
    public int MinDelayMs { get; set; } = 2000;

    public int MaxDelayMs { get; set; } = 6000;

    public int TimeoutSec { get; set; } = 30;

    /// <summary>After a captcha, 403 or 429 no request goes to that site (and its family) for this long: every hit prolongs a block.</summary>
    public int BlockCooldownMinutes { get; set; } = 120;

    /// <summary>Per site family (<see cref="PoliteHttp.Family"/>): its own pacing and request budgets.</summary>
    public Dictionary<string, ScrapeSiteOptions> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An HTTP proxy for sites this machine cannot reach (the one ShadowPrompter uses: <c>http://127.0.0.1:8081</c> on the host,
    /// <c>http://host.docker.internal:8081</c> from the container). Empty = no proxy route.
    /// </summary>
    public string Proxy { get; set; } = "";

    /// <summary>Hosts (and their subdomains) always read through <see cref="Proxy"/>.</summary>
    public List<string> ProxyHosts { get; set; } = [];

    /// <summary>
    /// A request that fails on the way (no connection, no DNS answer, a broken TLS handshake, a timeout) is repeated once through the
    /// proxy, and the site keeps that route for <see cref="ProxyStickyHours"/>. An HTTP answer (403, a captcha) is not a route problem:
    /// it stays a block.
    /// </summary>
    public bool ProxyOnNetworkFailure { get; set; } = true;

    public int ProxyStickyHours { get; set; } = 24;

    /// <summary>A body larger than this (by Content-Length) is not downloaded: a vacancy page is never megabytes of text.</summary>
    public int MaxPageBytes { get; set; } = 4 * 1024 * 1024;
}

/// <summary>A fetched page: where it ended up after redirects, what it is, and its text (empty for a binary or an oversized body).</summary>
public sealed record HttpPage(Uri FinalUri, string? MediaType, string Body, bool ViaProxy)
{
    /// <summary>HTML or XHTML: worth parsing as a page.</summary>
    public bool IsHtml => MediaType is null or "text/html" or "application/xhtml+xml";
}

/// <summary>
/// A site that tolerates only so much from one address (hh.ru / rabota.by behind DDoS-Guard: a client without its JavaScript check gets a
/// small allowance, then 403). Everything that reads the site shares these limits, whatever job it runs in.
/// </summary>
public sealed class ScrapeSiteOptions
{
    public int MinDelayMs { get; set; } = 10000;

    public int MaxDelayMs { get; set; } = 25000;

    /// <summary>Request budgets over sliding windows; a request that would exceed one waits for a slot or is refused locally.</summary>
    public List<ScrapeLimit> Limits { get; set; } = [];

    /// <summary>The share of every budget background reads (backfills, the company sweep) may use; the rest stays for new vacancies.</summary>
    public double BackgroundShare { get; set; } = 0.6;

    /// <summary>Blocks in a row double the pause (from <see cref="ScrapeOptions.BlockCooldownMinutes"/>) up to this.</summary>
    public int MaxCooldownMinutes { get; set; } = 1440;
}

public sealed class ScrapeLimit
{
    public int Minutes { get; set; }

    public int Max { get; set; }
}

/// <summary>Who needs the page: backfills and sweeps get a share of the budget, a link the user sent goes outside it.</summary>
public enum ScrapePriority
{
    Low,
    Normal,
    High,
}

/// <summary>The site answered with a captcha, 403 or 429, is still paused after one, or its request budget is used up.</summary>
public sealed class SourceBlockedException(string host, string reason, DateTimeOffset? until = null, bool fresh = true, bool budget = false, bool announced = false)
    : Exception(budget ? $"{host}: {reason}" : $"{host} blocked the scraper: {reason}")
{
    public string Host { get; } = host;

    /// <summary>No request goes to the site before this (null = unknown).</summary>
    public DateTimeOffset? Until { get; } = until;

    /// <summary>The site answered so just now; false = refused locally (a pause or the budget), nothing to announce again.</summary>
    public bool Fresh { get; } = fresh && !budget;

    /// <summary>Refused by our own request budget, not by the site.</summary>
    public bool Budget { get; } = budget;
    public bool Announced { get; } = announced;
}

/// <summary>What must survive a restart: the pause and the blocks in a row (the breaker lives in memory), and the recent requests the budgets start from.</summary>
public sealed record ScrapeState(DateTimeOffset? PausedUntil, int BlockStreak, IReadOnlyList<DateTimeOffset> Requests)
{
    public static readonly ScrapeState Empty = new(null, 0, []);
}

/// <summary>Where <see cref="ScrapeState"/> is kept (the settings table).</summary>
public interface IScrapeStateStore
{
    Task<ScrapeState> LoadAsync(string family, CancellationToken ct);

    Task SaveAsync(string family, ScrapeState state, CancellationToken ct);
}

public sealed record ScrapeAdmission(ScrapeState State, long BlockRevision, DateTimeOffset? RetryAt = null, bool Paused = false);

public interface IScrapeRequestLedger : IScrapeStateStore
{
    Task<ScrapeAdmission> ReserveAsync(string family, ScrapePriority priority, CancellationToken ct);
    Task<ScrapeState> BlockAsync(string family, CancellationToken ct);
    Task<ScrapeState> RecoverAsync(string family, long blockRevision, CancellationToken ct);
}

public static class ScrapeCooldown
{
    /// <summary>The pause after the <paramref name="streak"/>-th block in a row: the base, doubled each time, up to the cap.</summary>
    public static TimeSpan After(int streak, int baseMinutes, int maxMinutes) =>
        TimeSpan.FromMinutes(Math.Min(Math.Max(baseMinutes, maxMinutes), baseMinutes * Math.Pow(2, Math.Max(0, streak - 1))));
}

/// <summary>
/// GET-only HTTP for public pages, per site family, on standard parts: a Polly circuit breaker opens on a captcha/403/429 and stays open
/// for a pause that doubles with every block in a row (a failed probe after the pause counts), plus a timeout; request budgets are
/// chained sliding-window rate limiters (the background share chained in front), pacing a one-token bucket plus a random human-like
/// delay. Own code only keeps what these do not: the pause and the budgets across restarts, the anti-bot cookies of a flagged session
/// (dropped), and a regional redirect (rabota.by → gomel.rabota.by) remembered so a page costs one request.
/// </summary>
/// <param name="cookies">The jar of <paramref name="http"/>'s handler.</param>
/// <param name="store">Keeps the pause, the block streak and the recent requests across restarts; null = in memory only.</param>
/// <param name="proxied">The same client over <see cref="ScrapeOptions.Proxy"/>; null = no proxy route.</param>
public sealed class PoliteHttp(HttpClient http, ScrapeOptions options, TimeProvider time, CookieContainer? cookies = null,
    IScrapeStateStore? store = null, HttpClient? proxied = null)
{
    public const string ClientName = "scrape";

    private static readonly TimeSpan Ledger = TimeSpan.FromDays(1);

    private sealed class Site
    {
        public required ResiliencePipeline<HttpResponseMessage> Pipeline { get; init; }
        public required RateLimiter Pacing { get; init; }
        public RateLimiter? Budget { get; init; }
        public RateLimiter? Background { get; init; }
        public IReadOnlyList<(SlidingWindowRateLimiter Limiter, TimeSpan Window, int Max)> Windows { get; init; } = [];

        /// <summary>The breaker's current break, or a pause carried over from before a restart.</summary>
        public DateTimeOffset? PausedUntil;

        public int Streak;
        public int CarriedStreak;
        public readonly List<DateTimeOffset> Requests = [];
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<Site>>> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Families moved to the proxy after a network failure, until when.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _proxiedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a request to the host goes through the proxy now (configured, or learned from a network failure).</summary>
    public bool UsesProxy(string host) =>
        proxied is not null
        && (options.ProxyHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
            || _proxiedUntil.TryGetValue(Family(host), out var until) && until > time.GetUtcNow());

    /// <summary>The proxy a request to the host takes now; null = direct (for a browser that must reach the same site the same way).</summary>
    public string? ProxyFor(string host) => UsesProxy(host) ? options.Proxy : null;

    /// <summary>A failure of the route, not of the site: no connection, no DNS answer, a broken TLS handshake, or no answer in time.</summary>
    public static bool IsNetworkFailure(Exception e) =>
        e is Polly.Timeout.TimeoutRejectedException
        || e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError };

    /// <summary>hh.ru, its regional subdomains and rabota.by are one site behind one anti-bot: a block on one pauses all of them.</summary>
    public static string Family(string host) =>
        host.EndsWith("hh.ru", StringComparison.OrdinalIgnoreCase) || host.EndsWith("rabota.by", StringComparison.OrdinalIgnoreCase)
            ? "hh"
            : host.ToLowerInvariant();

    /// <summary>When the pause after a block ends for the host's site; null = not paused (or the site not used by this process yet).</summary>
    public DateTimeOffset? BlockedUntil(string host) =>
        _sites.TryGetValue(Family(host), out var lazy) && lazy.Value is { IsCompletedSuccessfully: true } task && task.Result.PausedUntil > time.GetUtcNow()
            ? task.Result.PausedUntil
            : null;

    /// <summary>Like <see cref="BlockedUntil"/>, reading the stored state first.</summary>
    public async Task<DateTimeOffset?> PausedUntilAsync(string host, CancellationToken ct)
    {
        var site = await SiteAsync(Family(host));
        if (store is IScrapeRequestLedger ledger) Sync(site, await ledger.LoadAsync(Family(host), ct));
        return site.PausedUntil > time.GetUtcNow() ? site.PausedUntil : null;
    }

    // A browser's Accept: hh answers 406 when application/json is listed for an HTML page (it takes it for an API call).
    /// <param name="maxWait">How long the caller may wait for a slot in the budget; null = refuse at once when there is none.</param>
    public async Task<string> GetStringAsync(Uri url, CancellationToken ct, string accept = DefaultAccept,
        ScrapePriority priority = ScrapePriority.Normal, TimeSpan? maxWait = null) =>
        (await GetPageAsync(url, ct, accept, priority, maxWait)).Body;

    public const string DefaultAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    /// <summary>
    /// The page with its final address and media type. A binary body (a PDF, an image) or one over <see cref="ScrapeOptions.MaxPageBytes"/>
    /// is not downloaded: <see cref="HttpPage.Body"/> is empty.
    /// </summary>
    public async Task<HttpPage> GetPageAsync(Uri url, CancellationToken ct, string accept = DefaultAccept,
        ScrapePriority priority = ScrapePriority.Normal, TimeSpan? maxWait = null)
    {
        var family = Family(url.Host);
        var budgetDeadline = maxWait is { } wait && wait > TimeSpan.Zero ? time.GetUtcNow() + wait : (DateTimeOffset?)null;
        var site = await SiteAsync(family);
        if (store is IScrapeRequestLedger ledger) Sync(site, await ledger.LoadAsync(family, ct));
        if (site.PausedUntil > time.GetUtcNow())
        {
            Count(family, priority, "paused");
            throw new SourceBlockedException(url.Host, "paused after a block", site.PausedUntil, fresh: false);
        }

        using var budget = await AcquireAsync(url, family, site, priority, maxWait, ct);
        using (var pace = await site.Pacing.AcquireAsync(1, ct))
        {
            var (min, max) = options.Sites.TryGetValue(family, out var s) ? (s.MinDelayMs, s.MaxDelayMs) : (options.MinDelayMs, options.MaxDelayMs);
            if (max > min) await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(0, max - min)), time, ct);
        }

        var target = _aliases.TryGetValue(url.Host, out var alias) ? new UriBuilder(url) { Host = alias }.Uri : url;
        var viaProxy = UsesProxy(url.Host);
        HttpResponseMessage response;
        try
        {
            try
            {
                response = await SendAsync(site, family, target, accept, viaProxy, priority, budgetDeadline, ct);
            }
            catch (Exception e) when (!viaProxy && proxied is not null && options.ProxyOnNetworkFailure && IsNetworkFailure(e) && !ct.IsCancellationRequested)
            {
                // Out of reach from here (a firewall, a DNS or TLS reset): once more through the proxy; the route sticks when it works.
                Count(family, priority, "network_failure");
                response = await SendAsync(site, family, target, accept, viaProxy: true, priority, budgetDeadline, ct);
                viaProxy = true;
                _proxiedUntil[family] = time.GetUtcNow().AddHours(options.ProxyStickyHours);
            }
        }
        catch (BrokenCircuitException e)
        {
            Count(family, priority, "paused");
            throw new SourceBlockedException(url.Host, "paused after a block", site.PausedUntil ?? time.GetUtcNow() + (e.RetryAfter ?? TimeSpan.Zero), fresh: false);
        }

        using (response)
        {
            var finalUri = response.RequestMessage?.RequestUri ?? target;
            if (IsBlock(response))
            {
                Count(family, priority, "blocked");
                DropCookies(family);
                if (store is IScrapeRequestLedger blockLedger)
                {
                    Sync(site, await blockLedger.BlockAsync(family, ct));
                }
                else if (!(site.PausedUntil > time.GetUtcNow()))
                {
                    // The breaker did not open (Polly needs two calls in its window, a fresh process has one): the same pause by hand.
                    site.CarriedStreak++;
                    site.Streak = site.CarriedStreak;
                    var max = options.Sites.TryGetValue(family, out var limits) ? limits.MaxCooldownMinutes : options.BlockCooldownMinutes;
                    site.PausedUntil = time.GetUtcNow() + ScrapeCooldown.After(site.Streak, options.BlockCooldownMinutes, max);
                    await SaveAsync(family, site);
                }

                var reason = finalUri.AbsolutePath.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                    ? $"redirected to {finalUri.AbsolutePath}"
                    : $"HTTP {(int)response.StatusCode}";
                throw new SourceBlockedException(url.Host, site.Streak > 1 ? $"{reason} ({site.Streak} blocks in a row)" : reason,
                    site.PausedUntil > time.GetUtcNow() ? site.PausedUntil : null, announced: store is IScrapeRequestLedger);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Count(family, priority, "not_found");
                throw new HttpRequestException($"404 {url}", null, HttpStatusCode.NotFound);
            }

            response.EnsureSuccessStatusCode();
            // A regional redirect of the same page (rabota.by → gomel.rabota.by): ask the region directly next time.
            if (!finalUri.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && Family(finalUri.Host) == family &&
                finalUri.AbsolutePath == target.AbsolutePath)
                _aliases[url.Host] = finalUri.Host;
            Count(family, priority, viaProxy ? "ok_proxy" : "ok");
            if (store is not IScrapeRequestLedger && site.CarriedStreak > 0)
            {
                // The site answers again: a streak carried over a restart is over.
                site.CarriedStreak = 0;
                await SaveAsync(family, site);
            }

            var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var textual = media is null || media.StartsWith("text/", StringComparison.Ordinal) || media.EndsWith("xml", StringComparison.Ordinal)
                          || media.EndsWith("json", StringComparison.Ordinal);
            var tooBig = response.Content.Headers.ContentLength > options.MaxPageBytes;
            // The pipeline's timeout ends with the headers: the body gets its own deadline.
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reading.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSec));
            var body = textual && !tooBig ? await response.Content.ReadAsStringAsync(reading.Token) : "";
            return new HttpPage(finalUri, media, body, viaProxy);
        }
    }

    /// <summary>One request through the site's pipeline (breaker + timeout) over the direct or the proxy route; headers only.</summary>
    private async Task<HttpResponseMessage> SendAsync(Site site, string family, Uri target, string accept, bool viaProxy, ScrapePriority priority, DateTimeOffset? budgetDeadline, CancellationToken ct)
    {
        var admission = store is IScrapeRequestLedger ledger ? await ReserveAsync(ledger, site, family, target, priority, budgetDeadline, ct) : null;
        return await site.Pipeline.ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.6");
            if (admission is null) await RecordAsync(family, site);
            var response = await (viaProxy ? proxied! : http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (store is IScrapeRequestLedger recovery && admission is not null && response.IsSuccessStatusCode && !IsBlock(response))
            {
                try { Sync(site, await recovery.RecoverAsync(family, admission.BlockRevision, token)); }
                catch { response.Dispose(); throw; }
            }
            return response;
        }, ct);
    }

    private async Task<ScrapeAdmission> ReserveAsync(IScrapeRequestLedger ledger, Site site, string family, Uri target,
        ScrapePriority priority, DateTimeOffset? budgetDeadline, CancellationToken ct)
    {
        var admission = await ledger.ReserveAsync(family, priority, ct);
        while (!admission.Paused && admission.RetryAt is { } next && next <= budgetDeadline)
        {
            var delay = next - time.GetUtcNow();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, time, ct);
            admission = await ledger.ReserveAsync(family, priority, ct);
        }
        Sync(site, admission.State);
        if (admission.RetryAt is { } retry)
        {
            Count(family, priority, admission.Paused ? "paused" : "budget");
            throw new SourceBlockedException(target.Host, admission.Paused ? "paused after a block" : "request budget used up",
                retry, fresh: false, budget: !admission.Paused);
        }
        return admission;
    }

    private static bool IsBlock(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ||
        response.RequestMessage?.RequestUri?.AbsolutePath.Contains("captcha", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<RateLimitLease?> AcquireAsync(Uri url, string family, Site site, ScrapePriority priority, TimeSpan? maxWait, CancellationToken ct)
    {
        var limiter = priority switch
        {
            ScrapePriority.High => null,
            ScrapePriority.Low => site.Background ?? site.Budget,
            _ => site.Budget,
        };
        if (limiter is null) return null;
        RateLimitLease lease;
        if (maxWait is { } wait && wait > TimeSpan.Zero)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            try
            {
                lease = await limiter.AcquireAsync(1, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lease = limiter.AttemptAcquire();
            }
        }
        else
        {
            lease = limiter.AttemptAcquire();
        }

        if (lease.IsAcquired) return lease;
        // A chained limiter does not pass RetryAfter on: then the next refill of the shortest window.
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? after
            : site.Windows.Min(w => w.Window / Segments((int)w.Window.TotalMinutes));
        lease.Dispose();
        Count(family, priority, "budget");
        throw new SourceBlockedException(url.Host, "request budget used up", time.GetUtcNow() + retryAfter, budget: true);
    }

    private Task<Site> SiteAsync(string family) =>
        _sites.GetOrAdd(family, f => new Lazy<Task<Site>>(() => CreateSiteAsync(f), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private async Task<Site> CreateSiteAsync(string family)
    {
        options.Sites.TryGetValue(family, out var limits);
        var baseMinutes = options.BlockCooldownMinutes;
        var maxMinutes = limits?.MaxCooldownMinutes ?? baseMinutes;
        Site? site = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = time }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Result is { } r && IsBlock(r)),
                // Any block among the calls of the last day opens it (a cold start needs two calls: Polly's minimum).
                FailureRatio = 0.001,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromDays(1),
                BreakDurationGenerator = args =>
                {
                    site!.Streak = site.CarriedStreak + (store is IScrapeRequestLedger ? 1 : args.HalfOpenAttempts + 1);
                    return ValueTask.FromResult(ScrapeCooldown.After(site.Streak, baseMinutes, maxMinutes));
                },
                OnOpened = async args =>
                {
                    site!.PausedUntil = time.GetUtcNow() + args.BreakDuration;
                    if (store is not IScrapeRequestLedger) await SaveAsync(family, site);
                },
                OnClosed = async _ =>
                {
                    (site!.Streak, site.CarriedStreak, site.PausedUntil) = (0, 0, null);
                    if (store is not IScrapeRequestLedger) await SaveAsync(family, site);
                },
            })
            .AddTimeout(TimeSpan.FromSeconds(options.TimeoutSec))
            .Build();

        var min = limits?.MinDelayMs ?? options.MinDelayMs;
        var pacing = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromMilliseconds(Math.Max(1, min)), AutoReplenishment = true,
            QueueLimit = 1000, QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
        var windows = (limits?.Limits ?? []).Where(l => l is { Minutes: > 0, Max: > 0 }).OrderByDescending(l => l.Minutes)
            .Select(l => (Limiter: Window(l.Minutes, l.Max), Window: TimeSpan.FromMinutes(l.Minutes), l.Max)).ToList();
        var background = limits is null
            ? []
            : windows.Select(w => Window((int)w.Window.TotalMinutes, Math.Max(1, (int)Math.Floor(w.Max * limits.BackgroundShare)))).ToList();
        site = new Site
        {
            Pipeline = pipeline,
            Pacing = pacing,
            Windows = windows,
            Budget = windows.Count == 0 ? null : RateLimiter.CreateChained([.. windows.Select(w => (RateLimiter)w.Limiter)]),
            // Background reads take their own smaller windows first, then the shared ones: they never eat the whole budget.
            Background = windows.Count == 0 ? null : RateLimiter.CreateChained([.. background, .. windows.Select(w => (RateLimiter)w.Limiter)]),
        };

        if (store is not null)
        {
            var state = await store.LoadAsync(family, CancellationToken.None);
            var now = time.GetUtcNow();
            site.PausedUntil = state.PausedUntil > now ? state.PausedUntil : null;
            site.CarriedStreak = state.BlockStreak;
            site.Requests.AddRange(state.Requests.Where(t => t > now - Ledger));
            // The budgets start from the requests made before the restart (counted as if made now: stricter, never looser).
            foreach (var (limiter, window, max) in store is IScrapeRequestLedger ? [] : site.Windows)
            {
                var used = Math.Min(max, site.Requests.Count(t => t > now - window));
                if (used > 0) limiter.AttemptAcquire(used).Dispose();
            }
        }

        return site;

        static SlidingWindowRateLimiter Window(int minutes, int max) => new(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = max, Window = TimeSpan.FromMinutes(minutes), SegmentsPerWindow = Segments(minutes),
            AutoReplenishment = true, QueueLimit = 1000, QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>A window refills in steps: every 5 minutes, at most 24 steps (hourly for a day).</summary>
    private static int Segments(int minutes) => Math.Clamp(minutes / 5, 1, 24);

    /// <summary>
    /// The request log feeds the budgets across restarts, so only a site with budgets keeps one: any other site (every host a crawl
    /// touches) would cost a settings row and a write per request.
    /// </summary>
    private async Task RecordAsync(string family, Site site)
    {
        if (site.Windows.Count == 0) return;
        var now = time.GetUtcNow();
        lock (site.Requests)
        {
            site.Requests.Add(now);
            site.Requests.RemoveAll(t => t <= now - Ledger);
        }

        await SaveAsync(family, site);
    }

    private Task SaveAsync(string family, Site site)
    {
        if (store is null) return Task.CompletedTask;
        List<DateTimeOffset> requests;
        lock (site.Requests) requests = [.. site.Requests];
        return store.SaveAsync(family, new ScrapeState(site.PausedUntil, Math.Max(site.Streak, site.CarriedStreak), requests), CancellationToken.None);
    }

    private static void Sync(Site site, ScrapeState state)
    {
        site.PausedUntil = state.PausedUntil;
        site.CarriedStreak = state.BlockStreak;
        site.Streak = state.BlockStreak;
    }

    /// <summary>The anti-bot cookies of a flagged session would keep the block after the pause: the next request starts a new session.</summary>
    private void DropCookies(string family)
    {
        if (cookies is null) return;
        foreach (Cookie c in cookies.GetAllCookies())
            if (Family(c.Domain.TrimStart('.')) == family) c.Expired = true;
    }

    private static void Count(string family, ScrapePriority priority, string outcome) =>
        Core.Telemetry.Telemetry.ScrapeRequests.Add(1, new TagList { { "family", family }, { "priority", priority.ToString() }, { "outcome", outcome } });

    /// <param name="proxy">An HTTP proxy address; empty = the direct route (the OS proxy is ignored: the route is explicit).</param>
    /// <exception cref="UriFormatException">A malformed proxy address: the start fails instead of silently going direct.</exception>
    public static HttpMessageHandler CreateHandler(CookieContainer? cookies = null, string? proxy = null) => new SocketsHttpHandler
    {
        CookieContainer = cookies ?? new CookieContainer(),
        UseCookies = true,
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        UseProxy = !string.IsNullOrWhiteSpace(proxy),
        Proxy = string.IsNullOrWhiteSpace(proxy) ? null : new WebProxy(new Uri(proxy)),
    };
}
