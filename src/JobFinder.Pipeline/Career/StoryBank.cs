using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Modules.Candidate;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// The story bank, filled by an interview in the bot: four questions per resume achievement (text or voice), a STAR+Reflection draft
/// composed from the answers only, saved when the user confirms. Confirmed stories are facts: tailored resumes, letters and interview
/// packs may use their numbers.
/// </summary>
public sealed class StoryBank(IDbContextFactory<JobFinderDbContext> db, ProfileProvider profiles, ConversationStore conversations,
    RuntimeSettings settings, StorySources sources, StoryPreparationRequests preparation)
{
    public const string Mode = "story";

    public static readonly string[] Questions =
    [
        "Контекст: что это была за система или продукт, какой масштаб и почему это стало задачей?",
        "Что сделали лично вы: какие решения принимали, какие были альтернативы и сложности?",
        "Какой результат и как его измеряли? Цифры — только те, в которых уверены.",
        "Чему это вас научило или что сделали бы иначе сегодня?",
    ];

    public async Task<List<string>> PendingAsync(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        if (profile is null) return [];
        await using var ctx = await db.CreateDbContextAsync(ct);
        var covered = (await ctx.Stories.Where(s => s.Achievement != null).Select(s => s.Achievement!).ToListAsync(ct)).ToHashSet();
        var skipped = ConversationStore.Strings(await settings.GetAsync(StorySources.SkippedKey, ct)).ToHashSet();
        return profile.Achievements.Where(a => !covered.Contains(a) && !skipped.Contains(a)).ToList();
    }

    public async Task<BotReply> StartAsync(CancellationToken ct, bool onlyIfIdle = false)
    {
        var result = await preparation.StartAsync(ct, onlyIfIdle);
        return new BotReply(result.Message);
    }

    public async Task<BotReply> AnswerAsync(ConversationState state, string answer, CancellationToken ct)
    {
        if (!StoryPreparationRequests.ValidConversation(state) || state.StoryPending == true || state.Draft is not null || string.IsNullOrWhiteSpace(answer)) return Changed();
        if (state.Step < Questions.Length - 1)
        {
            var result = await sources.AnswerQuestionAsync(state, answer, ct);
            return result.Ok ? new BotReply("") : Changed();
        }
        return await preparation.RequestAsync(state, ct, answer) is null ? Changed() : new BotReply("");
    }

    public async Task<BotReply> ComposeAsync(ConversationState state, CancellationToken ct) =>
        await preparation.RequestAsync(state, ct) is null ? Changed() : new BotReply("");

    public async Task<BotReply> SaveAsync(long controlId, int version, CancellationToken ct)
    {
        var state = await conversations.GetAsync(ct);
        if (state is not { Mode: Mode, Draft: not null } || state.StoryPending == true || !Matches(state, controlId, version)) return Changed();
        var publication = await sources.CreateFromConversationAsync(state, ct);
        if (!publication.Result.Ok) return new BotReply("⚠️ " + TextUtil.EscapeHtml(publication.Result.Message));

        var next = await StartAsync(ct, onlyIfIdle: true);
        return next with { Text = "✅ История сохранена.\n\n" + next.Text };
    }

    public async Task<BotReply> SkipAsync(long controlId, int version, CancellationToken ct)
    {
        var state = await conversations.GetAsync(ct);
        if (state is null || !Matches(state, controlId, version)) return Changed();
        var result = await sources.CloseConversationAsync(state, skip: true, ct);
        if (!result.Ok) return new BotReply("⚠️ " + TextUtil.EscapeHtml(result.Message));

        return await StartAsync(ct, onlyIfIdle: true);
    }

    public async Task<BotReply> RedoAsync(long controlId, int version, CancellationToken ct)
    {
        var state = await conversations.GetAsync(ct);
        return state is not null && Matches(state, controlId, version) ? await ComposeAsync(state, ct) : Changed();
    }

    public async Task<BotReply> StopAsync(long controlId, int version, CancellationToken ct)
    {
        var state = await conversations.GetAsync(ct);
        if (state is null || !Matches(state, controlId, version)) return Changed();
        var result = await sources.CloseConversationAsync(state, skip: false, ct);
        return new BotReply(TextUtil.EscapeHtml(result.Message));
    }

    public static long ControlId(ConversationState state) => BitConverter.ToInt64(state.Id.ToByteArray(), 0) & long.MaxValue;
    public static int Version(ConversationState state) => ReplyService.DraftVersion(CanonicalJson.Hash(ConversationStore.Capture(state)));
    private static bool Matches(ConversationState state, long controlId, int version) => state.Mode == Mode && state.Id != Guid.Empty
        && controlId > 0 && version > 0 && ControlId(state) == controlId && Version(state) == version;
    private static BotReply Changed() => new("Диалог или черновик истории изменился. Используйте кнопки актуального сообщения; данные не перезаписаны.");
}
