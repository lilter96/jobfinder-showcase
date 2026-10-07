using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Notifications;

/// <summary>
/// Writes bot messages into the outbox. <see cref="Add"/> takes the caller's context, so a message commits (or not) together with the
/// state change it reports; the dispatcher in the bot sends it later.
/// </summary>
public sealed class Notifier(IDbContextFactory<JobFinderDbContext> db, TimeProvider time, NotificationDeliveryRequests notifications)
{
    public void Add(JobFinderDbContext ctx, NotificationKind kind, string text, List<List<string>>? buttons = null, long? entityId = null,
        string? attachmentPath = null, string? attachmentName = null) =>
        ctx.Notifications.Add(new Notification
        {
            Kind = kind,
            Text = text,
            Buttons = buttons ?? [],
            EntityId = entityId,
            Status = NotificationStatus.Pending,
            CreatedAt = time.GetUtcNow(),
            AttachmentPath = attachmentPath,
            AttachmentName = attachmentName,
        });

    public async Task SendAsync(NotificationKind kind, string text, List<List<string>>? buttons = null, long? entityId = null,
        CancellationToken ct = default)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        Add(ctx, kind, text, buttons, entityId);
        await notifications.SaveAndFlushAsync(ctx, ct);
    }

    /// <summary>An alert at most once per <paramref name="every"/> per key (a captcha loop must not flood the chat).</summary>
    public async Task AlertAsync(string key, string title, string details, TimeSpan every, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || every <= TimeSpan.Zero) throw new ArgumentException("an alert requires a key and positive cooldown");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var settingKey = RuntimeSettings.AlertPrefix + key;
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({settingKey}, 0))", ct);
        var now = time.GetUtcNow();
        var setting = await context.Settings.SingleOrDefaultAsync(row => row.Key == settingKey, ct);
        if (setting is not null && DateTimeOffset.TryParse(setting.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
            && now - last < every) return;
        if (setting is null)
        {
            setting = new() { Key = settingKey, Value = "" };
            context.Settings.Add(setting);
        }
        setting.Value = now.ToString("O", CultureInfo.InvariantCulture);
        setting.UpdatedAt = now;
        Add(context, NotificationKind.Alert, Cards.Alert(title, details));
        await notifications.SaveAndFlushAsync(context, ct);
    }

    public Task SaveJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, CancellationToken ct) =>
        notifications.SaveJoinedAsync(context, outbox, ct);

    public Task SaveAndFlushAsync(JobFinderDbContext context, IDbContextOutbox outbox, CancellationToken ct) =>
        notifications.SaveAndFlushAsync(context, outbox, ct);

    public Task SaveAndFlushAsync(JobFinderDbContext context, CancellationToken ct) => notifications.SaveAndFlushAsync(context, ct);
}
