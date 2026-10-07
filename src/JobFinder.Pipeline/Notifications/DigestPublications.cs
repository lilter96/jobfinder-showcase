using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Notifications;

public sealed record DigestPublicationCursor
{
    public int Version { get; set; } = 1;
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset? LastSlot { get; set; }
    public long NotificationId { get; set; }
    public long JobRunId { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DigestPublicationCursor))]
internal partial class DigestPublicationJsonContext : JsonSerializerContext;

public sealed class DigestPublications(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    Notifier notifier, DigestOptions options, LocalClock clock, TimeProvider time)
{
    internal const string CursorKey = "jobs.digest.publication";

    public async Task<DateTimeOffset?> LastSlotAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return (await ReadCursorAsync(context, ct)).Cursor?.LastSlot;
    }

    public async Task<JobOutcome> PublishAsync(JobInvocation invocation, CancellationToken ct)
    {
        var now = Normalize(invocation.StartedAt);
        if (invocation.RunId <= 0 || now == default || invocation.Manual != (invocation.Slot is null)
            || invocation.Slot is { } slot && (slot > now || now - slot > TimeSpan.FromHours(12)))
            throw new ArgumentException("digest requires a captured manual run or recent calendar slot", nameof(invocation));
        using var stage = Telemetry.StartStage("publish", "digest").Tag("job.run_id", invocation.RunId).Tag("job.manual", invocation.Manual);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({CursorKey}, 0))", ct);
        var run = await context.JobRuns.FromSqlInterpolated($"SELECT * FROM job_runs WHERE id = {invocation.RunId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (run is null || run.Job != JobNames.Digest || run.StartedAt != now || run.ManualTrigger != invocation.Manual
            || run.ScheduledSlot != (invocation.Slot is { } capturedSlot ? Normalize(capturedSlot) : null)
            || run.Status is not (JobRunStatus.Running or JobRunStatus.Ok))
            throw new InvalidOperationException("digest run witness changed");
        var (setting, cursor) = await ReadCursorAsync(context, ct);
        if (run.Status == JobRunStatus.Ok)
        {
            var existing = await context.Notifications.SingleOrDefaultAsync(notice => notice.Kind == NotificationKind.Digest && notice.EntityId == run.Id, ct);
            if (existing is not null) await VerifyNoticeAsync(context, existing, ct);
            else if (invocation.Slot is null || cursor?.LastSlot < invocation.Slot || cursor?.LastSlot is null)
                throw new InvalidOperationException("digest completion has no publication witness");
            return new(run.Items, run.Message);
        }
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        if (invocation.Slot is { } due && cursor?.LastSlot >= due)
        {
            run.Status = JobRunStatus.Ok;
            run.FinishedAt = time.GetUtcNow();
            run.Message = "слот уже опубликован";
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return new(0, run.Message);
        }
        if (cursor?.PublishedAt > now) throw new InvalidOperationException("digest run predates the published reporting window");
        var since = cursor?.PublishedAt ?? now.AddHours(-12);
        var (text, total) = await ReadAsync(context, since, now, ct);
        run.Status = JobRunStatus.Ok;
        run.Items = total;
        run.FinishedAt = time.GetUtcNow();
        notifier.Add(context, NotificationKind.Digest, text, entityId: run.Id);
        var notice = context.Notifications.Local.Single(row => context.Entry(row).State == EntityState.Added);
        notice.CreatedAt = now;
        await notifier.SaveJoinedAsync(context, outbox, ct);
        if (setting is null)
        {
            setting = new AppSetting { Key = CursorKey, Value = "" };
            context.Settings.Add(setting);
        }
        setting.Value = JsonSerializer.Serialize(new DigestPublicationCursor
        {
            PublishedAt = now, LastSlot = invocation.Slot is { } captured ? Normalize(captured) : cursor?.LastSlot,
            NotificationId = notice.Id, JobRunId = run.Id,
        }, DigestPublicationJsonContext.Default.DigestPublicationCursor);
        setting.UpdatedAt = now;
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new(total);
    }

    private static async Task<(AppSetting? Setting, DigestPublicationCursor? Cursor)> ReadCursorAsync(JobFinderDbContext context, CancellationToken ct)
    {
        var setting = await context.Settings.SingleOrDefaultAsync(row => row.Key == CursorKey, ct);
        if (setting is null) return (null, null);
        DigestPublicationCursor cursor;
        try
        {
            cursor = JsonSerializer.Deserialize(setting.Value, DigestPublicationJsonContext.Default.DigestPublicationCursor)
                ?? throw new InvalidOperationException("unsupported digest cursor");
        }
        catch (JsonException error) { throw new InvalidOperationException("unsupported digest cursor", error); }
        if (cursor.Version != 1 || cursor.PublishedAt == default || cursor.JobRunId <= 0 || cursor.NotificationId <= 0
            || cursor.LastSlot > cursor.PublishedAt) throw new InvalidOperationException("unsupported digest cursor");
        var notice = await context.Notifications.SingleOrDefaultAsync(row => row.Id == cursor.NotificationId, ct);
        if (notice is null || notice.EntityId != cursor.JobRunId || notice.CreatedAt != cursor.PublishedAt)
            throw new InvalidOperationException("digest cursor has no matching notice");
        await VerifyNoticeAsync(context, notice, ct);
        return (setting, cursor);
    }

    private static async Task VerifyNoticeAsync(JobFinderDbContext context, Notification notice, CancellationToken ct)
    {
        if (notice.Kind != NotificationKind.Digest || notice.DeliveryRequestId is not { } id)
            throw new InvalidOperationException("digest notice has no native owner");
        var root = await context.NotificationDeliveryRequests.SingleOrDefaultAsync(row => row.Id == id && row.NotificationId == notice.Id, ct);
        if (root is null || !NotificationDeliveryInputs.Input(root).Matches(notice))
            throw new InvalidOperationException("digest notice witness changed");
    }

    private async Task<(string Text, int Total)> ReadAsync(JobFinderDbContext context, DateTimeOffset since, DateTimeOffset now, CancellationToken ct)
    {
        var fresh = await context.Vacancies.Where(vacancy => vacancy.FirstSeenAt >= since).GroupBy(vacancy => vacancy.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() }).ToListAsync(ct);
        var total = fresh.Sum(row => row.Count);
        var rejected = fresh.Where(row => row.Status == VacancyStatus.Rejected).Sum(row => row.Count);
        var sent = await context.Applications.Where(application => application.SentAt >= since && application.Status == ApplicationStatus.Sent)
            .GroupBy(application => application.Mode).Select(group => new { Mode = group.Key, Count = group.Count() }).ToListAsync(ct);
        var manual = await context.Applications.CountAsync(application => application.Status == ApplicationStatus.NeedsManual && application.CreatedAt >= since, ct);
        var waiting = await context.Vacancies.CountAsync(vacancy => vacancy.Status == VacancyStatus.Scored && vacancy.Decision == Decision.Approval, ct);
        var held = await context.Applications.CountAsync(application => application.Status == ApplicationStatus.DryRun, ct);
        var comments = await context.CommentDrafts.CountAsync(draft => draft.Status == CommentStatus.Pending, ct);
        var failedRuns = await context.JobRuns.Where(run => run.StartedAt >= since && run.Status == JobRunStatus.Failed)
            .GroupBy(run => run.Job).Select(group => new { Job = group.Key, Count = group.Count() }).ToListAsync(ct);
        var local = TimeZoneInfo.ConvertTime(now, clock.Zone);
        var today = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset).ToUniversalTime();
        var spent = await context.LlmCalls.Where(call => call.At >= today).SumAsync(call => call.CostUsd, ct);
        var top = await context.Vacancies.Include(vacancy => vacancy.Sources)
            .Where(vacancy => vacancy.FirstSeenAt >= since && vacancy.Status == VacancyStatus.Scored && vacancy.Score >= 50)
            .OrderByDescending(vacancy => vacancy.Score).Take(options.TopVacancies).ToListAsync(ct);
        var text = new StringBuilder();
        text.Append(local.Hour < 14 ? "☀️" : "🌙").Append(" <b>Дайджест ").Append(local.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)).Append("</b>\n\n");
        text.Append("Новых вакансий: <b>").Append(total).Append("</b> (отсеяно фильтрами: ").Append(rejected).Append(")\n");
        text.Append("Откликов: авто <b>").Append(sent.Where(row => row.Mode == ApplicationMode.Auto).Sum(row => row.Count))
            .Append("</b>, одобренных <b>").Append(sent.Where(row => row.Mode == ApplicationMode.Approved).Sum(row => row.Count)).Append("</b>");
        if (manual > 0) text.Append(", нужно вручную: ").Append(manual);
        text.Append('\n');
        if (held > 0) text.Append("🧪 Dry-run держит откликов: <b>").Append(held).Append("</b>, уйдут после /dryrun off\n");
        if (waiting > 0) text.Append("Ждут вашего решения: <b>").Append(waiting).Append("</b> вакансий\n");
        if (comments > 0) text.Append("Черновиков комментариев Threads: ").Append(comments).Append('\n');
        text.Append("LLM сегодня: $").Append(spent.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        if (failedRuns.Count > 0) text.Append("⚠️ Ошибки джобов: ").Append(string.Join(", ", failedRuns.Select(row => $"{row.Job}×{row.Count}"))).Append('\n');
        if (top.Count > 0)
        {
            text.Append("\n<b>Лучшее за период</b>\n");
            foreach (var vacancy in top)
            {
                text.Append(Cards.ScoreBadge(vacancy.Score)).Append(' ').Append(Cards.Link(vacancy.Url, TextUtil.Truncate(vacancy.Title, 60)));
                if (vacancy.CompanyName is not null) text.Append(" — ").Append(TextUtil.EscapeHtml(TextUtil.Truncate(vacancy.CompanyName, 40)));
                text.Append(" · ").Append(vacancy.Decision switch { Decision.AutoApply => "авто", Decision.Approval => "ждёт", _ => "ниже порога" }).Append('\n');
            }
        }
        return (text.ToString(), total);
    }

    private static DateTimeOffset Normalize(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
