using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace JobFinder.Tests.Pipeline;

/// <summary>A throwaway Postgres for the whole class; the tests skip when Docker is not running.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private IHost? _producer;
    private readonly SemaphoreSlim _producerLock = new(1, 1);

    public string? SkipReason { get; private set; }

    public IDbContextFactory<JobFinderDbContext> Db { get; private set; } = null!;
    public JobFinder.Pipeline.Settings.PgPolicyStore Policies =>
        new(Db, TimeProvider.System, _producer!.Services.GetRequiredService<JobFinder.Pipeline.Notifications.NotificationDeliveryRequests>());

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception e)
        {
            SkipReason = $"Docker is not available: {e.Message}";
            return;
        }

        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, _container.GetConnectionString());
        Db = new Factory(options.Options);
        using var owner = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(new DatabaseOptions { ConnectionString = _container.GetConnectionString() });
            services.AddSingleton<ServerOwnership>();
            services.AddSingleton(TimeProvider.System);
            services.AddHostedService<SingleInstanceGuard>();
        }).Build();
        await owner.StartAsync();
        try { await DatabaseInitializer.MigrateAsync(Db, NullLogger.Instance, owner.Services.GetRequiredService<ServerOwnership>(), CancellationToken.None); }
        finally { await owner.StopAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_producer is not null)
        {
            await _producer.StopAsync();
            _producer.Dispose();
        }
        _producerLock.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }

    public async Task<VacancyAssessmentCapture> CapturesAsync()
    {
        await _producerLock.WaitAsync();
        try
        {
            if (_producer is null)
            {
                await using var context = await Db.CreateDbContextAsync();
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = context.Database.GetConnectionString() });
                builder.Services.AddSingleton(Db);
                builder.Services.AddSingleton<VacancyAssessmentCapture>();
                builder.Services.AddSingleton<JobFinder.Pipeline.Notifications.Notifier>();
                builder.Services.AddSingleton(TimeProvider.System);
                builder.Services.AddSingleton<NativePublicationScopes>();
                builder.Services.AddSingleton<JobFinder.Pipeline.Notifications.INotificationDeliveryTransport, JobFinder.Pipeline.Notifications.UnavailableNotificationTransport>();
                builder.Services.AddSingleton<JobFinder.Pipeline.Notifications.NotificationDeliveryRequests>();
                builder.AddJobFinderMessaging(consumer: false);
                _producer = builder.Build();
                await _producer.StartAsync();
            }
            return _producer.Services.GetRequiredService<VacancyAssessmentCapture>();
        }
        finally { _producerLock.Release(); }
    }

    public async Task<JobFinder.Pipeline.Notifications.NotificationDeliveryRequests> NotificationsAsync()
    {
        await CapturesAsync();
        return _producer!.Services.GetRequiredService<JobFinder.Pipeline.Notifications.NotificationDeliveryRequests>();
    }

    public async Task<JobFinder.Pipeline.Modules.Candidate.CandidateDocuments> CandidateAsync(IDbContextFactory<JobFinderDbContext>? factory = null) =>
        new(factory ?? Db, TimeProvider.System, await NotificationsAsync());

    public async Task<AsyncServiceScope> PublicationScopeAsync()
    {
        await CapturesAsync();
        return _producer!.Services.CreateAsyncScope();
    }

    public async Task SeedPoliciesAsync()
    {
        await using var context = await Db.CreateDbContextAsync(TestContext.Current.CancellationToken);
        if ((await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).Any())
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        if (await context.Documents.CountAsync(row => row.Key.StartsWith("policy."), TestContext.Current.CancellationToken) == 4) return;
        using var producer = await PostProcessingTestHost.BuildAsync(this, consumer: false);
        await producer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await ActivatorUtilities.CreateInstance<JobFinder.Pipeline.Settings.PolicyInitializer>(producer.Services)
                .StartAsync(TestContext.Current.CancellationToken);
        }
        finally { await producer.StopAsync(CancellationToken.None); }
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }
}

public sealed class IngestIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<VacancyIngestor> IngestorAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var http = new PoliteHttp(new HttpClient(), new ScrapeOptions(), TimeProvider.System);
        return new VacancyIngestor(pg.Db, new HhClient(http), new HabrOptions(), TimeProvider.System, NullLogger<VacancyIngestor>.Instance, await pg.CapturesAsync());
    }

    private static HhVacancyDetail Hh(long id, string title = "Senior .NET разработчик", string company = "ООО «Рога и Копыта»") =>
        new(id, "rabota.by", title, 777, company, "Минск", "BY", WorkFormat.Hybrid, 4000, 5000, "USD", false, EnglishLevel.Unknown,
            "between3And6", ["C#"], "Описание с hh", false,
            // hh gives Moscow time: a non-UTC offset must be storable.
            new DateTimeOffset(2026, 9, 25, 18, 54, 9, TimeSpan.FromHours(3)), $"https://rabota.by/vacancy/{id}", "{}");

    /// <summary>A post read without pages: what the enricher gives a post with no links.</summary>
    private static EnrichedPost Enriched(RawPost post) =>
        new([], [], JobFinder.Core.Contacts.ContactExtractor.FromText(post.Text), post.Text, null, null, null, []);

    private static RawPost Post(string chat, string id, string text) => new()
    {
        Source = SourceKind.Telegram, ChatRef = chat, ChatTitle = "chan", ExternalId = id, Url = $"https://t.me/chan/{id}", Text = text,
        FetchedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Same_hh_vacancy_twice_is_one_row()
    {
        var ingestor = (await IngestorAsync());
        var ct = TestContext.Current.CancellationToken;

        var first = await ingestor.IngestHhAsync(Hh(900001, title: "Synthetic unique duplicate-test vacancy 900001"), null, null, ct);
        var second = await ingestor.IngestHhAsync(Hh(900001, title: "Synthetic unique duplicate-test vacancy 900001"), null, null, ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.VacancyId, second.VacancyId);
    }

    [Fact]
    public async Task A_channel_post_of_the_same_opening_merges_into_the_hh_vacancy()
    {
        var ingestor = (await IngestorAsync());
        var ct = TestContext.Current.CancellationToken;
        var hh = await ingestor.IngestHhAsync(Hh(900002, "Senior .NET разработчик (удалённо)", "ЗАО Альфа"), null, null, ct);

        var post = Post("100", "5", "Ищем Senior .NET Developer в Альфа, пишите @hr_alfa");
        var fromPost = await ingestor.IngestPostAsync(post, new PostExtraction
        {
            IsVacancy = true, IsDotnet = true, Title = "Senior .NET Developer", Company = "Альфа", Contacts = ["@hr_alfa"],
        }, Enriched(post), new PostFacts([.. Enriched(post).Contacts], null), ct);

        Assert.False(fromPost.Created);
        Assert.Equal(hh.VacancyId, fromPost.VacancyId);
        await using var ctx = await pg.Db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.Include(x => x.Sources).SingleAsync(x => x.Id == hh.VacancyId, ct);
        Assert.Equal(2, v.Sources.Count);
        Assert.Equal("Описание с hh", v.Description); // the structured source stays authoritative
        var contact = Assert.Single(v.Contacts);     // but the post fills what hh lacked
        Assert.Equal((ContactKind.Telegram, "@hr_alfa"), (contact.Kind, contact.Value));
    }

    [Fact]
    public async Task Posts_without_a_company_never_merge()
    {
        var ingestor = (await IngestorAsync());
        var ct = TestContext.Current.CancellationToken;
        var x = new PostExtraction { IsVacancy = true, IsDotnet = true, Title = "Senior .NET Developer" };

        var a = await ingestor.IngestPostAsync(Post("200", "1", "Senior .NET Developer, пишите"), x, Enriched(Post("200", "1", "Senior .NET Developer, пишите")), new PostFacts([], null), ct);
        var b = await ingestor.IngestPostAsync(Post("201", "1", "Senior .NET Developer, пишите"), x, Enriched(Post("201", "1", "Senior .NET Developer, пишите")), new PostFacts([], null), ct);

        Assert.True(a.Created);
        Assert.True(b.Created);
        Assert.NotEqual(a.VacancyId, b.VacancyId);
    }

    [Fact]
    public async Task Company_row_is_shared_by_hh_id()
    {
        var ingestor = (await IngestorAsync());
        var ct = TestContext.Current.CancellationToken;
        await ingestor.IngestHhAsync(Hh(900003, "Lead .NET", "Бета"), null, null, ct);
        await ingestor.IngestHhAsync(Hh(900004, "Middle .NET", "Бета"), null, null, ct);

        await using var ctx = await pg.Db.CreateDbContextAsync(ct);
        Assert.Equal(1, await ctx.Companies.CountAsync(c => c.HhId == 777, ct));
    }

    private static EnrichedPost WithPage(RawPost post, JobFinder.Integrations.Web.JobPostingData? posting, string pageUrl = "https://talanto.work/jobs/1") =>
        new([new PostLink { Url = pageUrl, Label = "Подробнее" }],
            [new JobFinder.Integrations.Web.CrawledPage(new JobFinder.Integrations.Web.WebPage(new Uri(pageUrl), "Senior .NET", "Полное описание вакансии", [], posting), 1, "Подробнее", false)],
            [], post.Text + "\n\n———\n" + pageUrl + "\nПолное описание вакансии", pageUrl, posting, null, []);

    [Fact]
    public void A_page_posting_fills_what_the_model_left_empty()
    {
        var post = Post("300", "1", "Senior .NET — подробнее по ссылке");
        var posting = new JobFinder.Integrations.Web.JobPostingData("Senior .NET Developer", "Netevia", null, 2000, 3500, "usd", true, null, null, null, []);

        var draft = VacancyIngestor.PostDraft(post, new PostExtraction { IsVacancy = true, IsDotnet = true },
            WithPage(post, posting), new PostFacts([], "https://talanto.work/jobs/1"));

        Assert.Equal(("Senior .NET Developer", "Netevia"), (draft.Title, draft.CompanyName));
        Assert.Equal((2000m, 3500m, "USD"), (draft.SalaryFrom, draft.SalaryTo, draft.Currency));
        Assert.Equal(WorkFormat.Remote, draft.WorkFormats);
        Assert.Equal("https://talanto.work/jobs/1", draft.Url);
        Assert.Contains("Полное описание вакансии", draft.Description);
    }

    [Fact]
    public void The_models_own_salary_and_format_win_over_the_page_and_without_a_page_the_post_is_the_address()
    {
        var post = Post("300", "2", "Senior .NET, 4000$, гибрид");
        var posting = new JobFinder.Integrations.Web.JobPostingData(null, null, null, 2000, 3500, "USD", true, null, null, null, []);

        var draft = VacancyIngestor.PostDraft(post, new PostExtraction
        {
            IsVacancy = true, IsDotnet = true, Title = "Senior .NET", SalaryFrom = 4000, Currency = "USD", WorkFormats = ["hybrid"],
        }, WithPage(post, posting), new PostFacts([], null));

        Assert.Equal((4000m, (decimal?)null), (draft.SalaryFrom, draft.SalaryTo));
        Assert.Equal(WorkFormat.Hybrid, draft.WorkFormats);
        Assert.Equal("https://t.me/chan/2", draft.Url);
    }

    [Fact]
    public async Task Rereading_a_post_gives_its_vacancy_the_contacts_the_page_and_the_full_text_but_an_hh_vacancy_only_contacts()
    {
        var ingestor = (await IngestorAsync());
        var ct = TestContext.Current.CancellationToken;
        var post = Post("400", "1", "MIDDLE C#/.NET DEVELOPER | ALFA-BANK");
        var x = new PostExtraction { IsVacancy = true, IsDotnet = true, Title = "Middle C#/.NET Developer", Company = "Альфа-Банк" };
        var created = await ingestor.IngestPostAsync(post, x, Enriched(post), new PostFacts([], null), ct);

        var reread = VacancyIngestor.PostDraft(post, x, WithPage(post, null, "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25"), new PostFacts(
            [new VacancyContact { Kind = ContactKind.Email, Value = "akhromov@alfabank.ru", Label = "AKHROMOV@ALFABANK.RU", Source = "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25" }],
            "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25"));
        Assert.True(await ingestor.RefreshAsync(created.VacancyId, reread, _ => false, ct));
        Assert.False(await ingestor.RefreshAsync(created.VacancyId, reread, _ => false, ct)); // nothing new the second time

        await using (var ctx = await pg.Db.CreateDbContextAsync(ct))
        {
            var v = await ctx.Vacancies.AsNoTracking().SingleAsync(x => x.Id == created.VacancyId, ct);
            Assert.Equal("https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25", v.Url);
            Assert.Contains("Полное описание вакансии", v.Description);
            Assert.Equal(("akhromov@alfabank.ru", "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25"), (v.Contacts[0].Value, v.Contacts[0].Source));
        }

        var hh = await ingestor.IngestHhAsync(Hh(900050), null, null, ct);
        await ingestor.RefreshAsync(hh.VacancyId, reread, _ => false, ct);
        await using (var ctx = await pg.Db.CreateDbContextAsync(ct))
        {
            var v = await ctx.Vacancies.AsNoTracking().SingleAsync(x => x.Id == hh.VacancyId, ct);
            Assert.Equal(("https://rabota.by/vacancy/900050", "Описание с hh"), (v.Url, v.Description)); // hh stays the source of truth
            Assert.Single(v.Contacts);
        }
    }

    [Fact]
    public async Task A_reread_corrects_an_address_it_took_from_the_post_but_keeps_one_from_elsewhere()
    {
        var ingestor = (await IngestorAsync());
        var ct = TestContext.Current.CancellationToken;
        var post = Post("600", "1", "Full-Stack .Net Engineer, Откликнуться");
        var x = new PostExtraction { IsVacancy = true, IsDotnet = true, Title = "Full-Stack .Net Engineer", Company = "Aristek" };
        // An earlier reading took the post link as the address; today the page is gone (a retired vacancy).
        var created = await ingestor.IngestPostAsync(post, x, Enriched(post), new PostFacts([], "https://aristeksystems.com/career/full-stack-net-engineer/"), ct);
        var today = VacancyIngestor.PostDraft(post, x, Enriched(post), new PostFacts([], null));

        await ingestor.RefreshAsync(created.VacancyId, today, u => u == "https://aristeksystems.com/career/full-stack-net-engineer/", ct);
        await using (var ctx = await pg.Db.CreateDbContextAsync(ct))
            Assert.Equal("https://t.me/chan/1", (await ctx.Vacancies.AsNoTracking().SingleAsync(v => v.Id == created.VacancyId, ct)).Url);

        // An address from another source (not among the post links) is not the reading to change.
        var other = await ingestor.IngestPostAsync(Post("601", "1", "Senior .NET"), x with { Title = "Senior .NET" }, Enriched(Post("601", "1", "Senior .NET")),
            new PostFacts([], "https://career.habr.com/vacancies/1000001"), ct);
        await ingestor.RefreshAsync(other.VacancyId, VacancyIngestor.PostDraft(Post("601", "1", "Senior .NET"), x, Enriched(Post("601", "1", "Senior .NET")),
            new PostFacts([], "https://talanto.work/jobs/1")), u => u == "https://talanto.work/jobs/1", ct);
        await using (var ctx = await pg.Db.CreateDbContextAsync(ct))
            Assert.Equal("https://career.habr.com/vacancies/1000001", (await ctx.Vacancies.AsNoTracking().SingleAsync(v => v.Id == other.VacancyId, ct)).Url);
    }

    [Fact]
    public async Task A_reread_of_the_only_source_replaces_its_contacts_a_vacancy_seen_elsewhere_keeps_the_union()
    {
        var ingestor = (await IngestorAsync());
        var ct = TestContext.Current.CancellationToken;
        var x = new PostExtraction { IsVacancy = true, IsDotnet = true, Title = "QA Automation C#", Company = "SimbirSoft" };
        var wrong = new VacancyContact { Kind = ContactKind.Telegram, Value = "@remoteit" }; // seen live: an earlier reading took a platform author
        var right = new VacancyContact { Kind = ContactKind.Email, Value = "jobs@simbirsoft.com", Source = "https://www.simbirsoft.com/vacancies/qa-automation/" };

        var sole = Post("700", "1", "QA AUTOMATION C# | SIMBIRSOFT");
        var soleId = (await ingestor.IngestPostAsync(sole, x, Enriched(sole), new PostFacts([wrong], null), ct)).VacancyId;
        Assert.True(await ingestor.RefreshAsync(soleId, VacancyIngestor.PostDraft(sole, x, Enriched(sole), new PostFacts([right], null)), _ => false, ct));

        // The same opening posted in a second channel: a merged vacancy, whose other sighting may have named its own contacts.
        var first = Post("701", "1", "Senior .NET в Гамма");
        var y = new PostExtraction { IsVacancy = true, IsDotnet = true, Title = "Senior .NET", Company = "Гамма" };
        var mergedId = (await ingestor.IngestPostAsync(first, y, Enriched(first), new PostFacts([wrong], null), ct)).VacancyId;
        var second = Post("702", "1", "Senior .NET в Гамма");
        await ingestor.IngestPostAsync(second, y, Enriched(second), new PostFacts([], null), ct);
        await ingestor.RefreshAsync(mergedId, VacancyIngestor.PostDraft(first, y, Enriched(first), new PostFacts([right], null)), _ => false, ct);

        // A reading without the model is incomplete (its grounded contacts are missing): it only adds, it never takes away.
        var incomplete = VacancyIngestor.PostDraft(sole, x, Enriched(sole), new PostFacts([], null));
        await ingestor.RefreshAsync(soleId, incomplete, _ => false, ct, complete: false);

        await using var ctx = await pg.Db.CreateDbContextAsync(ct);
        Assert.Equal(["jobs@simbirsoft.com"], (await ctx.Vacancies.AsNoTracking().SingleAsync(v => v.Id == soleId, ct)).Contacts.Select(c => c.Value));
        Assert.Equal(["@remoteit", "jobs@simbirsoft.com"], (await ctx.Vacancies.AsNoTracking().SingleAsync(v => v.Id == mergedId, ct)).Contacts.Select(c => c.Value));
    }

    [Fact]
    public async Task A_posts_hidden_links_and_a_vacancys_contacts_survive_the_database()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var ct = TestContext.Current.CancellationToken;
        var post = Post("500", "1", "Senior .NET");
        post.Links = [new PostLink { Url = "https://telegram.me/all_vakans_bot?start=c_1", Label = "Посмотреть контакт" }, new PostLink { Url = "https://x.io" }];
        await using (var ctx = await pg.Db.CreateDbContextAsync(ct))
        {
            ctx.RawPosts.Add(post);
            await ctx.SaveChangesAsync(ct);
        }

        await using var read = await pg.Db.CreateDbContextAsync(ct);
        var back = await read.RawPosts.AsNoTracking().SingleAsync(p => p.Id == post.Id, ct);
        Assert.Equal([("https://telegram.me/all_vakans_bot?start=c_1", "Посмотреть контакт"), ("https://x.io", (string?)null)],
            back.Links.Select(l => (l.Url, l.Label)));
        var json = await read.Database.SqlQuery<string>($"SELECT links::text AS \"Value\" FROM raw_posts WHERE id = {post.Id}").SingleAsync(ct);
        Assert.Contains("\"url\"", json); // camelCase jsonb, as the other jsonb columns
    }
}
