using System.Net;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Integrations.Http;
using JobFinder.Integrations.Web;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Ingest;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace JobFinder.Tests.Pipeline;

/// <summary>A post plus what it links to → the vacancy's contacts, description and the model's view; the pages are real fixtures.</summary>
public sealed class PostEnricherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PostPrefilter Prefilter = new(new PrefilterOptions
    {
        TechMarkers = [".net", "c#", "dotnet"], VacancyMarkers = ["ваканс", "ищем", "developer", "разработчик"], RejectMarkers = ["#резюме"], MinLength = 80,
    });

    private readonly MockHttpMessageHandler _web = new();
    private readonly EnrichmentOptions _options = new();

    public PostEnricherTests() => _web.Fallback.Respond(HttpStatusCode.NotFound);

    private PostEnricher Enricher()
    {
        var http = new PoliteHttp(new HttpClient(_web), new ScrapeOptions { MinDelayMs = 0, MaxDelayMs = 1 }, TimeProvider.System);
        var policy = new LinkPolicy(_options);
        return new PostEnricher(new VacancyCrawler(http, new PageReader(), policy, _options, NullLogger<VacancyCrawler>.Instance), policy, _options);
    }

    private static RawPost Post(string text, string url = "https://t.me/devs_it/5810", string? author = null, params PostLink[] links) => new()
    {
        Source = SourceKind.Telegram, ChatRef = "1", ExternalId = "5810", Url = url, Text = text, Author = author, Links = [.. links],
    };

    private static CrawledPage Page(string fixture, string url) =>
        new(new PageReader().Read(new Uri(url), Fixture.Read("web/" + fixture), "text/html"), 1, null, false);

    [Fact]
    public void Contacts_come_from_the_author_the_text_the_hidden_links_and_the_pages_without_site_chrome()
    {
        var post = Post("Senior .NET в Альфа. Резюме: hr@acme.io, канал @devs_it", author: "@Recruiter_Kate", links:
        [
            new PostLink { Url = "https://t.me/hr_anna", Label = "Рекрутер" },
            new PostLink { Url = "https://telegram.me/all_vakans_bot?start=c_1", Label = "Посмотреть контакт" },
            new PostLink { Url = "https://vk.com/devs_it", Label = "в VK" },
        ]);
        var pages = new[]
        {
            Page("telegraph_alfa.html", "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25"),
            Page("jobrocket_job.html", "https://jobrocket.ru/ru/job/net-engineer-cex-birzha-a24adebc"),
        };

        var contacts = Enricher().Contacts(post, PostEnricher.LinksOf(post), pages);

        Assert.Equal(
        [
            (ContactKind.Telegram, "@recruiter_kate", "автор поста", (string?)null),
            (ContactKind.Email, "hr@acme.io", null, null),
            (ContactKind.Telegram, "@hr_anna", "Рекрутер", null),
            (ContactKind.Link, "https://t.me/all_vakans_bot?start=c_1", "Посмотреть контакт", null),
            (ContactKind.Telegram, "@courierus", "INFLOW", "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25"),
            (ContactKind.Email, "akhromov@alfabank.ru", "AKHROMOV@ALFABANK.RU", "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25"),
            // The article's APPLY: how to answer even though that page is out of reach (an untrusted CA).
            (ContactKind.Link, "https://job.alfabank.ru/vacancies/moskva/remote-job/razrabotchik-c_-_-_net-(middle-_-middle+)_38098?archetype=1004&text=c", "APPLY",
                "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25"),
            (ContactKind.Telegram, "@freehrarka", "Откликнуться в Telegram", "https://jobrocket.ru/ru/job/net-engineer-cex-birzha-a24adebc"),
        ], contacts.Select(c => (c.Kind, c.Value, c.Label, c.Source)));
        // Not the channel's own handle, not the aggregator's channel or Telegram's report mailbox.
        Assert.DoesNotContain(contacts, c => c.Value is "@devs_it" or "@jobrocket" or "dmca@telegram.org");
    }

    [Fact]
    public void A_posts_apply_link_to_an_ats_page_is_how_to_answer_even_when_the_page_names_no_one()
    {
        // The shape of most contactless channel vacancies (seen live): «Откликнуться» → huntflow / greenhouse / a career page.
        var post = Post("Вакансия C# .Net Developer в Sakura Games. Откликнуться", links:
        [
            new PostLink { Url = "http://asp.net/", Label = "ASP.NET" },
            new PostLink { Url = "https://sakuragames.huntflow.io/vacancy/c-net-developer-1", Label = "Откликнуться" },
        ]);
        var ats = new CrawledPage(new WebPage(new Uri("https://sakuragames.huntflow.io/vacancy/c-net-developer-1"), "C# .Net Developer", "Описание вакансии без контактов", [], null),
            1, "Откликнуться", false);

        var contact = Assert.Single(Enricher().Contacts(post, PostEnricher.LinksOf(post), [ats]));

        Assert.Equal((ContactKind.Link, "https://sakuragames.huntflow.io/vacancy/c-net-developer-1", "Откликнуться"), (contact.Kind, contact.Value, contact.Label));
    }

    [Fact]
    public void A_sites_social_icons_are_not_contacts_and_its_general_mailbox_comes_after_hr()
    {
        // Seen live on simbirsoft.com: a "Telegram" icon of the company channel, info@/request@ before the HR address.
        var page = new CrawledPage(new WebPage(new Uri("https://www.simbirsoft.com/vacancies/qa-automation/"), "QA", "Пишите: info@simbirsoft.com или hr@simbirsoft.com",
        [
            new PostLink { Url = "https://t.me/simbirsoft_depthdev", Label = "Telegram" },
            new PostLink { Url = "https://vk.com/simbirsoft", Label = "VK" },
            new PostLink { Url = "mailto:request@simbirsoft.com", Label = "request@simbirsoft.com" },
            new PostLink { Url = "https://t.me/anna_simbirsoft_hr", Label = "Написать Анне в Telegram" },
        ], null), 2, "APPLY", false);

        var contacts = Enricher().Contacts(Post("QA C#"), [], [page]);

        Assert.Equal(["@anna_simbirsoft_hr", "hr@simbirsoft.com", "request@simbirsoft.com", "info@simbirsoft.com"], contacts.Select(c => c.Value));
    }

    [Fact]
    public void A_blog_platforms_author_handle_is_not_taken_for_a_telegram_contact()
    {
        var post = Post("QA AUTOMATION C# | REMOTE RUSSIA | SIMBIRSOFT https://teletype.in/@remoteit/Ze5d1LOPJqx");
        var contacts = Enricher().Contacts(post, PostEnricher.LinksOf(post), [Page("teletype_simbirsoft.html", "https://teletype.in/@remoteit/Ze5d1LOPJqx")]);
        Assert.DoesNotContain(contacts, c => c.Value == "@remoteit"); // seen live: the teletype author came out as a Telegram contact
    }

    [Fact]
    public void The_models_contacts_are_kept_only_when_the_sources_hold_them()
    {
        var enriched = new EnrichedPost([new PostLink { Url = "https://forms.gle/Real", Label = "Анкета" }],
            [new CrawledPage(new WebPage(new Uri("https://page.example/job"), "Job", "Звоните: +375 (29) 123-45-67, e-mail HR@Acme.io", [], null), 1, null, false)],
            [new VacancyContact { Kind = ContactKind.Email, Value = "hr@acme.io", Source = "https://page.example/job" }],
            "Senior .NET. Пишите Анне: hr_anna", null, null, null, []);

        var grounded = Enricher().Ground(Post("Senior .NET. Пишите Анне: hr_anna"),
            ["@hr_anna", "@made_up_recruiter", "8 029 123-45-67", "hr@acme.io", "https://forms.gle/Real", "https://forms.gle/Fake"], enriched);

        Assert.Equal(
        [
            (ContactKind.Email, "hr@acme.io", "https://page.example/job"), // the deterministic one keeps its source
            (ContactKind.Telegram, "@hr_anna", null),
            (ContactKind.Phone, "+375291234567", null), // same number however it was written
            (ContactKind.Link, "https://forms.gle/Real", null),
        ], grounded.Select(c => (c.Kind, c.Value, c.Source)));
    }

    [Fact]
    public void A_handle_the_model_found_on_a_page_or_in_a_url_is_not_grounded_only_the_posts_own_are()
    {
        // Seen live on #351: the model returned the teletype author "@remoteit", which occurs only in the page URL.
        var post = Post("QA AUTOMATION C# | SIMBIRSOFT https://teletype.in/@remoteit/Ze5d1LOPJqx");
        var page = new CrawledPage(new WebPage(new Uri("https://teletype.in/@remoteit/Ze5d1LOPJqx"), "QA", "Автор @remoteit · пишите на jobs@simbirsoft.com", [], null), 1, null, false);
        var enriched = new EnrichedPost(PostEnricher.LinksOf(post), [page], [], post.Text, null, null, null, []);

        var grounded = Enricher().Ground(post, ["@remoteit", "jobs@simbirsoft.com", "mailbox@teletype.in"], enriched);

        Assert.Equal(["jobs@simbirsoft.com"], grounded.Select(c => c.Value));
    }

    [Theory]
    [InlineData("https://t.me/all_vakans_bot?start=c_39004", null)] // seen live on #353: a bot is a contact, never the vacancy address
    [InlineData("https://aristeksystems.com/career/full-stack-net-engineer/", null)] // seen live on #333: the crawl found it gone
    [InlineData("https://forms.gle/abc", null)] // a form stays a contact
    [InlineData("https://made-up.example/job", null)] // not among the sources
    [InlineData("https://hh.ru/vacancy/137863928", "https://hh.ru/vacancy/137863928")]
    [InlineData("https://careers.example/job/7", "https://careers.example/job/7")]
    [InlineData("https://careers.example", null)] // seen live: a bare domain, a prefix of the real link, was taken as the address
    [InlineData("https://careers.example/", null)] // a root the page links (its logo) is still no address
    [InlineData("https://careers.example/job", null)]
    public void The_models_apply_url_is_the_address_only_when_it_is_a_live_page_the_post_links(string modelUrl, string? expected)
    {
        var post = Post("Senior .NET https://t.me/all_vakans_bot?start=c_39004 https://aristeksystems.com/career/full-stack-net-engineer/ https://forms.gle/abc "
                        + "https://hh.ru/vacancy/137863928 https://careers.example/job/7");
        var page = new CrawledPage(new WebPage(new Uri("https://careers.example/job/7"), "Job", "text", [new PostLink { Url = "https://careers.example/", Label = "Home" }], null),
            1, null, false);
        var enriched = new EnrichedPost(PostEnricher.LinksOf(post), [page], [], post.Text, null, null, null, [],
            new HashSet<string> { "https://aristeksystems.com/career/full-stack-net-engineer" });

        var facts = Enricher().Resolve(post, new PostExtraction { IsVacancy = true, IsDotnet = true, ApplyUrl = modelUrl }, enriched);

        Assert.Equal(expected, facts.Url);
    }

    [Theory]
    [InlineData("MIDDLE C#/.NET DEVELOPER | REMOTE RUSSIA | ALFA-BANK #csharp https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25", true)]
    [InlineData("Новая вакансия, подробности по ссылке https://careers.example/job/1", true)]
    [InlineData("Статья про котиков https://example.com/cats", false)] // no sign of a vacancy or the stack
    [InlineData("#резюме .NET разработчик, портфолио https://example.com/cv", false)] // a candidate, not an offer
    [InlineData("Вакансия C# — всё в нашем VK https://vk.com/jobs", false)] // nothing readable behind it
    public void A_short_post_that_only_links_out_is_worth_reading_when_it_looks_like_a_vacancy(string text, bool worth)
    {
        var post = Post(text);
        Assert.Equal(worth, Enricher().WorthReading(post, PostEnricher.LinksOf(post), Prefilter));
    }

    [Fact]
    public void A_long_post_is_judged_by_its_own_text()
    {
        var post = Post("Вакансия .NET " + new string('x', 500) + " https://careers.example/job/1");
        Assert.False(Enricher().WorthReading(post, PostEnricher.LinksOf(post), Prefilter));
    }

    [Fact]
    public void An_hh_vacancy_behind_a_text_link_is_found_and_links_are_one_per_page()
    {
        var post = Post("Senior .NET, откликайтесь https://talanto.work/jobs/1?utm_source=tg", links:
        [
            new PostLink { Url = "https://rabota.by/vacancy/137863928?from=share", Label = "Откликнуться" },
            new PostLink { Url = "https://talanto.work/jobs/1", Label = "Подробнее" },
        ]);

        Assert.Equal(("rabota.by", 137863928L), PostEnricher.HeadHunterLink(post, PostEnricher.LinksOf(post)));
        Assert.Equal([("https://rabota.by/vacancy/137863928?from=share", "Откликнуться"), ("https://talanto.work/jobs/1", "Подробнее")],
            PostEnricher.LinksOf(post).Select(l => (l.Url, l.Label)));
    }

    [Fact]
    public async Task A_post_whose_details_are_an_hh_vacancy_is_known_by_that_page_and_its_footer_is_not_read()
    {
        // Seen live on #65: «Подробнее о вакансии на hh.ru» behind the text, the channel network footer after it, "ASP.NET" auto-linked.
        var post = Post("Tech Lead (C#/.NET), ASP.NET Core. ➡️ Подробнее о вакансии на hh.ru. Навигация: База знаний • Задачи", links:
        [
            new PostLink { Url = "https://ASP.NET" },
            new PostLink { Url = "https://hh.ru/vacancy/135846342?query=.net&hhtmFrom=vacancy_search_list", Label = "Подробнее о вакансии на hh.ru" },
            new PostLink { Url = "https://t.me/csharpproglib", Label = "База знаний" },
        ]);
        var any = _web.When("*").Respond("text/html", "<p>should not be read</p>");

        var enriched = await Enricher().EnrichAsync(post, PostEnricher.LinksOf(post), Ct);

        Assert.Equal("https://hh.ru/vacancy/135846342", enriched.DetailUrl);
        Assert.Equal(("hh.ru", 135846342L), enriched.HeadHunter);
        Assert.Empty(enriched.Pages);
        Assert.Equal(0, _web.GetMatchCount(any)); // neither hh (its own path and budget) nor asp.net nor the channel is fetched
    }

    [Fact]
    public async Task The_vacancy_page_is_the_address_even_when_the_post_links_its_company_first()
    {
        // Seen live on #344: the company card was the first link, the vacancy the second.
        _web.When("https://talanto.work/companies/quickly").Respond("text/html", "<main><h1>Квикли Сеч ИТ</h1><p>О компании</p></main>");
        _web.When("https://talanto.work/jobs/ea101839").Respond("text/html",
            """<html><head><script type="application/ld+json">{"@type":"JobPosting","title":"Senior .NET"}</script></head><body><main><p>Вакансия</p></main></body></html>""");
        var post = Post("Senior .NET в Квикли", links:
        [
            new PostLink { Url = "https://talanto.work/companies/quickly", Label = "Квикли Сеч ИТ" },
            new PostLink { Url = "https://talanto.work/jobs/ea101839", Label = "Senior .NET" },
        ]);

        var enriched = await Enricher().EnrichAsync(post, PostEnricher.LinksOf(post), Ct);

        Assert.Equal("https://talanto.work/jobs/ea101839", enriched.DetailUrl);
    }

    [Fact]
    public async Task An_employers_homepage_is_read_but_is_not_the_vacancys_address()
    {
        // Seen live on #114-116: the post linked "baitun.kz", its root became the vacancy address.
        _web.When("https://baitun.kz/").Respond("text/html", "<main><h1>Baitun</h1><p>Мы делаем плагины для Revit</p></main>");
        var post = Post("Программист C# для Revit плагинов, сайт baitun.kz", links: [new PostLink { Url = "https://baitun.kz/" }]);

        var enriched = await Enricher().EnrichAsync(post, PostEnricher.LinksOf(post), Ct);

        Assert.Single(enriched.Pages);
        Assert.Contains("плагины для Revit", enriched.Description);
        Assert.Null(enriched.DetailUrl); // the post stays the address
    }

    [Fact]
    public async Task A_title_only_post_becomes_a_full_vacancy_from_the_page_it_links_to()
    {
        _web.When("https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25").Respond("text/html", Fixture.Read("web/telegraph_alfa.html"));
        var post = Post("MIDDLE C#/.NET DEVELOPER | REMOTE RUSSIA | ALFA-BANK #remote #csharp https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25",
            url: "https://t.me/csharp_jobs/352");

        var enriched = await Enricher().EnrichAsync(post, PostEnricher.LinksOf(post), Ct);

        Assert.Equal("https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25", enriched.DetailUrl);
        Assert.StartsWith(post.Text, enriched.Description);
        Assert.Contains("Разработка новых backend-микросервисов на .NET Core", enriched.Description);
        Assert.Contains(enriched.Contacts, c => c.Value == "akhromov@alfabank.ru");
        Assert.Contains(enriched.Contacts, c => c.Value == "@courierus");
        Assert.True(Prefilter.LooksLikeDotnetVacancy(enriched.Description, out _));
        Assert.Contains(enriched.Failures, f => f.Contains("job.alfabank.ru", StringComparison.Ordinal)); // APPLY → not served here: noted, not fatal

        var input = JobFinderLlm.PostInput(post.Text, "C# jobs", enriched.Links, enriched.ModelPages);
        Assert.Contains("<page url=\"https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25\">", input);
        Assert.Contains("Разработка новых backend-микросервисов", input);
    }
}

public sealed class PostInputTests
{
    [Fact]
    public void The_model_sees_the_post_its_hidden_links_and_its_pages_each_as_data()
    {
        var input = JobFinderLlm.PostInput("Senior .NET", "Канал", [new PostLink { Url = "https://forms.gle/a", Label = "Откликнуться\nсейчас" }, new PostLink { Url = "https://x.io" }],
            [new LinkedPage("https://a.example/job?q=\"x\"", "Senior .NET — Acme", "Описание вакансии")]);

        Assert.Equal("""
            Источник: Канал
            <post>
            Senior .NET
            </post>
            <links>
            - Откликнуться сейчас: https://forms.gle/a
            - https://x.io
            </links>
            <page url="https://a.example/job?q=%22x%22">
            Senior .NET — Acme
            Описание вакансии
            </page>
            """.Replace("\r\n", "\n"), input);
    }

    [Fact]
    public void Pages_share_a_budget_in_order_and_each_is_cut_to_its_share()
    {
        var pages = Enumerable.Range(1, 5).Select(i => new LinkedPage($"https://p{i}.example", null, new string((char)('a' + i), 6000))).ToList();

        var input = JobFinderLlm.PostInput("post", "src", null, pages);

        Assert.DoesNotContain("<links>", input);
        Assert.Contains("https://p1.example", input);
        Assert.Contains("https://p3.example", input);
        Assert.DoesNotContain("https://p4.example", input); // the budget is spent by the first three
        Assert.True(input.Length < 13000);
    }
}
