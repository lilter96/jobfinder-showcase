using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Notifications;

public sealed class NotificationDeliveryRequests(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    INotificationDeliveryTransport transport, TimeProvider time)
{
    public Task<Guid> PublishAsync(Notification notification, CancellationToken ct)
        => PublishAsync(notification, null, ct);

    public Task<Guid> PublishCapturedAsync(Notification notification, NotificationTarget target, CancellationToken ct)
    {
        if (target is not { BotId: > 0, ChatId: > 0 }) throw new ArgumentException("notification destination must be captured");
        return PublishAsync(notification, new NotificationTarget { BotId = target.BotId, ChatId = target.ChatId }, ct);
    }

    public async Task<Guid?> PublishKeyboardAsync(NotificationTarget target, int messageId, List<List<string>> buttons, CancellationToken ct,
        string? replaceCallback = null)
    {
        if (target is not { BotId: > 0, ChatId: > 0 } || messageId <= 0) throw new ArgumentException("keyboard destination must be captured");
        var captured = new NotificationTarget { BotId = target.BotId, ChatId = target.ChatId };
        var rows = buttons.Select(row => row.ToList()).ToList();
        if (replaceCallback is not null && (string.IsNullOrWhiteSpace(replaceCallback) || rows.Count != 1))
            throw new ArgumentException("keyboard row update requires its captured callback and one replacement row");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, ct);
        var parents = await context.Notifications.AsNoTracking().Where(row => row.TelegramMessageId == messageId && row.DeliveryRequestId != null).ToListAsync(ct);
        var matches = new List<Notification>();
        foreach (var parent in parents)
        {
            var witness = new Notification { Kind = NotificationKind.Info, Text = "", EntityId = parent.EntityId,
                EditKeyboardMessageId = messageId, EditParentNotificationId = parent.Id };
            if (await EditTargetAsync(context, witness, ct) == captured) matches.Add(parent);
        }
        if (matches.Count != 1) return null;
        var original = matches[0];
        var latest = await (from notice in context.Notifications
                            join root in context.NotificationDeliveryRequests on notice.DeliveryRequestId equals root.Id
                            where notice.EditParentNotificationId == original.Id
                            orderby notice.Id descending
                            select root).AsNoTracking().FirstOrDefaultAsync(ct);
        NotificationDeliveryInput? latestInput = null;
        if (latest is not null)
        {
            latestInput = NotificationDeliveryInputs.Input(latest);
            var latestNotice = await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == latest.NotificationId, ct);
            if (!latestInput.Matches(latestNotice) || latestNotice.EditParentNotificationId != original.Id
                || latestInput.Target != captured || latestInput.EditKeyboardMessageId != messageId)
                throw new UnsupportedNotificationDeliveryException();
        }
        if (replaceCallback is not null)
        {
            var currentRows = latestInput?.Buttons ?? original.Buttons;
            var merged = currentRows.Select(row => row.ToList()).ToList();
            var index = merged.FindIndex(row => row.Any(button => button[(button.IndexOf('|') + 1)..] == replaceCallback));
            if (index < 0) return null;
            merged[index] = rows[0];
            rows = merged;
        }
        if (latest is not null && latest.Stage is not (NotificationDeliveryStage.Failed or NotificationDeliveryStage.Cancelled or NotificationDeliveryStage.Reconciled))
        {
            if (JsonSerializer.Serialize(latestInput!.Buttons) == JsonSerializer.Serialize(rows)) return latest.Id;
        }
        var edit = new Notification { Kind = NotificationKind.Info, Text = "", Buttons = rows, EntityId = original.EntityId,
            EditKeyboardMessageId = messageId, EditParentNotificationId = original.Id, CreatedAt = time.GetUtcNow(), Status = NotificationStatus.Pending };
        context.Notifications.Add(edit);
        var ids = await QueueAddedAsync(context, outbox, ct, captured);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return ids.Single();
    }

    private async Task<Guid> PublishAsync(Notification notification, NotificationTarget? target, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        context.Notifications.Add(notification);
        var ids = await QueueAddedAsync(context, outbox, ct, target);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return ids.Single();
    }

    public async Task<Guid> QueueAsync(JobFinderDbContext context, IDbContextOutbox outbox, Notification notice, CancellationToken ct)
    {
        if (context.Entry(notice).State != EntityState.Added)
            throw new InvalidOperationException("native notification admission requires a new tracked notice and its source transaction");
        await QueueAddedAsync(context, outbox, ct);
        return notice.DeliveryRequestId!.Value;
    }

    public async Task<IReadOnlyList<Guid>> QueueAddedAsync(JobFinderDbContext context, IDbContextOutbox outbox, CancellationToken ct,
        NotificationTarget? capturedTarget = null)
    {
        var notices = context.ChangeTracker.Entries<Notification>().Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity).ToArray();
        if (notices.Length == 0) return [];
        if (context.Database.CurrentTransaction is null || !ReferenceEquals(outbox.ActiveContext, context)
            || notices.Any(notice => notice.Status != NotificationStatus.Pending || notice.DeliveryRequestId is not null))
            throw new InvalidOperationException("native notification admission requires new tracked notices and their source transaction");
        var target = capturedTarget ?? transport.CaptureTarget();
        await context.SaveChangesAsync(ct);
        var ids = new List<Guid>(notices.Length);
        foreach (var notice in notices)
        {
            var destination = notice.EditKeyboardMessageId is null ? target
                : await EditTargetAsync(context, notice, ct);
            var missingEditProof = notice.EditKeyboardMessageId is not null && destination is null;
            var request = new NotificationDeliveryRequest
            {
                Id = Guid.NewGuid(), NotificationId = notice.Id,
                Stage = missingEditProof ? NotificationDeliveryStage.Failed
                    : destination is null ? NotificationDeliveryStage.AwaitingTarget : NotificationDeliveryStage.Preparing, CreatedAt = time.GetUtcNow(),
                InputJson = JsonSerializer.Serialize(NotificationDeliveryInput.Capture(notice, destination), NotificationDeliveryJsonContext.Default.NotificationDeliveryInput),
            };
            request.InputHash = CanonicalJson.Hash(request.InputJson);
            request.AdmissionJson = request.InputJson;
            request.AdmissionHash = request.InputHash;
            NotificationDeliveryInputs.Input(request);
            notice.DeliveryRequestId = request.Id;
            context.NotificationDeliveryRequests.Add(request);
            if (missingEditProof)
            {
                request.Error = notice.Error = "notification edit has no verified original account, chat and message";
                request.CompletedAt = time.GetUtcNow();
                notice.Status = NotificationStatus.Failed;
            }
            else if (destination is null) await outbox.PublishAsync(new BindNotificationTarget { RequestId = request.Id });
            else await outbox.PublishAsync(new DeliverNotification { RequestId = request.Id });
            ids.Add(request.Id);
        }
        return ids;
    }

    public async Task SaveJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, CancellationToken ct)
    {
        EnsureJoined(context, outbox);
        await QueueAddedAsync(context, outbox, ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task SaveAndFlushAsync(JobFinderDbContext context, IDbContextOutbox outbox, CancellationToken ct)
    {
        EnsureJoined(context, outbox);
        await QueueAddedAsync(context, outbox, ct);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }

    public async Task SaveAndFlushAsync(JobFinderDbContext context, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var transaction = context.Database.CurrentTransaction is null ? await context.Database.BeginTransactionAsync(ct) : null;
        outbox.Enroll(context);
        await SaveAndFlushAsync(context, outbox, ct);
    }

    internal static Task LockAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('notifications.delivery', 0))", ct);

    private static void EnsureJoined(JobFinderDbContext context, IDbContextOutbox outbox)
    {
        if (context.Database.CurrentTransaction is null || !ReferenceEquals(outbox.ActiveContext, context))
            throw new InvalidOperationException("notification saving requires the enrolled source transaction");
    }

    private static async Task<NotificationTarget?> EditTargetAsync(JobFinderDbContext context, Notification notice, CancellationToken ct)
    {
        if (notice.EditParentNotificationId is not > 0 || notice.EditKeyboardMessageId is not > 0) return null;
        var parent = await context.Notifications.AsNoTracking().SingleOrDefaultAsync(row => row.Id == notice.EditParentNotificationId, ct);
        if (parent?.DeliveryRequestId is not { } id || parent.TelegramMessageId != notice.EditKeyboardMessageId) return null;
        var root = await context.NotificationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.NotificationId == parent.Id, ct);
        if (root is null) return null;
        try
        {
            var input = NotificationDeliveryInputs.Input(root);
            if (input.Target is null || input.EntityId != notice.EntityId || !input.Matches(parent)) return null;
            var receipts = await context.NotificationDeliveryAttempts.AsNoTracking().Where(row => row.RequestId == id && row.EffectIndex == 0
                && row.Status == DeliveryAttemptStatus.Confirmed && row.TelegramMessageId == notice.EditKeyboardMessageId).Take(2).ToListAsync(ct);
            if (receipts.Count != 1) return null;
            var attempt = receipts[0];
            if (attempt.InputHash != root.InputHash || attempt.PlanHash != root.PlanHash
                || CanonicalJson.Hash(attempt.EffectJson) != attempt.EffectHash) return null;
            var effect = JsonSerializer.Deserialize<NotificationEffect>(attempt.EffectJson, NotificationDeliveryJsonContext.Default.NotificationEffect);
            if (effect is null || effect.Kind is not (NotificationEffectKind.Text or NotificationEffectKind.Document)) return null;
            root.EffectIndex = 0;
            root.PlainText = !effect.Html;
            var expected = JsonSerializer.Serialize(NotificationDeliveryInputs.Effect(root), NotificationDeliveryJsonContext.Default.NotificationEffect);
            return CanonicalJson.Hash(expected) == attempt.EffectHash ? input.Target : null;
        }
        catch (Exception error) when (error is UnsupportedNotificationDeliveryException or JsonException or ArgumentException) { return null; }
    }
}
