using System.Text.Json;
using ApplicationRoot = JobFinder.App.Components.App;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace JobFinder.Tests.Pipeline;

public sealed partial class InterviewSourceIntegrationTests
{
    [Fact]
    public async Task Actual_Blazor_interview_editors_preserve_conflicting_time_kind_duration_until_explicit_reload()
    {
        var source = await InterviewAsync();
        var app = await BrowserHostAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var first = await BrowserPageAsync(browser, app, "interviews");
        var stale = await BrowserPageAsync(browser, app, "interviews");
        await first.Locator("input[type=number]").FillAsync("90");
        await first.Locator("select").SelectOptionAsync(nameof(InterviewKind.SystemDesign));
        await first.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(first.Locator("input[type=number]")).ToHaveValueAsync("90");
        await Assertions.Expect(first.Locator("select")).ToHaveValueAsync(nameof(InterviewKind.SystemDesign));
        await stale.Locator("input[type=number]").FillAsync("120");
        await stale.Locator("select").SelectOptionAsync(nameof(InterviewKind.Coding));
        const string typedTime = "2026-10-15T17:30";
        await stale.Locator("input[type=datetime-local]").FillAsync(typedTime);
        await stale.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.GetByText(new InterviewEditConflictException().Message, new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(stale.Locator("input[type=number]")).ToHaveValueAsync("120");
        await Assertions.Expect(stale.Locator("select")).ToHaveValueAsync(nameof(InterviewKind.Coding));
        await Assertions.Expect(stale.Locator("input[type=datetime-local]")).ToHaveValueAsync(typedTime);
        await stale.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.GetByText(new InterviewEditConflictException().Message, new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(90, (await ReadAsync(source.Id)).DurationMinutes);
        await ScreenshotAsync(stale, "form-conflict");
        await stale.GetByRole(AriaRole.Button, new() { Name = "Обновить данные (сбросить несохранённые правки)", Exact = true }).ClickAsync();
        await Assertions.Expect(stale.GetByText("Данные обновлены", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(stale.Locator("input[type=number]")).ToHaveValueAsync("90");
        await stale.Locator("input[type=number]").FillAsync("120");
        await stale.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await UntilBrowserAsync(async () => (await ReadAsync(source.Id)).DurationMinutes == 120);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await final.InterviewPreparationRequests.CountAsync(Ct));
        Assert.Equal(1, await final.InterviewPreparationRequests.CountAsync(row => row.Stage == InterviewPreparationStage.Cancelled, Ct));
    }

    [Fact]
    public async Task Actual_Blazor_position_editor_retains_typed_text_when_the_displayed_pack_is_superseded()
    {
        var source = await InterviewAsync(pack: true);
        var pack = new InterviewCopilot
        {
            Version = InterviewCopilot.CurrentVersion,
            Preparation = [new()
            {
                Q = new PlannedQuestion { Id = "about", Question = "Tell me about your experience", Category = "hr" },
                A = new SpokenAnswer { EvidenceLevel = "personal_unknown", PositionOptions = ["Confirmed answer"] },
            }],
        };
        var json = JsonSerializer.Serialize(pack, InterviewJsonContext.Default.InterviewCopilot);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Interviews.ExecuteUpdateAsync(update => update.SetProperty(row => row.PackJson, json), Ct);
        var app = await BrowserHostAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await BrowserPageAsync(browser, app, "interviews/" + source.Id);
        var own = page.GetByPlaceholder("Своими словами");
        const string typed = "My unsaved interview position";
        await own.FillAsync(typed);
        var commands = app.Services.GetRequiredService<InterviewSources>();
        await commands.SetTimeAsync((await commands.CaptureAsync(source.Id, Ct))!, source.ScheduledAt!.Value.AddDays(1), Ct);
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText(new InterviewEditConflictException().Message, new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(own).ToHaveValueAsync(typed);
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(own).ToHaveValueAsync(typed);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, (await final.Documents.SingleAsync(row => row.Key == FactsProvider.Key, Ct)).Version);
        await ScreenshotAsync(page, "position-conflict");
        await page.GetByRole(AriaRole.Button, new() { Name = "Обновить данные (сбросить несохранённые правки)", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Подготовки к этому интервью нет или она старого формата: страница «Интервью» → «Пересобрать».", new() { Exact = true })).ToBeVisibleAsync();
    }

    private async Task<WebApplication> BrowserHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ApplicationRoot).Assembly.GetName().Name, ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development", Args = [],
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0").UseStaticWebAssets();
        await PostProcessingTestHost.ConfigureAsync(builder, pg, consumer: false);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddDataProtection().PersistKeysToDbContext<JobFinderDbContext>().SetApplicationName("JobFinder.SyntheticInterviews");
        var app = builder.Build();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<ApplicationRoot>().AddInteractiveServerRenderMode();
        _host = app;
        await app.StartAsync(Ct);
        await app.Services.GetRequiredService<JobFinderLlm>().ForOperationAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Notifications.ExecuteDeleteAsync(Ct);
        return app;
    }

    private static async Task<IPage> BrowserPageAsync(IBrowser browser, WebApplication app, string route)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Assert.Equal("127.0.0.1", new Uri(address).Host);
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1100 } });
        await page.RouteAsync("**/*", request => new Uri(request.Request.Url).Host == "127.0.0.1" ? request.ContinueAsync() : request.AbortAsync());
        await page.GotoAsync(address + "/" + route);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }

    private static async Task UntilBrowserAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Blazor source command did not commit");
            await Task.Delay(50, Ct);
        }
    }

    private static Task ScreenshotAsync(IPage page, string name)
    {
        var path = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-interview-browser");
        Directory.CreateDirectory(path);
        return page.ScreenshotAsync(new() { Path = Path.Combine(path, name + ".png"), FullPage = true });
    }
}
