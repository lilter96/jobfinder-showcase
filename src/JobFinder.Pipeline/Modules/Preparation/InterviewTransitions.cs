using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class InterviewTransitions(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    InterviewSources sources, FunnelService funnel, Notifier notifier, TimeProvider time)
{
    public async Task<ActionResult> SetStageAsync(long applicationId, ApplicationStage stage, string? note, CancellationToken ct)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentException("некорректная стадия отклика", nameof(stage));
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var app = await context.Applications.SingleOrDefaultAsync(row => row.Id == applicationId, ct);
        if (app is null) return ActionResult.Fail("отклик не найден");
        await AdvanceAsync(context, outbox, app, stage, EventChannel.Manual, note, manual: true, ct);
        notifier.Add(context, NotificationKind.Info, "Стадия отклика #" + app.Id + ": " + FunnelService.Label(stage), entityId: app.Id);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("стадия: " + FunnelService.Label(stage));
    }

    internal async Task<bool> AdvanceAsync(JobFinderDbContext context, IDbContextOutbox outbox, Application app, ApplicationStage to,
        EventChannel channel, string? note, bool manual, CancellationToken ct, DateTimeOffset? interviewAt = null, InterviewKind? interviewKind = null)
    {
        if (context.Database.CurrentTransaction is null || context.Entry(app).State == EntityState.Detached)
            throw new InvalidOperationException("funnel handoff requires the source context and transaction");
        if (!Enum.IsDefined(to) || interviewKind is { } kind && !Enum.IsDefined(kind)) throw new ArgumentException("неизвестная стадия или вид интервью");
        using var stage = Telemetry.StartStage("transition", "interview").Tag("application.id", app.Id).Tag("application.stage", to.ToString());
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        if (context.Entry(app).State == EntityState.Added) await notifier.SaveJoinedAsync(context, outbox, ct);
        var current = await context.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {app.Id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
        if (current is null) throw new InterviewEditConflictException();
        var entry = context.Entry(app);
        var originalStage = entry.Property(row => row.Stage).OriginalValue;
        var originalAt = entry.Property(row => row.StageChangedAt).OriginalValue;
        if (originalStage != current.Stage || Ticks(originalAt) != Ticks(current.StageChangedAt))
        {
            if (app.Stage != originalStage || Ticks(app.StageChangedAt) != Ticks(originalAt)) throw new InterviewEditConflictException();
            app.Stage = current.Stage;
            app.StageChangedAt = current.StageChangedAt;
            entry.Property(row => row.Stage).OriginalValue = current.Stage;
            entry.Property(row => row.StageChangedAt).OriginalValue = current.StageChangedAt;
        }
        if (!funnel.ApplyStage(context, app, to, channel, note, manual, interviewAt)) return false;
        if (FunnelService.IsInterview(to)) await sources.EnsureAsync(context, outbox, app.Id, interviewKind ?? FunnelService.KindFor(to), interviewAt, ct);
        return true;
    }

    public async Task<long> CompleteDebriefAsync(InterviewEditSnapshot snapshot, DebriefResult result, CancellationToken ct, string? headerHash = null,
        ConversationState? prompt = null)
    {
        prompt = prompt is null ? null : ConversationStore.Freeze(prompt);
        if (result.Validate() is not null || result.QuestionsAsked is null || result.ToImprove is null
            || result.Stage is not ("none" or "hr_screen" or "tech_interview" or "final" or "offer" or "rejected"))
            throw new ArgumentException("неполный дебриф", nameof(result));
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM applications WHERE id = {snapshot.Source.ApplicationId} FOR UPDATE", ct);
        var source = await sources.CompleteDebriefAsync(context, snapshot,
            $"{result.Summary}\nВопросы: {string.Join("; ", result.QuestionsAsked)}\nПодтянуть: {string.Join("; ", result.ToImprove)}", ct);
        var app = await context.Applications.SingleAsync(row => row.Id == source.ApplicationId, ct);
        if (headerHash is not null)
        {
            var vacancy = await context.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {app.VacancyId} FOR UPDATE")
                .AsNoTracking().SingleAsync(ct);
            if (Fingerprint.Of(InterviewPrep.Header(vacancy.CompanyName, vacancy.Title, source.Kind)) != headerHash)
                throw new InterviewEditConflictException();
        }
        if (prompt is not null)
        {
            if (prompt.InterviewHeaderHash != headerHash) throw new InterviewEditConflictException();
            await CareerPromptSources.CompleteJoinedAsync(context, prompt, snapshot, CareerChat.DebriefMode, ct);
        }
        var next = result.Stage switch
        {
            "hr_screen" => ApplicationStage.HrScreen,
            "tech_interview" => ApplicationStage.TechInterview,
            "final" => ApplicationStage.Final,
            "offer" => ApplicationStage.Offer,
            "rejected" => ApplicationStage.Rejected,
            _ => (ApplicationStage?)null,
        };
        if (next is { } value) await AdvanceAsync(context, outbox, app, value, EventChannel.Manual, "по дебрифу", manual: false, ct);
        context.ApplicationEvents.Add(new ApplicationEvent
        {
            ApplicationId = source.ApplicationId, At = time.GetUtcNow(), Kind = EventKind.Note, Channel = EventChannel.Manual, Text = "Дебриф: " + source.Debrief,
        });
        notifier.Add(context, NotificationKind.Info, "📝 Дебриф интервью #" + source.Id + " сохранён", entityId: source.Id);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return source.ApplicationId;
    }

    private static long? Ticks(DateTimeOffset? value) => value is { } date ? date.UtcTicks - date.UtcTicks % 10 : null;
}
