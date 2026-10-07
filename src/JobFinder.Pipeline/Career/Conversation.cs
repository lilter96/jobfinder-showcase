using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

/// <summary>What the bot answers: text (Telegram HTML), buttons, an optional file.</summary>
public sealed record BotReply(string Text, List<List<string>>? Buttons = null, string? AttachmentPath = null, string? AttachmentName = null);

/// <summary>
/// The one dialogue the bot is in with the owner (story interview, practice interview, debrief, offer, interview time). Persisted in the
/// settings table, so a restart in the middle of a practice interview does not lose it.
/// </summary>
/// <param name="Mode">story / mock / debrief / offer / itime.</param>
/// <param name="EntityId">Mock session, interview… (0 when none).</param>
/// <param name="Subject">The achievement being expanded, the question asked.</param>
/// <param name="Draft">A composed result waiting for confirmation (JSON).</param>
public sealed record ConversationState(string Mode, long EntityId, int Step, string? Subject, List<string> Answers, string? Draft)
{
    public Guid Id { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MockRequestId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MockPending { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OfferRequestId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? StoryRequestId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StoryPending { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Modules.Candidate.StoryCandidateSnapshot? StoryCandidate { get; set; }
    public Modules.Preparation.InterviewEditSnapshot? Interview { get; set; }
    public string? InterviewHeaderHash { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConversationState))]
[JsonSerializable(typeof(List<MockTurn>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ConversationJsonContext : JsonSerializerContext;

public sealed record MockTurn
{
    public string Question { get; set; } = "";

    public string Focus { get; set; } = "";

    public string Ideal { get; set; } = "";

    public string? Answer { get; set; }

    public int? Score { get; set; }

    public string? Feedback { get; set; }

    /// <summary>A rehearsal of a prepared answer: <see cref="InterviewProgress.ItemKey"/>, so the score marks the question learned.</summary>
    public string? ItemKey { get; set; }
}

public sealed class ConversationStore(IDbContextFactory<JobFinderDbContext> db, TimeProvider time)
{
    internal const string Key = "bot.conversation";

    internal static string Capture(ConversationState state) => JsonSerializer.Serialize(state, ConversationJsonContext.Default.ConversationState);
    internal static ConversationState Freeze(ConversationState state) => JsonSerializer.Deserialize(Capture(state), ConversationJsonContext.Default.ConversationState)!;

    public async Task<bool> HasStateAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Settings.AnyAsync(row => row.Key == Key, ct);
    }

    public async Task<bool> MatchesAsync(ConversationState expected, CancellationToken ct)
    {
        if (expected.Id == Guid.Empty) return false;
        var before = Capture(expected);
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Settings.AnyAsync(row => row.Key == Key && row.Value == before, ct);
    }

    public async Task<bool> TryStartAsync(ConversationState state, CancellationToken ct)
    {
        if (state.Id == Guid.Empty) state.Id = Guid.NewGuid();
        var json = Capture(state);
        var now = time.GetUtcNow();
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key, value, updated_at) VALUES ({Key}, {json}, {now}) ON CONFLICT (key) DO NOTHING", ct) == 1;
    }

    internal static Task<bool> TryClearAsync(JobFinderDbContext context, ConversationState expected, CancellationToken ct) =>
        ClearJoinedAsync(context, expected, ct);

    private static async Task<bool> ClearJoinedAsync(JobFinderDbContext context, ConversationState expected, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || expected.Id == Guid.Empty) throw new InvalidOperationException("conversation clear requires its source transaction and witness");
        var before = Capture(expected);
        return await context.Settings.Where(row => row.Key == Key && row.Value == before).ExecuteDeleteAsync(ct) == 1;
    }

    public async Task<ConversationState?> GetAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var json = await context.Settings.Where(row => row.Key == Key).Select(row => row.Value).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize(json, ConversationJsonContext.Default.ConversationState);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetAsync(ConversationState state, CancellationToken ct)
    {
        if (state.Id == Guid.Empty) state.Id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(state, ConversationJsonContext.Default.ConversationState);
        var now = time.GetUtcNow();
        await using var context = await db.CreateDbContextAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key, value, updated_at) VALUES ({Key}, {json}, {now}) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at", ct);
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await context.Settings.Where(row => row.Key == Key).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> TrySetAsync(ConversationState expected, ConversationState replacement, CancellationToken ct)
    {
        if (expected.Id == Guid.Empty || replacement.Id != expected.Id) return false;
        var before = JsonSerializer.Serialize(expected, ConversationJsonContext.Default.ConversationState);
        var after = JsonSerializer.Serialize(replacement, ConversationJsonContext.Default.ConversationState);
        var now = time.GetUtcNow();
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Settings.Where(row => row.Key == Key && row.Value == before)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, after).SetProperty(row => row.UpdatedAt, now), ct) == 1;
    }

    public async Task<bool> TryClearAsync(ConversationState expected, CancellationToken ct)
    {
        if (expected.Id == Guid.Empty) return false;
        var before = JsonSerializer.Serialize(expected, ConversationJsonContext.Default.ConversationState);
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Settings.Where(row => row.Key == Key && row.Value == before).ExecuteDeleteAsync(ct) == 1;
    }

    public static string Serialize(List<MockTurn> turns) => JsonSerializer.Serialize(turns, ConversationJsonContext.Default.ListMockTurn);

    public static List<MockTurn> Turns(string json) => JsonSerializer.Deserialize(json, ConversationJsonContext.Default.ListMockTurn) ?? [];

    public static List<string> Strings(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize(json, ConversationJsonContext.Default.ListString) ?? [];

    public static string Serialize(List<string> list) => JsonSerializer.Serialize(list, ConversationJsonContext.Default.ListString);
}
