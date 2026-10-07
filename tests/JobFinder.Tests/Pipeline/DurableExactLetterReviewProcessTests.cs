using System.Diagnostics;
using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Career;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableLetterPreparationIntegrationTests
{
    [Theory]
    [InlineData("before-effect")]
    [InlineData("after-effect")]
    [InlineData("after-checkpoint")]
    public async Task Actual_process_termination_preserves_exact_text_and_reuses_only_committed_review(string boundary)
    {
        var producer = await HostAsync(false, new Steps());
        var ticket = await ExactAsync(producer, await SeedAsync());
        var id = ticket.RequestId!.Value;
        await StopAsync(producer);
        await KillExactReviewConsumerAsync(id, boundary);
        var before = await RowAsync(id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var lane = await context.LetterPreparationLanes.SingleAsync(row => row.RequestId == id, Ct);
            Assert.Equal(boundary == "after-checkpoint" ? LetterPreparationStage.Completed : LetterPreparationStage.Active, before.Stage);
            Assert.Equal(boundary == "after-checkpoint" ? LetterLaneStage.Completed : LetterLaneStage.Reviewing, lane.Stage);
            if (boundary == "after-checkpoint") Assert.Null(lane.ClaimEnvelopeId);
            else Assert.NotNull(lane.ClaimEnvelopeId);
            Assert.Equal(ExactText, JobFinder.Pipeline.Modules.Hiring.LetterPreparationRequests.ReadCandidate(lane.DraftJson).Text);
        }
        Assert.Equal(boundary == "before-effect" ? 0 : 1, await ReviewProcessCallsAsync(id));
        var recovered = await HostAsync(true, new Steps(), durableSteps: new LetterReviewProbeSteps(pg.Db, id), fastRecovery: true);
        var result = await CompletedAsync(id);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Incoming'").SingleAsync(Ct) == 0
                && await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_outgoing_envelopes").SingleAsync(Ct) == 0;
        });
        Assert.Equal(ExactText, result.Text);
        Assert.Equal(boundary == "after-effect" ? 2 : 1, await ReviewProcessCallsAsync(id));
        var review = await recovered.Services.GetRequiredService<LetterWriter>().GetReviewAsync(id, Ct);
        Assert.Equal(10, review!.Score);
        Assert.Equal(Fingerprint.Of(ExactText), review.TextHash);
        await using var saved = await pg.Db.CreateDbContextAsync(Ct);
        var completedLane = Assert.Single(await saved.LetterPreparationLanes.Where(row => row.RequestId == id).ToListAsync(Ct));
        Assert.Null(completedLane.ClaimEnvelopeId);
        Assert.Empty(await saved.DeliveryAttempts.ToListAsync(Ct));
    }

    private async Task<int> ReviewProcessCallsAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = LetterReviewProbeSteps.CounterKey(id);
        var value = await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct);
        return value is null ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private async Task KillExactReviewConsumerAsync(Guid id, string boundary)
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
        start.ArgumentList.Add("--letter-review-consumer");
        start.ArgumentList.Add(id.ToString("N"));
        start.ArgumentList.Add(boundary);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start isolated exact-review consumer");
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
