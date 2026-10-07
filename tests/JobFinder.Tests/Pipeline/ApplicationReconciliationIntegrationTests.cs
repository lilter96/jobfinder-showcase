using System.Diagnostics;
using System.Globalization;
using System.Text;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_application_reconciliation_records_one_checked_outcome_without_resending_or_certifying_the_pdf(bool sent)
    {
        var application = await SeedAsync(channel: ApplicationChannel.TelegramDm);
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown, "synthetic uncertain receipt") };
        var host = await HostAsync(true, new Gate(), recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        var ledger = host.Services.GetRequiredService<DeliveryLedger>();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ledger.ReconcileApplicationAsync(id, sent, "checked <inbox>", Ct)));
        Assert.Single(results, result => result);
        await host.Services.GetRequiredService<ApplicationDeliveryWorkflow>().DispatchAsync(id, 1, Guid.NewGuid(), Ct);
        Assert.Equal(0, await ActivatorUtilities.CreateInstance<ApplyWorker>(host.Services).ReconcileAsync(Ct));
        Assert.Single(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(sent ? ApplicationStatus.Sent : ApplicationStatus.NeedsManual, saved.Status);
        Assert.Null(saved.TailoredResumeId);
        Assert.Equal(sent ? ApplicationDeliveryStage.Completed : ApplicationDeliveryStage.Cancelled, (await RequestAsync(id)).Stage);
        var attempt = await context.DeliveryAttempts.SingleAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, attempt.Status);
        Assert.Equal(123, attempt.TailoredResumeId);
        Assert.Contains("checked <inbox>", attempt.Error);
        Assert.Equal(sent ? 1 : 0, await context.ApplicationEvents.CountAsync(row => row.Channel == EventChannel.Manual, Ct));
        var notice = Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Info && row.Text.StartsWith("🧾 Сверка отклика")).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Contains("checked &lt;inbox&gt;", notice.Text);
        Assert.Contains("--application-reconcile", (await context.Notifications.SingleAsync(row => row.Kind == NotificationKind.ApplyFailed, Ct)).Text);
    }

    [Fact]
    public async Task The_existing_manual_sent_action_reconciles_the_native_root_and_does_not_certify_the_attachment()
    {
        var application = await SeedAsync(channel: ApplicationChannel.TelegramDm);
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown) };
        var host = await HostAsync(true, new Gate(), recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        var result = await host.Services.GetRequiredService<ApprovalService>().MarkManualAsync(application.VacancyId, Ct);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(ApplicationDeliveryStage.Completed, (await RequestAsync(id)).Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(ApplicationStatus.Sent, saved.Status);
        Assert.Null(saved.TailoredResumeId);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Single(await context.ApplicationEvents.Where(row => row.Channel == EventChannel.Manual).ToListAsync(Ct));
        var notice = Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Info && row.Text.StartsWith("🧾 Сверка отклика")).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.False((await host.Services.GetRequiredService<ApprovalService>().MarkManualAsync(application.VacancyId, Ct)).Ok);
        Assert.Single(recipient.Deliveries);
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("missing-marker")]
    [InlineData("revision")]
    [InlineData("operation")]
    [InlineData("attempt")]
    public async Task Reconciliation_refuses_changed_attempts_or_approval_witnesses(string change)
    {
        var application = await SeedAsync();
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown) };
        var host = await HostAsync(true, new Gate(), recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            if (change == "marker") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.DeliveryRequestId, Guid.NewGuid()), Ct);
            else if (change == "missing-marker") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.DeliveryRequestId, (Guid?)null), Ct);
            else if (change == "revision") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
            else if (change == "operation") await context.DeliveryAttempts.ExecuteUpdateAsync(update => update.SetProperty(row => row.OperationId, Guid.NewGuid()), Ct);
            else await context.DeliveryAttempts.ExecuteDeleteAsync(Ct);
        }
        Assert.False(await host.Services.GetRequiredService<DeliveryLedger>().ReconcileApplicationAsync(id, true, "checked inbox", Ct));
        Assert.False((await host.Services.GetRequiredService<ApprovalService>().MarkManualAsync(application.VacancyId, Ct)).Ok);
        Assert.Equal(ApplicationDeliveryStage.OutcomeUnknown, (await RequestAsync(id)).Stage);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStatus.OutcomeUnknown, (await read.Applications.SingleAsync(Ct)).Status);
        Assert.Empty(await read.Notifications.Where(row => row.Kind == NotificationKind.Info && row.Text.StartsWith("🧾 Сверка отклика")).ToListAsync(Ct));
        Assert.Single(recipient.Deliveries);
    }

    [Fact]
    public async Task A_reconciliation_save_failure_rolls_back_the_outcome_funnel_and_notice()
    {
        var application = await SeedAsync();
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown) };
        var fault = new ReconciliationFault();
        var host = await HostAsync(true, new Gate(), recipient, fault);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        var ledger = host.Services.GetRequiredService<DeliveryLedger>();
        await Assert.ThrowsAsync<IOException>(() => ledger.ReconcileApplicationAsync(id, true, "checked inbox", Ct));
        Assert.Equal(ApplicationDeliveryStage.OutcomeUnknown, (await RequestAsync(id)).Stage);
        await using (var read = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal(ApplicationStatus.OutcomeUnknown, (await read.Applications.SingleAsync(Ct)).Status);
            Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await read.DeliveryAttempts.SingleAsync(Ct)).Status);
            Assert.Empty(await read.ApplicationEvents.ToListAsync(Ct));
            Assert.Empty(await read.Notifications.Where(row => row.Kind == NotificationKind.Info && row.Text.StartsWith("🧾 Сверка отклика")).ToListAsync(Ct));
        }
        Assert.True(await ledger.ReconcileApplicationAsync(id, true, "checked inbox", Ct));
        Assert.Equal(1, fault.Failures);
        Assert.Single(recipient.Deliveries);
    }

    [Fact]
    public async Task Reconciliation_uses_the_authoritative_attempt_without_reinterpreting_a_damaged_preparation_snapshot()
    {
        var application = await SeedAsync();
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown) };
        var host = await HostAsync(true, new Gate(), recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.ApplicationDeliveryRequests.ExecuteUpdateAsync(update => update.SetProperty(row => row.InputJson, "{\"version\":99}"), Ct);
        var ledger = host.Services.GetRequiredService<DeliveryLedger>();
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.ReconcileApplicationAsync(id, false, " ", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.ReconcileApplicationAsync(id, false, new string('x', 2001), Ct));
        Assert.Equal(ApplicationDeliveryStage.OutcomeUnknown, (await RequestAsync(id)).Stage);
        Assert.True(await ledger.ReconcileApplicationAsync(id, false, "checked inbox", Ct));
        Assert.Single(recipient.Deliveries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_actual_application_reconciliation_cli_records_a_checked_outcome_without_any_send(bool sent)
    {
        var application = await SeedAsync();
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown) };
        var host = await HostAsync(true, new Gate(), recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        var invalid = await ReconcileCliAsync(id, "--sent", "--not-sent", "--evidence", "synthetic checked inbox");
        Assert.Equal(2, invalid.Code);
        Assert.Equal(ApplicationDeliveryStage.OutcomeUnknown, (await RequestAsync(id)).Stage);
        var result = await ReconcileCliAsync(id, sent ? "--sent" : "--not-sent", "--evidence", "synthetic checked inbox");
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("Ничего не отправлялось", result.Output);
        Assert.Equal(3, (await ReconcileCliAsync(id, "--sent", "--evidence", "duplicate decision")).Code);
        Assert.Single(recipient.Deliveries);
        Assert.Equal(sent ? ApplicationDeliveryStage.Completed : ApplicationDeliveryStage.Cancelled, (await RequestAsync(id)).Stage);
    }

    private async Task<(int Code, string Output)> ReconcileCliAsync(Guid id, params string[] arguments)
    {
        var home = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-application-cli", id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(home, "config", "appsettings.json"), true);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_HOME"] = home;
        start.Environment["JOBFINDER_Database__ConnectionString"] = context.Database.GetConnectionString();
        start.Environment["JOBFINDER_Paths__Prompts"] = Fixture.PromptsDir;
        start.Environment["JOBFINDER_Paths__Data"] = Path.Combine(home, "data");
        start.ArgumentList.Add(typeof(CliHost).Assembly.Location);
        start.ArgumentList.Add("--application-reconcile");
        start.ArgumentList.Add(id.ToString("D", CultureInfo.InvariantCulture));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start isolated application reconciliation CLI");
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(Timeout, Ct);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private sealed class ReconciliationFault : SaveChangesInterceptor
    {
        public int Failures;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Failures == 0 && eventData.Context!.ChangeTracker.Entries<DeliveryAttempt>().Any(entry => entry.Entity.Status == DeliveryAttemptStatus.Reconciled))
            {
                Interlocked.Increment(ref Failures);
                throw new IOException("synthetic reconciliation save failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
