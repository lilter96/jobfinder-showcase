using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    private async Task DeliverStoredQuestionAsync(OutreachBotApi api)
    {
        var holder = new TelegramBotClientHolder(new BotOptions { Token = "123456:synthetic-test-token", OwnerUserId = 123456 }, new EditQuestionHttp(api));
        var transport = new NotificationDeliveryTransport(new BotOptions { Token = "123456:synthetic-test-token", OwnerUserId = 123456 }, holder);
        var owner = _host!.Services.GetRequiredService<ServerOwnership>();
        owner.Acquired();
        var workflow = ActivatorUtilities.CreateInstance<NotificationDeliveryWorkflow>(_host.Services, transport);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = await context.Notifications.AsNoTracking().SingleAsync(row => row.Kind == NotificationKind.EditPrompt, Ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var root = await context.NotificationDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == notice.DeliveryRequestId, Ct);
            if (root.Stage is NotificationDeliveryStage.Completed or NotificationDeliveryStage.OutcomeUnknown) return;
            if (root.RetryAt is { } retry && retry > DateTimeOffset.UtcNow)
            {
                await Task.Delay(retry - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(10), Ct);
                continue;
            }
            if (root.Stage == NotificationDeliveryStage.AwaitingTarget)
                await workflow.BindAsync(new BindNotificationTarget { RequestId = root.Id }, Guid.NewGuid(), Ct);
            else
                await workflow.RunAsync(new DeliverNotification { RequestId = root.Id, Stage = root.Stage, EffectIndex = root.EffectIndex }, Guid.NewGuid(), Ct);
        }
        Assert.Fail("Edit question did not reach a terminal receipt");
    }

    private static async Task NoSourceAdmissionBeyondQuestionAsync(JobFinderDbContext context)
    {
        var notices = await context.Notifications.AsNoTracking().ToListAsync(Ct);
        var notice = Assert.Single(notices, row => row.Kind == NotificationKind.EditPrompt);
        Assert.Equal(NotificationKind.EditPrompt, notice.Kind);
        foreach (var feedback in notices.Where(row => row.Id != notice.Id))
        {
            Assert.Equal(NotificationKind.Info, feedback.Kind);
            Assert.Null(feedback.EntityId);
            Assert.Empty(feedback.Buttons);
            Assert.Null(feedback.AttachmentPath);
            await NotificationAdmissionAssertions.JoinedAsync(context, feedback, Ct, new NotificationTarget { BotId = 123456, ChatId = 123456 });
        }
        var root = await context.NotificationDeliveryRequests.AsNoTracking().SingleAsync(row => row.NotificationId == notice.Id, Ct);
        Assert.Equal(notice.Id, root.NotificationId);
        Assert.Equal(NotificationDeliveryStage.Completed, root.Stage);
        var attempt = Assert.Single(await context.NotificationDeliveryAttempts.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(root.Id, attempt.RequestId);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, attempt.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edit_question_intent_notice_root_and_command_commit_together_or_all_roll_back(bool fail)
    {
        var (source, contact) = await OutreachDecisionAsync();
        _fault.Active = fail;
        var edits = _host!.Services.GetRequiredService<BotEdits>();
        Task<CapturedBotEdit> PublishAsync() => edits.BeginQuestionAsync(123456, 123456,
            new BotEditPrompt { Kind = "outreach", SourceId = source.Id, Outreach = JobFinder.Pipeline.Modules.Hiring.OutreachEditSnapshot.Capture(source, contact) }, "Synthetic edit question", Ct);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(PublishAsync);
        else Assert.IsType<CapturedBotEdit>(await PublishAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (fail)
        {
            Assert.Empty(await context.Documents.Where(row => row.Key.StartsWith("bot.edit.")).ToListAsync(Ct));
            await NoAdmissionAsync(context);
        }
        else
        {
            Assert.Single(await context.Documents.Where(row => row.Key.StartsWith("bot.edit.")).ToListAsync(Ct));
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
            var root = await context.NotificationDeliveryRequests.SingleAsync(Ct);
            Assert.Equal(3, NotificationDeliveryInputs.Input(root).Version);
            Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        }
        Assert.Equal(0, _provider.Calls);
    }

    private sealed class EditQuestionHttp(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
