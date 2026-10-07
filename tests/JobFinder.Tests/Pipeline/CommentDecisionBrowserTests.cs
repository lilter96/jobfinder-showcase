using JobFinder.Core.Domain;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Fact]
    public async Task Actual_Blazor_queued_comment_cancellation_fences_a_second_stale_editor()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var draft = new CommentDraft
        {
            PostId = "browser-cancel", PostUrl = "https://www.threads.com/@author/post/browser-cancel", Author = "author",
            PostText = "Синтетический пост о сроках обратной связи", ChosenText = "Я бы договаривался о сроке ответа до следующего этапа.",
            Variants = [], Status = CommentStatus.Approved, Revision = 1, CreatedAt = DateTimeOffset.UtcNow,
        };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        var recipient = new Recipient();
        var app = await BrowserHostAsync(new Gate(), recipient);
        var id = (await app.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.CommentDeliveryRequests>().RequestAsync(draft.Id, Ct))!.Value;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var first = await browser.NewPageAsync();
        var stale = await browser.NewPageAsync();
        foreach (var page in new[] { first, stale })
        {
            await page.RouteAsync("**/*", route => new Uri(route.Request.Url).Host == "127.0.0.1" ? route.ContinueAsync() : route.AbortAsync());
            await page.GotoAsync(address + "/threads");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.Locator("select").SelectOptionAsync("Approved");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Отменить публикацию", Exact = true })).ToBeVisibleAsync();
        }
        await first.GetByRole(AriaRole.Button, new() { Name = "Отменить публикацию", Exact = true }).ClickAsync();
        await Assertions.Expect(first.GetByRole(AriaRole.Button, new() { Name = "Отменить публикацию", Exact = true })).ToHaveCountAsync(0);
        await stale.GetByRole(AriaRole.Button, new() { Name = "Отменить публикацию", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.GetByText("черновик изменился: обновите карточку", new() { Exact = true })).ToBeVisibleAsync();
        await BrowserScreenshotAsync(stale, id, "comment-cancel-conflict");
        var root = await context.CommentDeliveryRequests.SingleAsync(row => row.Id == id, Ct);
        Assert.Equal(CommentDeliveryStage.Cancelled, root.Stage);
        Assert.Empty(await context.CommentDeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(recipient.Deliveries);
        await first.CloseAsync();
        await stale.CloseAsync();
    }

    [Fact]
    public async Task Actual_Blazor_unknown_comment_is_read_only_and_points_to_manual_reconciliation()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var draft = new CommentDraft
        {
            PostId = "browser-unknown", PostUrl = "https://www.threads.com/@author/post/browser-unknown", Author = "author",
            PostText = "Синтетический пост", ChosenText = "Я бы заранее договаривался о сроке ответа.", Status = CommentStatus.Approved,
            Revision = 1, Attempts = 1, CreatedAt = DateTimeOffset.UtcNow,
        };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        var app = await BrowserHostAsync(new Gate(), new Recipient());
        var id = (await app.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.CommentDeliveryRequests>().RequestAsync(draft.Id, Ct))!.Value;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*", route => new Uri(route.Request.Url).Host == "127.0.0.1" ? route.ContinueAsync() : route.AbortAsync());
        await page.GotoAsync(address + "/threads");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.Locator("select").SelectOptionAsync("OutcomeUnknown");
        await Assertions.Expect(page.GetByText("Повтор остановлен.", new() { Exact = false })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("--comment-reconcile " + id, new() { Exact = false })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("textarea")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Отменить публикацию", Exact = true })).ToHaveCountAsync(0);
        await BrowserScreenshotAsync(page, id, "comment-unknown-readonly");
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
        await page.CloseAsync();
    }

    [Fact]
    public async Task Actual_Threads_editors_preserve_a_stale_custom_draft_and_revision_until_explicit_reload()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        const string accepted = "А какой срок ответа вы обычно называете кандидату?";
        const string conflicting = "Я бы заранее договаривался о дате следующего разговора.";
        var draft = new CommentDraft
        {
            PostId = "synthetic-browser-post", PostUrl = "https://threads.invalid/post/synthetic-browser-post", Author = "synthetic-author",
            PostText = "Синтетический пост о сроках ответа после собеседования", Status = CommentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow, Audience = AudienceKind.Recruiter, Variants = [accepted, conflicting],
        };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        var recipient = new Recipient();
        var app = await BrowserHostAsync(new Gate(), recipient);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var first = await browser.NewPageAsync();
        var stale = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1100 } });
        foreach (var page in new[] { first, stale })
        {
            await page.RouteAsync("**/*", route => new Uri(route.Request.Url).Host == "127.0.0.1" ? route.ContinueAsync() : route.AbortAsync());
            await page.GotoAsync(address + "/threads");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
        await first.Locator("textarea").FillAsync(accepted);
        await first.GetByRole(AriaRole.Button, new() { Name = "Опубликовать свой", Exact = true }).ClickAsync();
        await Assertions.Expect(first.GetByText("комментарий в очереди на публикацию", new() { Exact = true })).ToBeVisibleAsync();
        await stale.Locator("textarea").FillAsync(conflicting);
        await stale.GetByRole(AriaRole.Button, new() { Name = "Опубликовать свой", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.GetByText("черновик изменился: обновите карточку", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(stale.Locator("textarea")).ToHaveValueAsync(conflicting);
        await stale.GetByRole(AriaRole.Button, new() { Name = "Опубликовать свой", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.Locator("textarea")).ToHaveValueAsync(conflicting);
        await BrowserScreenshotAsync(stale, Guid.NewGuid(), "comment-edit-conflict");
        var stored = await context.CommentDrafts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(CommentStatus.Approved, stored.Status);
        Assert.Equal(accepted, stored.ChosenText);
        await stale.GetByRole(AriaRole.Button, new() { Name = "Обновить", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.Locator("textarea")).ToHaveCountAsync(0);
        Assert.Empty(recipient.Deliveries);
        await first.CloseAsync();
        await stale.CloseAsync();
    }
}
