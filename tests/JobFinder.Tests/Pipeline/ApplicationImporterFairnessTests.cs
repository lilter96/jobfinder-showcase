using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Fact]
    public async Task Twenty_five_invalid_legacy_letters_cannot_starve_the_healthy_application_after_the_batch_limit()
    {
        var invalid = new List<long>();
        for (var index = 0; index < 25; index++) invalid.Add((await SeedAsync()).Id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Applications.Where(row => invalid.Contains(row.Id)).ExecuteUpdateAsync(update => update.SetProperty(row => row.CoverLetter, " \n\t"), Ct);
        var healthy = await SeedAsync();
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        var worker = ActivatorUtilities.CreateInstance<ApplyWorker>(producer.Services);
        Assert.Equal(25, await worker.ReconcileAsync(Ct));
        Assert.Equal(1, await worker.ReconcileAsync(Ct));
        Assert.Equal(0, await worker.ReconcileAsync(Ct));
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(25, await read.Applications.CountAsync(row => invalid.Contains(row.Id) && row.Status == ApplicationStatus.NeedsManual, Ct));
        Assert.Equal(25, await read.Notifications.CountAsync(row => row.Kind == NotificationKind.ApplyFailed, Ct));
        Assert.Equal(healthy.Id, (await read.ApplicationDeliveryRequests.SingleAsync(Ct)).ApplicationId);
        Assert.Equal(0, gate.Prepares);
        Assert.Empty(recipient.Deliveries);
        await StopAsync(producer);
        await HostAsync(true, new Gate(), recipient);
        await UntilAsync(async () => (await read.Applications.AsNoTracking().SingleAsync(row => row.Id == healthy.Id, Ct)).Status == ApplicationStatus.Sent);
        Assert.Equal(healthy.Id, Assert.Single(recipient.Deliveries).Application.Id);
    }

    [Theory]
    [InlineData(ApplicationChannel.TelegramDm)]
    [InlineData(ApplicationChannel.Email)]
    public async Task A_missing_legacy_recipient_is_retired_once_without_content_or_delivery_effects(ApplicationChannel channel)
    {
        var application = await SeedAsync(channel: channel);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Target, (string?)null), Ct);
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        var worker = ActivatorUtilities.CreateInstance<ApplyWorker>(producer.Services);
        Assert.Equal(1, await worker.ReconcileAsync(Ct));
        Assert.Equal(0, await worker.ReconcileAsync(Ct));
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStatus.NeedsManual, (await read.Applications.SingleAsync(Ct)).Status);
        Assert.Empty(await read.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Single(await read.Notifications.Where(row => row.Kind == NotificationKind.ApplyFailed).ToListAsync(Ct));
        Assert.Equal(0, gate.Prepares);
        Assert.Empty(recipient.Deliveries);
    }

    [Fact]
    public async Task A_candidate_prerequisite_pause_does_not_retire_a_well_formed_legacy_application()
    {
        var application = await SeedAsync();
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        var publisher = producer.Services.GetRequiredService<ICandidatePublisher>();
        var snapshot = await producer.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct);
        snapshot!.Value.Reviewed = false;
        await publisher.PublishProfileAsync(snapshot.Value, snapshot.Version, "synthetic-test", Ct);
        Assert.Equal(0, await ActivatorUtilities.CreateInstance<ApplyWorker>(producer.Services).ReconcileAsync(Ct));
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStatus.Pending, (await read.Applications.SingleAsync(Ct)).Status);
        Assert.Empty(await read.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await read.Notifications.Where(row => row.Kind == NotificationKind.ApplyFailed).ToListAsync(Ct));
        Assert.Equal(0, gate.Prepares);
        Assert.Empty(recipient.Deliveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_import_rejection_cannot_overwrite_a_concurrent_actual_edit_or_cancellation(bool edit)
    {
        var application = await SeedAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.CoverLetter, ""), Ct);
        var interception = new SnapshotInterception();
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient, interception);
        const string replacement = "Здравствуйте!\n\nОбновлённый проверяемый отклик.\n\nГотов обсудить задачи.";
        interception.OnRead = async token =>
        {
            var approvals = producer.Services.GetRequiredService<ApprovalService>();
            var result = edit ? await approvals.SetLetterAsync(application.VacancyId, replacement, await approvals.LetterSnapshotAsync(application.VacancyId, token), token)
                : await approvals.DismissAsync(application.VacancyId, token);
            Assert.True(result.Ok, result.Message);
        };
        Assert.Equal(edit ? 1 : 0, await ActivatorUtilities.CreateInstance<ApplyWorker>(producer.Services).ReconcileAsync(Ct));
        Assert.Equal(1, interception.Calls);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await read.Applications.SingleAsync(Ct);
        Assert.Equal(edit ? ApplicationStatus.Pending : ApplicationStatus.Cancelled, saved.Status);
        Assert.Equal(1, saved.Revision);
        Assert.Empty(await read.Notifications.Where(row => row.Kind == NotificationKind.ApplyFailed).ToListAsync(Ct));
        Assert.Equal(edit ? 1 : 0, await read.ApplicationDeliveryRequests.CountAsync(Ct));
        if (edit) Assert.Equal(replacement, saved.CoverLetter);
        Assert.Equal(0, gate.Prepares);
        Assert.Empty(recipient.Deliveries);
    }

    private sealed class SnapshotInterception : DbCommandInterceptor
    {
        private int _armed = 1;
        public int Calls;
        public Func<CancellationToken, Task>? OnRead { get; set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken ct = default)
        {
            if (OnRead is { } action && command.CommandText.Contains("ORDER BY", StringComparison.Ordinal)
                && command.CommandText.Contains(".created_at", StringComparison.Ordinal) && command.CommandText.Contains("COALESCE", StringComparison.Ordinal)
                && command.CommandText.Contains("application_delivery_requests", StringComparison.Ordinal) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Interlocked.Increment(ref Calls);
                await action(ct);
            }
            return result;
        }
    }
}
