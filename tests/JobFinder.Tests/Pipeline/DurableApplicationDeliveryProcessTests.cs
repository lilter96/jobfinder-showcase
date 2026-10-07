using System.Diagnostics;
using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("claim")]
    [InlineData("effect")]
    [InlineData("receipt")]
    public async Task Actual_process_termination_preserves_application_receipts_and_never_retries_an_uncertain_recipient_effect(string boundary)
    {
        var application = await SeedAsync();
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var id = await EnqueueAsync(producer, application);
        await StopAsync(producer);
        await KillApplicationConsumerAsync(id, boundary);
        var before = await RequestAsync(id);
        Assert.Equal(boundary == "ready" ? ApplicationDeliveryStage.Ready
            : boundary == "receipt" ? ApplicationDeliveryStage.Completed : ApplicationDeliveryStage.Sending, before.Stage);
        Assert.Equal(boundary is "effect" or "receipt" ? 1 : 0, await ProcessEffectsAsync(id));
        var recipient = new Recipient();
        var consumer = await HostAsync(true, new Gate(), recipient);
        var expected = boundary is "ready" or "receipt" ? ApplicationDeliveryStage.Completed : ApplicationDeliveryStage.OutcomeUnknown;
        await UntilAsync(async () => (await RequestAsync(id)).Stage == expected);
        await consumer.Services.GetRequiredService<ApplicationDeliveryWorkflow>().DispatchAsync(id, 1, Guid.NewGuid(), Ct);
        Assert.Equal(boundary == "ready" ? 1 : 0, recipient.Deliveries.Count);
        Assert.Equal(boundary is "effect" or "receipt" ? 1 : 0, await ProcessEffectsAsync(id));
        await using var saved = await pg.Db.CreateDbContextAsync(Ct);
        var attempt = await saved.DeliveryAttempts.SingleAsync(Ct);
        Assert.Equal(expected == ApplicationDeliveryStage.Completed ? DeliveryAttemptStatus.Confirmed : DeliveryAttemptStatus.OutcomeUnknown, attempt.Status);
        Assert.Equal(expected == ApplicationDeliveryStage.Completed ? ApplicationStatus.Sent : ApplicationStatus.OutcomeUnknown,
            (await saved.Applications.SingleAsync(Ct)).Status);
        Assert.Equal(expected == ApplicationDeliveryStage.OutcomeUnknown ? 1 : 0, await saved.Notifications.CountAsync(row => row.Kind == NotificationKind.ApplyFailed, Ct));
    }

    private async Task<int> ProcessEffectsAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = ApplicationDeliveryProbe.CounterKey(id);
        var value = await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct);
        return value is null ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private async Task KillApplicationConsumerAsync(Guid id, string boundary)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_DISPOSABLE_TEST_DATABASE"] = context.Database.GetConnectionString();
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"));
        start.ArgumentList.Add("--application-consumer");
        start.ArgumentList.Add(id.ToString("N"));
        start.ArgumentList.Add(boundary);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start isolated application consumer");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.True(ready == "READY", ready ?? await stderr);
            Assert.False(process.HasExited);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
