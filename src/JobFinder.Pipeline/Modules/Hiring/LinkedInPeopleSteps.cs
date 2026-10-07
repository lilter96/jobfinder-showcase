using JobFinder.Core.Artifacts;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;

namespace JobFinder.Pipeline.Modules.Hiring;

public interface ILinkedInPeopleSteps
{
    void EnsureCompatible(LinkedInPeopleInput input);
    Task<LinkedInPeopleResult> ReadAsync(LinkedInPeopleInput input, CancellationToken ct);
}

public sealed class LinkedInPeopleSteps(JobFinderLlm llm) : ILinkedInPeopleSteps
{
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, LinkedInPeopleInput input) => operation.Recipe(
        [(LlmTaskCatalog.ReadLinkedInPeople, LlmPurpose.Extract)],
        new Dictionary<string, string?> { ["company"] = input.Company, ["blocks"] = LinkedInPeopleReader.Blocks(input.Page().Cards) },
        new Dictionary<string, string> { ["linkedin-people-rules"] = "v1" });

    public void EnsureCompatible(LinkedInPeopleInput input)
    {
        if (Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash) throw new UnsupportedLinkedInPeopleException();
    }

    public async Task<LinkedInPeopleResult> ReadAsync(LinkedInPeopleInput input, CancellationToken ct)
    {
        EnsureCompatible(input);
        if (input.Cards.Count == 0) return new();
        var result = await llm.RestoreOperation(input.Operation).ReadLinkedInPeopleAsync(input.Company, LinkedInPeopleReader.Blocks(input.Page().Cards), ct);
        return new() { Reading = result.Value };
    }
}
