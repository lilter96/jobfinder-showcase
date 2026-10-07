using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Candidate;

public sealed partial class StorySources
{
    internal async Task<ActionResult> BeginConversationAsync(StoryCandidateSnapshot candidate, bool onlyIfIdle, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        var original = await context.Settings.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ConversationStore.Key, ct);
        if (onlyIfIdle && original is not null) return Conflict();
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        if (!await CandidateMatchesAsync(context, candidate, ct)) return Conflict();
        await LockConversationAsync(context, ct);
        var row = await context.Settings.FromSqlInterpolated($"SELECT * FROM settings WHERE key = {ConversationStore.Key} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (row?.Value != original?.Value) return Conflict();
        var covered = (await context.Stories.Where(story => story.Achievement != null).Select(story => story.Achievement!).ToListAsync(ct)).ToHashSet();
        var skipped = ConversationStore.Strings(await context.Settings.Where(setting => setting.Key == SkippedKey).Select(setting => setting.Value).SingleOrDefaultAsync(ct)).ToHashSet();
        var pending = candidate.Profile.Achievements.Where(achievement => !covered.Contains(achievement) && !skipped.Contains(achievement)).ToList();
        if (pending.Count == 0) return ActionResult.Done("📚 Все достижения из резюме уже разобраны в истории. Истории можно поправить в дашборде (страница «Истории»).");
        if (row is not null)
        {
            ConversationState? old;
            try { old = JsonSerializer.Deserialize(row.Value, ConversationJsonContext.Default.ConversationState); }
            catch (JsonException) { return Conflict(); }
            if (old?.Mode != StoryBank.Mode || !await CancelPreparationAsync(context, old, ct)) return Conflict();
        }
        var state = new ConversationState(StoryBank.Mode, 0, 0, pending[0], [], null) { Id = Guid.NewGuid(), StoryCandidate = candidate };
        SetConversation(context, row, state);
        var card = StoryPresentation.Ask(state, pending.Count);
        notifier.Add(context, NotificationKind.Info, card.Text, card.Buttons);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("");
    }

    internal async Task<ActionResult> AnswerQuestionAsync(ConversationState expected, string answer, CancellationToken ct)
    {
        expected = ConversationStore.Freeze(expected);
        if (!StoryPreparationRequests.ValidConversation(expected) || expected.StoryPending == true || expected.Draft is not null
            || expected.Step >= StoryBank.Questions.Length - 1 || string.IsNullOrWhiteSpace(answer)) return Conflict();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        if (expected.StoryCandidate is { } candidate && !await CandidateMatchesAsync(context, candidate, ct)) return Conflict();
        if (!await ConversationMatchesAsync(context, expected, ct)) return Conflict();
        var advanced = expected with { Step = expected.Step + 1, Answers = expected.Answers.Append(answer.Trim()).ToList() };
        SetConversation(context, context.Settings.Local.Single(setting => setting.Key == ConversationStore.Key), advanced);
        var card = StoryPresentation.Ask(advanced, null);
        notifier.Add(context, NotificationKind.Info, card.Text, card.Buttons);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("");
    }

    internal async Task<bool> PublishDraftJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox,
        StoryPreparationInput input, StoryPreparationResult result, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("story draft needs an enrolled source transaction");
        if (!await PreparationMatchesAsync(context, input, ct)) return false;
        var ready = StoryPresentation.Ready(input, result);
        SetConversation(context, context.Settings.Local.Single(setting => setting.Key == ConversationStore.Key), ready);
        var card = StoryPresentation.Draft(input, result);
        notifier.Add(context, NotificationKind.Info, card.Text, card.Buttons);
        await notifier.SaveJoinedAsync(context, outbox, ct);
        return true;
    }

    internal static async Task<bool> PreparationMatchesAsync(JobFinderDbContext context, StoryPreparationInput input, CancellationToken ct) =>
        await CandidateMatchesAsync(context, input.Candidate, ct) && await ConversationMatchesAsync(context, input.Waiting, ct);

    internal static async Task<bool> CandidateMatchesAsync(JobFinderDbContext context, StoryCandidateSnapshot candidate, CancellationToken ct)
    {
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        var documents = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {ProfileProvider.Key} OR key = {FactsProvider.Key} FOR SHARE").ToListAsync(ct);
        var profile = documents.SingleOrDefault(row => row.Key == ProfileProvider.Key);
        var facts = documents.SingleOrDefault(row => row.Key == FactsProvider.Key);
        return (profile?.Version ?? 0) == candidate.ProfileVersion && (facts?.Version ?? 0) == candidate.FactsVersion
            && CanonicalJson.Hash(ProfileProvider.ToJson(profile is null ? new() : ProfileProvider.FromJson(profile.Content)))
                == CanonicalJson.Hash(ProfileProvider.ToJson(candidate.Profile));
    }

    internal void SetConversation(JobFinderDbContext context, AppSetting? row, ConversationState state)
    {
        if (row is null) { row = new() { Key = ConversationStore.Key, Value = "" }; context.Settings.Add(row); }
        row.Value = ConversationStore.Capture(state);
        row.UpdatedAt = Now();
    }

    private async Task<bool> CancelPreparationAsync(JobFinderDbContext context, ConversationState expected, CancellationToken ct)
    {
        if (expected.StoryRequestId is not { } id) return expected.StoryPending != true;
        var root = await context.StoryPreparationRequests.FromSqlInterpolated($"SELECT * FROM story_preparation_requests WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (root is null) return false;
        if (root.Stage is StoryPreparationStage.Failed or StoryPreparationStage.Cancelled)
            return root.CompletedAt is not null && expected.StoryPending == true;
        StoryPreparationInput input;
        try { input = StoryPreparationRequests.ReadInput(root); }
        catch (Exception error) when (error is JsonException or UnsupportedStoryPreparationException) { return false; }
        if (root.Stage == StoryPreparationStage.Completed)
        {
            if (expected.StoryPending == true) return false;
            try { return ConversationStore.Capture(StoryPresentation.Ready(input, StoryPreparationRequests.ReadResult(root))) == ConversationStore.Capture(expected); }
            catch (Exception error) when (error is JsonException or UnsupportedStoryPreparationException) { return false; }
        }
        if (ConversationStore.Capture(input.Waiting) != ConversationStore.Capture(expected)) return false;
        root.Stage = StoryPreparationStage.Cancelled;
        root.ClaimEnvelopeId = null;
        root.RetryAt = null;
        root.CompletedAt = Now();
        root.Error = "cancelled by exact story dialogue; raw answers and checkpoints retained";
        return true;
    }

    internal static Task LockConversationAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"jobfinder.story.conversation"},0))", ct);
}
