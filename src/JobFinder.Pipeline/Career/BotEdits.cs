using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

public sealed record BotEditPrompt
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? MessageId { get; set; }
    public string Kind { get; set; } = "";
    public long SourceId { get; set; }
    public LetterEditSnapshot? Letter { get; set; }
    public CommentDecisionSnapshot? Comment { get; set; }
    public ReplyEditSnapshot? Reply { get; set; }
    public OutreachEditSnapshot? Outreach { get; set; }
    public string SnapshotHash { get; set; } = "";
    public int UserMessageId { get; set; }
    public string? Draft { get; set; }
    public bool Completed { get; set; }
}

public sealed record BotEditBinding
{
    public int Version { get; set; } = 1;
    public Guid IntentId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int MessageId { get; set; }
}

public sealed record CapturedBotEdit(BotEditPrompt Value, long Revision, string Json);
public sealed class BotEditPromptException() : Exception("запрос редактирования повреждён или несовместим; откройте новый запрос");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BotEditPrompt))]
[JsonSerializable(typeof(BotEditBinding))]
public sealed partial class BotEditJsonContext : JsonSerializerContext;

public sealed class BotEdits(IDbContextFactory<JobFinderDbContext> db, TimeProvider time, Notifier? notifier = null)
{
    private const string Marker = "\n\nКод редактирования: ";

    public static string Text(string text, CapturedBotEdit prompt) => text + Marker + prompt.Value.Id.ToString("N");

    public async Task<CapturedBotEdit> BeginQuestionAsync(long botId, long chatId, BotEditPrompt prompt, string text, CancellationToken ct)
    {
        if (notifier is null) throw new InvalidOperationException("edit questions require native notification admission");
        prompt = prompt with { Id = Guid.NewGuid(), BotId = botId, ChatId = chatId, MessageId = null, UserMessageId = 0, Draft = null, Completed = false };
        prompt.SnapshotHash = SnapshotHash(prompt);
        Validate(prompt);
        var key = IntentKey(botId, chatId, prompt.Id);
        var json = JsonSerializer.Serialize(prompt, BotEditJsonContext.Default.BotEditPrompt);
        var captured = Read(new AppDocument { Key = key, Content = json, Version = 1 }, botId, chatId, prompt.Id);
        var body = Text(text, captured);
        if (string.IsNullOrWhiteSpace(text) || body.Length > 4096) throw new ArgumentException("edit question text is empty or oversized");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        context.Documents.Add(new AppDocument { Key = key, Content = json, Version = 1, UpdatedAt = time.GetUtcNow() });
        notifier.Add(context, NotificationKind.EditPrompt, body);
        await notifier.SaveAndFlushAsync(context, ct);
        return captured;
    }

    public async Task<CapturedBotEdit> BeginAsync(long botId, long chatId, BotEditPrompt prompt, CancellationToken ct)
    {
        prompt = prompt with { Id = Guid.NewGuid(), BotId = botId, ChatId = chatId, MessageId = null, UserMessageId = 0, Draft = null, Completed = false };
        prompt.SnapshotHash = SnapshotHash(prompt);
        Validate(prompt);
        var key = IntentKey(botId, chatId, prompt.Id);
        var json = JsonSerializer.Serialize(prompt, BotEditJsonContext.Default.BotEditPrompt);
        await using var context = await db.CreateDbContextAsync(ct);
        context.Documents.Add(new AppDocument { Key = key, Content = json, Version = 1, UpdatedAt = time.GetUtcNow() });
        await context.SaveChangesAsync(ct);
        return Read(new AppDocument { Key = key, Content = json, Version = 1 }, botId, chatId, prompt.Id);
    }

    public async Task<CapturedBotEdit> BindAsync(long botId, long chatId, Guid id, int messageId, CancellationToken ct)
    {
        if (botId <= 0 || chatId <= 0 || id == Guid.Empty || messageId <= 0) throw new BotEditPromptException();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var result = await BindJoinedAsync(context, botId, chatId, id, messageId, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<CapturedBotEdit> BindJoinedAsync(JobFinderDbContext context, long botId, long chatId, Guid id, int messageId, CancellationToken ct)
    {
        var key = IntentKey(botId, chatId, id);
        var bindingKey = MessageKey(botId, chatId, messageId);
        var row = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {key} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new BotEditPromptException();
        var captured = Read(row, botId, chatId, id);
        if (captured.Value.MessageId is { } existing && existing != messageId) throw new BotEditPromptException();
        var binding = new BotEditBinding { BotId = botId, ChatId = chatId, IntentId = id, MessageId = messageId };
        var json = JsonSerializer.Serialize(binding, BotEditJsonContext.Default.BotEditBinding);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO app_documents (key, content, version, updated_at) VALUES ({bindingKey}, {json}::json, 1, {time.GetUtcNow()}) ON CONFLICT (key) DO NOTHING", ct);
        var savedBinding = await context.Documents.AsNoTracking().SingleAsync(document => document.Key == bindingKey, ct);
        if (ReadBinding(savedBinding.Content) != binding) throw new BotEditPromptException();
        if (captured.Value.MessageId is null)
        {
            captured.Value.MessageId = messageId;
            row.Content = JsonSerializer.Serialize(captured.Value, BotEditJsonContext.Default.BotEditPrompt);
            row.Version++;
            row.UpdatedAt = time.GetUtcNow();
            await context.SaveChangesAsync(ct);
        }
        return new CapturedBotEdit(captured.Value, row.Version, row.Content);
    }

    internal async Task<bool> MayDeliverQuestionAsync(JobFinderDbContext context, NotificationTarget target, string text, CancellationToken ct)
    {
        try
        {
            var prompt = await QuestionAsync(context, target, text, ct);
            return prompt.Value.MessageId is null && !prompt.Value.Completed;
        }
        catch (BotEditPromptException) { return false; }
    }

    internal async Task ConfirmQuestionAsync(JobFinderDbContext context, NotificationTarget target, string text, int messageId, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("edit question receipt requires its delivery transaction");
        var prompt = await QuestionAsync(context, target, text, ct);
        await BindJoinedAsync(context, target.BotId, target.ChatId, prompt.Value.Id, messageId, ct);
    }

    private static async Task<CapturedBotEdit> QuestionAsync(JobFinderDbContext context, NotificationTarget target, string text, CancellationToken ct)
    {
        var marker = text.LastIndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0 || !Guid.TryParseExact(text[(marker + Marker.Length)..], "N", out var id)) throw new BotEditPromptException();
        var key = IntentKey(target.BotId, target.ChatId, id);
        var row = await context.Documents.AsNoTracking().SingleOrDefaultAsync(document => document.Key == key, ct) ?? throw new BotEditPromptException();
        return Read(row, target.BotId, target.ChatId, id);
    }

    public async Task<CapturedBotEdit?> FindAsync(long botId, long chatId, int messageId, string? promptText, long? promptAuthor, CancellationToken ct)
    {
        if (botId <= 0 || chatId <= 0 || messageId <= 0) return null;
        if (promptAuthor is not null && promptAuthor != botId) return null;
        await using var context = await db.CreateDbContextAsync(ct);
        var binding = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == MessageKey(botId, chatId, messageId), ct);
        if (binding is null)
        {
            if (promptAuthor != botId || promptText is null) return null;
            var marker = promptText.LastIndexOf(Marker, StringComparison.Ordinal);
            if (marker < 0 || !Guid.TryParseExact(promptText[(marker + Marker.Length)..], "N", out var id))
            {
                if (marker >= 0 || promptText.StartsWith("✏️ Пришлите", StringComparison.Ordinal)) throw new BotEditPromptException();
                return null;
            }
            return await BindAsync(botId, chatId, id, messageId, ct);
        }
        if (binding.Version < 1) throw new BotEditPromptException();
        var saved = ReadBinding(binding.Content);
        if (saved.BotId != botId || saved.ChatId != chatId || saved.MessageId != messageId) throw new BotEditPromptException();
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == IntentKey(botId, chatId, saved.IntentId), ct)
            ?? throw new BotEditPromptException();
        var result = Read(document, botId, chatId, saved.IntentId);
        if (result.Value.MessageId != messageId) throw new BotEditPromptException();
        return result;
    }

    public async Task<CapturedBotEdit?> RecordReplyAsync(CapturedBotEdit expected, int userMessageId, string text, CancellationToken ct)
    {
        Validate(expected.Value);
        if (expected.Value.Completed || expected.Value.MessageId is null || userMessageId <= 0 || string.IsNullOrWhiteSpace(text)
            || userMessageId < expected.Value.UserMessageId) return null;
        if (userMessageId == expected.Value.UserMessageId && expected.Value.Draft != text) return null;
        return await ReplaceAsync(expected, expected.Value with { UserMessageId = userMessageId, Draft = text }, ct);
    }

    public async Task<bool> CompleteAsync(CapturedBotEdit expected, CancellationToken ct) =>
        !expected.Value.Completed && expected.Value.UserMessageId > 0 && expected.Value.Draft is not null
        && await ReplaceAsync(expected, expected.Value with { Completed = true }, ct) is not null;

    private async Task<CapturedBotEdit?> ReplaceAsync(CapturedBotEdit expected, BotEditPrompt replacement, CancellationToken ct)
    {
        Validate(replacement);
        if (expected.Revision < 1) throw new BotEditPromptException();
        var key = IntentKey(expected.Value.BotId, expected.Value.ChatId, expected.Value.Id);
        var original = Read(new AppDocument { Key = key, Content = expected.Json, Version = expected.Revision }, expected.Value.BotId,
            expected.Value.ChatId, expected.Value.Id);
        if (JsonSerializer.Serialize(original.Value, BotEditJsonContext.Default.BotEditPrompt)
            != JsonSerializer.Serialize(expected.Value, BotEditJsonContext.Default.BotEditPrompt)) throw new BotEditPromptException();
        var before = expected.Json;
        var after = JsonSerializer.Serialize(replacement, BotEditJsonContext.Default.BotEditPrompt);
        await using var context = await db.CreateDbContextAsync(ct);
        var updated = await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE app_documents SET content = {after}::json, version = version + 1, updated_at = {time.GetUtcNow()} WHERE key = {key} AND version = {expected.Revision} AND content::text = {before}", ct);
        return updated == 1 ? new CapturedBotEdit(replacement, expected.Revision + 1, after) : null;
    }

    private static CapturedBotEdit Read(AppDocument document, long botId, long chatId, Guid id)
    {
        BotEditPrompt prompt;
        try { prompt = JsonSerializer.Deserialize(document.Content, BotEditJsonContext.Default.BotEditPrompt) ?? throw new BotEditPromptException(); }
        catch (JsonException) { throw new BotEditPromptException(); }
        Validate(prompt);
        if (document.Version < 1 || prompt.BotId != botId || prompt.ChatId != chatId || prompt.Id != id) throw new BotEditPromptException();
        return new CapturedBotEdit(prompt, document.Version, document.Content);
    }

    private static BotEditBinding ReadBinding(string json)
    {
        BotEditBinding binding;
        try { binding = JsonSerializer.Deserialize(json, BotEditJsonContext.Default.BotEditBinding) ?? throw new BotEditPromptException(); }
        catch (JsonException) { throw new BotEditPromptException(); }
        if (binding.Version != 1 || binding.IntentId == Guid.Empty || binding.BotId <= 0 || binding.ChatId <= 0 || binding.MessageId <= 0)
            throw new BotEditPromptException();
        return binding;
    }

    private static void Validate(BotEditPrompt prompt)
    {
        var snapshots = new object?[] { prompt.Letter, prompt.Comment, prompt.Reply, prompt.Outreach };
        if (prompt.Version != 1 || prompt.Id == Guid.Empty || prompt.BotId <= 0 || prompt.ChatId <= 0 || prompt.SourceId <= 0
            || prompt.MessageId <= 0 || prompt.UserMessageId < 0 || snapshots.Count(snapshot => snapshot is not null) != 1
            || string.IsNullOrWhiteSpace(prompt.SnapshotHash) || prompt.SnapshotHash != SnapshotHash(prompt)
            || (prompt.UserMessageId > 0) != (prompt.Draft is not null) || prompt.Completed && (prompt.UserMessageId == 0 || prompt.MessageId is null)
            || !(prompt.Kind switch
            {
                "letter" => prompt.Letter?.VacancyId == prompt.SourceId,
                "comment" => prompt.Comment?.Id == prompt.SourceId,
                "reply" => prompt.Reply?.EventId == prompt.SourceId,
                "outreach" => prompt.Outreach?.Id == prompt.SourceId,
                _ => false,
            })) throw new BotEditPromptException();
    }

    private static string SnapshotHash(BotEditPrompt prompt)
    {
        object? snapshot = prompt.Kind switch
        {
            "letter" => prompt.Letter, "comment" => prompt.Comment, "reply" => prompt.Reply, "outreach" => prompt.Outreach, _ => null,
        };
        return snapshot is null ? "" : CanonicalJson.Hash(JsonSerializer.Serialize(snapshot, snapshot.GetType(), BotEditJsonContext.Default));
    }

    private static string IntentKey(long botId, long chatId, Guid id) => string.Create(CultureInfo.InvariantCulture, $"bot.edit.intent.{botId}.{chatId}.{id:N}");
    private static string MessageKey(long botId, long chatId, int messageId) => string.Create(CultureInfo.InvariantCulture, $"bot.edit.message.{botId}.{chatId}.{messageId}");
}
