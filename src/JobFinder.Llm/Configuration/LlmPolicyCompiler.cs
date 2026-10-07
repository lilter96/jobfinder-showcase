using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JobFinder.Core.Text;

namespace JobFinder.Llm.Configuration;

public sealed class LlmPolicyException(string message) : ArgumentException(message);

public static class LlmPolicyCompiler
{
    private static readonly string[] Efforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static IReadOnlyList<LlmModelOptions> Route(LlmOptions options, string purpose)
    {
        if (!LlmPurpose.All.Contains(purpose, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"unknown LLM purpose '{purpose}'", nameof(purpose));
        var route = options.Routes.FirstOrDefault(pair => pair.Key.Equals(purpose, StringComparison.OrdinalIgnoreCase)).Value;
        if (route is null || route.Count == 0) return [];
        return route.Select(model =>
        {
            var compiled = Model(model, options.TimeoutSec);
            if (purpose.Equals(LlmPurpose.Application, StringComparison.OrdinalIgnoreCase)
                || purpose.Equals(LlmPurpose.Resume, StringComparison.OrdinalIgnoreCase))
            {
                if (compiled.Reasoning?.Effort is not ("xhigh" or "max") || compiled.Reasoning.Enabled == false)
                    throw new LlmPolicyException($"{purpose}: every model, including '{model.Name}', must use maximum reasoning");
            }
            return compiled;
        }).ToArray();
    }

    public static LlmModelOptions Model(LlmModelOptions model, int defaultTimeout)
    {
        if (string.IsNullOrWhiteSpace(model.Name) || model.MaxTokens <= 0 || (model.TimeoutSec ?? defaultTimeout) <= 0
            || model.Structured is not ("json_schema" or "json_object" or "prompt")
            || model.Temperature is { } temperature && (!double.IsFinite(temperature) || temperature is < 0 or > 2))
            throw new LlmPolicyException("invalid model name, output mode, token limit, timeout or temperature");
        var reasoning = model.Reasoning is null ? null : model.Reasoning with { };
        var provider = model.Provider is null ? null : Clone(model.Provider);
        if (!string.IsNullOrWhiteSpace(model.ExtraBody))
        {
            JsonObject legacy;
            try
            {
                legacy = JsonNode.Parse(model.ExtraBody) as JsonObject ?? throw new LlmPolicyException("ExtraBody must be a JSON object");
            }
            catch (JsonException)
            {
                throw new LlmPolicyException("ExtraBody is not valid JSON");
            }
            if (legacy.Any(property => property.Key is not ("reasoning" or "provider")))
                throw new LlmPolicyException("ExtraBody contains a protected or unsupported field");
            foreach (var property in legacy)
                if (property.Value is not JsonObject) throw new LlmPolicyException($"ExtraBody.{property.Key} must be an object");
            try
            {
                if (legacy["reasoning"] is { } legacyReasoning)
                {
                    if (reasoning is not null) throw new LlmPolicyException("reasoning has both typed and legacy owners");
                    reasoning = legacyReasoning.Deserialize(LlmPolicyJsonContext.Default.ReasoningSettings);
                }
                if (legacy["provider"] is { } legacyProvider)
                {
                    if (provider is not null) throw new LlmPolicyException("provider has both typed and legacy owners");
                    if (legacyProvider.AsObject().TryGetPropertyValue("require_parameters", out var required) && required?.ToJsonString() != "true")
                        throw new LlmPolicyException("provider.require_parameters cannot be disabled or null");
                    provider = legacyProvider.Deserialize(LlmPolicyJsonContext.Default.ProviderSettings);
                }
            }
            catch (JsonException)
            {
                throw new LlmPolicyException("legacy reasoning/provider settings contain invalid types or unknown fields");
            }
        }
        if (reasoning?.Effort is { } effort && !Efforts.Contains(effort, StringComparer.Ordinal)
            || reasoning?.MaxTokens is <= 0 || reasoning is { Enabled: false, Effort: not null and not "none" }
            || reasoning is { Effort: not null, MaxTokens: not null })
            throw new LlmPolicyException("reasoning effort, token limit or enabled flag is inconsistent");
        if (provider?.RequireParameters == false || provider?.DataCollection is not (null or "allow" or "deny")
            || provider is not null && new[] { provider.Only, provider.Ignore, provider.Order, provider.Quantizations }
                .SelectMany(values => values).Any(string.IsNullOrWhiteSpace))
            throw new LlmPolicyException("provider parameters, data policy or names are invalid");
        if (model.Name.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) && reasoning is not null
            && model.Temperature is not null)
            throw new LlmPolicyException("OpenAI reasoning models must omit temperature");
        return new LlmModelOptions
        {
            Name = model.Name, Structured = model.Structured, MaxTokens = model.MaxTokens, Temperature = model.Temperature,
            TimeoutSec = model.TimeoutSec ?? defaultTimeout, UseProxy = model.UseProxy,
            Reasoning = reasoning, Provider = provider,
        };
    }

    private static ProviderSettings Clone(ProviderSettings source)
    {
        if (source.Only is null || source.Ignore is null || source.Order is null || source.Quantizations is null)
            throw new LlmPolicyException("provider lists cannot be null");
        return source with
        {
            Only = [.. source.Only], Ignore = [.. source.Ignore], Order = [.. source.Order], Quantizations = [.. source.Quantizations],
        };
    }

    public static string RouteVersion(LlmOptions options, string purpose) => CanonicalJson.Hash(
        JsonSerializer.Serialize(Route(options, purpose).ToList(), LlmPolicyJsonContext.Default.ListLlmModelOptions));
}

public sealed record ReasoningSettings
{
    public string? Effort { get; set; }
    public bool? Enabled { get; set; }
    public bool? Exclude { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
}

public sealed record ProviderSettings
{
    [JsonPropertyName("require_parameters")] public bool? RequireParameters { get; set; }
    [JsonPropertyName("data_collection")] public string? DataCollection { get; set; }
    [JsonPropertyName("allow_fallbacks")] public bool? AllowFallbacks { get; set; }
    public List<string> Only { get; set; } = [];
    public List<string> Ignore { get; set; } = [];
    public List<string> Order { get; set; } = [];
    public List<string> Quantizations { get; set; } = [];
    public string? Sort { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ReasoningSettings))]
[JsonSerializable(typeof(ProviderSettings))]
[JsonSerializable(typeof(List<LlmModelOptions>))]
public sealed partial class LlmPolicyJsonContext : JsonSerializerContext;
