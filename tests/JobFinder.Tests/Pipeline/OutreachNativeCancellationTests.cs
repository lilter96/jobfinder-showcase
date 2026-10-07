using JobFinder.Core.Domain;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class EmployerMessageIntegrationTests
{
    [Fact]
    public async Task Actual_native_reply_review_is_cancelled_atomically_before_any_recipient_effect()
    {
        var source = await ReplyAsync(ApplicationChannel.Email);
        var snapshot = ReplyEditSnapshot.Capture(source, source.Application!);
        var gate = new Gate { Hold = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var service = host.Services.GetRequiredService<ReplyService>();
        var sending = service.SendReplyAsync(source.Id, null, Ct, expected: snapshot);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(35), Ct);
        try { Assert.True((await service.DismissDraftAsync(source.Id, Ct, snapshot)).Ok); }
        finally { gate.Release.TrySetResult(); }
        Assert.False((await sending).Ok);
        Assert.Equal(EmployerMessageStatus.Cancelled, (await MessageAsync()).Status);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DraftStatus.Dismissed, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
        var notice = await context.Notifications.SingleAsync(row => row.Text.StartsWith("Ответ на сообщение #"), Ct);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(recipient.Deliveries);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Actual_native_reply_submitting_refuses_cancel_and_preserves_the_original_receipt()
    {
        var source = await ReplyAsync(ApplicationChannel.Email);
        var snapshot = ReplyEditSnapshot.Capture(source, source.Application!);
        var recipient = new Recipient { Hold = true };
        var host = await HostAsync(true, new Gate(), recipient);
        var service = host.Services.GetRequiredService<ReplyService>();
        var sending = service.SendReplyAsync(source.Id, null, Ct, expected: snapshot);
        await recipient.Started.Task.WaitAsync(TimeSpan.FromSeconds(35), Ct);
        try { Assert.False((await service.DismissDraftAsync(source.Id, Ct, snapshot)).Ok); }
        finally { recipient.Release.TrySetResult(); }
        Assert.True((await sending).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DraftStatus.Sent, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
        Assert.Single(recipient.Deliveries);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Actual_native_outreach_review_is_cancelled_with_source_and_notice_before_any_recipient_effect()
    {
        var (vacancy, contact) = await SeedAsync(ContactKind.Telegram, true);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = new Outreach { VacancyId = vacancy.Id, ContactId = contact.Id, Text = "Synthetic note", Fresh = true, CreatedAt = DateTimeOffset.UtcNow };
        context.Outreaches.Add(source);
        await context.SaveChangesAsync(Ct);
        var snapshot = OutreachEditSnapshot.Capture(source, contact);
        var gate = new Gate { Hold = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var service = host.Services.GetRequiredService<OutreachService>();
        var sending = service.SendAsync(source.Id, Ct, snapshot: snapshot);
        await gate.Started.Task.WaitAsync(Ct);
        try { Assert.True((await service.DismissAsync(source.Id, Ct, snapshot)).Ok); }
        finally { gate.Release.TrySetResult(); }
        Assert.False((await sending).Ok);
        Assert.Equal(EmployerMessageStatus.Cancelled, (await MessageAsync()).Status);
        Assert.Equal(OutreachStatus.Dismissed, (await context.Outreaches.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).Status);
        var notice = await context.Notifications.SingleAsync(row => row.Text.StartsWith("Аутрич #"), Ct);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(recipient.Deliveries);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Actual_native_outreach_submitting_receipt_fences_a_displayed_cancel_without_losing_the_send()
    {
        var (vacancy, contact) = await SeedAsync(ContactKind.Telegram, true);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = new Outreach { VacancyId = vacancy.Id, ContactId = contact.Id, Text = "Synthetic note", Fresh = true, CreatedAt = DateTimeOffset.UtcNow };
        context.Outreaches.Add(source);
        await context.SaveChangesAsync(Ct);
        var snapshot = OutreachEditSnapshot.Capture(source, contact);
        var recipient = new Recipient { Hold = true };
        var host = await HostAsync(true, new Gate(), recipient);
        var service = host.Services.GetRequiredService<OutreachService>();
        var sending = service.SendAsync(source.Id, Ct, snapshot: snapshot);
        await recipient.Started.Task.WaitAsync(Ct);
        try { Assert.False((await service.DismissAsync(source.Id, Ct, snapshot)).Ok); }
        finally { recipient.Release.TrySetResult(); }
        Assert.True((await sending).Ok);
        Assert.Equal(OutreachStatus.Sent, (await context.Outreaches.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).Status);
        Assert.Single(recipient.Deliveries);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
    }
}
