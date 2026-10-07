using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class WeeklyReportSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, JobFinderLlm llm,
    WeeklyReportCapture capture, ProfileProvider profiles, Notifier notifier, TimeProvider time)
{
    internal const string OwnerKey = "report.weekly.owner";
    internal const string AdviceOwnerKey = "report.hhAdvice.owner";
    internal const string CursorKey = "jobs.weekly-report.publication";
    public static bool OwnsKey(string key) => key is WeeklyReportJob.LatestReportKey or WeeklyReportJob.LatestAdviceKey or OwnerKey or AdviceOwnerKey or CursorKey;

    public async Task<Guid> RequestAsync(CancellationToken ct, JobInvocation? invocation = null, bool force = false)
    {
        var operation = await llm.ForOperationAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await WeeklyReportRequests.LockAsync(context, WeeklyReportJob.LatestReportKey, ct);
        var run = invocation is { } job ? await RunAsync(context, job, ct) : null;
        var cursor = await ReadCursorAsync(context, ct);
        var owner = await ValidateReportAsync(context, ct);
        await ValidateAdviceAsync(context, ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        if (run?.WeeklyReportRequestId is { } admitted)
        {
            var root = await context.WeeklyReportRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == admitted, ct) ?? throw new UnsupportedWeeklyReportException();
            await VerifyNoticeAsync(context, root, ct);
            var original = WeeklyReportRequests.ReadInput(root);
            if (run.Status != JobRunStatus.Ok || original.JobRunId == run.Id
                && (original.JobStartedAt != run.StartedAt || original.Manual != run.ManualTrigger || original.Slot != run.ScheduledSlot)
                || original.JobRunId != run.Id && (run.ManualTrigger != false || run.ScheduledSlot is null || original.Manual
                    || original.Slot is null || original.Slot < run.ScheduledSlot)) throw new UnsupportedWeeklyReportException();
            return admitted;
        }
        if (invocation?.Slot is { } due && cursor?.Slot >= Normalize(due))
        {
            run!.WeeklyReportRequestId = cursor.RequestId;
            CompleteRun(run, 0, "слот уже опубликован", time.GetUtcNow());
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return cursor.RequestId;
        }
        var now = invocation is { } captured ? Normalize(captured.StartedAt) : Normalize(time.GetUtcNow());
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        var resume = profiles.ResumeText() ?? "";
        var coverage = resume + " " + string.Join(" ", profile is null ? [] : ProfileProvider.FromJson(profile.Content).Skills.Select(skill => skill.Name));
        var frame = await capture.CaptureAsync(context, now, coverage, ct);
        if (invocation is null && !force && owner is not null)
        {
            var previous = WeeklyReportRequests.ReadInput(owner);
            if (owner.Stage is WeeklyReportStage.Preparing or WeeklyReportStage.Publishing or WeeklyReportStage.Completed
                && previous.JobRunId is null && previous.CapturedAt.UtcDateTime.Date == now.UtcDateTime.Date
                && previous.Frame.HeaderHtml == frame.HeaderHtml && previous.Frame.DemandText == frame.DemandText && previous.Frame.FunnelText == frame.FunnelText
                && previous.ProfileVersion == (profile?.Version ?? 0) && SameProfile(previous.ProfileJson, profile?.Content) && previous.ResumeText == resume
                && WeeklyReportSteps.Recipe(operation, previous).Hash == previous.Recipe.Hash)
                return owner.Id;
        }
        var input = new WeeklyReportInput
        {
            ProfileJson = profile?.Content, ProfileVersion = profile?.Version ?? 0, ResumeText = resume, Frame = frame,
            CapturedAt = now, JobRunId = invocation?.RunId, JobStartedAt = invocation is { } started ? Normalize(started.StartedAt) : null,
            Manual = invocation?.Manual ?? true, Slot = invocation?.Slot is { } slot ? Normalize(slot) : null, Operation = operation.CaptureOperation(),
        };
        input.Recipe = WeeklyReportSteps.Recipe(operation, input);
        var inputJson = JsonSerializer.Serialize(input, WeeklyReportJsonContext.Default.WeeklyReportInput);
        var request = new WeeklyReportRequest { Id = input.Operation.OperationId, BusinessKey = WeeklyReportRequests.Key(input),
            InputJson = inputJson, InputHash = CanonicalJson.Hash(inputJson), CreatedAt = now, Stage = WeeklyReportStage.Preparing, JobRunId = input.JobRunId };
        var duplicate = await context.WeeklyReportRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == request.BusinessKey, ct);
        if (duplicate is not null)
        {
            await VerifyNoticeAsync(context, duplicate, ct);
            if (run is null) throw new UnsupportedWeeklyReportException();
            run.WeeklyReportRequestId = duplicate.Id;
            CompleteRun(run, 0, "слот уже опубликован", time.GetUtcNow());
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return duplicate.Id;
        }
        var statistics = WeeklyReportSteps.Statistics(input, now);
        if (!input.NeedsAdvice)
        {
            request.Stage = WeeklyReportStage.Completed;
            request.ResultJson = JsonSerializer.Serialize(statistics, WeeklyReportJsonContext.Default.WeeklyReportResult);
            request.ResultHash = CanonicalJson.Hash(request.ResultJson);
            request.CompletedAt = now;
        }
        context.WeeklyReportRequests.Add(request);
        if (owner is null || WeeklyReportRequests.ReadInput(owner).CapturedAt <= now)
        {
            request.PublishedHtmlHash = DeliveryLedger.Hash(statistics.Html);
            await SetAsync(context, WeeklyReportJob.LatestReportKey, statistics.Html, now, ct);
            await SetAsync(context, OwnerKey, request.Id.ToString("D"), now, ct);
        }
        notifier.Add(context, run is null ? NotificationKind.Info : NotificationKind.Digest,
            run is null ? "📊 Статистика недели сохранена в «Аналитике»." + (input.NeedsAdvice ? " Советы hh готовятся отдельно." : "") : statistics.Html,
            entityId: run?.Id);
        var notice = context.Notifications.Local.Single(row => context.Entry(row).State == EntityState.Added);
        await notifier.SaveJoinedAsync(context, outbox, ct);
        request.NotificationId = notice.Id;
        WeeklyReportRequests.ReadInput(request);
        if (run is not null)
        {
            run.WeeklyReportRequestId = request.Id;
            CompleteRun(run, frame.Sent, input.NeedsAdvice ? "статистика опубликована; советы hh готовятся отдельно" : null, time.GetUtcNow());
        }
        if (input.Slot is { } planned)
            await SetAsync(context, CursorKey, JsonSerializer.Serialize(new WeeklyReportCursor
            {
                Slot = planned, RequestId = request.Id, JobRunId = run!.Id, NotificationId = notice.Id,
            }, WeeklyReportJsonContext.Default.WeeklyReportCursor), now, ct);
        if (input.NeedsAdvice) await WeeklyReportRequests.PublishAsync(outbox, request);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return request.Id;
    }

    internal async Task<bool> MatchesAsync(JobFinderDbContext context, WeeklyReportInput input, CancellationToken ct)
    {
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        if (await MarkerAsync(context, OwnerKey, ct) != input.Operation.OperationId || (profile?.Version ?? 0) != input.ProfileVersion
            || !SameProfile(input.ProfileJson, profile?.Content) || (profiles.ResumeText() ?? "") != input.ResumeText) return false;
        await ValidateReportAsync(context, ct);
        await ValidateAdviceAsync(context, ct);
        if (input.JobRunId is { } id)
        {
            var run = await context.JobRuns.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
            if (run is null || run.Job != JobNames.WeeklyReport || run.StartedAt != input.JobStartedAt || run.ScheduledSlot != input.Slot
                || run.ManualTrigger != input.Manual || run.WeeklyReportRequestId != input.Operation.OperationId || run.Status != JobRunStatus.Ok)
                throw new UnsupportedWeeklyReportException();
        }
        return true;
    }

    internal async Task<bool> PublishJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, WeeklyReportInput input, WeeklyReportResult result, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || !ReferenceEquals(outbox.ActiveContext, context)) throw new InvalidOperationException("weekly publication requires its enrolled transaction");
        if (!await MatchesAsync(context, input, ct)) return false;
        var root = await context.WeeklyReportRequests.SingleAsync(row => row.Id == input.Operation.OperationId, ct);
        var now = time.GetUtcNow();
        await SetAsync(context, WeeklyReportJob.LatestReportKey, result.Html, now, ct);
        root.PublishedHtmlHash = DeliveryLedger.Hash(result.Html);
        if (result.Advice is not null)
        {
            await SetAsync(context, WeeklyReportJob.LatestAdviceKey, JsonSerializer.Serialize(result.Advice, CareerJsonContext.Default.HhResumeAdvice), now, ct);
            await SetAsync(context, AdviceOwnerKey, root.Id.ToString("D"), now, ct);
            root.AdvicePublished = true;
            notifier.Add(context, NotificationKind.Info, "📊 Советы к недельному отчёту\n\n" + WeeklyReportSteps.AdviceHtml(result.Advice));
        }
        return true;
    }

    public async Task<(string? Html, HhResumeAdvice? Advice)> LatestAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await WeeklyReportRequests.LockAsync(context, WeeklyReportJob.LatestReportKey, ct);
        await ValidateReportAsync(context, ct);
        await ValidateAdviceAsync(context, ct);
        var advice = await ValueAsync(context, WeeklyReportJob.LatestAdviceKey, ct);
        return (await ValueAsync(context, WeeklyReportJob.LatestReportKey, ct), advice is null ? null : JsonSerializer.Deserialize(advice, CareerJsonContext.Default.HhResumeAdvice));
    }

    public async Task<DateTimeOffset?> LastSlotAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await WeeklyReportRequests.LockAsync(context, WeeklyReportJob.LatestReportKey, ct);
        return (await ReadCursorAsync(context, ct))?.Slot;
    }

    private static async Task<WeeklyReportRequest?> ValidateReportAsync(JobFinderDbContext context, CancellationToken ct)
    {
        if (await MarkerAsync(context, OwnerKey, ct) is not { } id) return null;
        var root = await context.WeeklyReportRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct) ?? throw new UnsupportedWeeklyReportException();
        var input = WeeklyReportRequests.ReadInput(root);
        var html = await ValueAsync(context, WeeklyReportJob.LatestReportKey, ct);
        var expected = root.AdvicePublished ? WeeklyReportRequests.ReadResult(root).Html : WeeklyReportSteps.Statistics(input, input.CapturedAt).Html;
        if (html is null || root.PublishedHtmlHash != DeliveryLedger.Hash(html) || html != expected
            || root.AdvicePublished && root.Stage != WeeklyReportStage.Completed) throw new UnsupportedWeeklyReportException();
        await VerifyNoticeAsync(context, root, ct);
        return root;
    }

    private static async Task ValidateAdviceAsync(JobFinderDbContext context, CancellationToken ct)
    {
        if (await MarkerAsync(context, AdviceOwnerKey, ct) is not { } id) return;
        var root = await context.WeeklyReportRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct) ?? throw new UnsupportedWeeklyReportException();
        var result = WeeklyReportRequests.ReadResult(root);
        var json = await ValueAsync(context, WeeklyReportJob.LatestAdviceKey, ct);
        if (!root.AdvicePublished || root.Stage != WeeklyReportStage.Completed || result.Advice is null || json is null
            || CanonicalJson.Hash(json) != CanonicalJson.Hash(JsonSerializer.Serialize(result.Advice, CareerJsonContext.Default.HhResumeAdvice))) throw new UnsupportedWeeklyReportException();
    }

    private static async Task<WeeklyReportCursor?> ReadCursorAsync(JobFinderDbContext context, CancellationToken ct)
    {
        var json = await ValueAsync(context, CursorKey, ct);
        if (json is null) return null;
        var cursor = JsonSerializer.Deserialize(json, WeeklyReportJsonContext.Default.WeeklyReportCursor) ?? throw new UnsupportedWeeklyReportException();
        if (cursor.Version != 1 || cursor.Slot == default || cursor.RequestId == Guid.Empty || cursor.JobRunId <= 0 || cursor.NotificationId <= 0)
            throw new UnsupportedWeeklyReportException();
        var root = await context.WeeklyReportRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == cursor.RequestId, ct) ?? throw new UnsupportedWeeklyReportException();
        var input = WeeklyReportRequests.ReadInput(root);
        var run = await context.JobRuns.AsNoTracking().SingleOrDefaultAsync(row => row.Id == cursor.JobRunId, ct);
        if (input.Slot != cursor.Slot || input.JobRunId != cursor.JobRunId || root.NotificationId != cursor.NotificationId
            || run is null || run.Job != JobNames.WeeklyReport || run.Status != JobRunStatus.Ok || run.WeeklyReportRequestId != root.Id
            || run.ScheduledSlot != cursor.Slot || run.ManualTrigger != false || run.StartedAt != input.JobStartedAt) throw new UnsupportedWeeklyReportException();
        await VerifyNoticeAsync(context, root, ct);
        return cursor;
    }

    private static async Task VerifyNoticeAsync(JobFinderDbContext context, WeeklyReportRequest root, CancellationToken ct)
    {
        var input = WeeklyReportRequests.ReadInput(root);
        var notice = await context.Notifications.AsNoTracking().SingleOrDefaultAsync(row => row.Id == root.NotificationId, ct);
        if (notice?.DeliveryRequestId is not { } id || notice.EntityId != input.JobRunId
            || notice.Kind != (input.JobRunId is null ? NotificationKind.Info : NotificationKind.Digest)
            || input.JobRunId is not null && notice.Text != WeeklyReportSteps.Statistics(input, input.CapturedAt).Html) throw new UnsupportedWeeklyReportException();
        var owner = await context.NotificationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.NotificationId == notice.Id, ct);
        if (owner is null || !NotificationDeliveryInputs.Input(owner).Matches(notice)) throw new UnsupportedWeeklyReportException();
    }

    private static async Task<JobRun> RunAsync(JobFinderDbContext context, JobInvocation invocation, CancellationToken ct)
    {
        var at = Normalize(invocation.StartedAt);
        if (invocation.RunId <= 0 || at == default || invocation.Manual != (invocation.Slot is null)
            || invocation.Slot is { } due && (due > at || at - due > TimeSpan.FromHours(12))) throw new ArgumentException("weekly report requires a captured manual run or recent slot", nameof(invocation));
        var run = await context.JobRuns.FromSqlInterpolated($"SELECT * FROM job_runs WHERE id = {invocation.RunId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (run is null || run.Job != JobNames.WeeklyReport || run.StartedAt != at || run.ManualTrigger != invocation.Manual
            || run.ScheduledSlot != (invocation.Slot is { } slot ? Normalize(slot) : null)
            || run.Status is not (JobRunStatus.Running or JobRunStatus.Ok) || run.Status == JobRunStatus.Ok && run.WeeklyReportRequestId is null)
            throw new UnsupportedWeeklyReportException();
        return run;
    }

    private static void CompleteRun(JobRun run, int sent, string? message, DateTimeOffset now)
    {
        run.Status = JobRunStatus.Ok;
        run.Items = sent;
        run.Message = message;
        run.FinishedAt = now;
    }

    private static bool SameProfile(string? left, string? right) => left is null || right is null ? left == right : CanonicalJson.Hash(left) == CanonicalJson.Hash(right);
    private static DateTimeOffset Normalize(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    private static async Task<Guid?> MarkerAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        var value = await ValueAsync(context, key, ct);
        if (value is null) return null;
        return Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new UnsupportedWeeklyReportException();
    }
    private static Task<string?> ValueAsync(JobFinderDbContext context, string key, CancellationToken ct) => context.Settings.AsNoTracking().Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(ct);
    private static async Task SetAsync(JobFinderDbContext context, string key, string value, DateTimeOffset at, CancellationToken ct)
    {
        var row = await context.Settings.FindAsync([key], ct);
        if (row is null) context.Settings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = at });
        else { row.Value = value; row.UpdatedAt = at; }
    }
}
