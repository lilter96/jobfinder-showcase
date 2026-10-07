using JobFinder.Integrations.Web;

namespace JobFinder.Tests.Sources;

/// <summary>Real pages that vacancy posts link to (saved 2026-09-28): what the reader takes as content, links and JobPosting.</summary>
public sealed class PageReaderTests
{
    private static readonly PageReader Reader = new();

    [Fact]
    public async Task Concurrent_native_reads_keep_each_document_text_and_contacts_isolated()
    {
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            var marker = "unique-document-" + index;
            var html = "<html><head><title>" + marker + "</title></head><body><article><h1>" + marker
                + "</h1><p>Senior .NET Developer, jobs" + index + "@example.test</p></article></body></html>";
            var page = Reader.Read(new Uri("https://example.test/jobs/" + index), html, "text/html");
            Assert.Contains(marker, page.Text);
            Assert.Contains("jobs" + index + "@example.test", page.Text);
        })));
    }

    private static WebPage Read(string fixture, string url) => Reader.Read(new Uri(url), Fixture.Read("web/" + fixture), "text/html");

    private static List<string> Urls(WebPage page) => page.Links.Select(l => l.Url).ToList();

    [Fact]
    public void Telegraph_article_gives_the_vacancy_the_recruiters_mailto_and_its_author_not_the_report_link()
    {
        var page = Read("telegraph_alfa.html", "https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25");

        Assert.Equal("MIDDLE C#/.NET DEVELOPER", page.Title);
        Assert.Contains("Разработка новых backend-микросервисов на .NET Core", page.Text);
        Assert.Contains(page.Links, l => l is { Url: "https://t.me/Courierus", Label: "INFLOW" }); // the article's author
        Assert.Contains("mailto:AKHROMOV@ALFABANK.RU", Urls(page));
        Assert.Contains(page.Links, l => l.Url.StartsWith("https://job.alfabank.ru/vacancies/", StringComparison.Ordinal) && l.Label == "APPLY");
        Assert.DoesNotContain(page.Links, l => l.Url.Contains("dmca", StringComparison.Ordinal)); // Telegraph's own "report" chrome
    }

    [Fact]
    public void Teletype_article_is_read_without_its_chrome_and_keeps_the_apply_link_to_the_employer()
    {
        var page = Read("teletype_simbirsoft.html", "https://teletype.in/@remoteit/Ze5d1LOPJqx");

        Assert.Contains("REMOTE RUSSIA | SIMBIRSOFT", page.Text);
        Assert.Contains("• Выполнять функциональное, интеграционное, регрессионное и приёмочное тестирование", page.Text); // list items stay lines
        Assert.Contains(page.Links, l => l is { Url: "https://www.simbirsoft.com/vacancies/qa-automation/", Label: "APPLY" });
        Assert.DoesNotContain("mailbox@teletype.in", page.Text);
    }

    [Fact]
    public void Talanto_job_posting_gives_the_structured_facts()
    {
        var page = Read("talanto_job.html", "https://talanto.work/jobs/b8df888b-57fa-41df-898b-c37c906e6350");

        var job = Assert.IsType<JobPostingData>(page.JobPosting);
        Assert.Equal("Backend .NET / C# Developer (AI-first)", job.Title);
        Assert.Equal("Netevia", job.Company);
        Assert.Equal((2000m, 3500m, "USD"), (job.SalaryFrom, job.SalaryTo, job.Currency));
        Assert.True(job.Remote);
        Assert.Contains("Ищем Backend .NET / C# Developer (AI-first)", page.Text);
        Assert.Contains("• Разработка и развитие бэкенд-сервисов на .NET 8", job.Description); // HTML of the posting rendered as text
    }

    [Fact]
    public void Jobrocket_keeps_the_recruiters_telegram_button_and_the_salary_but_not_the_site_menu()
    {
        var page = Read("jobrocket_job.html", "https://jobrocket.ru/ru/job/net-engineer-cex-birzha-a24adebc");

        Assert.Contains(page.Links, l => l is { Url: "https://t.me/FreeHRarka", Label: "Откликнуться в Telegram" });
        Assert.Equal((150000m, 200000m, "RUB"), (page.JobPosting!.SalaryFrom, page.JobPosting.SalaryTo, page.JobPosting.Currency));
        Assert.Equal("CEX Биржа", page.JobPosting.Company);
    }

    [Fact]
    public void A_script_built_page_reads_as_nearly_empty_the_case_for_the_browser()
    {
        var page = Read("aristek_career.html", "https://aristeksystems.com/career/full-stack-net-engineer/");

        Assert.Equal("Full-Stack .Net Engineer Wanted", page.Title);
        Assert.True(page.Text.Length < PageReader.ShortText, page.Text);
    }

    [Fact]
    public void A_telegram_embed_gives_the_message_text_and_its_hidden_links_only()
    {
        var talanto = Read("tme_embed_rabotacschap_702.html", "https://t.me/rabotacschap/702");
        Assert.StartsWith("Backend .NET / C# Developer (AI-first)", talanto.Text);
        Assert.Contains(talanto.Links, l => l.Url == "https://talanto.work/jobs/b8df888b-57fa-41df-898b-c37c906e6350");
        Assert.DoesNotContain(talanto.Links, l => l.Url == "https://t.me/rabotacschap"); // the widget's channel header is chrome

        var digest = Read("tme_embed_development_vakans_712.html", "https://t.me/development_vakans/712");
        Assert.Equal(5, digest.Links.Count(l => l.Url.StartsWith("https://telegram.me/all_vakans_bot?start=c_", StringComparison.Ordinal)
                                                && l.Label == "Посмотреть контакт"));
    }

    [Fact]
    public void Json_ld_job_postings_are_found_in_graphs_and_arrays_and_broken_blocks_are_skipped()
    {
        const string html = """
            <html><head>
            <script type="application/ld+json">{ this is not json</script>
            <script type="application/ld+json">
              {"@context":"https://schema.org","@graph":[{"@type":"Organization","name":"X"},
               {"@type":["JobPosting"],"title":"Senior .NET","hiringOrganization":"Acme",
                "baseSalary":{"@type":"MonetaryAmount","currency":"EUR","value":{"@type":"QuantitativeValue","value":"4 500"}},
                "jobLocationType":"TELECOMMUTE","employmentType":["FULL_TIME","CONTRACTOR"],"datePosted":"2026-09-20",
                "applicationContact":{"@type":"ContactPoint","email":"Jobs@Acme.io","telephone":"+375 29 123-45-67"},},]}
            </script></head><body><p>Short</p></body></html>
            """;
        var job = Reader.Read(new Uri("https://acme.io/jobs/1"), html, "text/html").JobPosting!;

        Assert.Equal(("Senior .NET", "Acme", 4500m, "EUR", true, "FULL_TIME, CONTRACTOR"),
            (job.Title, job.Company, job.SalaryFrom, job.Currency, job.Remote, job.EmploymentType));
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), job.DatePosted);
        Assert.Equal(["jobs@acme.io", "+375291234567"], job.Contacts.Select(c => c.Value));
    }

    [Fact]
    public void Links_resolve_against_the_base_skip_chrome_and_keep_mailto_and_tel()
    {
        const string html = """
            <html><head><base href="https://acme.io/careers/"></head><body>
            <header><a href="/login">Войти</a></header>
            <main><p>Senior .NET developer. Пишите <a href="mailto:hr@acme.io">HR</a> или звоните <a href="tel:+375291234567">нам</a>.</p>
            <a href="dotnet-senior">Подробнее</a> <a href="#top">наверх</a> <a href="javascript:void(0)">x</a></main>
            <footer><a href="https://vk.com/acme">VK</a></footer></body></html>
            """;
        var page = Reader.Read(new Uri("https://acme.io/careers/"), html, "text/html");

        Assert.Equal(["mailto:hr@acme.io", "tel:+375291234567", "https://acme.io/careers/dotnet-senior"], Urls(page));
    }

    [Fact]
    public void A_plain_text_page_is_its_text_with_its_urls()
    {
        var page = Reader.Read(new Uri("https://docs.google.com/document/d/x/export?format=txt"),
            "Senior .NET\r\n\r\n\r\nОтклик: https://forms.gle/abc  или hr@acme.io", "text/plain");

        Assert.Equal("Senior .NET\n\nОтклик: https://forms.gle/abc или hr@acme.io", page.Text);
        Assert.Equal(["https://forms.gle/abc"], Urls(page));
    }
}
