using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using JobFinder.Core.Domain;

namespace JobFinder.Integrations.Web;

/// <summary>schema.org JobPosting of a page (JSON-LD): the structured facts career sites and aggregators publish for search engines.</summary>
public sealed record JobPostingData(
    string? Title,
    string? Company,
    string? Url,
    decimal? SalaryFrom,
    decimal? SalaryTo,
    string? Currency,
    bool Remote,
    string? EmploymentType,
    DateTimeOffset? DatePosted,
    string? Description,
    IReadOnlyList<VacancyContact> Contacts);

/// <summary>A read page: its main text (no menus, footers, ads), the links on it with their text, and its JobPosting when it has one.</summary>
public sealed record WebPage(Uri Url, string? Title, string Text, IReadOnlyList<PostLink> Links, JobPostingData? JobPosting);

/// <summary>
/// HTML → <see cref="WebPage"/> on standard parts: AngleSharp parses (HTML5-conformant, like a browser), SmartReader (Mozilla Readability)
/// finds the main content, and the JobPosting comes from the page's JSON-LD. A few sites whose content node is known are read by it
/// (a Telegram post embed, a telegra.ph article) — Readability's guess is not needed there. Pure: no network.
/// </summary>
public sealed partial class PageReader
{
    /// <summary>A known site: where its content is, and whether links outside it are chrome.</summary>
    private sealed record Site(Func<Uri, bool> Matches, string Content, string? Extra = null);

    private static readonly Site[] Sites =
    [
        // The web embed of a Telegram post: the message text; its URL buttons sit in the inline keyboard.
        new(u => u.Host is "t.me" or "telegram.me", ".tgme_widget_message_text", ".tgme_widget_message_inline_button, .tgme_widget_message_inline_row a"),
        // Telegraph: the article; the author link (often the recruiter's Telegram) is in its header.
        new(u => u.Host == "telegra.ph", "article", "address a[rel=author]"),
    ];

    /// <summary>Elements that are never content.</summary>
    private const string Chrome = "script, style, noscript, template, svg, iframe, header, footer, nav";

    private static readonly HashSet<string> Blocks = new(
        ["p", "div", "section", "article", "main", "aside", "blockquote", "pre", "ul", "ol", "table", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "dl", "dt",
         "dd", "figure", "figcaption", "form", "fieldset", "details", "summary", "hr", "address"], StringComparer.OrdinalIgnoreCase);

    public WebPage Read(Uri url, string body, string? mediaType)
    {
        if (mediaType is not null && mediaType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
            return new WebPage(url, null, Normalize(body), [.. LinkPolicy.FindUrls(body)], null);

        var document = new HtmlParser().ParseDocument(body);
        var baseUri = document.QuerySelector("base[href]")?.GetAttribute("href") is { } b && Uri.TryCreate(url, b, out var based) ? based : url;
        var posting = JobPostings(document).FirstOrDefault();
        var title = Meta(document, "og:title") ?? document.Title?.Trim() ?? document.QuerySelector("h1")?.TextContent.Trim();

        if (Sites.FirstOrDefault(s => s.Matches(url)) is { } site && document.QuerySelector(site.Content) is { } content)
        {
            var links = Links(content, baseUri).Concat(site.Extra is null ? [] : document.QuerySelectorAll(site.Extra).SelectMany(e => Links(e, baseUri)));
            return new WebPage(url, title, Render(content), Distinct(links), posting);
        }

        // Links first: Readability rewrites the document it reads. Menus, headers and footers are chrome; the rest of the page's links
        // stay (a Telegram button of the recruiter often sits outside the article).
        var pageLinks = Distinct(Links(document.Body ?? document.DocumentElement, baseUri, skipChrome: true));
        var main = MainText(url, document, body);
        if (main.Length < 200 && posting?.Description is { Length: > 200 } described) main = described;
        return new WebPage(url, title, main, pageLinks, posting);
    }

    /// <summary>
    /// Readability's main content; the page body without chrome when Readability finds no article. The reader owns (and disposes) the
    /// document it is given: the fallback parses <paramref name="html"/> again.
    /// </summary>
    private string MainText(Uri url, IHtmlDocument document, string html)
    {
        var article = "";
        try
        {
            using var reader = new SmartReader.Reader(url.AbsoluteUri, document) { DisableJSONLD = true };
            var parsed = reader.GetArticle();
            if (parsed.IsReadable && !string.IsNullOrWhiteSpace(parsed.Content))
            {
                var fragment = new HtmlParser().ParseDocument(parsed.Content);
                article = Render(fragment.Body ?? fragment.DocumentElement);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Readability on a hostile page is a best effort: the body text below still serves.
        }

        // A real article wins; a short one may be a cookie banner Readability took for the content — then the body without chrome decides.
        if (article.Length >= ShortText) return article;
        var body = new HtmlParser().ParseDocument(html).Body;
        if (body is null) return article;
        foreach (var chrome in body.QuerySelectorAll(Chrome).ToList()) chrome.Remove();
        var all = Render(body);
        return all.Length > article.Length ? all : article;
    }

    /// <summary>Less text than this is not a vacancy description: a banner, a teaser, or a page its scripts have not drawn yet.</summary>
    public const int ShortText = 500;

    /// <summary>
    /// A "not found" page served with status 200 (a retired vacancy of a script-built career site routes itself to /404 — seen live on
    /// aristeksystems.com): by its address or its title.
    /// </summary>
    public static bool IsNotFound(WebPage page) =>
        NotFoundPath().IsMatch(page.Url.AbsolutePath) || NotFoundTitle().IsMatch(page.Title ?? "");

    [System.Text.RegularExpressions.GeneratedRegex(@"/(?:404|not-?found|page-not-found)(?:\.html?)?/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NotFoundPath();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\W*(?:404\b|page not found|not found|страница не найдена|ошибка 404)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NotFoundTitle();

    /// <summary>Text of an element as a reader sees it: blocks on their own lines, list items bulleted, line breaks kept.</summary>
    public static string Render(IElement root)
    {
        var sb = new StringBuilder();
        Walk(root, sb);
        return Normalize(sb.ToString());

        static void Walk(INode node, StringBuilder sb)
        {
            switch (node)
            {
                case IText text:
                    sb.Append(text.Data);
                    return;
                case IElement e when e.LocalName is "script" or "style" or "noscript" or "template" or "svg":
                    return;
                case IElement { LocalName: "br" }:
                    sb.Append('\n');
                    return;
                case IElement e:
                    var block = Blocks.Contains(e.LocalName);
                    if (block) sb.Append('\n');
                    if (e.LocalName == "li") sb.Append("\n• ");
                    if (e.LocalName is "td" or "th") sb.Append(' ');
                    foreach (var child in e.ChildNodes) Walk(child, sb);
                    if (block) sb.Append('\n');
                    return;
                default:
                    foreach (var child in node.ChildNodes) Walk(child, sb);
                    return;
            }
        }
    }

    /// <summary>Spaces collapsed within lines, at most one empty line in a row.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in text.Replace("\r", "").Replace(' ', ' ').Split('\n'))
        {
            var line = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0)
            {
                if (++blank > 1 || sb.Length == 0) continue;
            }
            else
            {
                blank = 0;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().Trim();
    }

    private static IEnumerable<PostLink> Links(IElement root, Uri baseUri, bool skipChrome = false)
    {
        foreach (var a in root.QuerySelectorAll("a[href]"))
        {
            // The author link of an article header is content (telegra.ph, blogs); other header/footer/nav links are the site's.
            if (skipChrome && a.GetAttribute("rel") != "author" && a.Closest("header, footer, nav") is not null) continue;
            var href = a.GetAttribute("href")!.Trim();
            if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            string url;
            if (href.Contains(':') && !href.StartsWith("//", StringComparison.Ordinal)) url = href; // absolute, mailto:, tel:, tg:
            else if (Uri.TryCreate(baseUri, href, out var resolved)) url = resolved.AbsoluteUri;
            else continue;
            // Rendered, not TextContent: an icon's <title> is not the caption and inline parts keep their spaces.
            var label = Render(a).Replace('\n', ' ').Trim();
            if (label.Length == 0) label = a.GetAttribute("aria-label") ?? a.GetAttribute("title") ?? "";
            yield return new PostLink { Url = url, Label = label.Length == 0 ? null : label };
        }
    }

    private static List<PostLink> Distinct(IEnumerable<PostLink> links)
    {
        var seen = new Dictionary<string, PostLink>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PostLink>();
        foreach (var link in links)
        {
            if (seen.TryGetValue(link.Url, out var first))
            {
                first.Label ??= link.Label;
                continue;
            }

            seen[link.Url] = link;
            result.Add(link);
        }

        return result;
    }

    private static string? Meta(IDocument document, string property) =>
        document.QuerySelector($"meta[property='{property}'], meta[name='{property}']")?.GetAttribute("content")?.Trim() is { Length: > 0 } v ? v : null;

    private static readonly JsonDocumentOptions LenientJson = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Every JobPosting in the page's JSON-LD (top level, arrays, @graph), mapped; malformed blocks are skipped.</summary>
    public IEnumerable<JobPostingData> JobPostings(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(script.TextContent, documentOptions: LenientJson);
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var node in Flatten(root))
                if (IsType(node, "JobPosting"))
                    yield return MapPosting(node);
        }
    }

    private static IEnumerable<JsonObject> Flatten(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                    foreach (var o in Flatten(item))
                        yield return o;
                break;
            case JsonObject obj:
                yield return obj;
                if (obj["@graph"] is JsonArray graph)
                    foreach (var o in Flatten(graph))
                        yield return o;
                break;
        }
    }

    private static bool IsType(JsonObject o, string type) => o["@type"] switch
    {
        JsonValue v => string.Equals(v.ToString(), type, StringComparison.OrdinalIgnoreCase),
        JsonArray a => a.Any(t => string.Equals(t?.ToString(), type, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private JobPostingData MapPosting(JsonObject o)
    {
        var salary = o["baseSalary"] as JsonObject;
        var value = salary?["value"];
        decimal? from = Number(value?["minValue"]) ?? Number(value is JsonObject ? value["value"] : value);
        var to = Number(value?["maxValue"]);
        var company = o["hiringOrganization"] switch
        {
            JsonObject org => Str(org["name"]),
            JsonValue name => name.ToString(),
            _ => null,
        };
        var locationType = o["jobLocationType"]?.ToJsonString() ?? "";
        var contacts = new List<VacancyContact>();
        if (o["applicationContact"] is JsonObject apply)
        {
            if (Core.Contacts.ContactExtractor.NormalizeEmail(Str(apply["email"])) is { } mail)
                contacts.Add(new VacancyContact { Kind = ContactKind.Email, Value = mail, Label = "JobPosting" });
            if (Core.Contacts.ContactExtractor.NormalizePhone(Str(apply["telephone"])) is { } phone)
                contacts.Add(new VacancyContact { Kind = ContactKind.Phone, Value = phone, Label = "JobPosting" });
        }

        var html = Str(o["description"]);
        var description = html is null ? null : Render(new HtmlParser().ParseDocument(html).Body!);
        return new JobPostingData(
            Str(o["title"]), company, Str(o["url"]), from, to, Str(salary?["currency"]) ?? Str(value?["currency"]),
            locationType.Contains("TELECOMMUTE", StringComparison.OrdinalIgnoreCase),
            o["employmentType"] switch { JsonArray a => string.Join(", ", a.Select(x => x?.ToString())), var e => Str(e) },
            DateTimeOffset.TryParse(Str(o["datePosted"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var posted) ? posted : null,
            description, contacts);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.ToString() is { Length: > 0 } s ? s.Trim() : null;

    private static decimal? Number(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue(out decimal d) ? d
        : decimal.TryParse(v.ToString().Replace(" ", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
        : null;
}
