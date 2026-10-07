namespace JobFinder.Integrations.HeadHunter;

public sealed class HhOptions
{
    public const string Section = "HeadHunter";

    public bool Enabled { get; set; } = true;

    public List<HhSearch> Searches { get; set; } = [];

    public int IntervalMinutes { get; set; } = 45;

    public int JitterMinutes { get; set; } = 15;

    /// <summary>Local hours without searching ("00:30-07:00"): little is published at night, and the site's budget goes to the company sweep.</summary>
    public string QuietHours { get; set; } = "00:30-07:00";

    /// <summary>Site the browser applies on (the user's resume lives on the shared hh account, either host works).</summary>
    public string ApplyHost { get; set; } = "hh.ru";

    /// <summary>Substring of the resume title to pick in the response form when the account has several resumes; empty = the default one.</summary>
    public string ResumeTitle { get; set; } = "";

    /// <summary>
    /// Vacancy pages read per day under the user's session for what the user asked for (links sent to the bot): the account is never a
    /// scraping channel, the searches and backfills stay anonymous.
    /// </summary>
    public int AccountReadsPerDay { get; set; } = 30;

    /// <summary>Auto-apply is paused for this long after a captcha or an expired session (the bot asks to re-login).</summary>
    public int BlockedPauseMinutes { get; set; } = 120;

    /// <summary>CSS selectors of the response flow, in config so a redesign is fixed without a rebuild.</summary>
    public HhSelectors Selectors { get; set; } = new();

    public HhCompanyCollectorOptions Companies { get; set; } = new();
}

/// <summary>One search profile. Text uses hh query language: <c>.NET OR C# OR ASP.NET</c>.</summary>
public sealed class HhSearch
{
    public string Name { get; set; } = "";

    /// <summary>hh.ru or rabota.by.</summary>
    public string Host { get; set; } = "hh.ru";

    public string Text { get; set; } = "";

    /// <summary>name = title only (far less noise than the default full-text search).</summary>
    public string SearchField { get; set; } = "name";

    /// <summary>hh area ids: 113 Russia, 16 Belarus, 1002 Minsk.</summary>
    public List<int> Areas { get; set; } = [];

    /// <summary>REMOTE / HYBRID / ON_SITE; empty = any.</summary>
    public List<string> WorkFormats { get; set; } = [];

    /// <summary>1, 3, 7 or 30 days.</summary>
    public int PeriodDays { get; set; } = 3;

    public int MaxPages { get; set; } = 3;
}

public sealed class HhSelectors
{
    public string ApplyButton { get; set; } = "[data-qa='vacancy-response-link-top']";

    public string ResumeSelector { get; set; } = "[data-qa='resume-title']";

    public string LetterToggle { get; set; } = "[data-qa='vacancy-response-letter-toggle']";

    /// <summary>The letter field: in the response popup, and under «Резюме доставлено» after a quick response (textarea name="text").</summary>
    public string LetterInput { get; set; } = "[data-qa='vacancy-response-popup-form-letter-input'], textarea[name='letter']";

    /// <summary>Sends the letter written under an existing response (seen live 2026-09-27).</summary>
    public string LetterSubmit { get; set; } = "[data-qa='vacancy-response-letter-submit']";

    public string SubmitButton { get; set; } = "[data-qa='vacancy-response-submit-popup']";

    /// <summary>"You are applying to a vacancy in another country" confirmation.</summary>
    public string RelocationConfirm { get; set; } = "[data-qa='relocation-warning-confirm']";

    /// <summary>Any of these visible after submit = success.</summary>
    public string Success { get; set; } = "[data-qa='vacancy-response-link-view-topic'], [data-qa='vacancy-response-success-standard-notification']";

    /// <summary>Any of these = the account is already applied.</summary>
    public string AlreadyApplied { get; set; } = "[data-qa='vacancy-response-link-view-topic']";

    /// <summary>Logged-in marker on any page (the applicant menu).</summary>
    public string LoggedIn { get; set; } = "[data-qa='mainmenu_applicantProfile'], [data-qa='mainmenu_myResumes']";

    /// <summary>The response opened an employer questionnaire instead of the popup.</summary>
    public string TestMarker { get; set; } = "[data-qa='task-body'], [data-qa='employer-asking-for-test']";
}

public sealed class HhCompanyCollectorOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Local time of the nightly run.</summary>
    public string RunAt { get; set; } = "03:30";

    /// <summary>Searches whose employers are collected (wide period, all pages).</summary>
    public List<HhSearch> Searches { get; set; } = [];

    /// <summary>Employer pages are refreshed at most this often.</summary>
    public int RefreshDays { get; set; } = 14;

    /// <summary>Tag → keywords (stems) looked up in the company description and industries.</summary>
    public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
