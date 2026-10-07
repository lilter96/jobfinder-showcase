using System.Text.RegularExpressions;
using System.Web;
using JobFinder.Core.Contacts;
using JobFinder.Core.Domain;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;

namespace JobFinder.Integrations.Web;

public enum LinkRole
{
    /// <summary>Not worth a request: a social network, media, an ad, a file, a private or invite link.</summary>
    Skip,

    /// <summary>The link is itself a way to reach the employer (a person's Telegram, mailto:, tel:, WhatsApp, LinkedIn).</summary>
    Contact,

    /// <summary>A form or a bot that takes the response: the apply link, never read.</summary>
    Apply,

    /// <summary>An hh / rabota.by vacancy: the structured hh path reads it (within the hh budget), not the crawler.</summary>
    HeadHunter,

    /// <summary>A public Telegram post: read through its web embed.</summary>
    TelegramPost,

    /// <summary>A web page that may hold the vacancy's details.</summary>
    Page,
}

/// <param name="Fetch">What to request for a readable link (a Telegram post's embed, a Google Doc's text export); null otherwise.</param>
public sealed record LinkDecision(LinkRole Role, Uri? Url, Uri? Fetch = null, VacancyContact? Contact = null);

/// <summary>
/// What a link in a vacancy post (or on a page it leads to) is, and whether it is worth reading. Pure: no network. Canonical URLs drop
/// what only tracks the click (utm_*, fbclid, …) so one page is read once however it was shared.
/// </summary>
public sealed partial class LinkPolicy(EnrichmentOptions options)
{
    public static readonly IReadOnlyList<string> DefaultSkipHosts =
    [
        "vk.com", "vk.ru", "vk.cc", "ok.ru", "max.ru", "dzen.ru", "zen.yandex.ru", "rutube.ru", "instagram.com", "facebook.com", "fb.com",
        "twitter.com", "x.com", "youtube.com", "youtu.be", "tiktok.com", "pinterest.com", "threads.net", "threads.com", "apps.apple.com",
        "play.google.com", "rustore.ru", "linkedin.com", "tenchat.ru", "updatecv.me", "telegram.org", "maps.google.com", "2gis.ru", "2gis.by",
        // Telegram links a tech term written like a domain ("ASP.NET" → https://asp.net): never the vacancy. Reference sites are not either.
        "asp.net", "vb.net", "ado.net", "ml.net", "dot.net", "microsoft.com", "github.com", "gitlab.com", "stackoverflow.com", "wikipedia.org",
    ];

    public static readonly IReadOnlyList<string> DefaultApplyHosts =
    [
        "forms.gle", "forms.yandex.ru", "forms.yandex.com", "typeform.com", "tally.so", "forms.office.com", "jotform.com", "airtable.com",
    ];

    public static readonly IReadOnlyList<string> DefaultPlatforms =
    [
        "telegra.ph", "teletype.in", "t.me", "talanto.work", "jobrocket.ru", "career.habr.com", "getmatch.ru", "notion.site", "vc.ru",
        "geekjob.ru", "remocate.app", "hirify.me", "finder.work", "trudvsem.ru", "superjob.ru", "zarplata.ru", "vseti.app",
    ];

    private static readonly HashSet<string> TrackingParameters =
        new(["fbclid", "gclid", "yclid", "ysclid", "_openstat", "ref", "ref_src", "igshid", "mc_cid", "mc_eid", "hhtmFrom", "hhtmSource", "from", "_ga"],
            StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FileExtensions =
        new([".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".mp4", ".mov", ".mp3", ".zip", ".rar", ".apk", ".exe", ".pdf", ".doc", ".docx", ".xls", ".xlsx"],
            StringComparer.OrdinalIgnoreCase);

    private readonly string[] _skip = [.. DefaultSkipHosts.Concat(options.SkipHosts).Select(Bare).Distinct()];
    private readonly string[] _apply = [.. DefaultApplyHosts.Concat(options.ApplyHosts).Select(Bare).Distinct()];
    private readonly string[] _platforms = [.. DefaultPlatforms.Concat(options.Platforms).Select(Bare).Distinct()];

    public LinkDecision Classify(string? raw, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new LinkDecision(LinkRole.Skip, null);
        var text = raw.Trim();
        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                                                                             || text.StartsWith("tg:", StringComparison.OrdinalIgnoreCase))
            return ContactDecision(ContactExtractor.FromUrl(text, label), null);

        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || text.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) return new LinkDecision(LinkRole.Skip, null);
        var url = Canonical(parsed);
        var host = Bare(url.Host);

        if (ContactExtractor.FromUrl(url.AbsoluteUri, label) is { } contact) return ContactDecision(contact, url);
        if (host is "t.me" or "telegram.me") return TelegramPost(url);
        if (HhClient.TryParseVacancyUrl(url.AbsoluteUri, out _, out _)) return new LinkDecision(LinkRole.HeadHunter, url);
        // Other hh pages (an employer, a search) are not the vacancy and cost the scarce hh budget.
        if (PoliteHttp.Family(host) == "hh") return new LinkDecision(LinkRole.Skip, url);
        if (host == "docs.google.com") return GoogleDoc(url, label);
        if (Matches(host, _apply)) return new LinkDecision(LinkRole.Apply, url, Contact: new VacancyContact { Kind = ContactKind.Link, Value = url.AbsoluteUri, Label = label });
        // A careers subdomain is read even when its company's main site is not (job.2gis.ru of 2gis.ru, a map service).
        if (Matches(host, _skip) && !CareerHost().IsMatch(host) || FileExtensions.Contains(Path.GetExtension(url.AbsolutePath)))
            return new LinkDecision(LinkRole.Skip, url);
        // A link on a technology's name (".NET", "ASP.NET Core", "Kafka") decorates the text: a product or marketing page, never the vacancy.
        if (TechWord().IsMatch(label ?? "")) return new LinkDecision(LinkRole.Skip, url);
        return new LinkDecision(LinkRole.Page, url, Clean(parsed));
    }

    /// <summary>
    /// A link on a read page that leads further into the same vacancy. The link says so ("Подробнее", "Откликнуться", "Apply", "Смотреть
    /// вакансию"), or — only from a page that is not a vacancy itself (a teaser, a company page) — its path is a vacancy's (/job/, /careers/…):
    /// on a vacancy page such paths are the "similar vacancies". It stays on the site, or it leaves a publishing platform or aggregator,
    /// whose "APPLY" goes to the employer's own vacancy page (teletype → simbirsoft.com/vacancies/…). Menus and logins are not followed.
    /// </summary>
    /// <param name="fromVacancy">The page is a vacancy already (it has a JobPosting, or a vacancy's path).</param>
    public bool LeadsToDetails(Uri from, Uri to, string? label, bool fromVacancy)
    {
        var says = DetailWords().IsMatch(label ?? "") || IsApplyLabel(label);
        // A platform's apply/details link to the employer's own page is followed from anywhere.
        if (IsPlatform(from) && !IsPlatform(to) && !SameSite(from, to)) return says || DetailPath().IsMatch(to.AbsolutePath);
        // On a vacancy page every other page of the same site is another vacancy ("Подробнее" on the similar-vacancy cards — seen live on
        // h.careers: Golang, Java and C++ openings appended to a .NET one), a menu or the company: nothing there is this vacancy.
        if (fromVacancy || !SameSite(from, to) || to.AbsolutePath == from.AbsolutePath) return false;
        return says || DetailPath().IsMatch(to.AbsolutePath);
    }

    /// <summary>
    /// A link captioned as the response itself ("Откликнуться", "Apply", "Отправить резюме"): wherever it leads (an ATS form, a career
    /// page), it is how this vacancy is answered — kept as an apply contact even when the page behind it names no person.
    /// </summary>
    public static bool IsApplyLabel(string? label) => label is not null && ApplyWords().IsMatch(label);

    /// <summary>The apply contact of a link captioned so (<see cref="IsApplyLabel"/>), for a readable page or an hh vacancy; null otherwise.</summary>
    public VacancyContact? ApplyLink(PostLink link, string? source = null) =>
        IsApplyLabel(link.Label) && Classify(link.Url, link.Label) is { Role: LinkRole.Page or LinkRole.HeadHunter } d
            ? new VacancyContact { Kind = ContactKind.Link, Value = (d.Fetch ?? d.Url)!.AbsoluteUri, Label = link.Label!.Trim(), Source = source }
            : null;

    [GeneratedRegex(@"^\W*(?:откликнуться(?: на вакансию)?|отклик(?:нуться)?|apply(?: now| here| for (?:this|the) (?:job|position))?|подать (?:заявку|резюме)|отправить резюме|respond|join us)\W*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ApplyWords();

    /// <summary>The path of a single vacancy (/job/…, /vacancies/…, /careers/…).</summary>
    public static bool IsVacancyPath(Uri url) => DetailPath().IsMatch(url.AbsolutePath);

    public bool IsPlatform(Uri url) => Matches(Bare(url.Host), _platforms);

    /// <summary>A contact of the site itself on a publishing platform or aggregator (its channel, its support): chrome, not the employer.</summary>
    public bool IsPlatformContact(Uri page, VacancyContact contact)
    {
        var host = Bare(page.Host);
        if (contact.Kind == ContactKind.Email && ContactExtractor.IsServiceMailbox(contact.Value)) return true;
        if (!Matches(host, _platforms)) return false;
        var label = MainLabel(host);
        return label.Length >= 4 && contact.Value.Contains(label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The URL without what only tracks the click, the fragment and "www."; lower-case host; a trailing slash dropped.</summary>
    public static Uri Canonical(Uri url)
    {
        var clean = Clean(url);
        var path = clean.AbsolutePath.Length > 1 ? clean.AbsolutePath.TrimEnd('/') : clean.AbsolutePath;
        var builder = new UriBuilder(clean) { Host = Bare(clean.Host), Path = path };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>
    /// What is requested: the link as written, without what only tracks the click and without the fragment. The host stays exactly as
    /// written — "www.simbirsoft.com" and "simbirsoft.com" are one page to the crawl (<see cref="Canonical"/>) but not one server (the bare
    /// name resolves to a dead address from some networks).
    /// </summary>
    public static Uri Clean(Uri url)
    {
        var query = HttpUtility.ParseQueryString(url.Query);
        var keys = query.AllKeys.Where(k => k is not null).ToList();
        var kept = keys.Where(k => !k!.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !TrackingParameters.Contains(k)).ToList();
        if (kept.Count == keys.Count) return new Uri(url.GetLeftPart(UriPartial.Query));
        var builder = new UriBuilder(url)
        {
            Query = string.Join('&', kept.Select(k => Uri.EscapeDataString(k!) + "=" + Uri.EscapeDataString(query[k] ?? ""))),
            Fragment = "",
        };
        return builder.Uri;
    }

    /// <summary>URLs written in a plain text (a Threads post, a page in text form), without the punctuation around them.</summary>
    public static IEnumerable<PostLink> FindUrls(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in TextUrl().Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '»', '”', '"', '\'');
            // A closing parenthesis belongs to the URL only when the URL opened one ("wiki/Foo_(bar)").
            while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '(')) url = url[..^1];
            yield return new PostLink { Url = url };
        }
    }

    private LinkDecision ContactDecision(VacancyContact? contact, Uri? url) =>
        contact is null
            ? new LinkDecision(LinkRole.Skip, url)
            : new LinkDecision(contact.Kind == ContactKind.Link ? LinkRole.Apply : LinkRole.Contact, url, Contact: contact);

    /// <summary>t.me/channel/123 (or t.me/s/channel/123) → its public embed; channels, invites and private links are not read.</summary>
    private static LinkDecision TelegramPost(Uri url)
    {
        var parts = url.AbsolutePath.Trim('/').Split('/');
        if (parts.Length == 3 && parts[0] == "s") parts = parts[1..];
        if (parts.Length == 2 && parts[0] != "c" && TelegramName().IsMatch(parts[0]) && long.TryParse(parts[1], out var id))
        {
            var post = new Uri($"https://t.me/{parts[0]}/{id}");
            return new LinkDecision(LinkRole.TelegramPost, post, new Uri($"https://t.me/{parts[0]}/{id}?embed=1&mode=tme"));
        }

        return new LinkDecision(LinkRole.Skip, url);
    }

    /// <summary>A Google form takes the response; a Google Doc (a common home of a job description) is read as its plain-text export.</summary>
    private static LinkDecision GoogleDoc(Uri url, string? label)
    {
        if (url.AbsolutePath.StartsWith("/forms/", StringComparison.Ordinal))
            return new LinkDecision(LinkRole.Apply, url, Contact: new VacancyContact { Kind = ContactKind.Link, Value = url.AbsoluteUri, Label = label ?? "Google-форма" });
        var doc = GoogleDocId().Match(url.AbsolutePath);
        return doc.Success
            ? new LinkDecision(LinkRole.Page, url, new Uri($"https://docs.google.com/document/d/{doc.Groups["id"].Value}/export?format=txt"))
            : new LinkDecision(LinkRole.Skip, url);
    }

    private static bool SameSite(Uri a, Uri b)
    {
        var (x, y) = (Bare(a.Host), Bare(b.Host));
        return x == y || x.EndsWith("." + y, StringComparison.Ordinal) || y.EndsWith("." + x, StringComparison.Ordinal) || Registrable(x) == Registrable(y);
    }

    /// <summary>The last two labels ("job.alfabank.ru" → "alfabank.ru"): enough for the .ru/.by/.com sites vacancies live on.</summary>
    private static string Registrable(string host)
    {
        var labels = host.Split('.');
        return labels.Length <= 2 ? host : string.Join('.', labels[^2..]);
    }

    private static string MainLabel(string host)
    {
        var labels = host.Split('.');
        return labels.Length >= 2 ? labels[^2] : host;
    }

    private static bool Matches(string host, IEnumerable<string> list) =>
        list.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));

    private static string Bare(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""'«» ]+", RegexOptions.IgnoreCase)]
    private static partial Regex TextUrl();

    [GeneratedRegex(@"подробн|детал|полное описание|описание ваканс|читать|отклик|ваканси[юя]\b|apply|details|read more|more info|learn more|view job|job description|full description",
        RegexOptions.IgnoreCase)]
    private static partial Regex DetailWords();

    [GeneratedRegex(@"/(?:job|jobs|vacancy|vacancies|career|careers|position|positions|opening|openings|ваканс\w*)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DetailPath();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{3,31}$")]
    private static partial Regex TelegramName();

    [GeneratedRegex(@"^(?:job|jobs|career|careers|hr|hh|rabota|vacancy|vacancies|talent)\.", RegexOptions.IgnoreCase)]
    private static partial Regex CareerHost();

    [GeneratedRegex(@"^\W*(?:\.net(?: core)?|asp\.net(?: core)?|dotnet|c#|f#|ef core|entity framework|blazor|unity|kafka|rabbitmq|redis|docker|kubernetes|k8s|postgres(?:ql)?|ms ?sql|sql|react|angular|vue|typescript|azure|aws|grpc|signalr|linq)\W*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TechWord();

    [GeneratedRegex(@"^/document/d/(?<id>[\w-]{20,})")]
    private static partial Regex GoogleDocId();
}
