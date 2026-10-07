using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_owner_report_preserves_complete_text_and_unknown_document_never_causes_a_fallback_or_resend(bool unknown)
    {
        var producer = await HostAsync(false);
        var notice = await NoticeAsync(multipart: true);
        var client = new TelegramBotClientHolder(_options, new BotHttp(_api)).ControlClient!;
        var publisher = new OwnerBotMessages(_options, producer.Services.GetRequiredService<NotificationDeliveryRequests>(), TimeProvider.System);
        var id = await publisher.PublishAsync(client, 123456, notice.Text, Ct, notice.Buttons, notice.AttachmentPath, notice.AttachmentName, entityId: 42);
        Assert.Empty(_api.Calls);
        _api.DocumentUnknown = unknown;
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == (unknown ? NotificationDeliveryStage.OutcomeUnknown : NotificationDeliveryStage.Completed));
        var effects = _api.Calls.ToArray();
        Assert.Equal(2, effects.Length);
        Assert.Equal("sendMessage", effects[0].Method);
        Assert.Equal(notice.Text, effects[0].Text);
        Assert.Equal("sendDocument", effects[1].Method);
        Assert.Equal(await File.ReadAllBytesAsync(notice.AttachmentPath!, Ct), effects[1].Attachment);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(42, (await StoredAsync(id)).EntityId);
        var receipts = await context.NotificationDeliveryAttempts.AsNoTracking().OrderBy(row => row.EffectIndex).ToListAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, receipts[0].Status);
        Assert.Equal(unknown ? DeliveryAttemptStatus.OutcomeUnknown : DeliveryAttemptStatus.Confirmed, receipts[1].Status);
        var workflow = consumer.Services.GetRequiredService<NotificationDeliveryWorkflow>();
        await workflow.RunAsync(new DeliverNotification { RequestId = id, Stage = NotificationDeliveryStage.Ready }, Guid.NewGuid(), Ct);
        Assert.Equal(2, _api.Calls.Count);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Native_owner_report_with_a_missing_document_pauses_before_any_SDK_effect_instead_of_dropping_the_file()
    {
        var producer = await HostAsync(false);
        var notice = await NoticeAsync(multipart: true);
        File.Delete(notice.AttachmentPath!);
        var client = new TelegramBotClientHolder(_options, new BotHttp(_api)).ControlClient!;
        var id = await new OwnerBotMessages(_options, producer.Services.GetRequiredService<NotificationDeliveryRequests>(), TimeProvider.System)
            .PublishAsync(client, 123456, notice.Text, Ct, notice.Buttons, notice.AttachmentPath, notice.AttachmentName);
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).RetryAt is not null);
        var saved = await RootAsync(id);
        Assert.Equal(NotificationDeliveryStage.Preparing, saved.Stage);
        Assert.Equal(notice.AttachmentPath, NotificationDeliveryInputs.Input(saved).AttachmentPath);
        Assert.Empty(_api.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Native_owner_report_cannot_rebind_to_a_new_owner_after_captured_publication()
    {
        var producer = await HostAsync(false);
        var client = new TelegramBotClientHolder(_options, new BotHttp(_api)).ControlClient!;
        var id = await new OwnerBotMessages(_options, producer.Services.GetRequiredService<NotificationDeliveryRequests>(), TimeProvider.System)
            .PublishAsync(client, 123456, "Synthetic owner report", Ct);
        var before = await RootAsync(id);
        _options.OwnerUserId = 654321;
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Failed);
        var after = await RootAsync(id);
        Assert.Equal(before.InputJson, after.InputJson);
        Assert.Equal(before.AdmissionHash, after.AdmissionHash);
        Assert.Equal(123456, NotificationDeliveryInputs.Input(after).Target!.ChatId);
        Assert.Empty(_api.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
    }
}
