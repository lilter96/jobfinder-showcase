using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class CareerPromptSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, Notifier notifier, TimeProvider time)
{
    public static int ControlVersion(Interview source) => ControlVersion(source, source.Application!.Vacancy!);

    public static int ControlVersion(Interview source, Vacancy vacancy) => ReplyService.DraftVersion(
        Fingerprint.Of(InterviewEditSnapshot.Capture(source).Source.Hash() + ":" + source.PreparationRequestId + ":" +
            InterviewPrep.Header(vacancy.CompanyName, vacancy.Title, source.Kind)));

    public async Task<BotReply> BeginInterviewAsync(long id, string mode, CancellationToken ct, int? version = null)
    {
        if (mode is not (CareerChat.TimeMode or CareerChat.DebriefMode)) throw new ArgumentException("unsupported interview prompt", nameof(mode));
        using var stage = Telemetry.StartStage("begin-prompt", "interview").Tag("interview.id", id).Tag("prompt.mode", mode);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await InterviewPreparationCapture.LockAsync(context, id, ct);
        var source = await context.Interviews.Include(row => row.Application).ThenInclude(row => row!.Vacancy).SingleOrDefaultAsync(row => row.Id == id, ct);
        if (source is null) return new BotReply("Интервью не найдено.");
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM vacancies WHERE id = {source.Application!.VacancyId} FOR UPDATE", ct);
        await context.Entry(source.Application.Vacancy!).ReloadAsync(ct);
        if (version is { } captured && (captured <= 0 || captured != ControlVersion(source)))
            return new BotReply("Кнопка устарела: откройте актуальную карточку интервью.");
        if (source.Status != InterviewStatus.Scheduled && (mode == CareerChat.TimeMode || source.Status != InterviewStatus.Done))
            return new BotReply("Интервью уже закрыто: откройте актуальную карточку.");
        var state = new ConversationState(mode, id, 0, null, [], null)
        {
            Id = Guid.NewGuid(), Interview = InterviewEditSnapshot.Capture(source),
            InterviewHeaderHash = Fingerprint.Of(InterviewPrep.Header(source.Application.Vacancy!.CompanyName, source.Application.Vacancy.Title, source.Kind)),
        };
        var text = mode == CareerChat.TimeMode
            ? $"🗓 {CareerCards.InterviewLabel(source.Kind)} · #{id} · сейчас: {source.ScheduledAt?.ToString("u") ?? "не назначено"}\nКогда интервью? Например: «завтра 15:00», «пт 11:30», «28.09 14:00» (время Минска)."
            : $"📝 <b>Дебриф</b> · {TextUtil.EscapeHtml(source.Application.Vacancy.CompanyName)} ({CareerCards.InterviewLabel(source.Kind)})\nКак прошло? Какие вопросы задавали, где было легко, где споткнулись, что сказали в конце? Можно голосовым.";
        return await StartJoinedAsync(context, state, text, ct);
    }

    public async Task<BotReply> BeginOfferAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        return await StartJoinedAsync(context, new ConversationState(OfferAdvisor.Mode, 0, 0, null, [], null) { Id = Guid.NewGuid() },
            "🏆 Вставьте текст оффера или условия своими словами (сумма, валюта, gross/net, оформление, испытательный, бонусы, формат).", ct);
    }

    private async Task<BotReply> StartJoinedAsync(JobFinderDbContext context, ConversationState state, string text, CancellationToken ct)
    {
        var json = ConversationStore.Capture(state);
        var now = time.GetUtcNow();
        if (await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key, value, updated_at) VALUES ({ConversationStore.Key}, {json}, {now}) ON CONFLICT (key) DO NOTHING", ct) != 1)
            return new BotReply("Уже есть активный диалог. Завершите его или используйте /stop; сохранённые ответы не изменены.");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        notifier.Add(context, NotificationKind.Info, text, entityId: state.EntityId > 0 ? state.EntityId : null);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new BotReply("");
    }

    public async Task<ConversationState?> SaveAnswerAsync(ConversationState expected, string text, CancellationToken ct)
    {
        expected = ConversationStore.Freeze(expected);
        if (!IsInterview(expected) || string.IsNullOrWhiteSpace(text)) return null;
        var replacement = expected with { Draft = text, Step = checked(expected.Step + 1) };
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var before = ConversationStore.Capture(expected);
        var after = ConversationStore.Capture(replacement);
        var now = time.GetUtcNow();
        if (await context.Settings.Where(row => row.Key == ConversationStore.Key && row.Value == before)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, after).SetProperty(row => row.UpdatedAt, now), ct) != 1) return null;
        await transaction.CommitAsync(ct);
        return replacement;
    }

    public async Task<BotReply> StopAsync(ConversationState expected, CancellationToken ct)
    {
        expected = ConversationStore.Freeze(expected);
        if (expected.Id == Guid.Empty || expected.Mode is not (CareerChat.TimeMode or CareerChat.DebriefMode or OfferAdvisor.Mode)
            || expected.OfferRequestId is not null) return new BotReply("Старый или неизвестный диалог не изменён: требуется проверка сохранённого состояния.");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        if (!await ConversationStore.TryClearAsync(context, expected, ct)) return new BotReply("Диалог изменился: новый запрос не остановлен.");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        notifier.Add(context, NotificationKind.Info, "Остановлено.");
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new BotReply("");
    }

    internal static async Task CompleteJoinedAsync(JobFinderDbContext context, ConversationState expected, InterviewEditSnapshot snapshot,
        string mode, CancellationToken ct)
    {
        if (!IsInterview(expected) || expected.Mode != mode || expected.Draft is null || expected.Interview is null
            || expected.Interview.Source.Hash() != snapshot.Source.Hash() || expected.Interview.RequestId != snapshot.RequestId
            || !await ConversationStore.TryClearAsync(context, expected, ct)) throw new InterviewEditConflictException();
    }

    private static bool IsInterview(ConversationState state) => state.Id != Guid.Empty && state.Step >= 0
        && state.Mode is CareerChat.TimeMode or CareerChat.DebriefMode && state.Interview is { Version: 1, Source: not null }
        && state.EntityId == state.Interview.Source.Id && state.EntityId > 0;
}
