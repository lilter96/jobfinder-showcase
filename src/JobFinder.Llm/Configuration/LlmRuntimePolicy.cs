using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Text;

namespace JobFinder.Llm.Configuration;

public sealed record LlmRuntimePolicy
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public decimal DailyBudgetUsd { get; set; } = 2;
    [JsonRequired] public int MaxAttempts { get; set; } = 4;
    [JsonRequired] public Dictionary<string, LlmModelDefinition> Models { get; set; } = new(StringComparer.Ordinal);
    [JsonRequired] public Dictionary<string, LlmRouteDefinition> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonRequired] public Dictionary<string, LlmQualityProfile> QualityProfiles { get; set; } = new(StringComparer.Ordinal);
}

public sealed record LlmModelDefinition
{
    public string Name { get; set; } = "";
    public string Structured { get; set; } = "json_schema";
    public bool UseProxy { get; set; }
    public ProviderSettings? Provider { get; set; }
    public List<string> ReasoningEfforts { get; set; } = [];
}

public sealed record LlmRouteDefinition
{
    public string QualityProfile { get; set; } = "standard";
    public List<LlmRouteStep> Steps { get; set; } = [];
}

public sealed record LlmRouteStep
{
    public string ModelId { get; set; } = "";
    public int MaxTokens { get; set; } = 2000;
    public int TimeoutSec { get; set; } = 90;
    public double? Temperature { get; set; }
    public ReasoningSettings? Reasoning { get; set; }
}

public sealed record LlmQualityProfile
{
    public string MinimumEffort { get; set; } = "none";
}

public sealed record LlmPolicySnapshot(LlmRuntimePolicy Value, long Version);

public sealed record LlmOperationSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public Guid OperationId { get; set; }
    [JsonRequired] public long PolicyRevision { get; set; }
    [JsonRequired] public LlmRuntimePolicy Policy { get; set; } = new();
}

public interface ILlmPolicySource
{
    Task<LlmPolicySnapshot> ReadAsync(CancellationToken ct);
}

public static class LlmRuntimePolicyCompiler
{
    public static LlmRuntimePolicy Import(LlmOptions effective)
    {
        var policy = new LlmRuntimePolicy
        {
            DailyBudgetUsd = effective.DailyBudgetUsd, MaxAttempts = effective.MaxAttempts,
            QualityProfiles = new() { ["standard"] = new(), ["maximum"] = new() { MinimumEffort = "max" } },
        };
        foreach (var purpose in effective.Routes.Keys)
        {
            var route = new LlmRouteDefinition { QualityProfile = RequiresMaximum(purpose) ? "maximum" : "standard" };
            foreach (var model in LlmPolicyCompiler.Route(effective, purpose))
            {
                var definition = new LlmModelDefinition
                {
                    Name = model.Name, Structured = model.Structured, UseProxy = model.UseProxy, Provider = model.Provider,
                };
                var id = "model-" + CanonicalJson.Hash(JsonSerializer.Serialize(definition, RuntimePolicyJsonContext.Default.LlmModelDefinition))[..16];
                if (!policy.Models.TryGetValue(id, out var registered)) policy.Models[id] = registered = definition;
                var effort = model.Reasoning?.Effort ?? "none";
                if (!registered.ReasoningEfforts.Contains(effort, StringComparer.Ordinal)) registered.ReasoningEfforts.Add(effort);
                route.Steps.Add(new LlmRouteStep
                {
                    ModelId = id, MaxTokens = model.MaxTokens, TimeoutSec = model.TimeoutSec!.Value,
                    Temperature = model.Temperature, Reasoning = model.Reasoning,
                });
            }
            policy.Routes.Add(purpose, route);
        }
        Compile(policy, effective);
        return policy;
    }

    public static LlmOptions Compile(LlmRuntimePolicy policy, LlmOptions deployment)
    {
        if (policy.SchemaVersion != 1 || policy.DailyBudgetUsd < 0 || policy.MaxAttempts <= 0
            || policy.Models is null || policy.Routes is null || policy.QualityProfiles is null)
            throw new LlmPolicyException("invalid LLM policy schema, budget, attempts or registry");
        if (policy.Routes.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.Routes.Count)
            throw new LlmPolicyException("LLM policy has duplicate case-insensitive purposes");
        foreach (var definition in policy.Models)
        {
            if (string.IsNullOrWhiteSpace(definition.Key) || definition.Value is null || definition.Value.ReasoningEfforts is null
                || definition.Value.ReasoningEfforts.Any(effort => EffortRank(effort) < 0))
                throw new LlmPolicyException("invalid model registry ID or reasoning capabilities");
            LlmPolicyCompiler.Model(new LlmModelOptions
            {
                Name = definition.Value.Name, Structured = definition.Value.Structured, UseProxy = definition.Value.UseProxy,
                Provider = definition.Value.Provider,
            }, deployment.TimeoutSec);
        }
        if (policy.QualityProfiles.Any(profile => string.IsNullOrWhiteSpace(profile.Key) || profile.Value is null || EffortRank(profile.Value.MinimumEffort) < 0))
            throw new LlmPolicyException("invalid LLM quality profile");
        var result = new LlmOptions
        {
            Endpoint = deployment.Endpoint, ApiKey = deployment.ApiKey, Proxy = deployment.Proxy, TimeoutSec = deployment.TimeoutSec,
            AppTitle = deployment.AppTitle, Referer = deployment.Referer, DailyBudgetUsd = policy.DailyBudgetUsd, MaxAttempts = policy.MaxAttempts,
        };
        foreach (var entry in policy.Routes)
        {
            if (!LlmPurpose.All.Contains(entry.Key, StringComparer.OrdinalIgnoreCase) || entry.Value is null || entry.Value.Steps is null
                || string.IsNullOrWhiteSpace(entry.Value.QualityProfile) || !policy.QualityProfiles.TryGetValue(entry.Value.QualityProfile, out var quality))
                throw new LlmPolicyException("unknown route purpose, quality profile or null route steps");
            var minimum = EffortRank(quality.MinimumEffort);
            if (RequiresMaximum(entry.Key) && minimum < EffortRank("max"))
                throw new LlmPolicyException($"{entry.Key}: quality profile must require maximum reasoning");
            var models = new List<LlmModelOptions>();
            foreach (var step in entry.Value.Steps)
            {
                if (step is null || string.IsNullOrWhiteSpace(step.ModelId) || !policy.Models.TryGetValue(step.ModelId, out var model))
                    throw new LlmPolicyException("route references an unknown model ID");
                var effort = step.Reasoning is { Enabled: false } ? "none" : step.Reasoning?.Effort ?? "none";
                if (!model.ReasoningEfforts.Contains(effort, StringComparer.Ordinal) || EffortRank(effort) < minimum)
                    throw new LlmPolicyException($"{entry.Key}: model '{step.ModelId}' violates capabilities or the quality floor");
                models.Add(LlmPolicyCompiler.Model(new LlmModelOptions
                {
                    Name = model.Name, Structured = model.Structured, UseProxy = model.UseProxy, Provider = model.Provider,
                    MaxTokens = step.MaxTokens, TimeoutSec = step.TimeoutSec, Temperature = step.Temperature, Reasoning = step.Reasoning,
                }, deployment.TimeoutSec));
            }
            result.Routes.Add(entry.Key, models);
            LlmPolicyCompiler.Route(result, entry.Key);
        }
        return result;
    }

    public static LlmRuntimePolicy Clone(LlmRuntimePolicy value) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(value, RuntimePolicyJsonContext.Default.LlmRuntimePolicy), RuntimePolicyJsonContext.Default.LlmRuntimePolicy)!;

    private static bool RequiresMaximum(string purpose) => purpose.Equals(LlmPurpose.Application, StringComparison.OrdinalIgnoreCase)
        || purpose.Equals(LlmPurpose.Resume, StringComparison.OrdinalIgnoreCase);

    private static int EffortRank(string? effort) => effort switch
    {
        "none" => 0, "minimal" => 1, "low" => 2, "medium" => 3, "high" => 4, "xhigh" or "max" => 5, _ => -1,
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(LlmRuntimePolicy))]
[JsonSerializable(typeof(LlmModelDefinition))]
[JsonSerializable(typeof(LlmOperationSnapshot))]
public sealed partial class RuntimePolicyJsonContext : JsonSerializerContext;
