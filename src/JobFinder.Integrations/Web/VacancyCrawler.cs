using System.Net;
using JobFinder.Core.Domain;
using JobFinder.Integrations.Browser;
using JobFinder.Integrations.Http;

namespace JobFinder.Integrations.Web;

/// <summary>Draws a script-built page (see <see cref="BrowserSessions.RenderAsync"/>); behind an interface so the crawler is testable without a browser.</summary>
public interface IPageRenderer
{
    Task<(Uri Url, string Html)> RenderAsync(Uri url, string? proxy, CancellationToken ct);
}

public sealed class BrowserPageRenderer(BrowserSessions browser) : IPageRenderer
{
    public Task<(Uri Url, string Html)> RenderAsync(Uri url, string? proxy, CancellationToken ct) => browser.RenderAsync(url, proxy, ct);
}

/// <param name="Depth">1 = linked from the post; 2 = linked from such a page.</param>
/// <param name="Via">The text of the link that led here.</param>
/// <param name="ViaProxy">Read through the proxy route (the site is out of reach directly).</param>
public sealed record CrawledPage(WebPage Page, int Depth, string? Via, bool Rendered, bool ViaProxy = false);

/// <param name="Missing">Canonical URLs that turned out not to exist (404, or a "not found" page): never a vacancy's address.</param>
public sealed record CrawlResult(IReadOnlyList<CrawledPage> Pages, IReadOnlyList<string> Failures, IReadOnlySet<string> Missing)
{
    public static readonly CrawlResult Empty = new([], [], new HashSet<string>());
}

/// <summary>
/// Reads what a vacancy post links to, and one level further where a page says "details" (a teaser → the vacancy page), breadth first:
/// the post's own links are read before anything they lead to. Every request goes through <see cref="PoliteHttp"/> (per-site pacing,
/// blocks, the proxy route); a page is read once however it was linked (canonical URLs); limits on pages and time per post; a failing
/// page (404, TLS, a block) costs only itself, and a blocked host is left for the rest of the crawl.
/// </summary>
public sealed class VacancyCrawler(PoliteHttp http, PageReader reader, LinkPolicy policy, EnrichmentOptions options, ILogger<VacancyCrawler> log,
    IPageRenderer? renderer = null)
{
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(20);

    /// <param name="self">The post's own address: never read back (a channel links its posts to each other).</param>
    public async Task<CrawlResult> CrawlAsync(IEnumerable<PostLink> links, Uri? self, CancellationToken ct)
    {
        if (!options.Enabled) return CrawlResult.Empty;
        using var stage = Core.Telemetry.Telemetry.StartStage("crawl", "enrich");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(options.BudgetSeconds));

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (self is not null) visited.Add(Key(self));
        var queue = new Queue<(LinkDecision Link, int Depth, string? Via)>();
        foreach (var link in links)
        {
            var d = policy.Classify(link.Url, link.Label);
            if (d.Role is LinkRole.Page or LinkRole.TelegramPost && visited.Add(Key(d.Url!))) queue.Enqueue((d, 1, link.Label));
        }

        var pages = new List<CrawledPage>();
        var failures = new List<string>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0 && pages.Count < options.MaxPages && !budget.IsCancellationRequested)
        {
            var (link, depth, via) = queue.Dequeue();
            var target = link.Fetch ?? link.Url!;
            if (blocked.Contains(PoliteHttp.Family(target.Host))) continue;
            try
            {
                if (await ReadAsync(link, budget.Token) is not { } read) continue;
                visited.Add(Key(read.Page.Url));
                pages.Add(new CrawledPage(read.Page, depth, via, read.Rendered, read.ViaProxy));
                if (depth >= options.MaxDepth) continue;

                var isVacancy = read.Page.JobPosting is not null || LinkPolicy.IsVacancyPath(read.Page.Url);
                foreach (var next in read.Page.Links)
                {
                    var d = policy.Classify(next.Url, next.Label);
                    if (d.Role is not (LinkRole.Page or LinkRole.TelegramPost)) continue;
                    // A linked Telegram post is the post's own content elsewhere (a repost): its links count like the post's.
                    var follow = link.Role == LinkRole.TelegramPost || policy.LeadsToDetails(read.Page.Url, d.Url!, next.Label, isVacancy);
                    if (follow && visited.Add(Key(d.Url!))) queue.Enqueue((d, depth + 1, next.Label));
                }
            }
            catch (SourceBlockedException e)
            {
                blocked.Add(PoliteHttp.Family(target.Host));
                failures.Add($"{target.Host}: {e.Message}");
            }
            catch (Exception e) when (IsPageFailure(e) && !ct.IsCancellationRequested)
            {
                if (e is HttpRequestException { StatusCode: HttpStatusCode.NotFound }) missing.Add(Key(link.Url!));
                failures.Add($"{link.Url}: {Describe(e)}");
                log.Debug($"[enrich] {link.Url}: {Describe(e)}");
            }
        }

        if (budget.IsCancellationRequested && !ct.IsCancellationRequested) failures.Add($"time budget of {options.BudgetSeconds} s used up");
        stage.Tag("pages", pages.Count).Tag("failures", failures.Count).Tag("rendered", pages.Count(p => p.Rendered)).Tag("proxied", pages.Count(p => p.ViaProxy));
        return new CrawlResult(pages, failures, missing);
    }

    private async Task<(WebPage Page, bool Rendered, bool ViaProxy)?> ReadAsync(LinkDecision link, CancellationToken ct)
    {
        var fetched = await http.GetPageAsync(link.Fetch!, ct, maxWait: SlotWait);
        var textual = fetched.IsHtml || fetched.MediaType?.StartsWith("text/plain", StringComparison.Ordinal) == true;
        if (!textual || fetched.Body.Length == 0) return null;

        // A Telegram embed or a Google Doc export is read at its public address; a page that redirected is known by where it ended up.
        var rewritten = LinkPolicy.Canonical(link.Fetch!) != link.Url;
        var url = link.Role == LinkRole.Page && !rewritten ? LinkPolicy.Canonical(fetched.FinalUri) : link.Url!;
        var page = reader.Read(url, fetched.Body, fetched.MediaType);
        var rendered = false;
        if (renderer is not null && options.RenderScripts && fetched.IsHtml && link.Role == LinkRole.Page && page.Text.Length < PageReader.ShortText)
        {
            // Nearly empty as HTML: a script-built page. The browser takes the same route the request did (direct or the proxy).
            var (final, html) = await renderer.RenderAsync(fetched.FinalUri, http.ProxyFor(fetched.FinalUri.Host), ct);
            var drawn = reader.Read(LinkPolicy.Canonical(final), html, "text/html");
            if (drawn.Text.Length > page.Text.Length) (page, rendered) = (drawn, true);
        }

        // A retired vacancy that still answers 200 is no content: the crawl notes it and goes on.
        if (PageReader.IsNotFound(page)) throw new HttpRequestException($"not found ({page.Url.AbsolutePath}{(rendered ? " after the scripts ran" : "")})", null, HttpStatusCode.NotFound);
        var text = page.Text.Length > options.MaxPageChars ? page.Text[..options.MaxPageChars] + "…" : page.Text;
        return (page with { Text = text }, rendered, fetched.ViaProxy);
    }

    private static string Key(Uri url) => LinkPolicy.Canonical(url).AbsoluteUri;

    /// <summary>
    /// What one page may fail with without failing the post: HTTP errors, the route, the time budget, the browser. Nothing broader: a bug
    /// must surface, not pass for an unreachable page.
    /// </summary>
    private static bool IsPageFailure(Exception e) =>
        e is HttpRequestException or OperationCanceledException or Polly.Timeout.TimeoutRejectedException or Microsoft.Playwright.PlaywrightException
            or TimeoutException or IOException;

    private static string Describe(Exception e) => e switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "TLS: " + (e.InnerException?.Message ?? e.Message),
        HttpRequestException { StatusCode: { } status } when e.Message.StartsWith("not found", StringComparison.Ordinal) => $"HTTP {(int)status}: {e.Message}",
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        OperationCanceledException => "time budget used up",
        _ => e.Message.Split('\n')[0],
    };
}
