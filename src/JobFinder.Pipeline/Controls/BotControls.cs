using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Controls;

public sealed class BotControls(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes, ServerOwnership ownership,
    Notifier notifier, TimeProvider time)
{
    internal const string CursorPrefix = "bot.poll.";

    public async Task<int?> OffsetAsync(long botId, CancellationToken ct)
    {
        if (botId <= 0) throw new ArgumentOutOfRangeException(nameof(botId));
        await using var context = await db.CreateDbContextAsync(ct);
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(row => row.Key == Key(botId), ct);
        return row is null ? null : Cursor(row.Value).NextUpdateId;
    }

    public async Task<int?> AdmitAsync(long botId, long ownerId, int? expectedOffset, IReadOnlyList<BotPollUpdate> updates, CancellationToken ct)
    {
        if (botId <= 0 || ownerId <= 0 || updates.Count > 100 || updates.Any(update => update.UpdateId < 0 || update.UpdateId == int.MaxValue)
            || updates.Select(update => update.UpdateId).Distinct().Count() != updates.Count
            || !updates.Select(update => update.UpdateId).SequenceEqual(updates.Select(update => update.UpdateId).Order()))
            throw new ArgumentException("invalid bot poll capture");
        ownership.EnsureOwned();
        if (updates.Count == 0) return await OffsetAsync(botId, ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, botId, ct);
        var cursor = await context.Settings.SingleOrDefaultAsync(row => row.Key == Key(botId), ct);
        var currentOffset = cursor is null ? (int?)null : Cursor(cursor.Value).NextUpdateId;
        if (currentOffset != expectedOffset) return currentOffset;
        foreach (var update in updates)
        {
            if (currentOffset is { } offset && update.UpdateId < offset) continue;
            if (update.PayloadJson is not null)
            {
                var input = new BotControlInput { BotId = botId, OwnerId = ownerId, UpdateId = update.UpdateId, PayloadJson = update.PayloadJson };
                var json = JsonSerializer.Serialize(input, BotControlJsonContext.Default.BotControlInput);
                var hash = CanonicalJson.Hash(json);
                var old = await context.BotControlRequests.SingleOrDefaultAsync(row => row.BotId == botId && row.UpdateId == update.UpdateId, ct);
                if (old is not null && old.InputHash != hash) throw new InvalidDataException("conflicting bot update capture retained");
                if (old is null)
                {
                    var request = new BotControlRequest { Id = Guid.NewGuid(), BotId = botId, UpdateId = update.UpdateId,
                        Stage = BotControlStage.Pending, InputJson = json, InputHash = hash, CreatedAt = time.GetUtcNow() };
                    Read(request);
                    context.BotControlRequests.Add(request);
                    await outbox.PublishAsync(new ProcessBotControl { RequestId = request.Id });
                }
            }
            currentOffset = update.UpdateId + 1;
        }
        if (currentOffset is null || currentOffset == expectedOffset) return currentOffset;
        if (cursor is null) context.Settings.Add(cursor = new() { Key = Key(botId), Value = "" });
        cursor.Value = JsonSerializer.Serialize(new BotPollCursor { NextUpdateId = currentOffset.Value }, BotControlJsonContext.Default.BotPollCursor);
        cursor.UpdatedAt = time.GetUtcNow();
        ownership.EnsureOwned();
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return currentOffset;
    }

    public async Task<bool> ReconcileAsync(Guid id, string evidence, CancellationToken ct)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000) throw new ArgumentException("checked evidence required");
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.BotControlRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, known.BotId, ct);
        var current = await context.BotControlRequests.SingleAsync(row => row.Id == id, ct);
        if (current.Stage is not (BotControlStage.OutcomeUnknown or BotControlStage.Quarantined)) return false;
        current.ReconciliationJson = JsonSerializer.Serialize(new BotControlReconciliation { Evidence = evidence.Trim(), At = time.GetUtcNow(), InputHash = current.InputHash },
            BotControlJsonContext.Default.BotControlReconciliation);
        current.Stage = BotControlStage.Reconciled;
        current.CompletedAt = time.GetUtcNow();
        notifier.Add(context, NotificationKind.Info, $"Команда бота {id}: исход проверен вручную. Повтор не выполнялся; доставка сообщения/резюме этим не подтверждается.");
        await notifier.SaveAndFlushAsync(context, ct);
        return true;
    }

    internal static BotControlInput Read(BotControlRequest request)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.InputJson, BotControlJsonContext.Default.BotControlInput);
            if (input is not { Version: 1 } || input.BotId <= 0 || input.OwnerId <= 0 || input.BotId != request.BotId || input.UpdateId != request.UpdateId
                || request.InputHash != CanonicalJson.Hash(request.InputJson) || input.PayloadJson is null || input.PayloadJson.Length > 1_000_000)
                throw new UnsupportedBotControlException();
            using var payload = JsonDocument.Parse(input.PayloadJson);
            if (payload.RootElement.ValueKind != JsonValueKind.Object) throw new UnsupportedBotControlException();
            return input;
        }
        catch (JsonException) { throw new UnsupportedBotControlException(); }
    }

    internal static async Task<int> RecoverAsync(JobFinderDbContext context, Notifier notifier, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var ids = await context.BotControlRequests.AsNoTracking().Where(row => row.Stage == BotControlStage.Executing)
            .OrderBy(row => row.BotId).Select(row => new { row.Id, row.BotId }).ToListAsync(ct);
        foreach (var id in ids)
        {
            await LockAsync(context, id.BotId, ct);
            var current = await context.BotControlRequests.SingleAsync(row => row.Id == id.Id, ct);
            if (current.Stage != BotControlStage.Executing) continue;
            current.Stage = BotControlStage.OutcomeUnknown;
            current.Error = "process ended during bot control execution; no automatic replay";
            notifier.Add(context, NotificationKind.Alert, $"⚠️ Команда бота {current.Id} прервана перезапуском. Исход неизвестен; сохранённые последующие команды ждут ручной проверки (--bot-update-reconcile).");
        }
        await notifier.SaveAndFlushAsync(context, ct);
        return ids.Count;
    }

    private static BotPollCursor Cursor(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("nextUpdateId", out var next) || !next.TryGetInt32(out var offset) || offset < 0)
                throw new InvalidDataException("damaged bot cursor retained");
            return new() { NextUpdateId = offset };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new InvalidDataException("damaged bot cursor retained", error); }
    }

    private static string Key(long botId) => CursorPrefix + botId.ToString(CultureInfo.InvariantCulture);
    internal static Task LockAsync(JobFinderDbContext context, long botId, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"bot.controls." + botId.ToString(CultureInfo.InvariantCulture)},0))", ct);
}
