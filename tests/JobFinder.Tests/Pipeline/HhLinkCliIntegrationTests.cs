using System.Diagnostics;
using System.Text;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed class HhLinkCliIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _home = Path.Combine(Path.GetTempPath(), "jobfinder-hh-cli-" + Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        Directory.CreateDirectory(Path.Combine(_home, "config"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(_home, "config", "appsettings.json"));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.HhLinkRequests.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_discovery; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes", Ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
        return ValueTask.CompletedTask;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(params string[] arguments)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_HOME"] = _home;
        start.Environment["JOBFINDER_Database__ConnectionString"] = context.Database.GetConnectionString();
        start.Environment["JOBFINDER_Paths__Prompts"] = Fixture.PromptsDir;
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.deps.json"));
        start.ArgumentList.Add(typeof(CliHost).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the isolated CLI");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(45), Ct);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    [Fact]
    public async Task Real_cli_can_publish_and_exit_while_another_process_owns_the_server_lock()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using var owner = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()) { Pooling = false }.ConnectionString);
        await owner.OpenAsync(Ct);
        await using (var claim = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(@key)", owner))
        {
            claim.Parameters.AddWithValue("key", 0x4A6F6246696E64L);
            await claim.ExecuteNonQueryAsync(Ct);
        }
        var first = await RunAsync("--add-vacancy", "https://hh.ru/vacancy/901101", "@alpha_hr");
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Contains("901101", first.Output);
        var second = await RunAsync("--add-vacancy", "https://rabota.by/vacancy/901101", "@beta_hr");
        Assert.True(second.ExitCode == 0, second.Output);
        var request = await context.HhLinkRequests.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(901101L, request.HhId);
        Assert.Equal(["@alpha_hr", "@beta_hr"], request.Contacts);
        Assert.Equal(HhLinkStage.Reading, request.Stage);
        Assert.Equal(0, request.Failures);
        Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_discovery").SingleAsync(Ct));
        Assert.Empty(await context.Vacancies.ToListAsync(Ct));
        Assert.Empty(await context.Applications.ToListAsync(Ct));
        Assert.Empty(await context.TelegramSessions.ToListAsync(Ct));
        await using var check = new Npgsql.NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid())", owner);
        Assert.Equal(true, await check.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Real_cli_rejects_invalid_input_without_creating_work()
    {
        var result = await RunAsync("--add-vacancy", "not-a-vacancy");
        Assert.Equal(2, result.ExitCode);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.HhLinkRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_discovery").SingleAsync(Ct));
    }
}
