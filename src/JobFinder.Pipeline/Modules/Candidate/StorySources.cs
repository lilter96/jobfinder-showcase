using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Candidate;

public sealed record StoryContent
{
    public string Title { get; set; } = "";
    public string? Achievement { get; set; }
    public string Situation { get; set; } = "";
    public string Task { get; set; } = "";
    public string Action { get; set; } = "";
    public string Result { get; set; } = "";
    public string Reflection { get; set; } = "";
    public List<string> Skills { get; set; } = [];
    public StorySource Source { get; set; }
    public bool Confirmed { get; set; }
    public string? RawNotes { get; set; }

    public static StoryContent From(Story story) => new()
    {
        Title = story.Title, Achievement = story.Achievement, Situation = story.Situation, Task = story.Task,
        Action = story.Action, Result = story.Result, Reflection = story.Reflection, Skills = [.. story.Skills],
        Source = story.Source, Confirmed = story.Confirmed, RawNotes = story.RawNotes,
    };
}

public sealed record StoryEditSnapshot
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public string SourceHash { get; set; } = "";
    public static StoryEditSnapshot Capture(Story story) => new() { Id = story.Id, Revision = story.Revision, SourceHash = CanonicalJson.Hash(StorySources.Json(story)) };
    public bool Matches(Story story) => this == Capture(story);
}

public sealed record StoryPublication(ActionResult Result, long? StoryId = null);

public sealed partial class StorySources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, Notifier notifier, TimeProvider time)
{
    public const string SkippedKey = "stories.skipped";

    public Task<StoryPublication> CreateAsync(StoryContent content, Guid intent, CancellationToken ct) =>
        intent == Guid.Empty ? Task.FromResult(new StoryPublication(ActionResult.Fail("нет исходного запроса создания")))
            : CreateAsync(content, "manual:" + intent.ToString("N"), null, ct);

    public async Task<StoryPublication> CreateFromConversationAsync(ConversationState expected, CancellationToken ct)
    {
        if (expected is null || expected.Answers is null) return new(Conflict());
        expected = ConversationStore.Freeze(expected);
        if (expected.Id == Guid.Empty || expected.Mode != StoryBank.Mode || expected.Draft is null || expected.StoryPending == true)
            return new(ActionResult.Fail("нет исходного черновика истории"));
        StoryDraft? draft;
        try { draft = JsonSerializer.Deserialize(expected.Draft, CareerJsonContext.Default.StoryDraft); }
        catch (JsonException) { return new(ActionResult.Fail("черновик истории повреждён; начните новый")); }
        if (draft is null || draft.Validate() is not null) return new(ActionResult.Fail("черновик истории пуст или не прошёл проверку"));
        var content = new StoryContent
        {
            Title = draft.Title, Achievement = expected.Subject, Situation = draft.Situation, Task = draft.Task, Action = draft.Action,
            Result = draft.Result, Reflection = draft.Reflection, Skills = draft.Skills, Source = StorySource.Interview,
            Confirmed = true, RawNotes = string.Join("\n", expected.Answers),
        };
        return await CreateAsync(content, "bot:" + expected.Id.ToString("N") + ":" + CanonicalJson.Hash(ConversationStore.Capture(expected)), expected, ct);
    }

    private async Task<StoryPublication> CreateAsync(StoryContent content, string key, ConversationState? expected, CancellationToken ct)
    {
        var frozen = Freeze(content);
        if (frozen is null) return new(ActionResult.Fail("нужно название, допустимый источник и непустые коллекции истории"));
        var hash = CanonicalJson.Hash(JsonSerializer.Serialize(frozen, StoryJsonContext.Default.StoryContent));
        using var stage = Telemetry.StartStage("create", "candidate-story");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        var old = await context.Stories.IgnoreQueryFilters().SingleOrDefaultAsync(row => row.CreationKey == key, ct);
        if (old is not null) return old.CreationHash == hash ? new(ActionResult.Done("история уже сохранена"), old.Id) : new(Conflict());
        if (expected is not null && !await ConversationMatchesAsync(context, expected, ct)) return new(Conflict());
        if (expected?.StoryRequestId is { } preparationId)
        {
            var prepared = await context.StoryPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == preparationId, ct);
            if (prepared is null || prepared.Stage != StoryPreparationStage.Completed) return new(Conflict());
            try
            {
                var saved = StoryPreparationRequests.ReadResult(prepared);
                if (expected.Draft != JsonSerializer.Serialize(saved.Draft, CareerJsonContext.Default.StoryDraft)) return new(Conflict());
                var input = StoryPreparationRequests.ReadInput(prepared);
                if (ConversationStore.Capture(StoryPresentation.Ready(input, saved)) != ConversationStore.Capture(expected)) return new(Conflict());
            }
            catch (Exception error) when (error is JsonException or UnsupportedStoryPreparationException) { return new(Conflict()); }
        }
        var now = Now();
        var story = new Story { Title = frozen.Title, Revision = 1, CreationKey = key, CreationHash = hash, CreatedAt = now, UpdatedAt = now, PreparationRequestId = expected?.StoryRequestId };
        Apply(story, frozen);
        context.Stories.Add(story);
        await notifier.SaveJoinedAsync(context, outbox, ct);
        AddRevision(context, story, null, "Created");
        if (expected is not null && !await ConversationStore.TryClearAsync(context, expected, ct)) throw new InvalidOperationException("story conversation changed under its lock");
        notifier.Add(context, NotificationKind.Info, "История сохранена: " + TextUtil.EscapeHtml(story.Title));
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new(ActionResult.Done("история сохранена"), story.Id);
    }

    public async Task<ActionResult> SaveAsync(StoryEditSnapshot expected, StoryContent content, CancellationToken ct)
    {
        var frozen = Freeze(content);
        if (frozen is null) return ActionResult.Fail("нужно название, допустимый источник и непустые коллекции истории");
        return await ChangeAsync(expected, frozen, ct);
    }

    public Task<ActionResult> RetireAsync(StoryEditSnapshot expected, CancellationToken ct) => ChangeAsync(expected, null, ct);

    private async Task<ActionResult> ChangeAsync(StoryEditSnapshot expected, StoryContent? content, CancellationToken ct)
    {
        if (expected is null || expected.Id <= 0 || expected.Revision < 0 || expected.Revision == long.MaxValue) return Conflict();
        expected = expected with { };
        using var stage = Telemetry.StartStage(content is null ? "retire" : "edit", "candidate-story").Tag("story.id", expected.Id);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        var story = await context.Stories.FromSqlInterpolated($"SELECT * FROM stories WHERE id = {expected.Id} FOR UPDATE").IgnoreQueryFilters().SingleOrDefaultAsync(ct);
        if (story is null || story.RetiredAt is not null || !expected.Matches(story)) return Conflict();
        if (content is not null && (content.Source != story.Source || content.Achievement != story.Achievement || content.RawNotes != story.RawNotes)) return Conflict();
        var before = Json(story);
        if (content is not null)
        {
            if (CanonicalJson.Hash(JsonSerializer.Serialize(StoryContent.From(story), StoryJsonContext.Default.StoryContent))
                == CanonicalJson.Hash(JsonSerializer.Serialize(content, StoryJsonContext.Default.StoryContent))) return ActionResult.Done("без изменений");
            Apply(story, content);
        }
        else story.RetiredAt = Now();
        story.Revision++;
        story.UpdatedAt = Now();
        AddRevision(context, story, before, content is null ? "Retired" : "Edited");
        notifier.Add(context, NotificationKind.Info, (content is null ? "История перемещена в архив: " : "История обновлена: ") + TextUtil.EscapeHtml(story.Title));
        await notifier.SaveAndFlushAsync(context, ct);
        return ActionResult.Done(content is null ? "история в архиве; исходный текст и история изменений сохранены" : "история сохранена");
    }

    public async Task<ActionResult> CloseConversationAsync(ConversationState expected, bool skip, CancellationToken ct)
    {
        if (expected is null || expected.Answers is null) return Conflict();
        expected = ConversationStore.Freeze(expected);
        if (expected.Id == Guid.Empty || expected.Mode != StoryBank.Mode || skip && string.IsNullOrWhiteSpace(expected.Subject)) return Conflict();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        if (!await ConversationMatchesAsync(context, expected, ct)) return Conflict();
        if (!await CancelPreparationAsync(context, expected, ct)) return Conflict();
        if (skip)
        {
            var setting = await context.Settings.SingleOrDefaultAsync(row => row.Key == SkippedKey, ct);
            var skipped = ConversationStore.Strings(setting?.Value);
            if (!skipped.Contains(expected.Subject!)) skipped.Add(expected.Subject!);
            if (setting is null) { setting = new AppSetting { Key = SkippedKey, Value = "" }; context.Settings.Add(setting); }
            setting.Value = ConversationStore.Serialize(skipped);
            setting.UpdatedAt = Now();
        }
        if (!await ConversationStore.TryClearAsync(context, expected, ct)) throw new InvalidOperationException("story conversation changed under its lock");
        notifier.Add(context, NotificationKind.Info, skip ? "Достижение пропущено: " + TextUtil.EscapeHtml(expected.Subject) : "Сбор истории остановлен; сохранённые истории не изменены.");
        await notifier.SaveAndFlushAsync(context, ct);
        return ActionResult.Done(skip ? "достижение пропущено" : "сбор истории остановлен");
    }

    private static async Task<bool> ConversationMatchesAsync(JobFinderDbContext context, ConversationState expected, CancellationToken ct) =>
        (await context.Settings.FromSqlInterpolated($"SELECT * FROM settings WHERE key = {ConversationStore.Key} FOR UPDATE").SingleOrDefaultAsync(ct))?.Value == ConversationStore.Capture(expected);

    private static StoryContent? Freeze(StoryContent content)
    {
        if (content is null || string.IsNullOrWhiteSpace(content.Title) || !Enum.IsDefined(content.Source) || content.Situation is null
            || content.Task is null || content.Action is null || content.Result is null || content.Reflection is null || content.Skills is null
            || content.Skills.Any(skill => skill is null)) return null;
        return content with { Title = content.Title.Trim(), Skills = content.Skills.Select(skill => skill.Trim()).Where(skill => skill.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
    }

    private static void Apply(Story story, StoryContent content)
    {
        story.Title = content.Title; story.Achievement = content.Achievement; story.Situation = content.Situation;
        story.Task = content.Task; story.Action = content.Action; story.Result = content.Result; story.Reflection = content.Reflection;
        story.Skills = [.. content.Skills]; story.Source = content.Source; story.Confirmed = content.Confirmed; story.RawNotes = content.RawNotes;
    }

    private static void AddRevision(JobFinderDbContext context, Story story, string? before, string change) =>
        context.StoryRevisions.Add(new StoryRevision { StoryId = story.Id, Revision = story.Revision, BeforeJson = before, AfterJson = Json(story), Change = change, RecordedAt = story.UpdatedAt });

    internal static string Json(Story story) => JsonSerializer.Serialize(story, StoryJsonContext.Default.Story);
    private DateTimeOffset Now() { var now = time.GetUtcNow(); return new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero); }
    private static ActionResult Conflict() => ActionResult.Fail("история, её исходный черновик или диалог изменились; данные не перезаписаны");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Story))]
[JsonSerializable(typeof(StoryContent))]
public sealed partial class StoryJsonContext : JsonSerializerContext;
