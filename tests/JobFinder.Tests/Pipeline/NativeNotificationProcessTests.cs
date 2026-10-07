using System.Diagnostics;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    [Theory]
    [InlineData("before-claim-commit")]
    [InlineData("after-claim-commit")]
    [InlineData("before-effect")]
    [InlineData("after-effect")]
    [InlineData("before-receipt-commit")]
    [InlineData("after-receipt-commit")]
    public async Task Actual_notification_process_termination_never_repeats_a_confirmed_or_uncertain_effect(string boundary)
    {
        var producer = await HostAsync(false);
        var notice = await NoticeAsync();
        notice.Text = NotificationProbe.Marker;
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        await producer.StopAsync(Ct);
        await KillNotificationProcessAsync(id, boundary);
        var wasCommitted = boundary != "before-claim-commit";
        var complete = boundary == "after-receipt-commit";
        var before = await RootAsync(id);
        Assert.Equal(complete ? NotificationDeliveryStage.Completed : wasCommitted ? NotificationDeliveryStage.Sending : NotificationDeliveryStage.Ready, before.Stage);
        var recovered = await PostProcessingTestHost.BuildAsync(pg, true, fastRecovery: true, configure: services =>
            services.AddSingleton<INotificationDeliveryTransport>(new NotificationProbeTransport(pg.Db, id)));
        _hosts.Add(recovered);
        await recovered.StartAsync(Ct);
        var expected = !wasCommitted || complete ? NotificationDeliveryStage.Completed : NotificationDeliveryStage.OutcomeUnknown;
        await UntilAsync(async () => (await RootAsync(id)).Stage == expected);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = NotificationProbeTransport.CounterKey(id);
        var effects = await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct);
        Assert.Equal(boundary is "after-claim-commit" or "before-effect" ? null : "1", effects);
        var root = await RootAsync(id);
        Assert.Equal(before.InputHash, root.InputHash);
        Assert.Equal(before.PlanHash, root.PlanHash);
        Assert.Null(root.ClaimEnvelopeId);
        var attempt = Assert.Single(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(expected == NotificationDeliveryStage.Completed ? DeliveryAttemptStatus.Confirmed : DeliveryAttemptStatus.OutcomeUnknown, attempt.Status);
        var stored = await StoredAsync(id);
        Assert.Equal(expected == NotificationDeliveryStage.Completed ? NotificationStatus.Sent : NotificationStatus.OutcomeUnknown, stored.Status);
        Assert.Equal(expected == NotificationDeliveryStage.Completed ? 73 : null, stored.TelegramMessageId);
        Assert.Single(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(_api.Calls);
    }

    private async Task KillNotificationProcessAsync(Guid id, string boundary)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_DISPOSABLE_TEST_DATABASE"] = context.Database.GetConnectionString();
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"), "--notification-consumer", id.ToString("N"), boundary })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("isolated notification consumer did not start");
        var errors = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line != "READY") continue;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(Ct);
                return;
            }
            Assert.Fail("notification consumer exited before its boundary: " + await errors);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
