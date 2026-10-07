using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class ApplicationDeliveryWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IApplicationDeliveryGate gate, IApplicationDeliveryControls controls,
    ApplicationDeliveryPublication publication, DeliveryGateway gateway, DeliveryLedger ledger, ServerOwnership ownership,
    Notifier notifier, TimeProvider time)
{
    public async Task PrepareAsync(Guid id, int version, Guid envelopeId, CancellationToken ct)
    {
        Validate(id, version, envelopeId);
        ownership.EnsureOwned();
        var request = await ClaimAsync(id, envelopeId, ct);
        if (request is null) return;
        using var stage = Telemetry.StartStage("prepare", "application-delivery").Tag("request.id", id).Tag("application.id", request.ApplicationId);
        var input = await ReadAsync(request, ct);
        try
        {
            var control = await controls.ReadAsync(input, ct);
            if (control.Stop is { } reason) { await StopAsync(request, reason, ct); return; }
            if (control.RetryAt is { } retry) { await DeferAsync(request, retry, false, ct); return; }
            await using var read = await db.CreateDbContextAsync(ct);
            var application = await ApplicationDeliverySources.ReadAsync(read, input.ApplicationId, ct) ?? throw new ResumeQualityException("отклик удалён");
            var result = await gate.PrepareAsync(request, input, application, ct);
            await ChangeAsync(request, async (context, outbox, current) =>
            {
                if (!await ApplicationDeliverySources.MatchesAsync(context, input, ct)) { await StopTrackedAsync(context, current, input, "источник или кандидат изменились во время подготовки", ct); return; }
                if (result is EmployerMessagePreparation.ReviewPending pendingReview)
                    current.LetterReviewRequestId = pendingReview.RequestId != Guid.Empty ? pendingReview.RequestId : throw new ResumeQualityException("нет проверки письма");
                else if (result is EmployerMessagePreparation.ResumePending pendingResume)
                {
                    EmployerContentGate.ValidateInitialReview(input, pendingResume.Review);
                    current.Review = pendingResume.Review;
                    current.LetterReviewRequestId = pendingResume.ReviewRequestId ?? current.LetterReviewRequestId;
                    current.ResumeRequestId = pendingResume.RequestId != Guid.Empty ? pendingResume.RequestId : throw new ResumeQualityException("нет подготовки резюме");
                }
                else if (result is EmployerMessagePreparation.Ready ready)
                {
                    if (ready.Dispatch.Text != input.Text || ready.Dispatch.ApplicationDeliveryRequestId != current.Id)
                        throw new ResumeQualityException("пакет относится к другому отклику");
                    EmployerContentGate.ValidateInitialReview(input, ready.Dispatch.Review);
                    current.Review = ready.Dispatch.Review;
                    current.LetterReviewRequestId = ready.ReviewRequestId ?? current.LetterReviewRequestId;
                    current.TailoredResumeId = ready.Dispatch.Resume?.TailoredResumeId;
                    current.ResumeHash = ready.Dispatch.Resume?.Sha256;
                    current.Stage = ApplicationDeliveryStage.Ready;
                    current.ClaimEnvelopeId = null;
                    current.RetryAt = null;
                    await outbox.PublishAsync(new DispatchApplicationDelivery { RequestId = id });
                    return;
                }
                else throw new ResumeQualityException("нет проверенного пакета отклика");
                current.ClaimEnvelopeId = null;
                current.RetryAt = time.GetUtcNow().AddSeconds(5);
                await outbox.PublishAsync(new PrepareApplicationDelivery { RequestId = id }, new DeliveryOptions { ScheduledTime = current.RetryAt });
            }, ct);
        }
        catch (ResumeQualityException error) { await StopAsync(request, error.Message, ct); }
        catch (ApplicationDeliverySupersededException error) { await StopAsync(request, error.Message, ct); }
        catch (LlmUnavailableException error) { await DeferAsync(request, time.GetUtcNow().AddMinutes(error.Unauthorized ? 30 : 2), false, ct); }
    }

    public async Task DispatchAsync(Guid id, int version, Guid envelopeId, CancellationToken ct)
    {
        Validate(id, version, envelopeId);
        ownership.EnsureOwned();
        await using var read = await db.CreateDbContextAsync(ct);
        var request = await read.ApplicationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (request is null) return;
        if (request.Stage == ApplicationDeliveryStage.Sending)
        {
            if (!gateway.IsSending(id)) await ledger.InterruptApplicationRequestAsync(id, ct);
            return;
        }
        if (request.Stage != ApplicationDeliveryStage.Ready || request.RetryAt > time.GetUtcNow()) return;
        var input = await ReadAsync(request, ct);
        using var stage = Telemetry.StartStage("dispatch", "application-delivery").Tag("request.id", id).Tag("application.id", request.ApplicationId);
        try
        {
            var control = await controls.ReadAsync(input, ct);
            if (control.Stop is { } reason) { await StopAsync(request, reason, ct); return; }
            if (control.RetryAt is { } retry) { await DeferAsync(request, retry, true, ct); return; }
            bool matches;
            await using (var context = await db.CreateDbContextAsync(ct))
            {
                await using var transaction = await context.Database.BeginTransactionAsync(ct);
                await DeliveryLedger.LockVacancyAsync(context, input.Vacancy.Id, ct);
                matches = await ApplicationDeliverySources.MatchesAsync(context, input, ct);
            }
            if (!matches) { await StopAsync(request, "источник или кандидат изменились после подготовки", ct); return; }
            var application = await ApplicationDeliverySources.ReadAsync(read, input.ApplicationId, ct) ?? throw new ResumeQualityException("отклик удалён");
            var packet = await gate.RestoreAsync(request, input, application, ct);
            var receipt = await gateway.DispatchAsync(packet with { DryRun = packet.DryRun || control.DryRun }, publication.Record, ct);
            if (receipt is null) { await DeferAsync(request, time.GetUtcNow().AddSeconds(5), true, ct); return; }
        }
        catch (Exception error) when (error is ResumeQualityException or ApplicationDeliverySupersededException or IOException or UnauthorizedAccessException)
        {
            await ledger.InterruptApplicationRequestAsync(id, ct);
            await StopAsync(request, error.Message, ct);
        }
    }

    private async Task<ApplicationDeliveryRequest?> ClaimAsync(Guid id, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.ApplicationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (known is null || known.Stage != ApplicationDeliveryStage.Preparing || known.RetryAt > time.GetUtcNow()) return null;
        var input = await ReadAsync(known, ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await DeliveryLedger.LockVacancyAsync(context, input.Vacancy.Id, ct);
        var current = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == id, ct);
        if (current.Stage != ApplicationDeliveryStage.Preparing || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } owner && owner != envelopeId) return null;
        if (!await context.Applications.AnyAsync(row => row.Id == input.ApplicationId && row.DeliveryRequestId == id, ct)
            || !await ApplicationDeliverySources.MatchesAsync(context, input, ct)) await StopTrackedAsync(context, current, input, "источник или кандидат изменились до подготовки", ct);
        else current.ClaimEnvelopeId = envelopeId;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return current.Stage == ApplicationDeliveryStage.Preparing ? current : null;
    }

    private Task StopAsync(ApplicationDeliveryRequest request, string reason, CancellationToken ct) => ChangeAsync(request,
        (context, _, current) => StopTrackedAsync(context, current, ApplicationDeliverySources.Read(current), reason, ct), ct);

    private async Task<ApplicationDeliveryInput> ReadAsync(ApplicationDeliveryRequest request, CancellationToken ct)
    {
        try { return ApplicationDeliverySources.Read(request); }
        catch (UnsupportedApplicationDeliveryException)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var vacancyId = await context.Applications.Where(row => row.Id == request.ApplicationId).Select(row => row.VacancyId).SingleAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
            var current = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == request.Id, ct);
            if (current.Stage is ApplicationDeliveryStage.Preparing or ApplicationDeliveryStage.Ready
                && current.InputJson == request.InputJson && current.InputHash == request.InputHash)
            {
                const string reason = "повреждён или несовместим snapshot отклика; запрос изолирован, автоматическая отправка остановлена";
                current.Stage = ApplicationDeliveryStage.Failed;
                current.CompletedAt = time.GetUtcNow();
                current.ClaimEnvelopeId = null;
                current.Error = reason;
                var application = await context.Applications.SingleAsync(row => row.Id == current.ApplicationId, ct);
                if (application.Status == ApplicationStatus.Pending && application.Revision == current.ApplicationRevision && application.DeliveryRequestId == current.Id)
                {
                    application.Status = ApplicationStatus.NeedsManual;
                    application.Error = reason;
                }
                notifier.Add(context, NotificationKind.ApplyFailed, $"⚠️ Отклик #{vacancyId} остановлен: {reason}", entityId: vacancyId);
                ownership.EnsureOwned();
                await notifier.SaveAndFlushAsync(context, ct);
            }
            throw;
        }
    }

    private Task DeferAsync(ApplicationDeliveryRequest request, DateTimeOffset at, bool dispatch, CancellationToken ct) => ChangeAsync(request,
        async (_, outbox, current) =>
        {
            current.ClaimEnvelopeId = null;
            current.RetryAt = at;
            if (dispatch) await outbox.PublishAsync(new DispatchApplicationDelivery { RequestId = current.Id }, new DeliveryOptions { ScheduledTime = at });
            else await outbox.PublishAsync(new PrepareApplicationDelivery { RequestId = current.Id }, new DeliveryOptions { ScheduledTime = at });
        }, ct);

    private async Task ChangeAsync(ApplicationDeliveryRequest before, Func<JobFinderDbContext, IDbContextOutbox, ApplicationDeliveryRequest, Task> change, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        var input = ApplicationDeliverySources.Read(before);
        await DeliveryLedger.LockVacancyAsync(context, input.Vacancy.Id, ct);
        var current = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (current.Stage != before.Stage || current.ClaimEnvelopeId != before.ClaimEnvelopeId) return;
        await change(context, outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task StopTrackedAsync(JobFinderDbContext context, ApplicationDeliveryRequest request, ApplicationDeliveryInput input, string reason, CancellationToken ct)
    {
        request.Stage = ApplicationDeliveryStage.Cancelled;
        request.Error = reason;
        request.CompletedAt = time.GetUtcNow();
        request.ClaimEnvelopeId = null;
        var application = await context.Applications.SingleAsync(row => row.Id == input.ApplicationId, ct);
        if (application.Status == ApplicationStatus.Pending && application.Revision == input.Revision && application.DeliveryRequestId == request.Id)
        {
            application.Status = ApplicationStatus.NeedsManual;
            application.Error = reason;
            notifier.Add(context, NotificationKind.ApplyFailed, $"⚠️ Отклик #{input.Vacancy.Id} остановлен: {TextUtil.EscapeHtml(reason)}", entityId: input.Vacancy.Id);
        }
    }

    private static void Validate(Guid id, int version, Guid envelopeId)
    {
        if (id == Guid.Empty || version != 1 || envelopeId == Guid.Empty) throw new UnsupportedApplicationDeliveryException();
    }
}

[NonTransactional]
public static class ApplicationDeliveryHandler
{
    public static async Task Handle(RetryApplicationDelivery command, IDbContextFactory<JobFinderDbContext> db,
        ApplicationDeliveryRequests requests, ServerOwnership ownership, CancellationToken ct)
    {
        if (command.RequestId == Guid.Empty || command.Version != 1 || command.ApplicationRevision < 0)
            throw new UnsupportedApplicationDeliveryException();
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        var request = await context.ApplicationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == command.RequestId, ct);
        if (request is null || request.Stage != ApplicationDeliveryStage.Completed) return;
        var input = ApplicationDeliverySources.Read(request);
        if (!await context.Applications.AnyAsync(row => row.Id == input.ApplicationId && row.Status == ApplicationStatus.Pending
            && row.Revision == command.ApplicationRevision && row.DeliveryRequestId == request.Id, ct)
            || !await context.DeliveryAttempts.AnyAsync(row => row.ApplicationDeliveryRequestId == request.Id
                && row.Status == DeliveryAttemptStatus.KnownNotSent && row.ApplicationRevision + 1 == command.ApplicationRevision, ct)) return;
        await requests.RequestAsync(input.ApplicationId, ct);
    }

    public static Task Handle(PrepareApplicationDelivery command, ApplicationDeliveryWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.PrepareAsync(command.RequestId, command.Version, message.Envelope?.Id ?? throw new UnsupportedApplicationDeliveryException(), ct);

    public static Task Handle(DispatchApplicationDelivery command, ApplicationDeliveryWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.DispatchAsync(command.RequestId, command.Version, message.Envelope?.Id ?? throw new UnsupportedApplicationDeliveryException(), ct);

    public static async Task Handle(RememberApplicationContact command, IDbContextFactory<JobFinderDbContext> db, ContactRegistry contacts,
        ServerOwnership ownership, CancellationToken ct)
    {
        if (command.RequestId == Guid.Empty || command.Version != 1) throw new UnsupportedApplicationDeliveryException();
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        var request = await context.ApplicationDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == command.RequestId, ct);
        var input = ApplicationDeliverySources.Read(request);
        if (input.Channel != ApplicationChannel.HeadHunter) throw new UnsupportedApplicationDeliveryException();
        if (request.Stage is not (ApplicationDeliveryStage.Completed or ApplicationDeliveryStage.OutcomeUnknown)
            || !await context.DeliveryAttempts.AnyAsync(row => row.ApplicationDeliveryRequestId == request.Id
                && row.Status != DeliveryAttemptStatus.Submitting, ct)) return;
        if (command.Email is { } email) await contacts.UpsertAsync(input.Vacancy.CompanyName, input.Vacancy.CompanyId, ContactKind.Email, email,
            command.Name, "контакт в вакансии hh", "hh", input.Vacancy.Url, ct);
        if (command.Phone is { } phone) await contacts.UpsertAsync(input.Vacancy.CompanyName, input.Vacancy.CompanyId, ContactKind.Phone, phone,
            command.Name, "контакт в вакансии hh", "hh", input.Vacancy.Url, ct);
    }
}
