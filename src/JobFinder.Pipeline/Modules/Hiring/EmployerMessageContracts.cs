using System.Text.Json.Serialization;
using JobFinder.Core.Domain;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record EmployerMessageInput
{
    public int Version { get; set; } = 1;
    public long VacancyId { get; set; }
    public EmployerMessageKind Kind { get; set; }
    public long? ApplicationId { get; set; }
    public long? ContactId { get; set; }
    public long? EventId { get; set; }
    public long? OutreachId { get; set; }
    public long SourceRevision { get; set; }
    public ApplicationChannel Channel { get; set; }
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string ContextHash { get; set; } = "";
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public bool First { get; set; }
    public bool? NeedsFact { get; set; }
    public bool Fresh { get; set; }
    public bool CreateApplication { get; set; }
    public bool AttachResume { get; set; }
    public bool Automatic { get; set; }
    public string Contact { get; set; } = "";
    public string Situation { get; set; } = "";
    public string Conversation { get; set; } = "";
    public string? Subject { get; set; }
    public string? InReplyTo { get; set; }
    public long? HhChatId { get; set; }
    public long? HhVacancyId { get; set; }
}

public sealed record PrepareEmployerMessage
{
    public Guid MessageId { get; set; }
    public int Version { get; set; } = 1;
}

public sealed record DispatchEmployerMessage
{
    public Guid MessageId { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class UnsupportedEmployerMessageException() : Exception("unsupported employer message command");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EmployerMessageInput))]
[JsonSerializable(typeof(PrepareEmployerMessage))]
[JsonSerializable(typeof(DispatchEmployerMessage))]
public sealed partial class EmployerMessageJsonContext : JsonSerializerContext;
