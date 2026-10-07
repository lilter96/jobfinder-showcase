using JobFinder.Core.Artifacts;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;

namespace JobFinder.Pipeline.Modules.Hiring;

public interface IReplyProcessingSteps
{
    void EnsureCompatible(ReplyProcessingInput input);
    Task<ReplyProcessingResult> AnalyzeAsync(ReplyProcessingInput input, ReplyProcessingResult? best, bool deep, CancellationToken ct);
}

public sealed class ReplyProcessingSteps(JobFinderLlm llm) : IReplyProcessingSteps
{
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, ReplyProcessingInput input) => operation.Recipe(
        [(LlmTaskCatalog.AnalyzeReply, LlmPurpose.Reply), (LlmTaskCatalog.AnalyzeReply, LlmPurpose.ReplyDeep)],
        new Dictionary<string, string?>
        {
            ["profile"] = ProfileProvider.ToJson(input.Profile), ["facts"] = input.Facts.ForPrompt(), ["stories"] = input.Stories,
            ["vacancy"] = JobFinderLlm.VacancyText(input.Vacancy.ToVacancy()), ["conversation"] = input.Conversation,
            ["message"] = input.Message, ["date"] = input.CapturedAt.ToString("O"), ["zone"] = input.TimeZone,
        }, new Dictionary<string, string> { ["reply-rules"] = "v1" });

    public void EnsureCompatible(ReplyProcessingInput input)
    {
        if (Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash)
            throw new ReplyProcessingSupersededException();
    }

    public async Task<ReplyProcessingResult> AnalyzeAsync(ReplyProcessingInput input, ReplyProcessingResult? best, bool deep, CancellationToken ct)
    {
        EnsureCompatible(input);
        var remarks = best is { Problems.Count: > 0 }
            ? $"- {string.Join("\n- ", best.Problems)}\nПрошлый draft: «{best.Analysis.Draft}»" : null;
        var analysis = (await llm.RestoreOperation(input.Operation).AnalyzeReplyAsync(input.Profile, input.Facts,
            input.Vacancy.ToVacancy(), input.Conversation, input.Message, input.Stories, input.CapturedAt, input.TimeZone, ct, remarks, deep)).Value;
        return new ReplyProcessingResult
        {
            Analysis = analysis,
            Problems = string.IsNullOrWhiteSpace(analysis.Draft) ? [] : ChatStyle.Problems(HumanText.Clean(analysis.Draft.Trim()), DialoguePolicy.IsConsidered(analysis)),
        };
    }
}
