using JasperFx;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Messaging;
using JobFinder.Data;
using JobFinder.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;

namespace JobFinder.Tests.Pipeline;

public sealed class WolverineStorageIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IHost> BuildAsync(bool consumer, bool allQueues = false)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(pg.Db);
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        builder.UseWolverine(options =>
        {
            options.UsePostgresqlPersistenceAndTransport(context.Database.GetConnectionString()!, WolverineStorage.MessageSchema, WolverineStorage.QueueSchema);
            options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            options.UseEntityFrameworkCoreTransactions();
            options.Discovery.DisableConventionalDiscovery();
            options.RegisterMessageType(typeof(SpikePreparation));
            options.RegisterMessageType(typeof(SpikePrepared));
            options.RegisterMessageType(typeof(StorageRetryProbe));
            options.UseSystemTextJsonForSerialization(serializer => serializer.TypeInfoResolverChain.Insert(0, StorageRetryJsonContext.Default));
            options.PublishMessage<SpikePreparation>().ToPostgresqlQueue(DurableQueues.HiringPreparation);
            options.PublishMessage<SpikePrepared>().ToPostgresqlQueue(DurableQueues.Preparation);
            options.PublishMessage<StorageRetryProbe>().ToPostgresqlQueue(DurableQueues.HiringPreparation);
            options.Durability.DurabilityAgentEnabled = consumer;
            options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(100);
            options.Durability.OrphanedMessageSweepPollingTime = TimeSpan.FromMilliseconds(200);
            options.Durability.NodeReassignmentPollingTime = TimeSpan.FromMilliseconds(200);
            options.Durability.HealthCheckPollingTime = TimeSpan.FromMilliseconds(200);
            options.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(1);
            options.Policies.OnException<StorageRetryException>().ScheduleRetry(Enumerable.Repeat(TimeSpan.FromSeconds(2), 5).ToArray());
            if (allQueues)
            {
                foreach (var queue in DurableQueues.All) options.ListenToPostgresqlQueue(queue);
            }
            if (consumer)
            {
                options.Discovery.IncludeType(typeof(SpikePreparationHandler));
                options.Discovery.IncludeType(typeof(SpikePreparedHandler));
                options.Discovery.IncludeType(typeof(StorageRetryProbeHandler));
                options.ListenToPostgresqlQueue(DurableQueues.HiringPreparation).PollingInterval(TimeSpan.FromMilliseconds(100)).Sequential();
                options.ListenToPostgresqlQueue(DurableQueues.Preparation).PollingInterval(TimeSpan.FromMilliseconds(100)).Sequential();
            }
        });
        return builder.Build();
    }

    [Fact]
    public async Task Checked_in_EF_migration_matches_the_pinned_native_storage_and_all_queues()
    {
        using var host = await BuildAsync(false, allQueues: true);
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        await transport.ConfigureAsync(runtime);
        await ((IDatabase)runtime.Storage).AssertDatabaseMatchesConfigurationAsync(Ct);
        Assert.Equal(AutoCreate.None, runtime.Options.AutoBuildMessageStorageOnStartup);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var tables = await context.Database.SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = {WolverineStorage.QueueSchema}").ToListAsync(Ct);
        Assert.Equal(DurableQueues.All.Count * 2, tables.Count);
        foreach (var queue in DurableQueues.All)
        {
            Assert.Contains("wolverine_queue_" + queue, tables);
            Assert.Contains("wolverine_queue_" + queue + "_scheduled", tables);
        }
    }

    [Fact]
    public async Task Migrated_storage_supports_producer_exit_and_a_later_consumer_without_auto_provision()
    {
        var operation = Guid.NewGuid().ToString("N");
        using (var producer = await BuildAsync(false))
        {
            await producer.StartAsync(Ct);
            await using var scope = producer.Services.CreateAsyncScope();
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            context.Documents.Add(new AppDocument { Key = "spike.request." + operation, Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow });
            await outbox.PublishAsync(new SpikePreparation { Operation = operation });
            await outbox.SaveChangesAndFlushMessagesAsync(Ct);
            await producer.StopAsync(Ct);
        }
        using var consumer = await BuildAsync(true);
        await consumer.StartAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var context = await pg.Db.CreateDbContextAsync(timeout.Token);
            if (await context.Documents.AnyAsync(document => document.Key == "spike.result." + operation, timeout.Token)) break;
            await Task.Delay(100, timeout.Token);
        }
        await consumer.StopAsync(Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, (await read.Documents.SingleAsync(document => document.Key == "spike.result." + operation, Ct)).Version);
    }

    [Fact]
    public async Task A_normal_runtime_detects_storage_drift_without_repairing_or_dropping_it()
    {
        using var host = await BuildAsync(false, allQueues: true);
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        await runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().ConfigureAsync(runtime);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DROP INDEX jobfinder_queues.idx_wolverine_queue_scoring_timestamp", Ct);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ((IDatabase)runtime.Storage).AssertDatabaseMatchesConfigurationAsync(Ct));
            Assert.False(await context.Database.SqlQuery<bool>($"SELECT to_regclass('jobfinder_queues.idx_wolverine_queue_scoring_timestamp') IS NOT NULL AS \"Value\"").SingleAsync(Ct));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("CREATE INDEX idx_wolverine_queue_scoring_timestamp ON jobfinder_queues.wolverine_queue_scoring USING btree (timestamp)", Ct);
        }
    }

    [Fact]
    public void Storage_roster_is_immutable_and_the_script_is_not_destructive()
    {
        Assert.Throws<NotSupportedException>(() => ((IList<string>)DurableQueues.All).Clear());
        Assert.DoesNotContain("DROP ", WolverineStorage.CreateSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", WolverineStorage.CreateSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", WolverineStorage.CreateSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("6.41.0", WolverineStorage.PackageVersion);
    }

    [Fact]
    public async Task A_scheduled_retry_does_not_block_a_healthy_operation_behind_it()
    {
        var failing = Guid.NewGuid().ToString("N");
        var healthy = Guid.NewGuid().ToString("N");
        using (var producer = await BuildAsync(false))
        {
            await producer.StartAsync(Ct);
            await PublishRetryAsync(producer, failing, failures: 2);
            await PublishRetryAsync(producer, healthy, failures: 0);
            await producer.StopAsync(Ct);
        }
        using var consumer = await BuildAsync(true);
        await consumer.StartAsync(Ct);
        await WaitForDocumentAsync("spike.retry-result." + healthy);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.False(await context.Documents.AnyAsync(document => document.Key == "spike.retry-result." + failing, Ct));
        }
        await WaitForDocumentAsync("spike.retry-result." + failing);
        await consumer.StopAsync(Ct);
    }

    [Fact]
    public async Task A_pending_message_refuses_rollback_before_any_storage_is_dropped()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var messageId = Guid.NewGuid();
        const string messageType = "synthetic.pending.message";
        var body = new byte[] { 1, 2, 3 };
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO jobfinder_messages.wolverine_outgoing_envelopes (id, owner_id, destination, body, message_type)
            VALUES ({messageId}, 0, 'postgresql://hiring_prepare/', {body}, {messageType})
            """, Ct);
        try
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930003413_GeneratedAnswerRecipe", Ct));
            Assert.Equal("P0001", error.SqlState);
            Assert.Contains("rollback refused", error.MessageText);
            Assert.True(await context.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE id = {messageId}) AS \"Value\"").SingleAsync(Ct));
            Assert.True(await context.Database.SqlQuery<bool>($"SELECT to_regclass('jobfinder_queues.wolverine_queue_discovery') IS NOT NULL AS \"Value\"").SingleAsync(Ct));
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE id = {messageId}", Ct);
        }
    }

    [Fact]
    public async Task Rollback_waits_for_a_concurrent_producer_and_retains_its_committed_message()
    {
        var disposable = new PostgresFixture();
        await disposable.InitializeAsync();
        try
        {
            Assert.SkipWhen(disposable.SkipReason is not null, disposable.SkipReason ?? "");
            await using var producer = await disposable.Db.CreateDbContextAsync(Ct);
            await using var transaction = await producer.Database.BeginTransactionAsync(Ct);
            var messageId = Guid.NewGuid();
            var body = new byte[] { 1, 2, 3 };
            await producer.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO jobfinder_messages.wolverine_outgoing_envelopes (id, owner_id, destination, body, message_type)
                VALUES ({messageId}, 0, 'postgresql://hiring_prepare/', {body}, 'synthetic.concurrent.message')
                """, Ct);
            await using var rollback = await disposable.Db.CreateDbContextAsync(Ct);
            await rollback.Database.OpenConnectionAsync(Ct);
            var rollbackPid = await rollback.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(Ct);
            var migration = rollback.GetService<IMigrator>().MigrateAsync("20260930003413_GeneratedAnswerRecipe", Ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!await producer.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid = {rollbackPid} AND mode = 'AccessExclusiveLock' AND NOT granted) AS \"Value\"").SingleAsync(timeout.Token))
                await Task.Delay(20, timeout.Token);
            await transaction.CommitAsync(Ct);
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migration);
            Assert.Equal("P0001", error.SqlState);
            Assert.True(await producer.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE id = {messageId}) AS \"Value\"").SingleAsync(Ct));
        }
        finally
        {
            await disposable.DisposeAsync();
        }
    }

    [Fact]
    public async Task Business_cancellation_before_a_scheduled_retry_prevents_the_stage_effect()
    {
        var operation = Guid.NewGuid().ToString("N");
        using (var producer = await BuildAsync(false))
        {
            await producer.StartAsync(Ct);
            await PublishRetryAsync(producer, operation, failures: 1);
            await producer.StopAsync(Ct);
        }
        using var consumer = await BuildAsync(true);
        await consumer.StartAsync(Ct);
        await WaitForDocumentAsync("spike.retry-attempt." + operation);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Documents.Add(new AppDocument { Key = "spike.retry-cancel." + operation, Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        await WaitForDocumentAsync("spike.retry-cancelled." + operation);
        await consumer.StopAsync(Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await read.Documents.AnyAsync(document => document.Key == "spike.retry-result." + operation, Ct));
        Assert.Equal(1, (await read.Documents.SingleAsync(document => document.Key == "spike.retry-attempt." + operation, Ct)).Version);
    }

    [Fact]
    public async Task A_scheduled_retry_survives_consumer_restart_on_migrated_storage()
    {
        var operation = Guid.NewGuid().ToString("N");
        using (var producer = await BuildAsync(false))
        {
            await producer.StartAsync(Ct);
            await PublishRetryAsync(producer, operation, failures: 1);
            await producer.StopAsync(Ct);
        }
        using (var first = await BuildAsync(true))
        {
            await first.StartAsync(Ct);
            await WaitForDocumentAsync("spike.retry-attempt." + operation);
            await first.StopAsync(Ct);
        }
        using var second = await BuildAsync(true);
        await second.StartAsync(Ct);
        await WaitForDocumentAsync("spike.retry-result." + operation);
        await second.StopAsync(Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, (await read.Documents.SingleAsync(document => document.Key == "spike.retry-attempt." + operation, Ct)).Version);
        Assert.Equal(1, (await read.Documents.SingleAsync(document => document.Key == "spike.retry-result." + operation, Ct)).Version);
    }

    private async Task PublishRetryAsync(IHost producer, string operation, int failures)
    {
        await using var scope = producer.Services.CreateAsyncScope();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        context.Documents.Add(new AppDocument { Key = "spike.retry-request." + operation, Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow });
        await outbox.PublishAsync(new StorageRetryProbe { Operation = operation, Failures = failures });
        await outbox.SaveChangesAndFlushMessagesAsync(Ct);
    }

    private async Task WaitForDocumentAsync(string key)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var context = await pg.Db.CreateDbContextAsync(timeout.Token);
            if (await context.Documents.AnyAsync(document => document.Key == key, timeout.Token)) return;
            await Task.Delay(50, timeout.Token);
        }
    }
}

[MessageIdentity("jobfinder.spike.retry.v1")]
public sealed record StorageRetryProbe
{
    public string Operation { get; set; } = "";
    public int Failures { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class StorageRetryException : Exception;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StorageRetryProbe))]
[JsonSerializable(typeof(SpikePreparation))]
[JsonSerializable(typeof(SpikePrepared))]
internal partial class StorageRetryJsonContext : JsonSerializerContext;

[NonTransactional]
public static class StorageRetryProbeHandler
{
    public static async Task Handle(StorageRetryProbe command, IDbContextFactory<JobFinderDbContext> db, CancellationToken ct)
    {
        if (command.Version != 1 || !Guid.TryParseExact(command.Operation, "N", out _)) throw new InvalidOperationException("invalid retry probe command");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({command.Operation}, 0))", ct);
        var cancelled = await context.Documents.AnyAsync(document => document.Key == "spike.retry-cancel." + command.Operation, ct);
        if (cancelled)
        {
            await AddOnceAsync(context, "spike.retry-cancelled." + command.Operation, ct);
            await transaction.CommitAsync(ct);
            return;
        }
        var attemptKey = "spike.retry-attempt." + command.Operation;
        const string empty = "{}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_documents (key, content, version, updated_at) VALUES ({attemptKey}, CAST({empty} AS json), 1, now())
            ON CONFLICT (key) DO UPDATE SET version = app_documents.version + 1
            """, ct);
        var attempts = await context.Documents.Where(document => document.Key == attemptKey).Select(document => document.Version).SingleAsync(ct);
        if (attempts <= command.Failures)
        {
            await transaction.CommitAsync(ct);
            throw new StorageRetryException();
        }
        await AddOnceAsync(context, "spike.retry-result." + command.Operation, ct);
        await transaction.CommitAsync(ct);
    }

    private static Task<int> AddOnceAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        const string empty = "{}";
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_documents (key, content, version, updated_at) VALUES ({key}, CAST({empty} AS json), 1, now())
            ON CONFLICT (key) DO NOTHING
            """, ct);
    }
}
