using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Pipeline.Actions;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed record LearningProgressSnapshot
{
    public string Scope { get; set; } = "";
    public long Revision { get; set; }
    public Dictionary<string, string> Learned { get; set; } = [];
    public HashSet<string> Approved { get; set; } = [];
    public Dictionary<string, long> ItemRevisions { get; set; } = [];
    public string? ProgressRaw { get; set; }
    public string? ApprovedRaw { get; set; }
    public string? MetadataRaw { get; set; }

    public LearningItemSnapshot Item(string key) => new()
    {
        Scope = Scope, Key = key, Revision = ItemRevisions.GetValueOrDefault(key),
        State = Learned.GetValueOrDefault(key), Approved = Approved.Contains(key),
    };
}

public sealed record LearningItemSnapshot
{
    public string Scope { get; set; } = "";
    public string Key { get; set; } = "";
    public long Revision { get; set; }
    public string? State { get; set; }
    public bool Approved { get; set; }
}

public sealed record LearningProgressMetadata
{
    public int Version { get; set; }
    public long Revision { get; set; }
    public Dictionary<string, long> ItemRevisions { get; set; } = [];
    public string ProgressHash { get; set; } = "";
    public string ApprovedHash { get; set; } = "";
}

public sealed record LearningPublication(ActionResult Result, LearningProgressSnapshot? Snapshot = null);
public sealed class UnsupportedLearningProgressException() : Exception("повреждённый или неподдерживаемый прогресс обучения; данные сохранены, автоматический сброс запрещён");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LearningProgressSnapshot))]
[JsonSerializable(typeof(LearningItemSnapshot))]
[JsonSerializable(typeof(LearningProgressMetadata))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(HashSet<string>))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class LearningProgressJsonContext : JsonSerializerContext;
