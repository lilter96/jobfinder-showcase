using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class EmployerMessageWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IEmployerMessageGate gate, DeliveryGateway gateway, DeliveryLedger ledger, ServerOwnership ownership, TimeProvider time,
    NotificationDeliveryRequests notifications)
{
    public async Task PrepareAsync(Guid id, int version, Guid envelopeId, CancellationToken ct)
    {
        Validate(id, version, envelopeId);
        var message = await ClaimAsync(id, envelopeId, ct);
        if (message is null) return;
        using var stage = Telemetry.StartStage("prepare", "employer-message").Tag("message.id", id).Tag("vacancy.id", message.VacancyId);
        var input = EmployerMessageSources.Read(message);
        try
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var vacancy = await EmployerMessageSources.VacancyAsync(context, message.VacancyId, ct)
                ?? throw new ResumeQualityException("вакансия удалена");
            ownership.EnsureOwned();
            var preparation = await gate.PrepareAsync(message, input, vacancy, ct);
            if (preparation is EmployerMessagePreparation.ReviewPending pendingReview)
            {
                if (pendingReview.RequestId == Guid.Empty) throw new ResumeQualityException("нет запроса проверки письма");
                await ChangeAsync(message, async (saved, outbox, current) =>
                {
                    if (!await EmployerMessageSources.MatchesAsync(saved, input, ct))
                    {
                        Stop(saved, current, EmployerMessageStatus.Cancelled, "контекст или факты изменились во время подготовки проверки");
                        return;
                    }
                    current.LetterReviewRequestId = pendingReview.RequestId;
                    await ContinueAsync(current, outbox);
                }, ct);
                return;
            }
            if (preparation is EmployerMessagePreparation.ResumePending pendingResume)
            {
                CheckReview(pendingResume.Review, input.Text);
                if (pendingResume.RequestId == Guid.Empty) throw new ResumeQualityException("нет запроса подготовки резюме");
                await ChangeAsync(message, async (saved, outbox, current) =>
                {
                    if (!await EmployerMessageSources.MatchesAsync(saved, input, ct))
                    {
                        Stop(saved, current, EmployerMessageStatus.Cancelled, "контекст или факты изменились во время проверки");
                        return;
                    }
                    current.Review = pendingResume.Review;
                    current.LetterReviewRequestId = pendingResume.ReviewRequestId ?? current.LetterReviewRequestId;
                    current.ResumeRequestId = pendingResume.RequestId;
                    await ContinueAsync(current, outbox);
                }, ct);
                return;
            }
            var output = preparation is EmployerMessagePreparation.Ready ready ? ready.Dispatch : throw new ResumeQualityException("нет проверенного пакета сообщения");
            if (output.Text != input.Text) throw new ResumeQualityException("проверка изменила подтверждённый текст сообщения");
            CheckReview(output.Review, input.Text);
            ownership.EnsureOwned();
            await ChangeAsync(message, async (saved, outbox, current) =>
            {
                if (!await EmployerMessageSources.MatchesAsync(saved, input, ct))
                {
                    Stop(saved, current, EmployerMessageStatus.Cancelled, "контекст или факты изменились во время проверки");
                    return;
                }
                current.Review = output.Review;
                current.LetterReviewRequestId = ((EmployerMessagePreparation.Ready)preparation).ReviewRequestId ?? current.LetterReviewRequestId;
                current.TailoredResumeId = output.Resume?.TailoredResumeId;
                current.ResumeHash = output.Resume?.Sha256;
                current.Status = EmployerMessageStatus.Ready;
                current.Error = null;
                current.RetryAt = null;
                current.ClaimEnvelopeId = null;
                await outbox.PublishAsync(new DispatchEmployerMessage { MessageId = id });
            }, ct);
        }
        catch (LlmUnavailableException error)
        {
            await ChangeAsync(message, async (_, outbox, current) =>
            {
                current.ClaimEnvelopeId = null;
                current.Error = error.Message;
                current.RetryAt = time.GetUtcNow() + (error.Unauthorized ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(2));
                await outbox.PublishAsync(new PrepareEmployerMessage { MessageId = id }, new DeliveryOptions { ScheduledTime = current.RetryAt });
            }, ct);
        }
        catch (ResumeQualityException error)
        {
            await ChangeAsync(message, (context, _, current) =>
            {
                Stop(context, current, EmployerMessageStatus.NeedsManual, error.Message);
                return Task.CompletedTask;
            }, ct);
        }
    }

    private async Task ContinueAsync(EmployerMessage current, IDbContextOutbox outbox)
    {
        ownership.EnsureOwned();
        current.ClaimEnvelopeId = null;
        current.RetryAt = time.GetUtcNow().AddSeconds(5);
        current.Error = null;
        await outbox.PublishAsync(new PrepareEmployerMessage { MessageId = current.Id }, new DeliveryOptions { ScheduledTime = current.RetryAt });
    }

    private static void CheckReview(ApplicationLetterReview review, string text)
    {
        if (review.Score is < Career.LetterWriter.MinVerdict or > 10 || review.Problems.Count != 0 || review.TextHash != Career.Fingerprint.Of(text))
            throw new ResumeQualityException("нет проверки точного текста сообщения");
    }

    public async Task DispatchAsync(Guid id, int version, Guid envelopeId, CancellationToken ct)
    {
        Validate(id, version, envelopeId);
        await using var context = await db.CreateDbContextAsync(ct);
        var message = await context.EmployerMessages.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (message is null) return;
        ownership.EnsureOwned();
        if (message.Status == EmployerMessageStatus.Sending)
        {
            if (!gateway.IsSending(id)) await ledger.InterruptMessageAsync(id, ct);
            return;
        }
        if (message.Status != EmployerMessageStatus.Ready) return;
        message.DispatchEnvelopeId = envelopeId;
        var input = EmployerMessageSources.Read(message);
        ApprovedDispatch dispatch;
        try
        {
            var vacancy = await EmployerMessageSources.VacancyAsync(context, input.VacancyId, ct)
                ?? throw new ResumeQualityException("вакансия удалена");
            dispatch = await gate.RestoreAsync(message, input, vacancy, ct);
        }
        catch (Exception error) when (error is ResumeQualityException or IOException or UnauthorizedAccessException)
        {
            await ChangeAsync(message, (saved, _, current) =>
            {
                Stop(saved, current, EmployerMessageStatus.NeedsManual, error.Message);
                return Task.CompletedTask;
            }, ct);
            return;
        }
        await gateway.DispatchAsync(dispatch, null, ct);
    }

    private async Task<EmployerMessage?> ClaimAsync(Guid id, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.EmployerMessages.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (known is null || known.Status != EmployerMessageStatus.Pending || known.RetryAt > time.GetUtcNow()) return null;
        ownership.EnsureOwned();
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await DeliveryLedger.LockVacancyAsync(context, known.VacancyId, ct);
        var current = await context.EmployerMessages.SingleAsync(row => row.Id == id, ct);
        if (current.Status != EmployerMessageStatus.Pending || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } owner && owner != envelopeId) return null;
        if (!await EmployerMessageSources.MatchesAsync(context, EmployerMessageSources.Read(current), ct))
        {
            Stop(context, current, EmployerMessageStatus.Cancelled, "контекст или факты изменились после подтверждения");
            await notifications.SaveAndFlushAsync(context, ct);
            return null;
        }
        current.ClaimEnvelopeId = envelopeId;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return current;
    }

    private async Task ChangeAsync(EmployerMessage before, Func<JobFinderDbContext, IDbContextOutbox, EmployerMessage, Task> change, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await DeliveryLedger.LockVacancyAsync(context, before.VacancyId, ct);
        var current = await context.EmployerMessages.SingleAsync(row => row.Id == before.Id, ct);
        if (current.Status != before.Status || current.ClaimEnvelopeId != before.ClaimEnvelopeId) return;
        ownership.EnsureOwned();
        await change(context, outbox, current);
        await notifications.SaveAndFlushAsync(context, outbox, ct);
    }

    private void Stop(JobFinderDbContext context, EmployerMessage message, EmployerMessageStatus status, string reason)
    {
        message.Status = status;
        message.Error = reason;
        message.CompletedAt = time.GetUtcNow();
        message.ClaimEnvelopeId = null;
        context.Notifications.Add(new Notification
        {
            Kind = NotificationKind.ApplyFailed, Status = NotificationStatus.Pending, EntityId = message.VacancyId,
            CreatedAt = time.GetUtcNow(), Text = $"⚠️ Сообщение по вакансии #{message.VacancyId} не отправлено: {TextUtil.EscapeHtml(reason)}",
        });
    }

    private static void Validate(Guid id, int version, Guid envelopeId)
    {
        if (id == Guid.Empty || version != 1 || envelopeId == Guid.Empty) throw new UnsupportedEmployerMessageException();
    }
}

[NonTransactional]
public static class EmployerMessageHandler
{
    public static Task Handle(PrepareEmployerMessage command, EmployerMessageWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.PrepareAsync(command.MessageId, command.Version, message.Envelope?.Id ?? throw new UnsupportedEmployerMessageException(), ct);

    public static Task Handle(DispatchEmployerMessage command, EmployerMessageWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.DispatchAsync(command.MessageId, command.Version, message.Envelope?.Id ?? throw new UnsupportedEmployerMessageException(), ct);
}
