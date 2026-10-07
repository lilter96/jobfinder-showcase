using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    private async Task<(CapturedBotEdit Prompt, Guid Root)> QuestionAsync(IHost host, long botId = 123456, long chatId = 123456)
    {
        var prompt = await host.Services.GetRequiredService<BotEdits>().BeginQuestionAsync(botId, chatId,
            new BotEditPrompt { Kind = "letter", SourceId = 42,
                Letter = new LetterEditSnapshot { VacancyId = 42, Status = VacancyStatus.Scored, Text = "Original synthetic letter" } },
            "✏️ Synthetic question <not HTML>", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = await context.Notifications.AsNoTracking().SingleAsync(row => row.Kind == NotificationKind.EditPrompt, Ct);
        return (prompt, notice.DeliveryRequestId!.Value);
    }

    [Fact]
    public async Task Native_edit_question_uses_force_reply_and_commits_its_binding_with_the_confirmed_receipt_once()
    {
        var producer = await HostAsync(false);
        var (prompt, id) = await QuestionAsync(producer);
        var birth = await RootAsync(id);
        Assert.Equal(3, NotificationDeliveryInputs.Input(birth).Version);
        Assert.Empty(_api.Calls);
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        var sent = Assert.Single(_api.Calls);
        Assert.Equal("sendMessage", sent.Method);
        Assert.False(sent.Html);
        Assert.Equal(BotEdits.Text("✏️ Synthetic question <not HTML>", prompt), sent.Text);
        using var markup = JsonDocument.Parse(sent.Markup!);
        Assert.True(markup.RootElement.GetProperty("force_reply").GetBoolean());
        var bound = Assert.IsType<CapturedBotEdit>(await consumer.Services.GetRequiredService<BotEdits>().FindAsync(123456, 123456, 73, null, null, Ct));
        Assert.Equal(prompt.Value.SnapshotHash, bound.Value.SnapshotHash);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var attempt = await context.NotificationDeliveryAttempts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, attempt.Status);
        Assert.Equal(73, attempt.TelegramMessageId);
        var completed = await RootAsync(id);
        var plan = Assert.IsType<NotificationPlan>(JsonSerializer.Deserialize(completed.PlanJson!, NotificationDeliveryJsonContext.Default.NotificationPlan));
        Assert.Equal(NotificationEffectKind.EditPrompt, plan.Effects.Single().Kind);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = id, Stage = NotificationDeliveryStage.Ready });
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = id });
        await Task.Delay(350, Ct);
        Assert.Single(_api.Calls);
        Assert.Equal(2, await context.Documents.CountAsync(row => row.Key.StartsWith("bot.edit."), Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Native_edit_question_retains_an_unknown_effect_and_a_trusted_reply_can_recover_binding_without_resend()
    {
        var producer = await HostAsync(false);
        var (prompt, id) = await QuestionAsync(producer);
        _api.DuringSend = _ => throw new IOException("synthetic accepted question with lost SDK response");
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        var sent = Assert.Single(_api.Calls);
        var edits = consumer.Services.GetRequiredService<BotEdits>();
        Assert.Null(await edits.FindAsync(123456, 123456, 73, null, null, Ct));
        var recovered = Assert.IsType<CapturedBotEdit>(await edits.FindAsync(123456, 123456, 73, sent.Text, 123456, Ct));
        Assert.Equal(prompt.Value.SnapshotHash, recovered.Value.SnapshotHash);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DeliverNotification { RequestId = id, Stage = NotificationDeliveryStage.Sending });
        await Task.Delay(350, Ct);
        Assert.Single(_api.Calls);
        Assert.Equal(NotificationDeliveryStage.OutcomeUnknown, (await RootAsync(id)).Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.NotificationDeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Null((await context.NotificationDeliveryAttempts.SingleAsync(Ct)).TelegramMessageId);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_edit_question_receipt_commit_failure_rolls_back_binding_and_stops_uncertain_delivery(bool io)
    {
        var producer = await HostAsync(false);
        var (_, id) = await QuestionAsync(producer);
        var fault = new ReceiptFault(io);
        var consumer = await HostAsync(true, fault);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        Assert.True(fault.Fired);
        Assert.Single(_api.Calls);
        Assert.Null(await consumer.Services.GetRequiredService<BotEdits>().FindAsync(123456, 123456, 73, null, null, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Documents.Where(row => row.Key.StartsWith("bot.edit.")).ToListAsync(Ct));
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.NotificationDeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Null((await StoredAsync(id)).TelegramMessageId);
    }

    [Fact]
    public async Task Native_edit_question_receipt_preserves_a_reply_that_arrives_before_the_sdk_result_is_committed()
    {
        var producer = await HostAsync(false);
        var (prompt, id) = await QuestionAsync(producer);
        _api.DuringSend = async _ =>
        {
            var edits = producer.Services.GetRequiredService<BotEdits>();
            var bound = Assert.IsType<CapturedBotEdit>(await edits.FindAsync(123456, 123456, 73, _api.Calls.Single().Text, 123456, Ct));
            Assert.NotNull(await edits.RecordReplyAsync(bound, 900, "User reply before receipt commit", Ct));
        };
        var consumer = await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        var saved = Assert.IsType<CapturedBotEdit>(await consumer.Services.GetRequiredService<BotEdits>().FindAsync(123456, 123456, 73, null, null, Ct));
        Assert.Equal(prompt.Value.SnapshotHash, saved.Value.SnapshotHash);
        Assert.Equal("User reply before receipt commit", saved.Value.Draft);
        Assert.Equal(900, saved.Value.UserMessageId);
        Assert.Equal(3, saved.Revision);
        Assert.Single(_api.Calls);
    }

    [Theory]
    [InlineData("bot")]
    [InlineData("chat")]
    [InlineData("missing")]
    [InlineData("future")]
    [InlineData("already-bound")]
    public async Task Native_edit_question_refuses_wrong_accounts_and_damaged_or_bound_intents_before_any_sdk_call(string damage)
    {
        var producer = await HostAsync(false);
        var (prompt, id) = await QuestionAsync(producer, damage == "bot" ? 654321 : 123456, damage == "chat" ? 654321 : 123456);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (damage == "missing") await context.Documents.Where(row => row.Key.StartsWith("bot.edit.")).ExecuteDeleteAsync(Ct);
        else if (damage == "future") await context.Documents.Where(row => row.Key.StartsWith("bot.edit.")).ExecuteUpdateAsync(update => update.SetProperty(row => row.Content, "{\"version\":99}"), Ct);
        else if (damage == "already-bound") await producer.Services.GetRequiredService<BotEdits>().BindAsync(123456, 123456, prompt.Value.Id, 73, Ct);
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Failed);
        Assert.Empty(_api.Calls);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
    }

    [Fact]
    public async Task Native_edit_question_without_a_target_binds_the_first_matching_configuration_without_rewriting_admission()
    {
        _options.Enabled = false;
        var producer = await HostAsync(false);
        var (_, id) = await QuestionAsync(producer);
        var before = await RootAsync(id);
        Assert.Equal(NotificationDeliveryStage.AwaitingTarget, before.Stage);
        _options.Enabled = true;
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        var after = await RootAsync(id);
        Assert.Equal(before.AdmissionHash, after.AdmissionHash);
        Assert.Equal(before.AdmissionJson, after.AdmissionJson);
        Assert.NotEqual(before.InputHash, after.InputHash);
        Assert.Single(_api.Calls);
    }
}
