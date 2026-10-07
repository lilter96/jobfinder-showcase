using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JobFinder.Integrations.Browser;
using JobFinder.Integrations.HeadHunter;
using Microsoft.Playwright;

namespace JobFinder.Integrations.Threads;

public sealed class ThreadsOptions
{
    public const string Section = "Threads";

    public bool Enabled { get; set; } = true;

    /// <summary>Auto = official API when <see cref="AccessToken"/> is set, the browser otherwise; Api / Browser force one.</summary>
    public string Mode { get; set; } = "Auto";

    /// <summary>Long-lived token with threads_basic, threads_keyword_search, threads_content_publish, threads_manage_replies.</summary>
    public string AccessToken { get; set; } = "";

    public string ApiBase { get; set; } = "https://graph.threads.net/v1.0";

    /// <summary>Searches for vacancy posts (they go through the same extraction as Telegram posts).</summary>
    public List<string> VacancyQueries { get; set; } = [];

    /// <summary>Searches for posts of the target audience (HR, hiring managers, .NET topics) to comment on.</summary>
    public List<string> AudienceQueries { get; set; } = [];

    public int IntervalMinutes { get; set; } = 60;

    public int JitterMinutes { get; set; } = 20;

    public int ResultsPerQuery { get; set; } = 25;

    public int MaxPostAgeHours { get; set; } = 48;

    public int MaxDraftsPerDay { get; set; } = 10;

    public int AuthorCooldownDays { get; set; } = 7;

    /// <summary>
    /// Comments go out without the user: the first variant that passed the style checks is published, the bot reports it afterwards.
    /// Off: every draft waits for a pick in the bot. <c>/comments off</c> stops both.
    /// </summary>
    public bool AutoPublish { get; set; } = true;

    public ThreadsSelectors Selectors { get; set; } = new();
}

public sealed class ThreadsSelectors
{
    public string ReplyButton { get; set; } = "svg[aria-label='Ответить'], svg[aria-label='Reply']";

    public string Composer { get; set; } = "div[contenteditable='true'][role='textbox']";

    /// <summary>The send arrow of the inline reply box (svg "Ответ"/"Reply", shown once there is text); the text buttons of the modal composer as a fallback.</summary>
    public string PostButton { get; set; } =
        "div[role='button']:has(svg[aria-label='Ответ']), div[role='button']:has(svg[aria-label='Reply']), " +
        "div[role='button']:has-text('Опубликовать'), div[role='button']:has-text('Post')";
}

public sealed record ThreadsPost(string Id, string Username, string Text, DateTimeOffset? PublishedAt, string Url);

public interface IThreadsClient
{
    string Name { get; }

    Task<List<ThreadsPost>> SearchRecentAsync(string query, int limit, CancellationToken ct);

}

/// <summary>graph.threads.net: keyword_search (needs App Review for other people's posts) and the two-step reply publish.</summary>
public sealed partial class ThreadsApiClient(HttpClient http, ThreadsOptions options) : IThreadsClient
{
    public string Name => "api";

    public async Task<List<ThreadsPost>> SearchRecentAsync(string query, int limit, CancellationToken ct)
    {
        var url = $"{options.ApiBase}/keyword_search?q={Uri.EscapeDataString(query)}&search_type=RECENT&limit={limit}" +
                  $"&fields=id,text,permalink,timestamp,username&access_token={Uri.EscapeDataString(options.AccessToken)}";
        var node = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        return (node["data"]?.AsArray() ?? [])
            .Where(p => p is not null)
            .Select(p => new ThreadsPost(
                HhStateParser.Str(p!["id"]) ?? "",
                HhStateParser.Str(p["username"]) ?? "",
                HhStateParser.Str(p["text"]) ?? "",
                HhStateParser.Str(p["timestamp"]) is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                HhStateParser.Str(p["permalink"]) ?? ""))
            .Where(p => p.Id.Length > 0 && p.Text.Length > 0)
            .ToList();
    }

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await http.SendAsync(request, ct))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                throw new HttpRequestException($"Threads API {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
            }

            if (!response.IsSuccessStatusCode || node?["error"] is not null)
                throw new HttpRequestException($"Threads API {(int)response.StatusCode}: {node?["error"]?["message"] ?? body[..Math.Min(body.Length, 200)]}");
            return node!;
        }
    }
}

/// <summary>threads.com under the saved login (<c>--login threads</c>): search page scraping and replying through the post UI.</summary>
public sealed partial class ThreadsBrowserClient(BrowserSessions browser, ThreadsOptions options, ILogger<ThreadsBrowserClient> log) : IThreadsClient
{
    public const string Site = "threads";

    // Every post on a search page is a link /@user/post/CODE; the text is the innerText of its card, cleaned below.
    private const string ExtractScript = """
        () => {
          const out = []; const seen = new Set();
          for (const a of document.querySelectorAll('a[href*="/post/"]')) {
            const href = a.getAttribute('href') || '';
            const m = href.match(/^\/@([^/]+)\/post\/([^/?#]+)/);
            if (!m || seen.has(m[2])) continue;
            const card = a.closest('[data-pressable-container]') || a.closest('div[class]')?.parentElement?.parentElement;
            const time = a.querySelector('time')?.getAttribute('datetime') || card?.querySelector('time')?.getAttribute('datetime') || null;
            seen.add(m[2]);
            out.push({ username: m[1], code: m[2], url: 'https://www.threads.com' + href.split('?')[0], time, text: card ? card.innerText : '' });
          }
          return JSON.stringify(out);
        }
        """;

    /// <summary>Each post link with the chain of its ancestors (tag, attributes, text): to find the card boundary on the live page.</summary>
    private const string DiagnoseScript = """
        () => JSON.stringify([...document.querySelectorAll('a[href*="/post/"]')].slice(0, 30).map(a => {
          const chain = []; let e = a;
          for (let i = 0; i < 12 && e; i++, e = e.parentElement) {
            chain.push({ tag: e.tagName, attrs: [...e.attributes].map(x => x.name).filter(n => !n.startsWith('class') && n !== 'style').join(','),
                         posts: e.querySelectorAll('a[href*="/post/"]').length, len: (e.innerText || '').length, text: (e.innerText || '').slice(0, 300) });
          }
          return { href: a.getAttribute('href'), text: (a.innerText || '').slice(0, 200), time: a.querySelector('time')?.getAttribute('datetime') || null, chain };
        }), null, 1)
        """;

    public string Name => "browser";

    public Task<bool> HasSessionAsync(CancellationToken ct) => browser.HasSessionAsync(Site, ct);

    public Task<List<ThreadsPost>> SearchRecentAsync(string query, int limit, CancellationToken ct) => SearchRecentAsync(query, limit, ct, null);

    /// <param name="diagnosePath">When set, the post-link structure of the page is written there (CLI check only).</param>
    public Task<List<ThreadsPost>> SearchRecentAsync(string query, int limit, CancellationToken ct, string? diagnosePath) =>
        browser.RunAsync(Site, async page =>
        {
            await page.GotoAsync($"https://www.threads.com/search?q={Uri.EscapeDataString(query)}&serp_type=default&filter=recent",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForTimeoutAsync(Random.Shared.Next(2500, 4500));
            if (diagnosePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(diagnosePath))!);
                await File.WriteAllTextAsync(diagnosePath, await page.EvaluateAsync<string>(DiagnoseScript), ct);
            }

            // The feed is virtualized (data-virtualized): scrolled-past posts leave the DOM, so posts are collected after every step.
            var collected = new Dictionary<string, ThreadsPost>();
            for (var i = 0; ; i++)
            {
                foreach (var post in Parse(await page.EvaluateAsync<string>(ExtractScript))) collected.TryAdd(post.Id, post);
                if (i == 4 || collected.Count >= limit) break;
                await page.Mouse.WheelAsync(0, 2500);
                await page.WaitForTimeoutAsync(Random.Shared.Next(1200, 2200));
            }

            var posts = collected.Values.OrderByDescending(p => p.PublishedAt).Take(limit).ToList();
            if (posts.Count == 0) log.Warn($"[threads] search \"{query}\": no posts on the page (logged out or a layout change?)");
            return posts;
        }, ct);

    /// <summary>
    /// Dry run of a reply on a live post: finds the reply control and opens the composer, then closes it — nothing is typed or posted.
    /// Writes the page's labelled controls to <paramref name="dumpPath"/> so the selectors can be fixed from facts.
    /// </summary>
    public Task<string> CheckReplyAsync(string postUrl, string dumpPath, CancellationToken ct) =>
        browser.RunAsync(Site, async page =>
        {
            var s = options.Selectors;
            await page.GotoAsync(postUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForTimeoutAsync(4000);
            var controls = await page.EvaluateAsync<string>("""
                () => JSON.stringify([...document.querySelectorAll('[aria-label], [role="button"]')].slice(0, 120).map(e => ({
                  tag: e.tagName, label: e.getAttribute('aria-label'), role: e.getAttribute('role'),
                  text: (e.innerText || '').trim().slice(0, 40), inArticle: !!e.closest('[data-pressable-container]') })), null, 1)
                """);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dumpPath))!);
            await File.WriteAllTextAsync(dumpPath, controls, ct);
            var report = new System.Text.StringBuilder();
            report.Append($"встроенное поле ответа: {await page.Locator(s.Composer).CountAsync()}, кнопка «Ответить»: {await page.Locator(s.ReplyButton).CountAsync()}");
            ILocator composer;
            try
            {
                composer = await OpenComposerAsync(page, s);
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException)
            {
                return report.Append("; ✗ ").Append(e.Message).ToString();
            }

            // Which post the box answers: the text of the card around it, and its placeholder.
            var context = await composer.EvaluateAsync<string>(
                "e => { const c = e.closest('[data-pressable-container]') || e.parentElement; const ph = e.getAttribute('aria-placeholder') || e.getAttribute('data-placeholder') || ''; return ph + ' | ' + ((c && c.innerText) || '').replace(/\\s+/g, ' ').slice(0, 160); }");
            report.Append($"; ✓ поле открывается; рядом: «{context}»");
            await composer.ClickAsync();
            // The publish button shows up only with text: type a word, count it, erase — never pressed, Enter is never sent.
            await page.Keyboard.TypeAsync("проверка", new KeyboardTypeOptions { Delay = 40 });
            await page.WaitForTimeoutAsync(1200);
            var publish = page.Locator(s.PostButton);
            report.Append($"; кнопка публикации с текстом ({s.PostButton}): {await publish.CountAsync()}");
            if (await publish.CountAsync() > 0) report.Append($", видима: {await publish.Last.IsVisibleAsync()}, текст «{(await publish.Last.InnerTextAsync()).Trim()}»");
            // The controls around the box with text in it: where the publish control is and how it is labelled.
            var around = await composer.EvaluateAsync<string>("""
                e => { let c = e; for (let i = 0; i < 8 && c.parentElement; i++) c = c.parentElement;
                  return JSON.stringify([...c.querySelectorAll('[role="button"], button, [aria-label]')].map(b => ({ tag: b.tagName, role: b.getAttribute('role'),
                    label: b.getAttribute('aria-label'), text: (b.innerText || '').trim().slice(0, 30), disabled: b.getAttribute('aria-disabled') }))); }
                """);
            await File.WriteAllTextAsync(Path.ChangeExtension(dumpPath, ".composer.json"), around, ct);
            await browser.ScreenshotAsync(page, "threads-reply-check");
            await page.Keyboard.PressAsync("Control+A");
            await page.Keyboard.PressAsync("Backspace");
            await page.WaitForTimeoutAsync(500);
            report.Append($"; поле очищено: {(await composer.InnerTextAsync()).Trim().Length == 0}");
            await page.Keyboard.PressAsync("Escape");
            return report.ToString();
        }, ct);

    /// <summary>Read-only: whether the post shows a reply with this text.</summary>
    public async Task<bool> HasReplyAsync(string postUrl, string text, CancellationToken ct)
    {
        if (!PostUrl(postUrl) || !await HasSessionAsync(ct)) return false;
        var parts = new Uri(postUrl).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var target = new ThreadsPost(parts[2], parts[0][1..], "", null, postUrl);
        return (await browser.RunAsync(Site, page => PrepareOnPageAsync(page, target, text, null, ct), ct)).State == ThreadsPreparationState.Unknown;
    }

    /// <summary>The start of the reply as the page shows it: whitespace collapsed, cut on a word (emoji and the tail can render differently).</summary>
    public static string Snippet(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        if (flat.Length <= 40) return flat;
        var cut = flat[..40];
        var space = cut.LastIndexOf(' ');
        return space > 20 ? cut[..space] : cut;
    }

    /// <summary>
    /// The post page (2026-09) has an inline reply box under the focused post; the action icons lost their aria-labels, so the reply
    /// control is only a fallback for layouts without the box.
    /// </summary>
    private static async Task<ILocator> OpenComposerAsync(IPage page, ThreadsSelectors s)
    {
        var composer = page.Locator(s.Composer).First;
        if (await page.Locator(s.Composer).CountAsync() == 0)
        {
            var reply = page.Locator(s.ReplyButton);
            if (await reply.CountAsync() == 0)
                throw new InvalidOperationException("на странице поста нет поля ответа и кнопки «Ответить» (изменилась вёрстка): --threads-reply-check");
            await reply.First.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
        }

        await composer.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        return composer;
    }

    internal static IEnumerable<ThreadsPost> Parse(string json)
    {
        foreach (var item in JsonNode.Parse(json)?.AsArray() ?? [])
        {
            if (item is null) continue;
            var username = HhStateParser.Str(item["username"]) ?? "";
            var code = HhStateParser.Str(item["code"]) ?? "";
            var text = Clean(HhStateParser.Str(item["text"]) ?? "", username);
            if (code.Length == 0 || text.Length < 20) continue;
            DateTimeOffset? time = HhStateParser.Str(item["time"]) is { } t
                                   && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            yield return new ThreadsPost(code, username, text, time, HhStateParser.Str(item["url"]) ?? "");
        }
    }

    /// <summary>A card's innerText is "username\n2 ч\npost text\nLike count…": drop the chrome lines, keep the post.</summary>
    internal static string Clean(string cardText, string username)
    {
        var lines = cardText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l != username && !Chrome().IsMatch(l));
        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@"^(\d+([.,]\d+)?\s*(тыс\.?|K|M)?|\d+\s*(с|мин|ч|д|нед|s|m|h|d|w)\.?|Перевести|Translate|Ещё|More|Автор|Author|·)$", RegexOptions.IgnoreCase)]
    private static partial Regex Chrome();
}

/// <summary>Chooses the client per <see cref="ThreadsOptions.Mode"/>; in Auto mode an API failure falls back to the browser.</summary>
public sealed class ThreadsGateway(ThreadsApiClient api, ThreadsBrowserClient web, ThreadsOptions options, ILogger<ThreadsGateway> log)
{
    public bool UsesApi => options.Mode.Equals("Api", StringComparison.OrdinalIgnoreCase)
                           || options.Mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(options.AccessToken);

    public async Task<bool> IsConfiguredAsync(CancellationToken ct) => UsesApi || await web.HasSessionAsync(ct);

    public async Task<List<ThreadsPost>> SearchRecentAsync(string query, CancellationToken ct, int? resultsPerQuery = null)
    {
        var limit = resultsPerQuery ?? options.ResultsPerQuery;
        if (!UsesApi) return await web.SearchRecentAsync(query, limit, ct);
        try
        {
            return await api.SearchRecentAsync(query, limit, ct);
        }
        catch (HttpRequestException e) when (AutoMode)
        {
            if (!await web.HasSessionAsync(ct)) throw;
            log.Warn($"[threads] API search failed ({e.Message}), using the browser");
            return await web.SearchRecentAsync(query, limit, ct);
        }
    }

    /// <summary>Auto mode: an API failure falls back to the browser when it has a login.</summary>
    private bool AutoMode => options.Mode.Equals("Auto", StringComparison.OrdinalIgnoreCase);
}
