using JobFinder.Core.Text;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;

namespace JobFinder.Llm.Contracts;

public sealed class LlmTextTaskDefinition<TInput>(string id, string prompt, string rulesVersion, IReadOnlyList<string> purposes,
    Func<TInput, LlmTaskPayload> payload, Func<string, TInput>? probeInput = null) : ILlmTaskDefinition
{
    public string Id { get; } = id;
    public string Prompt { get; } = prompt;
    public string RulesVersion { get; } = rulesVersion;
    public IReadOnlyList<string> Purposes { get; } = ValidatePurposes(purposes);
    public JsonSchemaSpec? Schema => null;
    public string ContractVersion => CanonicalJson.Hash("\"plain-text-v1\"");

    public LlmTaskPayload Build(TInput input)
    {
        var result = payload(input);
        if (!Purposes.Contains(result.Purpose, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"task {Id} cannot use purpose '{result.Purpose}'", nameof(input));
        return result;
    }

    public async Task<LlmResult<string>> ExecuteAsync(LlmClient client, PromptCatalog prompts, TInput input, CancellationToken ct)
    {
        var request = Build(input);
        var operation = await client.ForOperationAsync(ct);
        return await operation.CompleteTextAsync(request.Purpose, prompts.Render(Prompt, request.Bindings), request.UserMessage, ct,
            request.Essential, GenerationRecipe.Call(this, request, prompts, operation));
    }

    public LlmTaskProbe Probe(string purpose)
    {
        var input = probeInput ?? throw new InvalidOperationException($"task {Id} has no probe input");
        var request = Build(input(purpose));
        if (!request.Purpose.Equals(purpose, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"task {Id} cannot probe purpose '{purpose}'", nameof(purpose));
        return new LlmTaskProbe(Id, request, null, text => string.IsNullOrWhiteSpace(text) ? "empty response" : null);
    }

    private static IReadOnlyList<string> ValidatePurposes(IReadOnlyList<string> values)
    {
        if (values.Count == 0 || values.Any(value => !LlmPurpose.All.Contains(value, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("unknown or empty LLM purposes", nameof(values));
        return Array.AsReadOnly(values.ToArray());
    }
}
