namespace JobFinder.Integrations.Web;

/// <summary>
/// Reading the pages a vacancy post links to (<see cref="VacancyCrawler"/>). The host lists add to the built-in ones (<see cref="LinkPolicy"/>);
/// config cannot remove a default, only extend it.
/// </summary>
public sealed class EnrichmentOptions
{
    public const string Section = "Enrichment";

    public bool Enabled { get; set; } = true;

    /// <summary>1 = the post's own links; 2 = also the "details" links on those pages (a teaser page → the vacancy page).</summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>Pages read per post, whatever the depth.</summary>
    public int MaxPages { get; set; } = 6;

    /// <summary>Text kept per page (for the model and the description).</summary>
    public int MaxPageChars { get; set; } = 8000;

    /// <summary>The whole reading of one post; what is read by then is used.</summary>
    public int BudgetSeconds { get; set; } = 90;

    /// <summary>A post this short that the keyword prefilter rejects is still read with its pages when it links out ("Вакансия: &lt;link&gt;").</summary>
    public int ShortPostChars { get; set; } = 400;

    /// <summary>A page that is nearly empty as HTML (a script-drawn career site) is drawn by the headless browser and read again.</summary>
    public bool RenderScripts { get; set; } = true;

    /// <summary>Never read: social networks, media, ads next to vacancies.</summary>
    public List<string> SkipHosts { get; set; } = [];

    /// <summary>Forms that take the response: kept as the apply link, not read.</summary>
    public List<string> ApplyHosts { get; set; } = [];

    /// <summary>Publishing and aggregator sites: a contact of the site itself there (its channel, its support mailbox) is page chrome.</summary>
    public List<string> Platforms { get; set; } = [];
}
