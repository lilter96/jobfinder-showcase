using System.Diagnostics;
using System.Text;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class ChannelMembershipTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("account")]
    [InlineData("ownership")]
    public async Task Changes_inside_gateway_before_wire_prevent_the_effect(string change)
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _gateway.BeforeEffect = async () =>
        {
            if (change == "source")
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                await context.TelegramChannels.ExecuteUpdateAsync(update => update.SetProperty(row => row.DecisionRevision, row => row.DecisionRevision + 1), Ct);
            }
            else if (change == "account") _gateway.AccountId = 99;
            else _host!.Services.GetRequiredService<JobFinder.Pipeline.ServerOwnership>().Lost();
        };
        if (change == "source") await Assert.ThrowsAsync<JobFinder.Pipeline.Modules.Discovery.UnsupportedChannelMembershipException>(() => RunAsync(id));
        else if (change == "ownership") await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(id));
        else await RunAsync(id);
        Assert.Equal(0, _gateway.Effects);
        Assert.NotEqual(ChannelMembershipStage.Completed, (await ReadAsync(id)).Stage);
    }

    [Fact]
    public async Task Actual_reconciliation_cli_requires_exact_checked_evidence_and_never_resumes_unknown_work()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _gateway.Error = new IOException("synthetic lost wire response");
        await RunAsync(id);
        var unknown = await ReadAsync(id);
        var home = Path.Combine(Path.GetTempPath(), "jobfinder-membership-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(home, "config", "appsettings.json"));
        try
        {
            var invalid = await ReconciliationCliAsync(home, id, Guid.NewGuid(), "synthetic checked evidence");
            Assert.Equal(3, invalid.Code);
            Assert.Equal(ChannelMembershipStage.OutcomeUnknown, (await ReadAsync(id)).Stage);
            var accepted = await ReconciliationCliAsync(home, id, unknown.AttemptId!.Value, "synthetic owner verified membership");
            Assert.True(accepted.Code == 0, accepted.Output);
            Assert.Equal(ChannelMembershipStage.Reconciled, (await ReadAsync(id)).Stage);
            Assert.Equal(1, _gateway.Effects);
            await Workflow.RunAsync(new JobFinder.Pipeline.Modules.Discovery.ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Joining }, Guid.NewGuid(), Ct);
            Assert.Equal(1, _gateway.Effects);
        }
        finally { Directory.Delete(home, true); }
    }

    private async Task<(int Code, string Output)> ReconciliationCliAsync(string home, Guid id, Guid attempt, string evidence)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = home, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_HOME"] = home;
        start.Environment["JOBFINDER_Database__ConnectionString"] = context.Database.GetConnectionString();
        start.Environment["JOBFINDER_Paths__Prompts"] = Fixture.PromptsDir;
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.runtimeconfig.json"),
            "--depsfile", Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.deps.json"), typeof(CliHost).Assembly.Location,
            "--channel-membership-reconcile", id.ToString("D"), "--attempt", attempt.ToString("D"), "--applied", "--evidence", evidence })
            start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
        try { await child.WaitForExitAsync(timeout.Token); return (child.ExitCode, await stdout + await stderr); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); } }
    }
}
