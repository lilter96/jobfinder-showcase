using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class ChannelMembershipWorkflow(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    IChannelMembershipGateway gateway, ServerOwnership ownership, PostProcessingRequests posts, JobFinderLlm llm,
    SearchPolicyStore searches, ChannelMonitoring monitoring, TelegramOptions options, JobTriggers triggers, Notifier notifier, TimeProvider time)
{
    public async Task RunAsync(ProcessChannelMembership command, Guid envelopeId, CancellationToken ct)
    {
        if (command.Version != 1 || command.RequestId == Guid.Empty || envelopeId == Guid.Empty || !Enum.IsDefined(command.Stage)
            || ChannelMembershipInputs.Terminal(command.Stage)) throw new UnsupportedChannelMembershipException();
        ownership.EnsureOwned();
        ChannelMembershipRequest? request;
        try { request = await ClaimAsync(command, envelopeId, ct); }
        catch (UnsupportedChannelMembershipException)
        {
            await RetireAsync(command.RequestId, "unsupported membership checkpoint; no new Telegram call was authorized", ct);
            throw;
        }
        if (request is null) return;
        using var stage = Telemetry.StartStage(command.Stage.ToString().ToLowerInvariant(), "telegram-membership")
            .Tag("request.id", request.Id).Tag("channel.id", request.ChannelId);
        var input = ChannelMembershipInputs.Input(request);
        try
        {
            ownership.EnsureOwned();
            if (command.Stage == ChannelMembershipStage.Publishing) { await PublishAsync(request, input, ct); return; }
            if (command.Stage == ChannelMembershipStage.Resolving)
            {
                var chat = await gateway.ResolveAsync(input, request.AccountId!.Value, ct);
                if (chat is null) { await FailAsync(request, "не канал или супергруппа, либо адрес не найден", ct); return; }
                request.ChatJson = JsonSerializer.Serialize(chat, ChannelMembershipJsonContext.Default.ChannelMembershipChat);
                request.ChatHash = CanonicalJson.Hash(request.ChatJson);
                ChannelMembershipInputs.Chat(request);
            }
            else if (command.Stage == ChannelMembershipStage.ReadingHistory)
            {
                var history = await gateway.HistoryAsync(ChannelMembershipInputs.Chat(request), request.AccountId!.Value, ct);
                request.HistoryJson = JsonSerializer.Serialize(history, ChannelMembershipJsonContext.Default.ChannelMembershipHistory);
                request.HistoryHash = CanonicalJson.Hash(request.HistoryJson);
                ChannelMembershipInputs.History(request);
            }
            else
            {
                if (!await EffectAllowedAsync(request, input, ct)) return;
                ownership.EnsureOwned();
                await gateway.ApplyAsync(command.Stage, ChannelMembershipInputs.Chat(request), input.Folder, request.AccountId!.Value, token => EffectAllowedAsync(request, input, token), ct);
            }
            for (var retry = 0; ; retry++)
            {
                try { await CheckpointAsync(request, input, ct); return; }
                catch (Exception error) when (retry < 2 && error is DbException or DbUpdateException or IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), time, ct);
                }
            }
        }
        catch (ChannelSessionUnavailableException) { await DeferAsync(request, time.GetUtcNow().AddMinutes(2), "ожидается исходный Telegram-аккаунт", ct); }
        catch (ChannelMembershipRpcException error) when (error.RetryAfter is { })
        {
            await DeferAsync(request, time.GetUtcNow().Add(error.RetryAfter.Value), "Telegram FLOOD_WAIT; отклонённый вызов сохранён", ct, flood: true);
        }
        catch (ChannelMembershipRpcException error)
        {
            await FailAsync(request, error.InviteRequested ? "заявка на вступление отправлена; после одобрения админом нажмите «Одобрить»" : error.Message, ct, error.InviteRequested);
        }
        catch (UnsupportedChannelMembershipException)
        {
            if (request.AttemptId is not null) await UnknownAsync(request.Id, "membership effect cannot be certified from its checkpoint", CancellationToken.None);
            else await RetireAsync(request.Id, "unsupported membership result; no new Telegram call was authorized", ct);
            throw;
        }
        catch (Exception) when (ChannelMembershipInputs.Effect(command.Stage))
        {
            await UnknownAsync(request.Id, "исход Telegram-операции неизвестен; автоматический повтор остановлен", CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await DeferAsync(request, time.GetUtcNow().AddMinutes(2), "чтение Telegram временно недоступно; сохранённые этапы не повторяются", ct);
        }
    }

    private async Task<ChannelMembershipRequest?> ClaimAsync(ProcessChannelMembership command, Guid envelopeId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ChannelMembershipRequests.LockAsync(context, ct);
        ownership.EnsureOwned();
        var root = await context.ChannelMembershipRequests.SingleOrDefaultAsync(row => row.Id == command.RequestId, ct)
            ?? throw new UnsupportedChannelMembershipException();
        if (!Enum.IsDefined(root.Stage)) throw new UnsupportedChannelMembershipException();
        if (root.Stage != command.Stage) return null;
        var input = ChannelMembershipInputs.Input(root);
        var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == root.ChannelId, ct);
        if (root.AttemptId is not null)
        {
            await MarkUnknownAsync(context, root, "прерванная Telegram-операция требует сверки; повтор не разрешён", ct);
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return null;
        }
        if (!Matches(source, root, input))
        {
            root.Stage = ChannelMembershipStage.Cancelled;
            root.CompletedAt = time.GetUtcNow();
            root.ClaimEnvelopeId = null;
            root.Error = "captured channel changed before membership admission";
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return null;
        }
        if (command.Stage != ChannelMembershipStage.Resolving) ChannelMembershipInputs.Chat(root);
        var retained = await context.ChannelMembershipAttempts.Where(row => row.RequestId == root.Id).ToListAsync(ct);
        ChannelMembershipInputs.Progress(root, retained);
        foreach (var attempt in retained) ChannelMembershipInputs.Attempt(root, attempt);
        if (retained.Any(attempt => attempt.Outcome is ChannelMembershipOutcome.Sending or ChannelMembershipOutcome.OutcomeUnknown
            or ChannelMembershipOutcome.Reconciled or ChannelMembershipOutcome.InviteRequested)) throw new UnsupportedChannelMembershipException();
        if (command.Stage == ChannelMembershipStage.Publishing) ChannelMembershipInputs.History(root);
        if (command.Stage != ChannelMembershipStage.Publishing)
        {
            var flood = await context.Settings.SingleOrDefaultAsync(row => row.Key == ChannelMembershipRequests.FloodKey, ct);
            if (flood is not null)
            {
                if (!DateTimeOffset.TryParse(flood.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var pausedUntil))
                    throw new UnsupportedChannelMembershipException();
                if (pausedUntil > time.GetUtcNow())
                {
                    await ContinueAsync(outbox, root, pausedUntil, "сохранённая общая пауза Telegram FLOOD_WAIT");
                    await notifier.SaveAndFlushAsync(context, outbox, ct);
                    return null;
                }
            }
        }
        if (command.Stage != ChannelMembershipStage.Publishing && (gateway.AccountId is not > 0 || root.AccountId is { } captured && captured != gateway.AccountId))
        {
            await ContinueAsync(outbox, root, time.GetUtcNow().AddMinutes(2), "ожидается исходный Telegram-аккаунт");
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return null;
        }
        if (root.RetryAt > time.GetUtcNow()) return null;
        root.AccountId ??= gateway.AccountId;
        if (root.AccountId is not > 0) throw new UnsupportedChannelMembershipException();
        if (command.Stage == ChannelMembershipStage.Joining)
        {
            if (ChannelMembershipInputs.Chat(root).IsMember) throw new UnsupportedChannelMembershipException();
            var cursor = await context.Settings.SingleOrDefaultAsync(row => row.Key == ChannelMembershipRequests.PacingKey, ct);
            var due = DateTimeOffset.MinValue;
            if (cursor is not null && !DateTimeOffset.TryParse(cursor.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out due))
                throw new UnsupportedChannelMembershipException();
            var oldest = await context.ChannelMembershipAttempts.Where(row => row.Stage == ChannelMembershipStage.Joining && row.StartedAt > time.GetUtcNow().AddMinutes(-10))
                .OrderBy(row => row.StartedAt).Select(row => row.StartedAt).ToListAsync(ct);
            if (oldest.Count >= Math.Min(input.JoinsPerTenMinutes, options.MaxJoinsPerRun) && oldest[0].AddMinutes(10) > due) due = oldest[0].AddMinutes(10);
            if (await context.ChannelMembershipAttempts.AnyAsync(row => row.RequestId != root.Id && row.Outcome == ChannelMembershipOutcome.Sending, ct)
                && time.GetUtcNow().AddSeconds(30) > due) due = time.GetUtcNow().AddSeconds(30);
            if (due > time.GetUtcNow())
            {
                await ContinueAsync(outbox, root, due, "сохранённая пауза или лимит подписок");
                await notifier.SaveAndFlushAsync(context, outbox, ct);
                return null;
            }
            var minimum = Math.Max(input.MinSpacingSeconds, options.JoinDelayMinSec);
            root.SpacingSeconds = Random.Shared.Next(minimum, Math.Max(minimum, Math.Max(input.MaxSpacingSeconds, options.JoinDelayMaxSec)) + 1);
            await PaceAsync(context, time.GetUtcNow().AddSeconds(root.SpacingSeconds), ct);
        }
        root.ClaimEnvelopeId = envelopeId;
        root.RetryAt = null;
        root.Error = null;
        if (ChannelMembershipInputs.Effect(command.Stage))
        {
            var chat = ChannelMembershipInputs.Chat(root);
            var attempt = new ChannelMembershipAttempt { Id = Guid.NewGuid(), RequestId = root.Id, EnvelopeId = envelopeId,
                AccountId = root.AccountId.Value, PeerId = chat.Id, InputHash = root.InputHash, ChatHash = root.ChatHash!, Stage = root.Stage,
                Outcome = ChannelMembershipOutcome.Sending, StartedAt = time.GetUtcNow() };
            context.ChannelMembershipAttempts.Add(attempt);
            root.AttemptId = attempt.Id;
        }
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return root;
    }

    private async Task<bool> EffectAllowedAsync(ChannelMembershipRequest saved, ChannelMembershipInput input, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await ChannelMembershipRequests.LockAsync(context, ct);
        ownership.EnsureOwned();
        var root = await context.ChannelMembershipRequests.SingleAsync(row => row.Id == saved.Id, ct);
        ChannelMembershipInputs.Input(root);
        ChannelMembershipInputs.Chat(root);
        ChannelMembershipInputs.Progress(root, await context.ChannelMembershipAttempts.Where(row => row.RequestId == root.Id).ToListAsync(ct));
        var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == root.ChannelId, ct);
        if (root.Stage != saved.Stage || root.InputHash != saved.InputHash || root.ChatHash != saved.ChatHash
            || root.AccountId != saved.AccountId || root.AttemptId != saved.AttemptId || root.ClaimEnvelopeId != saved.ClaimEnvelopeId
            || !Matches(source, root, input)) throw new UnsupportedChannelMembershipException();
        var attempt = await context.ChannelMembershipAttempts.SingleOrDefaultAsync(row => row.Id == root.AttemptId, ct)
            ?? throw new UnsupportedChannelMembershipException();
        ChannelMembershipInputs.Attempt(root, attempt);
        if (attempt.Outcome != ChannelMembershipOutcome.Sending || attempt.Stage != root.Stage || attempt.EnvelopeId != root.ClaimEnvelopeId)
            throw new UnsupportedChannelMembershipException();
        if (gateway.AccountId != root.AccountId) throw new ChannelSessionUnavailableException();
        ct.ThrowIfCancellationRequested();
        ownership.EnsureOwned();
        return true;
    }

    private async Task CheckpointAsync(ChannelMembershipRequest saved, ChannelMembershipInput input, CancellationToken ct)
    {
        await ChangeAsync(saved, async (context, outbox, root, source) =>
        {
            if (saved.Stage == ChannelMembershipStage.Resolving) { root.ChatJson = saved.ChatJson; root.ChatHash = saved.ChatHash; }
            if (saved.Stage == ChannelMembershipStage.ReadingHistory) { root.HistoryJson = saved.HistoryJson; root.HistoryHash = saved.HistoryHash; }
            await ReceiptAsync(context, root, ChannelMembershipOutcome.Confirmed, "Telegram RPC returned successfully", ct);
            if (!Matches(source, root, input))
            {
                root.Stage = ChannelMembershipStage.Cancelled;
                root.CompletedAt = time.GetUtcNow();
                root.Error = "membership receipt retained; changed source was not overwritten";
                root.ClaimEnvelopeId = null;
                root.AttemptId = null;
                return;
            }
            if (saved.Stage == ChannelMembershipStage.Joining) await PaceAsync(context, time.GetUtcNow().AddSeconds(root.SpacingSeconds), ct);
            root.Stage = saved.Stage switch
            {
                ChannelMembershipStage.Resolving => ChannelMembershipInputs.Chat(root).IsMember ? ChannelMembershipStage.Muting : ChannelMembershipStage.Joining,
                ChannelMembershipStage.Joining => ChannelMembershipStage.Muting,
                ChannelMembershipStage.Muting => ChannelMembershipStage.Archiving,
                ChannelMembershipStage.Archiving => ChannelMembershipStage.Filing,
                ChannelMembershipStage.Filing => ChannelMembershipStage.ReadingHistory,
                ChannelMembershipStage.ReadingHistory => ChannelMembershipStage.Publishing,
                _ => throw new UnsupportedChannelMembershipException(),
            };
            root.ClaimEnvelopeId = null;
            root.AttemptId = null;
            root.Error = null;
            await outbox.PublishAsync(new ProcessChannelMembership { RequestId = root.Id, Stage = root.Stage });
        }, ct);
    }

    private async Task PublishAsync(ChannelMembershipRequest saved, ChannelMembershipInput input, CancellationToken ct)
    {
        var operation = await llm.ForOperationAsync(ct);
        var search = await searches.ReadAsync(ct);
        var chat = ChannelMembershipInputs.Chat(saved);
        var history = ChannelMembershipInputs.History(saved);
        var published = false;
        await ChangeAsync(saved, async (context, outbox, root, source) =>
        {
            if (!Matches(source, root, input))
            {
                root.Stage = ChannelMembershipStage.Cancelled;
                root.CompletedAt = time.GetUtcNow();
                root.ClaimEnvelopeId = null;
                root.Error = "captured channel changed before membership publication";
                return;
            }
            ChannelMembershipInputs.Publication(root, await context.ChannelMembershipAttempts.Where(row => row.RequestId == root.Id).ToListAsync(ct));
            source!.PeerId = chat.Id;
            source.AccessHash = chat.AccessHash;
            source.Username = chat.Username ?? source.Username;
            source.Title = chat.Title;
            source.Kind = chat.Kind;
            source.Status = ChannelStatus.Joined;
            source.JoinedAt = time.GetUtcNow();
            source.Error = null;
            root.Stage = ChannelMembershipStage.Completed;
            root.CompletedAt = time.GetUtcNow();
            root.ClaimEnvelopeId = null;
            root.Error = null;
            await notifier.SaveJoinedAsync(context, outbox, ct);
            foreach (var post in history.Posts)
            {
                if (await context.RawPosts.AnyAsync(row => row.Source == post.Source && row.ChatRef == post.ChatRef && row.ExternalId == post.ExternalId, ct)) continue;
                post.Id = 0;
                await posts.StoreJoinedAsync(context, outbox, post, operation, search, ct);
            }
            notifier.Add(context, NotificationKind.Info, "✅ Telegram: подписка и настройка канала @" + TextUtil.EscapeHtml(source.Username ?? chat.Id.ToString(CultureInfo.InvariantCulture))
                + " завершены; история принята в конвейер.");
            published = true;
        }, ct);
        if (published)
        {
            monitoring.Add(chat.Id);
            triggers.Trigger(JobNames.TelegramSync);
        }
    }

    private async Task ChangeAsync(ChannelMembershipRequest saved,
        Func<JobFinderDbContext, IDbContextOutbox, ChannelMembershipRequest, TelegramChannel?, Task> change, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var root = await context.ChannelMembershipRequests.SingleAsync(row => row.Id == saved.Id, ct);
        if (root.Stage != saved.Stage || root.InputHash != saved.InputHash || root.ClaimEnvelopeId != saved.ClaimEnvelopeId || root.AttemptId != saved.AttemptId) return;
        ChannelMembershipInputs.Input(root);
        var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == root.ChannelId, ct);
        await change(context, outbox, root, source);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private Task DeferAsync(ChannelMembershipRequest saved, DateTimeOffset due, string reason, CancellationToken ct, bool flood = false) =>
        ChangeAsync(saved, async (context, outbox, root, _) =>
        {
            await ReceiptAsync(context, root, ChannelMembershipOutcome.KnownNotApplied, reason, ct);
            root.AttemptId = null;
            if (flood)
            {
                var cursor = await context.Settings.SingleOrDefaultAsync(row => row.Key == ChannelMembershipRequests.FloodKey, ct);
                var old = DateTimeOffset.MinValue;
                if (cursor is null) { cursor = new AppSetting { Key = ChannelMembershipRequests.FloodKey, Value = "" }; context.Settings.Add(cursor); }
                else if (!DateTimeOffset.TryParse(cursor.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out old))
                    throw new UnsupportedChannelMembershipException();
                if (old < due)
                    cursor.Value = due.ToString("O", CultureInfo.InvariantCulture);
                cursor.UpdatedAt = time.GetUtcNow();
            }
            await ContinueAsync(outbox, root, due, reason);
        }, ct);

    private Task FailAsync(ChannelMembershipRequest saved, string reason, CancellationToken ct, bool inviteRequested = false) =>
        ChangeAsync(saved, async (context, _, root, source) =>
        {
            await ReceiptAsync(context, root, inviteRequested ? ChannelMembershipOutcome.InviteRequested : ChannelMembershipOutcome.KnownNotApplied, reason, ct);
            root.Stage = ChannelMembershipStage.Failed;
            root.CompletedAt = time.GetUtcNow();
            root.ClaimEnvelopeId = null;
            root.AttemptId = null;
            root.Error = TextUtil.Truncate(reason, 1000);
            if (Matches(source, root, ChannelMembershipInputs.Input(root))) { source!.Status = ChannelStatus.Failed; source.Error = root.Error; }
            notifier.Add(context, NotificationKind.Alert, "⚠️ Telegram-подписка #" + root.ChannelId + ": " + TextUtil.EscapeHtml(root.Error));
        }, ct);

    private async Task ReceiptAsync(JobFinderDbContext context, ChannelMembershipRequest root, ChannelMembershipOutcome outcome, string evidence, CancellationToken ct)
    {
        if (root.AttemptId is not { } id) return;
        var attempt = await context.ChannelMembershipAttempts.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (attempt is not null) ChannelMembershipInputs.Attempt(root, attempt);
        if (attempt is not { Outcome: ChannelMembershipOutcome.Sending } || attempt.RequestId != root.Id || attempt.Stage != root.Stage
            || attempt.EnvelopeId != root.ClaimEnvelopeId || attempt.InputHash != root.InputHash || attempt.ChatHash != root.ChatHash
            || attempt.AccountId != root.AccountId || attempt.PeerId != ChannelMembershipInputs.Chat(root).Id) throw new UnsupportedChannelMembershipException();
        attempt.Outcome = outcome;
        attempt.CompletedAt = time.GetUtcNow();
        attempt.Evidence = TextUtil.Truncate(evidence, 1000);
    }

    private static bool Matches(TelegramChannel? source, ChannelMembershipRequest root, ChannelMembershipInput input) =>
        source is not null && source.MembershipRequestId == root.Id && source.AccessHash == input.AccessHash && input.Source.Matches(source);

    private static async Task ContinueAsync(IDbContextOutbox outbox, ChannelMembershipRequest root, DateTimeOffset due, string reason)
    {
        root.RetryAt = due;
        root.Error = reason;
        root.ClaimEnvelopeId = null;
        await outbox.PublishAsync(new ProcessChannelMembership { RequestId = root.Id, Stage = root.Stage }, new DeliveryOptions { ScheduledTime = due });
    }

    private async Task PaceAsync(JobFinderDbContext context, DateTimeOffset due, CancellationToken ct)
    {
        var cursor = await context.Settings.SingleOrDefaultAsync(row => row.Key == ChannelMembershipRequests.PacingKey, ct);
        if (cursor is null) { cursor = new AppSetting { Key = ChannelMembershipRequests.PacingKey, Value = "" }; context.Settings.Add(cursor); }
        else
        {
            if (!DateTimeOffset.TryParse(cursor.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var saved))
                throw new UnsupportedChannelMembershipException();
            if (saved > due) return;
        }
        cursor.Value = due.ToString("O", CultureInfo.InvariantCulture);
        cursor.UpdatedAt = time.GetUtcNow();
    }

    private async Task UnknownAsync(Guid id, string reason, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var root = await context.ChannelMembershipRequests.SingleAsync(row => row.Id == id, ct);
        if (ChannelMembershipInputs.Terminal(root.Stage)) return;
        await MarkUnknownAsync(context, root, reason, ct);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
    }

    private async Task RetireAsync(Guid id, string reason, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var root = await context.ChannelMembershipRequests.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (root is null || ChannelMembershipInputs.Terminal(root.Stage)) return;
        if (root.AttemptId is not null) await MarkUnknownAsync(context, root, reason, ct);
        else
        {
            root.Stage = ChannelMembershipStage.Failed;
            root.CompletedAt = time.GetUtcNow();
            root.Error = reason;
            root.ClaimEnvelopeId = null;
            var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == root.ChannelId, ct);
            if (source is { Status: ChannelStatus.Approved } && source.MembershipRequestId == root.Id && source.DecisionRevision == root.DecisionRevision)
            {
                source.Status = ChannelStatus.Failed;
                source.Error = reason;
            }
            notifier.Add(context, NotificationKind.Alert, "⚠️ Telegram-подписка " + id + ": " + reason);
        }
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
    }

    private async Task MarkUnknownAsync(JobFinderDbContext context, ChannelMembershipRequest root, string reason, CancellationToken ct)
    {
        if (root.AttemptId is { } attemptId && await context.ChannelMembershipAttempts.SingleOrDefaultAsync(row => row.Id == attemptId, ct)
            is { Outcome: ChannelMembershipOutcome.Sending } attempt && attempt.RequestId == root.Id && attempt.Stage == root.Stage
            && attempt.InputHash == root.InputHash && attempt.ChatHash == root.ChatHash && attempt.AccountId == root.AccountId
            && attempt.EnvelopeId == root.ClaimEnvelopeId)
        {
            attempt.Outcome = ChannelMembershipOutcome.OutcomeUnknown;
            attempt.CompletedAt = time.GetUtcNow();
            attempt.Evidence = reason;
        }
        root.Stage = ChannelMembershipStage.OutcomeUnknown;
        root.Error = reason;
        root.RetryAt = null;
        var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == root.ChannelId && row.MembershipRequestId == root.Id, ct);
        if (source is not null) source.Error = reason;
        notifier.Add(context, NotificationKind.Alert, "⚠️ Telegram-подписка " + root.Id + ": " + TextUtil.EscapeHtml(reason) + ". Нужна проверенная сверка, не повтор подписки.");
    }
}

[NonTransactional]
public static class ChannelMembershipHandler
{
    public static Task Handle(ProcessChannelMembership command, ChannelMembershipWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command, message.Envelope!.Id, ct);
}
