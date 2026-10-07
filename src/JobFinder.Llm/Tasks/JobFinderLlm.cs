using System.Globalization;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Contracts.Wire;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;

namespace JobFinder.Llm.Tasks;

/// <summary>
/// The domain-level LLM operations. Each one renders its prompt from <c>prompts/</c>, wraps untrusted text (posts, vacancies) in
/// tags the prompt declares as data, and returns a validated, typed answer (or text).
/// </summary>
public sealed partial class JobFinderLlm(LlmClient client, PromptCatalog prompts)
{
    private const int MaxVacancyChars = 9000;
    internal const int MaxPostChars = 6000;

    /// <summary>An extraction model is not parked now (see <see cref="LlmClient.MayAnswer"/>).</summary>
    public bool MayExtract => client.MayAnswer(LlmPurpose.Extract);

    public Task<bool> MayExtractAsync(CancellationToken ct) => client.MayAnswerAsync(LlmPurpose.Extract, ct);

    public long PolicyRevision => client.PolicyRevision;
    public IReadOnlyList<string> ConfiguredPurposes => Array.AsReadOnly(client.Options.Routes.Keys.ToArray());

    public async Task<JobFinderLlm> ForOperationAsync(CancellationToken ct) => new(await client.ForOperationAsync(ct), prompts);

    public Configuration.LlmOperationSnapshot CaptureOperation() => client.CaptureOperation();

    public JobFinderLlm RestoreOperation(Configuration.LlmOperationSnapshot snapshot) => new(client.RestoreOperation(snapshot), prompts);

    private const int MaxPageChars = 5000;
    private const int MaxPagesChars = 12000;

    /// <param name="links">Links the post's text hides (text links, buttons) with their captions.</param>
    /// <param name="pages">Pages the post links to, as read (the details and contacts often live there).</param>
    public async Task<LlmResult<PostExtraction>> ExtractPostAsync(string text, string sourceTitle, CancellationToken ct,
        IReadOnlyList<PostLink>? links = null, IReadOnlyList<LinkedPage>? pages = null)
    {
        var input = new Discovery.ExtractPostInput(text, sourceTitle, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), links, pages);
        var result = await Discovery.ExtractPostTask.Definition.ExecuteAsync(client, prompts, input, ct);
        return new LlmResult<PostExtraction>(result.Value.ToDomain(), result.Model, result.CostUsd);
    }

    /// <summary>
    /// The model's view of a post: the text, its hidden links with captions, and the pages it links to, each in a tag the prompt declares
    /// as data. Pages share a budget in the order given (the post's own links first), each cut to its share.
    /// </summary>
    public static string PostInput(string text, string sourceTitle, IReadOnlyList<PostLink>? links, IReadOnlyList<LinkedPage>? pages)
    {
        var sb = new StringBuilder($"Источник: {sourceTitle}\n<post>\n{TextUtil.Truncate(text, MaxPostChars)}\n</post>");
        if (links is { Count: > 0 })
        {
            sb.Append("\n<links>");
            foreach (var link in links.Take(30)) sb.Append("\n- ").Append(link.Label is null ? "" : link.Label.Replace('\n', ' ') + ": ").Append(link.Url);
            sb.Append("\n</links>");
        }

        var left = MaxPagesChars;
        foreach (var page in pages ?? [])
        {
            if (left <= 200) break;
            var body = TextUtil.Truncate(page.Text, Math.Min(MaxPageChars, left));
            left -= body.Length;
            sb.Append("\n<page url=\"").Append(page.Url.Replace("\"", "%22")).Append("\">\n");
            if (!string.IsNullOrWhiteSpace(page.Title)) sb.Append(page.Title).Append('\n');
            sb.Append(body).Append("\n</page>");
        }

        return sb.ToString();
    }

    public Task<LlmResult<VacancyScoring>> ScoreAsync(CandidateProfile profile, Vacancy vacancy, string salaryNote, CancellationToken ct)
    {
        return LlmTaskCatalog.Score.ExecuteAsync(client, prompts,
            new ScoreInput(profile, vacancy, salaryNote), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <param name="remarks">What the style check found in the previous variants: the model rewrites all three with them in mind.</param>
    public Task<LlmResult<CommentPlan>> PlanCommentAsync(CandidateProfile profile, string author, string postText, CancellationToken ct,
        string? remarks = null)
    {
        return LlmTaskCatalog.PlanComment.ExecuteAsync(client, prompts,
            new PlanCommentInput(profile, author, postText, remarks), ct).MapAsync(static wire => wire.ToDomain());
    }

    public Task<LlmResult<CandidateProfile>> ImportResumeAsync(string resumeText, CancellationToken ct)
    {
        return LlmTaskCatalog.ImportResume.ExecuteAsync(client, prompts,
            new ImportResumeInput(resumeText), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <summary>
    /// A probe of every model of every route, bypassing the rotation (<c>--test-llm</c>): a route is only as good as the models that can
    /// actually return its JSON.
    /// </summary>
    public async IAsyncEnumerable<(string Purpose, string Model, bool Ok, string Detail, long Ms)> ProbeRoutesAsync(OpenRouterTransport transport,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, string? onlyPurpose = null)
    {
        var operation = await client.ForOperationAsync(ct);
        var options = operation.Options;
        if (onlyPurpose is not null && !options.Routes.Keys.Any(purpose => purpose.Equals(onlyPurpose, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Unknown LLM route", nameof(onlyPurpose));
        foreach (var purpose in options.Routes.Keys.Where(purpose => onlyPurpose is null || purpose.Equals(onlyPurpose, StringComparison.OrdinalIgnoreCase)))
        foreach (var model in Configuration.LlmPolicyCompiler.Route(options, purpose))
        foreach (var task in LlmTaskCatalog.All.Where(task => task.Purposes.Contains(purpose, StringComparer.OrdinalIgnoreCase)))
        {
            var probe = task.Probe(purpose);
            var system = prompts.Render(task.Prompt, probe.Payload.Bindings);
            var recipe = GenerationRecipe.Call(task, probe.Payload, prompts, operation);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string detail;
            var ok = false;
            try
            {
                var result = await operation.ProbeAsync(transport, model, probe, system, recipe, ct);
                ok = true;
                detail = $"{task.Id}: valid response ${result.CostUsd:0.#####}";
            }
            catch (Exception error) when (error is LlmCallException or JsonException)
            {
                detail = $"{task.Id}: {error.Message}";
            }

            yield return (purpose, model.Name, ok, detail, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// The resume rewritten for one vacancy. <paramref name="feedback"/> is the faithfulness gate's list of violations of a previous attempt:
    /// the second attempt is told exactly which numbers and skills it may not use.
    /// </summary>
    /// <param name="feedback">What the code checks or the review found in the previous version.</param>
    /// <param name="facts">The candidate's confirmed answers (formats, relocation, employment): newer than the resume, they win for logistics.</param>
    /// <param name="implied">What the experience implies (<c>TechThesaurus.ImpliedBy</c>): allowed to write.</param>
    /// <param name="current">The version to improve (from the second round): the model fixes what the remarks name and keeps the rest.</param>
    public Task<LlmResult<ResumeDraft>> TailorResumeAsync(string resumeText, string storiesText, string facts, string implied, Vacancy vacancy,
        IReadOnlyCollection<string> keywords, string language, string? feedback, CancellationToken ct, Core.Resume.ResumeDocument? current = null)
    {
        return LlmTaskCatalog.TailorResume.ExecuteAsync(client, prompts,
            new TailorResumeInput(resumeText, storiesText, facts, implied, vacancy, keywords, language, feedback, current), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <summary>
    /// A recruiter's reading of the finished resume as the PDF gives it (the text of each page), against the vacancy and the source facts.
    /// </summary>
    /// <param name="facts">The candidate's confirmed answers: first, whole — newer than the resume, they decide logistics.</param>
    public Task<LlmResult<Core.Resume.ResumeReview>> ReviewResumeAsync(Vacancy vacancy, string pdfText, string resumeText, string storiesText, string facts,
        string implied, CancellationToken ct)
    {
        return LlmTaskCatalog.ReviewResume.ExecuteAsync(client, prompts,
            new ReviewResumeInput(vacancy, pdfText, resumeText, storiesText, facts, implied), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <summary>The profile as the prompts see it: compact JSON, so every prompt gets the same facts in the same shape.</summary>
    public static string ProfileText(CandidateProfile profile) => JsonSerializer.Serialize(profile, LlmJsonContext.Default.CandidateProfile);

    public static string VacancyText(Vacancy v, bool withEvaluation = false)
    {
        var sb = new StringBuilder();
        sb.Append("<vacancy>\n");
        sb.Append("Название: ").Append(v.Title).Append('\n');
        if (v.CompanyName is not null) sb.Append("Компания: ").Append(v.CompanyName).Append('\n');
        if (v.Company?.Industries.Count > 0) sb.Append("Отрасль компании: ").Append(string.Join(", ", v.Company.Industries)).Append('\n');
        if (v.City is not null || v.CountryCode is not null) sb.Append("Локация: ").Append(v.City).Append(' ').Append(v.CountryCode).Append('\n');
        if (v.WorkFormats != WorkFormat.None) sb.Append("Формат: ").Append(v.WorkFormats).Append('\n');
        if (v.SalaryFrom is not null || v.SalaryTo is not null)
            sb.Append("Зарплата: ").Append(v.SalaryFrom?.ToString("0", CultureInfo.InvariantCulture) ?? "?").Append(" – ")
                .Append(v.SalaryTo?.ToString("0", CultureInfo.InvariantCulture) ?? "?").Append(' ').Append(v.Currency)
                .Append(v.SalaryGross == true ? " gross" : v.SalaryGross == false ? " net" : "").Append('\n');
        if (v.Experience is not null) sb.Append("Опыт: ").Append(v.Experience).Append('\n');
        if (v.EnglishRequired != EnglishLevel.Unknown) sb.Append("Английский: ").Append(v.EnglishRequired).Append('\n');
        if (v.Skills.Count > 0) sb.Append("Навыки: ").Append(string.Join(", ", v.Skills)).Append('\n');
        sb.Append("Описание:\n").Append(TextUtil.Truncate(v.Description, MaxVacancyChars)).Append('\n');
        sb.Append("</vacancy>");
        if (withEvaluation && v.Evaluation is { } e)
        {
            sb.Append("\n\nСильные стороны кандидата для этой вакансии: ").Append(string.Join("; ", e.Strengths));
            if (e.Gaps.Count > 0) sb.Append("\nПробелы: ").Append(string.Join("; ", e.Gaps));
        }

        return sb.ToString();
    }
}
