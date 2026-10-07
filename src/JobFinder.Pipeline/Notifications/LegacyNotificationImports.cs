using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Notifications;

public sealed record LegacyNotificationSource
{
    public int Version { get; set; } = 1;
    public string RawJson { get; set; } = "";
    public string RawHash { get; set; } = "";

    internal JsonObject Read(long notificationId)
    {
        if (Version != 1 || CanonicalJson.Hash(RawJson) != RawHash) throw new UnsupportedNotificationDeliveryException();
        var source = JsonNode.Parse(RawJson) as JsonObject ?? throw new UnsupportedNotificationDeliveryException();
        if (source["id"]?.GetValue<long>() != notificationId || source["delivery_request_id"] is not null)
            throw new UnsupportedNotificationDeliveryException();
        return source;
    }

    internal bool Matches(string current, long notificationId)
    {
        var original = Read(notificationId);
        var actual = JsonNode.Parse(current) as JsonObject ?? throw new UnsupportedNotificationDeliveryException();
        foreach (var name in new[] { "delivery_request_id", "status" })
        {
            original.Remove(name);
            actual.Remove(name);
        }
        return CanonicalJson.Hash(original.ToJsonString()) == CanonicalJson.Hash(actual.ToJsonString());
    }
}

public sealed record LegacyNotificationReconciliation
{
    public int Version { get; set; } = 1;
    public bool Sent { get; set; }
    public string Evidence { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string InputHash { get; set; } = "";
}

[MessageIdentity("jobfinder.notifications.quarantine-legacy.v1")]
public sealed record QuarantineLegacyNotification
{
    public int Version { get; set; } = 1;
    public Guid RequestId { get; set; }
}

public sealed class LegacyNotificationImports(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    ServerOwnership ownership, TimeProvider time)
{
    public async Task<int> ImportBatchAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        ownership.EnsureOwned();
        using var span = Telemetry.StartStage("import-uncertain", "notification-delivery");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await NotificationDeliveryRequests.LockAsync(context, ct);
        var ids = await context.Notifications.AsNoTracking().Where(row => row.DeliveryRequestId == null
            && (row.Status == NotificationStatus.Pending || row.Status == NotificationStatus.Failed || row.Status == NotificationStatus.Sending || row.Status == NotificationStatus.OutcomeUnknown)
            && !context.NotificationDeliveryRequests.Any(root => root.NotificationId == row.Id))
            .OrderBy(row => row.Id).Select(row => row.Id).Take(limit).ToListAsync(ct);
        span.Tag("batch.size", ids.Count);
        foreach (var id in ids)
        {
            var raw = await RawAsync(context, id, ct);
            var input = new NotificationDeliveryInput { Version = 2, NotificationId = id, Kind = NotificationKind.Info,
                LegacyUncertain = true, LegacySource = new() { RawJson = raw, RawHash = CanonicalJson.Hash(raw) } };
            var json = JsonSerializer.Serialize(input, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput);
            var root = new NotificationDeliveryRequest { Id = Guid.NewGuid(), NotificationId = id, Stage = NotificationDeliveryStage.OutcomeUnknown,
                InputJson = json, InputHash = CanonicalJson.Hash(json), AdmissionJson = json, AdmissionHash = CanonicalJson.Hash(json), CreatedAt = time.GetUtcNow(),
                Error = "legacy aggregate history cannot prove which effects were sent; explicit checked reconciliation required" };
            NotificationDeliveryInputs.Input(root);
            var affected = await context.Notifications.Where(row => row.Id == id && row.DeliveryRequestId == null
                && (row.Status == NotificationStatus.Pending || row.Status == NotificationStatus.Failed || row.Status == NotificationStatus.Sending || row.Status == NotificationStatus.OutcomeUnknown))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DeliveryRequestId, root.Id).SetProperty(row => row.Status, NotificationStatus.OutcomeUnknown), ct);
            if (affected != 1) throw new InvalidOperationException("legacy notification ownership changed");
            context.NotificationDeliveryRequests.Add(root);
            await outbox.PublishAsync(new QuarantineLegacyNotification { RequestId = root.Id });
        }
        ownership.EnsureOwned();
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return ids.Count;
    }

    internal static async Task<string> RawAsync(JobFinderDbContext context, long id, CancellationToken ct) =>
        await context.Database.SqlQuery<string>($"SELECT to_jsonb(n)::text AS \"Value\" FROM notifications n WHERE id = {id} FOR UPDATE").SingleAsync(ct);

    public async Task ObserveAsync(QuarantineLegacyNotification command, CancellationToken ct)
    {
        if (command.Version != 1 || command.RequestId == Guid.Empty) throw new UnsupportedNotificationDeliveryException();
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        var root = await context.NotificationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == command.RequestId, ct)
            ?? throw new UnsupportedNotificationDeliveryException();
        try
        {
            var input = NotificationDeliveryInputs.Input(root);
            if (!input.LegacyUncertain || input.LegacySource is null || root.Stage is not (NotificationDeliveryStage.OutcomeUnknown or NotificationDeliveryStage.Reconciled))
                throw new UnsupportedNotificationDeliveryException();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new UnsupportedNotificationDeliveryException();
        }
    }
}

public static class LegacyNotificationHandler
{
    public static Task Handle(QuarantineLegacyNotification command, LegacyNotificationImports imports, CancellationToken ct) => imports.ObserveAsync(command, ct);
}
