using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.App.Cli;
using JobFinder.App.Components.Pages;
using JobFinder.Core.Profile;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Profile;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed partial class CandidateIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_profile_page_rejects_stale_edit_or_confirmation_and_preserves_editor_text(bool confirm)
    {
        await Profiles.SaveAsync(Profile("Displayed") with { Reviewed = false }, 0, "test", Ct);
        using var host = await PageHostAsync();
        var capture = new CaptureComponent<SettingsPage>();
        using var services = PageServices(host, capture);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<SettingsPage>(ParameterView.Empty);
            SetField(capture.Page, "_profileJson", ProfileProvider.ToJson(Profile("Unsaved edit")));
            await Profiles.SaveAsync(Profile("New profile") with { Reviewed = false }, 1, "other-editor", Ct);
            await InvokeAsync(capture.Page, confirm ? "ConfirmProfile" : "SaveProfile");
            Repaint(capture.Page);
            var html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Загрузить актуальный профиль", html);
            Assert.Contains("Unsaved edit", html);
            var current = (await Profiles.GetSnapshotAsync(Ct))!;
            Assert.Equal("New profile", current.Value.Name);
            Assert.False(current.Value.Reviewed);
            Assert.Equal(2, current.Version);
            await InvokeAsync(capture.Page, "LoadProfileAsync");
            await InvokeAsync(capture.Page, "ConfirmProfile");
            var confirmed = (await Profiles.GetSnapshotAsync(Ct))!;
            Assert.Equal(3, confirmed.Version);
            Assert.True(confirmed.Value.Reviewed);
            Assert.Equal("New profile", confirmed.Value.Name);
        });
    }

    [Fact]
    public async Task Actual_facts_page_keeps_unsaved_answers_on_conflict_and_can_reload_the_current_snapshot()
    {
        await Facts.UpsertAsync("salary", "Salary", "Original salary", Ct);
        using var host = await PageHostAsync();
        var capture = new CaptureComponent<JobFinder.App.Components.Pages.Facts>();
        using var services = PageServices(host, capture);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<JobFinder.App.Components.Pages.Facts>(ParameterView.Empty);
            var edited = GetField<CandidateFacts>(capture.Page, "_facts");
            edited.Get("salary")!.Answer = "Unsaved salary";
            await Facts.UpsertAsync("notice", "Notice", "New notice", Ct);
            await InvokeAsync(capture.Page, "Save");
            Repaint(capture.Page);
            var html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Загрузить актуальные факты", html);
            Assert.Contains("Unsaved salary", html);
            Assert.Equal("Original salary", (await Facts.GetSnapshotAsync(Ct)).Value.Get("salary")!.Answer);
            await InvokeAsync(capture.Page, "LoadFactsAsync");
            Repaint(capture.Page);
            html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Original salary", html);
            Assert.Contains("New notice", html);
            Assert.DoesNotContain("Unsaved salary", html);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_import_cli_preserves_preferences_and_refuses_changes_made_during_the_provider_call(bool concurrentEdit)
    {
        await Profiles.SaveAsync(Profile("Original") with { Preferences = ["User preference"], DealBreakers = ["User restriction"],
            DesiredSalary = new() { Amount = 4000, Currency = "USD" } }, 0, "test", Ct);
        var root = Path.Combine(Path.GetTempPath(), "jobfinder-candidate-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(root, "config", "appsettings.json"));
        var resume = Path.Combine(root, "synthetic-resume.txt");
        await File.WriteAllTextAsync(resume, "Synthetic Candidate, Senior .NET Developer. Built synthetic C# services.", new UTF8Encoding(false), Ct);
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        Task<(int Code, string Output)>? child = null;
        try
        {
            child = ImportCliAsync(root, resume, $"http://127.0.0.1:{port}/completions", timeout.Token);
            var accepted = listener.GetContextAsync().WaitAsync(timeout.Token);
            if (await Task.WhenAny(child, accepted) == child) Assert.Fail("CLI exited before contacting the synthetic provider: " + (await child).Output);
            var request = await accepted;
            using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
            var body = JsonNode.Parse(await reader.ReadToEndAsync(timeout.Token))!;
            Assert.Equal("candidate_profile", body["response_format"]!["json_schema"]!["name"]!.ToString());
            if (concurrentEdit) await Profiles.SaveAsync(Profile("Concurrent edit") with { Reviewed = false }, 1, "dashboard", Ct);
            var wire = new CandidateProfileWire { Name = "Imported", Title = "Senior .NET Developer", Preferences = ["Model preference"] };
            var response = new JsonObject
            {
                ["model"] = "synthetic-profile-provider",
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject
                    { ["content"] = JsonSerializer.Serialize(wire, GeneratedWireJsonContext.Default.CandidateProfileWire) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 100, ["cost"] = 0.0001m },
            };
            request.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
            request.Response.ContentLength64 = bytes.Length;
            await request.Response.OutputStream.WriteAsync(bytes, timeout.Token);
            request.Response.Close();
            var result = await child;
            Assert.True(result.Code == (concurrentEdit ? 3 : 0), result.Output);
            var current = (await Profiles.GetSnapshotAsync(Ct))!;
            Assert.Equal(2, current.Version);
            Assert.False(current.Value.Reviewed);
            if (concurrentEdit)
            {
                Assert.Equal("Concurrent edit", current.Value.Name);
                Assert.Null(await Documents.GetAsync(ProfileProvider.PreviousKey, Ct));
                Assert.Contains("Ничего не перезаписано", result.Output);
            }
            else
            {
                Assert.Equal("Imported", current.Value.Name);
                Assert.Equal(["User preference"], current.Value.Preferences);
                Assert.Equal(["User restriction"], current.Value.DealBreakers);
                Assert.Equal(4000, current.Value.DesiredSalary!.Amount);
                Assert.Equal("Original", ProfileProvider.FromJson((await Documents.GetAsync(ProfileProvider.PreviousKey, Ct))!.Json).Name);
            }
            await using var audit = await pg.Db.CreateDbContextAsync(Ct);
            var revisions = await audit.CandidateRevisions.OrderBy(row => row.Version).ToListAsync(Ct);
            Assert.Equal([1L, 2L], revisions.Select(row => row.Version));
            Assert.Equal(concurrentEdit ? "dashboard" : "resume-import", revisions[1].Actor);
            Assert.Equal(2, await audit.Notifications.CountAsync(row => row.Text.StartsWith("👤 Данные кандидата обновлены: candidate.profile"), Ct));
        }
        finally
        {
            listener.Stop();
            timeout.Cancel();
            if (child is not null)
            {
                try { await child; }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(root, true);
        }
    }

    private async Task<IHost> PageHostAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
        });
        builder.Services.AddJobFinderCore(builder.Configuration);
        builder.Services.AddSingleton(await pg.NotificationsAsync());
        return builder.Build();
    }

    private static ServiceProvider PageServices<T>(IHost host, CaptureComponent<T> capture) where T : IComponent, new() =>
        new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture)
            .AddSingleton(host.Services.GetRequiredService<ProfileProvider>()).AddSingleton(host.Services.GetRequiredService<FactsProvider>())
            .AddSingleton(host.Services.GetRequiredService<JobFinder.Pipeline.Settings.RuntimeSettings>())
            .AddSingleton(host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.ApplicationBlocks>())
            .AddSingleton(host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.SearchPolicyStore>())
            .AddSingleton(host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.EngagementPolicyStore>())
            .AddSingleton(host.Services.GetRequiredService<JobFinder.Pipeline.Career.HrPlaybookService>()).BuildServiceProvider();

    private async Task<(int Code, string Output)> ImportCliAsync(string home, string path, string endpoint, CancellationToken ct)
    {
        await using var context = await pg.Db.CreateDbContextAsync(ct);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_HOME"] = home;
        start.Environment["JOBFINDER_Database__ConnectionString"] = context.Database.GetConnectionString();
        start.Environment["JOBFINDER_Paths__Prompts"] = Fixture.PromptsDir;
        start.Environment["JOBFINDER_Llm__Endpoint"] = endpoint;
        start.Environment["JOBFINDER_Llm__ApiKey"] = "synthetic-loopback-test-key";
        start.Environment["JOBFINDER_Llm__Proxy"] = "";
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.deps.json"));
        start.ArgumentList.Add(typeof(CliHost).Assembly.Location);
        start.ArgumentList.Add("--import-resume");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start isolated candidate CLI");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static void SetField(object page, string name, object value) => page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static T GetField<T>(object page, string name) => (T)page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static Task InvokeAsync(object page, string method) => (Task)page.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
    private static void Repaint(object page) => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);

    private sealed class CaptureComponent<T> : IComponentActivator where T : IComponent, new()
    {
        public T Page { get; } = new();
        public IComponent CreateInstance(Type type) => type == typeof(T) ? Page : (IComponent)Activator.CreateInstance(type)!;
    }
}
