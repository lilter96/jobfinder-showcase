using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Profile;
using System.Collections.Concurrent;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record ApprovedDispatch(Application Application, string Text, ApplicationLetterReview Review,
    EmailAttachment? Resume, string? Subject, bool DryRun, EmployerMessage? Message = null, EmployerMessageInput? Input = null,
    Guid? ApplicationDeliveryRequestId = null);

public abstract record EmployerMessagePreparation
{
    public sealed record ReviewPending(Guid RequestId) : EmployerMessagePreparation;
    public sealed record ResumePending(Guid RequestId, ApplicationLetterReview Review, Guid? ReviewRequestId = null) : EmployerMessagePreparation;
    public sealed record Ready(ApprovedDispatch Dispatch, Guid? ReviewRequestId = null) : EmployerMessagePreparation;
}

public sealed record DeliveryReceipt(ApplicationStatus Status, string? Error = null, string? Screenshot = null,
    long? TargetPeerId = null, string? EmailMessageId = null, HhContact? Contact = null, bool LetterRequired = false);

public interface IDeliveryTransport
{
    Task<DeliveryReceipt> SendAsync(ApprovedDispatch dispatch, CancellationToken ct);
}

public interface IEmployerMessageGate
{
    Task<EmployerMessagePreparation> PrepareAsync(EmployerMessage message, EmployerMessageInput input, Vacancy vacancy, CancellationToken ct);
    Task<ApprovedDispatch> RestoreAsync(EmployerMessage message, EmployerMessageInput input, Vacancy vacancy, CancellationToken ct);
}

public interface IApplicationDeliveryGate
{
    Task<EmployerMessagePreparation> PrepareAsync(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, CancellationToken ct);
    Task<ApprovedDispatch> RestoreAsync(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, CancellationToken ct);
}

public sealed class EmployerContentGate(LetterWriter writer, ResumeTailor tailor, ProfileProvider profiles, JobFinder.Llm.Tasks.JobFinderLlm? llm = null)
    : IEmployerMessageGate, IApplicationDeliveryGate
{
    public async Task<EmployerMessagePreparation> PrepareAsync(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, CancellationToken ct)
    {
        ApplicationDeliverySources.EnsureCompatible(input, llm ?? throw new InvalidOperationException("native application operation is required"));
        var brief = ApplicationDeliverySources.Brief(input);
        brief.Vacancy.LetterReview = input.CachedReview;
        var review = request.Review;
        var reviewId = request.LetterReviewRequestId;
        try
        {
            if (review is null)
            {
                var ticket = reviewId is { } id ? new LetterReviewTicket(id, await writer.GetReviewAsync(id, brief, input.Text, ct))
                    : await writer.RequestReviewAsync(brief, input.Text, input.Profile, input.Facts, llm!.RestoreOperation(input.Operation), ct);
                review = ticket.Review;
                reviewId = ticket.RequestId;
                if (review is null) return new EmployerMessagePreparation.ReviewPending(reviewId ?? throw new ResumeQualityException("нет проверки письма"));
            }
            ValidateInitialReview(input, review);
            EmailAttachment? resume = null;
            if (input.Channel is ApplicationChannel.Email or ApplicationChannel.TelegramDm)
            {
                if (request.ResumeRequestId is { } dependency)
                {
                    resume = await tailor.PreparedAttachmentAsync(input.Vacancy.Id, dependency, ct);
                    if (resume is null) return new EmployerMessagePreparation.ResumePending(dependency, review, reviewId);
                }
                else
                {
                    var ticket = await tailor.RequestAsync(input.Vacancy.Id, ct);
                    if (ticket.Cached is not { } cached) return new EmployerMessagePreparation.ResumePending(
                        ticket.RequestId ?? throw new ResumeQualityException("нет подготовки резюме"), review, reviewId);
                    resume = await tailor.CheckedAttachmentAsync(input.Vacancy.Id, cached.Id, ct);
                }
            }
            return new EmployerMessagePreparation.Ready(InitialPacket(request, input, application, review, resume), reviewId);
        }
        catch (Exception error) when (error is LetterPreparationSupersededException or LetterPreparationFailedException or ApplicationDeliverySupersededException)
        { throw new ResumeQualityException(error.Message); }
    }

    public async Task<ApprovedDispatch> RestoreAsync(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, CancellationToken ct)
    {
        ApplicationDeliverySources.EnsureCompatible(input, llm ?? throw new InvalidOperationException("native application operation is required"));
        var review = request.Review ?? throw new ResumeQualityException("нет проверки письма");
        ValidateInitialReview(input, review);
        var resume = input.Channel is ApplicationChannel.Email or ApplicationChannel.TelegramDm
            ? await tailor.CheckedAttachmentAsync(input.Vacancy.Id, request.TailoredResumeId ?? throw new ResumeQualityException("нет сохранённого резюме"), ct) : null;
        if (resume?.Sha256 != request.ResumeHash) throw new ResumeQualityException("вложение изменилось после подготовки");
        return InitialPacket(request, input, application, review, resume);
    }

    private static ApprovedDispatch InitialPacket(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application,
        ApplicationLetterReview review, EmailAttachment? resume) => new(application, input.Text, review, resume,
            input.Channel == ApplicationChannel.Email ? $"Отклик на вакансию «{input.Vacancy.Title}» — {input.Profile.Name}" : null,
            input.Automation.ApplyDryRun, ApplicationDeliveryRequestId: request.Id);

    internal static void ValidateInitialReview(ApplicationDeliveryInput input, ApplicationLetterReview review)
    {
        if (!LetterWriter.Matches(review, input.Text, input.ReviewContextHash)) throw new ResumeQualityException("нет проверки точного текста отклика ≥ 9/10");
    }
    public async Task<EmployerMessagePreparation> PrepareAsync(EmployerMessage message, EmployerMessageInput input, Vacancy vacancy, CancellationToken ct)
    {
        var channel = input.Channel switch
        {
            ApplicationChannel.TelegramDm => LetterChannel.Telegram,
            ApplicationChannel.Email => LetterChannel.Email,
            _ => LetterChannel.Hh,
        };
        var review = message.Review;
        var reviewRequestId = message.LetterReviewRequestId;
        if (review is null)
        {
            var ticket = await ReviewAsync(message, new LetterBrief(vacancy, channel, input.Contact, input.Situation,
                input.AttachResume, input.Conversation, input.First, input.NeedsFact ?? input.First, Deep: true), input.Text, ct);
            review = ticket.Review;
            reviewRequestId = ticket.RequestId;
            if (review is null) return new EmployerMessagePreparation.ReviewPending(ticket.RequestId ?? throw new ResumeQualityException("нет запроса проверки письма"));
        }
        if (review.Score is < LetterWriter.MinVerdict or > 10 || review.Problems.Count != 0)
            throw new ResumeQualityException("сообщение не прошло проверку: " + string.Join("; ", review.Problems));
        EmailAttachment? resume = null;
        if (input.AttachResume)
        {
            if (message.ResumeRequestId is { } dependency)
            {
                resume = await tailor.PreparedAttachmentAsync(input.VacancyId, dependency, ct);
                if (resume is null) return new EmployerMessagePreparation.ResumePending(dependency, review, reviewRequestId);
            }
            else
            {
                var ticket = await tailor.RequestAsync(input.VacancyId, ct);
                if (ticket.Cached is not { } cached)
                    return new EmployerMessagePreparation.ResumePending(ticket.RequestId ?? throw new ResumeQualityException("нет запроса подготовки резюме"), review, reviewRequestId);
                resume = await tailor.CheckedAttachmentAsync(input.VacancyId, cached.Id, ct);
            }
        }
        return new EmployerMessagePreparation.Ready(Describe(message, input, vacancy, review, resume), reviewRequestId);
    }

    private async Task<LetterReviewTicket> ReviewAsync(EmployerMessage message, LetterBrief brief, string text, CancellationToken ct)
    {
        try
        {
            return message.LetterReviewRequestId is { } id ? new(id, await writer.GetReviewAsync(id, brief, text, ct))
                : await writer.RequestReviewAsync(brief, text, ct);
        }
        catch (Exception error) when (error is LetterPreparationSupersededException or LetterPreparationFailedException)
        { throw new ResumeQualityException(error.Message); }
    }

    public async Task<ApprovedDispatch> RestoreAsync(EmployerMessage message, EmployerMessageInput input, Vacancy vacancy, CancellationToken ct)
    {
        var resume = input.AttachResume ? await tailor.CheckedAttachmentAsync(input.VacancyId,
            message.TailoredResumeId ?? throw new ResumeQualityException("нет сохранённого резюме"), ct) : null;
        if (resume?.Sha256 != message.ResumeHash) throw new ResumeQualityException("вложение изменилось после подготовки");
        return Describe(message, input, vacancy, message.Review ?? throw new ResumeQualityException("нет проверки письма"), resume);
    }

    private static ApprovedDispatch Describe(EmployerMessage message, EmployerMessageInput input, Vacancy vacancy,
        ApplicationLetterReview review, EmailAttachment? resume) => new(new Application
        {
            Id = input.ApplicationId ?? 0, VacancyId = input.VacancyId, Vacancy = vacancy, Channel = input.Channel,
            Target = input.Target, CoverLetter = input.Text, CreatedAt = message.CreatedAt,
        }, input.Text, review, resume, input.Subject, false, message, input);

    public async Task<ApprovedDispatch> PrepareAsync(Application application, bool dryRun, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("prepare", "delivery").Tag("application.id", application.Id);
        var text = Workers.ApplyWorker.LetterOf(application);
        ApplicationLetterReview review;
        try { review = await writer.ReviewResponseAsync(application.Vacancy!, application.Channel, application.Target, text, ct); }
        catch (Exception error) when (error is LetterPreparationSupersededException or LetterPreparationFailedException)
        { throw new ResumeQualityException(error.Message); }
        if (review.Problems.Count > 0 || review.Score < LetterWriter.MinVerdict)
            throw new ResumeQualityException("письмо не прошло проверку: " + string.Join("; ", review.Problems));
        var resume = application.Channel is ApplicationChannel.Email or ApplicationChannel.TelegramDm
            ? await tailor.AttachmentAsync(application.VacancyId, ct) ?? throw new ResumeQualityException("нет проверенного резюме")
            : null;
        var profile = application.Channel == ApplicationChannel.Email ? await profiles.GetAsync(ct) : null;
        var subject = application.Channel == ApplicationChannel.Email
            ? $"Отклик на вакансию «{application.Vacancy!.Title}»" + (string.IsNullOrWhiteSpace(profile?.Name) ? "" : $" — {profile.Name}")
            : null;
        return new ApprovedDispatch(application, text, review, resume, subject, dryRun);
    }
}

public sealed class DeliveryGateway(DeliveryLedger ledger, IDeliveryTransport transport, ILogger<DeliveryGateway> log, ServerOwnership ownership)
{
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public bool IsSending(Guid messageId) => _inFlight.ContainsKey(messageId);

    public async Task<DeliveryReceipt?> DispatchAsync(ApprovedDispatch dispatch,
        Action<JobFinderDbContext, Application, DeliveryReceipt>? record, CancellationToken ct)
    {
        var messageId = dispatch.Message?.Id ?? dispatch.ApplicationDeliveryRequestId;
        if (messageId is { } id && !_inFlight.TryAdd(id, 0)) return null;
        try
        {
            using var stage = Telemetry.StartStage("send", "delivery").Tag("application.id", dispatch.Application.Id);
            ownership.EnsureOwned();
            var attemptId = await ledger.TryClaimAsync(dispatch, ct);
            if (attemptId is null) return null;
            stage.Tag("delivery.attempt", attemptId);
            DeliveryReceipt receipt;
            try
            {
                ownership.EnsureOwned();
                receipt = await transport.SendAsync(dispatch, ct);
            }
            catch (Exception error)
            {
                stage.Fail(error);
                log.Warn($"[delivery] #{dispatch.Application.Id}: outcome unknown ({error.Message})");
                receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown, error.Message);
            }
            using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var completed = await ledger.CompleteAsync(attemptId.Value, receipt, record, persistenceTimeout.Token);
            if (!completed) throw new InvalidOperationException("delivery receipt was not committed; sending must be reconciled before retry");
            return receipt;
        }
        finally
        {
            if (messageId is { } completedId) _inFlight.TryRemove(completedId, out _);
        }
    }
}

public sealed class EmployerDeliveryTransport(HhApplier applier, HhChat chat, TelegramUserClient telegram, EmailSender email,
    Settings.RuntimeSettings settings, ServerOwnership ownership) : IDeliveryTransport
{
    public async Task<DeliveryReceipt> SendAsync(ApprovedDispatch dispatch, CancellationToken ct)
    {
        ownership.EnsureOwned();
        ct.ThrowIfCancellationRequested();
        var application = dispatch.Application;
        var dryRun = dispatch.DryRun || (dispatch.Input is null && await settings.GetBoolAsync(Settings.RuntimeSettings.ApplyDryRun, false, ct));
        if (dryRun && application.Channel != ApplicationChannel.HeadHunter) return new DeliveryReceipt(ApplicationStatus.DryRun, "nothing sent");
        if (dispatch.Input is { Automatic: true } && !await settings.GetBoolAsync(Settings.RuntimeSettings.ChatAutoReply, false, ct))
            return new DeliveryReceipt(ApplicationStatus.NeedsManual, "автоматические ответы выключены");
        switch (application.Channel)
        {
            case ApplicationChannel.TelegramDm:
                if (!telegram.IsReady) return new DeliveryReceipt(ApplicationStatus.NeedsManual, "Telegram не подключён; ничего не отправлено");
                var peer = await telegram.SendDirectMessageAsync(application.Target ?? throw new InvalidOperationException("no DM target"),
                    dispatch.Text, dispatch.Resume?.Path, dispatch.Resume?.FileName, dispatch.Resume?.Content);
                return new DeliveryReceipt(ApplicationStatus.Sent, TargetPeerId: peer);
            case ApplicationChannel.Email:
                if (!email.IsConfigured) return new DeliveryReceipt(ApplicationStatus.NeedsManual, "почта не настроена; ничего не отправлено");
                var messageId = await email.SendAsync(application.Target ?? throw new InvalidOperationException("no email target"),
                    dispatch.Subject!, dispatch.Text, dispatch.Resume, ct, inReplyTo: dispatch.Input?.InReplyTo);
                return new DeliveryReceipt(ApplicationStatus.Sent, EmailMessageId: messageId);
            case ApplicationChannel.HeadHunter:
                if (dispatch.Input?.HhChatId is { } chatId)
                {
                    if (!await applier.HasSessionAsync(ct)) return new DeliveryReceipt(ApplicationStatus.NeedsManual, "нет сессии hh; ничего не отправлено");
                    var sent = await chat.SendAsync(chatId, dispatch.Input.HhVacancyId, dispatch.Text,
                        dispatch.Input.Kind is EmployerMessageKind.HhLetter || dispatch.Input.Kind == EmployerMessageKind.HhChat && dispatch.Input.First, ct);
                    return new DeliveryReceipt(sent.Ok ? ApplicationStatus.Sent
                        : sent.OutcomeUnknown ? ApplicationStatus.OutcomeUnknown : ApplicationStatus.NeedsManual, sent.Message, sent.Screenshot);
                }
                if (dispatch.Input is not null) return new DeliveryReceipt(ApplicationStatus.NeedsManual, "нет чата hh; ничего не отправлено");
                var hhId = Workers.ApplyRoutes.HhId(application.Vacancy!);
                if (hhId is null) return new DeliveryReceipt(ApplicationStatus.NeedsManual, "у вакансии нет hh-источника");
                if (!await applier.HasSessionAsync(ct)) return new DeliveryReceipt(ApplicationStatus.Pending, "нет сессии hh");
                var result = await applier.ApplyAsync(hhId.Value, dispatch.Text, dryRun, ct);
                return new DeliveryReceipt(result.Outcome switch
                {
                    ApplyOutcome.Sent or ApplyOutcome.AlreadyApplied => ApplicationStatus.Sent,
                    ApplyOutcome.DryRun => ApplicationStatus.DryRun,
                    ApplyOutcome.SessionExpired or ApplyOutcome.Blocked => ApplicationStatus.Pending,
                    ApplyOutcome.NeedsManual => ApplicationStatus.NeedsManual,
                    ApplyOutcome.Closed => ApplicationStatus.Cancelled,
                    _ => ApplicationStatus.OutcomeUnknown,
                }, result.Message, result.Screenshot, Contact: result.Contact, LetterRequired: result.LetterRequired);
            default:
                return new DeliveryReceipt(ApplicationStatus.NeedsManual, "канал требует ручной отправки");
        }
    }
}
