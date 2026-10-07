using System.Security.Cryptography;
using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Integrations.Browser;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Profile;

namespace JobFinder.Pipeline.Modules.Preparation;

public interface IResumePreparationSteps
{
    Task<ResumeRound> DraftAsync(ResumePreparationInput input, int round, ResumeRound? best, CancellationToken ct);
    Task<ResumeRound> PrintAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct);
    Task<ResumeRound> ReviewAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct);
    Task<TailoredResume> FinishAsync(ResumePreparationInput input, ResumeRound best, CancellationToken ct);
}

public sealed class ResumePreparationSteps(JobFinderLlm llm, BrowserSessions browser, DocumentAssets assets, PathsOptions paths,
    TimeProvider time, ILogger<ResumePreparationSteps> log) : IResumePreparationSteps
{
    public static ArtifactRecipe Recipe(JobFinderLlm operation, ResumePreparationInput input) => operation.Recipe(
        [(LlmTaskCatalog.TailorResume, LlmPurpose.Resume), (LlmTaskCatalog.ReviewResume, LlmPurpose.Resume)],
        new Dictionary<string, string?>
        {
            ["profile"] = ProfileProvider.ToJson(input.Profile), ["resume"] = input.ResumeText, ["stories"] = input.StoriesText,
            ["facts"] = input.FactsText, ["vacancy"] = JobFinderLlm.VacancyText(input.Vacancy.ToVacancy()),
            ["language"] = input.Vacancy.Language, ["keywords"] = string.Join("\n", input.Vacancy.Keywords),
        }, new Dictionary<string, string> { ["quality"] = "resume-quality-v2" });

    private JobFinderLlm Operation(ResumePreparationInput input)
    {
        if (input.SchemaVersion != 1 || input.Vacancy.Id <= 0 || string.IsNullOrWhiteSpace(input.ResumeText))
            throw new ArgumentException("invalid resume preparation inputs");
        var operation = llm.RestoreOperation(input.Operation);
        if (input.Recipe.OperationId != input.Operation.OperationId || input.Recipe.PolicyRevision != input.Operation.PolicyRevision
            || Recipe(operation, input).Hash != input.Recipe.Hash) throw new ResumePreparationSupersededException();
        return operation;
    }

    public async Task<ResumeRound> DraftAsync(ResumePreparationInput input, int round, ResumeRound? best, CancellationToken ct)
    {
        var operation = Operation(input);
        var source = Source(input);
        var draft = await operation.TailorResumeAsync(input.ResumeText, input.StoriesText, input.FactsText,
            TechThesaurus.ImpliedBy(new TechThesaurus.Text(source)), input.Vacancy.ToVacancy(), input.Vacancy.Keywords,
            input.Vacancy.Language == "en" ? "en" : "ru", best is null ? null : Feedback(best), ct, best?.Document);
        string[] header = [input.Profile.Name, input.Profile.Location, .. new[] { input.Profile.Contacts.Email, input.Profile.Contacts.Phone, input.Profile.Contacts.Telegram }.OfType<string>()];
        var repaired = ResumeFaithfulness.Check(draft.Value.Document, source, input.Vacancy.Keywords, true, input.Profile.Experience, header);
        var final = ResumeFaithfulness.Check(repaired.Document, source, input.Vacancy.Keywords, false, input.Profile.Experience, header);
        return new ResumeRound
        {
            Number = round, Document = final.Document, Requirements = draft.Value.Requirements, Violations = final.Violations,
            Corrections = repaired.Corrections, Covered = final.Covered, Missing = final.Missing, Unshown = final.Unshown,
            Model = draft.Model, CostUsd = draft.CostUsd,
        };
    }

    public async Task<ResumeRound> PrintAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct)
    {
        Operation(input);
        var directory = Path.GetFullPath(Path.Combine(paths.Data, "resumes", input.Vacancy.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), input.Operation.OperationId.ToString("N")));
        var breaks = new HashSet<string>(StringComparer.Ordinal);
        var whole = true;
        for (var pass = 1; ; pass++)
        {
            var path = Path.Combine(directory, $"r{current.Number}p{pass}.pdf");
            await browser.RenderPdfAsync(ResumeHtml.Render(current.Document, input.Profile, assets.FontCss, breaks, wholeJobs: whole), path, ct);
            var content = await File.ReadAllBytesAsync(path, ct);
            var layout = ResumePdfCheck.Check(content, current.Document, input.Profile);
            if (whole && layout.Pages > ResumePdfCheck.MaxPages) { whole = false; continue; }
            var next = layout.Issues.Select(issue => issue.Key).FirstOrDefault(key => key is not null && !breaks.Contains(key));
            if (next is null || pass >= 4)
                return current with
                {
                    PdfPath = StoredPath.ToStored(path), PdfHash = Hash(content), Pages = layout.Pages,
                    PageTexts = [.. layout.PageTexts], Layout = layout.Issues.Select(issue => issue.Message).ToList(),
                    LayoutFeedback = layout.Issues.Where(issue => issue.Key is null).Select(issue => issue.Message).ToList(),
                };
            breaks.Add(next);
            log.Info($"[resume] #{input.Vacancy.Id}: page break before {next}");
        }
    }

    public async Task<ResumeRound> ReviewAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct)
    {
        var operation = Operation(input);
        var layout = await ReadPrintAsync(input, current, ct);
        var review = await operation.ReviewResumeAsync(input.Vacancy.ToVacancy(), ResumePdfCheck.ForReview(layout), input.ResumeText,
            input.StoriesText, input.FactsText, TechThesaurus.ImpliedBy(new TechThesaurus.Text(Source(input))), ct);
        return current with { Review = ResumeTailor.Grounded(review.Value, layout), CostUsd = current.CostUsd + review.CostUsd };
    }

    public async Task<TailoredResume> FinishAsync(ResumePreparationInput input, ResumeRound best, CancellationToken ct)
    {
        Operation(input);
        await ReadPrintAsync(input, best, ct);
        var report = new ResumeReport
        {
            Requirements = best.Requirements, Review = best.Review, Pages = best.Pages, Layout = best.Layout, Unshown = best.Unshown,
            Ready = best.Ready, Violations = best.Violations, PdfHash = best.PdfHash!, InputHash = input.Recipe.Hash, Recipe = input.Recipe,
        };
        return new TailoredResume
        {
            VacancyId = input.Vacancy.Id, Language = best.Document.Language,
            ContentJson = JsonSerializer.Serialize(best.Document, ResumeJsonContext.Default.ResumeDocument),
            ReportJson = JsonSerializer.Serialize(report, ResumeJsonContext.Default.ResumeReport), PdfPath = best.PdfPath!,
            FileName = ResumeTailor.FileName(input.Profile, best.Document), KeywordsCovered = best.Covered, KeywordsMissing = best.Missing,
            Corrections = best.Corrections, Model = best.Model, CreatedAt = time.GetUtcNow(),
        };
    }

    private static string Source(ResumePreparationInput input) => string.Join("\n", input.ResumeText, input.StoriesText,
        JsonSerializer.Serialize(input.Profile, ProfileJsonContext.Default.CandidateProfile), input.FactsText);
    private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task<PdfLayout> ReadPrintAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct)
    {
        try
        {
            if (current.PdfPath is null || current.PdfHash is null) throw new ResumePrintChangedException();
            var bytes = await File.ReadAllBytesAsync(StoredPath.Resolve(current.PdfPath), ct);
            if (Hash(bytes) != current.PdfHash) throw new ResumePrintChangedException();
            var layout = ResumePdfCheck.Check(bytes, current.Document, input.Profile);
            if (layout.Pages != current.Pages || !layout.PageTexts.SequenceEqual(current.PageTexts)
                || !layout.Issues.Select(issue => issue.Message).SequenceEqual(current.Layout)) throw new ResumePrintChangedException();
            return layout;
        }
        catch (IOException) { throw new ResumePrintChangedException(); }
    }

    private static string Feedback(ResumeRound best)
    {
        var lines = new List<string>();
        if (best.Review is { } review)
        {
            lines.Add($"Рецензия рекрутера: {review.Scored().Score}/10 — {review.Verdict}");
            lines.AddRange(review.Checks.Where(check => !check.Passed).Select(check => $"не выполнено «{ResumeReview.Criteria.GetValueOrDefault(check.Id).Question ?? check.Id}»: {check.Note}"));
            lines.AddRange(review.Issues.Select(issue => $"{issue.Where}: {issue.Problem} → {issue.Fix}"));
        }
        lines.AddRange(best.LayoutFeedback.Select(issue => $"вёрстка: {issue} — сократи текст"));
        lines.AddRange(best.Violations);
        if (best.Unshown.Count > 0) lines.Add($"напиши словами вакансии (это правда): {string.Join(", ", best.Unshown)}");
        return string.Join("\n", lines.Select(line => "- " + line));
    }
}
