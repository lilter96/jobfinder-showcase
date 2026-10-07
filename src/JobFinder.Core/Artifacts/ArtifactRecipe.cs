using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Text;

namespace JobFinder.Core.Artifacts;

public sealed record TaskRecipe
{
    public string TaskId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public string ContractVersion { get; set; } = "";
    public string RulesVersion { get; set; } = "";
    public string RouteVersion { get; set; } = "";
    public string ProviderProjectionVersion { get; set; } = "";
}

public sealed record ArtifactRecipe
{
    public int Version { get; set; } = 1;
    public long PolicyRevision { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OperationId { get; set; }
    public List<TaskRecipe> Tasks { get; set; } = [];
    public Dictionary<string, string?> Inputs { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Rules { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] public string Hash => CanonicalJson.Hash(JsonSerializer.Serialize(this with { PolicyRevision = 0, OperationId = null }, RecipeJsonContext.Default.ArtifactRecipe));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ArtifactRecipe))]
[JsonSerializable(typeof(string))]
public sealed partial class RecipeJsonContext : JsonSerializerContext;
