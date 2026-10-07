using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class MigrationOwnershipIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.NotificationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Notifications.ExecuteDeleteAsync(Ct);
        await context.Documents.ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes", Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Migration_and_recovery_require_ownership_before_any_database_context_is_created()
    {
        var factory = new TrackingFactory();
        var ownership = new ServerOwnership();
        using var host = new HostBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.MigrateAsync(factory, NullLogger.Instance, ownership, Ct));
        var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, ownership, host.Services.GetRequiredService<IHostApplicationLifetime>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.StartAsync(Ct));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task Ownership_lost_while_creating_the_migration_context_prevents_schema_writes()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var migrations = read.Database.GetMigrations().ToArray();
        await read.GetService<IMigrator>().MigrateAsync(migrations[^2], Ct);
        var ownership = new ServerOwnership();
        ownership.Acquired();
        var factory = new TrackingFactory(pg.Db, ownership.Lost);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.MigrateAsync(factory, NullLogger.Instance, ownership, Ct));
        Assert.Equal(1, factory.Calls);
        Assert.DoesNotContain(migrations[^1], await read.Database.GetAppliedMigrationsAsync(Ct));
        await read.Database.MigrateAsync(Ct);
    }

    [Fact]
    public async Task Host_shutdown_cancels_initialization_even_when_the_startup_token_was_not_cancelled()
    {
        using var host = new HostBuilder().Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();
        var ownership = new ServerOwnership();
        ownership.Acquired();
        var factory = new TrackingFactory();
        var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance, ownership, lifetime);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializer.StartAsync(Ct));
        Assert.True(factory.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task The_actual_migration_cli_refuses_a_live_owner_then_migrates_and_releases_its_own_lock_after_shutdown()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var migrations = read.Database.GetMigrations().ToArray();
        var application = new Application
        {
            Vacancy = new Vacancy { Title = "Synthetic migration witness", Fingerprint = Guid.NewGuid().ToString("N"), Url = "https://example.invalid/1" },
            Status = ApplicationStatus.Sending, Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.Approved, CreatedAt = DateTimeOffset.UtcNow,
        };
        read.Applications.Add(application);
        await read.SaveChangesAsync(Ct);
        await read.GetService<IMigrator>().MigrateAsync(migrations[^2], Ct);
        IHost Owner() => new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(new DatabaseOptions { ConnectionString = read.Database.GetConnectionString()! });
            services.AddSingleton<ServerOwnership>();
            services.AddSingleton(TimeProvider.System);
            services.AddHostedService<SingleInstanceGuard>();
        }).Build();
        using (var owner = Owner())
        {
            await owner.StartAsync(Ct);
            try
            {
                var refused = await MigrationCliAsync();
                Assert.Equal(1, refused.Code);
                Assert.Contains("another JobFinder server", refused.Output);
                Assert.DoesNotContain(migrations[^1], await read.Database.GetAppliedMigrationsAsync(Ct));
                Assert.Equal(ApplicationStatus.Sending, await read.Applications.Where(row => row.Id == application.Id).Select(row => row.Status).SingleAsync(Ct));
                Assert.True(owner.Services.GetRequiredService<ServerOwnership>().IsOwned);
            }
            finally { await owner.StopAsync(Ct); }
        }
        var completed = await MigrationCliAsync();
        Assert.True(completed.Code == 0, completed.Output);
        Assert.Contains(migrations[^1], await read.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Equal(ApplicationStatus.Sending, await read.Applications.Where(row => row.Id == application.Id).Select(row => row.Status).SingleAsync(Ct));
        using var next = Owner();
        await next.StartAsync(Ct);
        Assert.True(next.Services.GetRequiredService<ServerOwnership>().IsOwned);
        await next.StopAsync(Ct);
    }

    [Fact]
    public async Task Actual_exclusive_migration_imports_candidate_files_with_native_admissions_and_no_consumer_or_duplicate_publication()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var homes = new List<string>();
        void Prepare(string home)
        {
            homes.Add(home);
            var directory = Path.Combine(home, "profile");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "profile.json"), ProfileProvider.ToJson(new CandidateProfile
            {
                Name = "Synthetic migration candidate", Title = "Backend Developer", Reviewed = true,
            }));
            File.WriteAllText(Path.Combine(directory, "facts.json"), JsonSerializer.Serialize(new CandidateFacts
            {
                Facts = [new() { Key = "location", Question = "Location?", Answer = "Minsk", Confirmed = true }],
            }, FactsJsonContext.Default.CandidateFacts));
        }
        var first = await MigrationCliAsync(Prepare);
        Assert.True(first.Code == 0, first.Output);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        foreach (var key in new[] { ProfileProvider.Key, FactsProvider.Key })
        {
            Assert.Equal(1, (await context.Documents.SingleAsync(row => row.Key == key, Ct)).Version);
            Assert.Equal("file-import", (await context.CandidateRevisions.SingleAsync(row => row.Key == key, Ct)).Actor);
        }
        var notices = await context.Notifications.Where(row => row.Text.StartsWith("👤 Данные кандидата обновлены:")).ToListAsync(Ct);
        Assert.Equal(2, notices.Count);
        var ids = notices.Select(row => row.DeliveryRequestId!.Value).ToArray();
        var roots = await context.NotificationDeliveryRequests.Where(row => ids.Contains(row.Id)).ToListAsync(Ct);
        Assert.Equal(2, roots.Count);
        Assert.All(roots, row => Assert.Equal(NotificationDeliveryStage.AwaitingTarget, row.Stage));
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => ids.Contains(row.RequestId)).ToListAsync(Ct));
        var second = await MigrationCliAsync(Prepare);
        Assert.True(second.Code == 0, second.Output);
        Assert.Equal(2, await context.CandidateRevisions.CountAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(row => row.Text.StartsWith("👤 Данные кандидата обновлены:"), Ct));
        Assert.Equal(2, await context.NotificationDeliveryRequests.CountAsync(row => ids.Contains(row.Id), Ct));
        foreach (var home in homes)
            foreach (var file in new[] { "profile.json", "facts.json" })
            {
                var path = Path.Combine(home, "profile", file);
                Assert.False(File.Exists(path));
                Assert.True(File.Exists(path + FileStateImport.ImportedSuffix));
            }
    }

    private async Task<(int Code, string Output)> MigrationCliAsync(Action<string>? prepare = null)
    {
        var home = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-migration-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        prepare?.Invoke(home);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(home, "config", "appsettings.json"));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_HOME"] = home;
        start.Environment["JOBFINDER_Database__ConnectionString"] = context.Database.GetConnectionString();
        start.Environment["JOBFINDER_Paths__Prompts"] = Fixture.PromptsDir;
        start.Environment["JOBFINDER_Paths__Data"] = Path.Combine(home, "data");
        start.Environment["JOBFINDER_Paths__Profile"] = Path.Combine(home, "profile");
        start.ArgumentList.Add(typeof(CliHost).Assembly.Location);
        start.ArgumentList.Add("--migrate");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start isolated migration CLI");
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(45), Ct);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private sealed class TrackingFactory(IDbContextFactory<JobFinderDbContext>? inner = null, Action? onCreate = null) : IDbContextFactory<JobFinderDbContext>
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public JobFinderDbContext CreateDbContext()
        {
            Calls++;
            onCreate?.Invoke();
            return inner?.CreateDbContext() ?? throw new InvalidOperationException("unexpected database access");
        }

        public Task<JobFinderDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            Token = ct;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
