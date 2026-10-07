using ApplicationRoot = JobFinder.App.Components.App;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Fact]
    public async Task The_actual_Blazor_letter_editor_atomically_replaces_native_work_and_delivers_only_the_new_text()
    {
        var application = await SeedAsync();
        var gate = new Gate { WaitForResume = true };
        var recipient = new Recipient();
        var app = await BrowserHostAsync(gate, recipient);
        var oldId = await EnqueueAsync(app, application);
        await UntilAsync(async () => (await RequestAsync(oldId)).ResumeRequestId == gate.Dependency);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await LocalPageAsync(browser, app, application.VacancyId);
        const string replacement = "Здравствуйте!\n\nПишу по вакансии .NET Developer. Обновил проверяемый текст в реальном редакторе.\n\nГотов обсудить задачи.";
        var editor = page.Locator("div.panel").Filter(new() { Has = page.GetByText("Письмо", new() { Exact = true }) }).Locator("textarea");
        await Assertions.Expect(editor).ToHaveValueAsync(application.CoverLetter!);
        await editor.FillAsync(replacement);
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("письмо сохранено", new() { Exact = true })).ToBeVisibleAsync();
        Guid newId;
        await using (var read = await pg.Db.CreateDbContextAsync(Ct))
        {
            var saved = await read.Applications.SingleAsync(Ct);
            Assert.Equal(1, saved.Revision);
            newId = Assert.IsType<Guid>(saved.DeliveryRequestId);
            Assert.NotEqual(oldId, newId);
            Assert.Equal(replacement, saved.CoverLetter);
            Assert.Equal(2, await read.ApplicationDeliveryRequests.CountAsync(Ct));
        }
        await BrowserScreenshotAsync(page, oldId, "edited");
        gate.ResumeAvailable = true;
        await UntilAsync(async () => (await RequestAsync(oldId)).Stage == ApplicationDeliveryStage.Cancelled
            && (await RequestAsync(newId)).Stage == ApplicationDeliveryStage.Completed);
        Assert.Equal(replacement, Assert.Single(recipient.Deliveries).Text);
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = application.Vacancy!.Title })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("✅ Отклик отправлен", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Загрузка...", new() { Exact = true })).ToHaveCountAsync(0);
        await BrowserScreenshotAsync(page, oldId, "delivered");
        await page.CloseAsync();
    }

    [Fact]
    public async Task The_actual_Blazor_checked_sent_button_reconciles_native_unknown_delivery_without_a_second_effect()
    {
        var application = await SeedAsync();
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown, "synthetic uncertain receipt") };
        var app = await BrowserHostAsync(new Gate(), recipient);
        var id = await EnqueueAsync(app, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await LocalPageAsync(browser, app, application.VacancyId);
        await Assertions.Expect(page.Locator("div.panel").Filter(new() { Has = page.GetByText("Письмо", new() { Exact = true }) }).Locator("textarea")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "✅ Нашёл отправленный отклик", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("сверка записана; исходное письмо/PDF не сертифицированы как доставленные", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(ApplicationDeliveryStage.Completed, (await RequestAsync(id)).Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Null((await context.Applications.SingleAsync(Ct)).TailoredResumeId);
        Assert.Single(recipient.Deliveries);
        await BrowserScreenshotAsync(page, id, "reconciled");
        await page.CloseAsync();
    }

    private async Task<WebApplication> BrowserHostAsync(Gate gate, Recipient recipient)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ApplicationRoot).Assembly.GetName().Name, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Development", Args = [],
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0").UseStaticWebAssets();
        await ConfigureAsync(builder, true, gate, recipient);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddDataProtection().PersistKeysToDbContext<JobFinderDbContext>().SetApplicationName("JobFinder.SyntheticTests");
        var app = builder.Build();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<ApplicationRoot>().AddInteractiveServerRenderMode();
        _gates.Add(gate);
        _hosts.Add(app);
        await app.StartAsync(Ct).WaitAsync(Timeout, Ct);
        return app;
    }

    private static async Task<IPage> LocalPageAsync(IBrowser browser, WebApplication app, long vacancyId)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Assert.Equal("127.0.0.1", new Uri(address).Host);
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1100 } });
        await page.RouteAsync("**/*", route => new Uri(route.Request.Url).Host == "127.0.0.1" ? route.ContinueAsync() : route.AbortAsync());
        await page.GotoAsync($"{address}/vacancies/{vacancyId}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }

    private static Task BrowserScreenshotAsync(IPage page, Guid id, string phase)
    {
        var directory = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-application-browser", id.ToString("N"));
        Directory.CreateDirectory(directory);
        return page.ScreenshotAsync(new() { Path = Path.Combine(directory, phase + ".png"), FullPage = true });
    }
}
