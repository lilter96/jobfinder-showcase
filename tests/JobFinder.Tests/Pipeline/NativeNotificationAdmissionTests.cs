using System.Text.Json;
using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    [Fact]
    public async Task Unstarted_native_runtime_cannot_leave_a_partial_business_save_or_notification_admission()
    {
        using (var producer = await PostProcessingTestHost.BuildAsync(pg, consumer: false))
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            context.Settings.Add(new AppSetting { Key = "synthetic.notification.prestart", Value = "must roll back", UpdatedAt = DateTimeOffset.UtcNow });
            context.Notifications.Add(await NoticeAsync());
            await Assert.ThrowsAsync<WolverineHasNotStartedException>(() => producer.Services.GetRequiredService<NotificationDeliveryRequests>().SaveAndFlushAsync(context, Ct));
        }
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await read.Settings.AnyAsync(row => row.Key == "synthetic.notification.prestart", Ct));
        Assert.Empty(await read.Notifications.ToListAsync(Ct));
        Assert.Empty(await read.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await read.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Producer_only_admission_commits_business_notice_and_command_without_starting_a_server_or_a_consumer()
    {
        Guid id;
        using (var producer = await PostProcessingTestHost.BuildAsync(pg, consumer: false))
        {
            await producer.StartAsync(Ct);
            try
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                context.Settings.Add(new AppSetting { Key = "synthetic.notification.prestart", Value = "committed", UpdatedAt = DateTimeOffset.UtcNow });
                var notice = await NoticeAsync();
                context.Notifications.Add(notice);
                await producer.Services.GetRequiredService<NotificationDeliveryRequests>().SaveAndFlushAsync(context, Ct);
                id = notice.DeliveryRequestId!.Value;
            }
            finally { await producer.StopAsync(CancellationToken.None); }
        }
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal("committed", (await context.Settings.SingleAsync(row => row.Key == "synthetic.notification.prestart", Ct)).Value);
            var notice = await context.Notifications.SingleAsync(Ct);
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
            Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        }
        Assert.Empty(_api.Calls);
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Single(_api.Calls);
    }

    [Fact]
    public async Task Target_independent_admission_survives_producer_exit_and_binds_once_without_rewriting_its_original_snapshot()
    {
        _options.Enabled = false;
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        var admitted = await RootAsync(id);
        Assert.Equal(NotificationDeliveryStage.AwaitingTarget, admitted.Stage);
        Assert.Null(admitted.PlanJson);
        Assert.Null(admitted.AttemptId);
        await producer.StopAsync(Ct);
        _options.Enabled = true;
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        var completed = await RootAsync(id);
        Assert.Equal(admitted.AdmissionJson, completed.AdmissionJson);
        Assert.Equal(admitted.AdmissionHash, completed.AdmissionHash);
        Assert.NotEqual(admitted.InputHash, completed.InputHash);
        Assert.Equal(123456, NotificationDeliveryInputs.Input(completed).Target!.ChatId);
        _options.OwnerUserId++;
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new BindNotificationTarget { RequestId = id });
        await Task.Delay(500, Ct);
        Assert.Equal(completed.InputHash, (await RootAsync(id)).InputHash);
        Assert.Single(_api.Calls);
    }

    [Fact]
    public async Task Unavailable_binding_checkpoint_schedules_continuation_without_claiming_an_effect_or_starving_bound_work()
    {
        var producer = await HostAsync(false);
        var ready = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        _options.Enabled = false;
        var waiting = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(waiting)).RetryAt is not null);
        var checkpoint = await RootAsync(waiting);
        Assert.Equal(NotificationDeliveryStage.AwaitingTarget, checkpoint.Stage);
        Assert.Null(checkpoint.ClaimEnvelopeId);
        Assert.Null(checkpoint.PlanJson);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        _options.Enabled = true;
        await context.NotificationDeliveryRequests.Where(row => row.Id == ready).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        var prepared = await RootAsync(ready);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = ready, Stage = prepared.Stage });
        await UntilAsync(async () => (await RootAsync(ready)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(NotificationDeliveryStage.AwaitingTarget, (await RootAsync(waiting)).Stage);
        Assert.Single(_api.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("source")]
    [InlineData("admission")]
    [InlineData("admission-content")]
    public async Task Unbound_source_or_admission_drift_never_binds_or_sends(string drift)
    {
        _options.Enabled = false;
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (drift == "missing") await context.Notifications.ExecuteDeleteAsync(Ct);
        else if (drift == "source") await context.Notifications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Text, "changed source"), Ct);
        else if (drift == "admission") await context.NotificationDeliveryRequests.ExecuteUpdateAsync(update => update.SetProperty(row => row.AdmissionHash, new string('0', 64)), Ct);
        else
        {
            var root = await context.NotificationDeliveryRequests.SingleAsync(Ct);
            var input = JsonSerializer.Deserialize(root.AdmissionJson!, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput)!;
            input.Text = "changed original admission";
            root.AdmissionJson = JsonSerializer.Serialize(input, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput);
            root.AdmissionHash = CanonicalJson.Hash(root.AdmissionJson);
            await context.SaveChangesAsync(Ct);
        }
        _options.Enabled = true;
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage is NotificationDeliveryStage.Cancelled or NotificationDeliveryStage.Failed);
        Assert.Empty(_api.Calls);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Null((await RootAsync(id)).PlanJson);
    }

    [Fact]
    public async Task A_saved_v1_input_without_admission_fields_still_executes_the_original_delivery_protocol()
    {
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = await context.NotificationDeliveryRequests.SingleAsync(Ct);
        var notice = await context.Notifications.SingleAsync(Ct);
        root.InputJson = JsonSerializer.Serialize(NotificationDeliveryInput.Capture(notice, NotificationDeliveryInputs.Input(root).Target, version: 1), NotificationDeliveryJsonContext.Default.NotificationDeliveryInput);
        root.InputHash = CanonicalJson.Hash(root.InputJson);
        root.AdmissionHash = null;
        root.AdmissionJson = null;
        await context.SaveChangesAsync(Ct);
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Single(_api.Calls);
    }

    [Fact]
    public async Task Unsupported_binding_envelope_quarantines_unbound_work_without_binding_or_sending()
    {
        _options.Enabled = false;
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes", Ct);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new BindNotificationTarget { RequestId = id, Version = 2 });
        _options.Enabled = true;
        await HostAsync(true);
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters WHERE message_type = 'jobfinder.notifications.target.v1' OR message_type LIKE '%BindNotificationTarget%'").SingleAsync(Ct) == 1);
        Assert.Equal(NotificationDeliveryStage.Failed, (await RootAsync(id)).Stage);
        Assert.Equal(NotificationStatus.Failed, (await StoredAsync(id)).Status);
        Assert.Null(NotificationDeliveryInputs.Input(await RootAsync(id)).Target);
        Assert.Empty(_api.Calls);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Lost_binding_commit_observation_retains_the_bound_target_and_next_command_across_consumer_restart_without_repeating_delivery()
    {
        _options.Enabled = false;
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        var admission = await RootAsync(id);
        await producer.StopAsync(Ct);
        _options.Enabled = true;
        var fault = new BindingCommitFault();
        var consumer = await HostAsync(true, fault);
        await UntilAsync(() => Task.FromResult(fault.Fired));
        var checkpoint = await RootAsync(id);
        Assert.Equal(admission.AdmissionHash, checkpoint.AdmissionHash);
        Assert.NotEqual(admission.InputHash, checkpoint.InputHash);
        Assert.Equal(123456, NotificationDeliveryInputs.Input(checkpoint).Target!.ChatId);
        if (checkpoint.Stage != NotificationDeliveryStage.Completed)
        {
            await using var saved = await pg.Db.CreateDbContextAsync(Ct);
            var body = System.Text.Encoding.UTF8.GetBytes(id.ToString());
            Assert.True(await saved.Database.SqlQuery<int>($"""
                SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_notifications WHERE message_type = 'jobfinder.notifications.delivery.v1' AND position({body} in body) > 0)
                  + (SELECT count(*) FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type = 'jobfinder.notifications.delivery.v1' AND position({body} in body) > 0)
                  + (SELECT count(*) FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type = 'jobfinder.notifications.delivery.v1' AND position({body} in body) > 0))::int AS "Value"
                """).SingleAsync(Ct) > 0);
        }
        await consumer.StopAsync(Ct);
        await HostAsync(true, fastRecovery: true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.True(fault.Fired);
        Assert.Equal(admission.AdmissionHash, (await RootAsync(id)).AdmissionHash);
        Assert.Single(_api.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Joined_intermediate_saves_capture_each_added_batch_without_committing_the_business_transaction(bool commit)
    {
        _options.Enabled = false;
        var producer = await HostAsync(false);
        var requests = producer.Services.GetRequiredService<NotificationDeliveryRequests>();
        await using (var scope = producer.Services.CreateAsyncScope())
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            context.Settings.Add(new AppSetting { Key = "synthetic.notification.joined", Value = "business", UpdatedAt = DateTimeOffset.UtcNow });
            context.Notifications.Add(await NoticeAsync());
            await requests.SaveJoinedAsync(context, outbox, Ct);
            Assert.True(context.Notifications.Local.Single().Id > 0);
            Assert.NotNull(context.Notifications.Local.Single().DeliveryRequestId);
            context.Notifications.Add(await NoticeAsync());
            await requests.SaveJoinedAsync(context, outbox, Ct);
            await using var outside = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await outside.NotificationDeliveryRequests.ToListAsync(Ct));
            if (commit) await requests.SaveAndFlushAsync(context, outbox, Ct);
        }
        await using var verify = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(commit ? 2 : 0, await verify.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(commit, await verify.Settings.AnyAsync(row => row.Key == "synthetic.notification.joined", Ct));
        await HostAsync(true);
        if (commit) await UntilAsync(async () => await verify.NotificationDeliveryRequests.AllAsync(row => row.RetryAt != null, Ct));
        else await Task.Delay(500, Ct);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Joined_save_refuses_a_context_that_is_not_enrolled_even_when_no_notice_is_added()
    {
        var producer = await HostAsync(false);
        await using var scope = producer.Services.CreateAsyncScope();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Services.GetRequiredService<NotificationDeliveryRequests>().SaveJoinedAsync(context, outbox, Ct));
    }

    [Fact]
    public async Task Concurrent_alerts_commit_one_cooldown_notice_and_native_request_even_without_a_configured_target()
    {
        _options.Enabled = false;
        var producer = await HostAsync(false);
        var notifier = producer.Services.GetRequiredService<Notifier>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => notifier.AlertAsync("synthetic.notification.concurrent", "synthetic", "diagnostic", TimeSpan.FromHours(1), Ct)));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        var root = Assert.Single(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(root.Id, notice.DeliveryRequestId);
        Assert.Equal(NotificationDeliveryStage.AwaitingTarget, root.Stage);
        Assert.True(await context.Settings.AnyAsync(row => row.Key == "alert.synthetic.notification.concurrent", Ct));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Failed_alert_admission_rolls_back_its_cooldown_and_retry_preserves_the_alert()
    {
        _options.Enabled = false;
        var fault = new AdmissionSaveFault();
        var producer = await HostAsync(false, fault);
        var notifier = producer.Services.GetRequiredService<Notifier>();
        await Assert.ThrowsAsync<IOException>(() => notifier.AlertAsync("synthetic.notification.rollback", "synthetic", "diagnostic", TimeSpan.FromHours(1), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await context.Settings.AnyAsync(row => row.Key == "alert.synthetic.notification.rollback", Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        await notifier.AlertAsync("synthetic.notification.rollback", "synthetic", "diagnostic", TimeSpan.FromHours(1), Ct);
        Assert.Single(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.True(await context.Settings.AnyAsync(row => row.Key == "alert.synthetic.notification.rollback", Ct));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("foreign-entity")]
    [InlineData("message")]
    [InlineData("receipt")]
    [InlineData("duplicate-receipt")]
    public async Task Keyboard_edit_without_exact_original_source_and_confirmed_primary_receipt_fails_owned_without_guessing_a_chat(string damage)
    {
        var consumer = await HostAsync(true);
        var requests = consumer.Services.GetRequiredService<NotificationDeliveryRequests>();
        var parent = await NoticeAsync();
        parent.EntityId = 17;
        var original = await requests.PublishAsync(parent, Ct);
        await UntilAsync(async () => (await RootAsync(original)).Stage == NotificationDeliveryStage.Completed);
        if (damage == "receipt")
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.NotificationDeliveryAttempts.ExecuteUpdateAsync(update => update.SetProperty(row => row.EffectHash, new string('0', 64)), Ct);
        }
        else if (damage == "duplicate-receipt")
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var receipt = await context.NotificationDeliveryAttempts.AsNoTracking().SingleAsync(Ct);
            receipt.Id = Guid.NewGuid();
            receipt.Number++;
            context.NotificationDeliveryAttempts.Add(receipt);
            await context.SaveChangesAsync(Ct);
        }
        var edit = await NoticeAsync();
        edit.EditKeyboardMessageId = damage == "message" ? 74 : 73;
        edit.EditParentNotificationId = damage == "none" ? null : parent.Id;
        edit.EntityId = damage == "foreign-entity" ? 18 : 17;
        _options.Enabled = false;
        var id = await requests.PublishAsync(edit, Ct);
        Assert.Equal(NotificationDeliveryStage.Failed, (await RootAsync(id)).Stage);
        Assert.Equal(NotificationStatus.Failed, (await StoredAsync(id)).Status);
        _options.Enabled = true;
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new BindNotificationTarget { RequestId = id });
        await Task.Delay(500, Ct);
        Assert.Single(_api.Calls);
    }

    [Fact]
    public async Task Keyboard_edit_captures_the_original_chat_instead_of_the_current_configured_owner()
    {
        var consumer = await HostAsync(true);
        var requests = consumer.Services.GetRequiredService<NotificationDeliveryRequests>();
        var parent = await NoticeAsync();
        var original = await requests.PublishAsync(parent, Ct);
        await UntilAsync(async () => (await RootAsync(original)).Stage == NotificationDeliveryStage.Completed);
        _options.OwnerUserId++;
        var edit = await NoticeAsync();
        edit.EditParentNotificationId = parent.Id;
        edit.EditKeyboardMessageId = 73;
        var id = await requests.PublishAsync(edit, Ct);
        Assert.Equal(123456, NotificationDeliveryInputs.Input(await RootAsync(id)).Target!.ChatId);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Failed);
        Assert.Single(_api.Calls);
    }

    private sealed class AdmissionSaveFault : SaveChangesInterceptor
    {
        private bool _fired;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_fired && eventData.Context!.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                _fired = true;
                throw new IOException("synthetic notification admission save failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BindingCommitFault : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.Entity.Stage == NotificationDeliveryStage.Preparing
                && entry.Entity.AdmissionHash is not null && entry.Entity.InputHash != entry.Entity.AdmissionHash))
            {
                Fired = true;
                throw new IOException("synthetic lost target-binding commit observation");
            }
            return Task.CompletedTask;
        }
    }
}
