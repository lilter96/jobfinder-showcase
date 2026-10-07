using System.Diagnostics;
using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.Runtime;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableInterviewPreparationTests
{
    [Theory]
    [InlineData(InterviewPreparationStage.Planning, "before-effect")]
    [InlineData(InterviewPreparationStage.Planning, "after-effect")]
    [InlineData(InterviewPreparationStage.Planning, "before-checkpoint")]
    [InlineData(InterviewPreparationStage.Planning, "after-checkpoint")]
    [InlineData(InterviewPreparationStage.Answering, "before-effect")]
    [InlineData(InterviewPreparationStage.Answering, "after-effect")]
    [InlineData(InterviewPreparationStage.Answering, "before-checkpoint")]
    [InlineData(InterviewPreparationStage.Answering, "after-checkpoint")]
    [InlineData(InterviewPreparationStage.Printing, "before-effect")]
    [InlineData(InterviewPreparationStage.Printing, "after-effect")]
    [InlineData(InterviewPreparationStage.Printing, "before-checkpoint")]
    [InlineData(InterviewPreparationStage.Printing, "after-checkpoint")]
    [InlineData(InterviewPreparationStage.Completed, "before-checkpoint")]
    [InlineData(InterviewPreparationStage.Completed, "after-checkpoint")]
    public async Task Actual_interview_process_termination_recovers_only_uncommitted_stages_with_a_live_producer_leader_and_publishes_one_pack(
        InterviewPreparationStage phase, string boundary)
    {
        var producer = await HostAsync(false, new Steps());
        var source = await InterviewAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.CompanyName, InterviewProbe.Marker), Ct);
        var id = await RequestAsync(producer, source.Id);
        await producer.StopAsync(Ct);
        await using var publication = await pg.PublicationScopeAsync();
        var coordinator = (WolverineRuntime)publication.ServiceProvider.GetRequiredService<IWolverineRuntime>();
        await UntilAsync(() => Task.FromResult(coordinator.IsLeader()));
        Assert.False(coordinator.Options.Durability.DurabilityAgentEnabled);
        await KillInterviewProcessAsync(id, phase, boundary);
        var next = phase switch
        {
            InterviewPreparationStage.Planning => InterviewPreparationStage.Answering,
            InterviewPreparationStage.Answering => InterviewPreparationStage.Printing,
            InterviewPreparationStage.Printing => InterviewPreparationStage.Publishing,
            _ => InterviewPreparationStage.Completed,
        };
        var committed = boundary == "after-checkpoint";
        var published = phase == InterviewPreparationStage.Completed && committed;
        var before = await RootAsync(id);
        Assert.Equal(committed ? next : phase == InterviewPreparationStage.Completed ? InterviewPreparationStage.Publishing : phase, before.Stage);
        Assert.Equal(committed, before.ClaimEnvelopeId is null);
        var savedSource = await context.Interviews.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(published, savedSource.PackJson is not null);
        Assert.Equal(published ? 1 : 0, await context.Notifications.CountAsync(row => row.AttachmentPath != null, Ct));
        var paths = new PathsOptions { Data = InterviewProbe.DataPath(id) };
        var recovered = await PostProcessingTestHost.BuildAsync(pg, true, fastRecovery: true, configure: services =>
        {
            services.AddSingleton(paths);
            services.RemoveAll<IInterviewPreparationSteps>();
            services.AddSingleton<IInterviewPreparationSteps>(new InterviewProbeSteps(pg.Db, paths, id));
        });
        _hosts.Add(recovered);
        await recovered.StartAsync(Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Completed);
        await recovered.Services.GetRequiredService<InterviewPreparationRequests>().WaitAsync(id, Ct);
        foreach (var stage in new[] { InterviewPreparationStage.Planning, InterviewPreparationStage.Answering, InterviewPreparationStage.Printing })
        {
            var key = InterviewProbeSteps.CounterKey(id, stage);
            var count = await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleAsync(Ct);
            Assert.Equal(stage == phase && boundary is "after-effect" or "before-checkpoint" ? "2" : "1", count);
        }
        var root = await RootAsync(id);
        Assert.Equal(before.InputHash, root.InputHash);
        if (phase is InterviewPreparationStage.Answering or InterviewPreparationStage.Printing or InterviewPreparationStage.Completed || committed)
            Assert.Equal(before.PlanHash, root.PlanHash);
        if (phase is InterviewPreparationStage.Printing or InterviewPreparationStage.Completed || phase == InterviewPreparationStage.Answering && committed)
            Assert.Equal(before.CopilotHash, root.CopilotHash);
        Assert.Null(root.ClaimEnvelopeId);
        Assert.Equal(0, root.Failures);
        Assert.Single(await context.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.AttachmentPath != null, Ct));
        Assert.Equal(root.PdfPath, (await context.Interviews.AsNoTracking().SingleAsync(Ct)).PackPdfPath);
        Assert.True(coordinator.IsLeader());
        Assert.DoesNotContain(coordinator.AllRunningAgentUris(), uri => uri.Scheme == "wolverinedb");
    }

    private async Task KillInterviewProcessAsync(Guid id, InterviewPreparationStage phase, string boundary)
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
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"), "--interview-consumer",
            id.ToString("N"), phase.ToString(), boundary }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("isolated interview consumer did not start");
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
            Assert.Fail("interview consumer exited before its boundary: " + await errors);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
