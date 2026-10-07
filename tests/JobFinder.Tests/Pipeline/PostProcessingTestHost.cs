using JobFinder.Data;
using JobFinder.Llm;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

internal static class PostProcessingTestHost
{
    internal static async Task<IHost> BuildAsync(PostgresFixture pg, bool consumer, IPostProcessingSteps? steps = null,
        IInterceptor? fault = null, Action<IServiceCollection>? configure = null, bool fastRecovery = false)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        await ConfigureAsync(builder, pg, consumer, steps, fault, configure, fastRecovery);
        return builder.Build();
    }

    internal static async Task ConfigureAsync(IHostApplicationBuilder builder, PostgresFixture pg, bool consumer,
        IPostProcessingSteps? steps = null, IInterceptor? fault = null,
        Action<IServiceCollection>? configure = null, bool fastRecovery = false)
    {
        await using var context = await pg.Db.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var root = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-posts-synthetic", Guid.NewGuid().ToString("N"));
        var configuration = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Paths:Data"] = Path.Combine(root, "data"), ["Paths:Profile"] = Path.Combine(root, "profile"),
            ["Llm:ApiKey"] = "synthetic-key", ["Llm:Endpoint"] = "https://provider.invalid/completions", ["Llm:Proxy"] = "",
        };
        foreach (var purpose in LlmPurpose.All)
        {
            configuration[$"Llm:Routes:{purpose}:0:Name"] = "synthetic/" + purpose.ToLowerInvariant();
            configuration[$"Llm:Routes:{purpose}:0:Reasoning:Effort"] = "max";
        }
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddJobFinderCore(builder.Configuration);
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        if (fault is null) builder.Services.AddSingleton(pg.Db);
        else
        {
            var options = new DbContextOptionsBuilder<JobFinderDbContext>();
            JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
            options.AddInterceptors(fault);
            builder.Services.AddSingleton<IDbContextFactory<JobFinderDbContext>>(new Factory(options.Options));
        }
        builder.Services.RemoveAll<IHttpClientFactory>();
        builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
        builder.Services.RemoveAll<ICommentDeliveryTransport>();
        builder.Services.AddSingleton<ICommentDeliveryTransport, DormantCommentTransport>();
        if (steps is not null)
        {
            builder.Services.RemoveAll<IPostProcessingSteps>();
            builder.Services.AddSingleton(steps);
        }
        configure?.Invoke(builder.Services);
        builder.AddJobFinderMessaging(consumer);
        if (fastRecovery) builder.Services.ConfigureWolverine(JobFinder.MessagingProbe.ResumeProbe.FastRecovery);
        if (consumer) builder.Services.AddJobFinderInitialization();
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
        public Task<JobFinderDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new JobFinderDbContext(options));
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoThreadsNetwork());
    }
}
