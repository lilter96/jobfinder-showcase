using JobFinder.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Fact]
    public async Task Two_actual_Blazor_letter_editors_preserve_conflicting_text_until_explicit_reload_and_deliver_only_the_accepted_revision()
    {
        var application = await SeedAsync();
        var gate = new Gate { WaitForResume = true };
        var recipient = new Recipient();
        var app = await BrowserHostAsync(gate, recipient);
        var originalId = await EnqueueAsync(app, application);
        await UntilAsync(async () => (await RequestAsync(originalId)).ResumeRequestId == gate.Dependency);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var first = await LocalPageAsync(browser, app, application.VacancyId);
        var stale = await LocalPageAsync(browser, app, application.VacancyId);
        var firstEditor = LetterEditor(first);
        var staleEditor = LetterEditor(stale);
        await Assertions.Expect(firstEditor).ToHaveValueAsync(application.CoverLetter!);
        await Assertions.Expect(staleEditor).ToHaveValueAsync(application.CoverLetter!);
        await firstEditor.FillAsync("Accepted first edit");
        await first.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(first.GetByText("письмо сохранено", new() { Exact = true })).ToBeVisibleAsync();
        const string conflicting = "My unsaved second-tab text";
        await staleEditor.FillAsync(conflicting);
        await stale.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.GetByText("письмо или отклик изменились; ваш текст не сохранён — загрузите актуальную версию", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(staleEditor).ToHaveValueAsync(conflicting);
        await BrowserScreenshotAsync(stale, originalId, "edit-conflict");
        await stale.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.GetByText("письмо или отклик изменились; ваш текст не сохранён — загрузите актуальную версию", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(staleEditor).ToHaveValueAsync(conflicting);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal(1, (await context.Applications.SingleAsync(Ct)).Revision);
            Assert.Equal(2, await context.ApplicationDeliveryRequests.CountAsync(Ct));
        }
        await stale.GetByRole(AriaRole.Button, new() { Name = "Загрузить актуальное письмо", Exact = true }).ClickAsync();
        await Assertions.Expect(staleEditor).ToHaveValueAsync("Accepted first edit");
        const string accepted = "Explicitly rebased second edit";
        await staleEditor.FillAsync(accepted);
        await stale.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.GetByText("письмо сохранено", new() { Exact = true })).ToBeVisibleAsync();
        Guid finalId;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var saved = await context.Applications.SingleAsync(Ct);
            Assert.Equal(2, saved.Revision);
            Assert.Equal(3, await context.ApplicationDeliveryRequests.CountAsync(Ct));
            finalId = saved.DeliveryRequestId!.Value;
        }
        gate.ResumeAvailable = true;
        await UntilAsync(async () => (await RequestAsync(finalId)).Stage == ApplicationDeliveryStage.Completed);
        Assert.Equal(accepted, Assert.Single(recipient.Deliveries).Text);
        await first.CloseAsync();
        await stale.CloseAsync();
    }

    private static ILocator LetterEditor(IPage page) => page.Locator("div.panel")
        .Filter(new() { Has = page.GetByText("Письмо", new() { Exact = true }) }).Locator("textarea");
}
