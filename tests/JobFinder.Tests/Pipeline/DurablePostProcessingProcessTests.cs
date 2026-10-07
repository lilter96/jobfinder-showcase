using System.Diagnostics;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurablePostProcessingTests
{
    [Theory]
    [InlineData(PostProcessingStage.Extracting, "before-effect")]
    [InlineData(PostProcessingStage.Extracting, "after-effect")]
    [InlineData(PostProcessingStage.Extracting, "before-checkpoint")]
    [InlineData(PostProcessingStage.Extracting, "after-checkpoint")]
    [InlineData(PostProcessingStage.Completed, "before-checkpoint")]
    [InlineData(PostProcessingStage.Completed, "after-checkpoint")]
    public async Task Actual_process_termination_reuses_committed_paid_output_and_preserves_atomic_publication(PostProcessingStage phase, string boundary)
    {
        var producer = await HostAsync(false, new());
        var post = await PostAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.RawPosts.Where(row => row.Id == post.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.ChatTitle, "Synthetic process probe"), Ct);
        var id = await RequestAsync(producer, post);
        await producer.StopAsync(Ct);
        await KillPostProcessAsync(id, phase, boundary);
        var committedExtraction = phase == PostProcessingStage.Completed || boundary == "after-checkpoint";
        var committedPublication = phase == PostProcessingStage.Completed && boundary == "after-checkpoint";
        var before = await RootAsync(id);
        Assert.Equal(committedPublication ? PostProcessingStage.Completed : phase == PostProcessingStage.Completed ? PostProcessingStage.Publishing
            : committedExtraction ? PostProcessingStage.Verifying : PostProcessingStage.Extracting, before.Stage);
        Assert.Equal(committedPublication ? 1 : 0, await context.Vacancies.CountAsync(Ct));
        var steps = new PostProbeSteps(pg.Db, id);
        var recovered = await PostProcessingTestHost.BuildAsync(pg, true, steps, fastRecovery: true);
        _hosts.Add(recovered);
        await recovered.StartAsync(Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        Assert.Equal("1", await CounterAsync(PostProcessingStage.Reading));
        Assert.Equal(!committedExtraction && boundary != "before-effect" ? "2" : "1", await CounterAsync(PostProcessingStage.Extracting));
        Assert.Equal("1", await CounterAsync(PostProcessingStage.Verifying));
        Assert.Single(await context.Vacancies.ToListAsync(Ct));
        Assert.Equal(1, (await context.CompanyContacts.SingleAsync(Ct)).SeenCount);
        Assert.Equal(RawPostStatus.Vacancy, (await context.RawPosts.SingleAsync(Ct)).Status);
        async Task<string?> CounterAsync(PostProcessingStage stage)
        {
            var key = PostProbeSteps.CounterKey(id, stage);
            return await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct);
        }
    }

    private async Task KillPostProcessAsync(Guid id, PostProcessingStage phase, string boundary)
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
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"), "--post-consumer", id.ToString("N"), phase.ToString(), boundary })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("post probe did not start");
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                if (line == "READY") { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(Ct); return; }
            Assert.Fail("post probe exited before its checkpoint: " + await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
