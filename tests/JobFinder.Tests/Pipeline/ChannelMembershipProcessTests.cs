using System.Diagnostics;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobFinder.Tests.Pipeline;

public sealed partial class ChannelMembershipTests
{
    [Theory]
    [InlineData("before-effect")]
    [InlineData("after-effect")]
    [InlineData("before-checkpoint")]
    [InlineData("after-checkpoint")]
    public async Task Actual_process_kill_preserves_unknown_effect_without_replay_and_recovers_committed_receipt(string boundary)
    {
        var id = await AdmitAsync();
        await _host!.StopAsync(Ct);
        await KillAsync(id, boundary);
        var committed = boundary == "after-checkpoint";
        var before = await ReadAsync(id);
        Assert.Equal(committed ? ChannelMembershipStage.Muting : ChannelMembershipStage.Joining, before.Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = ChannelMembershipProbeGateway.CounterKey(id);
        Assert.Equal(boundary == "before-effect" ? null : "1", await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct));
        _host.Dispose();
        _host = await PostProcessingTestHost.BuildAsync(pg, true, configure: services =>
        {
            services.RemoveAll<IChannelMembershipGateway>();
            services.AddSingleton<IChannelMembershipGateway>(new ChannelMembershipProbeGateway(pg.Db, id));
        }, fastRecovery: true);
        await _host.StartAsync(Ct);
        var target = committed ? ChannelMembershipStage.Completed : ChannelMembershipStage.OutcomeUnknown;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while ((await ReadAsync(id)).Stage != target)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "membership did not recover its saved stage");
            await Task.Delay(100, Ct);
        }
        Assert.Equal(boundary == "before-effect" ? null : "1", await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct));
        Assert.Equal(committed ? ChannelStatus.Joined : ChannelStatus.Approved, (await context.TelegramChannels.AsNoTracking().SingleAsync(Ct)).Status);
        var joining = await context.ChannelMembershipAttempts.AsNoTracking().SingleAsync(row => row.RequestId == id && row.Stage == ChannelMembershipStage.Joining, Ct);
        Assert.Equal(committed ? ChannelMembershipOutcome.Confirmed : ChannelMembershipOutcome.OutcomeUnknown, joining.Outcome);
    }

    private async Task KillAsync(Guid id, string boundary)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_DISPOSABLE_TEST_DATABASE"] = context.Database.GetConnectionString();
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"), "--channel-membership-consumer", id.ToString("N"), boundary })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("membership probe did not start");
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                if (line == "READY") { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(Ct); return; }
            Assert.Fail("membership probe exited before boundary: " + await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
