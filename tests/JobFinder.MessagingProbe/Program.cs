using System.Text.Json.Serialization;
using JasperFx;
using JobFinder.Core.Domain;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace JobFinder.MessagingProbe;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--resume-consumer", _, _, _]) return await ResumeProbe.RunAsync(args);
        if (args is ["--letter-review-consumer", _, _]) return await LetterReviewProbe.RunAsync(args);
        if (args is ["--application-consumer", _, _]) return await ApplicationDeliveryProbe.RunAsync(args);
        if (args is ["--comment-consumer", _, _, _]) return await CommentProbe.RunAsync(args);
        if (args is ["--comment-source-consumer", _, _, _]) return await CommentProbe.RunAsync(args);
        if (args is ["--comment-delivery-consumer", _, _, _]) return await CommentProbe.RunAsync(args);
        if (args is ["--post-consumer", _, _, _]) return await PostProbe.RunAsync(args);
        if (args is ["--channel-membership-consumer", _, _]) return await ChannelMembershipProbe.RunAsync(args);
        if (args is ["--interview-consumer", _, _, _]) return await InterviewProbe.RunAsync(args);
        if (args is ["--notification-consumer", _, _]) return await NotificationProbe.RunAsync(args);
        if (args is ["--bot-control-consumer", _, _] or ["--bot-control-producer", _, _]) return await BotControlProbe.RunAsync(args);
        if (args is ["--reply-consumer", _, _, _]) return await ReplyProbe.RunAsync(args);
        if (args is ["--storage-script"])
        {
            Console.Write(await StorageScript.ExportAsync());
            return 0;
        }

        if (args.Length != 2 || !Guid.TryParseExact(args[0], "N", out _) || args[1] is not ("before-commit" or "after-commit" or "complete" or "after-handler-commit"))
            return 2;
        var connectionString = Environment.GetEnvironmentVariable("JOBFINDER_DISPOSABLE_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) return 2;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "postgres" || connection.Port is 5432 or 5433)
            return 2;
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options => JobFinderDbContext.Configure(options, connectionString));
            var consumer = args[1] == "after-handler-commit";
            builder.UseWolverine(options =>
            {
                options.UsePostgresqlPersistenceAndTransport(connectionString, "spike_messages", "spike_queues");
                options.UseEntityFrameworkCoreTransactions();
                options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                options.Discovery.DisableConventionalDiscovery();
                options.RegisterMessageType(typeof(ProbePreparation));
                options.RegisterMessageType(typeof(ProbePrepared));
                options.UseSystemTextJsonForSerialization(serializer => serializer.TypeInfoResolverChain.Insert(0, ProbeJsonContext.Default));
                options.PublishMessage<ProbePreparation>().ToPostgresqlQueue("prepare");
                options.PublishMessage<ProbePrepared>().ToPostgresqlQueue("prepared");
                options.Durability.DurabilityAgentEnabled = consumer;
                if (consumer)
                {
                    options.Discovery.IncludeType(typeof(ProbePreparationHandler));
                    options.ListenToPostgresqlQueue("prepare").PollingInterval(TimeSpan.FromMilliseconds(100)).Sequential();
                }
            });
            using var host = builder.Build();
            await host.StartAsync();
            if (consumer)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            }
            await using var scope = host.Services.CreateAsyncScope();
            var contextOptions = new DbContextOptionsBuilder<JobFinderDbContext>();
            JobFinderDbContext.Configure(contextOptions, connectionString);
            await using var context = new JobFinderDbContext(contextOptions.Options);
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            context.Documents.Add(new AppDocument
            {
                Key = $"spike.request.{args[0]}", Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await outbox.PublishAsync(new ProbePreparation { Operation = args[0] });
            if (args[1] == "complete")
            {
                await outbox.SaveChangesAndFlushMessagesAsync();
                await host.StopAsync();
                Console.WriteLine("READY");
                return 0;
            }
            if (args[1] == "after-commit")
            {
                await context.SaveChangesAsync();
                await context.Database.CommitTransactionAsync();
            }
            Console.WriteLine("READY");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString().Replace(connectionString, "<test database>").Replace(connection.Password ?? "<unset>", "<redacted>"));
            return 1;
        }
    }
}

[MessageIdentity("jobfinder.spike.prepare.v1")]
public sealed record ProbePreparation
{
    public string Operation { get; set; } = "";
    public int Version { get; set; } = 1;
}

[MessageIdentity("jobfinder.spike.prepared.v1")]
public sealed record ProbePrepared
{
    public string Operation { get; set; } = "";
}

[NonTransactional]
public static class ProbePreparationHandler
{
    public static async Task Handle(ProbePreparation command, IDbContextFactory<JobFinderDbContext> db, IDbContextOutbox outbox,
        CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        context.Documents.Add(new AppDocument
        {
            Key = $"spike.prepared.{command.Operation}", Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow,
        });
        outbox.Enroll(context);
        await outbox.PublishAsync(new ProbePrepared { Operation = command.Operation });
        await context.SaveChangesAsync(ct);
        await context.Database.CommitTransactionAsync(ct);
        Console.WriteLine("READY");
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProbePreparation))]
[JsonSerializable(typeof(ProbePrepared))]
internal partial class ProbeJsonContext : JsonSerializerContext;
