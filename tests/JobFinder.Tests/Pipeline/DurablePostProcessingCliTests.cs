using System.Diagnostics;
using System.Text;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurablePostProcessingTests
{
    [Fact]
    public async Task Actual_CLI_producer_uses_the_owned_native_consumer_without_providers_or_a_second_Telegram_client()
    {
        var steps = new Steps();
        await HostAsync(true, steps);
        var post = await PostAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var calls = await context.LlmCalls.CountAsync(Ct);
        var home = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-post-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(home, "config", "appsettings.json"));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_HOME"] = home;
        start.Environment["JOBFINDER_Database__ConnectionString"] = context.Database.GetConnectionString();
        start.Environment["JOBFINDER_Paths__Prompts"] = Fixture.PromptsDir;
        start.Environment["JOBFINDER_Llm__ApiKey"] = "synthetic-unused-key";
        start.Environment["JOBFINDER_Llm__Endpoint"] = "https://provider.invalid/completions";
        start.Environment["JOBFINDER_Llm__Proxy"] = "";
        start.Environment["JOBFINDER_Bot__Token"] = "";
        foreach (var argument in new[] { typeof(CliHost).Assembly.Location, "--post-process", post.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("post CLI did not start");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(40), Ct);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("post processed", await output);
            Assert.Equal(calls, await context.LlmCalls.CountAsync(Ct));
            Assert.Empty(await context.TelegramSessions.ToListAsync(Ct));
            Assert.Equal(1, steps.Extractions);
            Assert.Equal(PostProcessingStage.Completed, (await context.PostProcessingRequests.SingleAsync(Ct)).Stage);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
