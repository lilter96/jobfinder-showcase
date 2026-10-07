using System.Text.Json.Serialization;

namespace JobFinder.Core.Profile;

/// <summary>
/// The candidate as the scorer and letter writer see it: the versioned PostgreSQL document <c>candidate.profile</c>, imported
/// from a resume and edited through the Candidate owner. Updates require the version shown to the user.
/// </summary>
public sealed record CandidateProfile
{
    public string Name { get; set; } = "";

    /// <summary>The name for English documents ("Terentiy Gatsukov"); transliterated from <see cref="Name"/> when empty.</summary>
    public string? NameLatin { get; set; }

    public string Title { get; set; } = "";

    public double YearsOfExperience { get; set; }

    public string Summary { get; set; } = "";

    public List<ProfileSkill> Skills { get; set; } = [];

    public List<string> Domains { get; set; } = [];

    public List<string> Achievements { get; set; } = [];

    public List<ProfileJob> Experience { get; set; } = [];

    public string EnglishLevel { get; set; } = "B1";

    public string Location { get; set; } = "";

    /// <summary>office / hybrid / remote.</summary>
    public List<string> WorkFormats { get; set; } = [];

    public DesiredSalary? DesiredSalary { get; set; }

    /// <summary>What makes a vacancy unacceptable regardless of the stack (gambling, outstaff to a body shop, …).</summary>
    public List<string> DealBreakers { get; set; } = [];

    /// <summary>What makes a vacancy more attractive (product company, high load, DDD, …).</summary>
    public List<string> Preferences { get; set; } = [];

    public ProfileContacts Contacts { get; set; } = new();

    /// <summary>
    /// false right after <c>--import-resume</c>: the pipeline does not score against a profile nobody has checked (a missing salary or a
    /// wrong work format would mis-score every queued vacancy). A hand-written file without the field counts as reviewed.
    /// </summary>
    public bool Reviewed { get; set; } = true;
}

public sealed record ProfileSkill
{
    public string Name { get; set; } = "";

    /// <summary>basic / good / expert.</summary>
    public string Level { get; set; } = "";

    public double? Years { get; set; }
}

public sealed record ProfileJob
{
    public string Company { get; set; } = "";

    public string Role { get; set; } = "";

    public string Period { get; set; } = "";

    public string Highlights { get; set; } = "";
}

public sealed record DesiredSalary
{
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "USD";

    /// <summary>true = the amount is after tax.</summary>
    public bool Net { get; set; } = true;
}

public sealed record ProfileContacts
{
    public string? Telegram { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>"github.com/lilter96": a senior's code, printed and clickable in the resume.</summary>
    public string? GitHub { get; set; }

    /// <summary>"linkedin.com/in/terentiy-gatsukov".</summary>
    public string? LinkedIn { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CandidateProfile))]
public sealed partial class ProfileJsonContext : JsonSerializerContext;
