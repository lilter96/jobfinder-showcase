using System.Text.Json.Serialization;

namespace JobFinder.Core.Resume;

/// <summary>A resume tailored to one vacancy: the structure the model fills, the gate checks and the template renders.</summary>
public sealed record ResumeDocument
{
    /// <summary>"ru" or "en".</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Target title under the name ("Senior .NET Backend Developer").</summary>
    public string Headline { get; set; } = "";

    /// <summary>
    /// The line under the contacts, from the confirmed facts: "Минск · офис и гибрид в Минске, удалённо по Беларуси и России". Empty = the
    /// profile's city and formats.
    /// </summary>
    public string Location { get; set; } = "";

    public string Summary { get; set; } = "";

    public List<SkillGroup> Skills { get; set; } = [];

    /// <summary>Adjacent technologies from the vacancy the candidate has not used but can pick up: printed as "Также знаком".</summary>
    public List<string> Familiar { get; set; } = [];

    public List<ResumeJob> Experience { get; set; } = [];

    public List<ResumeEducation> Education { get; set; } = [];

    public List<string> Languages { get; set; } = [];

    /// <summary>Certificates, publications, open source, talks — only if the resume has them.</summary>
    public List<string> Extras { get; set; } = [];
}

public sealed record SkillGroup
{
    public string Group { get; set; } = "";

    public List<string> Items { get; set; } = [];
}

public sealed record ResumeJob
{
    public string Company { get; set; } = "";

    public string Role { get; set; } = "";

    public string Period { get; set; } = "";

    public string? Location { get; set; }

    /// <summary>One line about the product/domain (optional).</summary>
    public string? Context { get; set; }

    public List<string> Bullets { get; set; } = [];

    /// <summary>The technologies of this job as the source lists them, the vacancy's first: skills in context, as ATS and recruiters read them.</summary>
    public List<string> Stack { get; set; } = [];
}

public sealed record ResumeEducation
{
    public string Institution { get; set; } = "";

    public string Degree { get; set; } = "";

    public string Period { get; set; } = "";
}

/// <summary>A requirement of the vacancy and what of the source answers it: the plan the resume is written by (never printed).</summary>
public sealed record RequirementMatch
{
    public string Requirement { get; set; } = "";

    /// <summary>Required by the vacancy (not "nice to have").</summary>
    public bool MustHave { get; set; }

    /// <summary>The fragment of the resume or the stories that proves it, verbatim; empty = the candidate has no such fact (a gap).</summary>
    public string Evidence { get; set; } = "";

    /// <summary>Where the resume shows it ("summary", "skills", "Solvintech: first bullet").</summary>
    public string Placement { get; set; } = "";
}

/// <summary>
/// An independent recruiter's reading of the finished resume as the PDF gives it to an ATS and a person: a checklist of concrete criteria
/// (<see cref="Criteria"/>), each failure with its quote, and free remarks. The score is the code's, from the checklist
/// (<see cref="Scored"/>): 10 minus 3 for every critical failure, 1.5 for a major one, 0.5 for a minor one — so 9 means no critical and
/// no major failure, the same way every time.
/// </summary>
public sealed record ResumeReview
{
    /// <summary>1-10, computed from <see cref="Checks"/>.</summary>
    public int Score { get; set; } = 1;

    public string Verdict { get; set; } = "";

    public List<ReviewCheck> Checks { get; set; } = [];

    public List<ResumeIssue> Issues { get; set; } = [];

    /// <summary>Criterion id → weight and what it asks (the prompt lists the same).</summary>
    public static readonly IReadOnlyDictionary<string, (double Weight, string Question)> Criteria = new Dictionary<string, (double, string)>
    {
        ["truth"] = (3, "нет утверждений, которых нет в source, implied или фактах"),
        ["summary_proofs"] = (3, "«О себе»: два конкретных результата с цифрами под обязательные требования вакансии"),
        ["first_bullets"] = (1.5, "первый пункт каждого места работы — самый сильный для этой вакансии"),
        ["keywords"] = (1.5, "обязательные требования, которые у кандидата есть, названы словами вакансии"),
        ["no_filler"] = (1.5, "нет общих фраз без факта, «работал с», пунктов без результата"),
        ["logistics"] = (1.5, "формат работы, оформление, переезд — как в подтверждённых фактах"),
        ["no_repeat"] = (0.5, "одна и та же фраза не повторяется (кратко в «О себе» и подробно в опыте — это нормально)"),
        ["readability"] = (0.5, "пункты не длиннее двух строк, единый стиль, без канцелярита"),
    };

    /// <summary>The score of the checklist: a criterion not answered counts as failed.</summary>
    public ResumeReview Scored()
    {
        var failed = Criteria.Keys.Where(id => Checks.FirstOrDefault(c => c.Id == id) is not { Passed: true }).ToList();
        var score = 10 - failed.Sum(id => Criteria[id].Weight);
        return this with { Score = (int)Math.Clamp(Math.Floor(score), 1, 10) };
    }

    public bool HasCompleteChecklist() => Checks.Count == Criteria.Count && Checks.All(check => Criteria.ContainsKey(check.Id))
        && Checks.Select(check => check.Id).Distinct(StringComparer.Ordinal).Count() == Criteria.Count;
}

public sealed record ReviewCheck
{
    /// <summary>One of <see cref="ResumeReview.Criteria"/>.</summary>
    public string Id { get; set; } = "";

    public bool Passed { get; set; }

    /// <summary>For a failure: the text of the PDF that shows it, verbatim (empty for something missing).</summary>
    public string Quote { get; set; } = "";

    public string Note { get; set; } = "";
}

public sealed record ResumeIssue
{
    /// <summary>"summary", "навыки", "Custom Games Studio, пункт 3", "макет".</summary>
    public string Where { get; set; } = "";

    /// <summary>The text of the PDF the remark is about, verbatim; empty only for something missing. Code drops a remark whose quote is not there.</summary>
    public string Quote { get; set; } = "";

    public string Problem { get; set; } = "";

    /// <summary>The change that removes it, using only facts of the source.</summary>
    public string Fix { get; set; } = "";
}

/// <summary>Everything known about a tailored resume besides its content: the plan, the review, the page checks.</summary>
public sealed record ResumeReport
{
    public bool Ready { get; set; }

    public string PdfHash { get; set; } = "";

    public string InputHash { get; set; } = "";
    public Artifacts.ArtifactRecipe? Recipe { get; set; }

    public List<string> Violations { get; set; } = [];

    public List<RequirementMatch> Requirements { get; set; } = [];

    public ResumeReview? Review { get; set; }

    /// <summary>Pages of the PDF.</summary>
    public int Pages { get; set; }

    /// <summary>What the page checks still found after the layout fixes (a job split from its heading, a heading alone at a page bottom…).</summary>
    public List<string> Layout { get; set; } = [];

    /// <summary>Vacancy terms the candidate has (by the source) that the text still does not show in the vacancy's words.</summary>
    public List<string> Unshown { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResumeDocument))]
[JsonSerializable(typeof(ResumeReport))]
[JsonSerializable(typeof(ResumeReview))]
public sealed partial class ResumeJsonContext : JsonSerializerContext;
