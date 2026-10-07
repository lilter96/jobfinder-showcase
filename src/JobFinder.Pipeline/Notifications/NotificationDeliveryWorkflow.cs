using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Career;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Notifications;

public sealed class NotificationDeliveryWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    INotificationDeliveryTransport transport, NotificationDeliveryRequests requests, ServerOwnership ownership, TimeProvider time, BotEdits edits)
{
    public async Task BindAsync(BindNotificationTarget command, Guid envelopeId, CancellationToken ct)
    {
        ownership.EnsureOwned();
        if (command.RequestId == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedNotificationDeliveryException();
        var request = await ClaimAsync(new DeliverNotification { RequestId = command.RequestId, Stage = NotificationDeliveryStage.AwaitingTarget }, envelopeId, ct);
        if (command.Version != 1)
        {
            if (request is not null)
                await ChangeAsync(request, (_, _, current, notice) =>
                {
                    Stop(current, notice, NotificationDeliveryStage.Failed, "unsupported notification target-binding command version");
                    return Task.CompletedTask;
                }, ct, checkInput: false);
            throw new UnsupportedNotificationDeliveryException();
        }
        if (request is null) return;
        using var span = Telemetry.StartStage("bind-target", "notification-delivery").Tag("request.id", request.Id).Tag("notification.id", request.NotificationId);
        try
        {
            NotificationDeliveryInputs.Input(request);
            await ChangeAsync(request, async (_, outbox, current, _) =>
            {
                var input = NotificationDeliveryInputs.Input(current);
                var target = transport.CaptureTarget();
                if (target is null)
                {
                    current.Error = "notification target is unavailable; admission retained without an effect";
                    await ContinueAsync(outbox, current, time.GetUtcNow().AddMinutes(1));
                    return;
                }
                input.Target = target;
                current.InputJson = JsonSerializer.Serialize(input, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput);
                current.InputHash = CanonicalJson.Hash(current.InputJson);
                current.Stage = NotificationDeliveryStage.Preparing;
                current.Error = null;
                NotificationDeliveryInputs.Input(current);
                await ContinueAsync(outbox, current);
            }, ct);
        }
        catch (Exception error) when (error is UnsupportedNotificationDeliveryException or JsonException or ArgumentException)
        {
            await ChangeAsync(request, (_, _, current, notice) =>
            {
                Stop(current, notice, NotificationDeliveryStage.Failed, "unsupported notification admission checkpoint");
                return Task.CompletedTask;
            }, ct, checkInput: false);
            throw new UnsupportedNotificationDeliveryException();
        }
    }

    public async Task RunAsync(DeliverNotification command, Guid envelopeId, CancellationToken ct)
    {
        ownership.EnsureOwned();
        if (command.RequestId == Guid.Empty || envelopeId == Guid.Empty || command.Version != 1
            || command.Stage is not (NotificationDeliveryStage.Preparing or NotificationDeliveryStage.Ready))
            throw new UnsupportedNotificationDeliveryException();
        var request = await ClaimAsync(command, envelopeId, ct);
        if (request is null) return;
        using var span = Telemetry.StartStage(command.Stage.ToString().ToLowerInvariant(), "notification-delivery")
            .Tag("request.id", request.Id).Tag("notification.id", request.NotificationId).Tag("effect.index", request.EffectIndex);
        NotificationDeliveryRequest? effectClaim = null;
        try
        {
            var input = NotificationDeliveryInputs.Input(request);
            var target = input.Target ?? throw new UnsupportedNotificationDeliveryException();
            if (request.Stage == NotificationDeliveryStage.Preparing)
            {
                var plan = await PrepareAsync(input, ct);
                await ChangeAsync(request, async (_, outbox, current, _) =>
                {
                    current.PlanJson = JsonSerializer.Serialize(plan, NotificationDeliveryJsonContext.Default.NotificationPlan);
                    current.PlanHash = CanonicalJson.Hash(current.PlanJson);
                    current.Stage = NotificationDeliveryStage.Ready;
                    await ContinueAsync(outbox, current);
                }, ct);
                return;
            }
            var effect = NotificationDeliveryInputs.Effect(request);
            byte[]? attachment = null;
            if (effect.Kind == NotificationEffectKind.Document)
            {
                attachment = await ReadFileAsync(effect.AttachmentPath!, ct);
                if (Hash(attachment) != effect.AttachmentHash) throw new UnsupportedNotificationDeliveryException();
            }
            var sending = await BeginAsync(request, effect, ct);
            if (sending is null) return;
            effectClaim = sending;
            NotificationEffectReceipt receipt;
            try { receipt = await transport.SendAsync(target, effect, attachment, token => MaySendAsync(sending, input, token), ct); }
            catch (Exception) { receipt = new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "notification effect interrupted without a reliable receipt" }; }
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await FinishAsync(sending, input, effect, receipt, finish.Token);
        }
        catch (NotificationAttachmentUnavailableException)
        {
            await ChangeAsync(request, async (_, outbox, current, _) =>
            {
                current.Error = "notification attachment is unavailable; no effect started";
                await ContinueAsync(outbox, current, time.GetUtcNow().AddMinutes(10));
            }, ct);
        }
        catch (Exception error) when (error is UnsupportedNotificationDeliveryException or JsonException or ArgumentException)
        {
            if (effectClaim is not null)
            {
                await AmbiguousAsync(effectClaim, "notification checkpoint changed after an effect may have started", ct);
                throw new UnsupportedNotificationDeliveryException();
            }
            await ChangeAsync(request, (_, _, current, notice) =>
            {
                Stop(current, notice, NotificationDeliveryStage.Failed, "unsupported notification input, plan or attachment");
                return Task.CompletedTask;
            }, ct, checkInput: false);
            throw new UnsupportedNotificationDeliveryException();
        }
    }

    private async Task<NotificationDeliveryRequest?> ClaimAsync(DeliverNotification command, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        var current = await context.NotificationDeliveryRequests.SingleOrDefaultAsync(row => row.Id == command.RequestId, ct);
        if (current is null) return null;
        if (current.Stage == NotificationDeliveryStage.Sending && current.ClaimEnvelopeId == envelopeId)
        {
            await UnknownAsync(context, current, "notification handler resumed after a committed sending claim without its receipt", ct);
            await PaceAsync(context, time.GetUtcNow().AddMilliseconds(1100), ct);
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        if (current.EffectIndex != command.EffectIndex || current.Stage != command.Stage || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } claim && claim != envelopeId) return null;
        ownership.EnsureOwned();
        current.ClaimEnvelopeId = envelopeId;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return current;
    }

    private async Task<NotificationPlan> PrepareAsync(NotificationDeliveryInput input, CancellationToken ct)
    {
        if (input.LegacyUncertain || input.Text.Length > 4096 || input.Text.Length == 0 && input.EditKeyboardMessageId is null)
            throw new UnsupportedNotificationDeliveryException();
        var buttons = input.Buttons;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            if (input.Kind == NotificationKind.EditPrompt)
            {
                if (!await edits.MayDeliverQuestionAsync(context, input.Target!, input.Text, ct)) throw new UnsupportedNotificationDeliveryException();
                return new() { Version = 2, Effects = [new() { Kind = NotificationEffectKind.EditPrompt, Text = input.Text, Html = false }] };
            }
            if (input.Kind == NotificationKind.CommentDraft && input.EntityId is { } draftId
                && !await context.CommentDrafts.AnyAsync(row => row.Id == draftId && row.Status == CommentStatus.Pending, ct))
                buttons = [["♻️ Устаревшая карточка|noop:0:0"]];
        }
        if (input.EditKeyboardMessageId is { } messageId)
        {
            if (messageId <= 0 || input.AttachmentPath is not null) throw new UnsupportedNotificationDeliveryException();
            return new() { Effects = [new() { Kind = NotificationEffectKind.EditKeyboard, MessageId = messageId, Buttons = buttons }] };
        }
        var first = new NotificationEffect { Kind = NotificationEffectKind.Text, Text = input.Text, Buttons = buttons };
        if (input.AttachmentPath is null) return new() { Effects = [first] };
        var bytes = await ReadFileAsync(input.AttachmentPath, ct);
        var name = input.AttachmentName ?? Path.GetFileName(StoredPath.Resolve(input.AttachmentPath));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Contains('/') || name.Contains('\\')) throw new UnsupportedNotificationDeliveryException();
        var document = new NotificationEffect
        {
            Kind = NotificationEffectKind.Document, AttachmentPath = input.AttachmentPath, AttachmentName = name, AttachmentHash = Hash(bytes),
        };
        if (input.Text.Length <= 1000)
        {
            document.Text = input.Text;
            document.Buttons = buttons;
            return new() { Effects = [document] };
        }
        return new() { Effects = [first, document] };
    }

    private async Task<NotificationDeliveryRequest?> BeginAsync(NotificationDeliveryRequest before, NotificationEffect effect, CancellationToken ct)
    {
        NotificationDeliveryRequest? sending = null;
        await ChangeAsync(before, async (context, outbox, current, notice) =>
        {
            var now = time.GetUtcNow();
            var input = NotificationDeliveryInputs.Input(current);
            if (effect.Kind == NotificationEffectKind.EditKeyboard)
            {
                var keyboard = await KeyboardFenceAsync(context, current, input, ct);
                if (keyboard.Superseded)
                {
                    Stop(current, notice, NotificationDeliveryStage.Cancelled, "a newer keyboard intent supersedes this edit; no effect started");
                    return;
                }
                if (keyboard.Uncertain)
                {
                    current.Error = "an earlier keyboard effect requires checked reconciliation; no effect started";
                    await ContinueAsync(outbox, current, now.AddMinutes(1));
                    return;
                }
            }
            var pace = await context.Settings.Where(row => row.Key == "notifications.delivery.pace").Select(row => row.Value).SingleOrDefaultAsync(ct);
            var until = DateTimeOffset.TryParse(pace, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : now;
            if (until > now || await context.NotificationDeliveryRequests.AnyAsync(row => row.Id != current.Id && row.Stage == NotificationDeliveryStage.Sending, ct))
            {
                await ContinueAsync(outbox, current, until > now ? until : now.AddSeconds(2));
                return;
            }
            var target = transport.CaptureTarget();
            if (target is null)
            {
                current.Error = "notification transport is disabled or unavailable; no effect started";
                await ContinueAsync(outbox, current, now.AddMinutes(1));
                return;
            }
            if (target != input.Target)
            {
                Stop(current, notice, NotificationDeliveryStage.Failed, "configured notification account or owner changed");
                return;
            }
            if (effect.Kind == NotificationEffectKind.EditPrompt && !await edits.MayDeliverQuestionAsync(context, input.Target!, input.Text, ct))
            {
                Stop(current, notice, NotificationDeliveryStage.Failed, "edit question source changed or is already bound; no effect started");
                return;
            }
            var json = JsonSerializer.Serialize(effect, NotificationDeliveryJsonContext.Default.NotificationEffect);
            var attempt = new NotificationDeliveryAttempt
            {
                Id = Guid.NewGuid(), RequestId = current.Id, EffectIndex = current.EffectIndex,
                Number = 1 + await context.NotificationDeliveryAttempts.Where(row => row.RequestId == current.Id && row.EffectIndex == current.EffectIndex).CountAsync(ct),
                InputHash = current.InputHash, PlanHash = current.PlanHash!, EffectJson = json, EffectHash = CanonicalJson.Hash(json),
                Status = DeliveryAttemptStatus.Submitting, CreatedAt = now,
            };
            context.NotificationDeliveryAttempts.Add(attempt);
            current.Stage = NotificationDeliveryStage.Sending;
            current.ClaimEnvelopeId = before.ClaimEnvelopeId;
            current.AttemptId = attempt.Id;
            notice.Status = NotificationStatus.Sending;
            await PaceAsync(context, now.AddMilliseconds(1100), ct);
            sending = current;
        }, ct);
        return sending;
    }

    private async Task<bool> MaySendAsync(NotificationDeliveryRequest before, NotificationDeliveryInput input, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        var current = await context.NotificationDeliveryRequests.SingleAsync(row => row.Id == before.Id, ct);
        var notice = await NoticeAsync(context, current, ct);
        if (!Same(current, before) || notice.Status != NotificationStatus.Sending || notice.DeliveryRequestId != current.Id
            || !input.Matches(notice) || transport.CaptureTarget() != input.Target) return false;
        if (input.Kind == NotificationKind.EditPrompt && !await edits.MayDeliverQuestionAsync(context, input.Target!, input.Text, ct)) return false;
        if (input.EditKeyboardMessageId is not null)
        {
            var keyboard = await KeyboardFenceAsync(context, current, input, ct);
            if (keyboard.Superseded || keyboard.Uncertain) return false;
        }
        var attempt = await context.NotificationDeliveryAttempts.SingleAsync(row => row.Id == current.AttemptId, ct);
        if (!AttemptMatches(attempt, current)) return false;
        ownership.EnsureOwned();
        return true;
    }

    private static async Task<(bool Superseded, bool Uncertain)> KeyboardFenceAsync(JobFinderDbContext context,
        NotificationDeliveryRequest current, NotificationDeliveryInput input, CancellationToken ct)
    {
        if (input.EditParentNotificationId is not > 0) return (false, false);
        var siblings = from notice in context.Notifications
                       join root in context.NotificationDeliveryRequests on notice.DeliveryRequestId equals root.Id
                       where notice.EditParentNotificationId == input.EditParentNotificationId && root.Id != current.Id
                       select new { NoticeId = notice.Id, Root = root };
        var superseded = await siblings.AnyAsync(row => row.NoticeId > current.NotificationId, ct);
        var uncertain = await siblings.AnyAsync(row => row.Root.Stage != NotificationDeliveryStage.Preparing
            && row.Root.Stage != NotificationDeliveryStage.Ready && row.Root.Stage != NotificationDeliveryStage.AwaitingTarget
            && row.Root.Stage != NotificationDeliveryStage.Completed && row.Root.Stage != NotificationDeliveryStage.Cancelled
            && row.Root.Stage != NotificationDeliveryStage.Failed && row.Root.Stage != NotificationDeliveryStage.Reconciled
            || context.NotificationDeliveryAttempts.Any(attempt => attempt.RequestId == row.Root.Id
                && attempt.Status != DeliveryAttemptStatus.Confirmed && attempt.Status != DeliveryAttemptStatus.KnownNotSent
                && attempt.Status != DeliveryAttemptStatus.Reconciled), ct);
        return (superseded, uncertain);
    }

    private async Task FinishAsync(NotificationDeliveryRequest before, NotificationDeliveryInput input, NotificationEffect effect,
        NotificationEffectReceipt receipt, CancellationToken ct)
    {
        var finished = await ChangeAsync(before, async (context, outbox, current, notice) =>
        {
            if (notice.DeliveryRequestId != current.Id) throw new UnsupportedNotificationDeliveryException();
            var attempt = await context.NotificationDeliveryAttempts.SingleAsync(row => row.Id == current.AttemptId, ct);
            if (!AttemptMatches(attempt, current)) throw new UnsupportedNotificationDeliveryException();
            if (receipt.Status is not (DeliveryAttemptStatus.Confirmed or DeliveryAttemptStatus.KnownNotSent or DeliveryAttemptStatus.OutcomeUnknown)
                || receipt.Status == DeliveryAttemptStatus.Confirmed && (receipt.MessageId is not > 0
                    || effect.Kind is NotificationEffectKind.EditKeyboard or NotificationEffectKind.EditText && receipt.MessageId != effect.MessageId))
                receipt = new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "notification receipt has no reliable message identity" };
            if (!input.Matches(notice) || notice.DeliveryRequestId != current.Id || notice.Status != NotificationStatus.Sending)
                receipt = new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "notification source changed during delivery" };
            if (receipt.Status == DeliveryAttemptStatus.Confirmed && effect.Kind == NotificationEffectKind.EditPrompt)
            {
                try { await edits.ConfirmQuestionAsync(context, input.Target!, input.Text, receipt.MessageId!.Value, ct); }
                catch (BotEditPromptException) { receipt = new() { Status = DeliveryAttemptStatus.OutcomeUnknown, MessageId = receipt.MessageId,
                    Error = "edit question accepted but its saved binding witness conflicts" }; }
            }
            attempt.Status = receipt.Status;
            attempt.TelegramMessageId = receipt.MessageId;
            attempt.CompletedAt = time.GetUtcNow();
            attempt.Error = receipt.Error;
            current.Error = notice.Error = receipt.Error;
            await PaceAsync(context, time.GetUtcNow().AddSeconds(receipt.RetrySeconds is > 0 ? Math.Clamp(receipt.RetrySeconds.Value, 1, 3600) : 1.1), ct);
            if (receipt.Status == DeliveryAttemptStatus.Confirmed)
            {
                if (current.EffectIndex == 0) notice.TelegramMessageId = receipt.MessageId;
                await RetireLateCardAsync(context, notice, input.Target!, notice.TelegramMessageId!.Value, ct);
                current.EffectIndex++;
                current.PlainText = false;
                if (current.EffectIndex == JsonSerializer.Deserialize(current.PlanJson!, NotificationDeliveryJsonContext.Default.NotificationPlan)!.Effects.Count)
                {
                    current.Stage = NotificationDeliveryStage.Completed;
                    current.CompletedAt = notice.SentAt = time.GetUtcNow();
                    notice.Status = NotificationStatus.Sent;
                }
                else
                {
                    current.Stage = NotificationDeliveryStage.Ready;
                    notice.Status = NotificationStatus.Pending;
                    await ContinueAsync(outbox, current, time.GetUtcNow().AddMilliseconds(1100));
                }
            }
            else if (receipt.Status == DeliveryAttemptStatus.OutcomeUnknown) Stop(current, notice, NotificationDeliveryStage.OutcomeUnknown, receipt.Error!);
            else
            {
                notice.Attempts++;
                current.Stage = NotificationDeliveryStage.Ready;
                notice.Status = NotificationStatus.Pending;
                if (receipt.ParseRejected && effect.Html && effect.Kind is NotificationEffectKind.Text or NotificationEffectKind.Document or NotificationEffectKind.EditText)
                {
                    current.PlainText = true;
                    await ContinueAsync(outbox, current, time.GetUtcNow().AddMilliseconds(1100));
                }
                else if (receipt.RetrySeconds is > 0 && attempt.Number < 5)
                    await ContinueAsync(outbox, current, time.GetUtcNow().AddSeconds(Math.Clamp(receipt.RetrySeconds.Value, 1, 3600)));
                else Stop(current, notice, NotificationDeliveryStage.Failed, receipt.Error ?? "notification effect explicitly rejected");
            }
        }, ct, checkInput: false);
        if (!finished) await AmbiguousAsync(before, "notification receipt witnesses changed after an effect may have started", ct);
    }

    private async Task AmbiguousAsync(NotificationDeliveryRequest before, string error, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        var current = await context.NotificationDeliveryRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (current.Stage != NotificationDeliveryStage.Sending) return;
        await UnknownAsync(context, current, error, ct);
        await PaceAsync(context, time.GetUtcNow().AddMilliseconds(1100), ct);
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task UnknownAsync(JobFinderDbContext context, NotificationDeliveryRequest current, string error, CancellationToken ct)
    {
        var attempt = await context.NotificationDeliveryAttempts.SingleOrDefaultAsync(row => row.Id == current.AttemptId, ct);
        var valid = false;
        try { valid = attempt is not null && AttemptMatches(attempt, current); }
        catch (Exception exception) when (exception is UnsupportedNotificationDeliveryException or JsonException or ArgumentException) { }
        if (valid)
        {
            attempt!.Status = DeliveryAttemptStatus.OutcomeUnknown;
            attempt.Error = error;
            attempt.CompletedAt = time.GetUtcNow();
        }
        else error = "notification sending witnesses are damaged; no retry: " + error;
        var notice = await context.Notifications.SingleOrDefaultAsync(row => row.Id == current.NotificationId, ct);
        Stop(current, notice, NotificationDeliveryStage.OutcomeUnknown, error);
    }

    private async Task<bool> ChangeAsync(NotificationDeliveryRequest before,
        Func<JobFinderDbContext, IDbContextOutbox, NotificationDeliveryRequest, Notification, Task> change, CancellationToken ct, bool checkInput = true)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        var current = await context.NotificationDeliveryRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (!Same(current, before)) return false;
        var notice = await context.Notifications.SingleOrDefaultAsync(row => row.Id == current.NotificationId, ct);
        outbox.Enroll(context);
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        if (notice is null)
        {
            if (current.Stage == NotificationDeliveryStage.Sending) throw new UnsupportedNotificationDeliveryException();
            Stop(current, null, NotificationDeliveryStage.Failed, "notification source is missing before delivery");
        }
        else if (checkInput && (notice.DeliveryRequestId != current.Id || notice.Status != NotificationStatus.Pending || !NotificationDeliveryInputs.Input(current).Matches(notice)))
            Stop(current, notice, NotificationDeliveryStage.Cancelled, "notification source changed before delivery");
        else await change(context, outbox, current, notice);
        ownership.EnsureOwned();
        if (context.ChangeTracker.Entries<Notification>().Any(entry => entry.State == EntityState.Added))
            await requests.QueueAddedAsync(context, outbox, ct, NotificationDeliveryInputs.Input(current).Target);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return true;
    }

    private async Task RetireLateCardAsync(JobFinderDbContext context, Notification notice, NotificationTarget target, int messageId, CancellationToken ct)
    {
        if (notice.Kind != NotificationKind.CommentDraft || notice.EntityId is not { } draftId
            || await context.CommentDrafts.AnyAsync(row => row.Id == draftId && row.Status == CommentStatus.Pending, ct)) return;
        var existing = await (from notification in context.Notifications
                              join root in context.NotificationDeliveryRequests on notification.DeliveryRequestId equals root.Id
                              where notification.EditKeyboardMessageId == messageId && notification.Status != NotificationStatus.Failed
                              select root).ToListAsync(ct);
        if (existing.Any(root => NotificationDeliveryInputs.Input(root).Target == target)) return;
        context.Notifications.Add(new Notification
        {
            Kind = NotificationKind.Info, Text = "Устаревшая карточка комментария", EntityId = draftId, Status = NotificationStatus.Pending,
            CreatedAt = time.GetUtcNow(), EditKeyboardMessageId = messageId, EditParentNotificationId = notice.Id,
            Buttons = [["♻️ Устаревшая карточка|noop:0:0"]],
        });
    }

    private static bool Same(NotificationDeliveryRequest current, NotificationDeliveryRequest before) => current.Stage == before.Stage
        && current.ClaimEnvelopeId == before.ClaimEnvelopeId && current.EffectIndex == before.EffectIndex && current.AttemptId == before.AttemptId
        && current.InputHash == before.InputHash && current.AdmissionHash == before.AdmissionHash
        && current.PlanHash == before.PlanHash && current.PlainText == before.PlainText;

    private static bool AttemptMatches(NotificationDeliveryAttempt attempt, NotificationDeliveryRequest request) => attempt.Status == DeliveryAttemptStatus.Submitting
        && attempt.RequestId == request.Id && attempt.Id == request.AttemptId && attempt.EffectIndex == request.EffectIndex
        && attempt.InputHash == request.InputHash && attempt.PlanHash == request.PlanHash && CanonicalJson.Hash(attempt.EffectJson) == attempt.EffectHash
        && attempt.EffectHash == CanonicalJson.Hash(JsonSerializer.Serialize(NotificationDeliveryInputs.Effect(request), NotificationDeliveryJsonContext.Default.NotificationEffect));

    private static Task<Notification> NoticeAsync(JobFinderDbContext context, NotificationDeliveryRequest request, CancellationToken ct) =>
        context.Notifications.SingleAsync(row => row.Id == request.NotificationId, ct);

    private static async Task LockAsync(JobFinderDbContext context, CancellationToken ct)
    {
        await CommentSources.LockAsync(context, ct);
        await NotificationDeliveryRequests.LockAsync(context, ct);
    }

    private static async Task ContinueAsync(IDbContextOutbox outbox, NotificationDeliveryRequest request, DateTimeOffset? retryAt = null)
    {
        request.RetryAt = retryAt;
        if (request.Stage == NotificationDeliveryStage.AwaitingTarget)
        {
            var binding = new BindNotificationTarget { RequestId = request.Id };
            if (retryAt is { } waiting) await outbox.ScheduleAsync(binding, waiting);
            else await outbox.PublishAsync(binding);
            return;
        }
        var command = new DeliverNotification { RequestId = request.Id, Stage = request.Stage, EffectIndex = request.EffectIndex };
        if (retryAt is { } at) await outbox.ScheduleAsync(command, at);
        else await outbox.PublishAsync(command);
    }

    private void Stop(NotificationDeliveryRequest request, Notification? notice, NotificationDeliveryStage stage, string error)
    {
        request.Stage = stage;
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        request.CompletedAt = time.GetUtcNow();
        request.Error = error;
        if (notice?.DeliveryRequestId == request.Id)
        {
            notice.Status = stage == NotificationDeliveryStage.OutcomeUnknown ? NotificationStatus.OutcomeUnknown : NotificationStatus.Failed;
            notice.Error = error;
        }
    }

    private static Task PaceAsync(JobFinderDbContext context, DateTimeOffset until, CancellationToken ct)
    {
        var value = until.ToString("O", CultureInfo.InvariantCulture);
        return context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key, value, updated_at) VALUES ('notifications.delivery.pace', {value}, now()) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at", ct);
    }

    private static async Task<byte[]> ReadFileAsync(string stored, CancellationToken ct)
    {
        try
        {
            var path = StoredPath.Resolve(stored);
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("notification attachment is missing");
            if (file.Length is <= 0 or > 64 * 1024 * 1024) throw new UnsupportedNotificationDeliveryException();
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new NotificationAttachmentUnavailableException(error);
        }
    }

    private sealed class NotificationAttachmentUnavailableException(Exception inner) : Exception("notification attachment is unavailable", inner);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

[NonTransactional]
public static class NotificationDeliveryHandler
{
    public static Task Handle(BindNotificationTarget command, NotificationDeliveryWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.BindAsync(command, message.Envelope?.Id ?? throw new UnsupportedNotificationDeliveryException(), ct);

    public static Task Handle(DeliverNotification command, NotificationDeliveryWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command, message.Envelope?.Id ?? throw new UnsupportedNotificationDeliveryException(), ct);
}
