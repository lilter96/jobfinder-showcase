using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;

namespace JobFinder.Llm.Contracts.Wire;

public sealed record VacancyScoringWire : IValidatable
{
    [JsonRequired, Range(0, 100)] public int Score { get; set; }

    [JsonRequired, Range(0, 100)] public int StackMatch { get; set; }

    [JsonRequired, Range(0, 100)] public int SeniorityMatch { get; set; }

    [JsonRequired, Range(0, 100)] public int DomainMatch { get; set; }

    [JsonRequired, Range(0, 100)] public int FormatMatch { get; set; }

    [JsonRequired, WireChoices("unknown", "none", "a1", "a2", "b1", "b2", "c1", "c2", "native")] public string EnglishRequired { get; set; } = "";

    [JsonRequired] public List<string> Strengths { get; set; } = [];

    [JsonRequired] public List<string> Gaps { get; set; } = [];

    [JsonRequired] public List<string> RedFlags { get; set; } = [];

    [JsonRequired] public string Summary { get; set; } = "";

    [JsonRequired] public List<string> Keywords { get; set; } = [];

    [JsonRequired, WireChoices("ru", "en")] public string Language { get; set; } = "";

    [JsonRequired, WireChoices("ok", "suspicious", "scam")] public string Legitimacy { get; set; } = "";

    [JsonRequired] public List<string> LegitimacyReasons { get; set; } = [];

    public VacancyScoring ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.VacancyScoringWire, LlmJsonContext.Default.VacancyScoring);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record CommentPlanWire : IValidatable
{
    [JsonRequired, WireChoices("recruiter", "hiring_manager", "tech", "hiring", "other")] public string Audience { get; set; } = "";

    [JsonRequired] public bool WorthCommenting { get; set; }

    [JsonRequired] public string Reason { get; set; } = "";

    [JsonRequired] public List<string> Variants { get; set; } = [];

    public CommentPlan ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.CommentPlanWire, LlmJsonContext.Default.CommentPlan);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record ResumeDraftWireRequirementsItem
{
    [JsonRequired] public string Requirement { get; set; } = "";

    [JsonRequired] public bool MustHave { get; set; }

    [JsonRequired] public string Evidence { get; set; } = "";

    [JsonRequired] public string Placement { get; set; } = "";
}

public sealed record ResumeDraftWireDocumentSkillsItem
{
    [JsonRequired] public string Group { get; set; } = "";

    [JsonRequired] public List<string> Items { get; set; } = [];
}

public sealed record ResumeDraftWireDocumentExperienceItem
{
    [JsonRequired] public string Company { get; set; } = "";

    [JsonRequired] public string Role { get; set; } = "";

    [JsonRequired] public string Period { get; set; } = "";

    [JsonRequired] public string? Location { get; set; }

    [JsonRequired] public string? Context { get; set; }

    [JsonRequired] public List<string> Bullets { get; set; } = [];

    [JsonRequired] public List<string> Stack { get; set; } = [];
}

public sealed record ResumeDraftWireDocumentEducationItem
{
    [JsonRequired] public string Institution { get; set; } = "";

    [JsonRequired] public string Degree { get; set; } = "";

    [JsonRequired] public string Period { get; set; } = "";
}

public sealed record ResumeDraftWireDocument
{
    [JsonRequired, WireChoices("ru", "en")] public string Language { get; set; } = "";

    [JsonRequired] public string Headline { get; set; } = "";

    [JsonRequired] public string Location { get; set; } = "";

    [JsonRequired] public string Summary { get; set; } = "";

    [JsonRequired] public List<ResumeDraftWireDocumentSkillsItem> Skills { get; set; } = [];

    [JsonRequired] public List<string> Familiar { get; set; } = [];

    [JsonRequired] public List<ResumeDraftWireDocumentExperienceItem> Experience { get; set; } = [];

    [JsonRequired] public List<ResumeDraftWireDocumentEducationItem> Education { get; set; } = [];

    [JsonRequired] public List<string> Languages { get; set; } = [];

    [JsonRequired] public List<string> Extras { get; set; } = [];
}

public sealed record ResumeDraftWire : IValidatable
{
    [JsonRequired] public List<ResumeDraftWireRequirementsItem> Requirements { get; set; } = [];

    [JsonRequired] public ResumeDraftWireDocument Document { get; set; } = new();

    public ResumeDraft ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.ResumeDraftWire, LlmJsonContext.Default.ResumeDraft);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record ResumeReviewWireChecksItem
{
    [JsonRequired, WireChoices("truth", "summary_proofs", "first_bullets", "keywords", "no_filler", "logistics", "no_repeat", "readability")] public string Id { get; set; } = "";

    [JsonRequired] public bool Passed { get; set; }

    [JsonRequired] public string Quote { get; set; } = "";

    [JsonRequired] public string Note { get; set; } = "";
}

public sealed record ResumeReviewWireIssuesItem
{
    [JsonRequired] public string Where { get; set; } = "";

    [JsonRequired] public string Quote { get; set; } = "";

    [JsonRequired] public string Problem { get; set; } = "";

    [JsonRequired] public string Fix { get; set; } = "";
}

public sealed record ResumeReviewWire : IValidatable
{
    [JsonRequired] public int Score { get; set; }

    [JsonRequired] public string Verdict { get; set; } = "";

    [JsonRequired] public List<ResumeReviewWireChecksItem> Checks { get; set; } = [];

    [JsonRequired] public List<ResumeReviewWireIssuesItem> Issues { get; set; } = [];

    public JobFinder.Core.Resume.ResumeReview ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.ResumeReviewWire, LlmJsonContext.Default.ResumeReview);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record CandidateProfileWireSkillsItem
{
    [JsonRequired] public string Name { get; set; } = "";

    [JsonRequired, WireChoices("basic", "good", "expert")] public string Level { get; set; } = "";

    [JsonRequired] public decimal? Years { get; set; }
}

public sealed record CandidateProfileWireExperienceItem
{
    [JsonRequired] public string Company { get; set; } = "";

    [JsonRequired] public string Role { get; set; } = "";

    [JsonRequired] public string Period { get; set; } = "";

    [JsonRequired] public string Highlights { get; set; } = "";
}

public sealed record CandidateProfileWireDesiredSalary
{
    [JsonRequired] public decimal Amount { get; set; }

    [JsonRequired] public string Currency { get; set; } = "";

    [JsonRequired] public bool Net { get; set; }
}

public sealed record CandidateProfileWireContacts
{
    [JsonRequired] public string? Telegram { get; set; }

    [JsonRequired] public string? Email { get; set; }

    [JsonRequired] public string? Phone { get; set; }

    [JsonRequired] public string? GitHub { get; set; }

    [JsonRequired] public string? LinkedIn { get; set; }
}

public sealed record CandidateProfileWire : IValidatable
{
    [JsonRequired] public string Name { get; set; } = "";

    [JsonRequired] public string Title { get; set; } = "";

    [JsonRequired] public decimal YearsOfExperience { get; set; }

    [JsonRequired] public string Summary { get; set; } = "";

    [JsonRequired] public List<CandidateProfileWireSkillsItem> Skills { get; set; } = [];

    [JsonRequired] public List<string> Domains { get; set; } = [];

    [JsonRequired] public List<string> Achievements { get; set; } = [];

    [JsonRequired] public List<CandidateProfileWireExperienceItem> Experience { get; set; } = [];

    [JsonRequired] public string EnglishLevel { get; set; } = "";

    [JsonRequired] public string Location { get; set; } = "";

    [JsonRequired] public List<string> WorkFormats { get; set; } = [];

    [JsonRequired] public CandidateProfileWireDesiredSalary? DesiredSalary { get; set; }

    [JsonRequired] public List<string> DealBreakers { get; set; } = [];

    [JsonRequired] public List<string> Preferences { get; set; } = [];

    [JsonRequired] public CandidateProfileWireContacts Contacts { get; set; } = new();

    public JobFinder.Core.Profile.CandidateProfile ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.CandidateProfileWire, LlmJsonContext.Default.CandidateProfile);

    public string? Validate() => WireMapping.Validate(ToDomain());
}
