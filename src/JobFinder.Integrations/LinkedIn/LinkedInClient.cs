using System.Text.Json.Nodes;
using JobFinder.Integrations.Browser;
using JobFinder.Integrations.HeadHunter;
using Microsoft.Playwright;

namespace JobFinder.Integrations.LinkedIn;

public sealed class LinkedInOptions
{
    public const string Section = "LinkedIn";

    public bool Enabled { get; set; } = true;

    /// <summary>People searches per local day: LinkedIn restricts accounts that search like a script.</summary>
    public int MaxSearchesPerDay { get; set; } = 10;
}

/// <param name="Details">All text lines of the result card ("Текущая: Recruiter в EPAM Systems", location): the current employer is here
/// even when the headline does not name it.</param>
public sealed record LinkedInPerson(string Name, string Headline, string ProfileUrl, string Details = "");

/// <summary>A profile link of a result page and the text of its card (the largest container around it without other profiles).</summary>
public sealed record LinkedInCard(string ProfileUrl, string Text);

/// <summary>One search page: the raw cards (for the model) and the rule-based reading (the fallback when no model answers).</summary>
public sealed record LinkedInSearchPage(List<LinkedInCard> Cards, List<LinkedInPerson> Parsed);

/// <summary>
/// People search under the user's LinkedIn login (<c>--login linkedin</c>). Search only: messages and connection requests are sent by the
/// user from the link the bot gives — LinkedIn bans message automation.
/// </summary>
public sealed class LinkedInClient(BrowserSessions browser, ILogger<LinkedInClient> log)
{
    public const string Site = "linkedin";

    // Result markup changes often and its classes are obfuscated; profile links (/in/…) and their text lines are what stays. Checked on
    // the live page 2026-09-27 (data/debug/linkedin-search.json from --linkedin-search): each result is ONE link spanning the whole card
    // (name, "• 2-й", headline, location…); a second link holds just the name; the mutual-connections insight links hold just names.
    private const string ExtractScript = """
        () => {
          const skip = /mutual connection|общ(ий|их|ие) контакт|^(connect|message|follow|pending|установить контакт|сообщение|отслеживать|подписаться)$|^•|^(1st|2nd|3rd\+?|1-й|2-й|3-й\+?)$|degree connection|^view .*profile|^посмотреть|^status is/i;
          const clean = s => s.replace(/\s*•.*$/, '').trim();
          const people = new Map();
          const add = (url, name, headline, lines) => {
            if (!url.includes('/in/') || people.has(url) || !name || skip.test(name) || /linkedin member|участник linkedin/i.test(name)) return;
            if (headline) people.set(url, { name, headline, url, details: lines.filter(l => !skip.test(l)).join(' | ').slice(0, 600) });
          };
          const links = [...document.querySelectorAll('a[href*="linkedin.com/in/"], a[href^="/in/"]')];
          // Current layout: the card is the link. Name-only links (the name line, mutual connections) have fewer than three lines.
          for (const a of links) {
            const lines = (a.innerText || '').split('\n').map(s => s.trim()).filter(Boolean);
            if (lines.length < 3) continue;
            const name = clean(lines[0]);
            add((a.href || '').split('?')[0], name, lines.slice(1).find(l => l !== name && !skip.test(l)) || '', lines);
          }
          // Older layout: the card is a list item, its first link is the person, later links are mutual connections.
          if (people.size === 0) {
            const cards = new Set();
            for (const a of links) {
              const card = a.closest('li');
              if (!card || cards.has(card)) continue;
              cards.add(card);
              const lines = card.innerText.split('\n').map(s => s.trim()).filter(s => s && s.length < 200);
              const name = clean((a.innerText || '').split('\n')[0] || lines.find(l => !skip.test(l)) || '');
              add((a.href || '').split('?')[0], name, lines.find(l => l !== name && !l.startsWith(name) && !skip.test(l)) || '', lines);
            }
          }
          return JSON.stringify([...people.values()].slice(0, 10));
        }
        """;

    /// <summary>What the extraction sees: each profile link with its own, its parent's and its card's text (for fixing the rules).</summary>
    private const string DiagnoseScript = """
        () => JSON.stringify([...document.querySelectorAll('a[href*="linkedin.com/in/"], a[href^="/in/"]')].slice(0, 40).map(a => {
          const card = a.closest('li') || a.closest('div[data-view-name]') || a.parentElement;
          const t = el => ((el && el.innerText) || '').slice(0, 400);
          return { href: (a.href || '').split('?')[0], link: t(a), parentTag: a.parentElement && a.parentElement.tagName, parent: t(a.parentElement),
                   cardTag: card && card.tagName, cardDepth: (() => { let d = 0, e = a; while (e && e !== card) { d++; e = e.parentElement; } return d; })(), card: t(card) };
        }), null, 1)
        """;

    /// <summary>
    /// Layout-free blocks for the model: every profile URL with the text of the largest ancestor that links to no other profile — the
    /// whole result card for a result, just the name for a mutual connection (its container lists several people).
    /// </summary>
    private const string CardsScript = """
        () => {
          const key = a => (a.href || '').split('?')[0].replace(/\/$/, '');
          const links = [...document.querySelectorAll('a[href*="linkedin.com/in/"], a[href^="/in/"]')].filter(a => key(a).includes('/in/'));
          const cards = new Map();
          for (const a of links) {
            const url = key(a);
            let best = a;
            for (let e = a.parentElement, i = 0; e && i < 10; e = e.parentElement, i++) {
              const urls = new Set([...e.querySelectorAll('a[href*="/in/"]')].map(key));
              if (urls.size > 1) break;
              best = e;
            }
            const text = (best.innerText || '').trim();
            if (text && (!cards.has(url) || text.length > cards.get(url).length)) cards.set(url, text);
          }
          return JSON.stringify([...cards].slice(0, 25).map(([url, text]) => ({ url, text: text.slice(0, 900) })));
        }
        """;

    public Task<bool> HasSessionAsync(CancellationToken ct) => browser.HasSessionAsync(Site, ct);

    /// <param name="diagnosePath">When set, the link structure of the page is written there and the cards next to it (CLI check only).</param>
    public Task<LinkedInSearchPage> SearchAsync(string keywords, CancellationToken ct, string? diagnosePath = null) =>
        browser.RunAsync(Site, async page =>
        {
            var url = $"https://www.linkedin.com/search/results/people/?keywords={Uri.EscapeDataString(keywords)}&origin=GLOBAL_SEARCH_HEADER";
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForTimeoutAsync(Random.Shared.Next(3500, 6000));
            if (page.Url.Contains("/login", StringComparison.Ordinal) || page.Url.Contains("/checkpoint", StringComparison.Ordinal))
                throw new InvalidOperationException("LinkedIn: сессия истекла или проверка безопасности — --login linkedin");
            await page.Mouse.WheelAsync(0, 1500);
            await page.WaitForTimeoutAsync(Random.Shared.Next(1500, 2500));
            if (diagnosePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(diagnosePath))!);
                await File.WriteAllTextAsync(diagnosePath, await page.EvaluateAsync<string>(DiagnoseScript), ct);
            }

            var cardsJson = await page.EvaluateAsync<string>(CardsScript);
            if (diagnosePath is not null) await File.WriteAllTextAsync(Path.ChangeExtension(diagnosePath, ".cards.json"), cardsJson, ct);
            var cards = ParseCards(cardsJson);
            var json = await page.EvaluateAsync<string>(ExtractScript);
            var people = (JsonNode.Parse(json)?.AsArray() ?? [])
                .Select(n => new LinkedInPerson(HhStateParser.Str(n?["name"]) ?? "", HhStateParser.Str(n?["headline"]) ?? "", HhStateParser.Str(n?["url"]) ?? "",
                    HhStateParser.Str(n?["details"]) ?? ""))
                .Where(p => p.Name.Length > 0 && p.ProfileUrl.Length > 0)
                .ToList();
            if (cards.Count == 0) log.Warn($"[linkedin] \"{keywords}\": no profile links on the page (layout change or empty result)");
            return new LinkedInSearchPage(cards, people);
        }, ct);

    public static List<LinkedInCard> ParseCards(string json) =>
        (JsonNode.Parse(json)?.AsArray() ?? [])
        .Select(n => new LinkedInCard(HhStateParser.Str(n?["url"]) ?? "", HhStateParser.Str(n?["text"]) ?? ""))
        .Where(c => c.ProfileUrl.Contains("/in/", StringComparison.Ordinal) && c.Text.Length > 0)
        .ToList();
}
