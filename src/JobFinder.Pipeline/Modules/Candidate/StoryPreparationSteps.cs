using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;

namespace JobFinder.Pipeline.Modules.Candidate;

public interface IStoryPreparationSteps
{
    void EnsureCompatible(StoryPreparationInput input);
    Task<StoryPreparationResult> DraftAsync(StoryPreparationInput input, CancellationToken ct);
}

public sealed class StoryPreparationSteps(JobFinderLlm llm) : IStoryPreparationSteps
{
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, StoryPreparationInput input) => operation.Recipe(
        [(LlmTaskCatalog.ComposeStory, LlmPurpose.Coach)], new Dictionary<string, string?>
        {
            ["candidate"] = JsonSerializer.Serialize(input.Candidate, StoryPreparationJsonContext.Default.StoryCandidateSnapshot),
            ["before"] = ConversationStore.Capture(input.Before), ["waiting"] = ConversationStore.Capture(input.Waiting),
            ["questions"] = JsonSerializer.Serialize(input.Questions), ["answer"] = input.Answer,
        }, new Dictionary<string, string> { ["story"] = "native-v1", ["presentation"] = StoryPresentation.Version });

    public void EnsureCompatible(StoryPreparationInput input)
    {
        if (Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash) throw new StoryPreparationSupersededException();
    }

    public async Task<StoryPreparationResult> DraftAsync(StoryPreparationInput input, CancellationToken ct)
    {
        EnsureCompatible(input);
        return new() { Draft = (await llm.RestoreOperation(input.Operation).ComposeStoryAsync(input.Waiting.Subject!, input.AnswersText(), input.Candidate.ResumeText, ct)).Value };
    }
}
