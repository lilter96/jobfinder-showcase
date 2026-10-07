using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Data;
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
using Npgsql;
using Wolverine;

namespace JobFinder.MessagingProbe;

public static class PostProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!Guid.TryParseExact(args[1], "N", out var id) || !Enum.TryParse<PostProcessingStage>(args[2], out var phase)) return 2;
        var connectionString = Environment.GetEnvironmentVariable("JOBFINDER_DISPOSABLE_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) return 2;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "postgres" || connection.Port is 5432 or 5433) return 2;
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.ClearProviders();
            var root = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-post-probe", id.ToString("N"));
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString, ["Paths:Prompts"] = Path.Combine(AppContext.BaseDirectory, "prompts"),
                ["Paths:Data"] = Path.Combine(root, "data"), ["Paths:Profile"] = Path.Combine(root, "profile"), ["Llm:ApiKey"] = "synthetic-probe-key",
            });
            builder.Services.AddJobFinderCore(builder.Configuration);
            builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
            builder.Services.AddDbContextFactory<JobFinderDbContext>(options =>
            {
                JobFinderDbContext.Configure(options, connectionString);
                options.AddInterceptors(new Checkpoint(id, phase, args[3]));
            });
            builder.Services.RemoveAll<IPostProcessingSteps>();
            builder.Services.AddSingleton<IPostProcessingSteps>(provider => new PostProbeSteps(provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(), id, phase, args[3]));
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
            builder.Services.RemoveAll<ICommentDeliveryTransport>();
            builder.Services.AddSingleton<ICommentDeliveryTransport>(provider => new CommentDeliveryProbeTransport(provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>()));
            builder.AddJobFinderMessaging(true);
            builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
            builder.Services.AddJobFinderInitialization();
            using var host = builder.Build();
            await using (var context = await host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync())
            {
                var request = await context.PostProcessingRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id);
                if (request is null || !await context.RawPosts.AnyAsync(row => row.Id == request.PostId && row.ChatTitle == "Synthetic process probe")) return 2;
            }
            await host.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }

    private sealed class Checkpoint(Guid id, PostProcessingStage phase, string boundary) : DbTransactionInterceptor
    {
        private int _armed = 1;
        private Task PauseAsync(DbContext context, CancellationToken ct) =>
            context.ChangeTracker.Entries<PostProcessingRequest>().Any(entry => entry.Entity.Id == id
                && entry.Entity.Stage == (phase == PostProcessingStage.Extracting ? PostProcessingStage.Verifying : phase))
                && Interlocked.Exchange(ref _armed, 0) == 1 ? CommentDeliveryProbeTransport.PauseAsync(ct) : Task.CompletedTask;
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            if (boundary == "before-checkpoint") await PauseAsync(eventData.Context!, ct);
            return result;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) =>
            boundary == "after-checkpoint" ? PauseAsync(eventData.Context!, ct) : Task.CompletedTask;
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());
        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("post probe refuses external HTTP");
        }
    }
}

public sealed class PostProbeSteps(IDbContextFactory<JobFinderDbContext> db, Guid id, PostProcessingStage? phase = null, string? boundary = null) : IPostProcessingSteps
{
    public static string CounterKey(Guid id, PostProcessingStage stage) => "probe.post." + id.ToString("N") + "." + stage;
    public void EnsureCompatible(PostProcessingInput input)
    {
        if (input.Operation.OperationId != id || input.Source.ChatTitle != "Synthetic process probe") throw new UnsupportedPostProcessingException();
    }
    private async Task CountAsync(PostProcessingStage stage, CancellationToken ct)
    {
        if (phase == stage && boundary == "before-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(id, stage);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now())
            ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()
            """, ct);
        if (phase == stage && boundary == "after-effect") await CommentDeliveryProbeTransport.PauseAsync(ct);
    }
    public async Task<PostReading> ReadAsync(PostProcessingInput input, CancellationToken ct)
    {
        await CountAsync(PostProcessingStage.Reading, ct);
        return new() { Post = PostWitness.Capture(input.Source), Description = input.Source.Text };
    }
    public async Task<PostExtractionCheckpoint> ExtractAsync(PostProcessingInput input, PostReading reading, CancellationToken ct)
    {
        await CountAsync(PostProcessingStage.Extracting, ct);
        return new() { Extraction = new() { IsVacancy = true, IsDotnet = true, Title = "Senior .NET Developer", Company = "Synthetic" } };
    }
    public async Task<PostVerification> VerifyAsync(PostProcessingInput input, PostReading reading, PostExtractionCheckpoint extraction, CancellationToken ct)
    {
        await CountAsync(PostProcessingStage.Verifying, ct);
        return new() { Contacts = [new() { Kind = ContactKind.Email, Value = "jobs@synthetic.invalid" }], Url = "https://synthetic.invalid/job" };
    }
}
