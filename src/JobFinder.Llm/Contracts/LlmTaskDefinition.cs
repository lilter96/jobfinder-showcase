using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using JobFinder.Core.Text;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;

namespace JobFinder.Llm.Contracts;

public sealed record LlmTaskPayload(string Purpose, IReadOnlyDictionary<string, string> Bindings, string UserMessage, bool Essential = false);

public sealed record LlmTaskProbe(string TaskId, LlmTaskPayload Payload, JsonSchemaSpec? Schema, Func<string, string?> ValidateResponse);

public interface ILlmTaskDefinition
{
    string Id { get; }
    string Prompt { get; }
    string RulesVersion { get; }
    IReadOnlyList<string> Purposes { get; }
    JsonSchemaSpec? Schema { get; }
    string ContractVersion { get; }
    LlmTaskProbe Probe(string purpose);
}

public sealed class LlmTaskDefinition<TInput, TWireResult> : ILlmTaskDefinition where TWireResult : class, IValidatable
{
    public string Id { get; }

    public string Prompt { get; }

    public string Purpose { get; }

    public string RulesVersion { get; }

    public JsonTypeInfo<TWireResult> ResultType { get; }

    public JsonSchemaSpec Schema { get; }

    public IReadOnlyList<string> Purposes { get; }

    public bool Essential { get; }

    private readonly Func<TInput, LlmTaskPayload> _payload;
    private readonly Func<string, TInput>? _probeInput;

    public LlmTaskDefinition(string id, string prompt, string purpose, string rulesVersion, JsonTypeInfo<TWireResult> resultType,
        Func<TInput, IReadOnlyDictionary<string, string>> bindings, Func<TInput, string> userMessage)
        : this(id, prompt, rulesVersion, resultType, [purpose], input => new LlmTaskPayload(purpose, bindings(input), userMessage(input)))
    {
    }

    public LlmTaskDefinition(string id, string prompt, string rulesVersion, JsonTypeInfo<TWireResult> resultType,
        IReadOnlyList<string> purposes, Func<TInput, LlmTaskPayload> payload, bool essential = false, JsonSchemaSpec? schema = null,
        Func<string, TInput>? probeInput = null)
    {
        if (purposes.Count == 0 || purposes.Any(purpose => !LlmPurpose.All.Contains(purpose, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("unknown or empty LLM purposes", nameof(purposes));
        Id = id;
        Prompt = prompt;
        Purpose = purposes[0];
        Purposes = Array.AsReadOnly(purposes.ToArray());
        RulesVersion = rulesVersion;
        ResultType = resultType;
        _payload = payload;
        Essential = essential;
        _probeInput = probeInput;
        Schema = schema ?? WireSchema.Create(id, resultType);
    }

    public string ContractVersion => CanonicalJson.Hash(Schema.Schema);

    public LlmTaskPayload Build(TInput input)
    {
        var payload = _payload(input);
        if (!Purposes.Contains(payload.Purpose, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"task {Id} cannot use purpose '{payload.Purpose}'", nameof(input));
        return payload;
    }

    public async Task<LlmResult<TWireResult>> ExecuteAsync(LlmClient client, PromptCatalog prompts, TInput input, CancellationToken ct,
        bool essential = false)
    {
        var payload = Build(input);
        var operation = await client.ForOperationAsync(ct);
        return await operation.CompleteJsonAsync(payload.Purpose, prompts.Render(Prompt, payload.Bindings), payload.UserMessage, Schema, ResultType, ct,
            Essential || essential || payload.Essential, GenerationRecipe.Call(this, payload, prompts, operation));
    }

    public LlmTaskProbe Probe(string purpose)
    {
        var input = _probeInput ?? throw new InvalidOperationException($"task {Id} has no probe input");
        var request = Build(input(purpose));
        if (!request.Purpose.Equals(purpose, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"task {Id} cannot probe purpose '{purpose}'", nameof(purpose));
        return new LlmTaskProbe(Id, request, Schema, ValidateResponse);
    }

    private string? ValidateResponse(string response)
    {
        var json = LlmClient.ExtractJson(response);
        StructuredOutputValidator.Validate(json, Schema);
        var value = JsonSerializer.Deserialize(json, ResultType);
        return value is null ? "null response" : value.Validate();
    }
}
