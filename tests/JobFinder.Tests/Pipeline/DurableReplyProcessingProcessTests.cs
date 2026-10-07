using System.Diagnostics;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableReplyProcessingTests
{
    [Theory]
    [InlineData(ReplyProcessingStage.Capturing, "before-checkpoint")]
    [InlineData(ReplyProcessingStage.Capturing, "after-checkpoint")]
    [InlineData(ReplyProcessingStage.Fast, "before-effect")]
    [InlineData(ReplyProcessingStage.Fast, "after-effect")]
    [InlineData(ReplyProcessingStage.Fast, "before-checkpoint")]
    [InlineData(ReplyProcessingStage.Fast, "after-checkpoint")]
    [InlineData(ReplyProcessingStage.Deep, "before-effect")]
    [InlineData(ReplyProcessingStage.Deep, "after-effect")]
    [InlineData(ReplyProcessingStage.Deep, "before-checkpoint")]
    [InlineData(ReplyProcessingStage.Deep, "after-checkpoint")]
    [InlineData(ReplyProcessingStage.Publishing, "before-checkpoint")]
    [InlineData(ReplyProcessingStage.Publishing, "after-checkpoint")]
    public async Task Actual_incoming_reply_process_termination_preserves_analysis_checkpoints_and_source_card_atomicity(ReplyProcessingStage phase, string boundary)
    {
        var producer = await HostAsync(false, new Steps());
        var raw = await IncomingAsync(ReplyProbe.Marker);
        var ticket = await producer.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(raw, Ct);
        await producer.StopAsync(Ct);
        await KillReplyProcessAsync(ticket.RequestId, phase, boundary);
        var before = await RootAsync(ticket.RequestId);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var published = phase == ReplyProcessingStage.Publishing && boundary == "after-checkpoint";
            Assert.Equal(published ? 1 : 0, await context.ApplicationEvents.CountAsync(Ct));
            Assert.Equal(published ? 1 : 0, await context.Notifications.CountAsync(Ct));
            Assert.Equal(published, before.Stage == ReplyProcessingStage.Completed);
        }
        var recovered = await PostProcessingTestHost.BuildAsync(pg, true, fastRecovery: true, configure: services =>
        {
            services.RemoveAll<IReplyProcessingSteps>();
            services.AddSingleton<IReplyProcessingSteps>(new ReplyProbeSteps(pg.Db, ticket.RequestId));
        });
        _hosts.Add(recovered);
        await recovered.StartAsync(Ct);
        var complete = await WaitAsync(ticket.RequestId, root => root.Stage == ReplyProcessingStage.Completed);
        Assert.Equal(before.RawJson, complete.RawJson);
        Assert.Equal(before.RawHash, complete.RawHash);
        if (before.InputJson is not null)
        {
            Assert.Equal(before.InputJson, complete.InputJson);
            Assert.Equal(before.InputHash, complete.InputHash);
        }
        Assert.Equal(phase == ReplyProcessingStage.Fast && boundary is "after-effect" or "before-checkpoint" ? "2" : "1", await AnalysisCounterAsync(ticket.RequestId, false));
        Assert.Equal(phase == ReplyProcessingStage.Deep && boundary is "after-effect" or "before-checkpoint" ? "2" : "1", await AnalysisCounterAsync(ticket.RequestId, true));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var source = Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
            Assert.Equal(raw.ExternalId, source.ExternalId);
            Assert.Equal(raw.Message(), source.Text);
            Assert.Equal(complete.EventId, source.Id);
            Assert.Equal(DraftStatus.Pending, source.DraftStatus);
            Assert.Equal("Спасибо, расскажу о решении на встрече.", source.Draft);
            var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
            Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
            Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
            Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
            Assert.Equal(ReplyProcessingMode.Incoming, complete.Mode);
            Assert.Single(await context.ReplyProcessingRequests.ToListAsync(Ct));
        }
    }

    private async Task<string?> AnalysisCounterAsync(Guid id, bool deep)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = ReplyProbeSteps.CounterKey(id, deep);
        return await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct);
    }

    private async Task KillReplyProcessAsync(Guid id, ReplyProcessingStage phase, string boundary)
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
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"), "--reply-consumer", id.ToString("N"), phase.ToString(), boundary }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("isolated incoming reply process did not start");
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
                Assert.NotEqual(0, process.ExitCode);
                return;
            }
            Assert.Fail("incoming reply process exited before its boundary: " + await errors);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
