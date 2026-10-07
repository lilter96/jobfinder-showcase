using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Core.Time;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record ApplicationDeliveryControl(DateTimeOffset? RetryAt = null, string? Stop = null, bool DryRun = false);

public interface IApplicationDeliveryControls
{
    Task<ApplicationDeliveryControl> ReadAsync(ApplicationDeliveryInput input, CancellationToken ct);
}

public sealed class ApplicationDeliveryControls(RuntimeSettings settings, TelegramUserClient telegram, EmailSender email,
    HhApplier hh, TimeProvider time, IDbContextFactory<JobFinderDbContext> db, LocalClock clock) : IApplicationDeliveryControls
{
    internal const string NextSend = "apply.native.nextSendAt";

    public async Task<ApplicationDeliveryControl> ReadAsync(ApplicationDeliveryInput input, CancellationToken ct)
    {
        var policy = (await settings.Automation.ReadAsync(ct)).Value;
        if (input.Mode == ApplicationMode.Auto && (input.Automation.AutoApplyPaused || policy.AutoApplyPaused
            || input.Decision != Decision.AutoApply || input.Score is null || input.Score < Math.Max(input.Automation.AutoApplyThreshold, policy.AutoApplyThreshold)))
            return new(Stop: "автоотклик больше не разрешён политикой; требуется ручное одобрение");
        if (input.Mode == ApplicationMode.Auto)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var since = clock.StartOfTodayUtc();
            var used = await context.DeliveryAttempts.CountAsync(row => row.Application!.Mode == ApplicationMode.Auto && row.CreatedAt >= since
                && (row.Status == DeliveryAttemptStatus.Submitting || row.Status == DeliveryAttemptStatus.Confirmed
                    || row.Status == DeliveryAttemptStatus.OutcomeUnknown || row.Status == DeliveryAttemptStatus.Reconciled || row.Status == DeliveryAttemptStatus.DryRun), ct);
            if (used >= Math.Min(input.Automation.DailyAutoApplyLimit, policy.DailyAutoApplyLimit))
                return new(RetryAt: clock.NextOccurrenceUtc(TimeOnly.MinValue));
        }
        if (input.Channel == ApplicationChannel.HeadHunter && await settings.GetInstantAsync(RuntimeSettings.ApplyBlockedUntil, ct) is { } blocked && blocked > time.GetUtcNow())
            return new(RetryAt: blocked);
        if (input.Channel == ApplicationChannel.TelegramDm && !telegram.IsReady || input.Channel == ApplicationChannel.Email && !email.IsConfigured
            || input.Channel == ApplicationChannel.HeadHunter && !await hh.HasSessionAsync(ct))
            return new(RetryAt: time.GetUtcNow().AddMinutes(1));
        if (await settings.GetAsync(NextSend, ct) is { } cursor)
        {
            if (!DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var next))
                return new(Stop: "повреждён курсор интервала откликов; исправьте его перед отправкой");
            if (next > time.GetUtcNow()) return new(RetryAt: next);
        }
        return new(DryRun: input.Automation.ApplyDryRun || policy.ApplyDryRun);
    }
}
