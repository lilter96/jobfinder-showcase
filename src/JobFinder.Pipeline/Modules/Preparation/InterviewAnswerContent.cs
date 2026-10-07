using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Telemetry;
using JobFinder.Llm.Client;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed record InterviewAnswerSnapshot
{
    public int Version { get; set; } = 1;
    public long InterviewId { get; set; }
    public CandidateProfile Profile { get; set; } = new();
    public CandidateFacts Facts { get; set; } = new();
    public ResumeVacancyInput Vacancy { get; set; } = new();
    public string? CompanyDescription { get; set; }
    public string Kind { get; set; } = "";
    public PlannedQuestion Question { get; set; } = new();
    public Dictionary<string, string> Catalog { get; set; } = [];
    public string Conversation { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Known { get; set; } = [];
    public DateTimeOffset RenderedAt { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();

    public Vacancy ToVacancy()
    {
        var vacancy = Vacancy.ToVacancy();
        if (vacancy.Company is { } company) company.Description = CompanyDescription;
        else if (CompanyDescription is not null) vacancy.Company = new Company { Name = Vacancy.CompanyName ?? "", Description = CompanyDescription };
        return vacancy;
    }

    internal static InterviewAnswerSnapshot Capture(long interviewId, JobFinderLlm operation, CandidateProfile profile, CandidateFacts facts,
        Vacancy vacancy, string kind, PlannedQuestion question, IReadOnlyDictionary<string, string> catalog, string conversation,
        string notes, IReadOnlyList<string> known, DateTimeOffset renderedAt)
    {
        var input = new InterviewAnswerSnapshot
        {
            InterviewId = interviewId, Profile = profile, Facts = facts, Vacancy = ResumeVacancyInput.From(vacancy), CompanyDescription = vacancy.Company?.Description,
            Kind = kind, Question = question, Catalog = catalog.ToDictionary(pair => pair.Key, pair => pair.Value), Conversation = conversation, Notes = notes,
            Known = [.. known], RenderedAt = renderedAt, Operation = operation.CaptureOperation(),
        };
        input.Recipe = InterviewAnswerContent.Recipe(operation, input);
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(input, InterviewAnswerJsonContext.Default.InterviewAnswerSnapshot), InterviewAnswerJsonContext.Default.InterviewAnswerSnapshot)!;
    }
}

public sealed record InterviewAnswerOutput
{
    public PrepItem Item { get; set; } = new();
    public decimal CostUsd { get; set; }
}

internal sealed class InterviewAnswerContent(JobFinderLlm llm, GeneratedAnswers answers, ILogger log)
{
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, InterviewAnswerSnapshot input) => operation.Recipe(
        [(LlmTaskCatalog.InterviewAnswer, CopilotRules.PurposeFor(input.Question))], new Dictionary<string, string?>
        {
            ["question"] = JsonSerializer.Serialize(input.Question, InterviewJsonContext.Default.PlannedQuestion),
            ["evidence"] = CopilotRules.EvidenceFor(input.Question, input.Catalog),
            ["vacancy"] = Fingerprint.Of(input.Vacancy.Title, input.Vacancy.CompanyName, input.Vacancy.Description, input.CompanyDescription, input.Kind),
            ["profile"] = Fingerprint.Of(JobFinderLlm.ProfileText(input.Profile)),
            ["conversation"] = Fingerprint.Of(input.Conversation, input.Notes),
        }, new Dictionary<string, string> { ["copilot"] = CopilotRules.RulesVersion });

    public async Task<InterviewAnswerOutput> PrepareAsync(InterviewAnswerSnapshot input, double queueWait, CancellationToken ct)
    {
        if (input.Version != 1 || input.InterviewId <= 0 || input.Operation is null || input.Recipe is null || input.Operation.OperationId == Guid.Empty || input.Recipe.OperationId != input.Operation.OperationId
            || input.Profile is null || input.Facts is null || input.Vacancy is null || input.Question is null || input.Catalog is null || input.Known is null)
            throw new ArgumentException("invalid captured interview answer inputs");
        var operation = llm.RestoreOperation(input.Operation);
        if (Recipe(operation, input).Hash != input.Recipe.Hash) throw new ArgumentException("interview answer recipe changed");
        var question = input.Question;
        var purpose = CopilotRules.PurposeFor(question);
        var factsText = input.Facts.ForPrompt();
        var sources = CopilotRules.CatalogText(input.Catalog) + "\n" + JobFinderLlm.ProfileText(input.Profile) + "\n" + factsText;
        var key = $"interview:{input.InterviewId}:{question.Id}:" + input.Recipe.Hash;
        using var stage = Telemetry.StartStage("answer", "interview").Tag("question.id", question.Id).Tag("question.category", question.Category)
            .Tag("purpose", purpose).Tag("queue_wait_s", queueWait);
        decimal cost = 0;
        var (json, reused) = await answers.GetOrCreateAsync(new AnswerRequest(key, "interview", question.Id, deps => Fingerprint.OfFacts(input.Facts, deps)), async token =>
        {
            SpokenAnswer? best = null;
            List<string> bestProblems = [];
            string? remarks = null;
            string? model = null;
            for (var round = 1; round <= 2; round++)
            {
                stage.Tag("attempts", round);
                try
                {
                    var result = await operation.InterviewAnswerAsync(input.Profile, input.Kind, input.ToVacancy(), question, CopilotRules.EvidenceFor(question, input.Catalog),
                        factsText, input.Conversation, remarks, input.RenderedAt, purpose, token);
                    cost += result.CostUsd;
                    model ??= result.Model;
                    var answer = CopilotRules.Clean(result.Value);
                    var problems = CopilotRules.SpeechProblems(answer, question);
                    if (best is null || problems.Count < bestProblems.Count) (best, bestProblems) = (answer, problems);
                    if (problems.Count == 0) break;
                    log.Info($"[interview] «{question.Question}»: speech rejected ({string.Join("; ", problems.Take(4))}), rewriting");
                    remarks = "Прошлый вариант не прошёл проверку:\n- " + string.Join("\n- ", problems);
                }
                catch (LlmUnavailableException error)
                {
                    log.Warn($"[interview] «{question.Question}»: {error.Message}");
                    throw;
                }
            }
            var item = CopilotRules.Finish(question, best, bestProblems, input.Known, sources);
            item.Deps = (best?.UsedFacts ?? []).Append(HrPlaybookService.PositionFactKey(question.Id)).Distinct(StringComparer.Ordinal).ToList();
            item.Purpose = purpose;
            item.GeneratedAt = input.RenderedAt;
            stage.Tag("speech_problems", bestProblems.Count);
            return new Generated(JsonSerializer.Serialize(item, InterviewJsonContext.Default.PrepItem), item.Deps, model, purpose,
                operation.PromptVersion("interview_answer"), cost, input.Recipe);
        }, ct, value => ValidCached(value, question.Id));
        var stored = JsonSerializer.Deserialize(json, InterviewJsonContext.Default.PrepItem)!;
        var speech = CopilotRules.Clean(stored.A!);
        var checkedItem = CopilotRules.Finish(question, speech, CopilotRules.SpeechProblems(speech, question), input.Known, sources);
        checkedItem.Deps = [.. stored.Deps];
        checkedItem.Purpose = purpose;
        checkedItem.GeneratedAt = stored.GeneratedAt;
        checkedItem.Reused = reused;
        checkedItem.Fingerprint = Fingerprint.Of(json);
        stage.Tag("reused", reused).Tag("requires_approval", checkedItem.RequiresApproval).Tag("cost_usd", (double)cost);
        return new() { Item = checkedItem, CostUsd = cost };
    }

    internal static bool ValidCached(string json, string questionId)
    {
        try
        {
            var item = JsonSerializer.Deserialize(json, InterviewJsonContext.Default.PrepItem);
            return item?.Q?.Id == questionId && item.Deps is not null && item.StyleProblems is not null && item.ApprovalReasons is not null
                && item.CheckLevel is "none" or "review" or "blocker" && item.A is { } answer
                && answer.EvidenceLevel is "verified" or "derived" or "personal_unknown" or "unsupported"
                && answer.PositionOptions is not null && answer.DoNotSay is not null && answer.FollowUps is not null && answer.Verify is not null && answer.UsedFacts is not null
                && answer.Start is not null && answer.Core is not null && answer.DeepDive is not null
                && answer.FollowUps.All(followUp => followUp is not null && followUp.Start is not null && followUp.Core is not null)
                && answer.DoNotSay.All(phrase => phrase is not null && phrase.Say is not null && phrase.Instead is not null)
                && answer.PositionOptions.Concat(answer.Verify).Concat(answer.UsedFacts).All(value => value is not null)
                && answer.Validate() is null;
        }
        catch (JsonException) { return false; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InterviewAnswerSnapshot))]
[JsonSerializable(typeof(InterviewAnswerOutput))]
public sealed partial class InterviewAnswerJsonContext : JsonSerializerContext;
