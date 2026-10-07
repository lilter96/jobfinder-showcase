using System.Reflection;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Legacy_keyboard_edit_without_an_effect_witness_is_imported_as_unknown_without_a_new_card(int attempts)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = new Notification { Kind = NotificationKind.Info, Text = "Синтетическая устаревшая карточка", Status = NotificationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow, EditKeyboardMessageId = 73, Attempts = attempts, Buttons = [["Устаревшая карточка|noop:0:0"]] };
        context.Notifications.Add(notice);
        await context.SaveChangesAsync(Ct);
        using var dispatcher = await ImporterAsync();
        Assert.True(await DispatchAsync(dispatcher) >= 1);
        var stored = await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == notice.Id, Ct);
        Assert.Equal(NotificationStatus.OutcomeUnknown, stored.Status);
        Assert.Null(stored.TelegramMessageId);
        Assert.Null(stored.SentAt);
        Assert.Equal(attempts, stored.Attempts);
        var root = await context.NotificationDeliveryRequests.SingleAsync(row => row.Id == stored.DeliveryRequestId, Ct);
        Assert.Equal(NotificationDeliveryStage.OutcomeUnknown, root.Stage);
        Assert.Null(root.PlanJson);
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == root.Id).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_comment_card_does_not_fabricate_a_receipt_or_retirement_after_source_expiry(bool expired)
    {
        var draft = await SeedSourceDraftAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = new Notification { Kind = NotificationKind.CommentDraft, EntityId = draft.Id, Text = "Синтетическая карточка", Status = NotificationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow, Buttons = [["Опубликовать|c:pick:" + draft.Id + ":0:0"]] };
        context.Notifications.Add(notice);
        await context.SaveChangesAsync(Ct);
        if (expired)
        {
            await using var write = await pg.Db.CreateDbContextAsync(Ct);
            await using var transaction = await write.Database.BeginTransactionAsync(Ct);
            await CommentSources.LockAsync(write, Ct);
            await write.CommentDrafts.Where(row => row.Id == draft.Id).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Status, CommentStatus.Expired).SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
            await transaction.CommitAsync(Ct);
        }
        using var dispatcher = await ImporterAsync();
        Assert.True(await DispatchAsync(dispatcher) >= 1);
        var stored = await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == notice.Id, Ct);
        Assert.Equal(NotificationStatus.OutcomeUnknown, stored.Status);
        Assert.Null(stored.TelegramMessageId);
        Assert.Equal(notice.Buttons[0][0], stored.Buttons[0][0]);
        Assert.False(await context.Notifications.AnyAsync(row => row.EditParentNotificationId == notice.Id, Ct));
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == stored.DeliveryRequestId).ToListAsync(Ct));
    }

    private async Task<NotificationDispatcher> ImporterAsync()
    {
        var host = await HostAsync(false);
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        return new(host.Services.GetRequiredService<LegacyNotificationImports>(), new JobTriggers(), TimeProvider.System, NullLogger<NotificationDispatcher>.Instance);
    }

    private static Task<int> DispatchAsync(NotificationDispatcher dispatcher) => (Task<int>)typeof(NotificationDispatcher)
        .GetMethod("ProcessBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dispatcher, [Ct])!;
}
