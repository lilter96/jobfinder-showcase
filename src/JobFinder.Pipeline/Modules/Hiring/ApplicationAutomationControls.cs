using JobFinder.Data;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class ApplicationAutomationControls(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    RuntimeSettings settings, ApplicationBlocks blocks, Notifier notifier)
{
    public async Task<PolicySnapshot<AutomationPolicy>> SetPauseAsync(PolicySnapshot<AutomationPolicy> expected, bool paused,
        ApplicationBlockSnapshot? release, string actor, CancellationToken ct)
    {
        var edited = expected.Value with { AutoApplyPaused = paused };
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        if (!paused && release is not null) await ApplicationBlocks.EnsureAsync(context, release, ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var published = await settings.Automation.PublishJoinedAsync(context, outbox, edited, expected.Version, actor, ct);
        if (!paused && release is not null) await blocks.ReleaseJoinedAsync(context, outbox, release, actor, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return published;
    }
}
