using System.Security.Cryptography;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Notifications;
using System.Text.Json;
using System.Globalization;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed partial class DeliveryLedger(IDbContextFactory<JobFinderDbContext> db, TimeProvider time, Notifier notifier, IServiceScopeFactory? scopes = null)
{
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static Task<Vacancy?> LockVacancyAsync(JobFinderDbContext context, long vacancyId, CancellationToken ct) =>
        context.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {vacancyId} FOR UPDATE").SingleOrDefaultAsync(ct);

    public async Task<Guid?> TryClaimAsync(ApprovedDispatch dispatch, CancellationToken ct)
    {
        if (dispatch.Message is not null) return await TryClaimMessageAsync(dispatch, ct);
        var expected = dispatch.Application;
        if (dispatch.Review.Score < Career.LetterWriter.MinVerdict || dispatch.Review.Problems.Count != 0)
            throw new InvalidOperationException("письмо не прошло проверку");
        if (dispatch.Review.TextHash != Career.Fingerprint.Of(dispatch.Text))
            throw new InvalidOperationException("письмо изменилось после проверки");
        if (dispatch.Resume is { } resume && (resume.TailoredResumeId is null || resume.Content is null || resume.Sha256 != HashBytes(resume.Content)))
            throw new InvalidOperationException("нет проверенного снимка резюме");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var vacancy = await LockVacancyAsync(context, expected.VacancyId, ct);
        if (vacancy is null || vacancy.Status is VacancyStatus.Dismissed or VacancyStatus.Applied) return null;
        var marker = await context.Applications.Where(row => row.Id == expected.Id).Select(row => row.DeliveryRequestId).SingleAsync(ct);
        if (marker != dispatch.ApplicationDeliveryRequestId) return null;
        ApplicationDeliveryRequest? native = null;
        if (marker is { } nativeId)
        {
            native = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == nativeId, ct);
            if (native.Stage != ApplicationDeliveryStage.Ready) return null;
            var input = ApplicationDeliverySources.Read(native);
            if (input.ApplicationId != expected.Id || input.Text != dispatch.Text || native.Review is null
                || !Career.LetterWriter.Matches(native.Review, dispatch.Text, input.ReviewContextHash)
                || native.Review.ContextHash != dispatch.Review.ContextHash || native.Review.TextHash != dispatch.Review.TextHash
                || native.Review.Score != dispatch.Review.Score || native.TailoredResumeId != dispatch.Resume?.TailoredResumeId
                || native.ResumeHash != dispatch.Resume?.Sha256 || input.Channel is ApplicationChannel.Email or ApplicationChannel.TelegramDm && dispatch.Resume is null
                || !await ApplicationDeliverySources.MatchesAsync(context, input, ct)) return null;
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('apply-native-dispatch', 0))", ct);
            if (await context.Applications.AnyAsync(row => row.Status == ApplicationStatus.Sending, ct)) return null;
            var cursor = await context.Settings.SingleOrDefaultAsync(row => row.Key == ApplicationDeliveryControls.NextSend, ct);
            if (cursor is not null && (!DateTimeOffset.TryParse(cursor.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var next)
                || next > time.GetUtcNow())) return null;
            var policyRow = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {AutomationPolicyStore.Key} FOR SHARE").SingleAsync(ct);
            var policy = JsonSerializer.Deserialize(policyRow.Content, AutomationPolicyJsonContext.Default.AutomationPolicy) ?? throw new JsonException("missing automation policy");
            if (policy.Validate() is { } policyError) throw new InvalidOperationException("invalid current automation policy: " + policyError);
            if ((input.Automation.ApplyDryRun || policy.ApplyDryRun) && !dispatch.DryRun) return null;
            if (input.Mode == ApplicationMode.Auto)
            {
                if (input.Automation.AutoApplyPaused || policy.AutoApplyPaused || input.Decision != Decision.AutoApply || input.Score is null
                    || input.Score < Math.Max(input.Automation.AutoApplyThreshold, policy.AutoApplyThreshold)) return null;
                using var scope = (scopes ?? throw new InvalidOperationException("native delivery scope is required")).CreateScope();
                var since = scope.ServiceProvider.GetRequiredService<Core.Time.LocalClock>().StartOfTodayUtc();
                var reservations = await context.DeliveryAttempts.CountAsync(row => row.Application!.Mode == ApplicationMode.Auto && row.CreatedAt >= since
                    && (row.Status == DeliveryAttemptStatus.Submitting || row.Status == DeliveryAttemptStatus.Confirmed
                        || row.Status == DeliveryAttemptStatus.OutcomeUnknown || row.Status == DeliveryAttemptStatus.Reconciled || row.Status == DeliveryAttemptStatus.DryRun), ct);
                if (reservations >= Math.Min(input.Automation.DailyAutoApplyLimit, policy.DailyAutoApplyLimit)) return null;
            }
            cursor ??= new AppSetting { Key = ApplicationDeliveryControls.NextSend, Value = "" };
            if (context.Entry(cursor).State == EntityState.Detached) context.Settings.Add(cursor);
            cursor.Value = time.GetUtcNow().AddSeconds(Random.Shared.Next(25, 70)).ToString("O", CultureInfo.InvariantCulture);
            cursor.UpdatedAt = time.GetUtcNow();
        }
        if (await context.Applications.AnyAsync(application => application.VacancyId == expected.VacancyId && application.Id != expected.Id
                && (application.Status == ApplicationStatus.Sending || application.Status == ApplicationStatus.Sent
                    || application.Status == ApplicationStatus.OutcomeUnknown), ct)) return null;
        if (await context.EmployerMessages.AnyAsync(message => message.VacancyId == expected.VacancyId
            && (message.Status == EmployerMessageStatus.Sending || message.Status == EmployerMessageStatus.OutcomeUnknown), ct)) return null;
        if (await context.ApplicationDeliveryRequests.AnyAsync(row => row.Id != marker
            && context.Applications.Any(application => application.Id == row.ApplicationId && application.VacancyId == expected.VacancyId)
            && (row.Stage == ApplicationDeliveryStage.Sending || row.Stage == ApplicationDeliveryStage.OutcomeUnknown), ct)) return null;
        var changed = await context.Applications.Where(application => application.Id == expected.Id
                && application.Status == ApplicationStatus.Pending && application.Revision == expected.Revision
                && application.CoverLetter == expected.CoverLetter && application.Target == expected.Target
                && application.Channel == expected.Channel
                && (application.CoverLetter != null || application.Vacancy!.CoverLetter == expected.Vacancy!.CoverLetter))
            .ExecuteUpdateAsync(update => update.SetProperty(application => application.Status, ApplicationStatus.Sending)
                .SetProperty(application => application.Revision, application => application.Revision + 1), ct);
        if (changed == 0) return null;
        var attempt = new DeliveryAttempt
        {
            Id = Guid.NewGuid(), ApplicationId = expected.Id, ApplicationRevision = expected.Revision + 1,
            OperationId = expected.OperationId, Channel = expected.Channel, Target = expected.Target,
            ApplicationDeliveryRequestId = native?.Id,
            Text = dispatch.Text, TextHash = Hash(dispatch.Text), Review = dispatch.Review,
            TailoredResumeId = dispatch.Resume?.TailoredResumeId, ResumeHash = dispatch.Resume?.Sha256,
            ResumeFileName = dispatch.Resume?.FileName, Status = DeliveryAttemptStatus.Submitting, CreatedAt = time.GetUtcNow(),
        };
        context.DeliveryAttempts.Add(attempt);
        if (native is not null) { native.Stage = ApplicationDeliveryStage.Sending; native.ClaimEnvelopeId = null; }
        if (vacancy.CoverLetter == expected.Vacancy?.CoverLetter) vacancy.LetterReview = dispatch.Review;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return attempt.Id;
    }

    public async Task<bool> CompleteAsync(Guid attemptId, DeliveryReceipt receipt,
        Action<JobFinderDbContext, Application, DeliveryReceipt>? record, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var attempt = await context.DeliveryAttempts.SingleAsync(attempt => attempt.Id == attemptId, ct);
        if (attempt.Status != DeliveryAttemptStatus.Submitting) return false;
        if (attempt.MessageId is not null) return await CompleteMessageAsync(context, transaction, attempt, receipt, ct);
        var vacancyId = await context.Applications.Where(application => application.Id == attempt.ApplicationId)
            .Select(application => application.VacancyId).SingleAsync(ct);
        await LockVacancyAsync(context, vacancyId, ct);
        var application = await context.Applications.Include(application => application.Vacancy).ThenInclude(vacancy => vacancy!.Sources)
            .SingleAsync(application => application.Id == attempt.ApplicationId, ct);
        if (application.Status != ApplicationStatus.Sending || application.Revision != attempt.ApplicationRevision) return false;
        application.Status = receipt.Status;
        application.Error = receipt.Error;
        application.ScreenshotPath = receipt.Screenshot ?? application.ScreenshotPath;
        if (receipt.Status != ApplicationStatus.DryRun) application.Attempts++;
        if (receipt.Status == ApplicationStatus.Sent)
        {
            application.SentAt = time.GetUtcNow();
            application.TargetPeerId = receipt.TargetPeerId;
            application.EmailMessageId = receipt.EmailMessageId;
            application.TailoredResumeId = attempt.TailoredResumeId;
            application.CoverLetter = attempt.Text;
        }
        attempt.Status = receipt.Status switch
        {
            ApplicationStatus.Sent => DeliveryAttemptStatus.Confirmed,
            ApplicationStatus.OutcomeUnknown => DeliveryAttemptStatus.OutcomeUnknown,
            ApplicationStatus.DryRun => DeliveryAttemptStatus.DryRun,
            _ => DeliveryAttemptStatus.KnownNotSent,
        };
        attempt.CompletedAt = time.GetUtcNow();
        attempt.Error = receipt.Error;
        attempt.ExternalId = receipt.EmailMessageId ?? receipt.TargetPeerId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (attempt.ApplicationDeliveryRequestId is { } nativeId)
        {
            var native = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == nativeId, ct);
            if (native.Stage != ApplicationDeliveryStage.Sending) return false;
            native.Stage = receipt.Status == ApplicationStatus.OutcomeUnknown ? ApplicationDeliveryStage.OutcomeUnknown : ApplicationDeliveryStage.Completed;
            native.CompletedAt = time.GetUtcNow();
            native.Error = receipt.Error;
        }
        record?.Invoke(context, application, receipt);
        if (attempt.ApplicationDeliveryRequestId is { } requestId)
        {
            await using var scope = (scopes ?? throw new InvalidOperationException("native receipt scope is required")).CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('apply-native-dispatch', 0))", ct);
            var cursor = await context.Settings.SingleOrDefaultAsync(row => row.Key == ApplicationDeliveryControls.NextSend, ct)
                ?? new AppSetting { Key = ApplicationDeliveryControls.NextSend, Value = "" };
            if (context.Entry(cursor).State == EntityState.Detached) context.Settings.Add(cursor);
            cursor.Value = time.GetUtcNow().AddSeconds(Random.Shared.Next(25, 70)).ToString("O", CultureInfo.InvariantCulture);
            cursor.UpdatedAt = time.GetUtcNow();
            if (receipt.Status == ApplicationStatus.Pending)
            {
                if (application.Channel != ApplicationChannel.HeadHunter) throw new InvalidOperationException("only hh can retry known-not-sent delivery");
                var at = time.GetUtcNow().AddMinutes(scope.ServiceProvider.GetRequiredService<Integrations.HeadHunter.HhOptions>().BlockedPauseMinutes);
                await scope.ServiceProvider.GetRequiredService<ApplicationBlocks>().RecordJoinedAsync(context, outbox, at, attempt, ct);
                application.Revision++;
                await outbox.PublishAsync(new RetryApplicationDelivery { RequestId = requestId, ApplicationRevision = application.Revision });
            }
            if (receipt.Contact is { } contact) await outbox.PublishAsync(new RememberApplicationContact
            {
                RequestId = requestId, Name = contact.Name, Email = contact.Email, Phone = contact.Phone,
            });
            await scope.ServiceProvider.GetRequiredService<NotificationDeliveryRequests>().SaveAndFlushAsync(context, outbox, ct);
        }
        else
        {
            await notifier.SaveAndFlushAsync(context, ct);
        }
        return true;
    }

    public async Task<bool> RejectPreparationAsync(Application expected, string message,
        Action<JobFinderDbContext, Application>? record, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockVacancyAsync(context, expected.VacancyId, ct);
        var application = await context.Applications.Include(application => application.Vacancy).ThenInclude(vacancy => vacancy!.Sources)
            .SingleOrDefaultAsync(application => application.Id == expected.Id, ct);
        if (application is null || application.Status != ApplicationStatus.Pending || application.Revision != expected.Revision) return false;
        application.Status = ApplicationStatus.NeedsManual;
        application.Error = message;
        record?.Invoke(context, application);
        await notifier.SaveAndFlushAsync(context, ct);
        return true;
    }

    public async Task InterruptApplicationRequestAsync(Guid id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.ApplicationDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        var vacancyId = await context.Applications.Where(row => row.Id == known.ApplicationId).Select(row => row.VacancyId).SingleAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockVacancyAsync(context, vacancyId, ct);
        var request = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == id, ct);
        if (request.Stage != ApplicationDeliveryStage.Sending) return;
        var application = await context.Applications.Include(row => row.Vacancy).ThenInclude(row => row!.Sources)
            .SingleAsync(row => row.Id == known.ApplicationId, ct);
        if (application.Status != ApplicationStatus.Sending) return;
        const string reason = "доставка прервана до сохранения receipt; результат нужно сверить, повтор остановлен";
        request.Stage = ApplicationDeliveryStage.OutcomeUnknown;
        request.Error = reason;
        request.CompletedAt = time.GetUtcNow();
        application.Status = ApplicationStatus.OutcomeUnknown;
        application.Revision++;
        application.Error = reason;
        await context.DeliveryAttempts.Where(row => row.ApplicationDeliveryRequestId == id && row.Status == DeliveryAttemptStatus.Submitting)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, DeliveryAttemptStatus.OutcomeUnknown)
                .SetProperty(row => row.CompletedAt, time.GetUtcNow()).SetProperty(row => row.Error, reason), ct);
        using var scope = (scopes ?? throw new InvalidOperationException("native interruption scope is required")).CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDeliveryPublication>().Record(context, application, new DeliveryReceipt(ApplicationStatus.OutcomeUnknown, reason));
        await scope.ServiceProvider.GetRequiredService<NotificationDeliveryRequests>().SaveAndFlushAsync(context, ct);
    }

    public static async Task<int> RecoverAsync(JobFinderDbContext context, Notifier notifier, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var interrupted = await context.Applications.Where(application => application.Status == ApplicationStatus.Sending)
            .Select(application => new { application.VacancyId, application.Vacancy!.Title, application.DeliveryRequestId }).ToListAsync(ct);
        var recovered = await context.Applications.Where(application => application.Status == ApplicationStatus.Sending)
            .ExecuteUpdateAsync(update => update.SetProperty(application => application.Status, ApplicationStatus.OutcomeUnknown)
                .SetProperty(application => application.Revision, application => application.Revision + 1)
                .SetProperty(application => application.Error, "доставка прервана; результат нужно сверить, автоматический повтор остановлен"), ct);
        var messages = await context.EmployerMessages.Where(row => row.Status == EmployerMessageStatus.Sending).ToListAsync(ct);
        await context.ApplicationDeliveryRequests.Where(row => row.Stage == ApplicationDeliveryStage.Sending)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Stage, ApplicationDeliveryStage.OutcomeUnknown)
                .SetProperty(row => row.CompletedAt, now).SetProperty(row => row.Error, "server interrupted before delivery receipt was committed"), ct);
        foreach (var message in messages)
        {
            message.Status = EmployerMessageStatus.OutcomeUnknown;
            message.CompletedAt = now;
            message.Error = "доставка прервана; результат нужно сверить, автоматический повтор остановлен";
            await FinishSourceAsync(context, EmployerMessageSources.Read(message), message.Status, now, ct);
            AddMessageNotice(context, message, false);
        }
        recovered += messages.Count;
        await context.DeliveryAttempts.Where(attempt => attempt.Status == DeliveryAttemptStatus.Submitting)
            .ExecuteUpdateAsync(update => update.SetProperty(attempt => attempt.Status, DeliveryAttemptStatus.OutcomeUnknown)
                .SetProperty(attempt => attempt.CompletedAt, now)
                .SetProperty(attempt => attempt.Error, "server interrupted before delivery receipt was committed"), ct);
        foreach (var application in interrupted)
            context.Notifications.Add(new Notification
            {
                Kind = NotificationKind.ApplyFailed, Status = NotificationStatus.Pending, CreatedAt = now, EntityId = application.VacancyId,
                Text = $"⚠️ Доставка отклика требует сверки: {Core.Text.TextUtil.EscapeHtml(application.Title)} · #{application.VacancyId}. Автоматический повтор остановлен."
                    + (application.DeliveryRequestId is { } id ? $"\nID операции: <code>{id}</code>\nРезультат записывается через --application-reconcile." : ""),
            });
        await notifier.SaveAndFlushAsync(context, ct);
        return recovered;
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
