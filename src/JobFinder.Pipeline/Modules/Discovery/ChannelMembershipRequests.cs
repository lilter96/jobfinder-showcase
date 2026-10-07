using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class ChannelMembershipRequests(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    TelegramOptions options, Notifier notifier, TimeProvider time)
{
    public const string PacingKey = "telegram.membership.notBefore";
    public const string FloodKey = "telegram.membership.floodUntil";
    public static bool OwnsKey(string key) => key == PacingKey || key == FloodKey;

    internal static Task LockAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('telegram-channel-decisions', 0))", ct);

    internal static async Task<bool> DecisionAllowedAsync(JobFinderDbContext context, TelegramChannel source, CancellationToken ct)
    {
        var roots = await context.ChannelMembershipRequests.Where(row => row.ChannelId == source.Id).ToListAsync(ct);
        if (source.MembershipRequestId is null) return roots.Count == 0;
        var root = roots.SingleOrDefault(row => row.Id == source.MembershipRequestId);
        if (root is null || roots.Any(row => row.DecisionRevision > root.DecisionRevision) || root.DecisionRevision > source.DecisionRevision) return false;
        var ids = roots.Select(row => row.Id).ToArray();
        var attempts = await context.ChannelMembershipAttempts.Where(row => ids.Contains(row.RequestId)).ToListAsync(ct);
        try
        {
            foreach (var retained in roots)
            {
                ChannelMembershipInputs.Input(retained);
                if (retained.Stage == ChannelMembershipStage.Completed)
                    ChannelMembershipInputs.Publication(retained, attempts.Where(row => row.RequestId == retained.Id).ToList());
                if (retained.Stage == ChannelMembershipStage.OutcomeUnknown || retained != root && !ChannelMembershipInputs.Terminal(retained.Stage)) return false;
                foreach (var attempt in attempts.Where(row => row.RequestId == retained.Id))
                {
                    ChannelMembershipInputs.Attempt(retained, attempt);
                    if (attempt.Outcome is ChannelMembershipOutcome.Sending or ChannelMembershipOutcome.OutcomeUnknown) return false;
                }
                if (retained.AttemptId is { } pointer && !attempts.Any(row => row.Id == pointer && row.RequestId == retained.Id)) return false;
            }
        }
        catch (UnsupportedChannelMembershipException) { return false; }
        if (ChannelMembershipInputs.Terminal(root.Stage)) return true;
        if (root.DecisionRevision != source.DecisionRevision || root.AttemptId is not null) return false;
        return !await context.ChannelMembershipAttempts.AnyAsync(row => row.RequestId == root.Id
            && (row.Outcome == ChannelMembershipOutcome.Sending || row.Outcome == ChannelMembershipOutcome.Confirmed || row.Outcome == ChannelMembershipOutcome.OutcomeUnknown), ct);
    }

    internal static async Task CancelPendingAsync(JobFinderDbContext context, TelegramChannel source, DateTimeOffset now, CancellationToken ct)
    {
        if (source.MembershipRequestId is not { } id) return;
        var root = await context.ChannelMembershipRequests.SingleAsync(row => row.Id == id, ct);
        if (ChannelMembershipInputs.Terminal(root.Stage)) return;
        root.Stage = ChannelMembershipStage.Cancelled;
        root.CompletedAt = now;
        root.ClaimEnvelopeId = null;
        root.RetryAt = null;
        root.Error = "captured user decision cancelled queued membership work";
    }

    internal async Task QueueJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, TelegramChannel source, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("membership admission joins the enrolled source transaction");
        var input = new ChannelMembershipInput { Source = ChannelDecisionSnapshot.Capture(source), AccessHash = source.AccessHash,
            Folder = options.FolderTitle, MinSpacingSeconds = options.JoinDelayMinSec, MaxSpacingSeconds = options.JoinDelayMaxSec,
            JoinsPerTenMinutes = options.MaxJoinsPerRun };
        var json = JsonSerializer.Serialize(input, ChannelMembershipJsonContext.Default.ChannelMembershipInput);
        var root = new ChannelMembershipRequest { Id = Guid.NewGuid(), ChannelId = source.Id, DecisionRevision = source.DecisionRevision,
            Stage = ChannelMembershipStage.Resolving, InputJson = json, InputHash = CanonicalJson.Hash(json), CreatedAt = time.GetUtcNow() };
        ChannelMembershipInputs.Input(root);
        context.ChannelMembershipRequests.Add(root);
        source.MembershipRequestId = root.Id;
        await outbox.PublishAsync(new ProcessChannelMembership { RequestId = root.Id, Stage = root.Stage });
    }

    public async Task<int> ImportApprovedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, ct);
        var damaged = await context.TelegramChannels.Where(row => row.Status == ChannelStatus.Approved
            && (row.MembershipRequestId != null && !context.ChannelMembershipRequests.Any(root => root.Id == row.MembershipRequestId && root.ChannelId == row.Id && root.DecisionRevision == row.DecisionRevision)
                || context.ChannelMembershipRequests.Any(root => root.ChannelId == row.Id && root.DecisionRevision > row.DecisionRevision)
                || row.MembershipRequestId == null && context.ChannelMembershipRequests.Any(root => root.ChannelId == row.Id)))
            .OrderBy(row => row.Id).Take(options.MaxJoinsPerRun).ToListAsync(ct);
        foreach (var source in damaged)
        {
            source.Status = ChannelStatus.Failed;
            source.Error = "damaged membership ownership marker; retained evidence requires investigation";
            notifier.Add(context, NotificationKind.Alert, "⚠️ Telegram-канал #" + source.Id + ": повреждён маркер операции; автоматическая подписка остановлена.");
        }
        var sources = await context.TelegramChannels.Where(row => row.Status == ChannelStatus.Approved && row.MembershipRequestId == null
            && !context.ChannelMembershipRequests.Any(root => root.ChannelId == row.Id)).OrderBy(row => row.Id).Take(options.MaxJoinsPerRun).ToListAsync(ct);
        foreach (var source in sources)
        {
            var last = await context.TelegramChannelDecisionRevisions.Where(row => row.ChannelId == source.Id).OrderByDescending(row => row.Revision).FirstOrDefaultAsync(ct);
            if (!ChannelDecisions.HasProof(source, last) || !Enum.IsDefined(source.Kind) || source.AddedAt == default || source.PeerId is <= 0
                || string.IsNullOrWhiteSpace(source.Username) && (source.PeerId is null || source.AccessHash is null))
            {
                source.Status = ChannelStatus.Failed;
                source.Error = "membership target is missing; source was not sent to Telegram";
                notifier.Add(context, NotificationKind.Alert, "⚠️ Telegram-канал #" + source.Id + ": не задан адрес подписки.");
                continue;
            }
            await QueueJoinedAsync(context, outbox, source, ct);
        }
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return sources.Count;
    }
}
