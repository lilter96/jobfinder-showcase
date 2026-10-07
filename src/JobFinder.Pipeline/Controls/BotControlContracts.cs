using System.Text.Json;
using System.Text.Json.Serialization;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Controls;

public sealed record BotControlInput
{
    public int Version { get; set; } = 1;
    public long BotId { get; set; }
    public long OwnerId { get; set; }
    public int UpdateId { get; set; }
    public string PayloadJson { get; set; } = "";
}

public sealed record BotPollUpdate
{
    public int UpdateId { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed record BotPollCursor
{
    public int Version { get; set; } = 1;
    public int NextUpdateId { get; set; }
}

public sealed record BotControlReconciliation
{
    public int Version { get; set; } = 1;
    public string Evidence { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string InputHash { get; set; } = "";
}

[MessageIdentity("jobfinder.controls.process-bot.v1")]
public sealed record ProcessBotControl
{
    public int Version { get; set; } = 1;
    public Guid RequestId { get; set; }
}

public interface IBotControlExecutor
{
    bool CanExecute(BotControlInput input);
    Task ExecuteAsync(BotControlInput input, CancellationToken ct);
}

public sealed class DormantBotControlExecutor : IBotControlExecutor
{
    public bool CanExecute(BotControlInput input) => false;
    public Task ExecuteAsync(BotControlInput input, CancellationToken ct) => throw new InvalidOperationException("bot controls unavailable");
}

public sealed class UnsupportedBotControlException : Exception;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BotControlInput))]
[JsonSerializable(typeof(BotPollCursor))]
[JsonSerializable(typeof(BotControlReconciliation))]
[JsonSerializable(typeof(ProcessBotControl))]
public sealed partial class BotControlJsonContext : JsonSerializerContext;
