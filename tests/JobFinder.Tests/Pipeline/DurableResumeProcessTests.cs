using System.Diagnostics;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableResumeIntegrationTests
{
    [Theory]
    [InlineData(ResumePreparationStage.Printing, "before-effect")]
    [InlineData(ResumePreparationStage.Printing, "after-effect")]
    [InlineData(ResumePreparationStage.Printing, "after-checkpoint")]
    [InlineData(ResumePreparationStage.Reviewing, "before-effect")]
    [InlineData(ResumePreparationStage.Reviewing, "after-effect")]
    [InlineData(ResumePreparationStage.Reviewing, "after-checkpoint")]
    [InlineData(ResumePreparationStage.Finalizing, "before-effect")]
    [InlineData(ResumePreparationStage.Finalizing, "after-effect")]
    [InlineData(ResumePreparationStage.Finalizing, "after-checkpoint")]
    public async Task Actual_process_termination_recovers_only_uncommitted_resume_work(ResumePreparationStage phase, string boundary)
    {
        Guid id;
        using (var producer = await HostAsync(false, new Steps()))
        {
            await producer.StartAsync(Ct);
            id = await producer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(producer), Ct);
            await producer.StopAsync(Ct);
        }
        await KillResumeConsumerAsync(id, phase, boundary);
        var before = await RequestAsync(id);
        if (boundary == "after-checkpoint")
        {
            Assert.NotEqual(phase, before.Stage);
            Assert.Null(before.ClaimEnvelopeId);
        }
        else
        {
            Assert.Equal(phase, before.Stage);
            Assert.NotNull(before.ClaimEnvelopeId);
        }
        Assert.Equal(1, await PhaseCallsAsync(id, ResumePreparationStage.Drafting));
        Assert.Equal(boundary == "before-effect" ? 0 : 1, await PhaseCallsAsync(id, phase));
        if (phase == ResumePreparationStage.Finalizing && boundary == "after-checkpoint")
        {
            Assert.Equal(ResumePreparationStage.Completed, before.Stage);
            Assert.NotNull(before.ResultId);
            Assert.Equal(1, await NoticesAsync());
        }
        else
        {
            Assert.Null(before.ResultId);
            Assert.Equal(0, await NoticesAsync());
        }
        using var recovered = await HostAsync(true, new ResumeProbeSteps(pg.Db, id), fastRecovery: true);
        await recovered.StartAsync(Ct);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(1), recovered.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>().Options.Durability.StaleNodeTimeout);
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            await UntilAsync(async () =>
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Incoming'").SingleAsync(Ct) == 0
                    && await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_outgoing_envelopes").SingleAsync(Ct) == 0;
            });
            Assert.Equal(1, await PhaseCallsAsync(id, ResumePreparationStage.Drafting));
            foreach (var stage in new[] { ResumePreparationStage.Printing, ResumePreparationStage.Reviewing, ResumePreparationStage.Finalizing })
                Assert.Equal(stage == phase && boundary == "after-effect" ? 2 : 1, await PhaseCallsAsync(id, stage));
            Assert.Equal(1, await NoticesAsync());
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Single(await context.TailoredResumes.ToListAsync(Ct));
            Assert.Null((await RequestAsync(id)).ClaimEnvelopeId);
        }
        finally { await recovered.StopAsync(Ct); }
    }

    private async Task<int> PhaseCallsAsync(Guid id, ResumePreparationStage stage)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = ResumeProbeSteps.CounterKey(id, stage);
        var value = await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct);
        return value is null ? 0 : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task KillResumeConsumerAsync(Guid id, ResumePreparationStage phase, string boundary)
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
        start.Environment["JOBFINDER_DISPOSABLE_TEST_ARTIFACTS"] = _artifacts;
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"));
        start.ArgumentList.Add("--resume-consumer");
        start.ArgumentList.Add(id.ToString("N"));
        start.ArgumentList.Add(phase.ToString());
        start.ArgumentList.Add(boundary);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start isolated resume consumer");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
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
