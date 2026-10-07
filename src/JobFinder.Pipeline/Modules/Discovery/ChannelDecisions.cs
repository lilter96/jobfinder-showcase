using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed record ChannelDecisionSnapshot
{
    public int Version { get; set; } = 1;
    public long Id { get; set; }
    public long Revision { get; set; }
    public string? Username { get; set; }
    public long? PeerId { get; set; }
    public ChannelKind Kind { get; set; }
    public ChannelStatus Status { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    public static ChannelDecisionSnapshot Capture(TelegramChannel source) => new()
    {
        Id = source.Id, Revision = source.DecisionRevision, Username = source.Username, PeerId = source.PeerId,
        Kind = source.Kind, Status = source.Status, AddedAt = source.AddedAt,
    };

    public bool Matches(TelegramChannel source) => Version == 1 && Revision >= 0 && Id == source.Id
        && Revision == source.DecisionRevision && string.Equals(Username, source.Username, StringComparison.OrdinalIgnoreCase)
        && PeerId == source.PeerId && Kind == source.Kind && Status == source.Status && AddedAt == source.AddedAt;
}

public sealed record ChannelProposal(string Username, string? Title, ChannelKind Kind, string? Reason);

public sealed class ChannelDecisions(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes, Notifier notifier,
    JobTriggers triggers, TimeProvider time, ChannelMembershipRequests memberships)
{
    public const int CardSize = 20;

    public async Task<bool> IsCurrentApprovedAsync(ChannelDecisionSnapshot expected, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var source = await context.TelegramChannels.AsNoTracking().SingleOrDefaultAsync(row => row.Id == expected.Id, ct);
        if (source is not { Status: ChannelStatus.Approved } || !expected.Matches(source)) return false;
        var last = await context.TelegramChannelDecisionRevisions.AsNoTracking().Where(row => row.ChannelId == source.Id).OrderByDescending(row => row.Revision).FirstOrDefaultAsync(ct);
        return HasProof(source, last);
    }

    public Task<ActionResult> SetAsync(ChannelDecisionSnapshot expected, ChannelStatus desired, string actor, CancellationToken ct) =>
        ChangeAsync([expected], desired, actor, null, ct);

    public Task<ActionResult> ApproveAsync(IReadOnlyList<ChannelDecisionSnapshot> expected, string actor, CancellationToken ct) =>
        ChangeAsync(expected, ChannelStatus.Approved, actor, null, ct);

    public Task<ActionResult> ApproveBatchAsync(long id, string actor, CancellationToken ct) =>
        ChangeAsync([], ChannelStatus.Approved, actor, id, ct);

    private async Task<ActionResult> ChangeAsync(IReadOnlyList<ChannelDecisionSnapshot> expected, ChannelStatus desired,
        string actor, long? batchId, CancellationToken ct)
    {
        if (desired is not (ChannelStatus.Approved or ChannelStatus.Rejected)) throw new ArgumentException("only approval or rejection is a user decision", nameof(desired));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("actor is required", nameof(actor));
        using var stage = Telemetry.StartStage("decide", "telegram-channels").Tag("batch.id", batchId).Tag("count", expected.Count).Tag("status", desired.ToString());
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        TelegramChannelApprovalBatch? batch = null;
        if (batchId is { } id)
        {
            batch = await context.TelegramChannelApprovalBatches.SingleOrDefaultAsync(row => row.Id == id, ct);
            if (batch is null || batch.Version != 1 || batch.InputHash != CanonicalJson.Hash(batch.InputJson)) return Conflict();
            try { expected = JsonSerializer.Deserialize(batch.InputJson, ChannelDecisionJsonContext.Default.ListChannelDecisionSnapshot) ?? []; }
            catch (JsonException) { return Conflict(); }
            if (expected.Count is 0 or > CardSize || expected.Any(snapshot => snapshot is null || snapshot.Status != ChannelStatus.Proposed
                || snapshot.Version != 1 || snapshot.Id <= 0 || snapshot.Revision < 0 || snapshot.Revision == long.MaxValue)
                || expected.Select(snapshot => snapshot.Id).Distinct().Count() != expected.Count) return Conflict();
            if (!await HasCardProofAsync(context, batch, expected.Count, ct)) return Conflict();
            if (batch.AppliedAt is not null)
            {
                var history = await context.TelegramChannelDecisionRevisions.Where(row => row.BatchId == id).ToListAsync(ct);
                if (history.Count != expected.Count || expected.Any(snapshot => !history.Any(row => row.ChannelId == snapshot.Id
                    && row.Revision == snapshot.Revision + 1 && row.Status == ChannelStatus.Approved
                    && CanonicalJson.Hash(row.BeforeJson) == CanonicalJson.Hash(JsonSerializer.Serialize(snapshot, ChannelDecisionJsonContext.Default.ChannelDecisionSnapshot)))))
                    return Conflict();
                return ActionResult.Done("эта подборка уже обработана; новых изменений нет");
            }
        }
        if (expected.Count == 0) return ActionResult.Done("нет каналов в показанной подборке");
        if (expected.Any(snapshot => snapshot is null || snapshot.Id <= 0 || snapshot.Version != 1 || snapshot.Revision < 0 || snapshot.Revision == long.MaxValue)
            || expected.Select(snapshot => snapshot.Id).Distinct().Count() != expected.Count) return Conflict();
        var ids = expected.Select(snapshot => snapshot.Id).ToArray();
        var channels = await context.TelegramChannels.Where(row => ids.Contains(row.Id)).ToDictionaryAsync(row => row.Id, ct);
        var proofs = await context.TelegramChannelDecisionRevisions.AsNoTracking().Where(row => ids.Contains(row.ChannelId))
            .GroupBy(row => row.ChannelId).Select(group => group.OrderByDescending(row => row.Revision).First()).ToDictionaryAsync(row => row.ChannelId, ct);
        foreach (var source in channels.Values) if (!HasProof(source, proofs.GetValueOrDefault(source.Id))) return Conflict();
        foreach (var source in channels.Values) if (!await ChannelMembershipRequests.DecisionAllowedAsync(context, source, ct)) return Conflict();
        if (expected.Any(snapshot => !channels.TryGetValue(snapshot.Id, out var source) || !snapshot.Matches(source)
            || desired == ChannelStatus.Approved && source.Status is not (ChannelStatus.Proposed or ChannelStatus.Rejected or ChannelStatus.Failed or ChannelStatus.Approved)
            || desired == ChannelStatus.Rejected && source.Status is not (ChannelStatus.Proposed or ChannelStatus.Approved or ChannelStatus.Rejected))) return Conflict();
        var changed = 0;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        foreach (var snapshot in expected.OrderBy(snapshot => snapshot.Id))
        {
            var source = channels[snapshot.Id];
            if (source.Status == desired) continue;
            await ChannelMembershipRequests.CancelPendingAsync(context, source, time.GetUtcNow(), ct);
            source.Status = desired;
            source.DecisionRevision = checked(source.DecisionRevision + 1);
            if (desired == ChannelStatus.Approved) source.Error = null;
            context.TelegramChannelDecisionRevisions.Add(new()
            {
                ChannelId = source.Id, Revision = source.DecisionRevision, BeforeJson = JsonSerializer.Serialize(snapshot, ChannelDecisionJsonContext.Default.ChannelDecisionSnapshot),
                Status = desired, Actor = actor, PublishedAt = time.GetUtcNow(), BatchId = batchId,
            });
            if (desired == ChannelStatus.Approved) await memberships.QueueJoinedAsync(context, outbox, source, ct);
            changed++;
        }
        if (changed == 0) return ActionResult.Done("без изменений");
        if (batch is not null) batch.AppliedAt = time.GetUtcNow();
        notifier.Add(context, NotificationKind.Info, desired == ChannelStatus.Approved
            ? $"✅ Одобрено каналов в проверенной подборке: {changed}; подписка пойдёт постепенно."
            : $"⛔ Отклонено каналов в проверенной подборке: {changed}.");
        try { await notifier.SaveAndFlushAsync(context, outbox, ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        if (desired == ChannelStatus.Approved) triggers.Trigger(JobNames.TelegramSync);
        return ActionResult.Done($"обновлено каналов: {changed}");
    }

    public async Task<int> ProposeAsync(IReadOnlyList<ChannelProposal> proposals, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("propose", "telegram-channels").Tag("count", proposals.Count);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        var existing = (await context.TelegramChannels.Where(row => row.Username != null).Select(row => row.Username!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<TelegramChannel>();
        foreach (var proposal in proposals)
        {
            var username = proposal.Username.Trim().TrimStart('@');
            if (username.Length == 0 || !existing.Add(username)) continue;
            if (username.Length > 64 || username.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') || !Enum.IsDefined(proposal.Kind))
                throw new ArgumentException("invalid channel proposal");
            var now = time.GetUtcNow();
            added.Add(new() { Username = username, Title = proposal.Title, Kind = proposal.Kind, Reason = proposal.Reason,
                Status = ChannelStatus.Proposed, AddedAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero) });
        }
        if (added.Count == 0) return 0;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        context.TelegramChannels.AddRange(added);
        await notifier.SaveJoinedAsync(context, outbox, ct);
        foreach (var chunk in added.Chunk(CardSize)) await AddCardAsync(context, outbox, chunk, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return added.Count;
    }

    public async Task<ActionResult> PublishCurrentCardAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        var proposed = await context.TelegramChannels.Where(row => row.Status == ChannelStatus.Proposed).OrderBy(row => row.Id).ToListAsync(ct);
        if (proposed.Count == 0) return ActionResult.Done("нет каналов, ждущих одобрения");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        foreach (var chunk in proposed.Chunk(CardSize)) await AddCardAsync(context, outbox, chunk, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done($"проверенная подборка каналов опубликована: {proposed.Count}");
    }

    private async Task AddCardAsync(JobFinderDbContext context, IDbContextOutbox outbox, IReadOnlyList<TelegramChannel> proposed, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(proposed.Select(ChannelDecisionSnapshot.Capture).OrderBy(snapshot => snapshot.Id).ToList(), ChannelDecisionJsonContext.Default.ListChannelDecisionSnapshot);
        var batch = new TelegramChannelApprovalBatch { InputJson = json, InputHash = CanonicalJson.Hash(json), CreatedAt = time.GetUtcNow() };
        context.TelegramChannelApprovalBatches.Add(batch);
        await notifier.SaveJoinedAsync(context, outbox, ct);
        var (text, buttons) = Cards.ChannelsProposed(proposed, batch.Id);
        var notice = new Notification { Kind = NotificationKind.ChannelsProposed, Text = text, Buttons = buttons,
            EntityId = batch.Id, Status = NotificationStatus.Pending, CreatedAt = time.GetUtcNow() };
        context.Notifications.Add(notice);
        batch.Notification = notice;
    }

    private static Task LockAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('telegram-channel-decisions', 0))", ct);

    private static async Task<bool> HasCardProofAsync(JobFinderDbContext context, TelegramChannelApprovalBatch batch, int count, CancellationToken ct)
    {
        if (batch.NotificationId is null) return false;
        var notice = await context.Notifications.AsNoTracking().SingleOrDefaultAsync(row => row.Id == batch.NotificationId, ct);
        if (notice is not { Kind: NotificationKind.ChannelsProposed, DeliveryRequestId: not null } || notice.EntityId != batch.Id
            || notice.Buttons.Count != 1 || notice.Buttons[0].Count != 1
            || notice.Buttons[0][0] != Callback.Button($"✅ Одобрить эти {count}", Callback.ApproveChannels, batch.Id)) return false;
        var root = await context.NotificationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == notice.DeliveryRequestId, ct);
        if (root is null || root.NotificationId != notice.Id) return false;
        try { return NotificationDeliveryInputs.Input(root).Matches(notice); }
        catch (UnsupportedNotificationDeliveryException) { return false; }
    }

    internal static bool HasProof(TelegramChannel source, TelegramChannelDecisionRevision? last)
    {
        if (source.DecisionRevision == 0) return last is null;
        if (source.DecisionRevision < 0 || last is null || last.Revision != source.DecisionRevision || last.PublishedAt == default || string.IsNullOrWhiteSpace(last.Actor)) return false;
        ChannelDecisionSnapshot? before;
        try { before = JsonSerializer.Deserialize(last.BeforeJson, ChannelDecisionJsonContext.Default.ChannelDecisionSnapshot); }
        catch (JsonException) { return false; }
        return before is { Version: 1 } && before.Id == source.Id && before.Revision == last.Revision - 1
            && (last.Status == ChannelStatus.Approved && before.Status is ChannelStatus.Proposed or ChannelStatus.Rejected or ChannelStatus.Failed
                || last.Status == ChannelStatus.Rejected && before.Status is ChannelStatus.Proposed or ChannelStatus.Approved)
            && (source.Status is ChannelStatus.Approved or ChannelStatus.Rejected ? last.Status == source.Status
                : source.Status is ChannelStatus.Joined or ChannelStatus.Failed && last.Status == ChannelStatus.Approved);
    }

    private static ActionResult Conflict() => ActionResult.Fail("канал или подборка изменились; исходный выбор сохранён — загрузите актуальные каналы");
}

[JsonSerializable(typeof(ChannelDecisionSnapshot))]
[JsonSerializable(typeof(List<ChannelDecisionSnapshot>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ChannelDecisionJsonContext : JsonSerializerContext;
