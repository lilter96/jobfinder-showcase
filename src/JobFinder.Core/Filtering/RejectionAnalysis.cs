using JobFinder.Core.Domain;
using JobFinder.Core.Resume;

namespace JobFinder.Core.Filtering;

/// <summary>How a response ended, for the review: what worked against what did not.</summary>
public enum ResponseOutcome
{
    /// <summary>The employer answered: a question, an invitation, an offer.</summary>
    Positive,
    Rejected,

    /// <summary>No answer for <see cref="RejectionAnalysis.SilentAfterDays"/> days (or marked so).</summary>
    Silent,

    /// <summary>Too fresh to judge.</summary>
    Waiting,
}

/// <summary>What is known about one response: the vacancy's demands, how the resume covers them, how and when it ended.</summary>
public sealed record ResponseFacts(
    long ApplicationId,
    long VacancyId,
    string Title,
    string? Company,
    ResponseOutcome Outcome,
    int? Score,
    VacancyEvaluation? Evaluation,
    int SalaryAdjustment,
    EnglishLevel English,
    IReadOnlyList<string> Covered,
    IReadOnlyList<string> Missing,
    double? DaysToOutcome,
    bool? LetterSent,
    string? EmployerText)
{
    public double? Coverage => Covered.Count + Missing.Count == 0 ? null : (double)Covered.Count / (Covered.Count + Missing.Count);

    /// <summary>The factors that plausibly cost this response, strongest first; empty when nothing stands out.</summary>
    public List<string> Suspects()
    {
        var list = new List<string>();
        if (Evaluation is { } e)
        {
            foreach (var flag in e.RedFlags.Where(f => f.StartsWith("фильтр системы", StringComparison.Ordinal))) list.Add(flag);
            if (English >= EnglishLevel.B2) list.Add($"английский {English}");
            if (e.FormatMatch < RejectionAnalysis.WeakMatch) list.Add($"формат/локация {e.FormatMatch}");
            if (e.SeniorityMatch < RejectionAnalysis.WeakMatch) list.Add($"грейд {e.SeniorityMatch}");
            if (e.StackMatch < RejectionAnalysis.WeakMatch) list.Add($"стек {e.StackMatch}");
            if (e.DomainMatch < RejectionAnalysis.WeakMatch) list.Add($"домен {e.DomainMatch}");
        }

        if (SalaryAdjustment < 0) list.Add("зарплата ниже ожиданий");
        if (Coverage is < RejectionAnalysis.WeakCoverage && Missing.Count > 0)
            list.Add($"ATS: нет в резюме {string.Join(", ", Missing.Take(5))}");
        if (LetterSent == false) list.Add("без письма");
        return list;
    }
}

/// <summary>Numbers of one outcome group.</summary>
public sealed record OutcomeGroup(
    ResponseOutcome Outcome,
    int Count,
    double? Score,
    double? Stack,
    double? Seniority,
    double? Domain,
    double? Format,
    double? Coverage,
    int EnglishB2Plus,
    int FormatWeak,
    int SalaryBelow,
    int FilterWouldReject,
    int WithLetter,
    int LetterKnown,
    double? MedianDays);

/// <summary>
/// The rejection review's arithmetic: responses split by outcome, the same factors averaged per group, and the vacancy keywords the
/// resume does not show, counted among rejections and among positive answers. The model reads these numbers; it does not compute them.
/// </summary>
public static class RejectionAnalysis
{
    public const int SilentAfterDays = 14;

    /// <summary>A component score below this reads as a mismatch.</summary>
    public const int WeakMatch = 50;

    /// <summary>Below this share of the vacancy's keywords present in the resume an ATS likely ranks it low.</summary>
    public const double WeakCoverage = 0.6;

    public static ResponseOutcome OutcomeOf(ApplicationStage? stage, DateTimeOffset sentAt, DateTimeOffset now) => stage switch
    {
        ApplicationStage.Rejected => ResponseOutcome.Rejected,
        ApplicationStage.Replied or ApplicationStage.HrScreen or ApplicationStage.TechInterview or ApplicationStage.Final or ApplicationStage.Offer
            or ApplicationStage.Accepted => ResponseOutcome.Positive,
        ApplicationStage.NoResponse => ResponseOutcome.Silent,
        _ => (now - sentAt).TotalDays >= SilentAfterDays ? ResponseOutcome.Silent : ResponseOutcome.Waiting,
    };

    /// <summary>The vacancy keywords present in the resume text (technology spelling ignored: "ASP.NET Core" = "asp.netcore").</summary>
    public static (List<string> Covered, List<string> Missing) Coverage(string resumeText, IEnumerable<string> keywords)
    {
        var text = ResumeFaithfulness.TechForm(resumeText);
        var covered = new List<string>();
        var missing = new List<string>();
        foreach (var k in keywords.Where(k => !string.IsNullOrWhiteSpace(k)).DistinctBy(ResumeFaithfulness.TechForm))
            (text.Contains(ResumeFaithfulness.TechForm(k), StringComparison.Ordinal) ? covered : missing).Add(k);
        return (covered, missing);
    }

    public static OutcomeGroup Group(ResponseOutcome outcome, IReadOnlyCollection<ResponseFacts> all)
    {
        var g = all.Where(f => f.Outcome == outcome).ToList();
        var evaluated = g.Where(f => f.Evaluation is not null).Select(f => f.Evaluation!).ToList();
        var days = g.Where(f => f.DaysToOutcome is not null).Select(f => f.DaysToOutcome!.Value).Order().ToList();
        return new OutcomeGroup(
            outcome,
            g.Count,
            Avg(g.Where(f => f.Score is not null).Select(f => (double)f.Score!.Value)),
            Avg(evaluated.Select(e => (double)e.StackMatch)),
            Avg(evaluated.Select(e => (double)e.SeniorityMatch)),
            Avg(evaluated.Select(e => (double)e.DomainMatch)),
            Avg(evaluated.Select(e => (double)e.FormatMatch)),
            Avg(g.Where(f => f.Coverage is not null).Select(f => f.Coverage!.Value)),
            g.Count(f => f.English >= EnglishLevel.B2),
            evaluated.Count(e => e.FormatMatch < WeakMatch),
            g.Count(f => f.SalaryAdjustment < 0),
            g.Count(f => f.Evaluation?.RedFlags.Any(r => r.StartsWith("фильтр системы", StringComparison.Ordinal)) == true),
            g.Count(f => f.LetterSent == true),
            g.Count(f => f.LetterSent is not null),
            days.Count == 0 ? null : days[days.Count / 2]);
    }

    /// <summary>Missing keywords by how many responses of the outcome lacked them, most frequent first.</summary>
    public static List<(string Keyword, int Count)> MissingKeywords(IEnumerable<ResponseFacts> facts, ResponseOutcome outcome, int top = 20) =>
        facts.Where(f => f.Outcome == outcome)
            .SelectMany(f => f.Missing.DistinctBy(ResumeFaithfulness.TechForm))
            .GroupBy(ResumeFaithfulness.TechForm)
            .Select(g => (Keyword: g.First(), Count: g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Keyword, StringComparer.OrdinalIgnoreCase)
            .Take(top).ToList();

    /// <summary>How fast rejections came: within a day (a filter or a glance), a few days, a week, later.</summary>
    public static List<(string Bucket, int Count)> RejectionSpeed(IEnumerable<ResponseFacts> facts)
    {
        var days = facts.Where(f => f.Outcome == ResponseOutcome.Rejected && f.DaysToOutcome is not null).Select(f => f.DaysToOutcome!.Value).ToList();
        return
        [
            ("до суток", days.Count(d => d < 1)),
            ("1–3 дня", days.Count(d => d is >= 1 and < 3)),
            ("3–7 дней", days.Count(d => d is >= 3 and < 7)),
            ("неделя и дольше", days.Count(d => d >= 7)),
        ];
    }

    private static double? Avg(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : list.Average();
    }
}
