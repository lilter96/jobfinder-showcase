using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed partial class DeliveryLedger
{
    private async Task<Guid?> TryClaimMessageAsync(ApprovedDispatch dispatch, CancellationToken ct)
    {
        var expected = dispatch.Message!;
        var input = dispatch.Input ?? throw new InvalidOperationException("нет снимка сообщения");
        if (dispatch.Review.Score < LetterWriter.MinVerdict || dispatch.Review.Problems.Count != 0
            || dispatch.Review.TextHash != Fingerprint.Of(dispatch.Text) || dispatch.Text != input.Text)
            throw new InvalidOperationException("сообщение изменилось или не прошло проверку");
        if (input.AttachResume && dispatch.Resume is null || dispatch.Resume is { } resume
            && (resume.TailoredResumeId is null || resume.Content is null || resume.Sha256 != HashBytes(resume.Content)))
            throw new InvalidOperationException("нет проверенного снимка вложения");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var vacancy = await LockVacancyAsync(context, expected.VacancyId, ct);
        var current = await context.EmployerMessages.SingleAsync(row => row.Id == expected.Id, ct);
        if (current.Status != EmployerMessageStatus.Ready) return null;
        if (JsonSerializer.Serialize(EmployerMessageSources.Read(current), EmployerMessageJsonContext.Default.EmployerMessageInput)
                != JsonSerializer.Serialize(input, EmployerMessageJsonContext.Default.EmployerMessageInput)
            || current.Review is null || current.Review.TextHash != dispatch.Review.TextHash || current.Review.ContextHash != dispatch.Review.ContextHash
            || current.Review.Score != dispatch.Review.Score || current.Review.Problems.Count != 0
            || current.TailoredResumeId != dispatch.Resume?.TailoredResumeId || current.ResumeHash != dispatch.Resume?.Sha256)
            throw new InvalidOperationException("проверенный пакет не совпадает с сохранённым");
        await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {ProfileProvider.Key} OR key = {FactsProvider.Key} FOR SHARE").ToListAsync(ct);
        if (input.EventId is { } eventId)
            await context.ApplicationEvents.FromSqlInterpolated($"SELECT * FROM application_events WHERE id = {eventId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (input.OutreachId is { } outreachId)
            await context.Outreaches.FromSqlInterpolated($"SELECT * FROM outreaches WHERE id = {outreachId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (input.ContactId is { } contactId)
            await context.CompanyContacts.FromSqlInterpolated($"SELECT * FROM company_contacts WHERE id = {contactId} FOR SHARE").SingleOrDefaultAsync(ct);
        var blocked = vacancy is null || !await EmployerMessageSources.MatchesAsync(context, input, ct)
            || await context.EmployerMessages.AnyAsync(row => row.VacancyId == input.VacancyId && row.Id != current.Id
                && (row.Status == EmployerMessageStatus.Sending || row.Status == EmployerMessageStatus.OutcomeUnknown), ct)
            || await context.Applications.AnyAsync(row => row.VacancyId == input.VacancyId
                && (row.Status == ApplicationStatus.Pending || row.Status == ApplicationStatus.Sending || row.Status == ApplicationStatus.OutcomeUnknown), ct);
        if (input.Automatic)
        {
            var policy = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {AutomationPolicyStore.Key} FOR SHARE").SingleOrDefaultAsync(ct);
            blocked |= policy is null || JsonSerializer.Deserialize(policy.Content, AutomationPolicyJsonContext.Default.AutomationPolicy) is not { ChatAutoReply: true };
        }
        if (blocked || !await EmployerMessageSources.FreezeAsync(context, input, ct))
        {
            current.Status = EmployerMessageStatus.Cancelled;
            current.Error = "контекст изменился, автоматизация выключена или предыдущая доставка требует сверки";
            current.CompletedAt = time.GetUtcNow();
            AddMessageNotice(context, current, false);
            await notifier.SaveAndFlushAsync(context, ct);
            return null;
        }
        current.Status = EmployerMessageStatus.Sending;
        current.DispatchEnvelopeId = expected.DispatchEnvelopeId;
        var attempt = new DeliveryAttempt
        {
            Id = Guid.NewGuid(), MessageId = current.Id, ApplicationId = input.ApplicationId, ApplicationRevision = input.SourceRevision,
            OperationId = current.Id, Channel = input.Channel, Target = input.Target, Text = input.Text, TextHash = Hash(input.Text),
            Review = current.Review, TailoredResumeId = current.TailoredResumeId, ResumeHash = current.ResumeHash,
            ResumeFileName = dispatch.Resume?.FileName, CreatedAt = time.GetUtcNow(), Status = DeliveryAttemptStatus.Submitting,
        };
        context.DeliveryAttempts.Add(attempt);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return attempt.Id;
    }

    private async Task<bool> CompleteMessageAsync(JobFinderDbContext context, IDbContextTransaction transaction,
        DeliveryAttempt attempt, DeliveryReceipt receipt, CancellationToken ct, bool reconciled = false)
    {
        var known = await context.EmployerMessages.AsNoTracking().SingleAsync(row => row.Id == attempt.MessageId, ct);
        var vacancy = await LockVacancyAsync(context, known.VacancyId, ct) ?? throw new InvalidOperationException("вакансия удалена во время доставки");
        var message = await context.EmployerMessages.SingleAsync(row => row.Id == known.Id, ct);
        await context.Entry(attempt).ReloadAsync(ct);
        if (message.Status != (reconciled ? EmployerMessageStatus.OutcomeUnknown : EmployerMessageStatus.Sending)
            || attempt.Status != (reconciled ? DeliveryAttemptStatus.OutcomeUnknown : DeliveryAttemptStatus.Submitting)) return false;
        var input = EmployerMessageSources.Read(message);
        var now = time.GetUtcNow();
        message.Status = receipt.Status switch
        {
            ApplicationStatus.Sent => EmployerMessageStatus.Sent,
            ApplicationStatus.OutcomeUnknown => EmployerMessageStatus.OutcomeUnknown,
            _ => EmployerMessageStatus.NeedsManual,
        };
        message.CompletedAt = now;
        message.Error = receipt.Error;
        attempt.Status = reconciled ? DeliveryAttemptStatus.Reconciled : message.Status switch
        {
            EmployerMessageStatus.Sent => DeliveryAttemptStatus.Confirmed,
            EmployerMessageStatus.OutcomeUnknown => DeliveryAttemptStatus.OutcomeUnknown,
            _ => DeliveryAttemptStatus.KnownNotSent,
        };
        attempt.CompletedAt = now;
        attempt.Error = receipt.Error;
        attempt.ExternalId = receipt.EmailMessageId ?? receipt.TargetPeerId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await FinishSourceAsync(context, input, message.Status, now, ct, reconciled);
        if (message.Status == EmployerMessageStatus.Sent)
        {
            var application = input.CreateApplication ? new Application
            {
                VacancyId = input.VacancyId, OperationId = message.Id, Channel = input.Channel, Target = input.Target,
                TargetPeerId = receipt.TargetPeerId, EmailMessageId = receipt.EmailMessageId, CoverLetter = input.Text,
                Status = ApplicationStatus.Sent, Stage = ApplicationStage.Sent, Mode = ApplicationMode.Approved,
                CreatedAt = now, SentAt = now, StageChangedAt = now, TailoredResumeId = reconciled ? null : attempt.TailoredResumeId,
            } : await context.Applications.SingleAsync(row => row.Id == input.ApplicationId, ct);
            if (input.CreateApplication)
            {
                context.Applications.Add(application);
                vacancy.Status = VacancyStatus.Applied;
                vacancy.UpdatedAt = now;
            }
            application.LastOutboundAt = now;
            var channel = input.Channel switch
            {
                ApplicationChannel.TelegramDm => EventChannel.Telegram,
                ApplicationChannel.Email => EventChannel.Email,
                _ => EventChannel.HeadHunter,
            };
            context.ApplicationEvents.Add(new ApplicationEvent
            {
                Application = application, At = now, Kind = EventKind.MessageOut, Channel = channel,
                Stage = input.CreateApplication ? ApplicationStage.Sent : null, Author = input.Automatic ? ReplyService.AutoAuthor : "я",
                Text = input.Text + (attempt.ResumeFileName is null || reconciled ? "" : $"\n📎 {attempt.ResumeFileName}"),
            });
            if (input.ContactId is { } contactId && input.OutreachId is null && !input.CreateApplication)
                context.Outreaches.Add(new Outreach
                {
                    VacancyId = input.VacancyId, ContactId = contactId, Text = input.Text, Status = OutreachStatus.Sent,
                    CreatedAt = now, SentAt = now, Fresh = input.Fresh,
                });
            await context.SaveChangesAsync(ct);
            message.ApplicationId = application.Id;
            attempt.ApplicationId = application.Id;
        }
        if (reconciled)
            context.Notifications.Add(new Notification
            {
                Kind = NotificationKind.Info, Status = NotificationStatus.Pending, EntityId = message.VacancyId, CreatedAt = now,
                Text = $"🔎 Доставка сообщения по вакансии #{message.VacancyId} сверена пользователем: "
                    + (message.Status == EmployerMessageStatus.Sent ? "отправлено" : "не отправлено") + $". {TextUtil.EscapeHtml(receipt.Error)}",
            });
        else AddMessageNotice(context, message, message.Status == EmployerMessageStatus.Sent);
        await notifier.SaveAndFlushAsync(context, ct);
        return true;
    }

    private static async Task FinishSourceAsync(JobFinderDbContext context, EmployerMessageInput input,
        EmployerMessageStatus status, DateTimeOffset now, CancellationToken ct, bool reconciled = false)
    {
        if (input.EventId is { } eventId)
        {
            var source = await context.ApplicationEvents.SingleAsync(row => row.Id == eventId, ct);
            if (source.DraftStatus != (reconciled ? DraftStatus.OutcomeUnknown : DraftStatus.Sending)
                || source.Revision != input.SourceRevision + (reconciled ? 2 : 1))
                throw new InvalidOperationException("источник изменён во время доставки");
            source.DraftStatus = status == EmployerMessageStatus.Sent ? DraftStatus.Sent
                : status == EmployerMessageStatus.OutcomeUnknown ? DraftStatus.OutcomeUnknown : DraftStatus.Pending;
            if (status == EmployerMessageStatus.Sent) source.Draft = input.Text;
            source.Revision++;
        }
        if (input.OutreachId is { } outreachId)
        {
            var source = await context.Outreaches.SingleAsync(row => row.Id == outreachId, ct);
            if (source.Status != (reconciled ? OutreachStatus.OutcomeUnknown : OutreachStatus.Sending)
                || source.Revision != input.SourceRevision + (reconciled ? 2 : 1))
                throw new InvalidOperationException("источник изменён во время доставки");
            source.Status = status == EmployerMessageStatus.Sent ? OutreachStatus.Sent
                : status == EmployerMessageStatus.OutcomeUnknown ? OutreachStatus.OutcomeUnknown : OutreachStatus.Draft;
            if (status == EmployerMessageStatus.Sent) { source.Text = input.Text; source.SentAt = now; }
            source.Revision++;
        }
    }

    public async Task InterruptMessageAsync(Guid id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var attempt = await context.DeliveryAttempts.AsNoTracking().SingleOrDefaultAsync(row => row.MessageId == id && row.Status == DeliveryAttemptStatus.Submitting, ct);
        if (attempt is not null) await CompleteAsync(attempt.Id,
            new DeliveryReceipt(ApplicationStatus.OutcomeUnknown, "доставка прервана; автоматический повтор остановлен до сверки"), null, ct);
    }

    public async Task<bool> ReconcileMessageAsync(Guid id, bool sent, string evidence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evidence)) throw new ArgumentException("нужно описать результат сверки", nameof(evidence));
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var attempt = await context.DeliveryAttempts.SingleOrDefaultAsync(row => row.MessageId == id, ct);
        if (attempt is not { Status: DeliveryAttemptStatus.OutcomeUnknown }) return false;
        return await CompleteMessageAsync(context, transaction, attempt,
            new DeliveryReceipt(sent ? ApplicationStatus.Sent : ApplicationStatus.NeedsManual, evidence.Trim()), ct, reconciled: true);
    }

    private static void AddMessageNotice(JobFinderDbContext context, EmployerMessage message, bool sent) => context.Notifications.Add(new Notification
    {
        Kind = sent ? NotificationKind.Info : NotificationKind.ApplyFailed, Status = NotificationStatus.Pending,
        CreatedAt = message.CompletedAt ?? message.CreatedAt, EntityId = message.VacancyId,
        Text = sent ? $"✉️ Сообщение по вакансии #{message.VacancyId} отправлено"
            : $"⚠️ Доставка сообщения по вакансии #{message.VacancyId}: {TextUtil.EscapeHtml(message.Error ?? message.Status.ToString())}"
                + $"\nID операции: <code>{message.Id}</code>"
                + (message.Status == EmployerMessageStatus.OutcomeUnknown ? "\nНе повторяйте отправку до ручной сверки; результат записывается через --message-reconcile." : ""),
    });
}
