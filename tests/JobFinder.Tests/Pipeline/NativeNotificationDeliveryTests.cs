using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];
    private readonly BotOptions _options = new() { OwnerUserId = 123456, Token = "123456:synthetic-test-token" };
    private readonly NotificationApi _api = new();
    private readonly string _directory = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-notification-acceptance", Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Notifications.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("bot.edit.")).ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == "notifications.delivery.pace" || row.Key.StartsWith("synthetic.notification.") || row.Key.StartsWith("alert.synthetic.notification.")).ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        Directory.CreateDirectory(_directory);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Dispose(); }
        }
        _api.Dispose();
    }

    private async Task<IHost> HostAsync(bool consumer, IInterceptor? fault = null, bool fastRecovery = false)
    {
        var factory = new BotHttp(_api);
        var holder = new TelegramBotClientHolder(_options, factory);
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: services =>
            services.AddSingleton<INotificationDeliveryTransport>(new NotificationDeliveryTransport(_options, holder)), fastRecovery: fastRecovery);
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<Notification> NoticeAsync(bool multipart = false, bool attachment = false)
    {
        string? path = null;
        if (attachment || multipart)
        {
            path = Path.Combine(_directory, "synthetic-pack.pdf");
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("synthetic diagnostic attachment, not a candidate PDF"), Ct);
        }
        return new()
        {
            Kind = NotificationKind.Info, Text = multipart ? "<b>" + new string('я', 1100) + "</b>" : "<b>Синтетическое уведомление</b>",
            Status = NotificationStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, AttachmentPath = path,
            AttachmentName = path is null ? null : "synthetic-pack.pdf", Buttons = [["Проверка|noop:0:0"]],
        };
    }

    private async Task<NotificationDeliveryRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.NotificationDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<Notification> StoredAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Notifications.AsNoTracking().SingleAsync(row => row.DeliveryRequestId == id, Ct);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("native notification checkpoint did not arrive");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Multipart_delivery_checkpoints_both_message_ids_and_exact_attachment_without_replaying_the_primary_card()
    {
        var producer = await HostAsync(false);
        var notice = await NoticeAsync(multipart: true);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        var calls = _api.Calls.ToArray();
        Assert.Equal(new[] { "sendMessage", "sendDocument" }, calls.Select(call => call.Method));
        Assert.Equal(Encoding.UTF8.GetBytes("synthetic diagnostic attachment, not a candidate PDF"), calls[1].Attachment);
        var stored = await StoredAsync(id);
        Assert.Equal(NotificationStatus.Sent, stored.Status);
        Assert.Equal(73, stored.TelegramMessageId);
        Assert.NotNull(stored.SentAt);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var attempts = await context.NotificationDeliveryAttempts.OrderBy(row => row.EffectIndex).ToListAsync(Ct);
        Assert.Equal(new[] { 73, 74 }, attempts.Select(row => row.TelegramMessageId!.Value));
        Assert.All(attempts, attempt => Assert.Equal(DeliveryAttemptStatus.Confirmed, attempt.Status));
        foreach (var stage in new[] { NotificationDeliveryStage.Preparing, NotificationDeliveryStage.Ready })
            await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = id, Stage = stage });
        await Task.Delay(1500, Ct);
        Assert.Equal(2, _api.Calls.Count);
        Assert.Single(await context.NotificationDeliveryRequests.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Document_rate_limit_retries_only_that_effect_and_preserves_the_confirmed_text_receipt(int? retryAfter)
    {
        _api.DocumentRateLimit = true;
        _api.DocumentRetryAfter = retryAfter;
        var host = await HostAsync(true);
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(multipart: true), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(new[] { "sendMessage", "sendDocument", "sendDocument" }, _api.Calls.Select(call => call.Method));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var attempts = await context.NotificationDeliveryAttempts.OrderBy(row => row.EffectIndex).ThenBy(row => row.Number).ToListAsync(Ct);
        Assert.Equal(new[] { DeliveryAttemptStatus.Confirmed, DeliveryAttemptStatus.KnownNotSent, DeliveryAttemptStatus.Confirmed }, attempts.Select(row => row.Status));
        Assert.Equal(73, (await StoredAsync(id)).TelegramMessageId);
        Assert.Equal(1, (await StoredAsync(id)).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_document_delivery_never_repeats_text_or_document_and_reconciliation_does_not_certify_delivery(bool sent)
    {
        _api.DocumentUnknown = true;
        var host = await HostAsync(true);
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(multipart: true), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        var notice = await StoredAsync(id);
        Assert.Equal(NotificationStatus.OutcomeUnknown, notice.Status);
        Assert.Equal(73, notice.TelegramMessageId);
        Assert.Null(notice.SentAt);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = id, Stage = NotificationDeliveryStage.Ready, EffectIndex = 1 });
        await Task.Delay(1500, Ct);
        Assert.Equal(2, _api.Calls.Count);
        await host.Services.GetRequiredService<NotificationDeliveryLedger>().ReconcileAsync(id, sent, "synthetic owner check", Ct);
        Assert.Equal(NotificationDeliveryStage.Reconciled, (await RootAsync(id)).Stage);
        Assert.Equal(NotificationStatus.Reconciled, (await StoredAsync(id)).Status);
        Assert.Null((await StoredAsync(id)).SentAt);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var attempt = await context.NotificationDeliveryAttempts.SingleAsync(row => row.EffectIndex == 1, Ct);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, attempt.Status);
        Assert.Equal(sent, attempt.ReconciledSent);
        Assert.Null(attempt.TelegramMessageId);
        Assert.Equal(2, _api.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HTML_fallback_changes_only_the_explicitly_rejected_effect(bool multipart)
    {
        _api.ParseRejection = true;
        var host = await HostAsync(true);
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(multipart: multipart, attachment: true), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        var calls = _api.Calls.ToArray();
        Assert.True(calls[0].Html);
        Assert.False(calls[1].Html);
        Assert.Equal(multipart ? new[] { "sendMessage", "sendMessage", "sendDocument" } : new[] { "sendDocument", "sendDocument" }, calls.Select(call => call.Method));
        Assert.Equal(1, (await StoredAsync(id)).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Keyboard_edit_has_its_own_receipt_and_cannot_fall_back_to_a_new_message(bool notModified)
    {
        _api.KeyboardError = notModified ? "Bad Request: message is not modified" : "Bad Request: cannot parse keyboard";
        var host = await HostAsync(true);
        var parent = await NoticeAsync();
        var parentId = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(parent, Ct);
        await UntilAsync(async () => (await RootAsync(parentId)).Stage == NotificationDeliveryStage.Completed);
        var notice = await NoticeAsync();
        notice.EditKeyboardMessageId = 73;
        notice.EditParentNotificationId = parent.Id;
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == (notModified ? NotificationDeliveryStage.Completed : NotificationDeliveryStage.Failed));
        Assert.Equal("editMessageReplyMarkup", Assert.Single(_api.Calls.Skip(1)).Method);
        Assert.Equal(notModified ? 73 : null, (await StoredAsync(id)).TelegramMessageId);
        Assert.Equal(notModified ? NotificationStatus.Sent : NotificationStatus.Failed, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Missing_attachment_pauses_before_text_and_replaced_attachment_after_text_never_repeats_that_text()
    {
        var producer = await HostAsync(false);
        var notice = await NoticeAsync(multipart: true);
        File.Delete(notice.AttachmentPath!);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).RetryAt != null);
        Assert.Equal(NotificationDeliveryStage.Preparing, (await RootAsync(id)).Stage);
        Assert.Empty(_api.Calls);
        await File.WriteAllTextAsync(notice.AttachmentPath!, "original diagnostic bytes", Ct);
        _api.DuringSend = async method =>
        {
            if (method == "sendMessage") await File.WriteAllTextAsync(notice.AttachmentPath!, "replaced diagnostic bytes", Ct);
        };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.NotificationDeliveryRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = id });
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Failed);
        Assert.Equal("sendMessage", Assert.Single(_api.Calls).Method);
        Assert.Equal(73, (await StoredAsync(id)).TelegramMessageId);
        Assert.Null((await StoredAsync(id)).SentAt);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, (await context.NotificationDeliveryAttempts.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Source_changes_and_changed_owner_refuse_effects_without_silently_rebasing_the_captured_text()
    {
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Notifications.Where(row => row.DeliveryRequestId == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Text, "new owner text"), Ct);
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Cancelled);
        Assert.Equal("new owner text", (await StoredAsync(id)).Text);
        Assert.Empty(_api.Calls);
        var second = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        _options.OwnerUserId++;
        await UntilAsync(async () => (await RootAsync(second)).Stage == NotificationDeliveryStage.Failed);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Joined_admission_rollback_preserves_neither_the_notice_root_nor_the_native_command()
    {
        var producer = await HostAsync(false);
        await using var scope = producer.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            outbox.Enroll(context);
            context.Settings.Add(new AppSetting { Key = "synthetic.notification.business", Value = "commits with notice", UpdatedAt = DateTimeOffset.UtcNow });
            var notice = await NoticeAsync();
            context.Notifications.Add(notice);
            await producer.Services.GetRequiredService<NotificationDeliveryRequests>().QueueAsync(context, outbox, notice, Ct);
            await context.SaveChangesAsync(Ct);
        }
        await using var verify = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await verify.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await verify.Notifications.Where(row => row.DeliveryRequestId != null).ToListAsync(Ct));
        Assert.False(await verify.Settings.AnyAsync(row => row.Key == "synthetic.notification.business", Ct));
        var envelopes = await verify.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%notifications.delivery%' OR message_type LIKE '%DeliverNotification%'").SingleAsync(Ct);
        Assert.Equal(0, envelopes);
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_receipt_commit_becomes_unknown_instead_of_repeating_an_accepted_effect(bool io)
    {
        var fault = new ReceiptFault(io);
        var host = await HostAsync(true, fault);
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        Assert.True(fault.Fired);
        Assert.Single(_api.Calls);
        Assert.Equal(NotificationStatus.OutcomeUnknown, (await StoredAsync(id)).Status);
        Assert.Null((await StoredAsync(id)).TelegramMessageId);
        Assert.Null((await StoredAsync(id)).SentAt);
        var next = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(next)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(2, _api.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_admission_joins_every_new_notice_and_command_to_the_business_transaction(bool commit)
    {
        var producer = await HostAsync(false);
        await using var scope = producer.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        var notices = new[] { await NoticeAsync(), await NoticeAsync() };
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            outbox.Enroll(context);
            context.Settings.Add(new AppSetting { Key = "synthetic.notification.batch", Value = "paired", UpdatedAt = DateTimeOffset.UtcNow });
            context.Notifications.AddRange(notices);
            var requests = producer.Services.GetRequiredService<NotificationDeliveryRequests>();
            var ids = await requests.QueueAddedAsync(context, outbox, Ct);
            Assert.Equal(2, ids.Count);
            Assert.Equal(2, ids.Distinct().Count());
            Assert.All(notices, notice => Assert.Contains(notice.DeliveryRequestId!.Value, ids));
            Assert.Empty(await requests.QueueAddedAsync(context, outbox, Ct));
            if (commit) await outbox.SaveChangesAndFlushMessagesAsync(Ct);
            else await context.SaveChangesAsync(Ct);
        }
        await using var verify = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(commit ? 2 : 0, await verify.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(commit ? 2 : 0, await verify.Notifications.CountAsync(Ct));
        Assert.Equal(commit, await verify.Settings.AnyAsync(row => row.Key == "synthetic.notification.batch", Ct));
        Assert.Empty(_api.Calls);
        if (!commit)
        {
            Assert.Equal(0, await verify.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%notifications.delivery%' OR message_type LIKE '%DeliverNotification%'").SingleAsync(Ct));
            return;
        }
        await producer.StopAsync(Ct);
        await HostAsync(true);
        await UntilAsync(async () =>
        {
            await using var check = await pg.Db.CreateDbContextAsync(Ct);
            return await check.NotificationDeliveryRequests.CountAsync(row => row.Stage == NotificationDeliveryStage.Completed, Ct) == 2;
        });
        Assert.Equal(2, _api.Calls.Count);
    }

    [Fact]
    public async Task Lost_sending_commit_observation_recovers_without_transport_and_does_not_block_the_next_notice()
    {
        var fault = new SendingCommitFault();
        var host = await HostAsync(true, fault);
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        Assert.True(fault.Fired);
        Assert.Empty(_api.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.NotificationDeliveryAttempts.SingleAsync(Ct)).Status);
        var next = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(next)).Stage == NotificationDeliveryStage.Completed);
        Assert.Single(_api.Calls);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("input")]
    [InlineData("attempt")]
    [InlineData("owner")]
    public async Task Changed_delivery_witness_after_an_effect_becomes_unknown_without_overwriting_a_new_source_owner(string witness)
    {
        var newOwner = Guid.NewGuid();
        var host = await HostAsync(true);
        _api.DuringSend = async _ =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var root = await context.NotificationDeliveryRequests.SingleAsync(Ct);
            if (witness == "plan") root.PlanHash = new string('0', 64);
            else if (witness == "input") root.InputJson = "{}";
            else if (witness == "attempt")
                (await context.NotificationDeliveryAttempts.SingleAsync(Ct)).EffectHash = new string('0', 64);
            else
            {
                var source = await context.Notifications.SingleAsync(Ct);
                source.DeliveryRequestId = newOwner;
                source.Status = NotificationStatus.Pending;
            }
            await context.SaveChangesAsync(Ct);
        };
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        await using var verify = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(_api.Calls);
        var attempt = await verify.NotificationDeliveryAttempts.SingleAsync(Ct);
        Assert.Equal(witness == "owner" ? DeliveryAttemptStatus.OutcomeUnknown : DeliveryAttemptStatus.Submitting, attempt.Status);
        Assert.Null(attempt.TelegramMessageId);
        var notice = await verify.Notifications.SingleAsync(Ct);
        Assert.Equal(witness == "owner" ? NotificationStatus.Pending : NotificationStatus.OutcomeUnknown, notice.Status);
        Assert.Equal(witness == "owner" ? newOwner : id, notice.DeliveryRequestId);
        Assert.Null(notice.SentAt);
        if (witness == "owner")
        {
            await host.Services.GetRequiredService<NotificationDeliveryLedger>().ReconcileAsync(id, true, "synthetic checked original outcome", Ct);
            Assert.Equal(NotificationDeliveryStage.Reconciled, (await RootAsync(id)).Stage);
            Assert.Equal(NotificationStatus.Pending, (await verify.Notifications.AsNoTracking().SingleAsync(Ct)).Status);
            Assert.Single(_api.Calls);
        }
        else await Assert.ThrowsAsync<UnsupportedNotificationDeliveryException>(() => host.Services.GetRequiredService<NotificationDeliveryLedger>()
            .ReconcileAsync(id, true, "synthetic checked outcome with damaged immutable witnesses", Ct));
    }

    [Theory]
    [InlineData("missing-attempt")]
    [InlineData("foreign-attempt")]
    [InlineData("missing-notice")]
    [InlineData("claim")]
    [InlineData("ordinal")]
    public async Task Damaged_sending_references_quarantine_without_resending_or_changing_foreign_receipts(string damage)
    {
        var host = await HostAsync(true);
        Guid? foreignId = null;
        _api.DuringSend = async _ =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var root = await context.NotificationDeliveryRequests.SingleAsync(Ct);
            if (damage == "missing-attempt") await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(Ct);
            else if (damage == "missing-notice") await context.Notifications.ExecuteDeleteAsync(Ct);
            else if (damage == "claim") root.ClaimEnvelopeId = Guid.NewGuid();
            else if (damage == "ordinal") root.EffectIndex++;
            else
            {
                var foreign = new NotificationDeliveryAttempt
                {
                    Id = Guid.NewGuid(), RequestId = Guid.NewGuid(), Status = DeliveryAttemptStatus.Confirmed,
                    Number = 1, InputHash = new string('0', 64), PlanHash = new string('0', 64), EffectHash = new string('0', 64),
                    TelegramMessageId = 444, CreatedAt = DateTimeOffset.UtcNow,
                };
                context.NotificationDeliveryAttempts.Add(foreign);
                root.AttemptId = foreignId = foreign.Id;
            }
            await context.SaveChangesAsync(Ct);
        };
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        Assert.Single(_api.Calls);
        if (foreignId is { } foreign)
        {
            await using var verify = await pg.Db.CreateDbContextAsync(Ct);
            var attempt = await verify.NotificationDeliveryAttempts.SingleAsync(row => row.Id == foreign, Ct);
            Assert.Equal(DeliveryAttemptStatus.Confirmed, attempt.Status);
            Assert.Equal(444, attempt.TelegramMessageId);
        }
        _api.DuringSend = null;
        var next = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(next)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(2, _api.Calls.Count);
    }

    [Fact]
    public async Task Changing_options_token_does_not_change_the_identity_of_the_already_constructed_sdk_client()
    {
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        _options.Token = "999999:synthetic-replacement-token";
        await producer.StopAsync(Ct);
        _options.Token = "123456:synthetic-test-token";
        var consumer = await HostAsync(true);
        _options.Token = "999999:synthetic-replacement-token";
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Single(_api.Calls);
        Assert.Equal(NotificationStatus.Sent, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task A_disabled_transport_pauses_without_claiming_or_sending_and_resumes_the_same_plan()
    {
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await producer.StopAsync(Ct);
        _options.Enabled = false;
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)) is { Stage: NotificationDeliveryStage.Ready, RetryAt: not null });
        var before = await RootAsync(id);
        Assert.Empty(_api.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        _options.Enabled = true;
        await context.NotificationDeliveryRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = id, Stage = NotificationDeliveryStage.Ready });
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(before.PlanHash, (await RootAsync(id)).PlanHash);
        Assert.Single(_api.Calls);
    }

    [Fact]
    public async Task Disabled_at_admission_commits_an_owned_target_independent_notice_without_sending()
    {
        var producer = await HostAsync(false);
        _options.Enabled = false;
        var notice = await NoticeAsync();
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(id, (await context.Notifications.SingleAsync(Ct)).DeliveryRequestId);
        var root = await RootAsync(id);
        Assert.Equal(NotificationDeliveryStage.AwaitingTarget, root.Stage);
        Assert.Equal(root.InputHash, root.AdmissionHash);
        Assert.Null(JsonSerializer.Deserialize(root.InputJson, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput)!.Target);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task A_response_for_another_chat_is_not_a_confirmed_receipt()
    {
        var host = await HostAsync(true);
        _api.ResponseChatId++;
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        Assert.Single(_api.Calls);
        Assert.Null((await StoredAsync(id)).TelegramMessageId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Checked_historical_outcomes_can_be_reconciled_after_source_drift_without_certifying_the_changed_source(bool missing, bool sent)
    {
        var host = await HostAsync(true);
        _api.DuringSend = async _ =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            if (missing) await context.Notifications.ExecuteDeleteAsync(Ct);
            else await context.Notifications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Text, "replacement source text"), Ct);
        };
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        await host.Services.GetRequiredService<NotificationDeliveryLedger>().ReconcileAsync(id, sent, "synthetic owner-checked historical effect", Ct);
        Assert.Equal(NotificationDeliveryStage.Reconciled, (await RootAsync(id)).Stage);
        await using var verify = await pg.Db.CreateDbContextAsync(Ct);
        var attempt = await verify.NotificationDeliveryAttempts.SingleAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, attempt.Status);
        Assert.Equal(sent, attempt.ReconciledSent);
        Assert.Null(attempt.TelegramMessageId);
        if (missing) Assert.Empty(await verify.Notifications.ToListAsync(Ct));
        else
        {
            var notice = await verify.Notifications.SingleAsync(Ct);
            Assert.Equal("replacement source text", notice.Text);
            Assert.Equal(NotificationStatus.OutcomeUnknown, notice.Status);
            Assert.Null(notice.SentAt);
        }
        Assert.Single(_api.Calls);
    }

    [Fact]
    public async Task Late_card_retirement_retains_the_original_account_and_chat_after_configuration_changes()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var draft = new CommentDraft { PostId = Guid.NewGuid().ToString("N"), PostUrl = "https://threads.invalid/post", Author = "synthetic",
            PostText = "synthetic post", Status = CommentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        var host = await HostAsync(true);
        _api.DuringSend = async _ =>
        {
            await context.CommentDrafts.Where(row => row.Id == draft.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, CommentStatus.Expired), Ct);
            _options.OwnerUserId++;
        };
        var notice = await NoticeAsync();
        notice.Kind = NotificationKind.CommentDraft;
        notice.EntityId = draft.Id;
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        var retirement = await context.Notifications.SingleAsync(row => row.EditKeyboardMessageId == 73, Ct);
        Assert.NotNull(retirement.DeliveryRequestId);
        await UntilAsync(async () => (await RootAsync(retirement.DeliveryRequestId.Value)).Stage == NotificationDeliveryStage.Failed);
        var root = await RootAsync(retirement.DeliveryRequestId.Value);
        var captured = JsonSerializer.Deserialize(root.InputJson, NotificationDeliveryJsonContext.Default.NotificationDeliveryInput)!;
        Assert.Equal(123456, captured.Target!.ChatId);
        Assert.Equal(123456, captured.Target.BotId);
        Assert.Single(_api.Calls);
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == root.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task Legacy_pending_notice_cannot_be_admitted_as_known_new_and_the_old_dispatcher_skips_native_owned_rows()
    {
        var producer = await HostAsync(false);
        var notice = await NoticeAsync();
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        producer.Services.GetRequiredService<JobFinder.Pipeline.ServerOwnership>().Acquired();
        using var dispatcher = new NotificationDispatcher(producer.Services.GetRequiredService<LegacyNotificationImports>(), new JobTriggers(), TimeProvider.System, NullLogger<NotificationDispatcher>.Instance);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Notifications.Where(row => row.DeliveryRequestId == null).ExecuteDeleteAsync(Ct);
        var batch = (Task<int>)typeof(NotificationDispatcher).GetMethod("ProcessBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dispatcher, [Ct])!;
        Assert.Equal(0, await batch);
        Assert.Empty(_api.Calls);
        var legacy = await NoticeAsync();
        context.Notifications.Add(legacy);
        await context.SaveChangesAsync(Ct);
        await using var scope = producer.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        outbox.Enroll(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Services.GetRequiredService<NotificationDeliveryRequests>().QueueAsync(context, outbox, legacy, Ct));
        Assert.Equal(NotificationDeliveryStage.Preparing, (await RootAsync(id)).Stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_comment_cards_keep_the_primary_receipt_and_transactionally_admit_keyboard_retirement(bool during)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var draft = new CommentDraft { PostId = Guid.NewGuid().ToString("N"), PostUrl = "https://threads.invalid/post", Author = "synthetic",
            PostText = "Синтетический пост", Status = CommentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        var producer = await HostAsync(false);
        var notice = await NoticeAsync();
        notice.Kind = NotificationKind.CommentDraft;
        notice.EntityId = draft.Id;
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        async Task ExpireAsync() => await context.CommentDrafts.Where(row => row.Id == draft.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, CommentStatus.Expired), Ct);
        if (during) _api.DuringSend = _ => ExpireAsync();
        else await ExpireAsync();
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(73, (await StoredAsync(id)).TelegramMessageId);
        var edit = Assert.Single(await context.Notifications.Where(row => row.EditKeyboardMessageId == 73).ToListAsync(Ct));
        Assert.Equal(draft.Id, edit.EntityId);
        Assert.Contains("noop:0:0", edit.Buttons.SelectMany(row => row).Single(), StringComparison.Ordinal);
        Assert.NotNull(edit.DeliveryRequestId);
        await UntilAsync(async () => (await RootAsync(edit.DeliveryRequestId.Value)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(new[] { "sendMessage", "editMessageReplyMarkup" }, _api.Calls.Select(call => call.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupted_captured_input_or_missing_source_fails_closed_before_the_first_effect(bool missing)
    {
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (missing) await context.Notifications.ExecuteDeleteAsync(Ct);
        else await context.NotificationDeliveryRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.InputHash, new string('0', 64)), Ct);
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Failed);
        Assert.Empty(_api.Calls);
        if (!missing) Assert.Equal(NotificationStatus.Failed, (await StoredAsync(id)).Status);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
    }

    private sealed class SendingCommitFault : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.Entity.Stage == NotificationDeliveryStage.Sending))
            {
                Fired = true;
                throw new IOException("synthetic lost sending commit observation");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ReceiptFault(bool io) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<NotificationDeliveryAttempt>().Any(entry => entry.Entity.Status == DeliveryAttemptStatus.Confirmed))
            {
                Fired = true;
                if (io) throw new IOException("synthetic lost notification receipt I/O commit");
                throw new InvalidOperationException("synthetic lost notification receipt commit");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BotHttp(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(TelegramBotClientHolder.HttpClientName, name);
            return new(handler, disposeHandler: false);
        }
    }

    private sealed record Recorded(string Method, bool Html, byte[]? Attachment, string? Text, string? Markup);

    private sealed class NotificationApi : HttpMessageHandler
    {
        public ConcurrentQueue<Recorded> Calls { get; } = new();
        public bool DocumentRateLimit { get; set; }
        public int? DocumentRetryAfter { get; set; } = 1;
        public bool DocumentUnknown { get; set; }
        public bool ParseRejection { get; set; }
        public string? KeyboardError { get; set; }
        public long ResponseChatId { get; set; } = 123456;
        public Func<string, Task>? DuringSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.telegram.org", request.RequestUri!.Host);
            var method = request.RequestUri.Segments[^1];
            Assert.Contains(method, new[] { "sendMessage", "sendDocument", "editMessageReplyMarkup" });
            var fields = new Dictionary<string, string>();
            byte[]? attachment = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (part.Headers.ContentDisposition.FileName is not null) attachment = await part.ReadAsByteArrayAsync(ct);
                    else fields[name] = await part.ReadAsStringAsync(ct);
                }
            }
            else
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                foreach (var property in json.RootElement.EnumerateObject()) fields[property.Name] = property.Value.ToString();
            }
            Assert.Equal("123456", fields["chat_id"]);
            var html = fields.GetValueOrDefault("parse_mode")?.Equals("HTML", StringComparison.OrdinalIgnoreCase) == true;
            Calls.Enqueue(new(method, html, attachment, fields.GetValueOrDefault("text"), fields.GetValueOrDefault("reply_markup")));
            if (DuringSend is { } during) await during(method);
            if (method == "sendDocument" && DocumentUnknown) throw new IOException("synthetic lost response after document acceptance");
            if (method == "sendDocument" && DocumentRateLimit)
            {
                DocumentRateLimit = false;
                return Error(429, "Too Many Requests", retryAfter: DocumentRetryAfter);
            }
            if (ParseRejection && html)
            {
                ParseRejection = false;
                return Error(400, "Bad Request: can't parse entities");
            }
            if (method == "editMessageReplyMarkup" && KeyboardError is { } error) return Error(400, error);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result = new { message_id = method == "editMessageReplyMarkup" ? int.Parse(fields["message_id"], System.Globalization.CultureInfo.InvariantCulture) : 72 + Calls.Count,
                    date = 1790800000, chat = new { id = ResponseChatId, type = "private" } } }), Encoding.UTF8, "application/json"),
            };
        }

        private static HttpResponseMessage Error(int code, string error, int? retryAfter = null) => new((HttpStatusCode)code)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = code, description = error,
                parameters = retryAfter is { } seconds ? new { retry_after = seconds } : null }), Encoding.UTF8, "application/json"),
        };
    }
}
