using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    private async Task<Guid> LegacyAsync(Notification notice, bool consumer = false)
    {
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Notifications.Add(notice);
            await context.SaveChangesAsync(Ct);
        }
        var host = await HostAsync(consumer);
        if (!consumer) host.Services.GetRequiredService<ServerOwnership>().Acquired();
        await host.Services.GetRequiredService<LegacyNotificationImports>().ImportBatchAsync(10, Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        return (await read.Notifications.AsNoTracking().SingleAsync(row => row.Id == notice.Id, Ct)).DeliveryRequestId!.Value;
    }

    [Theory]
    [InlineData(NotificationStatus.Pending, 0)]
    [InlineData(NotificationStatus.Pending, 2)]
    [InlineData(NotificationStatus.Failed, 5)]
    [InlineData(NotificationStatus.Sending, 0)]
    [InlineData(NotificationStatus.OutcomeUnknown, 1)]
    public async Task Legacy_aggregate_admission_preserves_raw_multipart_history_without_any_transport(NotificationStatus status, int attempts)
    {
        var notice = await NoticeAsync(multipart: true);
        notice.Status = status;
        notice.Attempts = attempts;
        notice.TelegramMessageId = 73;
        notice.Error = "synthetic document outcome not recorded";
        var id = await LegacyAsync(notice, consumer: true);
        var root = await RootAsync(id);
        var input = NotificationDeliveryInputs.Input(root);
        Assert.Equal(NotificationDeliveryStage.OutcomeUnknown, root.Stage);
        Assert.True(input.LegacyUncertain);
        Assert.Null(input.Target);
        Assert.Null(root.AttemptId);
        Assert.Null(root.PlanJson);
        var raw = input.LegacySource!.Read(notice.Id);
        Assert.Equal(status.ToString(), raw["status"]!.GetValue<string>());
        Assert.Equal(attempts, raw["attempts"]!.GetValue<int>());
        Assert.Equal(notice.AttachmentPath, raw["attachment_path"]!.GetValue<string>());
        Assert.Equal(73, raw["telegram_message_id"]!.GetValue<int>());
        Assert.Empty(_api.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == id).ToListAsync(Ct));
        var saved = await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == notice.Id, Ct);
        Assert.Equal(NotificationStatus.OutcomeUnknown, saved.Status);
        Assert.Equal(attempts, saved.Attempts);
        Assert.Equal(notice.Error, saved.Error);
    }

    [Fact]
    public async Task Damaged_legacy_observation_preserves_unknown_source_and_immutable_admission_and_refuses_downgrade()
    {
        var notice = await NoticeAsync();
        var id = await LegacyAsync(notice);
        var root = await RootAsync(id);
        var input = NotificationDeliveryInputs.Input(root);
        input.LegacySource!.RawJson = "{";
        var damaged = JsonSerializer.Serialize(input, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.NotificationDeliveryRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.InputJson, damaged).SetProperty(row => row.InputHash, CanonicalJson.Hash(damaged)), Ct);
        await Assert.ThrowsAsync<UnsupportedNotificationDeliveryException>(() => _hosts.Last().Services.GetRequiredService<LegacyNotificationImports>()
            .ObserveAsync(new() { RequestId = id }, Ct));
        await context.NotificationDeliveryRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.InputJson, "{}"), Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003152702_DurableLinkedInPeople", Ct));
        Assert.Contains("Cannot roll back legacy notification admission", error.MessageText);
        var saved = await RootAsync(id);
        Assert.Equal(root.AdmissionHash, saved.AdmissionHash);
        Assert.Equal(NotificationDeliveryStage.OutcomeUnknown, saved.Stage);
        Assert.Equal(NotificationStatus.OutcomeUnknown, (await context.Notifications.SingleAsync(row => row.Id == notice.Id, Ct)).Status);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Missing_notice_marker_does_not_reimport_an_existing_native_owner_or_starve_other_legacy_work()
    {
        var host = await HostAsync(false);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var owned = await NoticeAsync();
        var owner = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(owned, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Notifications.Where(row => row.Id == owned.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DeliveryRequestId, (Guid?)null), Ct);
        var legacy = await NoticeAsync();
        context.Notifications.Add(legacy);
        await context.SaveChangesAsync(Ct);
        Assert.Equal(1, await host.Services.GetRequiredService<LegacyNotificationImports>().ImportBatchAsync(1, Ct));
        Assert.Equal(2, await context.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(NotificationDeliveryStage.Preparing, (await RootAsync(owner)).Stage);
        Assert.Null((await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == owned.Id, Ct)).DeliveryRequestId);
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData("incoming")]
    [InlineData("outgoing")]
    [InlineData("dead-letter")]
    public async Task Legacy_notification_downgrade_refuses_orphaned_future_protocol_envelopes(string store)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var id = Guid.NewGuid();
        var messageType = "jobfinder.notifications.quarantine-legacy.v99";
        if (store == "incoming")
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_incoming_envelopes (id,status,owner_id,body,message_type) VALUES ({id},'Scheduled',0,{Array.Empty<byte>()},{messageType})", Ct);
        else if (store == "outgoing")
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_outgoing_envelopes (id,owner_id,destination,body,message_type) VALUES ({id},0,'postgresql://notifications/',{Array.Empty<byte>()},{messageType})", Ct);
        else
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_dead_letters (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{messageType})", Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003152702_DurableLinkedInPeople", Ct));
        Assert.Contains("Cannot roll back legacy notification admission", error.MessageText);
    }

    [Fact]
    public async Task Legacy_import_is_bounded_once_and_concurrent_scans_do_not_compete_for_the_same_notice()
    {
        var host = await HostAsync(false);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        for (var index = 0; index < 5; index++) context.Notifications.Add(await NoticeAsync());
        await context.SaveChangesAsync(Ct);
        var imports = host.Services.GetRequiredService<LegacyNotificationImports>();
        var counts = await Task.WhenAll(imports.ImportBatchAsync(2, Ct), imports.ImportBatchAsync(2, Ct));
        Assert.Equal(4, counts.Sum());
        Assert.Equal(1, await imports.ImportBatchAsync(2, Ct));
        Assert.Equal(0, await imports.ImportBatchAsync(2, Ct));
        Assert.Equal(5, await context.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(5, await context.Notifications.CountAsync(row => row.Status == NotificationStatus.OutcomeUnknown, Ct));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Damaged_legacy_buttons_are_retained_as_raw_evidence_instead_of_starving_the_import_batch()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var damaged = await NoticeAsync();
        var valid = await NoticeAsync();
        context.Notifications.AddRange(damaged, valid);
        await context.SaveChangesAsync(Ct);
        var buttons = "{\"unsupported\":true}";
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET buttons = {buttons}::jsonb WHERE id = {damaged.Id}", Ct);
        var host = await HostAsync(false);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        Assert.Equal(2, await host.Services.GetRequiredService<LegacyNotificationImports>().ImportBatchAsync(10, Ct));
        var root = await context.NotificationDeliveryRequests.SingleAsync(row => row.NotificationId == damaged.Id, Ct);
        Assert.True(NotificationDeliveryInputs.Input(root).LegacySource!.Read(damaged.Id)["buttons"]!["unsupported"]!.GetValue<bool>());
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Legacy_admission_fault_rolls_back_source_marker_unknown_status_root_and_native_command()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = await NoticeAsync();
        context.Notifications.Add(notice);
        await context.SaveChangesAsync(Ct);
        var host = await HostAsync(false, new LegacyAdmissionFault());
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        await Assert.ThrowsAsync<IOException>(() => host.Services.GetRequiredService<LegacyNotificationImports>().ImportBatchAsync(10, Ct));
        var saved = await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == notice.Id, Ct);
        Assert.Equal(NotificationStatus.Pending, saved.Status);
        Assert.Null(saved.DeliveryRequestId);
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_notifications").SingleAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checked_legacy_resolution_is_persisted_without_certifying_any_message_or_PDF_or_resending(bool sent)
    {
        var notice = await NoticeAsync(multipart: true);
        var id = await LegacyAsync(notice);
        var ledger = new NotificationDeliveryLedger(pg.Db, TimeProvider.System);
        await ledger.ReconcileAsync(id, sent, "synthetic checked aggregate history", Ct);
        var root = await RootAsync(id);
        var input = NotificationDeliveryInputs.Input(root);
        var receipt = JsonSerializer.Deserialize(root.LegacyReconciliationJson!, NotificationDeliveryJsonContext.Default.LegacyNotificationReconciliation)!;
        Assert.Equal(sent, receipt.Sent);
        Assert.Equal(root.InputHash, receipt.InputHash);
        Assert.Equal(CanonicalJson.Hash(root.LegacyReconciliationJson!), root.LegacyReconciliationHash);
        Assert.Equal(NotificationDeliveryStage.Reconciled, root.Stage);
        Assert.True(input.LegacyUncertain);
        Assert.Null(root.AttemptId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReconcileAsync(id, !sent, "conflicting second resolution", Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(NotificationStatus.Reconciled, (await context.Notifications.SingleAsync(row => row.Id == notice.Id, Ct)).Status);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Changed_legacy_source_is_not_overwritten_or_certified_by_checked_old_history()
    {
        var notice = await NoticeAsync();
        var id = await LegacyAsync(notice);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Notifications.Where(row => row.Id == notice.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Text, "new source content"), Ct);
        await Assert.ThrowsAsync<UnsupportedNotificationDeliveryException>(() => new NotificationDeliveryLedger(pg.Db, TimeProvider.System)
            .ReconcileAsync(id, true, "old checked history", Ct));
        Assert.Equal(NotificationDeliveryStage.OutcomeUnknown, (await RootAsync(id)).Stage);
        Assert.Equal("new source content", (await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == notice.Id, Ct)).Text);
    }

    [Fact]
    public async Task Legacy_delivery_and_target_binding_commands_cannot_turn_unknown_history_into_a_send()
    {
        var notice = await NoticeAsync();
        var id = await LegacyAsync(notice);
        var host = _hosts.Last();
        var workflow = host.Services.GetRequiredService<NotificationDeliveryWorkflow>();
        await workflow.RunAsync(new DeliverNotification { RequestId = id }, Guid.NewGuid(), Ct);
        await workflow.BindAsync(new BindNotificationTarget { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(NotificationDeliveryStage.OutcomeUnknown, (await RootAsync(id)).Stage);
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_admission_and_checked_history_refuse_downgrade(bool reconcile)
    {
        var notice = await NoticeAsync();
        var id = await LegacyAsync(notice);
        if (reconcile) await new NotificationDeliveryLedger(pg.Db, TimeProvider.System).ReconcileAsync(id, true, "synthetic checked aggregate history", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003152702_DurableLinkedInPeople", Ct));
        Assert.Contains("Cannot roll back legacy notification admission", error.MessageText);
        Assert.Contains("20261003160007_LegacyNotificationReconciliation", await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    private sealed class LegacyAdmissionFault : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added && entry.Entity.Stage == NotificationDeliveryStage.OutcomeUnknown))
                throw new IOException("synthetic legacy root admission failure");
            return ValueTask.FromResult(result);
        }
    }
}
