using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Llm.Tasks;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed record InterviewEditSnapshot
{
    public int Version { get; set; } = 1;
    public InterviewSourceSnapshot Source { get; set; } = new();
    public Guid? RequestId { get; set; }

    public static InterviewEditSnapshot Capture(Interview source) => new()
    {
        Source = InterviewSourceSnapshot.Capture(source), RequestId = source.PreparationRequestId,
    };

    public bool Matches(Interview source) => Version == 1 && Source is not null
        && source.PreparationRequestId == RequestId && InterviewSourceSnapshot.Capture(source).Hash() == Source.Hash();
}

public sealed record InterviewPositionSnapshot
{
    public int Version { get; set; } = 1;
    public InterviewEditSnapshot Interview { get; set; } = new();
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public string PackHash { get; set; } = "";
    public PlannedQuestion Question { get; set; } = new();
}

public sealed class InterviewEditConflictException() : Exception("интервью уже изменено: сохраните свой текст и явно обновите данные");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(InterviewEditSnapshot))]
[JsonSerializable(typeof(InterviewPositionSnapshot))]
public sealed partial class InterviewEditJsonContext : JsonSerializerContext;
