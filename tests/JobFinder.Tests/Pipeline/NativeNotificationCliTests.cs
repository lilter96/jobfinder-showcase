using System.Diagnostics;
using System.Text;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    [Theory]
    [InlineData("--sent")]
    [InlineData("--not-sent")]
    public async Task Actual_notification_reconciliation_cli_records_checked_evidence_without_delivery_or_paid_preparation(string outcome)
    {
        _api.DocumentUnknown = true;
        var host = await HostAsync(true);
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(multipart: true), Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.OutcomeUnknown);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var calls = await context.LlmCalls.CountAsync(Ct);
        var home = Path.Combine(_directory, "cli");
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
        start.Environment["JOBFINDER_Paths__Data"] = Path.Combine(home, "data");
        start.Environment["JOBFINDER_Paths__Profile"] = Path.Combine(home, "profile");
        start.Environment["JOBFINDER_Llm__ApiKey"] = "synthetic-unused-key";
        start.Environment["JOBFINDER_Llm__Endpoint"] = "https://provider.invalid/completions";
        start.Environment["JOBFINDER_Llm__Proxy"] = "";
        start.Environment["JOBFINDER_Bot__Token"] = "";
        foreach (var argument in new[] { typeof(CliHost).Assembly.Location, "--notification-reconcile", id.ToString(), outcome, "--evidence", "Synthetic owner-checked exact notification outcome" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("isolated notification reconciliation CLI did not start");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("отправка не выполнялась", await output);
            Assert.Equal(NotificationDeliveryStage.Reconciled, (await RootAsync(id)).Stage);
            var attempt = await context.NotificationDeliveryAttempts.SingleAsync(row => row.EffectIndex == 1, Ct);
            Assert.Equal(DeliveryAttemptStatus.Reconciled, attempt.Status);
            Assert.Equal(outcome == "--sent", attempt.ReconciledSent);
            Assert.Null(attempt.TelegramMessageId);
            Assert.Null((await StoredAsync(id)).SentAt);
            Assert.Equal(calls, await context.LlmCalls.CountAsync(Ct));
            Assert.Empty(await context.TelegramSessions.ToListAsync(Ct));
            Assert.Equal(2, _api.Calls.Count);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
