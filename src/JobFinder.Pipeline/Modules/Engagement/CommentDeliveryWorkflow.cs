using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class CommentDeliveryWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    ICommentDeliveryTransport transport, SearchPolicyStore searches, ServerOwnership ownership, RuntimeSettings settings, EngagementPolicyStore policies,
    Notifier notifier, LocalClock clock, TimeProvider time)
{
    public const string PacingKey = "engagement.publish.notBefore";

    public async Task RunAsync(Guid id, int version, Guid envelopeId, bool dispatch, CancellationToken ct)
    {
        if (id == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedCommentDeliveryException();
        ownership.EnsureOwned();
        var request = await ClaimAsync(id, envelopeId, dispatch, ct);
        if (request is null) return;
        using var span = Telemetry.StartStage(dispatch ? "publish" : "prepare", "comment-delivery").Tag("request.id", id).Tag("draft.id", request.DraftId);
        try
        {
            if (version != 1) throw new UnsupportedCommentDeliveryException();
            var input = CommentDeliveryInputs.Read(request);
            if (!dispatch)
            {
                if (!await BeforePreparationAsync(request, input, ct)) return;
                var saved = Prepared(request, ready: false);
                ThreadsPreparedPublication prepared;
                try { prepared = await transport.PrepareAsync(input, saved, ct); }
                catch (Exception error) when (error is not (UnsupportedCommentDeliveryException or OperationCanceledException))
                {
                    prepared = (saved ?? new()) with { State = ThreadsPreparationState.Waiting, RetrySeconds = 60, Error = "Threads preparation unavailable; publication has not started" };
                }
                await ChangeAsync(request, async (context, outbox, current) =>
                {
                    if (!await ControlsAsync(context, outbox, current, input, false, ct)) return;
                    SavePrepared(current, prepared);
                    current.Error = prepared.Error;
                    if (prepared.State == ThreadsPreparationState.Unknown) Unknown(context, current, await DraftAsync(context, current, ct), "preparation found an unowned publication");
                    else if (prepared.State == ThreadsPreparationState.NotSent) Stop(context, current, await DraftAsync(context, current, ct), prepared.Error ?? "Threads preparation refused publication");
                    else if (prepared.State == ThreadsPreparationState.Ready)
                    {
                        Prepared(current, ready: true);
                        current.Stage = CommentDeliveryStage.Ready;
                        await outbox.PublishAsync(new DispatchCommentDelivery { RequestId = current.Id });
                    }
                    else await ContinueAsync(outbox, current, time.GetUtcNow().AddSeconds(Math.Clamp(prepared.RetrySeconds, 1, 3600)), false);
                }, ct);
                return;
            }
            var packet = Prepared(request, ready: true)!;
            var submitting = await BeginSendAsync(request, input, ct);
            if (submitting is null) return;
            ThreadsPublicationReceipt receipt;
            try { receipt = await transport.PublishAsync(input, packet, token => MayPublishAsync(submitting, input, token), ct); }
            catch (Exception) { receipt = new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "publication interrupted without a reliable receipt" }; }
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await FinishAsync(submitting, input, receipt, finish.Token);
        }
        catch (Exception error) when (error is UnsupportedCommentDeliveryException or JsonException)
        {
            await QuarantineAsync(request, ct);
            throw new UnsupportedCommentDeliveryException();
        }
    }

    private async Task<CommentDeliveryRequest?> ClaimAsync(Guid id, Guid envelopeId, bool dispatch, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await CommentSources.LockAsync(context, ct);
        var request = await context.CommentDeliveryRequests.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (request is null) return null;
        if (request.Stage == CommentDeliveryStage.Sending)
        {
            if (request.ClaimEnvelopeId == envelopeId)
            {
                Unknown(context, request, await DraftAsync(context, request, ct), "publication handler resumed without its receipt");
                await CompleteAttemptAsync(context, request, DeliveryAttemptStatus.OutcomeUnknown, request.Error, ct);
                await PaceAsync(context, time.GetUtcNow().AddSeconds(180), ct);
                await notifier.SaveAndFlushAsync(context, ct);
            }
            return null;
        }
        if (request.Stage != (dispatch ? CommentDeliveryStage.Ready : CommentDeliveryStage.Preparing)
            || request.RetryAt > time.GetUtcNow() || request.ClaimEnvelopeId is { } claim && claim != envelopeId) return null;
        ownership.EnsureOwned();
        request.ClaimEnvelopeId = envelopeId;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return request;
    }

    private async Task<bool> BeforePreparationAsync(CommentDeliveryRequest request, CommentDeliveryInput input, CancellationToken ct)
    {
        var permitted = false;
        await ChangeAsync(request, async (context, outbox, current) =>
        {
            permitted = await ControlsAsync(context, outbox, current, input, false, ct);
            if (permitted) current.ClaimEnvelopeId = request.ClaimEnvelopeId;
        }, ct);
        return permitted;
    }

    private async Task<CommentDeliveryRequest?> BeginSendAsync(CommentDeliveryRequest request, CommentDeliveryInput input, CancellationToken ct)
    {
        CommentDeliveryRequest? submitting = null;
        await ChangeAsync(request, async (context, outbox, current) =>
        {
            if (!await ControlsAsync(context, outbox, current, input, true, ct)) return;
            var cursor = await context.Settings.SingleOrDefaultAsync(row => row.Key == PacingKey, ct);
            var next = cursor is not null && DateTimeOffset.TryParse(cursor.Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var saved) ? saved : DateTimeOffset.MinValue;
            if (next > time.GetUtcNow() || await context.CommentDeliveryRequests.AnyAsync(row => row.Id != current.Id && row.Stage == CommentDeliveryStage.Sending, ct))
            {
                await ContinueAsync(outbox, current, next > time.GetUtcNow() ? next : time.GetUtcNow().AddSeconds(30), true);
                return;
            }
            var draft = (await DraftAsync(context, current, ct))!;
            var attempt = new CommentDeliveryAttempt
            {
                Id = Guid.NewGuid(), RequestId = current.Id, DraftId = draft.Id, Revision = draft.Revision,
                Target = input.Source.PostUrl, Text = input.Source.Text, TextHash = CanonicalJson.Hash(JsonSerializer.Serialize(input.Source.Text)),
                InputHash = current.InputHash, PreparedHash = current.PreparedHash!, Status = DeliveryAttemptStatus.Submitting, CreatedAt = time.GetUtcNow(),
            };
            context.CommentDeliveryAttempts.Add(attempt);
            draft.Status = CommentStatus.Sending;
            draft.Attempts++;
            current.Stage = CommentDeliveryStage.Sending;
            current.ClaimEnvelopeId = request.ClaimEnvelopeId;
            current.AttemptId = attempt.Id;
            await PaceAsync(context, time.GetUtcNow().AddSeconds(input.SpacingSeconds), ct);
            submitting = current;
        }, ct);
        return submitting;
    }

    private async Task<bool> MayPublishAsync(CommentDeliveryRequest request, CommentDeliveryInput input, CancellationToken ct)
    {
        if (!ownership.IsOwned || ct.IsCancellationRequested) return false;
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await CommentSources.LockAsync(context, ct);
        var current = await context.CommentDeliveryRequests.SingleAsync(row => row.Id == request.Id, ct);
        var draft = await DraftAsync(context, current, ct);
        if (current.Stage != CommentDeliveryStage.Sending || current.AttemptId != request.AttemptId || current.InputHash != request.InputHash
            || current.ClaimEnvelopeId != request.ClaimEnvelopeId || current.PreparedHash != request.PreparedHash
            || draft is null || !CommentDeliveryInputs.Matches(draft, current, input, CommentStatus.Sending)) return false;
        CommentDeliveryInputs.Read(current);
        Prepared(current, ready: true);
        var stop = await CurrentStopAsync(context, input, ct);
        if (await ExpiredAsync(draft, input, ct)) return false;
        var attempt = await context.CommentDeliveryAttempts.SingleOrDefaultAsync(row => row.Id == current.AttemptId, ct);
        if (attempt is not { Status: DeliveryAttemptStatus.Submitting } || !AttemptMatches(attempt, current, input)) return false;
        var used = await UsedTodayAsync(context, current, ct);
        var cap = Math.Min(input.Policy.MaxDraftsPerDay, (await policies.ReadAsync(ct)).Value.MaxDraftsPerDay);
        return stop is null && used < cap && !await settings.GetBoolAsync(RuntimeSettings.CommentsPaused, false, ct) && ownership.IsOwned;
    }

    private async Task<bool> ControlsAsync(JobFinderDbContext context, IDbContextOutbox outbox, CommentDeliveryRequest request,
        CommentDeliveryInput input, bool dispatch, CancellationToken ct)
    {
        var draft = await DraftAsync(context, request, ct);
        if (draft is null || !CommentDeliveryInputs.Matches(draft, request, input, CommentStatus.Approved))
        {
            Stop(context, request, null, "approved source changed or was cancelled");
            return false;
        }
        if (await ExpiredAsync(draft, input, ct))
        {
            request.Stage = CommentDeliveryStage.Cancelled;
            request.CompletedAt = time.GetUtcNow();
            request.Error = draft.Error = "пост устарел до публикации";
            draft.Status = CommentStatus.Expired;
            draft.Revision++;
            notifier.Add(context, NotificationKind.Info, "Комментарий #c" + draft.Id + " не опубликован: пост устарел.", entityId: draft.Id);
            return false;
        }
        var stop = await CurrentStopAsync(context, input, ct);
        if (stop is not null)
        {
            request.Stage = CommentDeliveryStage.Cancelled;
            request.CompletedAt = time.GetUtcNow();
            request.Error = stop;
            draft.Status = CommentStatus.Pending;
            draft.Revision++;
            draft.DeliveryRequestId = null;
            draft.ChosenText = null;
            draft.Error = stop;
            var card = Cards.CommentDraft(draft);
            notifier.Add(context, NotificationKind.CommentDraft, card.Text, card.Buttons, draft.Id);
            return false;
        }
        if (await settings.GetBoolAsync(RuntimeSettings.CommentsPaused, false, ct))
        {
            await ContinueAsync(outbox, request, time.GetUtcNow().AddMinutes(1), dispatch);
            return false;
        }
        var current = (await policies.ReadAsync(ct)).Value;
        var used = await UsedTodayAsync(context, request, ct);
        if (used >= Math.Min(input.Policy.MaxDraftsPerDay, current.MaxDraftsPerDay))
        {
            await ContinueAsync(outbox, request, current.MaxDraftsPerDay == 0 ? time.GetUtcNow().AddMinutes(1) : clock.StartOfTodayUtc().AddDays(1), dispatch);
            return false;
        }
        return true;
    }

    private async Task<string?> CurrentStopAsync(JobFinderDbContext context, CommentDeliveryInput input, CancellationToken ct)
    {
        await context.Database.ExecuteSqlRawAsync("SELECT 1 FROM app_documents WHERE key IN ('candidate.profile', 'policy.automation', 'policy.engagement', 'policy.search') FOR SHARE", ct);
        var profile = await context.Documents.SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        if (profile is null || profile.Version != input.ProfileVersion
            || CanonicalJson.Hash(ProfileProvider.ToJson(ProfileProvider.FromJson(profile.Content))) != input.ProfileHash) return "профиль изменился: подготовьте новые варианты";
        return input.Automatic && !(await policies.ReadAsync(ct)).Value.AutoPublish ? "автоматическая публикация отключена: выберите вариант вручную" : null;
    }

    private async Task FinishAsync(CommentDeliveryRequest before, CommentDeliveryInput input, ThreadsPublicationReceipt receipt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await CommentSources.LockAsync(context, ct);
        var request = await context.CommentDeliveryRequests.SingleAsync(row => row.Id == before.Id, ct);
        var draft = await DraftAsync(context, request, ct);
        if (request.Stage != CommentDeliveryStage.Sending || request.AttemptId != before.AttemptId || request.ClaimEnvelopeId != before.ClaimEnvelopeId
            || request.InputHash != before.InputHash || request.PreparedHash != before.PreparedHash) return;
        if (draft is null || !CommentDeliveryInputs.Matches(draft, request, input, CommentStatus.Sending)) receipt = new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "source changed during publication" };
        if (receipt.Status == DeliveryAttemptStatus.Confirmed && !ValidProof(input, Prepared(request, true)!, receipt.ExternalId))
            receipt = new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "publication receipt has no valid identity proof" };
        if (receipt.Status is not (DeliveryAttemptStatus.Confirmed or DeliveryAttemptStatus.KnownNotSent or DeliveryAttemptStatus.OutcomeUnknown))
            receipt = new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "unsupported publication receipt" };
        var attempt = await context.CommentDeliveryAttempts.SingleAsync(row => row.Id == request.AttemptId, ct);
        if (attempt.Status != DeliveryAttemptStatus.Submitting || !AttemptMatches(attempt, request, input))
            throw new UnsupportedCommentDeliveryException();
        attempt.Status = receipt.Status;
        attempt.ExternalId = receipt.ExternalId;
        attempt.Error = receipt.Error;
        attempt.CompletedAt = time.GetUtcNow();
        request.ClaimEnvelopeId = null;
        request.Error = receipt.Error;
        if (receipt.Status == DeliveryAttemptStatus.Confirmed)
        {
            request.Stage = CommentDeliveryStage.Completed;
            request.CompletedAt = time.GetUtcNow();
            draft!.Status = CommentStatus.Posted;
            draft.PublishedAt = time.GetUtcNow();
            draft.Error = null;
            notifier.Add(context, NotificationKind.Info, Cards.CommentPosted(draft), entityId: draft.Id);
        }
        else if (receipt.Status == DeliveryAttemptStatus.OutcomeUnknown) Unknown(context, request, draft, receipt.Error ?? "unknown publication outcome");
        else if (receipt.Retryable && draft is not null)
        {
            request.Stage = CommentDeliveryStage.Ready;
            draft.Status = CommentStatus.Approved;
            await ContinueAsync(outbox, request, time.GetUtcNow().AddMinutes(1), true);
        }
        else Stop(context, request, draft, receipt.Error ?? "publication was not attempted");
        await PaceAsync(context, time.GetUtcNow().AddSeconds(input.SpacingSeconds), ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static bool ValidProof(CommentDeliveryInput input, ThreadsPreparedPublication prepared, string? proof) => input.Route.Mode == ThreadsPublicationMode.Api
        ? proof is { Length: > 0 and <= 40 } && proof.All(char.IsAsciiDigit)
        : proof is not null && ThreadsBrowserClient.PostUrl(proof) && new Uri(proof).AbsolutePath.StartsWith("/@" + prepared.Username + "/post/", StringComparison.OrdinalIgnoreCase)
            && new Uri(proof).AbsolutePath != new Uri(input.Source.PostUrl).AbsolutePath;

    private async Task ChangeAsync(CommentDeliveryRequest before, Func<JobFinderDbContext, IDbContextOutbox, CommentDeliveryRequest, Task> change, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await CommentSources.LockAsync(context, ct);
        ownership.EnsureOwned();
        var current = await context.CommentDeliveryRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (current.Stage != before.Stage || current.ClaimEnvelopeId != before.ClaimEnvelopeId) return;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        await change(context, outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task QuarantineAsync(CommentDeliveryRequest before, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await CommentSources.LockAsync(context, ct);
        var current = await context.CommentDeliveryRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (current.Stage is not (CommentDeliveryStage.Preparing or CommentDeliveryStage.Ready or CommentDeliveryStage.Sending)
            || current.ClaimEnvelopeId is { } claim && claim != before.ClaimEnvelopeId) return;
        var draft = await DraftAsync(context, current, ct);
        if (current.Stage == CommentDeliveryStage.Sending)
        {
            Unknown(context, current, draft, "unsupported checkpoint after dispatch claim; publication may have occurred");
            await CompleteAttemptAsync(context, current, DeliveryAttemptStatus.OutcomeUnknown, current.Error, ct);
            await PaceAsync(context, time.GetUtcNow().AddSeconds(180), ct);
        }
        else Stop(context, current, draft, "unsupported comment delivery checkpoint; publication refused");
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private void Stop(JobFinderDbContext context, CommentDeliveryRequest request, CommentDraft? draft, string reason)
    {
        request.Stage = CommentDeliveryStage.Failed;
        request.CompletedAt = time.GetUtcNow();
        request.Error = reason;
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        if (draft is { Status: CommentStatus.Approved or CommentStatus.Sending } && draft.DeliveryRequestId == request.Id && draft.Revision == request.Revision)
        {
            draft.Status = CommentStatus.Failed;
            draft.Error = reason;
        }
        notifier.Add(context, NotificationKind.Alert, "⚠️ Комментарий #c" + request.DraftId + " не опубликован: " + TextUtil.EscapeHtml(reason) + " · запрос " + request.Id);
    }

    private void Unknown(JobFinderDbContext context, CommentDeliveryRequest request, CommentDraft? draft, string reason)
    {
        request.Stage = CommentDeliveryStage.OutcomeUnknown;
        request.CompletedAt = time.GetUtcNow();
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        request.Error = reason;
        if (draft is not null && draft.DeliveryRequestId == request.Id && draft.Revision == request.Revision)
        {
            draft.Status = CommentStatus.OutcomeUnknown;
            draft.Error = reason;
        }
        notifier.Add(context, NotificationKind.Alert, "⚠️ Неизвестный исход комментария #c" + request.DraftId + ": повтор остановлен. Проверьте Threads и выполните --comment-reconcile " + request.Id);
    }

    internal static ThreadsPreparedPublication? Prepared(CommentDeliveryRequest request, bool ready)
    {
        if (request.PreparedJson is null)
        {
            if (ready) throw new UnsupportedCommentDeliveryException();
            return null;
        }
        if (CanonicalJson.Hash(request.PreparedJson) != request.PreparedHash) throw new UnsupportedCommentDeliveryException();
        var result = JsonSerializer.Deserialize(request.PreparedJson, CommentDeliveryJsonContext.Default.ThreadsPreparedPublication) ?? throw new UnsupportedCommentDeliveryException();
        if (!Enum.IsDefined(result.State) || ready && (result.State != ThreadsPreparationState.Ready || string.IsNullOrWhiteSpace(result.Username)
            || string.IsNullOrWhiteSpace(result.AccountId))) throw new UnsupportedCommentDeliveryException();
        return result;
    }

    private async Task<int> UsedTodayAsync(JobFinderDbContext context, CommentDeliveryRequest request, CancellationToken ct)
    {
        var since = clock.StartOfTodayUtc();
        var attempts = context.CommentDeliveryAttempts.Where(row => row.CreatedAt >= since && row.Id != request.AttemptId
            && (row.Status == DeliveryAttemptStatus.Submitting || row.Status == DeliveryAttemptStatus.Confirmed || row.Status == DeliveryAttemptStatus.OutcomeUnknown
                || row.Status == DeliveryAttemptStatus.Reconciled && row.ReconciledSent != false));
        var legacy = context.CommentDrafts.Where(draft => draft.Status == CommentStatus.Posted
            && (draft.PublishedAt ?? draft.DecidedAt ?? draft.CreatedAt) >= since
            && !attempts.Any(attempt => attempt.DraftId == draft.Id));
        return await attempts.CountAsync(ct) + await legacy.CountAsync(ct);
    }

    private async Task<bool> ExpiredAsync(CommentDraft draft, CommentDeliveryInput input, CancellationToken ct) => input.ExpiresAt <= time.GetUtcNow()
        || (draft.PostPublishedAt ?? draft.CreatedAt) < time.GetUtcNow().AddHours(-Math.Min(input.MaxPostAgeHours, (await searches.ReadAsync(ct)).Value.Threads.MaxPostAgeHours));

    internal static bool AttemptMatches(CommentDeliveryAttempt attempt, CommentDeliveryRequest request, CommentDeliveryInput input) =>
        attempt.RequestId == request.Id && attempt.DraftId == request.DraftId && attempt.Revision == request.Revision
        && attempt.InputHash == request.InputHash && attempt.PreparedHash == (request.PreparedHash ?? "")
        && attempt.Text == input.Source.Text && attempt.Target == input.Source.PostUrl
        && attempt.TextHash == CanonicalJson.Hash(JsonSerializer.Serialize(input.Source.Text));

    private static void SavePrepared(CommentDeliveryRequest request, ThreadsPreparedPublication prepared)
    {
        request.PreparedJson = JsonSerializer.Serialize(prepared, CommentDeliveryJsonContext.Default.ThreadsPreparedPublication);
        request.PreparedHash = CanonicalJson.Hash(request.PreparedJson);
    }

    private static Task<CommentDraft?> DraftAsync(JobFinderDbContext context, CommentDeliveryRequest request, CancellationToken ct) =>
        context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == request.DraftId, ct);

    private Task PaceAsync(JobFinderDbContext context, DateTimeOffset next, CancellationToken ct) => context.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO settings (key, value, updated_at) VALUES ({PacingKey}, {next.ToString("O")}, {time.GetUtcNow()})
        ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
        """, ct);

    private async Task CompleteAttemptAsync(JobFinderDbContext context, CommentDeliveryRequest request, DeliveryAttemptStatus status, string? error, CancellationToken ct)
    {
        if (request.AttemptId is not { } id) return;
        var attempt = await context.CommentDeliveryAttempts.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (attempt is not { Status: DeliveryAttemptStatus.Submitting }) return;
        attempt.Status = status;
        attempt.Error = error;
        attempt.CompletedAt = time.GetUtcNow();
    }

    private static Task ContinueAsync(IDbContextOutbox outbox, CommentDeliveryRequest request, DateTimeOffset at, bool dispatch)
    {
        request.RetryAt = at;
        request.ClaimEnvelopeId = null;
        return dispatch ? outbox.PublishAsync(new DispatchCommentDelivery { RequestId = request.Id }, new DeliveryOptions { ScheduledTime = at }).AsTask()
            : outbox.PublishAsync(new PrepareCommentDelivery { RequestId = request.Id }, new DeliveryOptions { ScheduledTime = at }).AsTask();
    }
}

[NonTransactional]
public static class CommentDeliveryHandler
{
    public static Task Handle(PrepareCommentDelivery command, CommentDeliveryWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Version, message.Envelope?.Id ?? Guid.Empty, false, ct);
    public static Task Handle(DispatchCommentDelivery command, CommentDeliveryWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Version, message.Envelope?.Id ?? Guid.Empty, true, ct);
}
