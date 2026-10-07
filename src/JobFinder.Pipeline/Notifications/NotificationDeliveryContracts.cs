using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Notifications;

public sealed record NotificationTarget
{
    public long ChatId { get; set; }
    public long BotId { get; set; }
}

public sealed record NotificationDeliveryInput
{
    public int Version { get; set; } = 1;
    public NotificationTarget? Target { get; set; }
    public long NotificationId { get; set; }
    public NotificationKind Kind { get; set; }
    public long? EntityId { get; set; }
    public string Text { get; set; } = "";
    public List<List<string>> Buttons { get; set; } = [];
    public string? AttachmentPath { get; set; }
    public string? AttachmentName { get; set; }
    public int? EditKeyboardMessageId { get; set; }
    public long? EditParentNotificationId { get; set; }
    public bool LegacyUncertain { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyNotificationSource? LegacySource { get; set; }

    public static NotificationDeliveryInput Capture(Notification notice, NotificationTarget? target, bool legacy = false, int version = 2) => new()
    {
        Version = notice.Kind == NotificationKind.EditPrompt ? 3 : version, Target = target, NotificationId = notice.Id, Kind = notice.Kind, EntityId = notice.EntityId, Text = notice.Text,
        Buttons = notice.Buttons.Select(row => row.ToList()).ToList(), AttachmentPath = notice.AttachmentPath,
        AttachmentName = notice.AttachmentName, EditKeyboardMessageId = notice.EditKeyboardMessageId,
        EditParentNotificationId = version == 1 ? null : notice.EditParentNotificationId, LegacyUncertain = legacy,
    };

    public bool Matches(Notification notice) => SourceHash() == Capture(notice, Target, LegacyUncertain, Version).SourceHash();
    public string SourceHash() => CanonicalJson.Hash(JsonSerializer.Serialize(this, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput));
}

public sealed record NotificationEffect
{
    public NotificationEffectKind Kind { get; set; }
    public string Text { get; set; } = "";
    public bool Html { get; set; } = true;
    public List<List<string>> Buttons { get; set; } = [];
    public int? MessageId { get; set; }
    public string? AttachmentPath { get; set; }
    public string? AttachmentName { get; set; }
    public string? AttachmentHash { get; set; }
}

public sealed record NotificationPlan
{
    public int Version { get; set; } = 1;
    public List<NotificationEffect> Effects { get; set; } = [];
}

public sealed record NotificationEffectReceipt
{
    public DeliveryAttemptStatus Status { get; set; }
    public int? MessageId { get; set; }
    public string? Error { get; set; }
    public int? RetrySeconds { get; set; }
    public bool ParseRejected { get; set; }
}

public interface INotificationDeliveryTransport
{
    NotificationTarget? CaptureTarget();
    Task<NotificationEffectReceipt> SendAsync(NotificationTarget target, NotificationEffect effect, byte[]? attachment,
        Func<CancellationToken, Task<bool>> maySend, CancellationToken ct);
}

public sealed class UnavailableNotificationTransport : INotificationDeliveryTransport
{
    public NotificationTarget? CaptureTarget() => null;
    public Task<NotificationEffectReceipt> SendAsync(NotificationTarget target, NotificationEffect effect, byte[]? attachment,
        Func<CancellationToken, Task<bool>> maySend, CancellationToken ct) => throw new InvalidOperationException("notification transport is not configured");
}

[MessageIdentity("jobfinder.notifications.delivery.v1")]
public sealed record DeliverNotification
{
    public int Version { get; set; } = 1;
    public Guid RequestId { get; set; }
    public NotificationDeliveryStage Stage { get; set; } = NotificationDeliveryStage.Preparing;
    public int EffectIndex { get; set; }
}

[MessageIdentity("jobfinder.notifications.target.v1")]
public sealed record BindNotificationTarget
{
    public int Version { get; set; } = 1;
    public Guid RequestId { get; set; }
}

public sealed class UnsupportedNotificationDeliveryException() : Exception("unsupported notification delivery checkpoint");

internal static partial class NotificationDeliveryInputs
{
    public static NotificationDeliveryInput Input(NotificationDeliveryRequest request)
    {
        if (CanonicalJson.Hash(request.InputJson) != request.InputHash) throw new UnsupportedNotificationDeliveryException();
        var input = JsonSerializer.Deserialize(request.InputJson, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput);
        if (input is not { Version: 1 or 2 or 3, Text: not null, Buttons: not null } || input.NotificationId != request.NotificationId
            || input.NotificationId <= 0 || !Enum.IsDefined(input.Kind)
            || input.Buttons.Any(row => row is null || row.Any(button => button is null))) throw new UnsupportedNotificationDeliveryException();
        if ((input.Kind == NotificationKind.EditPrompt) != (input.Version == 3)
            || input.Version == 3 && (request.PlainText || input.LegacyUncertain || input.AttachmentPath is not null || input.AttachmentName is not null
                || input.EditKeyboardMessageId is not null || input.EditParentNotificationId is not null || input.Buttons.Count != 0))
            throw new UnsupportedNotificationDeliveryException();
        if (input.LegacySource is not null)
        {
            input.LegacySource.Read(request.NotificationId);
            if (!input.LegacyUncertain || input.Version != 2 || input.Target is not null || request.Stage is not (NotificationDeliveryStage.OutcomeUnknown or NotificationDeliveryStage.Reconciled)
                || request.InputHash != request.AdmissionHash || request.AdmissionJson is null || CanonicalJson.Hash(request.AdmissionJson) != request.AdmissionHash
                || request.PlanJson is not null || request.PlanHash is not null || request.AttemptId is not null || request.ClaimEnvelopeId is not null
                || request.RetryAt is not null || request.EffectIndex != 0 || request.PlainText) throw new UnsupportedNotificationDeliveryException();
            if (request.Stage == NotificationDeliveryStage.OutcomeUnknown)
            {
                if (request.LegacyReconciliationJson is not null || request.LegacyReconciliationHash is not null || request.CompletedAt is not null)
                    throw new UnsupportedNotificationDeliveryException();
            }
            else
            {
                if (request.LegacyReconciliationJson is null || CanonicalJson.Hash(request.LegacyReconciliationJson) != request.LegacyReconciliationHash)
                    throw new UnsupportedNotificationDeliveryException();
                var receipt = JsonSerializer.Deserialize(request.LegacyReconciliationJson, NotificationDeliveryJsonContext.Default.LegacyNotificationReconciliation);
                if (receipt is not { Version: 1 } || string.IsNullOrWhiteSpace(receipt.Evidence) || receipt.Evidence.Length > 2000
                    || receipt.At == default || receipt.At != request.CompletedAt || receipt.InputHash != request.InputHash)
                    throw new UnsupportedNotificationDeliveryException();
            }
            return input;
        }
        if (request.LegacyReconciliationJson is not null || request.LegacyReconciliationHash is not null) throw new UnsupportedNotificationDeliveryException();
        if (input.Version == 1)
        {
            if (input.Target is not { ChatId: > 0, BotId: > 0 } || request.Stage == NotificationDeliveryStage.AwaitingTarget
                || request.AdmissionJson is not null || request.AdmissionHash is not null) throw new UnsupportedNotificationDeliveryException();
        }
        else
        {
            if (request.AdmissionJson is null || request.AdmissionHash is null || CanonicalJson.Hash(request.AdmissionJson) != request.AdmissionHash)
                throw new UnsupportedNotificationDeliveryException();
            var admission = JsonSerializer.Deserialize(request.AdmissionJson, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput);
            if (admission is not { Version: 2 or 3 } || admission.Version != input.Version
                || admission.Target is not null && admission.Target is not { ChatId: > 0, BotId: > 0 })
                throw new UnsupportedNotificationDeliveryException();
            var originalTarget = input.Target;
            input.Target = admission.Target;
            var matches = input.SourceHash() == admission.SourceHash();
            input.Target = originalTarget;
            if (!matches || admission.Target is not null && admission.Target != originalTarget) throw new UnsupportedNotificationDeliveryException();
            if (originalTarget is null && request.Stage is NotificationDeliveryStage.AwaitingTarget or NotificationDeliveryStage.Cancelled or NotificationDeliveryStage.Failed)
            {
                if (originalTarget is not null || request.InputHash != request.AdmissionHash || request.PlanJson is not null || request.PlanHash is not null
                    || request.AttemptId is not null || request.EffectIndex != 0 || request.PlainText) throw new UnsupportedNotificationDeliveryException();
            }
            else if (request.Stage == NotificationDeliveryStage.AwaitingTarget || originalTarget is not { ChatId: > 0, BotId: > 0 })
                throw new UnsupportedNotificationDeliveryException();
        }
        return input;
    }

    public static NotificationPlan Plan(NotificationDeliveryRequest request)
    {
        if (request.PlanJson is null || request.PlanHash is null || CanonicalJson.Hash(request.PlanJson) != request.PlanHash)
            throw new UnsupportedNotificationDeliveryException();
        var plan = JsonSerializer.Deserialize(request.PlanJson, NotificationDeliveryJsonContext.Default.NotificationPlan);
        if (plan is not { Version: 1 or 2, Effects: not null } || plan.Effects.Count is < 1 or > 2 || request.EffectIndex < 0 || request.EffectIndex >= plan.Effects.Count
            || plan.Effects.Any(effect => effect is null || !Enum.IsDefined(effect.Kind) || effect.Text is null || effect.Buttons is null
                || effect.Buttons.Any(row => row is null || row.Any(button => button is null))
                || effect.Kind == NotificationEffectKind.Document && (string.IsNullOrWhiteSpace(effect.AttachmentPath)
                    || string.IsNullOrWhiteSpace(effect.AttachmentName) || effect.AttachmentHash is not { Length: 64 })
                || effect.Kind is NotificationEffectKind.EditKeyboard or NotificationEffectKind.EditText && effect.MessageId is not > 0))
            throw new UnsupportedNotificationDeliveryException();
        var input = Input(request);
        if (plan.Version != (input.Kind == NotificationKind.EditPrompt ? 2 : 1)) throw new UnsupportedNotificationDeliveryException();
        var kinds = input.Kind == NotificationKind.EditPrompt ? new[] { NotificationEffectKind.EditPrompt }
            : input.EditKeyboardMessageId is not null ? new[] { NotificationEffectKind.EditKeyboard }
            : input.AttachmentPath is null ? [NotificationEffectKind.Text]
            : input.Text.Length <= 1000 ? [NotificationEffectKind.Document] : [NotificationEffectKind.Text, NotificationEffectKind.Document];
        if (!plan.Effects.Select(effect => effect.Kind).SequenceEqual(kinds)) throw new UnsupportedNotificationDeliveryException();
        for (var index = 0; index < plan.Effects.Count; index++)
        {
            var effect = plan.Effects[index];
            var expectedText = effect.Kind == NotificationEffectKind.EditKeyboard || index > 0 ? "" : input.Text;
            var expectedButtons = index == 0 ? input.Buttons : [];
            var retired = input.Kind == NotificationKind.CommentDraft && index == 0
                && effect.Buttons.Count == 1 && effect.Buttons[0].SequenceEqual(new[] { "♻️ Устаревшая карточка|noop:0:0" });
            if (effect.Html != (input.Kind != NotificationKind.EditPrompt) || effect.Text != expectedText || effect.MessageId != (effect.Kind == NotificationEffectKind.EditKeyboard ? input.EditKeyboardMessageId : null)
                || !retired && JsonSerializer.Serialize(effect.Buttons) != JsonSerializer.Serialize(expectedButtons)) throw new UnsupportedNotificationDeliveryException();
            if (effect.Kind == NotificationEffectKind.Document && (effect.AttachmentPath != input.AttachmentPath
                || effect.AttachmentName != (input.AttachmentName ?? Path.GetFileName(JobFinder.Core.Options.StoredPath.Resolve(input.AttachmentPath!)))
                || !effect.AttachmentHash!.All(char.IsAsciiHexDigit))) throw new UnsupportedNotificationDeliveryException();
            if (effect.Kind == NotificationEffectKind.EditPrompt && (effect.AttachmentPath is not null || effect.AttachmentName is not null || effect.AttachmentHash is not null))
                throw new UnsupportedNotificationDeliveryException();
        }
        return plan;
    }

    public static NotificationEffect Effect(NotificationDeliveryRequest request)
    {
        var original = Plan(request).Effects[request.EffectIndex];
        var effect = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, NotificationDeliveryJsonContext.Default.NotificationEffect), NotificationDeliveryJsonContext.Default.NotificationEffect)!;
        if (request.PlainText)
        {
            effect.Html = false;
            effect.Text = WebUtility.HtmlDecode(Tags().Replace(effect.Text, ""));
        }
        return effect;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeliverNotification))]
[JsonSerializable(typeof(BindNotificationTarget))]
[JsonSerializable(typeof(NotificationDeliveryInput))]
[JsonSerializable(typeof(NotificationPlan))]
[JsonSerializable(typeof(NotificationEffect))]
[JsonSerializable(typeof(QuarantineLegacyNotification))]
[JsonSerializable(typeof(LegacyNotificationReconciliation))]
public sealed partial class NotificationDeliveryJsonContext : JsonSerializerContext;
