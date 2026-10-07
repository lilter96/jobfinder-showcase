using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Documents;

public sealed record ResumePreparationTicket(TailoredResume? Cached, Guid? RequestId);

public sealed class ResumeTailor(IDbContextFactory<JobFinderDbContext> db, JobFinderLlm llm, ProfileProvider profiles, FactsProvider facts,
    ResumePreparationRequests requests, TimeProvider time, ILogger<ResumeTailor> log)
{
    public const int MinReview = 9;

    public async Task<TailoredResume?> LatestAsync(long vacancyId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.TailoredResumes.AsNoTracking().Where(t => t.VacancyId == vacancyId).OrderByDescending(t => t.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// A resume of the current pipeline: planned, checked, printed, read back and reviewed (its report carries the review). One made
    /// before that (2026-09-29: «в предоставленном опыте не указаны» in the summary, jobs torn between pages) or one the review could not
    /// read (the models unreachable) is made again.
    /// </summary>
    public static bool Current(TailoredResume r)
    {
        try
        {
            if (r.ReportJson is not { } json || JsonSerializer.Deserialize(json, ResumeJsonContext.Default.ResumeReport) is not { } report)
                return false;
            return Ready(report) && Core.Options.StoredPath.Exists(r.PdfPath)
                   && report.PdfHash == PdfHash(Core.Options.StoredPath.Resolve(r.PdfPath));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool Ready(ResumeReport report) => report.Ready && report.Review is { } review && review.HasCompleteChecklist() && review.Scored().Score >= MinReview
        && report.Pages is > 0 and <= ResumePdfCheck.MaxPages && report.Layout.Count == 0 && report.Violations.Count == 0 && report.Unshown.Count == 0;

    private static string PdfHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public async Task<TailoredResume> GetOrCreateAsync(long vacancyId, CancellationToken ct, bool force = false)
    {
        var ticket = await RequestAsync(vacancyId, ct, force);
        return ticket.Cached ?? await requests.WaitAsync(ticket.RequestId!.Value, ct);
    }

    public async Task<ResumePreparationTicket> RequestAsync(long vacancyId, CancellationToken ct, bool force = false)
    {
        var input = await CaptureInputAsync(vacancyId, ct);
        if (!force && await LatestAsync(vacancyId, ct) is { } existing && existing.ReportJson is { } json
            && JsonSerializer.Deserialize(json, ResumeJsonContext.Default.ResumeReport) is { } report
            && report.InputHash == input.Recipe.Hash
            && (Current(existing) || existing.CreatedAt > time.GetUtcNow().AddHours(-1) && report is { Review: not null, Ready: false }))
            return new(existing, null);
        return new(null, await requests.EnqueueAsync(input, ct, force));
    }

    private async Task<ResumePreparationInput> CaptureInputAsync(long vacancyId, CancellationToken ct)
    {
        var profile = (await profiles.GetSnapshotAsync(ct))?.Value ?? throw new InvalidOperationException("нет профиля кандидата (--import-resume)");
        var resumeText = profiles.ResumeText() ?? throw new InvalidOperationException("нет profile/resume.pdf");
        var factsText = (await facts.GetSnapshotAsync(ct)).Value.ForPrompt();
        await using var context = await db.CreateDbContextAsync(ct);
        var vacancy = await context.Vacancies.AsNoTracking().Include(row => row.Company).SingleAsync(row => row.Id == vacancyId, ct);
        var stories = await context.Stories.AsNoTracking().Where(row => row.Confirmed).OrderBy(row => row.Id).ToListAsync(ct);
        var operation = await llm.ForOperationAsync(ct);
        var input = new ResumePreparationInput
        {
            Profile = profile, ResumeText = resumeText, FactsText = factsText, StoriesText = StoriesText(stories),
            Vacancy = ResumeVacancyInput.From(vacancy), Operation = operation.CaptureOperation(),
        };
        input.Recipe = ResumePreparationSteps.Recipe(operation, input);
        return input;
    }

    /// <summary>The attachment for a DM or an e-mail, only after the quality gate and a fresh PDF read-back.</summary>
    public async Task<EmailAttachment?> AttachmentAsync(long vacancyId, CancellationToken ct)
    {
        try
        {
            var tailored = await GetOrCreateAsync(vacancyId, ct);
            return await CheckAttachmentAsync(tailored, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn($"[resume] #{vacancyId}: attachment blocked ({e.Message})");
            throw;
        }
    }

    public async Task<EmailAttachment> CheckedAttachmentAsync(long vacancyId, long resumeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var tailored = await context.TailoredResumes.AsNoTracking().SingleOrDefaultAsync(row => row.Id == resumeId && row.VacancyId == vacancyId, ct)
            ?? throw new ResumeQualityException("сохранённое резюме не найдено");
        return await CheckAttachmentAsync(tailored, ct);
    }

    public async Task<EmailAttachment?> PreparedAttachmentAsync(long vacancyId, Guid requestId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var request = await context.ResumePreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == requestId && row.VacancyId == vacancyId, ct)
            ?? throw new ResumeQualityException("подготовка резюме не найдена");
        if (request.Stage is ResumePreparationStage.Failed or ResumePreparationStage.Cancelled)
            throw new ResumeQualityException(request.Error ?? "подготовка резюме остановлена");
        if (request.Stage != ResumePreparationStage.Completed) return null;
        return await CheckedAttachmentAsync(vacancyId, request.ResultId ?? throw new ResumeQualityException("нет результата подготовки резюме"), ct);
    }

    private async Task<EmailAttachment> CheckAttachmentAsync(TailoredResume tailored, CancellationToken ct)
    {
        if (!Current(tailored)) throw new ResumeQualityException("резюме не прошло проверку качества, фактов или PDF; отправка заблокирована");
        var profile = (await profiles.GetSnapshotAsync(ct))?.Value ?? throw new InvalidOperationException("нет профиля кандидата");
        var document = JsonSerializer.Deserialize(tailored.ContentJson, ResumeJsonContext.Default.ResumeDocument)
                       ?? throw new InvalidOperationException("нет документа резюме");
        var path = Core.Options.StoredPath.Resolve(tailored.PdfPath);
        var content = await File.ReadAllBytesAsync(path, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var report = JsonSerializer.Deserialize(tailored.ReportJson!, ResumeJsonContext.Default.ResumeReport)!;
        if (hash != report.PdfHash) throw new ResumeQualityException("PDF изменился после проверки; отправка заблокирована");
        var layout = ResumePdfCheck.Check(content, document, profile);
        if (layout.Issues.Count > 0) throw new ResumeQualityException("PDF не прошёл повторную проверку: " + string.Join("; ", layout.Issues.Select(issue => issue.Message)));
        return new EmailAttachment(path, tailored.FileName, tailored.Id, hash, content);
    }

    /// <summary>
    /// The remarks the PDF bears out: one that quotes text the PDF does not have is the reviewer misreading, and a revision by it would
    /// break what is right. A remark about something missing (no quote) stays.
    /// </summary>
    public static ResumeReview Grounded(ResumeReview review, PdfLayout layout)
    {
        var text = ResumePdfCheck.Compact(string.Concat(layout.PageTexts));
        bool InPdf(string quote) => string.IsNullOrWhiteSpace(quote) || text.Contains(ResumePdfCheck.Compact(quote), StringComparison.Ordinal);
        var kept = review.Issues.Where(i => InPdf(i.Quote)).ToList();
        var checks = review.Checks.Select(c => c.Passed || InPdf(c.Quote) ? c : c with { Quote = "", Note = "цитаты нет в PDF, нужна повторная проверка: " + c.Note }).ToList();
        return (review with { Issues = kept, Checks = checks }).Scored();
    }

    public static string StoriesText(IEnumerable<Story> stories)
    {
        var sb = new StringBuilder();
        foreach (var s in stories)
        {
            sb.Append("## ").Append(s.Title).Append('\n')
                .Append("Ситуация: ").Append(s.Situation).Append('\n')
                .Append("Задача: ").Append(s.Task).Append('\n')
                .Append("Действия: ").Append(s.Action).Append('\n')
                .Append("Результат: ").Append(s.Result).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.Reflection)) sb.Append("Вывод: ").Append(s.Reflection).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.RawNotes)) sb.Append("Заметки кандидата: ").Append(s.RawNotes).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// "Terentiy_Gatsukov_Senior_NET_Backend_Developer_CV.pdf": Latin letters, digits and underscores only — an ATS upload form, a mail
    /// client or a URL does not mangle it — first name, last name, the target role.
    /// </summary>
    public static string FileName(CandidateProfile profile, ResumeDocument doc)
    {
        var name = ResumeHtml.DisplayName(profile, en: true).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2);
        var role = (string.IsNullOrWhiteSpace(doc.Headline) ? profile.Title : doc.Headline)
            .Replace("C#/.NET", ".NET", StringComparison.OrdinalIgnoreCase).Replace("C# / .NET", ".NET", StringComparison.OrdinalIgnoreCase)
            .Replace("C#", "CSharp", StringComparison.OrdinalIgnoreCase).Replace(".NET", "NET", StringComparison.OrdinalIgnoreCase);
        var raw = string.Join("_", name.Append(ResumeHtml.Transliterate(role)).Append("CV"));
        var safe = System.Text.RegularExpressions.Regex.Replace(raw, "[^A-Za-z0-9]+", "_").Trim('_');
        return safe + ".pdf";
    }
}
