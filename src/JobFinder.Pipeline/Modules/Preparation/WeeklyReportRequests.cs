using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class WeeklyReportRequests(IDbContextFactory<JobFinderDbContext> db, TimeProvider time)
{
    public async Task<WeeklyReportResult> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.WeeklyReportRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            ReadInput(root);
            if (root.Stage == WeeklyReportStage.Completed) return ReadResult(root);
            if (root.Stage is WeeklyReportStage.Cancelled or WeeklyReportStage.Failed) throw new InvalidOperationException(root.Error ?? "weekly advice stopped; statistics retained");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static string Key(WeeklyReportInput input) => input.Slot is { } slot ? "weekly-slot-v1:" + slot.UtcTicks : "weekly-manual-v1:" + input.Operation.OperationId;

    internal static WeeklyReportInput ReadInput(WeeklyReportRequest root)
    {
        if (string.IsNullOrWhiteSpace(root.InputJson) || CanonicalJson.Hash(root.InputJson) != root.InputHash) throw new UnsupportedWeeklyReportException();
        var input = JsonSerializer.Deserialize(root.InputJson, WeeklyReportJsonContext.Default.WeeklyReportInput) ?? throw new UnsupportedWeeklyReportException();
        if (root.Id == Guid.Empty || root.BusinessKey != Key(input) || !Enum.IsDefined(root.Stage) || root.Failures < 0 || root.NotificationId <= 0
            || input.Version != 1 || input.CapturedAt == default || root.JobRunId != input.JobRunId || input.ProfileVersion < 0
            || (input.ProfileJson is null) != (input.ProfileVersion == 0) || input.ResumeText is null
            || input.Frame is not { Sent: >= 0, DemandText: not null, FunnelText: not null, CostHtml: not null }
            || string.IsNullOrWhiteSpace(input.Frame.HeaderHtml) || input.Frame.HasDemand && string.IsNullOrWhiteSpace(input.Frame.DemandText)
            || input.Operation is not { SchemaVersion: 1, Policy: not null } || input.Operation.OperationId != root.Id
            || input.Recipe is not { Version: 1, Tasks.Count: 1, Inputs: not null, Rules: not null } || input.Recipe.OperationId != root.Id
            || input.Recipe.Tasks[0] is null || input.Recipe.Tasks[0].TaskId != LlmTaskCatalog.HhAdvice.Id
            || input.JobRunId is null && (input.JobStartedAt is not null || !input.Manual || input.Slot is not null)
            || input.JobRunId is not null && (input.JobRunId <= 0 || input.JobStartedAt is null || input.JobStartedAt == default
                || input.Manual != (input.Slot is null) || input.JobStartedAt != input.CapturedAt)
            || input.Slot is { } due && (due > input.CapturedAt || input.CapturedAt - due > TimeSpan.FromHours(12))) throw new UnsupportedWeeklyReportException();
        if (input.ProfileJson is not null) ProfileProvider.FromJson(input.ProfileJson);
        return input;
    }

    internal static WeeklyReportResult ReadResult(WeeklyReportRequest root)
    {
        var input = ReadInput(root);
        if (root.ResultJson is null || CanonicalJson.Hash(root.ResultJson) != root.ResultHash) throw new UnsupportedWeeklyReportException();
        var result = JsonSerializer.Deserialize(root.ResultJson, WeeklyReportJsonContext.Default.WeeklyReportResult) ?? throw new UnsupportedWeeklyReportException();
        if (result.GeneratedAt == default || result.CostUsd < 0 || result.Sent != input.Frame.Sent || string.IsNullOrWhiteSpace(result.Html)
            || input.NeedsAdvice != (result.Advice is not null) || result.Advice is { } advice && (advice.Validate() is not null
                || advice.SkillsToAdd is null || advice.Improvements is null || advice.MarketGaps is null || string.IsNullOrWhiteSpace(result.Model))
            || result.Html != WeeklyReportSteps.Compose(input.Frame, result.Advice)) throw new UnsupportedWeeklyReportException();
        return result;
    }

    internal static async Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("weekly report requires a source transaction");
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({WeeklyReportJob.LatestReportKey}, 0))", ct);
    }

    internal static async Task PublishAsync(IDbContextOutbox outbox, WeeklyReportRequest root, DateTimeOffset? at = null)
    {
        object command = root.Stage switch
        {
            WeeklyReportStage.Preparing => new PrepareWeeklyReport { RequestId = root.Id },
            WeeklyReportStage.Publishing => new PublishWeeklyReport { RequestId = root.Id },
            _ => throw new UnsupportedWeeklyReportException(),
        };
        if (at is { } due) await outbox.ScheduleAsync(command, due);
        else await outbox.PublishAsync(command);
    }
}
