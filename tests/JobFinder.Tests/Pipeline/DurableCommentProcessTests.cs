using System.Diagnostics;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    [Theory]
    [InlineData(CommentPreparationStage.Drafting, "before-effect")]
    [InlineData(CommentPreparationStage.Drafting, "after-effect")]
    [InlineData(CommentPreparationStage.Drafting, "after-checkpoint")]
    [InlineData(CommentPreparationStage.Checking, "before-effect")]
    [InlineData(CommentPreparationStage.Checking, "after-effect")]
    [InlineData(CommentPreparationStage.Checking, "after-checkpoint")]
    public async Task Actual_process_termination_reuses_committed_comment_drafts_and_checks(CommentPreparationStage phase, string boundary)
    {
        var producer = await HostAsync(false);
        var id = await RequestAsync(producer);
        await producer.StopAsync(Ct);
        await KillCommentConsumerAsync(id, phase, boundary);
        var before = await ReadAsync(id);
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
        Assert.Equal(boundary == "before-effect" ? 0 : 1, await CallsAsync(id, phase));
        if (phase == CommentPreparationStage.Checking) Assert.Equal(1, await CallsAsync(id, CommentPreparationStage.Drafting));
        var recovered = await BuildAsync(true, steps: new CommentProbeSteps(pg.Db, id), fastRecovery: true);
        _hosts.Add(recovered);
        await recovered.StartAsync(Ct);
        var result = await recovered.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(id, Ct);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Incoming'").SingleAsync(Ct) == 0
                && await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_outgoing_envelopes").SingleAsync(Ct) == 0;
        });
        Assert.True(result.Plan.WorthCommenting);
        Assert.Equal(phase == CommentPreparationStage.Drafting && boundary == "after-effect" ? 2 : 1, await CallsAsync(id, CommentPreparationStage.Drafting));
        Assert.Equal(phase == CommentPreparationStage.Checking && boundary == "after-effect" ? 2 : 1, await CallsAsync(id, CommentPreparationStage.Checking));
        Assert.Null((await ReadAsync(id)).ClaimEnvelopeId);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.CommentDrafts.ToListAsync(Ct));
        Assert.Empty(await read.DeliveryAttempts.ToListAsync(Ct));
    }

    private async Task<int> CallsAsync(Guid id, CommentPreparationStage stage)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = CommentProbeSteps.CounterKey(id, stage);
        var value = await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct);
        return value is null ? 0 : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task KillCommentConsumerAsync(Guid id, CommentPreparationStage phase, string boundary) =>
        await KillCommentProcessAsync(id, "--comment-consumer", phase.ToString(), boundary);

    private async Task KillCommentProcessAsync(Guid id, string command, string phase, string boundary)
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
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"));
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(id.ToString("N"));
        start.ArgumentList.Add(phase);
        start.ArgumentList.Add(boundary);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("comment probe did not start");
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                if (line == "READY")
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(Ct);
                    return;
                }
            Assert.Fail("comment probe exited before its boundary: " + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
