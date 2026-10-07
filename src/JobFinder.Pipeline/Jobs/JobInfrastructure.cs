using System.Collections.Concurrent;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Http;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Pipeline.Jobs;

public readonly record struct JobOutcome(int Items, string? Message = null);

public readonly record struct JobInvocation(long RunId, DateTimeOffset StartedAt, bool Manual, DateTimeOffset? Slot);

/// <summary>"Run now" from the dashboard or the bot: wakes the named job (or worker) out of its wait.</summary>
public sealed class JobTriggers
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _signals.Keys.ToList();

    public void Register(string name) => _signals.GetOrAdd(name, _ => new SemaphoreSlim(0, 1));

    public bool Trigger(string name)
    {
        if (!_signals.TryGetValue(name, out var signal)) return false;
        if (signal.CurrentCount == 0) signal.Release();
        return true;
    }

    /// <summary>Waits for <paramref name="delay"/> or a trigger, whichever comes first.</summary>
    public async Task WaitAsync(string name, TimeSpan delay, TimeProvider time, CancellationToken ct) =>
        await WaitForSignalAsync(name, delay, time, ct);

    public async Task<bool> WaitForSignalAsync(string name, TimeSpan delay, TimeProvider time, CancellationToken ct)
    {
        var signal = _signals.GetOrAdd(name, _ => new SemaphoreSlim(0, 1));
        if (delay <= TimeSpan.Zero) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(delay, time, cts.Token);
        var trigger = signal.WaitAsync(cts.Token);
        var completed = await Task.WhenAny(timer, trigger);
        await cts.CancelAsync();
        ct.ThrowIfCancellationRequested();
        return completed == trigger && trigger.IsCompletedSuccessfully;
    }
}

/// <summary>
/// A periodic job: runs, records a <see cref="JobRun"/>, sleeps until <see cref="NextRunAfter"/> (or a trigger). An exception fails the
/// run, not the host; a source that blocks the scraper raises one alert per hour instead of one per run.
/// </summary>
public abstract class ScheduledJob(IDbContextFactory<JobFinderDbContext> db, JobTriggers triggers, Notifier notifier, TimeProvider time,
    ILogger log) : BackgroundService
{
    private bool _manualTrigger;

    public abstract string Name { get; }

    protected abstract bool Enabled { get; }

    protected virtual TimeSpan? ConfigurationRefreshInterval => null;

    protected virtual long ConfigurationRevision => 0;

    protected virtual Task RefreshConfigurationAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>First run shortly after start (staggered, so the jobs do not all hit the network in the same second).</summary>
    protected virtual TimeSpan FirstDelay => TimeSpan.FromSeconds(Random.Shared.Next(10, 60));

    protected abstract DateTimeOffset NextRunAfter(DateTimeOffset finishedAt);

    /// <summary>
    /// A time-of-day job's latest slot at or before <paramref name="now"/>; null for interval jobs. With it, a slot missed while the app
    /// was down runs on start (seen 2026-09-28: the host was down at 19:00 and the evening digest never came) — see
    /// <see cref="ShouldCatchUp"/>. Interval jobs need nothing: their first run comes shortly after start anyway.
    /// </summary>
    protected virtual DateTimeOffset? LastSlot(DateTimeOffset now) => null;

    /// <summary>A slot older than this is not caught up: a digest from yesterday morning is stale news.</summary>
    protected virtual TimeSpan CatchUpWindow => TimeSpan.FromHours(12);

    /// <summary>The next regular slot is this close: it is waited for instead (no two digests an hour apart).</summary>
    public static readonly TimeSpan CatchUpMinLead = TimeSpan.FromHours(1);

    /// <summary>
    /// Run a missed slot now: there is a recent slot, no successful run started at it (or after), and the next slot is not about to
    /// come anyway.
    /// </summary>
    public static bool ShouldCatchUp(DateTimeOffset now, DateTimeOffset? slot, DateTimeOffset nextSlot, DateTimeOffset? lastOkStart, TimeSpan window) =>
        slot is { } due && due <= now && now - due <= window && nextSlot - now >= CatchUpMinLead
        && (lastOkStart is null || lastOkStart < due - TimeSpan.FromMinutes(2));

    protected abstract Task<JobOutcome> RunOnceAsync(CancellationToken ct);

    protected virtual Task<JobOutcome> RunForInvocationAsync(JobInvocation invocation, CancellationToken ct) => RunOnceAsync(ct);

    protected virtual async Task<DateTimeOffset?> LastSuccessfulStartAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.JobRuns.Where(run => run.Job == Name && run.Status == JobRunStatus.Ok)
            .OrderByDescending(run => run.StartedAt).Select(run => (DateTimeOffset?)run.StartedAt).FirstOrDefaultAsync(ct);
    }

    protected TimeProvider Time => time;

    protected JobTriggers Triggers => triggers;

    protected Notifier Notifier => notifier;

    private static readonly TimeZoneInfo Minsk = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");

    /// <summary>A fresh block is announced once with the end of the pause; requests refused during the pause are not news.</summary>
    protected Task AlertBlockedAsync(SourceBlockedException e) => e.Fresh && !e.Announced
        ? notifier.AlertAsync($"blocked:{JobFinder.Integrations.Http.PoliteHttp.Family(e.Host)}", $"{e.Host} блокирует сбор",
            $"{e.Message}. Сбор с этого сайта на паузе" +
            (e.Until is { } until ? $" до {TimeZoneInfo.ConvertTime(until, Minsk):HH:mm}" : "") +
            ": повторные запросы только продлевают блокировку. Чтение чатов браузером тоже ждёт конца паузы.",
            TimeSpan.FromHours(1), CancellationToken.None)
        : Task.CompletedTask;

    protected static TimeSpan Jittered(int minutes, int jitterMinutes) =>
        TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(Random.Shared.Next(0, Math.Max(1, jitterMinutes * 60)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        triggers.Register(Name);
        await RefreshConfigurationAsync(stoppingToken);
        if (!Enabled && ConfigurationRefreshInterval is null)
        {
            log.Info($"[{Name}] disabled in config");
            return;
        }

        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshConfigurationAsync(stoppingToken);
            if (!Enabled)
            {
                first = true;
                await triggers.WaitAsync(Name, ConfigurationRefreshInterval ?? TimeSpan.FromMinutes(1), time, stoppingToken);
                continue;
            }

            if (first && !await WaitDueAsync(time.GetUtcNow(), true, stoppingToken)) continue;
            first = false;
            await RunRecordedAsync(stoppingToken);
            if (!await WaitDueAsync(time.GetUtcNow(), false, stoppingToken)) first = true;
        }
    }

    private async Task<bool> WaitDueAsync(DateTimeOffset finishedAt, bool first, CancellationToken ct)
    {
        _manualTrigger = false;
        var next = first ? time.GetUtcNow() + (await CatchUpAsync(ct) ?? FirstDelay) : NextRunAfter(finishedAt);
        var revision = ConfigurationRevision;
        while (next > time.GetUtcNow())
        {
            var delay = next - time.GetUtcNow();
            if (ConfigurationRefreshInterval is { } refresh && delay > refresh) delay = refresh;
            if (await triggers.WaitForSignalAsync(Name, delay, time, ct))
            {
                _manualTrigger = true;
                await RefreshConfigurationAsync(ct);
                return Enabled;
            }
            await RefreshConfigurationAsync(ct);
            if (!Enabled) return false;
            if (revision == ConfigurationRevision) continue;
            revision = ConfigurationRevision;
            next = first ? time.GetUtcNow() + FirstDelay : NextRunAfter(finishedAt);
        }
        await RefreshConfigurationAsync(ct);
        return Enabled;
    }

    /// <summary>A short first delay when a slot was missed (<see cref="ShouldCatchUp"/>), null otherwise.</summary>
    private async Task<TimeSpan?> CatchUpAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (LastSlot(now) is not { } slot) return null;
        var lastOk = await LastSuccessfulStartAsync(ct);

        if (!ShouldCatchUp(now, slot, NextRunAfter(now), lastOk, CatchUpWindow)) return null;
        log.Info($"[{Name}] the {TimeZoneInfo.ConvertTime(slot, Minsk):dd.MM HH:mm} run was missed (the app was down), catching up now");
        return TimeSpan.FromSeconds(Random.Shared.Next(20, 60));
    }

    private async Task RunRecordedAsync(CancellationToken ct)
    {
        using var span = Core.Telemetry.Telemetry.Start($"job {Name}");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var startedAt = time.GetUtcNow();
        var run = new JobRun
        {
            Job = Name, StartedAt = startedAt, Status = JobRunStatus.Running, ManualTrigger = _manualTrigger,
            ScheduledSlot = _manualTrigger ? null : LastSlot(startedAt),
        };
        await using (var ctx = await db.CreateDbContextAsync(ct))
        {
            ctx.JobRuns.Add(run);
            await ctx.SaveChangesAsync(ct);
        }

        try
        {
            var invocation = new JobInvocation(run.Id, run.StartedAt, run.ManualTrigger!.Value, run.ScheduledSlot);
            var outcome = await RunForInvocationAsync(invocation, ct);
            run.Status = JobRunStatus.Ok;
            run.Items = outcome.Items;
            run.Message = outcome.Message;
            log.Info($"[{Name}] done: {outcome.Items} items{(outcome.Message is null ? "" : " — " + outcome.Message)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = JobRunStatus.Failed;
            run.Message = "stopped";
        }
        catch (SourceBlockedException e) when (!e.Fresh)
        {
            // Refused locally (the pause after a block, or the site's request budget): waiting is the plan, not a failure.
            run.Status = JobRunStatus.Ok;
            run.Message = "⏸ " + e.Message;
            log.Info($"[{Name}] {e.Message}");
        }
        catch (SourceBlockedException e)
        {
            run.Status = JobRunStatus.Failed;
            run.Message = e.Message;
            log.Warn($"[{Name}] {e.Message}");
            await AlertBlockedAsync(e);
        }
        catch (LlmUnavailableException e)
        {
            run.Status = JobRunStatus.Failed;
            run.Message = e.Message;
            log.Warn($"[{Name}] {e.Message}");
        }
        catch (Exception e)
        {
            run.Status = JobRunStatus.Failed;
            run.Message = e.Message.Length > 900 ? e.Message[..900] : e.Message;
            log.Error($"[{Name}] failed: {e.Message}", e);
        }

        run.FinishedAt = time.GetUtcNow();
        await using var save = await db.CreateDbContextAsync(CancellationToken.None);
        await save.JobRuns.Where(current => current.Id == run.Id && current.Status == JobRunStatus.Running)
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.Status, run.Status)
                .SetProperty(current => current.Items, run.Items).SetProperty(current => current.Message, run.Message)
                .SetProperty(current => current.FinishedAt, run.FinishedAt), CancellationToken.None);
        run = await save.JobRuns.AsNoTracking().SingleAsync(current => current.Id == run.Id, CancellationToken.None);
        span?.SetTag("job.status", run.Status.ToString());
        span?.SetTag("job.items", run.Items);
        if (run.Message is not null) span?.SetTag("job.message", run.Message);
        if (run.Status == JobRunStatus.Failed) span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, run.Message);
        var jobTags = new System.Diagnostics.TagList { { "job", Name }, { "status", run.Status.ToString() } };
        Core.Telemetry.Telemetry.JobRuns.Add(1, jobTags);
        Core.Telemetry.Telemetry.JobDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, jobTags);
    }
}

/// <summary>
/// A queue consumer cannot go on for now for a reason outside its items (no model answers, a budget is spent): the items stay queued,
/// untouched, and the worker waits <see cref="Delay"/> (a trigger wakes it early). Not a failure: no error, no backoff streak.
/// </summary>
public sealed class QueuePausedException(TimeSpan delay, string reason) : Exception(reason)
{
    public TimeSpan Delay { get; } = delay;
}

/// <summary>
/// A queue consumer over a status column: processes a batch, and when the queue is empty sleeps (a trigger — e.g. the ingest of a new
/// vacancy — wakes it early). Failures back off instead of spinning.
/// </summary>
public abstract class QueueWorker(JobTriggers triggers, TimeProvider time, ILogger log) : BackgroundService
{
    public abstract string Name { get; }

    protected virtual TimeSpan IdleDelay => TimeSpan.FromSeconds(30);

    protected TimeProvider Time => time;

    protected JobTriggers Triggers => triggers;

    protected virtual bool Enabled => true;

    /// <returns>How many items were handled; 0 = the queue is empty (or blocked) and the worker sleeps.</returns>
    protected abstract Task<int> ProcessBatchAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        triggers.Register(Name);
        if (!Enabled) return;
        await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            int handled;
            try
            {
                using (var span = Core.Telemetry.Telemetry.Start($"worker {Name}"))
                {
                    handled = await ProcessBatchAsync(stoppingToken);
                    span?.SetTag("worker.items", handled);
                    // An empty poll is not worth a trace.
                    if (handled == 0 && span is not null)
                    {
                        span.ActivityTraceFlags &= ~System.Diagnostics.ActivityTraceFlags.Recorded;
                        span.IsAllDataRequested = false;
                    }
                }

                if (handled > 0) Core.Telemetry.Telemetry.WorkerItems.Add(handled, new KeyValuePair<string, object?>("worker", Name));
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (QueuePausedException pause)
            {
                log.Info($"[{Name}] paused for {pause.Delay.TotalMinutes:0.#} min: {pause.Message}");
                failures = 0;
                await triggers.WaitAsync(Name, pause.Delay, time, stoppingToken);
                continue;
            }
            catch (Exception e)
            {
                failures++;
                log.Error($"[{Name}] batch failed ({failures} in a row): {e.Message}", e);
                handled = 0;
            }

            if (handled > 0) continue;
            var backoff = failures == 0 ? IdleDelay : TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, Math.Min(failures, 6))));
            await triggers.WaitAsync(Name, backoff, time, stoppingToken);
        }
    }
}

public static class JobNames
{
    public const string HhSearch = "hh-search";
    public const string HabrSearch = "habr-search";
    public const string Companies = "companies";
    public const string Threads = "threads";
    public const string Digest = "digest";
    public const string Housekeeping = "housekeeping";
    public const string TelegramSync = "telegram-sync";
    public const string Posts = "posts";
    public const string Vacancies = "vacancies";
    public const string Apply = "apply";
    public const string Comments = "comments";
    public const string Notifications = "notifications";
    public const string Resumes = "resumes";
    public const string Interviews = "interviews";
    public const string InterviewReminders = "interview-reminders";
    public const string EmailInbox = "email-inbox";
    public const string HhNegotiations = "hh-negotiations";
    public const string FollowUps = "follow-ups";
    public const string Outreach = "outreach";
    public const string WeeklyReport = "weekly-report";
    public const string HrPlaybook = "hr-playbook";
    public const string HhLinks = "hh-links";
}
