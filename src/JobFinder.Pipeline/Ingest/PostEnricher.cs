using System.Text;
using System.Text.RegularExpressions;
using JobFinder.Core.Contacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Web;
using JobFinder.Llm.Tasks;

namespace JobFinder.Pipeline.Ingest;

/// <summary>What a post's vacancy is reached by: its contacts and its own page (null = the post itself).</summary>
public sealed record PostFacts(List<VacancyContact> Contacts, string? Url);

/// <summary>A post with what it links to: the model's input, the vacancy's description and contacts.</summary>
/// <param name="Links">The post's links: hidden ones (text links, buttons) and those written in its text, one per page.</param>
/// <param name="Contacts">Found without the model: the post, its links, the pages (text, links, JobPosting).</param>
/// <param name="DetailUrl">The vacancy's own page: the first one read from the post's links, else the hh vacancy it links to, else null.</param>
/// <param name="HeadHunter">An hh / rabota.by vacancy the post links to (visibly or behind a text link): the structured path takes it.</param>
public sealed record EnrichedPost(
    IReadOnlyList<PostLink> Links,
    IReadOnlyList<CrawledPage> Pages,
    IReadOnlyList<VacancyContact> Contacts,
    string Description,
    string? DetailUrl,
    JobPostingData? Posting,
    (string Host, long Id)? HeadHunter,
    IReadOnlyList<string> Failures,
    IReadOnlySet<string>? Missing = null)
{
    public IReadOnlyList<LinkedPage> ModelPages => [.. Pages.Select(p => new LinkedPage(p.Page.Url.AbsoluteUri, p.Page.Title, p.Page.Text))];

    /// <summary>Everything the contacts could have come from: a model's contact that is in none of it was made up.</summary>
    public string SourceText => string.Join('\n', Pages.Select(p => p.Page.Text).Prepend(Description)
        .Concat(Links.Select(l => l.Url)).Concat(Pages.SelectMany(p => p.Page.Links.Select(l => l.Url))));
}

/// <summary>
/// Turns a raw post into what the vacancy is made of. Contacts are found deterministically (<see cref="ContactExtractor"/>) in the post,
/// its hidden links and every page read; a contact of the site itself on a platform page (its channel, its support mailbox) and the
/// channel's own handle are not the employer's. The model's contacts are kept only when grounded in those sources.
/// </summary>
public sealed partial class PostEnricher(VacancyCrawler crawler, LinkPolicy policy, EnrichmentOptions options)
{
    private const int MaxDescription = 20000;

    /// <summary>The post's links: the captured ones first (they carry captions), then URLs written in the text; one per canonical page.</summary>
    public static List<PostLink> LinksOf(RawPost post)
    {
        var result = new List<PostLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in post.Links.Concat(LinkPolicy.FindUrls(post.Text)))
        {
            var key = Uri.TryCreate(link.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? LinkPolicy.Canonical(url).AbsoluteUri : link.Url;
            if (seen.Add(key)) result.Add(link);
        }

        return result;
    }

    /// <summary>An hh / rabota.by vacancy the post points to, in its text or behind a text link or button.</summary>
    public static (string Host, long Id)? HeadHunterLink(RawPost post, IEnumerable<PostLink> links)
    {
        if (HhClient.TryParseVacancyUrl(post.Text, out var host, out var id)) return (host, id);
        foreach (var link in links)
            if (HhClient.TryParseVacancyUrl(link.Url, out host, out id))
                return (host, id);
        return null;
    }

    /// <summary>
    /// A post the keyword prefilter rejects is still worth reading with its pages when it is short, links to something readable and
    /// shows a sign of a vacancy or of the stack ("MIDDLE C#/.NET | ALFA-BANK &lt;telegra.ph&gt;").
    /// </summary>
    public bool WorthReading(RawPost post, IReadOnlyList<PostLink> links, PostPrefilter prefilter) =>
        options.Enabled && post.Text.Length <= options.ShortPostChars && prefilter.MightBeVacancy(post.Text)
        && links.Any(l => policy.Classify(l.Url, l.Label).Role is LinkRole.Page or LinkRole.TelegramPost);

    public async Task<EnrichedPost> EnrichAsync(RawPost post, IReadOnlyList<PostLink> links, CancellationToken ct)
    {
        var self = Uri.TryCreate(post.Url, UriKind.Absolute, out var postUrl) ? postUrl : null;
        var crawl = await crawler.CrawlAsync(links, self, ct);
        var posting = crawl.Pages.Select(p => p.Page.JobPosting).FirstOrDefault(j => j is not null);
        // The vacancy's own page: the first one read from the post, else an hh vacancy the post links to (not read here: the hh path's).
        var hh = HeadHunterLink(post, links);
        // Among the post's pages the vacancy itself wins over a company page read before it (seen live: talanto's company card first).
        // A site's root (the employer's homepage) is read for what it says about the company, but it is not the vacancy's address.
        var first = crawl.Pages.Where(p => p.Depth == 1 && p.Page.Url.AbsolutePath.Length > 1).ToList();
        var detail = (first.FirstOrDefault(p => p.Page.JobPosting is not null) ?? first.FirstOrDefault(p => LinkPolicy.IsVacancyPath(p.Page.Url)) ?? first.FirstOrDefault())
                     ?.Page.Url.AbsoluteUri
                     ?? (hh is var (host, id) ? HhStateParser.VacancyUrl(host, id) : null);
        return new EnrichedPost(links, crawl.Pages, Contacts(post, links, crawl.Pages), Description(post, crawl.Pages), detail, posting,
            hh, crawl.Failures, crawl.Missing);
    }

    /// <summary>Post text, the post's link contacts, then per page: its JobPosting, its link contacts, its text — merged, chrome dropped.</summary>
    public List<VacancyContact> Contacts(RawPost post, IEnumerable<PostLink> links, IEnumerable<CrawledPage> pages)
    {
        var found = new List<VacancyContact>();
        // In a group the author of a post is the recruiter; a channel signs its posts with its own title.
        if (post.Author is { } author && author.StartsWith('@') && ContactExtractor.Classify(author) is { Kind: ContactKind.Telegram } poster)
            found.Add(new VacancyContact { Kind = poster.Kind, Value = poster.Value, Label = "автор поста" });
        found.AddRange(ContactExtractor.FromText(post.Text));
        // A link that is a contact, or one captioned as the response ("Откликнуться" → an ATS page): both are how to answer.
        found.AddRange(links.Select(l => policy.Classify(l.Url, l.Label).Contact ?? policy.ApplyLink(l)).OfType<VacancyContact>());
        var fromPages = new List<VacancyContact>();
        foreach (var crawled in pages)
        {
            var page = crawled.Page;
            var source = page.Url.AbsoluteUri;
            var onPage = new List<VacancyContact>();
            onPage.AddRange(page.JobPosting?.Contacts ?? []);
            // A link captioned only with a network's name is the site's social icon (its channel), not a person to write to.
            onPage.AddRange(page.Links.Where(l => !SocialIcon().IsMatch(l.Label ?? "")).Select(l => policy.Classify(l.Url, l.Label).Contact ?? policy.ApplyLink(l, source))
                .OfType<VacancyContact>());
            onPage.AddRange(ContactExtractor.FromText(page.Text, source, bareMentions: false));
            fromPages.AddRange(onPage.Where(c => !policy.IsPlatformContact(page.Url, c)).Select(c =>
            {
                c.Source ??= source;
                return c;
            }));
        }

        // A page's general mailbox (info@, request@) is a way in, but after its HR or a person: the e-mail route takes the first address.
        found.AddRange(fromPages.OrderBy(c => c.Kind == ContactKind.Email && ContactExtractor.IsGenericMailbox(c.Value)));

        var own = OwnChannel(post);
        return ContactExtractor.Merge(found.Where(c => own is null || c.Kind != ContactKind.Telegram || c.Value != own));
    }

    /// <summary>
    /// What the model says, checked against the sources: its contacts (<see cref="Ground"/>) and its apply URL, which becomes the vacancy's
    /// address only when it is one of the links read, a page or an hh vacancy (a bot or a form stays a contact), and not one the crawl found
    /// missing; else the vacancy page read, else the post.
    /// </summary>
    public PostFacts Resolve(RawPost post, PostExtraction x, EnrichedPost enriched)
    {
        var contacts = Ground(post, x.Contacts, enriched);
        var missing = enriched.Missing ?? new HashSet<string>();
        string? url = null;
        // One of the URLs actually seen, whole: a prefix of one ("https://talanto.work" of ".../jobs/…", seen live) is no address.
        var seen = enriched.Links.Select(l => l.Url).Concat(enriched.Pages.SelectMany(p => p.Page.Links.Select(l => l.Url).Append(p.Page.Url.AbsoluteUri)))
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" ? LinkPolicy.Canonical(parsed).AbsoluteUri : null)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A site's root is never the vacancy, even when a page links it (every page's logo does — seen live: "https://talanto.work").
        if (Uri.TryCreate(x.ApplyUrl, UriKind.Absolute, out var modelUrl) && modelUrl.Scheme is "http" or "https" && modelUrl.AbsolutePath.Length > 1
            && seen.Contains(LinkPolicy.Canonical(modelUrl).AbsoluteUri)
            && policy.Classify(x.ApplyUrl) is { Role: LinkRole.Page or LinkRole.HeadHunter, Url: { } page }
            && !missing.Contains(page.AbsoluteUri))
            url = modelUrl.AbsoluteUri;
        return new PostFacts(contacts, url ?? enriched.DetailUrl);
    }

    /// <summary>
    /// The model's contacts that are real, merged after the deterministic ones (so theirs keep the labels and sources):
    /// <list type="bullet">
    /// <item>a Telegram username only when the post itself names it (its text or links): on a web page "@name" may be anybody's handle on
    /// another network, and the page's real Telegram contacts are found without the model;</item>
    /// <item>an e-mail, a phone (by its subscriber digits) or an apply form/bot URL when any source holds it — not a platform's or a service
    /// mailbox (the same chrome rules as for the pages).</item>
    /// </list>
    /// </summary>
    public List<VacancyContact> Ground(RawPost post, IEnumerable<string> modelContacts, EnrichedPost enriched)
    {
        var everything = enriched.SourceText.ToLowerInvariant();
        // The post's words with its URLs blanked ("teletype.in/@remoteit/…" names no one), plus the Telegram people its links point to.
        var words = LinkPolicy.FindUrls(post.Text).Aggregate(post.Text, (text, link) => text.Replace(link.Url, " ", StringComparison.Ordinal));
        var ownHandles = enriched.Links.Select(l => policy.Classify(l.Url, l.Label).Contact).OfType<VacancyContact>()
            .Where(c => c.Kind == ContactKind.Telegram).Select(c => c.Value.TrimStart('@')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool NamedByPost(string handle) =>
            ownHandles.Contains(handle) || System.Text.RegularExpressions.Regex.IsMatch(words, $@"(?<![\w.]){System.Text.RegularExpressions.Regex.Escape(handle)}(?![\w])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var digits = new string(everything.Where(char.IsAsciiDigit).ToArray());
        var grounded = new List<VacancyContact>();
        foreach (var raw in modelContacts)
        {
            var c = ContactExtractor.Classify(raw) ?? (policy.Classify(raw) is { Role: LinkRole.Apply, Contact: { } apply } ? apply : null);
            if (c is null || enriched.Pages.Any(p => policy.IsPlatformContact(p.Page.Url, c))) continue;
            var real = c.Kind switch
            {
                ContactKind.Telegram => NamedByPost(c.Value.TrimStart('@')),
                ContactKind.Phone or ContactKind.WhatsApp => digits.Contains(LastDigits(c.Value), StringComparison.Ordinal),
                _ => everything.Contains(c.Value.ToLowerInvariant().TrimEnd('/'), StringComparison.Ordinal),
            };
            if (real) grounded.Add(c);
        }

        return ContactExtractor.Merge(enriched.Contacts.Concat(grounded));
    }

    /// <summary>The subscriber part of a phone (its last 9 digits): the same however the country code or trunk prefix was written.</summary>
    private static string LastDigits(string phone)
    {
        var d = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return d[Math.Max(0, d.Length - 9)..];
    }

    /// <summary>The post's text, then each page read with where it came from: what scoring and the letter read.</summary>
    private static string Description(RawPost post, IEnumerable<CrawledPage> pages)
    {
        var sb = new StringBuilder(post.Text.Trim());
        foreach (var crawled in pages)
        {
            if (crawled.Page.Text.Length == 0) continue;
            sb.Append("\n\n———\n").Append(crawled.Page.Title is { Length: > 0 } title ? title + " — " : "").Append(crawled.Page.Url.AbsoluteUri)
                .Append('\n').Append(crawled.Page.Text);
            if (sb.Length >= MaxDescription) break;
        }

        return sb.Length > MaxDescription ? sb.ToString(0, MaxDescription) + "…" : sb.ToString();
    }

    /// <summary>"@channel" of a public channel's post (https://t.me/channel/123): its own handle is not a contact of the employer.</summary>
    private static string? OwnChannel(RawPost post) =>
        post.Source == SourceKind.Telegram && Uri.TryCreate(post.Url, UriKind.Absolute, out var url) && url.Host == "t.me"
                                           && url.AbsolutePath.Trim('/').Split('/') is [var channel, _] && channel != "c"
            ? "@" + channel.ToLowerInvariant()
            : null;

    /// <summary>A link captioned only with a network's name ("Telegram", "VK"): a site's social icon.</summary>
    [GeneratedRegex(@"^\W*(?:telegram|tg|телеграм\w*|whatsapp|vk|вконтакте)(?:[\s-]*(?:канал|channel|чат|chat|group|группа))?\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex SocialIcon();
}
