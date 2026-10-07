using System.Diagnostics;
using System.Text;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    [Theory]
    [InlineData("--sent")]
    [InlineData("--not-sent")]
    public async Task Real_reconciliation_CLI_changes_only_the_checked_ledger_without_a_provider_or_publication(string outcome)
    {
        var owner = await DeliveryHostAsync(true);
        await SourceProfileAsync(owner);
        var id = await QueueDeliveryAsync(owner, await DeliveryDraftAsync(attempts: 1));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var existingCalls = await context.LlmCalls.CountAsync(Ct);
        var home = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-comment-reconcile-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(home, "config", "appsettings.json"));
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
        start.Environment["JOBFINDER_Llm__ApiKey"] = "synthetic-unused-key";
        start.Environment["JOBFINDER_Llm__Endpoint"] = "https://provider.invalid/completions";
        start.Environment["JOBFINDER_Llm__Proxy"] = "";
        start.Environment["JOBFINDER_Bot__Token"] = "";
        start.ArgumentList.Add(typeof(CliHost).Assembly.Location);
        start.ArgumentList.Add("--comment-reconcile");
        start.ArgumentList.Add(id.ToString());
        start.ArgumentList.Add(outcome);
        start.ArgumentList.Add("--evidence");
        start.ArgumentList.Add("Synthetic checked exact Threads post");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("isolated comment reconciliation CLI did not start");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("публикация не выполнялась", await output);
            Assert.Equal(DeliveryAttemptStatus.Reconciled, (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
            Assert.Equal(outcome == "--sent", (await context.CommentDeliveryAttempts.SingleAsync(Ct)).ReconciledSent);
            Assert.Equal(existingCalls, await context.LlmCalls.CountAsync(Ct));
            Assert.Empty(await context.TelegramSessions.ToListAsync(Ct));
            Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
