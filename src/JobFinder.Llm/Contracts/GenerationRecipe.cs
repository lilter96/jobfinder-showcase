using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Text;
using JobFinder.Llm.Prompts;

namespace JobFinder.Llm.Contracts;

public static class GenerationRecipe
{
    public static ArtifactRecipe Capture(PromptCatalog prompts, Func<string, string> routeVersion,
        IEnumerable<(ILlmTaskDefinition Task, string Purpose)> tasks, IReadOnlyDictionary<string, string?> inputs,
        IReadOnlyDictionary<string, string>? rules = null, long policyRevision = 0, Guid? operationId = null)
    {
        var captured = tasks.Select(request =>
        {
            if (!request.Task.Purposes.Contains(request.Purpose, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"task {request.Task.Id} cannot use purpose '{request.Purpose}'", nameof(tasks));
            return new TaskRecipe
            {
                TaskId = request.Task.Id, Purpose = request.Purpose.ToLowerInvariant(), RulesVersion = request.Task.RulesVersion,
                PromptVersion = CanonicalJson.Hash(JsonSerializer.Serialize(prompts.Template(request.Task.Prompt), RecipeJsonContext.Default.String)),
                ContractVersion = request.Task.ContractVersion, RouteVersion = routeVersion(request.Purpose),
                ProviderProjectionVersion = ProviderSchemaProjection.Version,
            };
        }).ToList();
        if (captured.Count == 0) throw new ArgumentException("a generation recipe must contain a task", nameof(tasks));
        return new ArtifactRecipe
        {
            PolicyRevision = policyRevision, OperationId = operationId, Tasks = captured,
            Inputs = inputs.ToDictionary(pair => pair.Key,
                pair => pair.Value is null ? null : CanonicalJson.Hash(JsonSerializer.Serialize(pair.Value, RecipeJsonContext.Default.String)), StringComparer.Ordinal),
            Rules = rules?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal),
        };
    }

    internal static ArtifactRecipe Call(ILlmTaskDefinition task, LlmTaskPayload payload, PromptCatalog prompts, Client.LlmClient operation)
    {
        var inputs = payload.Bindings.ToDictionary(pair => "binding." + pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        inputs["user"] = payload.UserMessage;
        return Capture(prompts, operation.RouteVersion, [(task, payload.Purpose)], inputs,
            policyRevision: operation.PolicyRevision, operationId: operation.OperationId);
    }
}
