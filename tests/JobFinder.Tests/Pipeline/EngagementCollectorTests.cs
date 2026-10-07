using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed partial class EngagementPolicyIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Threads_collection_pins_preferences_for_one_pass_and_adopts_the_next_publication(bool sameAuthor)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Llm:ApiKey"] = "synthetic-test-key", ["Llm:Endpoint"] = "https://provider.invalid/completions", ["Llm:Proxy"] = "",
            ["Threads:Mode"] = "Api", ["Threads:AccessToken"] = "synthetic-test-token", ["Threads:ApiBase"] = "https://threads.invalid",
            ["Paths:Data"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-collector-data", Guid.NewGuid().ToString("N")),
            ["Paths:Profile"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-collector-profile", Guid.NewGuid().ToString("N")),
        });
        builder.Services.AddJobFinderCore(builder.Configuration);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var provider = new CollectorProvider { SameAuthor = sameAuthor };
        builder.Services.AddSingleton<IHttpClientFactory>(new CollectorClientFactory(provider));
        builder.Services.AddSingleton(services => new ThreadsApiClient(new HttpClient(provider, disposeHandler: false), services.GetRequiredService<ThreadsOptions>()));
        builder.AddJobFinderMessaging(consumer: true);
        builder.Services.AddJobFinderInitialization();
        using var host = builder.Build();
        await host.StartAsync(Ct);
        var services = host.Services;
        var profiles = services.GetRequiredService<ProfileProvider>();
        await profiles.SaveAsync(new CandidateProfile { Name = "Synthetic Candidate", Title = "Backend Developer", Reviewed = true },
            (await profiles.GetSnapshotAsync(Ct))?.Version ?? 0, "collector-test", Ct);
        var search = services.GetRequiredService<SearchPolicyStore>();
        var selectors = await search.ReadAsync(Ct);
        selectors.Value.Threads.Enabled = true;
        selectors.Value.Threads.VacancyQueries = [];
        selectors.Value.Threads.AudienceQueries = ["synthetic-query"];
        selectors.Value.Threads.JitterMinutes = 0;
        await search.PublishAsync(selectors.Value, selectors.Version, "collector-test", Ct);
        var engagement = services.GetRequiredService<EngagementPolicyStore>();
        var initial = await engagement.ReadAsync(Ct);
        await engagement.PublishAsync(initial.Value with { AutoPublish = false, MaxDraftsPerDay = 1, AuthorCooldownDays = 0 }, initial.Version, "collector-test", Ct);
        provider.DuringFirstSearch = async () =>
        {
            var current = await engagement.ReadAsync(Ct);
            await engagement.PublishAsync(current.Value with { AutoPublish = true, MaxDraftsPerDay = 2, AuthorCooldownDays = 4 }, current.Version, "concurrent-edit", Ct);
        };
        var triggers = services.GetRequiredService<JobTriggers>();
        using var job = new ThreadsJob(search, engagement, services.GetRequiredService<ThreadsGateway>(), services.GetRequiredService<CommentSources>(),
            services.GetRequiredService<PostProcessingRequests>(), triggers, pg.Db,
            services.GetRequiredService<Notifier>(), time, NullLogger<ThreadsJob>.Instance);
        await job.StartAsync(Ct);
        try
        {
            await WaitForCollectorAsync(() => Task.FromResult(triggers.Names.Contains(JobNames.Threads)));
            triggers.Trigger(JobNames.Threads);
            await WaitForCollectorAsync(() => CompletedPassesAsync(1));
            await WaitForCollectorAsync(SourcesCompletedAsync);
            context.ChangeTracker.Clear();
            var pending = await context.CommentDrafts.SingleAsync(Ct);
            Assert.Equal("101", pending.PostId);
            Assert.Equal(CommentStatus.Pending, pending.Status);
            Assert.Null(pending.ChosenText);
            Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft).ToListAsync(Ct));
            triggers.Trigger(JobNames.Threads);
            await WaitForCollectorAsync(() => CompletedPassesAsync(2));
            await WaitForCollectorAsync(SourcesCompletedAsync);
            context.ChangeTracker.Clear();
            var drafts = await context.CommentDrafts.OrderBy(row => row.PostId).ToListAsync(Ct);
            Assert.Equal(sameAuthor ? 1 : 2, drafts.Count);
            Assert.Equal(CommentStatus.Pending, drafts[0].Status);
            if (!sameAuthor)
            {
                Assert.Equal("102", drafts[1].PostId);
                Assert.Equal(CommentStatus.Approved, drafts[1].Status);
                Assert.Equal(drafts[1].Variants[0], drafts[1].ChosenText);
            }
            Assert.Equal(2, provider.Searches);
            Assert.Equal(sameAuthor ? 1 : 2, provider.Plans);
        }
        finally { await job.StopAsync(Ct); await host.StopAsync(Ct); }

        async Task<bool> CompletedPassesAsync(int count)
        {
            await using var read = await pg.Db.CreateDbContextAsync(Ct);
            var failed = await read.JobRuns.Where(row => row.Job == JobNames.Threads && row.Status != JobRunStatus.Ok && row.FinishedAt != null).FirstOrDefaultAsync(Ct);
            Assert.True(failed is null, $"Threads pass failed after {provider.Searches} searches and {provider.Plans} plans: {failed?.Message}");
            return await read.JobRuns.CountAsync(row => row.Job == JobNames.Threads && row.Status == JobRunStatus.Ok, Ct) == count;
        }

        async Task<bool> SourcesCompletedAsync()
        {
            await using var read = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await read.CommentSourceRequests.Where(row => row.Stage == CommentSourceStage.Failed || row.Stage == CommentSourceStage.Cancelled).ToListAsync(Ct));
            return !await read.CommentSourceRequests.AnyAsync(row => row.Stage != CommentSourceStage.Completed, Ct);
        }
    }

    private static async Task WaitForCollectorAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Threads collector did not complete the expected pass");
            await Task.Delay(30, Ct);
        }
    }

    private sealed class CollectorClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CollectorProvider : HttpMessageHandler
    {
        public Func<Task>? DuringFirstSearch { get; set; }
        public bool SameAuthor { get; set; }
        public int Searches { get; private set; }
        public int Plans { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.Host == "threads.invalid" && request.RequestUri.AbsolutePath == "/keyword_search")
            {
                if (++Searches == 1) await DuringFirstSearch!();
                var posts = """{"data":[{"id":"101","username":"synthetic-first","text":"Сегодня обсуждаем, как сделать собеседование понятнее для кандидатов","permalink":"https://threads.invalid/@synthetic-first/post/101"},{"id":"102","username":"synthetic-second","text":"Вчера обсуждали, как избежать долгого ожидания ответа после собеседования","permalink":"https://threads.invalid/@synthetic-second/post/102"}]}""";
                return Json(SameAuthor ? posts.Replace("synthetic-second", "synthetic-first", StringComparison.Ordinal) : posts);
            }
            if (request.Method == HttpMethod.Post && request.RequestUri?.Host == "provider.invalid")
            {
                Plans++;
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                if (body["response_format"]?["json_schema"]?["name"] is { } schema)
                    Assert.Equal("comment_plan", schema.GetValue<string>());
                return Json(new JsonObject
                {
                    ["model"] = "synthetic-provider", ["choices"] = new JsonArray(new JsonObject
                    {
                        ["message"] = new JsonObject { ["content"] = """{"audience":"recruiter","worthCommenting":true,"reason":"Короткий вопрос о процессе","variants":["А какой срок ответа вы обычно называете кандидату?","Я бы заранее договаривался о дате следующего разговора."]}""" },
                        ["finish_reason"] = "stop",
                    }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 60, ["cost"] = 0.0001m },
                }.ToJsonString());
            }
            throw new InvalidOperationException("The synthetic collector must never publish a reply or contact an unexpected endpoint");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
