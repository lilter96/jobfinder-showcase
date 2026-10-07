using JobFinder.Core.Domain;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurablePostProcessingTests
{
    [Fact]
    public async Task Source_changed_during_model_outage_cancels_instead_of_stranding_the_retry_claim()
    {
        var steps = new Steps { HoldExtraction = true, Unavailable = true };
        var host = await HostAsync(true, steps);
        var post = await PostAsync();
        var id = await RequestAsync(host, post);
        await steps.Entered.Task.WaitAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.RawPosts.Where(row => row.Id == post.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Text, "Changed source"), Ct);
        steps.Release.TrySetResult();
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Cancelled);
        var root = await RootAsync(id);
        Assert.Null(root.ClaimEnvelopeId);
        Assert.Null(root.RetryAt);
        Assert.Equal(0, root.Failures);
        Assert.Null((await context.RawPosts.AsNoTracking().SingleAsync(Ct)).ProcessingRequestId);
        Assert.Empty(await context.Vacancies.ToListAsync(Ct));
    }

    [Fact]
    public async Task Unsupported_late_command_cannot_retire_a_newer_committed_stage()
    {
        var steps = new Steps { VerifyUnavailable = true };
        var host = await HostAsync(true, steps);
        var id = await RequestAsync(host, await PostAsync());
        await UntilAsync(async () => (await RootAsync(id)).RetryAt is not null);
        var root = await RootAsync(id);
        Assert.Equal(PostProcessingStage.Verifying, root.Stage);
        await Assert.ThrowsAsync<UnsupportedPostProcessingException>(() => host.Services.GetRequiredService<PostProcessingWorkflow>().RunAsync(
            new ProcessPost { RequestId = id, Stage = PostProcessingStage.Reading, Version = 2 }, Guid.NewGuid(), Ct));
        Assert.Equal(root.Stage, (await RootAsync(id)).Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        steps.VerifyUnavailable = false;
        await WakeAsync(host, id);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        Assert.Equal(1, steps.Extractions);
    }

    [Fact]
    public async Task Importer_quarantines_a_dangling_owner_once_and_processes_the_next_post()
    {
        var steps = new Steps();
        var host = await HostAsync(true, steps);
        var orphan = await PostAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.RawPosts.Where(row => row.Id == orphan.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.ProcessingRequestId, Guid.NewGuid()), Ct);
        var good = await PostAsync();
        using var worker = new RawPostWorker(pg.Db, host.Services.GetRequiredService<PostProcessingRequests>(), host.Services.GetRequiredService<JobTriggers>(),
            TimeProvider.System, NullLogger<RawPostWorker>.Instance);
        await worker.StartAsync(Ct);
        try
        {
            await UntilAsync(async () => await context.RawPosts.AnyAsync(row => row.Id == good.Id && row.Status == RawPostStatus.Vacancy, Ct));
            Assert.Equal(RawPostStatus.Failed, (await context.RawPosts.AsNoTracking().SingleAsync(row => row.Id == orphan.Id, Ct)).Status);
            Assert.Single(await context.Notifications.Where(row => row.Text.Contains("owner is missing")).ToListAsync(Ct));
            Assert.Null(await host.Services.GetRequiredService<PostProcessingRequests>().RequestAsync(orphan.Id, Ct));
            Assert.Single(await context.PostProcessingRequests.ToListAsync(Ct));
            Assert.Equal(1, steps.Extractions);
        }
        finally { await worker.StopAsync(Ct); }
    }
}
