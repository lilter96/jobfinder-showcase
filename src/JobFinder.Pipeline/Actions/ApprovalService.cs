using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Actions;

public sealed record ActionResult(bool Ok, string Message)
{
    public static ActionResult Done(string message) => new(true, message);

    public static ActionResult Fail(string message) => new(false, message);
}

/// <summary>
/// Everything the user decides — from the bot or the dashboard, same code: apply / skip / edit the letter, pick a comment, approve
/// channels. Each action re-reads the current state, so a double click or a stale card cannot send twice.
/// </summary>
public sealed class ApprovalService(IDbContextFactory<JobFinderDbContext> db, Career.LetterWriter writer, ProfileProvider profiles, EmailSender email,
    Career.FunnelService funnel, RuntimeSettings settings, JobTriggers triggers, TimeProvider time, ILogger<ApprovalService> log,
    Modules.Discovery.VacancyAssessmentCapture captures, ApplicationDeliveryRequests deliveries, Modules.Engagement.CommentDecisions comments,
    Notifier notifier, Modules.Discovery.ChannelDecisions channelDecisions)
{
    private const int RefreshAttempts = 5;

    /// <summary>Vacancies refreshed at once: five parallel writers stay under the providers' rate limits and Telegram's card-edit pace.</summary>
    private const int RefreshParallelism = 5;

    private static readonly TimeSpan RefreshPause = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The letters of vacancies still waiting for a decision, written again by the current writer: approving would otherwise send the
    /// letter an older writer made. Without ids — the ones seen within <paramref name="days"/>, the best scored first. The cards are the
    /// caller's (the CLI edits them in place). Vacancies run <see cref="RefreshParallelism"/> at a time: LLM calls are thread-safe and the
    /// wait is the models, not the CPU.
    /// </summary>
    /// <returns>Per vacancy: the id and what came of it.</returns>
    public async Task<List<(long Id, string Result)>> RefreshLettersAsync(IReadOnlyCollection<long> ids, int days, CancellationToken ct,
        Func<long, IReadOnlyList<string>, Task>? refreshed = null)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var since = time.GetUtcNow().AddDays(-days);
        var waiting = await ctx.Vacancies.AsNoTracking()
            .Where(v => v.Status == VacancyStatus.Scored && v.Decision == Decision.Approval && !v.Applications.Any())
            .Where(v => ids.Count > 0 ? ids.Contains(v.Id) : v.FirstSeenAt >= since)
            .OrderByDescending(v => v.Score).Select(v => v.Id).ToListAsync(ct);
        var results = new (long Id, string Result)?[waiting.Count];
        using var cardGate = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(waiting.Select((id, i) => (id, i)), new ParallelOptions { MaxDegreeOfParallelism = RefreshParallelism, CancellationToken = ct },
            async (item, parallelCt) =>
            {
                // A network blip (every model unreachable at once) must not end a batch of hours: wait, try the same vacancy again, and after
                // five failures leave it with its old letter and go on.
                (ActionResult Result, IReadOnlyList<string> Problems)? regenerated = null;
                string? skipped = null;
                for (var attempt = 1; regenerated is null; attempt++)
                {
                    try
                    {
                        regenerated = await RegenerateLetterAsync(item.id, parallelCt);
                    }
                    catch (JobFinder.Llm.Client.LlmUnavailableException e) when (attempt < RefreshAttempts)
                    {
                        log.Warn($"[approval] #{item.id}: the models are unavailable ({e.Message}), retry {attempt}/{RefreshAttempts - 1} in {RefreshPause.TotalMinutes:0} min");
                        await Task.Delay(RefreshPause, time, parallelCt);
                    }
                    catch (JobFinder.Llm.Client.LlmUnavailableException e)
                    {
                        log.Warn($"[approval] #{item.id}: skipped, the models stayed unavailable ({e.Message})");
                        skipped = "пропущено: модели недоступны";
                        break;
                    }
                }

                if (regenerated is not { } r)
                {
                    results[item.i] = (item.id, skipped ?? "пропущено");
                    return;
                }

                if (r.Result.Ok && refreshed is not null)
                {
                    await cardGate.WaitAsync(parallelCt);
                    try
                    {
                        await refreshed(item.id, r.Problems);
                    }
                    finally
                    {
                        cardGate.Release();
                    }
                }
                log.Info($"[approval] #{item.id}: letter refreshed ({r.Result.Message})");
                results[item.i] = (item.id, r.Result.Message);
            });

        return results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
    }

    public async Task<ActionResult> ApplyAsync(long vacancyId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.AsNoTracking().Include(x => x.Sources).Include(x => x.Company).Include(x => x.Applications).FirstOrDefaultAsync(x => x.Id == vacancyId, ct);
        if (v is null) return ActionResult.Fail("вакансия не найдена");
        if (v.Applications.Any(a => a.Status is ApplicationStatus.Pending or ApplicationStatus.Sending or ApplicationStatus.Sent or ApplicationStatus.OutcomeUnknown))
            return ActionResult.Fail("отклик уже отправлен или в очереди");
        if (v.Applications.Any(a => a.Status == ApplicationStatus.DryRun))
            return ActionResult.Fail("отклик проверен в dry-run и ждёт: уйдёт сам после /dryrun off");

        var route = ApplyRoutes.Resolve(v, email.IsConfigured);
        if (route.Channel is not { } channel)
            return ActionResult.Fail(ApplyRoutes.EmailContact(v.Contacts) is not null && !email.IsConfigured
                ? "в вакансии только e-mail, а почта не настроена (Email:Username / Email:Password) — откликнитесь по ссылке"
                : "автоматический отклик невозможен (нет hh-источника без теста, @контакта или e-mail) — откликнитесь по ссылке");
        var target = route.Target;
        var originalLetter = v.CoverLetter;
        var originalLetterRevision = v.LetterRevision;
        var originalStatus = v.Status;
        if (route.AttachesResume && ApplyRoutes.Resume(profiles, await profiles.GetAsync(ct)) is null)
            return ActionResult.Fail("нет файла резюме (profile/resume.pdf) — без него отклик в ЛС или на почту не отправляется");

        try
        {
            if (string.IsNullOrWhiteSpace(v.CoverLetter))
            {
                var draft = await writer.ResponseAsync(v, channel, target, ct);
                v.CoverLetter = draft?.Text;
                v.LetterReview = draft?.Review;
            }
            if (string.IsNullOrWhiteSpace(v.CoverLetter)) return ActionResult.Fail("нет проверенного письма");
            v.LetterReview = await writer.ReviewResponseAsync(v, channel, target, v.CoverLetter, ct);
        }
        catch (Exception error) when (error is Modules.Hiring.LetterPreparationSupersededException or Modules.Hiring.LetterPreparationFailedException)
        {
            return ActionResult.Fail("проверка письма остановлена: " + error.Message);
        }
        var reviewedLetter = v.CoverLetter;
        var review = v.LetterReview;
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var current = await DeliveryLedger.LockVacancyAsync(ctx, vacancyId, ct);
        if (current is null) return ActionResult.Fail("вакансия не найдена");
        if (current.CoverLetter != originalLetter || current.LetterRevision != originalLetterRevision || current.Status != originalStatus)
            return ActionResult.Fail("вакансия или письмо изменились во время проверки; отклик не поставлен в очередь");
        if (await ctx.Applications.AnyAsync(application => application.VacancyId == vacancyId
                && (application.Status == ApplicationStatus.Pending || application.Status == ApplicationStatus.Sending
                    || application.Status == ApplicationStatus.Sent || application.Status == ApplicationStatus.DryRun
                    || application.Status == ApplicationStatus.OutcomeUnknown), ct))
            return ActionResult.Fail("отклик уже отправлен, в очереди или требует сверки доставки");
        v = current;
        if (v.CoverLetter != reviewedLetter) v.LetterRevision++;
        v.CoverLetter = reviewedLetter;
        v.LetterReview = review;
        await ctx.SaveChangesAsync(ct);
        if (v.LetterReview.Problems.Count > 0 || v.LetterReview.Score < Career.LetterWriter.MinVerdict)
        {
            await transaction.CommitAsync(ct);
            return ActionResult.Fail("письмо не прошло проверку: " + string.Join("; ", v.LetterReview.Problems));
        }

        var application = new Application
        {
            VacancyId = v.Id,
            Channel = channel,
            Mode = ApplicationMode.Approved,
            Status = ApplicationStatus.Pending,
            CoverLetter = v.CoverLetter,
            Target = target,
            CreatedAt = time.GetUtcNow(),
        };
        ctx.Applications.Add(application);
        v.UpdatedAt = time.GetUtcNow();
        await deliveries.SaveAsync(ctx, application, ct);
        triggers.Trigger(JobNames.Apply);
        log.Info($"[approve] #{v.Id} {v.Title}: {channel} queued");
        // Said at the moment of the press: a forgotten dry-run otherwise shows up only as a card later.
        var dryRun = channel == ApplicationChannel.HeadHunter && await settings.GetBoolAsync(RuntimeSettings.ApplyDryRun, false, ct);
        return ActionResult.Done(channel switch
        {
            ApplicationChannel.TelegramDm => $"сообщение {target} с резюме в очереди",
            ApplicationChannel.Email => $"письмо на {target} с резюме в очереди",
            _ when dryRun => "🧪 включён dry-run: отклик только проверится и подождёт /dryrun off",
            _ => "отклик в очереди",
        });
    }

    public async Task<ActionResult> DismissAsync(long vacancyId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var v = await DeliveryLedger.LockVacancyAsync(ctx, vacancyId, ct);
        if (v is null) return ActionResult.Fail("вакансия не найдена");
        if (await ctx.Applications.AnyAsync(application => application.VacancyId == vacancyId
                && (application.Status == ApplicationStatus.Sending || application.Status == ApplicationStatus.OutcomeUnknown), ct))
            return ActionResult.Fail("отклик уже отправляется или доставка требует сверки");
        if (await ctx.EmployerMessages.AnyAsync(message => message.VacancyId == vacancyId
            && (message.Status == EmployerMessageStatus.Sending || message.Status == EmployerMessageStatus.OutcomeUnknown), ct))
            return ActionResult.Fail("сообщение уже отправляется или требует отдельной сверки доставки");
        // After a response the vacancy is not "skipped": the application is closed through its funnel stage.
        if (await ctx.Applications.AnyAsync(a => a.VacancyId == vacancyId && a.Status == ApplicationStatus.Sent, ct))
            return ActionResult.Fail("отклик уже отправлен: закройте его стадией («отказ», «без ответа»)");
        v.Status = VacancyStatus.Dismissed;
        v.UpdatedAt = time.GetUtcNow();
        await ctx.Applications.Where(a => a.VacancyId == vacancyId && (a.Status == ApplicationStatus.Pending || a.Status == ApplicationStatus.DryRun))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ApplicationStatus.Cancelled)
                .SetProperty(a => a.Revision, a => a.Revision + 1), ct);
        await ctx.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ActionResult.Done("пропущено");
    }

    public async Task<ActionResult> MarkManualAsync(long vacancyId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var v = await DeliveryLedger.LockVacancyAsync(ctx, vacancyId, ct);
        if (v is null) return ActionResult.Fail("вакансия не найдена");
        await ctx.Entry(v).Collection(vacancy => vacancy.Applications).LoadAsync(ct);
        if (await ctx.EmployerMessages.AnyAsync(message => message.VacancyId == vacancyId
            && (message.Status == EmployerMessageStatus.Sending || message.Status == EmployerMessageStatus.OutcomeUnknown), ct))
            return ActionResult.Fail("сообщение отправляется или требует отдельной сверки через --message-reconcile");
        if (v.Applications.Any(application => application.Status == ApplicationStatus.Sending))
            return ActionResult.Fail("отклик уже отправляется; дождитесь результата перед сверкой");
        if (v.Applications.Any(a => a.Status == ApplicationStatus.Sent)) return ActionResult.Fail("отклик уже отмечен как отправленный");
        var unknownApplications = v.Applications.Where(application => application.Status == ApplicationStatus.OutcomeUnknown).Select(application => application.Id).ToArray();
        if (await ctx.ApplicationDeliveryRequests.Where(request => unknownApplications.Contains(request.ApplicationId)
                && request.Stage == ApplicationDeliveryStage.OutcomeUnknown).OrderByDescending(request => request.CreatedAt)
                .Select(request => (Guid?)request.Id).FirstOrDefaultAsync(ct) is { } requestId)
        {
            if (!await DeliveryLedger.ReconcileApplicationAsync(ctx, requestId, true,
                    "пользователь подтвердил отклик на сайте через действие «отправлено вручную»", funnel, time.GetUtcNow(), ct))
                return ActionResult.Fail("запрос требует отдельной сверки через --application-reconcile");
            await notifier.SaveAndFlushAsync(ctx, ct);
            return ActionResult.Done("сверка записана; исходное письмо/PDF не сертифицированы как доставленные");
        }
        v.Status = VacancyStatus.Applied;
        v.UpdatedAt = time.GetUtcNow();

        // A response the system could not finish (a test, a questionnaire) or only checked (dry-run), and the user finished on the site:
        // that application is the one.
        if (v.Applications.Where(a => a.Status is ApplicationStatus.NeedsManual or ApplicationStatus.DryRun or ApplicationStatus.Pending or ApplicationStatus.OutcomeUnknown)
                .MaxBy(a => a.CreatedAt) is { } unfinished)
        {
            var unknownAttempts = await ctx.DeliveryAttempts.Where(attempt => attempt.ApplicationId == unfinished.Id
                && attempt.Status == DeliveryAttemptStatus.OutcomeUnknown).ToListAsync(ct);
            foreach (var attempt in unknownAttempts)
            {
                attempt.Status = DeliveryAttemptStatus.Reconciled;
                attempt.CompletedAt = time.GetUtcNow();
                attempt.Error = "user confirmed the application externally; original payload delivery was not independently verified";
            }
            unfinished.Status = ApplicationStatus.Sent;
            unfinished.SentAt = time.GetUtcNow();
            unfinished.Error = null;
            funnel.Entered(ctx, unfinished, EventChannel.Manual);
            await ctx.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return ActionResult.Done("отмечено как отправленное вручную");
        }

        var app = new Application
        {
            VacancyId = v.Id,
            Channel = ApplicationChannel.Manual,
            Mode = ApplicationMode.Approved,
            Status = ApplicationStatus.Sent,
            CoverLetter = v.CoverLetter,
            CreatedAt = time.GetUtcNow(),
            SentAt = time.GetUtcNow(),
        };
        ctx.Applications.Add(app);
        funnel.Entered(ctx, app, EventChannel.Manual);
        await ctx.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ActionResult.Done("отмечено как отправленное вручную");
    }

    /// <summary>
    /// What the recruiter of this vacancy would get now: the stored letter while the vacancy waits for a decision, otherwise a fresh one
    /// by the current writer (not saved). Null when the vacancy or the profile is missing.
    /// </summary>
    public async Task<(string Text, IReadOnlyList<string> Problems)?> ResponsePreviewAsync(long vacancyId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.AsNoTracking().Include(x => x.Sources).Include(x => x.Company).Include(x => x.Applications).FirstOrDefaultAsync(x => x.Id == vacancyId, ct);
        if (v is null) return null;
        if (v.Applications.Count == 0 && !string.IsNullOrWhiteSpace(v.CoverLetter))
            return (v.CoverLetter, v.LetterReview?.Problems ?? ["сохранённое письмо ещё не проверено"]);
        var route = ApplyRoutes.Resolve(v, email.IsConfigured);
        var letter = await writer.ResponseAsync(v, route.Channel ?? ApplicationChannel.HeadHunter, route.Target, ct);
        return letter is null ? null : (letter.Text, letter.Problems);
    }

    public async Task<LetterEditSnapshot?> LetterSnapshotAsync(long vacancyId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var vacancy = await context.Vacancies.AsNoTracking().Include(row => row.Applications).SingleOrDefaultAsync(row => row.Id == vacancyId, ct);
        return vacancy is null ? null : LetterEditSnapshot.Capture(vacancy);
    }

    public async Task<(ActionResult Result, IReadOnlyList<string> Problems)> RegenerateLetterAsync(long vacancyId, CancellationToken ct,
        LetterEditSnapshot? expected = null)
    {
        if (await profiles.GetAsync(ct) is null) return (ActionResult.Fail("нет профиля"), []);
        await using var ctx = await db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.AsNoTracking().Include(x => x.Sources).Include(x => x.Company).Include(x => x.Applications).FirstOrDefaultAsync(x => x.Id == vacancyId, ct);
        if (v is null) return (ActionResult.Fail("вакансия не найдена"), []);
        expected ??= LetterEditSnapshot.Capture(v);
        if (!expected.Matches(v)) return (ActionResult.Fail("письмо или отклик изменились; сначала загрузите актуальную версию"), []);
        if (ReadOnlyLetter(v)) return (ActionResult.Fail("отклик уже отправлен, отправляется или требует сверки; письмо не изменено"), []);
        var route = ApplyRoutes.Resolve(v, email.IsConfigured);
        var letter = await writer.ResponseAsync(v, route.Channel ?? ApplicationChannel.HeadHunter, route.Target, ct, force: true);
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var current = await DeliveryLedger.LockVacancyAsync(ctx, vacancyId, ct);
        if (current is null)
            return (ActionResult.Fail("вакансия не найдена"), []);
        var applications = await ctx.Applications.AsNoTracking().Where(row => row.VacancyId == vacancyId).ToListAsync(ct);
        if (!expected.Matches(current, applications))
            return (ActionResult.Fail("письмо изменилось во время генерации; новая версия не сохранена"), []);
        if (ReadOnlyLetter(current, applications)) return (ActionResult.Fail("отклик уже отправлен, отправляется или требует сверки; письмо не изменено"), []);
        v = current;
        v.LetterRevision++;
        v.CoverLetter = letter?.Text;
        v.LetterReview = letter?.Review;
        v.UpdatedAt = time.GetUtcNow();
        await ctx.Applications.Where(application => application.VacancyId == vacancyId
                && (application.Status == ApplicationStatus.Pending || application.Status == ApplicationStatus.DryRun))
            .ExecuteUpdateAsync(update => update.SetProperty(application => application.CoverLetter, v.CoverLetter)
                .SetProperty(application => application.Revision, application => application.Revision + 1), ct);
        await deliveries.SavePendingAsync(ctx, vacancyId, ct);
        return letter is { Problems.Count: > 0 }
            ? (ActionResult.Done("новое письмо готово, но проверку не прошло: " + string.Join("; ", letter.Problems.Take(3))), letter.Problems)
            : (ActionResult.Done("новое письмо готово: факты сверены с профилем"), []);
    }

    private static bool ReadOnlyLetter(Vacancy vacancy, IReadOnlyCollection<Application>? applications = null) => vacancy.Status is VacancyStatus.Applied or VacancyStatus.Dismissed
        || (applications ?? vacancy.Applications).Any(application => application.Status is ApplicationStatus.Sent or ApplicationStatus.Sending or ApplicationStatus.OutcomeUnknown);

    public async Task<ActionResult> SetLetterAsync(long vacancyId, string letter, LetterEditSnapshot? expected, CancellationToken ct)
    {
        if (expected is null || expected.VacancyId != vacancyId) return ActionResult.Fail("нет исходной версии письма; откройте редактор заново");
        await using var ctx = await db.CreateDbContextAsync(ct);
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var v = await DeliveryLedger.LockVacancyAsync(ctx, vacancyId, ct);
        if (v is null) return ActionResult.Fail("вакансия не найдена");
        var applications = await ctx.Applications.AsNoTracking().Where(row => row.VacancyId == vacancyId).ToListAsync(ct);
        if (!expected.Matches(v, applications)) return ActionResult.Fail("письмо или отклик изменились; ваш текст не сохранён — загрузите актуальную версию");
        if (ReadOnlyLetter(v, applications)) return ActionResult.Fail("отклик уже отправлен, отправляется или требует сверки; письмо не изменено");
        v.LetterRevision++;
        v.CoverLetter = letter.Trim();
        v.LetterReview = null;
        v.UpdatedAt = time.GetUtcNow();
        await ctx.Applications.Where(a => a.VacancyId == vacancyId && (a.Status == ApplicationStatus.Pending || a.Status == ApplicationStatus.DryRun))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CoverLetter, v.CoverLetter)
                .SetProperty(a => a.Revision, a => a.Revision + 1), ct);
        if (await ctx.Applications.AnyAsync(row => row.VacancyId == vacancyId && row.Status == ApplicationStatus.Pending, ct))
            await deliveries.SavePendingAsync(ctx, vacancyId, ct);
        else
        {
            await ctx.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        return ActionResult.Done("письмо сохранено");
    }

    /// <summary>Back to the queue: the vacancy is filtered and scored again (after a profile or filter change).</summary>
    public async Task<ActionResult> RescoreAsync(long vacancyId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var vacancy = await Modules.Hiring.DeliveryLedger.LockVacancyAsync(ctx, vacancyId, ct);
        if (vacancy is null || vacancy.Status == VacancyStatus.Applied) return ActionResult.Fail("не найдено или уже есть отклик");
        if (await ctx.Applications.AnyAsync(application => application.VacancyId == vacancyId
            && (application.Status == ApplicationStatus.Pending || application.Status == ApplicationStatus.Sending
                || application.Status == ApplicationStatus.OutcomeUnknown || application.Status == ApplicationStatus.DryRun), ct)
            || await ctx.EmployerMessages.AnyAsync(message => message.VacancyId == vacancyId
                && (message.Status == EmployerMessageStatus.Sending || message.Status == EmployerMessageStatus.OutcomeUnknown), ct))
            return ActionResult.Fail("сначала завершите, отмените или согласуйте исход текущего отклика");
        vacancy.Status = VacancyStatus.New;
        vacancy.Attempts = 0;
        vacancy.Notified = false;
        vacancy.Decision = Decision.None;
        vacancy.AssessmentRevision++;
        vacancy.AssessmentCaptureId = null;
        vacancy.UpdatedAt = time.GetUtcNow();
        await ctx.VacancyAssessmentRequests.Where(request => request.VacancyId == vacancyId
            && (request.Stage == VacancyAssessmentStage.Scoring || request.Stage == VacancyAssessmentStage.Lettering || request.Stage == VacancyAssessmentStage.Finishing))
            .ExecuteUpdateAsync(update => update.SetProperty(request => request.Stage, VacancyAssessmentStage.Cancelled)
                .SetProperty(request => request.ClaimEnvelopeId, (Guid?)null).SetProperty(request => request.RetryAt, (DateTimeOffset?)null)
                .SetProperty(request => request.CompletedAt, time.GetUtcNow()).SetProperty(request => request.Error, "manual rescore superseded this assessment"), ct);
        await captures.SaveAsync(ctx, vacancy, ct);
        triggers.Trigger(JobNames.Vacancies);
        return ActionResult.Done("отправлено на переоценку");
    }

    public Task<ActionResult> PickCommentAsync(Modules.Engagement.CommentDecisionSnapshot snapshot, int variant, CancellationToken ct) =>
        comments.PickAsync(snapshot, variant, ct);

    public Task<ActionResult> SetCommentAsync(Modules.Engagement.CommentDecisionSnapshot snapshot, string text, CancellationToken ct) =>
        comments.SetAsync(snapshot, text, ct);

    public Task<ActionResult> RejectCommentAsync(Modules.Engagement.CommentDecisionSnapshot snapshot, CancellationToken ct) =>
        comments.RejectAsync(snapshot, ct);

    public Task<ActionResult> CancelCommentAsync(Modules.Engagement.CommentDecisionSnapshot snapshot, CancellationToken ct) =>
        comments.CancelAsync(snapshot, ct);

    public Task<ActionResult> ApproveChannelsAsync(IReadOnlyList<Modules.Discovery.ChannelDecisionSnapshot> expected, CancellationToken ct) =>
        channelDecisions.ApproveAsync(expected, "dashboard", ct);

    public Task<ActionResult> ApproveChannelBatchAsync(long id, CancellationToken ct) =>
        channelDecisions.ApproveBatchAsync(id, "bot", ct);

    public Task<ActionResult> SetChannelStatusAsync(Modules.Discovery.ChannelDecisionSnapshot expected, ChannelStatus status, CancellationToken ct) =>
        channelDecisions.SetAsync(expected, status, "dashboard", ct);

}
