using System.Net;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;
using JobFinder.Integrations.Telegram;
using JobFinder.Integrations.Web;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace JobFinder.Tests.Pipeline;

/// <summary>
/// /reparse end to end on the database: a stored vacancy whose earlier readings went wrong is read again. Telegram is not connected (the
/// stored links serve) and no model answers (the reading is incomplete: it adds contacts, never removes them).
/// </summary>
public sealed class PostReparserTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MockHttpMessageHandler _web = new();
    private IHost? _host;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) { await _host.StopAsync(CancellationToken.None); _host.Dispose(); }
        _web.Dispose();
    }

    private sealed class NoUsage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;

        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    /// <summary>OpenRouter answering every call with one extraction (the model's view of the post).</summary>
    private sealed class ModelAnswer(string extractionJson) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new System.Text.Json.Nodes.JsonObject
                {
                    ["model"] = "m",
                    ["choices"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject { ["message"] = new System.Text.Json.Nodes.JsonObject { ["content"] = extractionJson }, ["finish_reason"] = "stop" },
                    },
                    ["usage"] = new System.Text.Json.Nodes.JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 50, ["cost"] = 0.0001 },
                }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static string Extraction(string? applyUrl, params string[] contacts) => System.Text.Json.JsonSerializer.Serialize(new
    {
        isVacancy = true, isDotnet = true, title = "Senior C#/.NET Developer", company = "Acme", city = (string?)null, country = (string?)null,
        workFormats = Array.Empty<string>(), salaryFrom = (decimal?)null, salaryTo = (decimal?)null, currency = (string?)null, salaryGross = (bool?)null,
        englishLevel = "unknown", seniority = "senior", skills = new[] { "C#" }, contacts, applyUrl,
    });

    private async Task<PostReparser> ReparserAsync(string? modelAnswer = null)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        _web.Fallback.Respond(HttpStatusCode.NotFound);
        var options = new EnrichmentOptions();
        var policy = new LinkPolicy(options);
        var http = new PoliteHttp(new HttpClient(_web), new ScrapeOptions { MinDelayMs = 0, MaxDelayMs = 1 }, TimeProvider.System);
        var crawler = new VacancyCrawler(http, new PageReader(), policy, options, NullLogger<VacancyCrawler>.Instance);
        var enricher = new PostEnricher(crawler, policy, options);
        var telegram = new TelegramUserClient(new TelegramOptions(), new NoTelegramSession(), new MemoryDocumentStore(), NullLogger<TelegramUserClient>.Instance);
        var verifier = new TelegramContactVerifier(pg.Db, telegram, TimeProvider.System, NullLogger<TelegramContactVerifier>.Instance);
        // No answer given: no API key, the model is unavailable.
        var llmOptions = modelAnswer is null
            ? new LlmOptions()
            : new LlmOptions { ApiKey = "k", Routes = new(StringComparer.OrdinalIgnoreCase) { [LlmPurpose.Extract] = [new LlmModelOptions { Name = "m" }] } };
        var llm = new JobFinderLlm(new LlmClient(new OpenRouterTransport(modelAnswer is null ? null! : new ModelAnswer(modelAnswer), llmOptions),
            new ModelHealth(TimeProvider.System), llmOptions, new NoUsage(), NullLogger<LlmClient>.Instance), new PromptCatalog(Fixture.PromptsDir));
        var steps = new PostProcessingSteps(llm, enricher, verifier, telegram, new HhClient(http), pg.Db, TimeProvider.System, NullLogger<PostProcessingSteps>.Instance);
        _host = await PostProcessingTestHost.BuildAsync(pg, true, steps, configure: services =>
        {
            services.RemoveAll<JobFinderLlm>();
            services.AddSingleton(llm);
            services.RemoveAll<LinkPolicy>();
            services.AddSingleton(policy);
        });
        await _host.StartAsync(Ct);
        return _host.Services.GetRequiredService<PostReparser>();
    }

    private async Task<long> SeedAsync(string url, string text, params PostLink[] links)
    {
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var externalId = Guid.NewGuid().ToString("N")[..10];
        var v = new Vacancy
        {
            Title = "Senior C#/.NET Developer", CompanyName = "Acme", Url = url, Fingerprint = Guid.NewGuid().ToString("N"), Description = text,
            Status = VacancyStatus.New, FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Sources = [new VacancySource { Source = SourceKind.Telegram, ExternalId = "1212014211/" + externalId, Url = "https://t.me/csharp_jobs/2514" }],
        };
        ctx.Vacancies.Add(v);
        await ctx.SaveChangesAsync(Ct);
        ctx.RawPosts.Add(new RawPost
        {
            Source = SourceKind.Telegram, ChatRef = "1212014211", ExternalId = externalId, Url = "https://t.me/csharp_jobs/2514", Text = text,
            Links = [.. links], Status = RawPostStatus.Vacancy, VacancyId = v.Id, FetchedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);
        return v.Id;
    }

    private async Task<Vacancy> LoadAsync(long id)
    {
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        return await ctx.Vacancies.AsNoTracking().SingleAsync(v => v.Id == id, Ct);
    }

    [Fact]
    public async Task A_product_page_an_earlier_reading_took_gives_way_to_the_hh_vacancy_the_post_names()
    {
        // Seen live on #57: "ASP.NET" auto-linked, read, redirected to Microsoft — that became the vacancy's address.
        var id = await SeedAsync("https://dotnet.microsoft.com/en-us/apps/aspnet", "Senior C#/.NET, ASP.NET Core. Подробнее о вакансии на hh.ru",
            new PostLink { Url = "https://ASP.NET" }, new PostLink { Url = "https://hh.ru/vacancy/135521230", Label = "Подробнее о вакансии на hh.ru" });

        var line = Assert.Single(await (await ReparserAsync()).ReparseAsync([id], 5, Ct));

        Assert.Equal(0, line.Pages);
        Assert.Equal("https://hh.ru/vacancy/135521230", (await LoadAsync(id)).Url);
    }

    [Fact]
    public async Task A_sites_root_an_earlier_reading_took_gives_way_to_the_vacancy_page_read_today()
    {
        // Seen live on #60: "https://talanto.work" as the address, the vacancy page linked right there.
        _web.When("https://talanto.work/jobs/2a3c9884").Respond("text/html",
            """<html><head><script type="application/ld+json">{"@type":"JobPosting","title":".NET / C# Developer"}</script></head><body><main><p>Вакансия .NET</p></main></body></html>""");
        var id = await SeedAsync("https://talanto.work", ".NET / C# Developer. Подробнее о вакансии на talanto.work",
            new PostLink { Url = "https://talanto.work/jobs/2a3c9884", Label = "Подробнее о вакансии на talanto.work" });

        await (await ReparserAsync()).ReparseAsync([id], 5, Ct);

        var v = await LoadAsync(id);
        Assert.Equal("https://talanto.work/jobs/2a3c9884", v.Url);
        Assert.Contains("Вакансия .NET", v.Description);
    }

    [Theory]
    [InlineData("https://dotnet.microsoft.com/en-us/apps/aspnet")] // the address an earlier reading left: a product page
    [InlineData("https://ASP.NET")] // the auto-linked tech word itself
    [InlineData(null)]
    public async Task With_the_model_answering_its_address_is_taken_only_when_it_is_a_vacancy_page(string? modelUrl)
    {
        var id = await SeedAsync("https://dotnet.microsoft.com/en-us/apps/aspnet", "Senior C#/.NET, ASP.NET Core. Подробнее о вакансии на hh.ru",
            new PostLink { Url = "https://ASP.NET" }, new PostLink { Url = "https://hh.ru/vacancy/135521230", Label = "Подробнее о вакансии на hh.ru" });

        await (await ReparserAsync(Extraction(modelUrl))).ReparseAsync([id], 5, Ct);

        Assert.Equal("https://hh.ru/vacancy/135521230", (await LoadAsync(id)).Url);
    }

    [Fact]
    public async Task With_the_model_answering_a_root_the_page_links_is_not_the_address()
    {
        // Seen live on #60: the model said "https://talanto.work", which the vacancy page links as its logo.
        _web.When("https://talanto.work/jobs/2a3c9885").Respond("text/html",
            """<html><body><header><a href="https://talanto.work/">Talanto</a></header><main><p>Вакансия .NET</p><a href="https://talanto.work/">На главную</a></main></body></html>""");
        var id = await SeedAsync("https://talanto.work", ".NET / C# Developer. Подробнее о вакансии на talanto.work",
            new PostLink { Url = "https://talanto.work/jobs/2a3c9885", Label = "Подробнее о вакансии на talanto.work" });

        await (await ReparserAsync(Extraction("https://talanto.work"))).ReparseAsync([id], 5, Ct);

        Assert.Equal("https://talanto.work/jobs/2a3c9885", (await LoadAsync(id)).Url);
    }

    [Fact]
    public async Task Without_the_model_a_reading_adds_contacts_and_never_removes_them()
    {
        var id = await SeedAsync("https://t.me/csharp_jobs/2514", "Senior .NET. Резюме: jobs@acme.io");
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            var v = await ctx.Vacancies.SingleAsync(x => x.Id == id, Ct);
            v.Contacts = [new VacancyContact { Kind = ContactKind.Telegram, Value = "@hr_named_by_the_model" }]; // a grounded model contact of an earlier reading
            await ctx.SaveChangesAsync(Ct);
        }

        await (await ReparserAsync()).ReparseAsync([id], 5, Ct);

        Assert.Equal(["@hr_named_by_the_model", "jobs@acme.io"], (await LoadAsync(id)).Contacts.Select(c => c.Value));
    }
}
