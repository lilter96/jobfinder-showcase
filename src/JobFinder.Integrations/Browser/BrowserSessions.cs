using JobFinder.Core.Storage;
using Microsoft.Playwright;

namespace JobFinder.Integrations.Browser;

public sealed class BrowserOptions
{
    public const string Section = "Browser";

    public bool Headless { get; set; } = true;

    public string ScreenshotDir { get; set; } = "data/screenshots";

    public string Locale { get; set; } = "ru-RU";

    public string TimezoneId { get; set; } = "Europe/Minsk";

    public int TimeoutSec { get; set; } = 30;

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    /// <summary>
    /// The cookie that is the login, per site. A run after which it is gone (or turned into a session cookie) does not overwrite the
    /// saved state: a logged-out page must not destroy the stored login.
    /// </summary>
    public Dictionary<string, string> SessionCookies { get; set; } = new()
    {
        ["hh"] = "hhtoken",
        ["linkedin"] = "li_at",
        ["threads"] = "sessionid",
    };
}

/// <summary>
/// One browser per site across processes (the server and a CLI command in another container share the same login): two browsers on one
/// session look like two devices at once to the site. Implemented over the database (advisory lock).
/// </summary>
public interface IBrowserSiteLock
{
    Task<IAsyncDisposable> AcquireAsync(string site, CancellationToken ct);
}

/// <summary>
/// One Chromium for the whole process, one context at a time (a single person does not click in two tabs at once, and it keeps the
/// memory flat). Each site's login is a Playwright storage state (cookies + local storage) in the document <c>browser.{site}</c>, created
/// by the interactive <c>--login &lt;site&gt;</c> command on the host; every run loads it and writes the refreshed cookies back.
/// </summary>
public sealed class BrowserSessions(BrowserOptions options, IDocumentStore documents, ILogger<BrowserSessions> log, IBrowserSiteLock? siteLock = null)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public static string StateKey(string site) => "browser." + site;

    public async Task<bool> HasSessionAsync(string site, CancellationToken ct) => await documents.VersionAsync(StateKey(site), ct) is not null;

    public async Task<T> RunAsync<T>(string site, Func<IPage, Task<T>> work, CancellationToken ct)
    {
        using var span = Core.Telemetry.Telemetry.Start($"browser {site}");
        span?.SetTag("browser.site", site);
        await using var crossProcess = siteLock is null ? null : await siteLock.AcquireAsync(site, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var browser = await EnsureBrowserAsync();
            var saved = await documents.GetAsync(StateKey(site), ct);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                StorageState = saved?.Json,
                Locale = options.Locale,
                TimezoneId = options.TimezoneId,
                UserAgent = options.UserAgent,
                ViewportSize = new ViewportSize { Width = 1366, Height = 850 },
            });
            context.SetDefaultTimeout(options.TimeoutSec * 1000);
            var page = await context.NewPageAsync();
            try
            {
                return await work(page);
            }
            finally
            {
                // A run without a stored login leaves nothing to refresh; a cancelled run still keeps its refreshed cookies. A failed save
                // (the page is closed, the database is away) must not turn the finished work into a failure.
                if (saved is not null)
                {
                    try
                    {
                        if (await StillLoggedInAsync(site, context))
                            await documents.SaveAsync(StateKey(site), await context.StorageStateAsync(), CancellationToken.None);
                        else
                            log.Warn($"[browser] {site}: the login cookie is gone after this run — the saved login is kept, not overwritten");
                    }
                    catch (Exception e)
                    {
                        log.Warn($"[browser] {site}: storage state not saved: {e.Message}");
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The site's login cookie is present and persistent (a session-only one is what a logout leaves behind).</summary>
    private async Task<bool> StillLoggedInAsync(string site, IBrowserContext context)
    {
        if (!options.SessionCookies.TryGetValue(site, out var name)) return true;
        var cookies = await context.CookiesAsync();
        return cookies.Any(c => c.Name == name && c.Expires > 0);
    }

    /// <summary>
    /// A public page as a browser draws it, for sites whose content comes from their scripts (a Gatsby/React career page ships an empty
    /// shell). A clean context (no site session: nobody's account reads it), images, fonts and media not loaded, the scripts given until
    /// the network is quiet (within the timeout).
    /// </summary>
    /// <param name="proxy">An HTTP proxy for a site the direct route cannot reach; null = direct.</param>
    /// <returns>Where the page ended up and its HTML after the scripts ran.</returns>
    public async Task<(Uri Url, string Html)> RenderAsync(Uri url, string? proxy, CancellationToken ct)
    {
        using var span = Core.Telemetry.Telemetry.Start("browser render");
        span?.SetTag("url.host", url.Host);
        await _gate.WaitAsync(ct);
        try
        {
            var browser = await EnsureBrowserAsync();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = options.Locale,
                TimezoneId = options.TimezoneId,
                UserAgent = options.UserAgent,
                Proxy = string.IsNullOrWhiteSpace(proxy) ? null : new Proxy { Server = proxy },
            });
            context.SetDefaultTimeout(options.TimeoutSec * 1000);
            await context.RouteAsync("**/*", route =>
                route.Request.ResourceType is "image" or "font" or "media" ? route.AbortAsync() : route.ContinueAsync());
            var page = await context.NewPageAsync();
            await page.GotoAsync(url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15000 });
            }
            catch (TimeoutException)
            {
                // A page that polls forever is never idle: what is drawn by now is read.
            }

            return (new Uri(page.Url), await page.ContentAsync());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// HTML → A4 PDF in a clean context (no site session). The document carries its own fonts, so the host and the container produce
    /// the same file; the text stays real text (an ATS reads it, nothing is rasterized).
    /// </summary>
    public async Task RenderPdfAsync(string html, string outputPath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var browser = await EnsureBrowserAsync();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = options.Locale });
            var page = await context.NewPageAsync();
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });
            await page.EvaluateAsync("() => document.fonts.ready");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await page.PdfAsync(new PagePdfOptions
            {
                Path = outputPath,
                Format = "A4",
                PrintBackground = true,
                PreferCSSPageSize = true,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> ScreenshotAsync(IPage page, string name)
    {
        try
        {
            Directory.CreateDirectory(options.ScreenshotDir);
            var path = Path.GetFullPath(Path.Combine(options.ScreenshotDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{name}.png"));
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = false });
            return path;
        }
        catch (PlaywrightException e)
        {
            log.Warn($"[browser] screenshot failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Opens a visible browser on <paramref name="url"/>, lets the user log in by hand, and saves the session once
    /// <paramref name="isLoggedIn"/> says so (or the user presses Enter). Runs on the host, never in the container.
    /// </summary>
    public async Task<bool> LoginInteractiveAsync(string site, string url, Func<IPage, Task<bool>> isLoggedIn, TimeSpan timeout,
        CancellationToken ct)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = options.Locale,
            TimezoneId = options.TimezoneId,
            UserAgent = options.UserAgent,
            ViewportSize = new ViewportSize { Width = 1280, Height = 860 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(url);

        // Enter confirms by hand. Without a console (launched by a tool, stdin closed) ReadLine returns at once: only the check counts.
        var enter = Console.IsInputRedirected ? new TaskCompletionSource<string?>().Task : Task.Run(Console.ReadLine, ct);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var current = context.Pages.LastOrDefault() ?? page;
            var pressed = enter.IsCompleted && enter.Result is not null;
            if (pressed || await SafeCheckAsync(isLoggedIn, current))
            {
                await documents.SaveAsync(StateKey(site), await context.StorageStateAsync(), ct);
                return await SafeCheckAsync(isLoggedIn, current) || pressed;
            }

            await Task.Delay(2000, ct);
        }

        return false;
    }

    public static int InstallBrowsers(bool withDeps) =>
        Microsoft.Playwright.Program.Main(withDeps ? ["install", "--with-deps", "chromium"] : ["install", "chromium"]);

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is { IsConnected: true }) return _browser;
        _playwright ??= await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Args = ["--disable-blink-features=AutomationControlled"],
        });
        return _browser;
    }

    private static async Task<bool> SafeCheckAsync(Func<IPage, Task<bool>> check, IPage page)
    {
        try
        {
            return await check(page);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }
}
