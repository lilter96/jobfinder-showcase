using JobFinder.Core.Policies;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Fact]
    public async Task The_actual_Engagement_settings_editors_preserve_conflicts_until_explicit_reload_without_external_effects()
    {
        var recipient = new Recipient();
        var app = await BrowserHostAsync(new Gate(), recipient);
        var store = app.Services.GetRequiredService<EngagementPolicyStore>();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var first = await LocalPageAsync(browser, app, 1);
        var second = await LocalPageAsync(browser, app, 1);
        var address = new Uri(first.Url).GetLeftPart(UriPartial.Authority);
        await first.GotoAsync(address + "/settings", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await second.GotoAsync(address + "/settings", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(first.Locator("#engagement-limit")).ToHaveValueAsync("10");
        await Assertions.Expect(second.Locator("#engagement-limit")).ToHaveValueAsync("10");
        await first.Locator("#engagement-auto").UncheckAsync();
        await first.Locator("#engagement-limit").FillAsync("19");
        await first.GetByRole(AriaRole.Button, new() { Name = "Сохранить публикацию Threads", Exact = true }).ClickAsync();
        await Assertions.Expect(first.GetByText("Публикация Threads сохранена: применяется к следующему проходу.", new() { Exact = true })).ToBeVisibleAsync();
        await second.Locator("#engagement-limit").FillAsync("37");
        await second.GetByRole(AriaRole.Button, new() { Name = "Сохранить публикацию Threads", Exact = true }).ClickAsync();
        await Assertions.Expect(second.GetByText("Политика уже изменилась. Ваши правки сохранены в форме; загрузите актуальную версию перед сохранением.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(second.Locator("#engagement-limit")).ToHaveValueAsync("37");
        await BrowserScreenshotAsync(second, Guid.NewGuid(), "engagement-policy-conflict");
        var current = await store.ReadAsync(Ct);
        Assert.Equal(2, current.Version);
        Assert.Equal(new EngagementPolicy { AutoPublish = false, MaxDraftsPerDay = 19 }, current.Value);
        await second.GetByRole(AriaRole.Button, new() { Name = "Загрузить актуальную публикацию Threads", Exact = true }).ClickAsync();
        await Assertions.Expect(second.Locator("#engagement-limit")).ToHaveValueAsync("19");
        await Assertions.Expect(second.Locator("#engagement-auto")).Not.ToBeCheckedAsync();
        await second.Locator("#engagement-limit").FillAsync("22");
        await second.GetByRole(AriaRole.Button, new() { Name = "Сохранить публикацию Threads", Exact = true }).ClickAsync();
        await Assertions.Expect(second.GetByText("Публикация Threads сохранена: применяется к следующему проходу.", new() { Exact = true })).ToBeVisibleAsync();
        current = await store.ReadAsync(Ct);
        Assert.Equal(3, current.Version);
        Assert.Equal(22, current.Value.MaxDraftsPerDay);
        Assert.Empty(recipient.Deliveries);
        await first.CloseAsync();
        await second.CloseAsync();
    }
}
