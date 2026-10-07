using System.Diagnostics;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed partial class BotControlTests
{
    [Theory]
    [InlineData("before-claim-commit")]
    [InlineData("after-claim-commit")]
    [InlineData("before-effect")]
    [InlineData("after-effect")]
    [InlineData("before-completion-commit")]
    [InlineData("after-completion-commit")]
    public async Task Actual_control_process_termination_preserves_claim_effect_and_completion_boundaries(string boundary)
    {
        await Controls.AdmitAsync(123456, 777, null, [new() { UpdateId = 10, PayloadJson = BotControlProbe.Payload }], Ct);
        var original = await RootAsync(10);
        await StopProducerAsync();
        await KillBotControlProcessAsync(original.Id, "--bot-control-consumer", boundary);
        var wasClaimed = boundary != "before-claim-commit";
        var complete = boundary == "after-completion-commit";
        var interrupted = await RootAsync(10);
        Assert.Equal(complete ? BotControlStage.Completed : wasClaimed ? BotControlStage.Executing : BotControlStage.Pending, interrupted.Stage);
        using var recovered = await RecoveryHostAsync(original.Id);
        await recovered.StartAsync(Ct);
        try
        {
            var expected = wasClaimed && !complete ? BotControlStage.OutcomeUnknown : BotControlStage.Completed;
            await UntilControlAsync(async () => (await RootAsync(10)).Stage == expected);
            var current = await RootAsync(10);
            Assert.Equal(original.InputJson, current.InputJson);
            Assert.Equal(original.InputHash, current.InputHash);
            Assert.Equal(11, await recovered.Services.GetRequiredService<BotControls>().OffsetAsync(123456, Ct));
            Assert.Equal(boundary is "after-claim-commit" or "before-effect" ? null : "1", await CounterAsync(original.Id));
            await recovered.Services.GetRequiredService<BotControlWorkflow>().RunAsync(new() { RequestId = current.Id }, Guid.NewGuid(), Ct);
            Assert.Equal(boundary is "after-claim-commit" or "before-effect" ? null : "1", await CounterAsync(original.Id));
            if (wasClaimed) Assert.Equal(interrupted.ClaimEnvelopeId, current.ClaimEnvelopeId);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
            Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        }
        finally { await recovered.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct); }
    }

    [Theory]
    [InlineData("before-admission-commit")]
    [InlineData("after-admission-commit")]
    public async Task Actual_producer_termination_keeps_account_cursor_and_saved_control_admission_atomic(string boundary)
    {
        await StopProducerAsync();
        await KillBotControlProcessAsync(Guid.NewGuid(), "--bot-control-producer", boundary);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = await context.BotControlRequests.AsNoTracking().SingleOrDefaultAsync(Ct);
        var cursor = await context.Settings.AsNoTracking().SingleOrDefaultAsync(row => row.Key == "bot.poll.123456", Ct);
        if (boundary == "before-admission-commit")
        {
            Assert.Null(root);
            Assert.Null(cursor);
            Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM jobfinder_queues.wolverine_queue_bot_controls").SingleAsync(Ct));
            Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type = 'jobfinder.controls.process-bot.v1'").SingleAsync(Ct));
        }
        else
        {
            Assert.NotNull(root);
            Assert.NotNull(cursor);
            Assert.Equal(BotControlStage.Pending, root.Stage);
            using var recovered = await RecoveryHostAsync(root.Id);
            await recovered.StartAsync(Ct);
            try
            {
                await UntilControlAsync(async () => (await RootAsync(10)).Stage == BotControlStage.Completed);
                Assert.Equal(11, await recovered.Services.GetRequiredService<BotControls>().OffsetAsync(123456, Ct));
                Assert.Equal("1", await CounterAsync(root.Id));
            }
            finally { await recovered.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct); }
        }
    }

    private async Task StopProducerAsync()
    {
        await _host!.StopAsync(Ct);
        _host.Dispose();
        _host = null;
    }

    private Task<IHost> RecoveryHostAsync(Guid id) => PostProcessingTestHost.BuildAsync(pg, true, fastRecovery: true, configure: services =>
    {
        services.RemoveAll<IBotControlExecutor>();
        services.AddSingleton<IBotControlExecutor>(new BotControlProbeExecutor(pg.Db, id));
    });

    private async Task<string?> CounterAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = BotControlProbeExecutor.CounterKey(id);
        return await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct);
    }

    private static async Task UntilControlAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition() && DateTimeOffset.UtcNow < until) await Task.Delay(100, Ct);
        Assert.True(await condition(), "saved control did not reach its expected recovery state");
    }

    private async Task KillBotControlProcessAsync(Guid id, string command, string boundary)
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
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"), command, id.ToString("N"), boundary }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("isolated bot control process did not start");
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
            Assert.Fail("bot control process exited before its boundary: " + await errors);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
