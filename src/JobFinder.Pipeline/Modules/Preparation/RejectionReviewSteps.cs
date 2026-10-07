using System.Globalization;
using JobFinder.Core.Artifacts;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Profile;

namespace JobFinder.Pipeline.Modules.Preparation;

public interface IRejectionReviewSteps
{
    void EnsureCompatible(RejectionReviewInputSnapshot input);
    Task<RejectionReviewResult> AnalyzeAsync(RejectionReviewInputSnapshot input, CancellationToken ct);
}

public sealed class RejectionReviewSteps(JobFinderLlm llm, TimeProvider time) : IRejectionReviewSteps
{
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, RejectionReviewInputSnapshot input) => operation.Recipe(
        [(LlmTaskCatalog.RejectionReview, LlmPurpose.Resume)],
        new Dictionary<string, string?>
        {
            ["profile"] = input.ProfileJson, ["resume"] = input.ResumeText, ["source"] = input.SourceJson,
            ["facts"] = input.FactsText, ["rendered-day"] = input.CapturedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, new Dictionary<string, string> { ["rejections"] = "native-v1" });

    public void EnsureCompatible(RejectionReviewInputSnapshot input)
    {
        if (Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash) throw new RejectionReviewSupersededException();
    }

    public async Task<RejectionReviewResult> AnalyzeAsync(RejectionReviewInputSnapshot input, CancellationToken ct)
    {
        EnsureCompatible(input);
        var result = await llm.RestoreOperation(input.Operation).RejectionReviewAsync(ProfileProvider.FromJson(input.ProfileJson),
            input.ResumeText, input.FactsText, input.CapturedAt, ct);
        return new() { Review = result.Value, Model = result.Model, CostUsd = result.CostUsd, GeneratedAt = time.GetUtcNow() };
    }
}
