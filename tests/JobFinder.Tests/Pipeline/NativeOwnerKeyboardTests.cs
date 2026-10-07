using System.Reflection;
using System.Text.Json;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    [Fact]
    public async Task Blank_non_keyboard_notification_still_fails_before_any_SDK_effect()
    {
        var host = await HostAsync(true);
        var notice = await NoticeAsync();
        notice.Text = "";
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Failed);
        Assert.Empty(_api.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
    }

    private async Task<(IHost Host, Notification Parent, OwnerBotMessages Outputs)> KeyboardCardAsync(IInterceptor? fault = null)
    {
        var host = await HostAsync(true, fault);
        var parent = await NoticeAsync();
        parent.Buttons = [["Action|synthetic-action"], ["Other row|noop:0:0"]];
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(parent, Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        return (host, parent, new OwnerBotMessages(_options, host.Services.GetRequiredService<NotificationDeliveryRequests>(), TimeProvider.System));
    }

    private async Task WakeKeyboardAsync(IHost host, Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.NotificationDeliveryRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        var root = await RootAsync(id);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = id, Stage = root.Stage, EffectIndex = root.EffectIndex });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_career_keyboard_painter_publishes_native_edits_preserving_other_rows_or_clearing_them(bool clear)
    {
        var (host, parent, outputs) = await KeyboardCardAsync();
        var bot = new TelegramBotClientHolder(_options, new BotHttp(_api)).ControlClient!;
        var constructor = typeof(CareerBot).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(BotOptions) ? (object)_options
            : parameter.ParameterType == typeof(OwnerBotMessages) ? outputs
            : parameter.ParameterType == typeof(ILogger<CareerBot>) ? NullLogger<CareerBot>.Instance : host.Services.GetService(parameter.ParameterType)).ToArray();
        var service = (CareerBot)constructor.Invoke(arguments);
        var callback = new CallbackQuery { Id = "synthetic-keyboard", Data = "synthetic-action", From = new User { Id = 123456 },
            Message = new Message { Id = 73, Chat = new Chat { Id = 123456 }, ReplyMarkup = NotificationDispatcher.Keyboard(parent.Buttons) } };
        _options.Enabled = false;
        if (!clear) await outputs.EditKeyboardAsync(bot, 123456, 73, [["Earlier row result|noop:0:0"]], Ct, replaceCallback: "noop:0:0");
        var method = typeof(CareerBot).GetMethod(clear ? "ClearKeyboardAsync" : "DoneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var call = method.Invoke(service, clear ? [bot, callback, Ct] : [bot, callback, ActionResult.Done("Done"), Ct, true]);
        await Assert.IsAssignableFrom<Task>(call);
        Assert.Single(_api.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var edit = await context.Notifications.AsNoTracking().Where(row => row.EditParentNotificationId == parent.Id).OrderByDescending(row => row.Id).FirstAsync(Ct);
        var input = NotificationDeliveryInputs.Input(await RootAsync(edit.DeliveryRequestId!.Value));
        if (clear) Assert.Empty(input.Buttons);
        else
        {
            Assert.Equal("✔️ Done|noop:0:0", input.Buttons[0].Single());
            Assert.Equal("Earlier row result|noop:0:0", input.Buttons[1].Single());
        }
        _options.Enabled = true;
        await WakeKeyboardAsync(host, edit.DeliveryRequestId.Value);
        await UntilAsync(async () => (await RootAsync(edit.DeliveryRequestId.Value)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal("editMessageReplyMarkup", _api.Calls.Last().Method);
        Assert.Equal(2, _api.Calls.Count);
        Assert.Equal(73, (await StoredAsync(edit.DeliveryRequestId.Value)).TelegramMessageId);
        Assert.Equal(parent.Text, (await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == parent.Id, Ct)).Text);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
    }

    [Fact]
    public async Task Native_keyboard_admission_reuses_identical_intents_and_supersedes_old_delayed_edits_before_SDK()
    {
        var (host, parent, outputs) = await KeyboardCardAsync();
        var bot = new TelegramBotClientHolder(_options, new BotHttp(_api)).ControlClient!;
        _options.Enabled = false;
        var first = (await outputs.EditKeyboardAsync(bot, 123456, 73, [["Old|noop:0:0"]], Ct))!.Value;
        Assert.Equal(first, await outputs.EditKeyboardAsync(bot, 123456, 73, [["Old|noop:0:0"]], Ct));
        var last = (await outputs.EditKeyboardAsync(bot, 123456, 73, [["Latest|noop:0:0"]], Ct))!.Value;
        Assert.NotEqual(first, last);
        _options.Enabled = true;
        await WakeKeyboardAsync(host, first);
        await WakeKeyboardAsync(host, last);
        await UntilAsync(async () => (await RootAsync(first)).Stage == NotificationDeliveryStage.Cancelled
            && (await RootAsync(last)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(2, _api.Calls.Count);
        Assert.Contains("Latest", _api.Calls.Last().Markup!);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == first).ToListAsync(Ct));
        Assert.Single(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == last && row.Status == DeliveryAttemptStatus.Confirmed).ToListAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(row => row.EditParentNotificationId == parent.Id, Ct));
    }

    [Fact]
    public async Task Native_keyboard_waits_for_checked_unknown_predecessor_and_never_resends_that_effect()
    {
        var (host, _, outputs) = await KeyboardCardAsync();
        var bot = new TelegramBotClientHolder(_options, new BotHttp(_api)).ControlClient!;
        _api.DuringSend = method => method == "editMessageReplyMarkup" ? throw new IOException("synthetic accepted keyboard with lost receipt") : Task.CompletedTask;
        var uncertain = (await outputs.EditKeyboardAsync(bot, 123456, 73, [["Uncertain|noop:0:0"]], Ct))!.Value;
        await UntilAsync(async () => (await RootAsync(uncertain)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        var next = (await outputs.EditKeyboardAsync(bot, 123456, 73, [["Next|noop:0:0"]], Ct))!.Value;
        await UntilAsync(async () => (await RootAsync(next)).Error?.Contains("reconciliation", StringComparison.Ordinal) == true);
        Assert.Equal(2, _api.Calls.Count);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == next).ToListAsync(Ct));
        await host.Services.GetRequiredService<NotificationDeliveryLedger>().ReconcileAsync(uncertain, true, "synthetic checked keyboard outcome", Ct);
        _api.DuringSend = null;
        await WakeKeyboardAsync(host, next);
        await UntilAsync(async () => (await RootAsync(next)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(3, _api.Calls.Count);
        Assert.Equal(NotificationDeliveryStage.Reconciled, (await RootAsync(uncertain)).Stage);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, (await context.NotificationDeliveryAttempts.AsNoTracking().SingleAsync(row => row.RequestId == uncertain, Ct)).Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bot")]
    [InlineData("chat")]
    [InlineData("receipt")]
    [InlineData("receipt-hash")]
    [InlineData("source")]
    public async Task Native_keyboard_without_exact_original_account_and_confirmed_primary_receipt_is_not_admitted(string damage)
    {
        var (host, _, _) = await KeyboardCardAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (damage == "receipt") await context.NotificationDeliveryAttempts.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, DeliveryAttemptStatus.Reconciled), Ct);
        if (damage == "receipt-hash") await context.NotificationDeliveryAttempts.ExecuteUpdateAsync(update => update.SetProperty(row => row.EffectHash, new string('0', 64)), Ct);
        if (damage == "source") await context.Notifications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Text, "Changed source"), Ct);
        var result = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishKeyboardAsync(
            new NotificationTarget { BotId = damage == "bot" ? 654321 : 123456, ChatId = damage == "chat" ? 654321 : 123456 },
            damage == "missing" ? 999 : 73, [["Refused|noop:0:0"]], Ct);
        Assert.Null(result);
        Assert.Single(_api.Calls);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Single(await context.NotificationDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Native_keyboard_notice_root_and_command_roll_back_together_and_retry_does_not_resend_parent()
    {
        var fault = new KeyboardAdmissionFault();
        var (host, _, outputs) = await KeyboardCardAsync(fault);
        var bot = new TelegramBotClientHolder(_options, new BotHttp(_api)).ControlClient!;
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => outputs.EditKeyboardAsync(bot, 123456, 73, [], Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Single(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Single(_api.Calls);
        fault.Active = false;
        var id = (await outputs.EditKeyboardAsync(bot, 123456, 73, [], Ct))!.Value;
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(2, _api.Calls.Count);
        Assert.Equal("editMessageReplyMarkup", _api.Calls.Last().Method);
    }

    private sealed class KeyboardAdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Active && eventData.Context is JobFinderDbContext context && context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("synthetic keyboard admission rollback");
            return ValueTask.FromResult(result);
        }
    }
}
