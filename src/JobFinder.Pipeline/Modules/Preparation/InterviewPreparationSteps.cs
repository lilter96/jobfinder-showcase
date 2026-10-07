using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Time;
using JobFinder.Integrations.Browser;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Notifications;
using UglyToad.PdfPig;

namespace JobFinder.Pipeline.Modules.Preparation;

public interface IInterviewPreparationSteps
{
    Task<InterviewPlanCheckpoint> PlanAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, CancellationToken ct);
    Task<InterviewCopilot> AnswerAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewPlanCheckpoint plan, CancellationToken ct);
    Task<InterviewPrintCheckpoint> PrintAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewCopilot copilot, CancellationToken ct);
}

public sealed class InterviewPreparationSteps(JobFinderLlm llm, GeneratedAnswers answers, BrowserSessions browser, DocumentAssets assets,
    PathsOptions paths, LocalClock clock, ILogger<InterviewPreparationSteps> log) : IInterviewPreparationSteps
{
    private readonly InterviewAnswerContent _content = new(llm, answers, log);
    private static readonly string[] PlanFacts =
        ["salary", "location", "work_format", "remote_from", "relocation", "english", "employment", "notice", "citizenship", "experience"];

    internal static ArtifactRecipe Recipe(JobFinderLlm operation, InterviewPreparationSeed seed) => operation.Recipe(
        [(LlmTaskCatalog.InterviewPlan, LlmPurpose.Interview), (LlmTaskCatalog.InterviewAnswer, LlmPurpose.Interview), (LlmTaskCatalog.InterviewAnswer, LlmPurpose.InterviewHigh),
            (LlmTaskCatalog.InterviewAnswer, LlmPurpose.InterviewMedium), (LlmTaskCatalog.InterviewAnswer, LlmPurpose.InterviewLow)],
        new Dictionary<string, string?> { ["source"] = seed.Source.Hash() },
        new Dictionary<string, string> { ["copilot"] = CopilotRules.RulesVersion, ["preparation"] = "interview-preparation-v1" });

    internal static ArtifactRecipe InputRecipe(JobFinderLlm operation, InterviewPreparationSeed seed, InterviewPreparationInput input) => operation.Recipe(
        [(LlmTaskCatalog.InterviewPlan, LlmPurpose.Interview), (LlmTaskCatalog.InterviewAnswer, LlmPurpose.Interview), (LlmTaskCatalog.InterviewAnswer, LlmPurpose.InterviewHigh),
            (LlmTaskCatalog.InterviewAnswer, LlmPurpose.InterviewMedium), (LlmTaskCatalog.InterviewAnswer, LlmPurpose.InterviewLow)],
        new Dictionary<string, string?>
        {
            ["seed"] = CanonicalJson.Hash(JsonSerializer.Serialize(seed, InterviewPreparationJsonContext.Default.InterviewPreparationSeed)),
            ["input"] = CanonicalJson.Hash(JsonSerializer.Serialize(input with { Recipe = new() }, InterviewPreparationJsonContext.Default.InterviewPreparationInput)),
        }, new Dictionary<string, string> { ["copilot"] = CopilotRules.RulesVersion, ["preparation"] = "interview-preparation-v1" });

    internal static InterviewPreparationInput Capture(InterviewPreparationSeed seed, JobFinderLlm operation, CandidateProfile profile, CandidateFacts facts,
        long profileVersion, long factsVersion, Vacancy vacancy, IReadOnlyDictionary<string, string> catalog, string conversation, string notes,
        string supportHash, string research, string market)
    {
        var input = new InterviewPreparationInput
        {
            AnswerSource = InterviewAnswerSnapshot.Capture(seed.Source.Id, operation, profile, facts, vacancy, CareerCards.InterviewLabel(seed.Source.Kind),
                new(), catalog, conversation, notes, [], seed.RenderedAt),
            ProfileVersion = profileVersion, FactsVersion = factsVersion, SupportHash = supportHash, CompanyResearch = research, MarketText = market,
        };
        input.Recipe = InputRecipe(operation, seed, input);
        return input;
    }

    internal static JobFinderLlm Restore(JobFinderLlm llm, InterviewPreparationSeed seed, InterviewPreparationInput input)
    {
        var source = input.AnswerSource;
        if (seed.Version != 1 || seed.Source is null || seed.Operation is null || seed.Recipe is null || seed.Operation.OperationId == Guid.Empty
            || seed.Source.Id <= 0 || seed.Source.ApplicationId <= 0 || seed.Source.Revision < 0 || seed.Source.Status != InterviewStatus.Scheduled
            || !Enum.IsDefined(seed.Source.Kind) || seed.RenderedAt == default || input.Version != 1 || source is null
            || input.Recipe is null || source.Version != 1 || source.Operation is null || source.Profile is null || !source.Profile.Reviewed
            || source.Facts is null || source.Vacancy is null || source.Vacancy.Id <= 0 || source.Catalog is null
            || input.ProfileVersion <= 0 || input.FactsVersion <= 0 || string.IsNullOrWhiteSpace(input.SupportHash)
            || input.CompanyResearch is null || input.MarketText is null || source.Conversation is null || source.Notes is null
            || source.InterviewId != seed.Source.Id || source.RenderedAt != seed.RenderedAt || source.Kind != CareerCards.InterviewLabel(seed.Source.Kind))
            throw new UnsupportedInterviewPreparationException();
        var operation = llm.RestoreOperation(seed.Operation);
        if (seed.Recipe.OperationId != seed.Operation.OperationId || seed.Recipe.PolicyRevision != seed.Operation.PolicyRevision
            || source.Operation.OperationId != seed.Operation.OperationId || source.Operation.PolicyRevision != seed.Operation.PolicyRevision
            || input.Recipe.OperationId != seed.Operation.OperationId || input.Recipe.PolicyRevision != seed.Operation.PolicyRevision
            || Recipe(operation, seed).Hash != seed.Recipe.Hash || InputRecipe(operation, seed, input).Hash != input.Recipe.Hash)
            throw new InterviewPreparationSupersededException();
        return operation;
    }

    internal static ArtifactRecipe PlanRecipe(JobFinderLlm operation, InterviewPreparationInput input)
    {
        var source = input.AnswerSource;
        return operation.Recipe([(LlmTaskCatalog.InterviewPlan, LlmPurpose.Interview)], new Dictionary<string, string?>
        {
            ["vacancy"] = Fingerprint.Of(source.Vacancy.Title, source.Vacancy.CompanyName, source.Vacancy.Description, source.CompanyDescription, source.Kind),
            ["profile"] = Fingerprint.Of(JobFinderLlm.ProfileText(source.Profile)), ["catalog"] = Fingerprint.Of(CopilotRules.CatalogText(source.Catalog)),
            ["conversation"] = Fingerprint.Of(source.Conversation, source.Notes), ["research"] = input.CompanyResearch, ["market"] = input.MarketText,
        }, new Dictionary<string, string> { ["copilot"] = CopilotRules.RulesVersion, ["preparation"] = "interview-plan-v1" });
    }

    public async Task<InterviewPlanCheckpoint> PlanAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("plan", "interview").Tag("interview.id", seed.Source.Id).Tag("request.id", seed.Operation.OperationId);
        var operation = Restore(llm, seed, input);
        var source = input.AnswerSource;
        var recipe = PlanRecipe(operation, input);
        decimal cost = 0;
        string? model = null;
        var (json, reused) = await answers.GetOrCreateAsync(new AnswerRequest($"interview-plan:{source.InterviewId}:{recipe.Hash}", "interview-plan", "plan",
            deps => Fingerprint.OfFacts(source.Facts, deps)), async token =>
        {
            var result = await operation.InterviewPlanAsync(source.Profile, source.Kind, source.ToVacancy(), input.CompanyResearch,
                CopilotRules.CatalogText(source.Catalog), source.Facts.ForPrompt(), source.Conversation, input.MarketText,
                string.IsNullOrWhiteSpace(source.Notes) ? "(прошлых этапов нет)" : source.Notes, seed.RenderedAt, token);
            (cost, model) = (result.CostUsd, result.Model);
            return new Generated(JsonSerializer.Serialize(result.Value, InterviewJsonContext.Default.InterviewPlan), PlanFacts, result.Model,
                LlmPurpose.Interview, operation.PromptVersion("interview_plan"), result.CostUsd, recipe);
        }, ct, ValidPlan);
        stage.Tag("reused", reused).Tag("cost_usd", (double)cost);
        return new() { Plan = JsonSerializer.Deserialize(json, InterviewJsonContext.Default.InterviewPlan)!, Model = model, CostUsd = cost, Reused = reused };
    }

    internal static bool ValidPlan(string json)
    {
        try
        {
            var plan = JsonSerializer.Deserialize(json, InterviewJsonContext.Default.InterviewPlan);
            return plan is { Role: not null, OverallRisk: "low" or "medium" or "high", Questions: not null, Priorities: not null, Gaps: not null,
                    Risks: not null, EmployerQuestions: not null, Negotiation: not null, Company: not null }
                && plan.Questions.All(question => question is { Id: not null, Question: not null, Evidence: not null, Category: not null, Priority: not null, Likelihood: not null }
                    && question.Evidence.All(value => value is not null) && (question.Story is null || question.Story is { Ref: not null, SupportedClaims: not null } story
                        && story.SupportedClaims.All(value => value is not null)))
                && plan.Priorities.All(priority => priority is { Topic: not null, Priority: not null, Reason: not null, Evidence: not null })
                && plan.Gaps.All(gap => gap is { Topic: not null, Level: not null, Known: not null, SafeWording: not null, Learn: not null, DoNotSay: not null }
                    && gap.Learn.Concat(gap.DoNotSay).All(value => value is not null))
                && plan.Risks.All(risk => risk is { Risk: not null, Severity: not null, Mitigation: not null })
                && plan.EmployerQuestions.MustAsk is not null && plan.EmployerQuestions.NiceToAsk is not null
                && plan.EmployerQuestions.MustAsk.Concat(plan.EmployerQuestions.NiceToAsk).All(item => item is { Question: not null, Why: not null })
                && plan.Negotiation is { Stance: not null, Market: not null, Conditions: not null } && plan.Negotiation.Conditions.All(value => value is not null)
                && plan.Company is { Brief: not null, WhatTheyLookFor: not null, ToVerify: not null, SystemDesignThemes: not null, CodingThemes: not null }
                && plan.Company.WhatTheyLookFor.Concat(plan.Company.ToVerify).Concat(plan.Company.SystemDesignThemes).Concat(plan.Company.CodingThemes).All(value => value is not null)
                && plan.Validate() is null;
        }
        catch (JsonException) { return false; }
    }

    public async Task<InterviewCopilot> AnswerAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewPlanCheckpoint checkpoint, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("answers", "interview").Tag("interview.id", seed.Source.Id).Tag("request.id", seed.Operation.OperationId);
        var operation = Restore(llm, seed, input);
        var planJson = JsonSerializer.Serialize(checkpoint.Plan, InterviewJsonContext.Default.InterviewPlan);
        if (!ValidPlan(planJson)) throw new UnsupportedInterviewPreparationException();
        var plan = JsonSerializer.Deserialize(planJson, InterviewJsonContext.Default.InterviewPlan)!;
        var source = input.AnswerSource;
        var (questions, known) = CopilotRules.Normalize(plan, source.Catalog);
        using var gate = new SemaphoreSlim(4);
        var answered = await Task.WhenAll(questions.Select(async question =>
        {
            var queued = System.Diagnostics.Stopwatch.GetTimestamp();
            await gate.WaitAsync(ct);
            try
            {
                var snapshot = InterviewAnswerSnapshot.Capture(source.InterviewId, operation, source.Profile, source.Facts, source.ToVacancy(), source.Kind,
                    question, source.Catalog, source.Conversation, source.Notes, known.GetValueOrDefault(question.Id) ?? [], seed.RenderedAt);
                return await _content.PrepareAsync(snapshot, System.Diagnostics.Stopwatch.GetElapsedTime(queued).TotalSeconds, ct);
            }
            finally { gate.Release(); }
        }));
        var copilot = new InterviewCopilot
        {
            Version = InterviewCopilot.CurrentVersion, Role = plan.Role, OverallRisk = plan.OverallRisk,
            Priorities = plan.Priorities.OrderBy(priority => CopilotRules.Rank(priority.Priority)).ToList(),
            Preparation = answered.Select(answer => answer.Item).Where(item => item.Q.Id != CopilotRules.SalaryId).ToList(),
            Salary = answered.Select(answer => answer.Item).FirstOrDefault(item => item.Q.Id == CopilotRules.SalaryId),
            Gaps = plan.Gaps.Select(gap => gap with { SafeWording = HumanText.Clean(gap.SafeWording) }).ToList(),
            EmployerQuestions = plan.EmployerQuestions, Negotiation = plan.Negotiation, Risks = plan.Risks.OrderBy(risk => CopilotRules.Rank(risk.Severity)).ToList(),
            Company = plan.Company, Evidence = source.Catalog.ToDictionary(pair => pair.Key, pair => pair.Value), GeneratedAt = seed.RenderedAt,
            Model = checkpoint.Model, CostUsd = checkpoint.CostUsd + answered.Sum(answer => answer.CostUsd),
        };
        copilot.LastMinute = CopilotRules.LastMinute(copilot);
        stage.Tag("questions", answered.Length).Tag("reused", answered.Count(answer => answer.Item.Reused)).Tag("cost_usd", (double)copilot.CostUsd);
        return copilot;
    }

    public async Task<InterviewPrintCheckpoint> PrintAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewCopilot copilot, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("pdf", "interview").Tag("interview.id", seed.Source.Id).Tag("request.id", seed.Operation.OperationId);
        Restore(llm, seed, input);
        var local = seed.Source.ScheduledAt is { } at ? TimeZoneInfo.ConvertTime(at, clock.Zone) : (DateTimeOffset?)null;
        var html = PackHtml.Render(copilot, input.AnswerSource.ToVacancy(), input.AnswerSource.Kind, local, assets.FontCss);
        var path = Path.GetFullPath(Path.Combine(paths.Data, "interviews", seed.Source.Id.ToString(CultureInfo.InvariantCulture), seed.Operation.OperationId.ToString("N"), "pack.pdf"));
        await browser.RenderPdfAsync(html, path, ct);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var checkpoint = new InterviewPrintCheckpoint { Path = StoredPath.ToStored(path), Hash = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        await VerifyPrintAsync(paths, seed, checkpoint, ct);
        return checkpoint;
    }

    internal static async Task VerifyPrintAsync(PathsOptions paths, InterviewPreparationSeed seed, InterviewPrintCheckpoint checkpoint, CancellationToken ct)
    {
        var expected = Path.GetFullPath(Path.Combine(paths.Data, "interviews", seed.Source.Id.ToString(CultureInfo.InvariantCulture), seed.Operation.OperationId.ToString("N"), "pack.pdf"));
        if (string.IsNullOrWhiteSpace(checkpoint.Path) || string.IsNullOrWhiteSpace(checkpoint.Hash)
            || !string.Equals(StoredPath.Resolve(checkpoint.Path), expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnsupportedInterviewPreparationException();
        var file = new FileInfo(expected);
        if (!file.Exists || file.Length is <= 0 or > 64 * 1024 * 1024) throw new UnsupportedInterviewPreparationException();
        var bytes = await File.ReadAllBytesAsync(expected, ct);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != checkpoint.Hash) throw new UnsupportedInterviewPreparationException();
        using var document = PdfDocument.Open(bytes);
        if (document.NumberOfPages == 0 || !document.GetPages().Any(page => !string.IsNullOrWhiteSpace(page.Text))) throw new UnsupportedInterviewPreparationException();
    }

    internal static void ValidateResult(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewPlanCheckpoint checkpoint, InterviewCopilot result)
    {
        var planJson = JsonSerializer.Serialize(checkpoint.Plan, InterviewJsonContext.Default.InterviewPlan);
        if (!ValidPlan(planJson) || result.Version != InterviewCopilot.CurrentVersion || result.GeneratedAt != seed.RenderedAt
            || result.Preparation is null || result.Salary is null || result.Evidence is null || result.Priorities is null || result.Gaps is null
            || result.Risks is null || result.EmployerQuestions is null || result.Negotiation is null || result.Company is null || result.LastMinute is null)
            throw new UnsupportedInterviewPreparationException();
        var (questions, _) = CopilotRules.Normalize(JsonSerializer.Deserialize(planJson, InterviewJsonContext.Default.InterviewPlan)!, input.AnswerSource.Catalog);
        var items = result.Preparation.Append(result.Salary).ToList();
        if (items.Count != questions.Count || items.Any(item => item?.Q is null)
            || items.Select(item => item.Q.Id).Distinct(StringComparer.Ordinal).Count() != questions.Count) throw new UnsupportedInterviewPreparationException();
        foreach (var question in questions)
        {
            var item = items.SingleOrDefault(candidate => candidate.Q.Id == question.Id);
            if (item is null || !InterviewAnswerContent.ValidCached(JsonSerializer.Serialize(item, InterviewJsonContext.Default.PrepItem), question.Id)
                || CanonicalJson.Hash(JsonSerializer.Serialize(item.Q, InterviewJsonContext.Default.PlannedQuestion))
                    != CanonicalJson.Hash(JsonSerializer.Serialize(question, InterviewJsonContext.Default.PlannedQuestion)))
                throw new UnsupportedInterviewPreparationException();
        }
    }
}
