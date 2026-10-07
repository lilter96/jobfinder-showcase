using System.Globalization;
using System.Text.Json;
using System.Data.Common;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public interface IHrPlaybookPreparationSteps
{
    void EnsureCompatible(HrPlaybookInput input);
    Task<HrPlaybook> BuildAsync(HrPlaybookInput input, CancellationToken ct);
}

public sealed class HrPlaybookPreparationSteps(JobFinderLlm llm, GeneratedAnswers answers, TimeProvider time, ILogger<HrPlaybookPreparationSteps> log)
    : IHrPlaybookPreparationSteps
{
    private const int ParallelAnswers = 6;
    private static readonly string[] MoneyQuestions = ["current_salary", "salary_expectations"];
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, HrPlaybookInput input) => operation.Recipe(
        input.Bank.Questions.Select(question => ((JobFinder.Llm.Contracts.ILlmTaskDefinition)LlmTaskCatalog.HrAnswer,
            CopilotRules.PurposeFor(HrPlaybookService.ToPlanned(question), question.Effort))).Distinct(),
        new Dictionary<string, string?>
        {
            ["profile"] = input.ProfileJson, ["facts"] = input.FactsJson, ["catalog"] = input.Evidence, ["funnel"] = input.Funnel,
            ["bank"] = JsonSerializer.Serialize(input.Bank, InterviewJsonContext.Default.HrBank),
            ["rendered-day"] = input.CapturedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["previous"] = input.PreviousJson,
        }, new Dictionary<string, string> { ["hr"] = "native-v1", ["copilot"] = CopilotRules.RulesVersion });

    public void EnsureCompatible(HrPlaybookInput input)
    {
        if (Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash) throw new HrPlaybookSupersededException();
    }

    public async Task<HrPlaybook> BuildAsync(HrPlaybookInput input, CancellationToken ct)
    {
        EnsureCompatible(input);
        var operation = llm.RestoreOperation(input.Operation);
        var profile = ProfileProvider.FromJson(input.ProfileJson);
        var current = JsonSerializer.Deserialize(input.FactsJson, FactsJsonContext.Default.CandidateFacts) ?? throw new UnsupportedHrPlaybookException();
        var factsText = current.ForPrompt();
        var evidence = input.Evidence;
        var funnel = input.Funnel;
        var sources = evidence + "\n" + JobFinderLlm.ProfileText(profile) + "\n" + factsText;
        var versions = new Versions(operation.PromptVersion("hr_answer"), Fingerprint.Of(JobFinderLlm.ProfileText(profile)), Fingerprint.Of(evidence), input.CapturedAt);
        var shown = input.PreviousJson is null ? new HashSet<string>(StringComparer.Ordinal) :
            (JsonSerializer.Deserialize(input.PreviousJson, InterviewJsonContext.Default.HrPlaybook)?.Items
                .Select(item => item.Item.Fingerprint).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? []);
        using var build = Core.Telemetry.Telemetry.StartStage("build", "hr").Tag("questions", input.Bank.Questions.Count).Tag("parallel", ParallelAnswers);
        using var gate = new SemaphoreSlim(ParallelAnswers);
        var built = await Task.WhenAll(input.Bank.Questions.Select(async question =>
        {
            var queued = System.Diagnostics.Stopwatch.GetTimestamp();
            await gate.WaitAsync(ct);
            try
            {
                return await ItemAsync(operation, profile, question, evidence, factsText, funnel, sources, current, versions, shown,
                    System.Diagnostics.Stopwatch.GetElapsedTime(queued).TotalSeconds, ct);
            }
            finally { gate.Release(); }
        }));
        return new HrPlaybook { Version = HrPlaybook.CurrentVersion, Items = built.Select(item => item.Item).ToList(),
            GeneratedAt = time.GetUtcNow(), Model = built.Select(item => item.Model).FirstOrDefault(model => model is not null), CostUsd = built.Sum(item => item.Cost) };
    }

    /// <summary>The versions of what every answer is generated from: the prompt, the profile, the evidence catalog.</summary>
    private sealed record Versions(string Prompt, string Profile, string Catalog, DateTimeOffset RenderedAt);

    /// <summary>
    /// One question. A declared fact missing → the candidate's position is asked for, the model is not called. Otherwise the answer comes
    /// from the store when nothing it depends on changed (its fact keys, this question's own position fact), else it is generated on
    /// the reasoning tier the question needs.
    /// </summary>
    private async Task<(HrPlaybookItem Item, decimal Cost, string? Model)> ItemAsync(JobFinderLlm operation, Core.Profile.CandidateProfile profile, HrQuestion q,
        string evidence, string factsText, string funnel, string sources, Core.Profile.CandidateFacts current, Versions versions,
        IReadOnlySet<string> shown, double queueWait, CancellationToken ct)
    {
        var planned = HrPlaybookService.ToPlanned(q);
        var purpose = CopilotRules.PurposeFor(planned, q.Effort);
        using var stage = Core.Telemetry.Telemetry.StartStage("answer", "hr").Tag("question.id", q.Id).Tag("question.category", q.Category)
            .Tag("purpose", purpose).Tag("queue_wait_s", queueWait);

        if (q.Facts.FirstOrDefault(k => k != "funnel" && !current.IsConfirmed(k)) is { } missing)
        {
            stage.Tag("position", true);
            var item = CopilotRules.PositionItem(planned, q.Options, "нужна ваша позиция: " + (current.Get(missing)?.Question ?? missing).ToLowerInvariant());
            item.Fingerprint = Fingerprint.Of(q.Id, "position", missing, string.Join("\n", q.Options));
            item.Reused = shown.Contains(item.Fingerprint);
            return (new HrPlaybookItem { Bank = q, Item = item }, 0, null);
        }

        var declared = q.Facts.Append(HrPlaybookService.PositionFactKey(q.Id)).ToList();
        var recipe = operation.Recipe([(LlmTaskCatalog.HrAnswer, purpose)], new Dictionary<string, string?>
        {
            ["question"] = JsonSerializer.Serialize(q, InterviewJsonContext.Default.HrQuestion), ["profile"] = versions.Profile, ["catalog"] = versions.Catalog, ["rendered-day"] = versions.RenderedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, new Dictionary<string, string> { ["copilot"] = CopilotRules.RulesVersion, ["hr"] = "native-v1" });
        var key = $"hr:{q.Id}:" + recipe.Hash;
        var request = new AnswerRequest(key, "hr", q.Id, deps => Fingerprint.OfFacts(current, deps, funnel));
        decimal cost = 0;
        string? model = null;
        var (json, reused) = await GetOrCheckpointAsync(q, request, async c =>
        {
            var (item, genCost, genModel) = await GenerateAsync(operation, profile, q, planned, purpose, evidence, factsText, funnel, sources, stage, versions.RenderedAt, c);
            (cost, model) = (genCost, genModel);
            item.Deps = declared.Concat(item.A?.UsedFacts ?? []).Distinct(StringComparer.Ordinal).ToList();
            item.Purpose = purpose;
            item.GeneratedAt = time.GetUtcNow();
            return new Generated(JsonSerializer.Serialize(item, InterviewJsonContext.Default.PrepItem), item.Deps, genModel, purpose, versions.Prompt, genCost, recipe);
        }, ct);

        var result = JsonSerializer.Deserialize(json, InterviewJsonContext.Default.PrepItem)!;
        result.Q = planned;
        result.Reused = reused;
        result.Fingerprint = Fingerprint.Of(json);
        stage.Tag("reused", reused).Tag("requires_approval", result.RequiresApproval).Tag("cost_usd", (double)cost);
        return (new HrPlaybookItem { Bank = q, Item = result }, cost, model);
    }

    private async Task<(string Value, bool Reused)> GetOrCheckpointAsync(HrQuestion question, AnswerRequest request,
        Func<CancellationToken, Task<Generated>> generate, CancellationToken ct)
    {
        Generated? paid = null;
        try
        {
            return await answers.GetOrCreateAsync(request, async token => { paid = await generate(token); return paid; }, ct);
        }
        catch (Exception error) when (paid is not null && error is IOException or DbException or DbUpdateException)
        {
            var item = JsonSerializer.Deserialize(paid.Value, InterviewJsonContext.Default.PrepItem) ?? throw new UnsupportedHrPlaybookException();
            var partial = new HrPlaybook { Version = HrPlaybook.CurrentVersion, GeneratedAt = time.GetUtcNow(), Model = paid.Model, CostUsd = paid.CostUsd,
                Items = [new HrPlaybookItem { Bank = question, Item = item }] };
            throw new HrPlaybookCheckpointException(error, JsonSerializer.Serialize(partial, InterviewJsonContext.Default.HrPlaybook));
        }
    }

    private async Task<(PrepItem Item, decimal Cost, string? Model)> GenerateAsync(JobFinderLlm operation, Core.Profile.CandidateProfile profile, HrQuestion q,
        PlannedQuestion planned, string purpose, string evidence, string factsText, string funnel, string sources, Core.Telemetry.Telemetry.Stage stage, DateTimeOffset renderedAt,
        CancellationToken ct)
    {
        SpokenAnswer? best = null;
        List<string> bestProblems = [];
        string? remarks = null;
        string? model = null;
        decimal cost = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            stage.Tag("attempts", attempt + 1);
            try
            {
                var result = await operation.HrAnswerAsync(profile, q, evidence, factsText, funnel, remarks, renderedAt, purpose, ct);
                cost += result.CostUsd;
                model ??= result.Model;
                var answer = CopilotRules.Clean(result.Value);
                var problems = CopilotRules.SpeechProblems(answer, planned);
                if (best is null || problems.Count < bestProblems.Count) (best, bestProblems) = (answer, problems);
                if (problems.Count == 0) break;
                log.Info($"[hr] «{q.Question}»: speech rejected ({string.Join("; ", problems.Take(4))}), rewriting");
                remarks = "Прошлый вариант не прошёл проверку:\n- " + string.Join("\n- ", problems);
            }
            catch (LlmUnavailableException e)
            {
                log.Warn($"[hr] «{q.Question}»: {e.Message}");
                if (best is null) throw;
                break;
            }
        }

        var known = new List<string>();
        if (q.Personal) known.Add("личный вопрос: как отвечать, решаете вы");
        if (MoneyQuestions.Contains(q.Id)) known.Add("деньги: цифру и позицию решаете вы");
        stage.Tag("speech_problems", bestProblems.Count);
        return (CopilotRules.Finish(planned, best, bestProblems, known, sources), cost, model);
    }

}
