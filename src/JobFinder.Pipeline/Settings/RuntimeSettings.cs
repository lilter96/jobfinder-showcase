using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Settings;

/// <summary>
/// Settings the user changes at runtime (dashboard, bot) without a restart. Config gives the defaults, the <c>settings</c> table the
/// overrides. Values are plain strings; the typed accessors below are the only readers.
/// </summary>
public sealed class RuntimeSettings(IDbContextFactory<JobFinderDbContext> db, DecisionOptions defaults, TimeProvider time, PgPolicyStore policies)
{
    public AutomationPolicyStore Automation { get; } = new(policies, defaults);
    public const string AutoApplyThreshold = "decision.autoApplyThreshold";
    public const string ApprovalThreshold = "decision.approvalThreshold";
    public const string DailyAutoApplyLimit = "decision.dailyAutoApplyLimit";
    public const string AutoApplyPaused = "decision.autoApplyPaused";

    /// <summary>Set after a captcha / expired session: auto-apply resumes after this instant (ISO-8601 UTC).</summary>
    public const string ApplyBlockedUntil = "apply.blockedUntil";

    /// <summary>true = the browser fills the form but never submits (first runs, selector checks).</summary>
    public const string ApplyDryRun = "apply.dryRun";

    public const string CommentsPaused = "threads.commentsPaused";

    /// <summary>Routine replies to employers (interest, confirmed facts) go out on their own; false = every reply waits for the button.</summary>
    public const string ChatAutoReply = "chat.autoReply";

    /// <summary>Last alert time per key (throttles repeated alerts).</summary>
    public const string AlertPrefix = "alert.";

    private Dictionary<string, string>? _cache;
    private DateTimeOffset _cachedAt;

    public async Task<DecisionSettings> GetDecisionAsync(CancellationToken ct)
    {
        var policy = (await Automation.ReadAsync(ct)).Value;
        return new DecisionSettings(
            policy.AutoApplyThreshold, policy.ApprovalThreshold, policy.DailyAutoApplyLimit, policy.AutoApplyPaused);
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct) =>
        AutomationPolicyStore.LegacyKeys.Contains(key) ? bool.Parse(AutomationPolicyStore.Get((await Automation.ReadAsync(ct)).Value, key))
            : Bool(await AllAsync(ct), key, fallback);

    public async Task<DateTimeOffset?> GetInstantAsync(string key, CancellationToken ct)
    {
        if (key == ApplyBlockedUntil) return (await Modules.Hiring.ApplicationBlocks.ReadAsync(db, ct)).Until;
        var all = await AllAsync(ct);
        return all.TryGetValue(key, out var s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct) => AutomationPolicyStore.LegacyKeys.Contains(key)
        ? AutomationPolicyStore.Get((await Automation.ReadAsync(ct)).Value, key) : (await AllAsync(ct)).GetValueOrDefault(key);

    public Task SetAsync(string key, bool value, CancellationToken ct) => SetAsync(key, value ? "true" : "false", ct);

    public Task SetAsync(string key, int value, CancellationToken ct) => SetAsync(key, value.ToString(CultureInfo.InvariantCulture), ct);

    public Task SetAsync(string key, DateTimeOffset value, CancellationToken ct) => SetAsync(key, value.ToString("O", CultureInfo.InvariantCulture), ct);

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        if (ScrapeStateStore.OwnsKey(key)) throw new InvalidOperationException("scrape budgets and pauses have a dedicated owner");
        if (Modules.Hiring.ApplicationBlocks.OwnsKey(key)) throw new InvalidOperationException("application blocks have a dedicated owner");
        if (Modules.Discovery.ChannelMembershipRequests.OwnsKey(key)) throw new InvalidOperationException("Telegram membership pacing has a dedicated owner");
        if (Modules.Discovery.ChannelCollectionSources.OwnsKey(key)) throw new InvalidOperationException("Telegram collection progress has a dedicated owner");
        if (Modules.Preparation.HrPlaybookSources.OwnsKey(key)) throw new InvalidOperationException("HR preparation has a dedicated owner");
        if (Modules.Preparation.RejectionReviewSources.OwnsKey(key)) throw new InvalidOperationException("rejection analysis has a dedicated owner");
        if (Modules.Preparation.WeeklyReportSources.OwnsKey(key)) throw new InvalidOperationException("weekly report has a dedicated owner");
        if (key == Career.ConversationStore.Key) throw new InvalidOperationException("career dialogues have dedicated source owners");
        if (Modules.Hiring.EmailInboxSources.OwnsKey(key)) throw new InvalidOperationException("mailbox cursors have a dedicated owner");
        if (Modules.Preparation.LearningProgressSources.OwnsKey(key)) throw new InvalidOperationException("learning progress has a dedicated owner");
        if (key.StartsWith(Modules.Hiring.LinkedInSearchQuota.Prefix, StringComparison.Ordinal)) throw new InvalidOperationException("LinkedIn search budgets have a dedicated owner");
        if (key.StartsWith(Modules.Hiring.OutreachScans.Prefix, StringComparison.Ordinal)) throw new InvalidOperationException("outreach scan progress has a dedicated owner");
        if (key.StartsWith(Controls.BotControls.CursorPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("bot poll cursors have a dedicated owner");
        if (AutomationPolicyStore.LegacyKeys.Contains(key))
        {
            await Automation.SetAsync(key, value, ct);
            return;
        }
        await using var ctx = await db.CreateDbContextAsync(ct);
        var row = await ctx.Settings.FindAsync([key], ct);
        if (row is null)
        {
            ctx.Settings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = time.GetUtcNow();
        }

        await ctx.SaveChangesAsync(ct);
        _cache = null;
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        if (Modules.Discovery.ChannelMembershipRequests.OwnsKey(key)) throw new InvalidOperationException("Telegram membership pacing has a dedicated owner");
        if (Modules.Discovery.ChannelCollectionSources.OwnsKey(key)) throw new InvalidOperationException("Telegram collection progress has a dedicated owner");
        if (ScrapeStateStore.OwnsKey(key)) throw new InvalidOperationException("scrape budgets and pauses have a dedicated owner");
        if (Modules.Hiring.ApplicationBlocks.OwnsKey(key)) throw new InvalidOperationException("application blocks have a dedicated owner");
        if (Modules.Preparation.HrPlaybookSources.OwnsKey(key)) throw new InvalidOperationException("HR preparation has a dedicated owner");
        if (Modules.Preparation.RejectionReviewSources.OwnsKey(key)) throw new InvalidOperationException("rejection analysis has a dedicated owner");
        if (Modules.Preparation.WeeklyReportSources.OwnsKey(key)) throw new InvalidOperationException("weekly report has a dedicated owner");
        if (key == Career.ConversationStore.Key) throw new InvalidOperationException("career dialogues have dedicated source owners");
        if (Modules.Hiring.EmailInboxSources.OwnsKey(key)) throw new InvalidOperationException("mailbox cursors have a dedicated owner");
        if (Modules.Preparation.LearningProgressSources.OwnsKey(key)) throw new InvalidOperationException("learning progress has a dedicated owner");
        if (key.StartsWith(Modules.Hiring.LinkedInSearchQuota.Prefix, StringComparison.Ordinal)) throw new InvalidOperationException("LinkedIn search budgets have a dedicated owner");
        if (key.StartsWith(Modules.Hiring.OutreachScans.Prefix, StringComparison.Ordinal)) throw new InvalidOperationException("outreach scan progress has a dedicated owner");
        if (key.StartsWith(Controls.BotControls.CursorPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("bot poll cursors have a dedicated owner");
        if (AutomationPolicyStore.LegacyKeys.Contains(key))
        {
            await Automation.SetAsync(key, Automation.Default(key), ct);
            return;
        }
        await using var ctx = await db.CreateDbContextAsync(ct);
        await ctx.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(ct);
        _cache = null;
    }

    private async Task<Dictionary<string, string>> AllAsync(CancellationToken ct)
    {
        // A few seconds of staleness is fine; it saves a query on every vacancy.
        if (_cache is not null && time.GetUtcNow() - _cachedAt < TimeSpan.FromSeconds(10)) return _cache;
        await using var ctx = await db.CreateDbContextAsync(ct);
        _cache = await ctx.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        _cachedAt = time.GetUtcNow();
        return _cache;
    }

    private static int Int(Dictionary<string, string> all, string key, int fallback) =>
        all.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool Bool(Dictionary<string, string> all, string key, bool fallback) =>
        all.TryGetValue(key, out var s) && bool.TryParse(s, out var v) ? v : fallback;
}
