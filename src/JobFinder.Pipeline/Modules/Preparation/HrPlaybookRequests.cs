using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class HrPlaybookRequests(IDbContextFactory<JobFinderDbContext> db, TimeProvider time)
{
    public async Task<HrPlaybook> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.HrPlaybookRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            ReadInput(root);
            if (root.Stage == HrPlaybookStage.Completed) return ReadResult(root);
            if (root.Stage is HrPlaybookStage.Cancelled or HrPlaybookStage.Failed) throw new InvalidOperationException(root.Error ?? "HR preparation stopped");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static HrPlaybookRequest Root(HrPlaybookInput input)
    {
        var json = JsonSerializer.Serialize(input, HrPlaybookJsonContext.Default.HrPlaybookInput);
        var root = new HrPlaybookRequest { Id = input.Operation.OperationId, BusinessKey = "hr-v1:" + input.Operation.OperationId,
            InputJson = json, InputHash = CanonicalJson.Hash(json), Stage = HrPlaybookStage.Building, CreatedAt = input.CapturedAt };
        ReadInput(root);
        return root;
    }

    internal static HrPlaybookInput ReadInput(HrPlaybookRequest root)
    {
        if (string.IsNullOrWhiteSpace(root.InputJson) || CanonicalJson.Hash(root.InputJson) != root.InputHash) throw new UnsupportedHrPlaybookException();
        var input = JsonSerializer.Deserialize(root.InputJson, HrPlaybookJsonContext.Default.HrPlaybookInput) ?? throw new UnsupportedHrPlaybookException();
        if (root.Id == Guid.Empty || root.BusinessKey != "hr-v1:" + root.Id || !Enum.IsDefined(root.Stage) || root.Failures < 0
            || input.Version != 1 || input.CapturedAt == default || input.ProfileVersion <= 0 || input.FactsVersion < 0
            || input.ProfileJson is null || input.FactsJson is null || input.Evidence is null || input.Funnel is null
            || input.Bank?.Questions is not { Count: > 0 and <= 250 } questions || questions.Any(question => question is null
                || string.IsNullOrWhiteSpace(question.Id) || string.IsNullOrWhiteSpace(question.Question) || question.Facts is null || question.Options is null)
            || questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() != questions.Count
            || input.Operation is not { SchemaVersion: 1, Policy: not null } || input.Operation.OperationId != root.Id
            || input.Recipe is not { Version: 1, Tasks.Count: > 0, Inputs: not null, Rules: not null } || input.Recipe.OperationId != root.Id
            || input.Recipe.Tasks.Any(task => task is null || task.TaskId != LlmTaskCatalog.HrAnswer.Id)) throw new UnsupportedHrPlaybookException();
        ProfileProvider.FromJson(input.ProfileJson);
        if (JsonSerializer.Deserialize(input.FactsJson, FactsJsonContext.Default.CandidateFacts) is null) throw new UnsupportedHrPlaybookException();
        if (input.PreviousJson is not null && JsonSerializer.Deserialize(input.PreviousJson, InterviewJsonContext.Default.HrPlaybook) is null)
            throw new UnsupportedHrPlaybookException();
        return input;
    }

    internal static HrPlaybook ReadResult(HrPlaybookRequest root)
    {
        var input = ReadInput(root);
        if (root.ResultJson is null || CanonicalJson.Hash(root.ResultJson) != root.ResultHash) throw new UnsupportedHrPlaybookException();
        var result = JsonSerializer.Deserialize(root.ResultJson, InterviewJsonContext.Default.HrPlaybook) ?? throw new UnsupportedHrPlaybookException();
        if (result.Version != HrPlaybook.CurrentVersion || result.GeneratedAt == default || result.CostUsd < 0 || result.Items is null
            || result.Items.Count != input.Bank.Questions.Count) throw new UnsupportedHrPlaybookException();
        for (var index = 0; index < result.Items.Count; index++)
        {
            var item = result.Items[index];
            if (item?.Bank is null || item.Item?.Q is null || item.Item.Q.Id != input.Bank.Questions[index].Id
                || HrPlaybookSources.QuestionHash(item.Bank) != HrPlaybookSources.QuestionHash(input.Bank.Questions[index])
                || item.Item.Q.Question != input.Bank.Questions[index].Question || item.Item.A is null || item.Item.A.Validate() is not null
                || string.IsNullOrWhiteSpace(item.Item.Fingerprint)) throw new UnsupportedHrPlaybookException();
        }
        return result;
    }

    internal static async Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("HR preparation requires a source transaction");
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({HrPlaybookService.LatestKey}, 0))", ct);
    }

    internal static async Task PublishAsync(IDbContextOutbox outbox, HrPlaybookRequest root, DateTimeOffset? at = null)
    {
        object command = root.Stage switch
        {
            HrPlaybookStage.Building => new BuildHrPlaybook { RequestId = root.Id },
            HrPlaybookStage.Publishing => new PublishHrPlaybook { RequestId = root.Id },
            _ => throw new UnsupportedHrPlaybookException(),
        };
        if (at is { } due) await outbox.ScheduleAsync(command, due);
        else await outbox.PublishAsync(command);
    }
}
