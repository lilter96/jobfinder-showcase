using System.Text.Json;
using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Policies;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class VacancyAssessmentWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IVacancyAssessmentSteps steps, ServerOwnership ownership, Notifier notifier, RuntimeSettings settings,
    ContactRegistry contacts, EmailSender email, LocalClock clock, JobTriggers triggers, TimeProvider time, ILogger<VacancyAssessmentWorkflow> log,
    LetterPreparationRequests preparations, ApplicationDeliveryRequests deliveries)
{
    public const int MaxApprovalCardsPerHour = 6;

    public async Task RunAsync(Guid id, int version, VacancyAssessmentStage expected, Guid envelopeId, CancellationToken ct, bool preview = false)
    {
        if (id == Guid.Empty || envelopeId == Guid.Empty || version != 1
            || expected is not (VacancyAssessmentStage.Scoring or VacancyAssessmentStage.Lettering or VacancyAssessmentStage.Finishing))
            throw new UnsupportedVacancyAssessmentException();
        var request = await ClaimAsync(id, expected, envelopeId, ct, preview);
        if (request is null) return;
        using var stage = Telemetry.StartStage(expected.ToString().ToLowerInvariant(), "vacancy-assessment")
            .Tag("operation.id", id).Tag("vacancy.id", request.VacancyId);
        AssessmentScore? previewScore = null;
        try
        {
            var input = VacancyAssessments.ReadInput(request);
            steps.EnsureCompatible(input);
            if (input.Annotation is not null && expected == VacancyAssessmentStage.Lettering) throw new UnsupportedVacancyAssessmentException();
            if (expected == VacancyAssessmentStage.Scoring)
            {
                var score = await steps.ScoreAsync(input, ct);
                if (input.Preview is not null) previewScore = score;
                if (input.Annotation is not null ? score.Evaluation is null || score.Evaluation.Keywords is null || score.Evaluation.LegitimacyReasons is null
                    : score.Status is not (VacancyStatus.Scored or VacancyStatus.Rejected or VacancyStatus.Applied)
                        || score.Status != VacancyStatus.Rejected && (score.Score is not (>= 0 and <= 100) || score.Evaluation is null))
                    throw new InvalidOperationException("invalid vacancy score checkpoint");
                await CheckpointAsync(request, async (_, outbox, current) =>
                {
                    current.ScoreJson = JsonSerializer.Serialize(score, VacancyAssessmentJsonContext.Default.AssessmentScore);
                    if (input.Preview is not null) current.ScoreHash = CanonicalJson.Hash(current.ScoreJson);
                    current.Stage = input.Preview is null && input.Annotation is null && score.Status == VacancyStatus.Scored && score.Decision is Decision.Approval or Decision.AutoApply
                        ? VacancyAssessmentStage.Lettering : VacancyAssessmentStage.Finishing;
                    await NextAsync(outbox, current);
                }, ct);
            }
            else if (expected == VacancyAssessmentStage.Lettering)
            {
                var pending = request.LetterJson is null ? null : JsonSerializer.Deserialize(request.LetterJson, VacancyAssessmentJsonContext.Default.AssessmentLetter);
                AssessmentLetter letter;
                if (pending?.PreparationId is { } dependency)
                {
                    var ticket = await preparations.GetAsync(dependency, ct);
                    letter = new AssessmentLetter { PreparationId = dependency, Text = ticket.Result?.Text ?? "", Review = ticket.Result?.Review,
                        Problems = ticket.Result?.Problems.ToList() ?? [] };
                }
                else letter = await steps.LetterAsync(input, ReadScore(request), ct);
                if (letter.PreparationId is not null && string.IsNullOrWhiteSpace(letter.Text))
                {
                    await CheckpointAsync(request, async (_, outbox, current) =>
                    {
                        current.LetterJson = JsonSerializer.Serialize(letter, VacancyAssessmentJsonContext.Default.AssessmentLetter);
                        current.RetryAt = time.GetUtcNow().AddSeconds(5);
                        await PublishAsync(outbox, current, current.RetryAt);
                    }, ct);
                    return;
                }
                if (letter.Review is not { Score: >= LetterWriter.MinVerdict and <= 10, Problems.Count: 0 }
                    || letter.Review.TextHash != Fingerprint.Of(letter.Text))
                    letter.Problems = [.. letter.Problems, "нет действующей проверки письма ≥ 9/10"];
                await CheckpointAsync(request, async (_, outbox, current) =>
                {
                    current.LetterJson = JsonSerializer.Serialize(letter, VacancyAssessmentJsonContext.Default.AssessmentLetter);
                    current.Stage = VacancyAssessmentStage.Finishing;
                    await NextAsync(outbox, current);
                }, ct);
            }
            else
            {
                await CheckpointAsync(request, async (context, outbox, current) =>
                {
                    if (input.Preview is null) await FinishAsync(context, outbox, current, input, ct);
                    else VacancyAssessments.ReadPreviewScore(current);
                    current.Stage = VacancyAssessmentStage.Completed;
                    current.CompletedAt = time.GetUtcNow();
                }, ct);
                if (input.Annotation is null && input.Preview is null)
                {
                    triggers.Trigger(JobNames.Apply);
                    triggers.Trigger(JobNames.Resumes);
                }
            }
            log.Info($"[vacancies] #{request.VacancyId}: {expected} checkpointed");
        }
        catch (LlmUnavailableException error)
        {
            await ChangeAsync(request, async (_, outbox, current) =>
            {
                current.Error = TextUtil.Truncate(error.Message, 900);
                current.RetryAt = time.GetUtcNow().AddMinutes(error.Unauthorized ? 30 : 2);
                await PublishAsync(outbox, current, current.RetryAt);
            }, ct);
            if (error.Unauthorized || error.BudgetExceeded)
                await notifier.AlertAsync(error.Unauthorized ? "llm-key" : "llm-budget", error.Unauthorized ? "OpenRouter отклонил ключ" : "Дневной бюджет LLM исчерпан",
                    "Оценка вакансии сохранена и ждёт доступности LLM.", TimeSpan.FromHours(6), ct);
        }
        catch (Exception error) when (error is VacancyAssessmentSupersededException or LetterPreparationSupersededException)
        {
            await ChangeAsync(request, (_, _, current) =>
            {
                Cancel(current, "vacancy assessment recipe superseded");
                return Task.CompletedTask;
            }, ct);
            if (!preview) triggers.Trigger(JobNames.Vacancies);
        }
        catch (VacancyAssessmentCheckpointException) when (previewScore is not null)
        {
            await QuarantineAsync(request, ct, previewScore);
            throw;
        }
        catch (Exception error) when (error is not (OperationCanceledException or UnsupportedVacancyAssessmentException or VacancyAssessmentCheckpointException))
        {
            var terminal = false;
            await ChangeAsync(request, (context, _, current) =>
            {
                current.Error = TextUtil.Truncate(error.Message, 900);
                current.Failures++;
                var captured = VacancyAssessments.ReadInput(current);
                var vacancy = captured.Preview is null ? context.Vacancies.Local.Single(row => row.Id == current.VacancyId) : null;
                var annotation = captured.Annotation is not null;
                if (!annotation && vacancy is not null)
                {
                    vacancy.Attempts = current.Failures;
                    vacancy.LastError = current.Error;
                }
                if (current.Failures >= 4)
                {
                    terminal = true;
                    current.Stage = VacancyAssessmentStage.Failed;
                    current.CompletedAt = time.GetUtcNow();
                    if (!annotation && vacancy is not null) vacancy.Status = VacancyStatus.Failed;
                    notifier.Add(context, NotificationKind.Alert, $"⚠️ Оценка {(preview ? "превью" : "вакансии #" + current.VacancyId)} остановлена: " + TextUtil.EscapeHtml(current.Error), entityId: preview ? null : current.VacancyId);
                }
                return Task.CompletedTask;
            }, ct);
            if (!terminal) throw;
        }
    }

    private async Task<VacancyAssessmentRequest?> ClaimAsync(Guid id, VacancyAssessmentStage expected, Guid envelopeId, CancellationToken ct, bool preview)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.VacancyAssessmentRequests.AsNoTracking().SingleOrDefaultAsync(request => request.Id == id, ct);
        if (known is null || known.Stage != expected || known.RetryAt > time.GetUtcNow()) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        VacancyAssessmentInput input;
        try
        {
            input = VacancyAssessments.ReadInput(known);
            if ((input.Preview is not null) != preview) throw new UnsupportedVacancyAssessmentException();
            if (preview && expected == VacancyAssessmentStage.Finishing) VacancyAssessments.ReadPreviewScore(known);
        }
        catch (Exception error) when (error is JsonException or UnsupportedVacancyAssessmentException)
        {
            await transaction.RollbackAsync(ct);
            await QuarantineAsync(known, ct);
            throw new UnsupportedVacancyAssessmentException();
        }
        var matches = await VacancyAssessments.MatchesAsync(context, input, ct);
        var request = await context.VacancyAssessmentRequests.SingleAsync(row => row.Id == id, ct);
        if (request.Stage != expected || request.RetryAt > time.GetUtcNow()
            || request.ClaimEnvelopeId is { } owner && owner != envelopeId) return null;
        ownership.EnsureOwned();
        if (!matches)
        {
            Cancel(request, "vacancy or candidate snapshot superseded");
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            triggers.Trigger(JobNames.Vacancies);
            return null;
        }
        request.ClaimEnvelopeId = envelopeId;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return request;
    }

    private async Task ChangeAsync(VacancyAssessmentRequest before,
        Func<JobFinderDbContext, IDbContextOutbox, VacancyAssessmentRequest, Task> change, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        if (before.Stage == VacancyAssessmentStage.Finishing && before.PreviewKey is null)
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('jobfinder.vacancy-assessment.finish', 0))", ct);
        var matches = await VacancyAssessments.MatchesAsync(context, VacancyAssessments.ReadInput(before), ct);
        var current = await context.VacancyAssessmentRequests.SingleAsync(request => request.Id == before.Id, ct);
        if (current.Stage != before.Stage || current.ClaimEnvelopeId != before.ClaimEnvelopeId) return;
        if (before.PreviewKey is not null && (current.InputHash != before.InputHash || current.InputHash != CanonicalJson.Hash(current.InputJson)))
            throw new UnsupportedVacancyAssessmentException();
        ownership.EnsureOwned();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        if (matches) await change(context, outbox, current);
        else Cancel(current, "vacancy or candidate snapshot superseded");
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        if (!matches) triggers.Trigger(JobNames.Vacancies);
    }

    private async Task CheckpointAsync(VacancyAssessmentRequest before,
        Func<JobFinderDbContext, IDbContextOutbox, VacancyAssessmentRequest, Task> change, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await ChangeAsync(before, change, ct); return; }
            catch (Exception error) when (error is IOException or DbException or DbUpdateException)
            {
                if (attempt >= 2) throw new VacancyAssessmentCheckpointException(error);
                ownership.EnsureOwned();
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
            }
        }
    }

    private async Task QuarantineAsync(VacancyAssessmentRequest known, CancellationToken ct, AssessmentScore? retainedScore = null)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        if (known.PreviewKey is null) await Modules.Hiring.DeliveryLedger.LockVacancyAsync(context, known.VacancyId, ct);
        else await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"jobfinder.vacancy-preview:" + known.PreviewKey},0))", ct);
        var request = await context.VacancyAssessmentRequests.FromSqlInterpolated($"SELECT * FROM vacancy_assessment_requests WHERE id = {known.Id} FOR UPDATE").SingleAsync(ct);
        if (request.Stage != known.Stage || request.InputHash != known.InputHash) return;
        request.Stage = VacancyAssessmentStage.Failed;
        request.CompletedAt = time.GetUtcNow();
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        if (retainedScore is not null)
        {
            request.ScoreJson = JsonSerializer.Serialize(retainedScore, VacancyAssessmentJsonContext.Default.AssessmentScore);
            request.ScoreHash = CanonicalJson.Hash(request.ScoreJson);
        }
        request.Error = retainedScore is null ? "invalid or unsupported saved assessment snapshot; quarantined without paid work"
            : "paid preview score retained after exhausted persistence; automatic paid rework refused";
        notifier.Add(context, NotificationKind.Alert, known.PreviewKey is null
            ? $"⚠️ Повреждённая оценка вакансии #{request.VacancyId} изолирована; вакансия может быть оценена заново."
            : "⚠️ Превью оценки изолировано; сохранённые исходные данные и платный результат не удалены.", entityId: known.PreviewKey is null ? request.VacancyId : null);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
        if (known.PreviewKey is null) triggers.Trigger(JobNames.Vacancies);
    }

    private async Task FinishAsync(JobFinderDbContext context, IDbContextOutbox outbox, VacancyAssessmentRequest request, VacancyAssessmentInput input, CancellationToken ct)
    {
        var vacancy = context.Vacancies.Local.Single(row => row.Id == request.VacancyId);
        await context.Entry(vacancy).Collection(row => row.Sources).LoadAsync(ct);
        var score = ReadScore(request);
        if (input.Annotation is { } annotation)
        {
            if (vacancy.AssessmentRevision == long.MaxValue || score.Evaluation is not { Keywords: not null, LegitimacyReasons: not null } evaluation
                || !Enum.IsDefined(score.Legitimacy)) throw new InvalidOperationException("invalid evaluation annotation checkpoint");
            vacancy.Evaluation = annotation.Evaluation with
            {
                Keywords = evaluation.Keywords, Language = evaluation.Language,
                LegitimacyReasons = evaluation.LegitimacyReasons, MarketNote = evaluation.MarketNote,
            };
            vacancy.Legitimacy = score.Legitimacy;
            vacancy.AssessmentRevision++;
            vacancy.UpdatedAt = time.GetUtcNow();
            notifier.Add(context, NotificationKind.Info, $"Обновлены ATS-данные вакансии #{vacancy.Id}; оценка и история откликов сохранены.", entityId: vacancy.Id);
            return;
        }
        vacancy.Evaluation = score.Evaluation;
        vacancy.Score = score.Score;
        vacancy.LlmScore = score.Evaluation?.Score;
        vacancy.SalaryAdjustment = input.SalaryAdjustment;
        vacancy.EnglishRequired = score.EnglishRequired;
        vacancy.Legitimacy = score.Legitimacy;
        vacancy.RejectReason = score.RejectReason;
        vacancy.Decision = score.Decision;
        vacancy.Status = score.Status;
        vacancy.Attempts = 0;
        vacancy.LastError = null;
        vacancy.ScoredAt = score.Evaluation is null ? null : time.GetUtcNow();
        vacancy.UpdatedAt = time.GetUtcNow();
        if (await VacancyAssessments.RespondedAsync(context, vacancy, ct))
        {
            vacancy.Status = VacancyStatus.Applied;
            vacancy.Decision = Decision.None;
            if (!await context.Applications.AnyAsync(application => application.VacancyId == vacancy.Id && application.Status == ApplicationStatus.Sent, ct))
            {
                var hhId = ApplyRoutes.HhId(vacancy);
                var response = await context.HhResponses.Where(row => hhId != null && row.HhVacancyId == hhId || row.Fingerprint == vacancy.Fingerprint)
                    .OrderByDescending(row => row.RespondedAt).FirstAsync(ct);
                await context.Entry(vacancy).Collection(row => row.Applications).LoadAsync(ct);
                HhHistory.Link(context, vacancy, response, HhHistory.StageOf(response.State, response.ViewedByEmployer) ?? ApplicationStage.Sent, time.GetUtcNow());
            }
            return;
        }
        if (vacancy.Status != VacancyStatus.Scored) return;
        var searchDocument = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {SearchPolicyStore.Key} FOR SHARE").SingleAsync(ct);
        var searchPolicy = JsonSerializer.Deserialize(searchDocument.Content, SearchPolicyJsonContext.Default.SearchPolicy)
            ?? throw new JsonException("missing search policy");
        var restriction = new SearchPolicySnapshot(searchPolicy, searchDocument.Version).Filter().Check(vacancy, alreadyApplied: false);
        if (!restriction.Passed)
        {
            vacancy.Status = VacancyStatus.Rejected;
            vacancy.RejectReason = restriction.Reason;
            vacancy.Decision = Decision.None;
            return;
        }
        if (vacancy.Decision is not (Decision.Approval or Decision.AutoApply)) return;
        var letter = request.LetterJson is null ? null : JsonSerializer.Deserialize(request.LetterJson, VacancyAssessmentJsonContext.Default.AssessmentLetter);
        vacancy.LetterRevision++;
        vacancy.CoverLetter = letter?.Text;
        vacancy.LetterReview = letter?.Review;
        var problems = letter?.Problems ?? ["нет проверенного письма"];
        var policyDocument = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {AutomationPolicyStore.Key} FOR SHARE").SingleAsync(ct);
        var policy = JsonSerializer.Deserialize(policyDocument.Content, AutomationPolicyJsonContext.Default.AutomationPolicy)
            ?? throw new JsonException("missing automation policy");
        var today = await context.Applications.CountAsync(ApplyWorker.CountsTowardAutoLimit(clock.StartOfTodayUtc()), ct);
        var blockedUntil = await settings.GetInstantAsync(RuntimeSettings.ApplyBlockedUntil, ct);
        var allowed = DecisionPolicy.Decide(new DecisionInput(vacancy.Score!.Value, vacancy.Decision == Decision.AutoApply
            && problems.Count == 0 && (blockedUntil is null || blockedUntil <= time.GetUtcNow()) && ApplyRoutes.HhApplicable(vacancy), today),
            new DecisionSettings(Math.Max(input.Automation.AutoApplyThreshold, policy.AutoApplyThreshold), policy.ApprovalThreshold,
                Math.Min(input.Automation.DailyAutoApplyLimit, policy.DailyAutoApplyLimit), policy.AutoApplyPaused));
        if (allowed != Decision.AutoApply) vacancy.Decision = allowed;
        if (vacancy.Decision == Decision.Ignore) return;
        if (vacancy.Decision == Decision.AutoApply)
        {
            var application = new Application
            {
                VacancyId = vacancy.Id, Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.Auto,
                Status = ApplicationStatus.Pending, CoverLetter = vacancy.CoverLetter, CreatedAt = time.GetUtcNow(),
            };
            context.Applications.Add(application);
            await deliveries.QueueAsync(context, outbox, application, ct);
        }
        else
        {
            var hourAgo = time.GetUtcNow().AddHours(-1);
            var recent = await context.Notifications.CountAsync(notification => notification.Kind == NotificationKind.VacancyApproval && notification.CreatedAt > hourAgo, ct);
            if (recent < MaxApprovalCardsPerHour)
            {
                var recruiter = (await contacts.ForCompanyAsync(vacancy.CompanyName, ct)).Where(contact => contact.Kind == ContactKind.Telegram)
                    .OrderByDescending(contact => contact.Source == ContactRegistry.ManualSource).FirstOrDefault();
                var (text, buttons) = Cards.VacancyApproval(vacancy, ApplyRoutes.Resolve(vacancy, email.IsConfigured), recruiter, problems);
                notifier.Add(context, NotificationKind.VacancyApproval, text, buttons, vacancy.Id);
            }
        }
        vacancy.Notified = true;
    }

    private static AssessmentScore ReadScore(VacancyAssessmentRequest request) => request.ScoreJson is null
        ? throw new JsonException("missing vacancy score checkpoint")
        : JsonSerializer.Deserialize(request.ScoreJson, VacancyAssessmentJsonContext.Default.AssessmentScore) ?? throw new JsonException("missing vacancy score checkpoint");

    private void Cancel(VacancyAssessmentRequest request, string reason)
    {
        request.Stage = VacancyAssessmentStage.Cancelled;
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        request.CompletedAt = time.GetUtcNow();
        request.Error = reason;
    }

    private static ValueTask NextAsync(IDbContextOutbox outbox, VacancyAssessmentRequest request)
    {
        request.Failures = 0;
        return PublishAsync(outbox, request);
    }

    private static ValueTask PublishAsync(IDbContextOutbox outbox, VacancyAssessmentRequest request, DateTimeOffset? scheduled = null)
    {
        object command = request.Stage switch
        {
            VacancyAssessmentStage.Scoring when request.PreviewKey is not null => new ScoreVacancyPreview { RequestId = request.Id },
            VacancyAssessmentStage.Scoring => new ScoreVacancy { RequestId = request.Id },
            VacancyAssessmentStage.Lettering => new PrepareVacancyLetter { RequestId = request.Id },
            VacancyAssessmentStage.Finishing when request.PreviewKey is not null => new FinishVacancyPreview { RequestId = request.Id },
            VacancyAssessmentStage.Finishing => new FinishVacancyAssessment { RequestId = request.Id },
            _ => throw new UnsupportedVacancyAssessmentException(),
        };
        return outbox.PublishAsync(command, new DeliveryOptions { ScheduledTime = scheduled });
    }
}

[NonTransactional]
public static class VacancyAssessmentHandler
{
    public static Task Handle(ScoreVacancyPreview command, VacancyAssessmentWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Version, VacancyAssessmentStage.Scoring, EnvelopeId(message), ct, preview: true);
    public static Task Handle(FinishVacancyPreview command, VacancyAssessmentWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Version, VacancyAssessmentStage.Finishing, EnvelopeId(message), ct, preview: true);
    public static Task Handle(ScoreVacancy command, VacancyAssessmentWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Version, VacancyAssessmentStage.Scoring, EnvelopeId(message), ct);
    public static Task Handle(PrepareVacancyLetter command, VacancyAssessmentWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Version, VacancyAssessmentStage.Lettering, EnvelopeId(message), ct);
    public static Task Handle(FinishVacancyAssessment command, VacancyAssessmentWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Version, VacancyAssessmentStage.Finishing, EnvelopeId(message), ct);

    private static Guid EnvelopeId(IMessageContext message) => message.Envelope?.Id ?? throw new UnsupportedVacancyAssessmentException();
}
