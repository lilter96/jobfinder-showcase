using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace JobFinder.Tests.Pipeline;

public sealed class WolverineIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using var provisioner = await StartAsync(true, provision: true);
        await provisioner.StopAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IHost> StartAsync(bool consumer, bool provision = false)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddSingleton(pg.Db);
        builder.Services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContext());
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        builder.UseWolverine(options =>
        {
            var transport = options.UsePostgresqlPersistenceAndTransport(context.Database.GetConnectionString()!, "spike_messages", "spike_queues");
            if (provision) transport.AutoProvision();
            options.UseEntityFrameworkCoreTransactions();
            options.AutoBuildMessageStorageOnStartup = provision ? AutoCreate.CreateOrUpdate : AutoCreate.None;
            options.Discovery.DisableConventionalDiscovery();
            options.RegisterMessageType(typeof(SpikePreparation));
            options.RegisterMessageType(typeof(SpikePrepared));
            options.UseSystemTextJsonForSerialization(serializer => serializer.TypeInfoResolverChain.Insert(0, SpikeMessageJsonContext.Default));
            options.PublishMessage<SpikePreparation>().ToPostgresqlQueue("prepare");
            options.PublishMessage<SpikePrepared>().ToPostgresqlQueue("prepared");
            options.Durability.DurabilityAgentEnabled = consumer;
            options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(100);
            options.Durability.OrphanedMessageSweepPollingTime = TimeSpan.FromMilliseconds(200);
            options.Durability.NodeReassignmentPollingTime = TimeSpan.FromMilliseconds(200);
            options.Durability.HealthCheckPollingTime = TimeSpan.FromMilliseconds(200);
            options.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(1);
            if (consumer)
            {
                options.Discovery.IncludeType(typeof(SpikePreparationHandler));
                options.Discovery.IncludeType(typeof(SpikePreparedHandler));
                options.ListenToPostgresqlQueue("prepare").PollingInterval(TimeSpan.FromMilliseconds(100)).Sequential();
                options.ListenToPostgresqlQueue("prepared").PollingInterval(TimeSpan.FromMilliseconds(100)).Sequential();
            }
        });
        var host = builder.Build();
        try
        {
            await host.StartAsync(Ct);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private async Task PublishAsync(IHost host, string operation, bool commit = true, string? requestKey = null,
        DbContextOptions<JobFinderDbContext>? options = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = options is null ? await pg.Db.CreateDbContextAsync(Ct) : new JobFinderDbContext(options);
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        context.Documents.Add(new AppDocument
        {
            Key = $"spike.request.{requestKey ?? operation}", Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await outbox.PublishAsync(new SpikePreparation { Operation = operation });
        if (commit) await outbox.SaveChangesAndFlushMessagesAsync(Ct);
    }

    private async Task<bool> ExistsAsync(string key)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Documents.AnyAsync(document => document.Key == key, Ct);
    }

    private async Task WaitForAsync(string key)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        while (!await ExistsAsync(key)) await Task.Delay(100, timeout.Token);
    }

    [Fact]
    public async Task Producer_exits_without_handling_and_committed_work_survives_until_server_starts()
    {
        var operation = Guid.NewGuid().ToString("N");
        using (var producer = await StartAsync(false))
        {
            await PublishAsync(producer, operation);
            await producer.StopAsync(Ct);
        }
        Assert.True(await ExistsAsync($"spike.request.{operation}"));
        Assert.False(await ExistsAsync($"spike.result.{operation}"));
        using var consumer = await StartAsync(true);
        await WaitForAsync($"spike.result.{operation}");
        Assert.True(await ExistsAsync($"spike.prepared.{operation}"));
        await consumer.StopAsync(Ct);
    }

    [Fact]
    public async Task Abandoned_context_does_not_persist_either_state_or_command()
    {
        var operation = Guid.NewGuid().ToString("N");
        using (var producer = await StartAsync(false))
        {
            await PublishAsync(producer, operation, commit: false);
            await producer.StopAsync(Ct);
        }
        using var consumer = await StartAsync(true);
        await Task.Delay(500, Ct);
        Assert.False(await ExistsAsync($"spike.request.{operation}"));
        Assert.False(await ExistsAsync($"spike.result.{operation}"));
        await consumer.StopAsync(Ct);
    }

    [Fact]
    public async Task Failed_state_commit_does_not_release_its_command()
    {
        var operation = Guid.NewGuid().ToString("N");
        var failedOperation = Guid.NewGuid().ToString("N");
        using (var producer = await StartAsync(false))
        {
            await PublishAsync(producer, operation);
            await Assert.ThrowsAsync<DbUpdateException>(() => PublishAsync(producer, failedOperation, requestKey: operation));
            await producer.StopAsync(Ct);
        }
        using var consumer = await StartAsync(true);
        await WaitForAsync($"spike.result.{operation}");
        await Task.Delay(500, Ct);
        Assert.False(await ExistsAsync($"spike.prepared.{failedOperation}"));
        Assert.False(await ExistsAsync($"spike.result.{failedOperation}"));
        await consumer.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, (await context.Documents.SingleAsync(document => document.Key == $"spike.result.{operation}", Ct)).Version);
    }

    [Fact]
    public async Task Different_envelopes_for_one_business_operation_have_one_stage_result()
    {
        var operation = Guid.NewGuid().ToString("N");
        using (var producer = await StartAsync(false))
        {
            await PublishAsync(producer, operation);
            await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new SpikePreparation { Operation = operation });
            await producer.StopAsync(Ct);
        }
        using var consumer = await StartAsync(true);
        await WaitForAsync($"spike.result.{operation}");
        await Task.Delay(500, Ct);
        await consumer.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, (await context.Documents.SingleAsync(document => document.Key == $"spike.result.{operation}", Ct)).Version);
    }

    [Fact]
    public async Task Failure_after_transaction_commit_before_flush_recovers_saved_work()
    {
        var operation = Guid.NewGuid().ToString("N");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(new FailAfterCommit());
        using (var producer = await StartAsync(false))
        {
            await Assert.ThrowsAsync<SimulatedProcessExit>(() => PublishAsync(producer, operation, options: options.Options));
            await producer.StopAsync(Ct);
        }
        Assert.True(await ExistsAsync($"spike.request.{operation}"));
        Assert.False(await ExistsAsync($"spike.prepared.{operation}"));
        using var consumer = await StartAsync(true);
        await WaitForAsync($"spike.result.{operation}");
        await consumer.StopAsync(Ct);
    }

    private sealed class SimulatedProcessExit : Exception;

    [Theory]
    [InlineData("before-commit", false)]
    [InlineData("after-commit", true)]
    [InlineData("complete", true)]
    [InlineData("after-handler-commit", true)]
    public async Task Actual_producer_process_exit_preserves_only_committed_commands(string mode, bool committed)
    {
        var operation = Guid.NewGuid().ToString("N");
        if (mode == "after-handler-commit")
        {
            using var producer = await StartAsync(false);
            await PublishAsync(producer, operation);
            await producer.StopAsync(Ct);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.MessagingProbe.dll"));
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(mode);
        start.Environment["JOBFINDER_DISPOSABLE_TEST_DATABASE"] = context.Database.GetConnectionString();
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.True(ready == "READY", ready ?? await process.StandardError.ReadToEndAsync(timeout.Token));
            if (mode != "complete") process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            if (mode == "complete") Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        Assert.Equal(committed, await ExistsAsync($"spike.request.{operation}"));
        Assert.Equal(mode == "after-handler-commit", await ExistsAsync($"spike.prepared.{operation}"));
        using var consumer = await StartAsync(true);
        if (committed) await WaitForAsync($"spike.result.{operation}");
        else
        {
            await Task.Delay(500, Ct);
            Assert.False(await ExistsAsync($"spike.result.{operation}"));
        }
        if (committed)
        {
            Assert.Equal(1, (await context.Documents.SingleAsync(document => document.Key == $"spike.result.{operation}", Ct)).Version);
        }
        await consumer.StopAsync(Ct);
    }

    private sealed class FailAfterCommit : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => throw new SimulatedProcessExit();
    }

    [Fact]
    public void Older_message_keeps_defaults_and_stable_identity()
    {
        var message = JsonSerializer.Deserialize("{\"operation\":\"old\"}", SpikeMessageJsonContext.Default.SpikePreparation)!;
        Assert.Equal(1, message.Version);
        Assert.Equal("old", message.Operation);
        Assert.Equal("jobfinder.spike.prepare.v1", typeof(SpikePreparation).GetCustomAttributes(typeof(MessageIdentityAttribute), false)
            .Cast<MessageIdentityAttribute>().Single().Alias);
    }
}

[MessageIdentity("jobfinder.spike.prepare.v1")]
public sealed record SpikePreparation
{
    public string Operation { get; set; } = "";
    public int Version { get; set; } = 1;
}

[MessageIdentity("jobfinder.spike.prepared.v1")]
public sealed record SpikePrepared
{
    public string Operation { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SpikePreparation))]
[JsonSerializable(typeof(SpikePrepared))]
internal partial class SpikeMessageJsonContext : JsonSerializerContext;

[NonTransactional]
public static class SpikePreparationHandler
{
    public static async Task Handle(SpikePreparation command, IDbContextOutbox outbox, IDbContextFactory<JobFinderDbContext> db,
        CancellationToken ct)
    {
        if (command.Version != 1) throw new InvalidOperationException("Unsupported message version");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var key = $"spike.prepared.{command.Operation}";
        const string empty = "{}";
        var inserted = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_documents (key, content, version, updated_at) VALUES ({key}, CAST({empty} AS json), 1, now())
            ON CONFLICT (key) DO NOTHING
            """, ct);
        if (inserted == 0) return;
        outbox.Enroll(context);
        await outbox.PublishAsync(new SpikePrepared { Operation = command.Operation });
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }
}

[NonTransactional]
public static class SpikePreparedHandler
{
    public static async Task Handle(SpikePrepared command, IDbContextFactory<JobFinderDbContext> db, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var key = $"spike.result.{command.Operation}";
        const string empty = "{}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_documents (key, content, version, updated_at) VALUES ({key}, CAST({empty} AS json), 1, now())
            ON CONFLICT (key) DO UPDATE SET version = app_documents.version + 1
            """, ct);
    }
}
