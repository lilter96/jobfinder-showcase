using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Candidate;

public sealed class StoryPreparationRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    StorySources sources, ProfileProvider profiles, JobFinderLlm llm, Notifier notifier, TimeProvider time)
{
    public async Task<ActionResult> StartAsync(CancellationToken ct, bool onlyIfIdle = false) =>
        await sources.BeginConversationAsync(await CaptureCandidateAsync(ct), onlyIfIdle, ct);

    private async Task<StoryCandidateSnapshot> CaptureCandidateAsync(CancellationToken ct)
    {
        var profile = await profiles.GetSnapshotAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var factsVersion = await context.Documents.Where(row => row.Key == FactsProvider.Key).Select(row => (long?)row.Version).SingleOrDefaultAsync(ct) ?? 0;
        var resume = profiles.ResumeText() ?? "";
        return new() { Profile = profile?.Value ?? new CandidateProfile(), ProfileVersion = profile?.Version ?? 0, FactsVersion = factsVersion,
            ResumeText = resume, ResumeHash = Fingerprint.Of(resume), Questions = [.. StoryBank.Questions] };
    }

    public async Task<Guid?> RequestAsync(ConversationState expected, CancellationToken ct, string? answer = null)
    {
        if (expected is null) return null;
        expected = ConversationStore.Freeze(expected);
        if (!ValidConversation(expected)) return null;
        answer = answer?.Trim();
        var key = Key(expected, answer);
        await using (var lookup = await db.CreateDbContextAsync(ct))
        {
            if (expected.StoryPending == true)
            {
                var pending = await lookup.StoryPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == expected.StoryRequestId, ct);
                if (pending is null || ConversationStore.Capture(ReadInput(pending).Waiting) != ConversationStore.Capture(expected)) return null;
                return pending.Id;
            }
            var old = await lookup.StoryPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
            if (old is not null) { ReadInput(old); return old.Id; }
        }
        if (answer is not null && (string.IsNullOrWhiteSpace(answer) || expected.Step != StoryBank.Questions.Length - 1 || expected.Draft is not null)
            || answer is null && (expected.Step != StoryBank.Questions.Length || expected.Answers.Count == 0)) return null;
        var candidate = expected.StoryCandidate ?? await CaptureCandidateAsync(ct);
        var operation = await llm.ForOperationAsync(ct);
        var id = operation.CaptureOperation().OperationId;
        var waiting = expected with { StoryRequestId = id, StoryPending = true, StoryCandidate = candidate,
            Step = answer is null ? expected.Step : expected.Step + 1, Answers = answer is null ? [.. expected.Answers] : expected.Answers.Append(answer).ToList() };
        var input = new StoryPreparationInput { Before = expected, Waiting = waiting, Candidate = candidate,
            Answer = answer, Questions = [.. candidate.Questions], CapturedAt = time.GetUtcNow(), Operation = operation.CaptureOperation() };
        input.Recipe = StoryPreparationSteps.Recipe(operation, input);
        var json = JsonSerializer.Serialize(input, StoryPreparationJsonContext.Default.StoryPreparationInput);
        var root = new StoryPreparationRequest { Id = id, BusinessKey = key, InputJson = json, InputHash = CanonicalJson.Hash(json),
            Stage = StoryPreparationStage.Drafting, CreatedAt = time.GetUtcNow() };
        ReadInput(root);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, key, ct);
        var known = await context.StoryPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
        if (known is not null) { ReadInput(known); return known.Id; }
        if (!await StorySources.CandidateMatchesAsync(context, candidate, ct)) return null;
        var row = await context.Settings.FromSqlInterpolated($"SELECT * FROM settings WHERE key = {ConversationStore.Key} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (row?.Value != ConversationStore.Capture(expected)) return null;
        sources.SetConversation(context, row, waiting);
        context.StoryPreparationRequests.Add(root);
        notifier.Add(context, NotificationKind.Info, "⏳ Ответы сохранены. Собираю черновик истории; фактом он станет только после вашего подтверждения. /stop отменяет подготовку.", StoryPresentation.StopButtons(waiting));
        await PublishAsync(outbox, root);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return id;
    }

    public async Task<(StoryPreparationInput Input, StoryPreparationResult Result)> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.StoryPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            if (root.Stage == StoryPreparationStage.Completed) return (ReadInput(root), ReadResult(root));
            if (root.Stage is not (StoryPreparationStage.Drafting or StoryPreparationStage.Publishing)) throw new StoryPreparationSupersededException();
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static bool ValidConversation(ConversationState state) => state.Id != Guid.Empty && state.Mode == StoryBank.Mode
        && !string.IsNullOrWhiteSpace(state.Subject) && state.Answers is not null && state.Answers.All(answer => !string.IsNullOrWhiteSpace(answer))
        && state.Step >= 0 && state.Step <= StoryBank.Questions.Length && state.Answers.Count <= StoryBank.Questions.Length
        && (state.Step < StoryBank.Questions.Length ? state.Step == state.Answers.Count : state.Answers.Count > 0);

    internal static StoryPreparationInput ReadInput(StoryPreparationRequest root)
    {
        if (string.IsNullOrWhiteSpace(root.InputJson) || CanonicalJson.Hash(root.InputJson) != root.InputHash) throw new UnsupportedStoryPreparationException();
        var input = JsonSerializer.Deserialize(root.InputJson, StoryPreparationJsonContext.Default.StoryPreparationInput) ?? throw new UnsupportedStoryPreparationException();
        if (root.Id == Guid.Empty || !Enum.IsDefined(root.Stage) || root.Failures < 0 || input.Version != 1 || input.CapturedAt == default
            || input.Before is null || !ValidConversation(input.Before) || input.Before.StoryPending == true
            || input.Waiting is null || !ValidConversation(input.Waiting) || input.Waiting.Id != input.Before.Id || input.Waiting.EntityId != input.Before.EntityId
            || input.Waiting.Subject != input.Before.Subject || input.Waiting.Draft != input.Before.Draft || input.Waiting.StoryPending != true || input.Waiting.StoryRequestId != root.Id
            || input.Questions is not { Count: 4 } || input.Questions.Any(string.IsNullOrWhiteSpace)
            || input.Waiting.Step != input.Questions.Count || input.Waiting.Step != input.Before.Step + (input.Answer is null ? 0 : 1)
            || input.Answer is not null && (string.IsNullOrWhiteSpace(input.Answer) || input.Answer != input.Answer.Trim() || input.Before.Draft is not null)
            || !input.Waiting.Answers.SequenceEqual(input.Answer is null ? input.Before.Answers : input.Before.Answers.Append(input.Answer))
            || input.Candidate is null || input.Candidate.Profile is null || input.Candidate.ProfileVersion < 0 || input.Candidate.FactsVersion < 0
            || input.Candidate.ResumeText is null
            || input.Candidate.ResumeHash != Fingerprint.Of(input.Candidate.ResumeText)
            || input.Candidate.Questions is null || !input.Candidate.Questions.SequenceEqual(input.Questions)
            || input.Waiting.StoryCandidate is null || CandidateJson(input.Waiting.StoryCandidate) != CandidateJson(input.Candidate)
            || input.Before.StoryCandidate is { } beforeCandidate && CandidateJson(beforeCandidate) != CandidateJson(input.Candidate)
            || root.BusinessKey != Key(input.Before, input.Answer)
            || input.Operation is null || input.Operation.SchemaVersion != 1 || input.Operation.OperationId != root.Id || input.Operation.Policy is null
            || input.Recipe is null || input.Recipe.Version != 1 || input.Recipe.OperationId != root.Id || input.Recipe.Tasks is not { Count: 1 }
            || input.Recipe.Tasks.Any(task => task is null) || input.Recipe.Inputs is null || input.Recipe.Rules is null)
            throw new UnsupportedStoryPreparationException();
        return input;
    }

    internal static StoryPreparationResult ReadResult(StoryPreparationRequest root)
    {
        ReadInput(root);
        if (root.ResultJson is null || CanonicalJson.Hash(root.ResultJson) != root.ResultHash) throw new UnsupportedStoryPreparationException();
        var result = JsonSerializer.Deserialize(root.ResultJson, StoryPreparationJsonContext.Default.StoryPreparationResult) ?? throw new UnsupportedStoryPreparationException();
        var draft = result.Draft;
        if (result.Version != 1 || draft is null || draft.Validate() is not null || draft.Situation is null || draft.Task is null
            || draft.Result is null || draft.Reflection is null || draft.Skills is null || draft.Skills.Any(skill => skill is null))
            throw new UnsupportedStoryPreparationException();
        return result;
    }

    private static string CandidateJson(StoryCandidateSnapshot candidate) =>
        JsonSerializer.Serialize(candidate, StoryPreparationJsonContext.Default.StoryCandidateSnapshot);
    private static string Key(ConversationState state, string? answer) => "story-v1:" + Fingerprint.Of(ConversationStore.Capture(state), answer);
    internal static async Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"candidate.story." + key},0))", ct);
    }
    internal static ValueTask PublishAsync(IDbContextOutbox outbox, StoryPreparationRequest root, DateTimeOffset? at = null) => root.Stage switch
    {
        StoryPreparationStage.Drafting => outbox.PublishAsync(new DraftStory { RequestId = root.Id }, new DeliveryOptions { ScheduledTime = at }),
        StoryPreparationStage.Publishing => outbox.PublishAsync(new PublishStory { RequestId = root.Id }, new DeliveryOptions { ScheduledTime = at }),
        _ => throw new UnsupportedStoryPreparationException(),
    };
}
