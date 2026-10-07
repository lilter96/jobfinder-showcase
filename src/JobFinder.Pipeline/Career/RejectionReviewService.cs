using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Llm.Tasks;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// The work on mistakes. Every sent response, the system's and the user's own on the site, with its vacancy's evaluation, the resume's
/// coverage of the vacancy keywords and how it ended; the numbers per outcome (<see cref="RejectionAnalysis"/>) and, on request, the
/// model's reading of them: reasons with evidence, resume fixes, search fixes.
/// </summary>
public sealed class RejectionReviewService(IDbContextFactory<JobFinderDbContext> db, ProfileProvider profiles,
    RejectionReviewSources sources, TimeProvider time)
{
    public const string LatestKey = "analysis.rejections";
    public const string LatestAtKey = "analysis.rejectionsAt";

    /// <summary>The note the hh negotiations job leaves once it has read a response's whole chat.</summary>
    public const string ChatHistoryMarker = "hhchat-history:";

    /// <summary>A review needs a few rejections to compare; below that it would be guessing.</summary>
    public const int MinRejections = 5;

    /// <summary>What an ATS matches the vacancy keywords against: the resume text and the profile's skill names.</summary>
    public async Task<string> ResumeTextAsync(CancellationToken ct) =>
        (profiles.ResumeText() ?? "") + " " + string.Join(" ", (await profiles.GetAsync(ct))?.Skills.Select(s => s.Name) ?? []);

    public async Task<List<ResponseFacts>> FactsAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var apps = await ctx.Applications.AsNoTracking().Include(a => a.Vacancy).Include(a => a.Events)
            .Where(a => a.Status == ApplicationStatus.Sent && a.Stage != ApplicationStage.Withdrawn).ToListAsync(ct);
        var resume = await ResumeTextAsync(ct);
        var now = time.GetUtcNow();
        return apps.Where(a => a.Vacancy is not null).Select(a => Facts(a, resume, now)).ToList();
    }

    /// <param name="a">With its vacancy and events loaded.</param>
    public static ResponseFacts Facts(Application a, string resumeText, DateTimeOffset now)
    {
        var v = a.Vacancy!;
        var sent = a.SentAt ?? a.CreatedAt;
        var outcome = RejectionAnalysis.OutcomeOf(a.Stage, sent, now);
        var (covered, missing) = RejectionAnalysis.Coverage(resumeText, v.Evaluation?.Keywords ?? []);
        double? days = outcome is ResponseOutcome.Rejected or ResponseOutcome.Positive && a.StageChangedAt is { } at
            ? Math.Max(0, (at - sent).TotalDays)
            : null;
        // A letter: the system's own, or a long message of ours in the hh chat (the site shows the letter there). For a response made on
        // the site nobody knows before its chat history is read: unknown is not "without".
        bool? letter = !string.IsNullOrWhiteSpace(a.CoverLetter) || a.Events.Any(e => e.Kind == EventKind.MessageOut && e.Text?.Length > 200)
            ? true
            : a.Mode != ApplicationMode.External || a.Events.Any(e => e.ExternalId != null && e.ExternalId.StartsWith(ChatHistoryMarker, StringComparison.Ordinal))
                ? false
                : null;
        var employer = a.Events.Where(e => e.Kind == EventKind.MessageIn && !string.IsNullOrWhiteSpace(e.Text))
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id).FirstOrDefault()?.Text;
        return new ResponseFacts(a.Id, v.Id, v.Title, v.CompanyName, outcome, v.Score, v.Evaluation, v.SalaryAdjustment, v.EnglishRequired, covered,
            missing, days, letter, employer);
    }

    public static string OutcomeLabel(ResponseOutcome o) => o switch
    {
        ResponseOutcome.Positive => "ответ",
        ResponseOutcome.Rejected => "отказ",
        ResponseOutcome.Silent => "молчание",
        _ => "ждут",
    };

    /// <summary>The computed facts as the model reads them: groups, keyword gaps, rejection speed, one line per decided response.</summary>
    public static string Describe(IReadOnlyCollection<ResponseFacts> facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Группы (средние; компоненты 0-100; ATS = доля ключей вакансии, найденных в резюме)");
        foreach (var outcome in Enum.GetValues<ResponseOutcome>())
        {
            var g = RejectionAnalysis.Group(outcome, facts);
            if (g.Count == 0) continue;
            sb.Append("- ").Append(OutcomeLabel(outcome)).Append(": ").Append(g.Count).Append(" откликов; скор ").Append(N(g.Score))
                .Append(", стек ").Append(N(g.Stack)).Append(", грейд ").Append(N(g.Seniority)).Append(", домен ").Append(N(g.Domain))
                .Append(", формат ").Append(N(g.Format)).Append(", ATS ").Append(g.Coverage is { } c ? $"{c:P0}" : "—")
                .Append("; английский B2+: ").Append(g.EnglishB2Plus).Append(", формат/локация < 50: ").Append(g.FormatWeak)
                .Append(", зарплата ниже ожиданий: ").Append(g.SalaryBelow).Append(", фильтр отсеял бы: ").Append(g.FilterWouldReject)
                .Append(", с письмом: ").Append(g.WithLetter).Append(" из ").Append(g.LetterKnown).Append(" известных")
                .Append(", медиана дней до исхода: ").Append(N(g.MedianDays)).AppendLine();
        }

        sb.AppendLine().AppendLine("## Нет в резюме (сколько откликов этой группы требовали)");
        sb.Append("- у отказов: ").AppendLine(Keywords(RejectionAnalysis.MissingKeywords(facts, ResponseOutcome.Rejected)));
        sb.Append("- у ответов: ").AppendLine(Keywords(RejectionAnalysis.MissingKeywords(facts, ResponseOutcome.Positive)));
        sb.Append("- у молчания: ").AppendLine(Keywords(RejectionAnalysis.MissingKeywords(facts, ResponseOutcome.Silent)));

        sb.AppendLine().Append("## Скорость отказов: ")
            .AppendLine(string.Join(", ", RejectionAnalysis.RejectionSpeed(facts).Select(b => $"{b.Bucket} {b.Count}")));

        sb.AppendLine().AppendLine("## Отклики");
        foreach (var f in facts.Where(f => f.Outcome != ResponseOutcome.Waiting).OrderBy(f => f.Outcome).ThenByDescending(f => f.Score).Take(150))
        {
            sb.Append('[').Append(OutcomeLabel(f.Outcome)).Append("] ").Append(f.Title).Append(" — ").Append(f.Company ?? "?")
                .Append(" · скор ").Append(f.Score?.ToString(CultureInfo.InvariantCulture) ?? "—");
            if (f.Evaluation is { } e)
                sb.Append(" · стек ").Append(e.StackMatch).Append(" грейд ").Append(e.SeniorityMatch).Append(" домен ").Append(e.DomainMatch)
                    .Append(" формат ").Append(e.FormatMatch);
            if (f.Missing.Count > 0) sb.Append(" · нет в резюме: ").Append(string.Join(", ", f.Missing.Take(8)));
            if (f.Suspects() is { Count: > 0 } suspects) sb.Append(" · факторы: ").Append(string.Join("; ", suspects));
            if (f.DaysToOutcome is { } d) sb.Append(" · ").Append(d.ToString("0.#", CultureInfo.InvariantCulture)).Append(" дн.");
            if (f.EmployerText is { Length: > 0 } text) sb.Append(" · работодатель: «").Append(TextUtil.Truncate(Flat(text), 220)).Append('»');
            sb.AppendLine();
        }

        return sb.ToString();

        static string N(double? v) => v is null ? "—" : v.Value.ToString("0.#", CultureInfo.InvariantCulture);
        static string Keywords(List<(string Keyword, int Count)> list) => list.Count == 0 ? "—" : string.Join(", ", list.Select(k => $"{k.Keyword} {k.Count}"));
        static string Flat(string text) => string.Join(' ', text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    public async Task<ActionResult> ReviewAsync(CancellationToken ct) => (await sources.RequestAsync(ct)).Result;

    public Task<(RejectionReview? Review, DateTimeOffset? At)> LatestAsync(CancellationToken ct) => sources.LatestAsync(ct);
}
