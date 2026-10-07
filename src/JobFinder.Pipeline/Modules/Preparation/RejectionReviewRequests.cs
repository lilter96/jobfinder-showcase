using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class RejectionReviewRequests(IDbContextFactory<JobFinderDbContext> db, TimeProvider time)
{
    public async Task<RejectionReviewResult> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.RejectionReviewRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            ReadInput(root);
            if (root.Stage == RejectionReviewStage.Completed) return ReadResult(root);
            if (root.Stage is RejectionReviewStage.Cancelled or RejectionReviewStage.Failed) throw new InvalidOperationException(root.Error ?? "rejection review stopped");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static RejectionReviewRequest Root(RejectionReviewInputSnapshot input)
    {
        var json = JsonSerializer.Serialize(input, RejectionReviewJsonContext.Default.RejectionReviewInputSnapshot);
        var root = new RejectionReviewRequest { Id = input.Operation.OperationId, BusinessKey = "rejections-v1:" + input.Operation.OperationId,
            InputJson = json, InputHash = CanonicalJson.Hash(json), Stage = RejectionReviewStage.Analyzing, CreatedAt = input.CapturedAt };
        ReadInput(root);
        return root;
    }

    internal static RejectionReviewInputSnapshot ReadInput(RejectionReviewRequest root)
    {
        if (string.IsNullOrWhiteSpace(root.InputJson) || CanonicalJson.Hash(root.InputJson) != root.InputHash) throw new UnsupportedRejectionReviewException();
        var input = JsonSerializer.Deserialize(root.InputJson, RejectionReviewJsonContext.Default.RejectionReviewInputSnapshot) ?? throw new UnsupportedRejectionReviewException();
        if (root.Id == Guid.Empty || root.BusinessKey != "rejections-v1:" + root.Id || !Enum.IsDefined(root.Stage) || root.Failures < 0
            || input.Version != 1 || input.CapturedAt == default || input.ProfileVersion <= 0 || input.ProfileJson is null || input.ResumeText is null
            || input.SourceJson is null || string.IsNullOrWhiteSpace(input.FactsText) || input.Responses < input.Rejections || input.Rejections < RejectionReviewService.MinRejections
            || input.Operation is not { SchemaVersion: 1, Policy: not null } || input.Operation.OperationId != root.Id
            || input.Recipe is not { Version: 1, Tasks.Count: 1, Inputs: not null, Rules: not null } || input.Recipe.OperationId != root.Id
            || input.Recipe.Tasks[0] is null || input.Recipe.Tasks[0].TaskId != LlmTaskCatalog.RejectionReview.Id) throw new UnsupportedRejectionReviewException();
        ProfileProvider.FromJson(input.ProfileJson);
        using var source = JsonDocument.Parse(input.SourceJson);
        if (source.RootElement.ValueKind != JsonValueKind.Array || source.RootElement.GetArrayLength() < input.Responses) throw new UnsupportedRejectionReviewException();
        var seen = new HashSet<long>();
        foreach (var item in source.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("application", out var application) || application.ValueKind != JsonValueKind.Object
                || !application.TryGetProperty("id", out var identity) || identity.ValueKind != JsonValueKind.Number || !identity.TryGetInt64(out var id) || id <= 0 || !seen.Add(id)
                || !application.TryGetProperty("vacancy_id", out var vacancyIdentity) || vacancyIdentity.ValueKind != JsonValueKind.Number || !vacancyIdentity.TryGetInt64(out var vacancyId) || vacancyId <= 0
                || !item.TryGetProperty("vacancy", out var vacancy) || vacancy.ValueKind != JsonValueKind.Object
                || !vacancy.TryGetProperty("id", out var linked) || linked.ValueKind != JsonValueKind.Number || !linked.TryGetInt64(out var linkedId) || linkedId != vacancyId
                || !item.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) throw new UnsupportedRejectionReviewException();
        }
        if (input.PreviousJson is not null && JsonSerializer.Deserialize(input.PreviousJson, CareerJsonContext.Default.RejectionReview) is null)
            throw new UnsupportedRejectionReviewException();
        return input;
    }

    internal static RejectionReviewResult ReadResult(RejectionReviewRequest root)
    {
        ReadInput(root);
        if (root.ResultJson is null || CanonicalJson.Hash(root.ResultJson) != root.ResultHash) throw new UnsupportedRejectionReviewException();
        var result = JsonSerializer.Deserialize(root.ResultJson, RejectionReviewJsonContext.Default.RejectionReviewResult) ?? throw new UnsupportedRejectionReviewException();
        if (result.Review is null || result.Review.Validate() is not null || result.Review.Reasons is null || result.Review.ResumeFixes is null
            || result.Review.KeywordsToAdd is null || result.Review.SearchFixes is null || result.Review.Learn is null
            || result.GeneratedAt == default || string.IsNullOrWhiteSpace(result.Model) || result.CostUsd < 0) throw new UnsupportedRejectionReviewException();
        return result;
    }

    internal static async Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("rejection review requires a source transaction");
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({RejectionReviewService.LatestKey}, 0))", ct);
    }

    internal static async Task PublishAsync(IDbContextOutbox outbox, RejectionReviewRequest root, DateTimeOffset? at = null)
    {
        object command = root.Stage switch
        {
            RejectionReviewStage.Analyzing => new AnalyzeRejectionReview { RequestId = root.Id },
            RejectionReviewStage.Publishing => new PublishRejectionReview { RequestId = root.Id },
            _ => throw new UnsupportedRejectionReviewException(),
        };
        if (at is { } due) await outbox.ScheduleAsync(command, due);
        else await outbox.PublishAsync(command);
    }
}
