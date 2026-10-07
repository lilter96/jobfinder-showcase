using JobFinder.Core.Domain;

namespace JobFinder.Core.Options;

/// <summary>Filesystem layout. Relative paths resolve against the content root (the repo root locally, /app in the container).</summary>
public sealed class PathsOptions
{
    public const string Section = "Paths";

    public string Data { get; set; } = "data";

    public string Profile { get; set; } = "profile";

    public string Prompts { get; set; } = "prompts";

    /// <summary>Fonts and other static inputs of generated documents.</summary>
    public string Assets { get; set; } = "assets";
}

/// <summary>Deterministic rules applied before the LLM (<see cref="Filtering.HardFilter"/>).</summary>
public sealed class FilterOptions
{
    public const string Section = "Filter";

    /// <summary>A title containing one of these (as a whole word) is rejected: junior, стажер, frontend, QA, 1С, Java, …</summary>
    public List<string> TitleStopWords { get; set; } = [];

    /// <summary>At least one must appear in the title, the skills or the description: .net, c#, dotnet, asp.net.</summary>
    public List<string> RequiredMarkers { get; set; } = [];

    /// <summary>Cities where office or hybrid work is acceptable.</summary>
    public List<string> OfficeCities { get; set; } = [];

    /// <summary>Employer countries (ISO) for which remote work is acceptable.</summary>
    public List<string> RemoteCountries { get; set; } = [];

    /// <summary>The highest English requirement the candidate can meet.</summary>
    public EnglishLevel MaxEnglish { get; set; } = EnglishLevel.B1;

    /// <summary>Company names (normalized, substring match) never applied to.</summary>
    public List<string> CompanyBlacklist { get; set; } = [];
}

/// <summary>Currency rates to USD and the shape of the salary score adjustment (<see cref="Filtering.SalaryEvaluator"/>).</summary>
public sealed class SalaryOptions
{
    public const string Section = "Salary";

    /// <summary>How many USD one unit of the currency is worth. Approximate on purpose: the result only nudges the score.</summary>
    public Dictionary<string, decimal> RatesToUsd { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Net share of a gross amount (income tax).</summary>
    public decimal NetShareOfGross { get; set; } = 0.87m;

    /// <summary>Largest penalty for an offer below expectations.</summary>
    public int MaxPenalty { get; set; } = 25;

    /// <summary>Largest bonus for an offer above expectations.</summary>
    public int MaxBonus { get; set; } = 5;
}

/// <summary>Defaults of the runtime-tunable decision settings (the dashboard and the bot override them in the settings table).</summary>
public sealed class DecisionOptions
{
    public const string Section = "Decision";

    public int AutoApplyThreshold { get; set; } = 80;

    public int ApprovalThreshold { get; set; } = 60;

    public int DailyAutoApplyLimit { get; set; } = 25;

    public bool AutoApplyPaused { get; set; }

    /// <summary>IANA zone the "day" of the limit, the digest times and the working hours are counted in.</summary>
    public string TimeZone { get; set; } = "Europe/Minsk";
}
