using System.Net;
using JobFinder.Core.Domain;
using JobFinder.Integrations.Http;
using JobFinder.Integrations.Web;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace JobFinder.Tests.Sources;

/// <summary>
/// The crawl behind a post, over the real PoliteHttp with the network mocked: the pages real posts linked to (saved fixtures), what is
/// followed, what never is, and what a failing page costs.
/// </summary>
public sealed class VacancyCrawlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MockHttpMessageHandler _web = new();

    public VacancyCrawlerTests() => _web.Fallback.Respond(HttpStatusCode.NotFound);

    private VacancyCrawler Crawler(EnrichmentOptions? options = null, IPageRenderer? renderer = null)
    {
        options ??= new EnrichmentOptions();
        var http = new PoliteHttp(new HttpClient(_web), new ScrapeOptions { MinDelayMs = 0, MaxDelayMs = 1 }, TimeProvider.System);
        return new VacancyCrawler(http, new PageReader(), new LinkPolicy(options), options, NullLogger<VacancyCrawler>.Instance, renderer);
    }

    private MockedRequest Serve(string url, string fixture) => _web.When(url).Respond("text/html", Fixture.Read("web/" + fixture));

    private MockedRequest ServeHtml(string url, string html) => _web.When(url).Respond("text/html", html);

    private static PostLink Link(string url, string? label = null) => new() { Url = url, Label = label };

    [Fact]
    public async Task A_repost_is_followed_to_the_vacancy_page_its_link_hides()
    {
        Serve("https://t.me/rabotacschap/702?embed=1&mode=tme", "tme_embed_rabotacschap_702.html");
        Serve("https://talanto.work/jobs/b8df888b-57fa-41df-898b-c37c906e6350", "talanto_job.html");
        var channel = _web.When("https://t.me/rabotacschap").Respond("text/html", "");
        var ad = _web.When("https://updatecv.me/*").Respond("text/html", "");

        var result = await Crawler().CrawlAsync([Link("https://t.me/rabotacschap/702")], null, Ct);

        Assert.Equal(["https://t.me/rabotacschap/702", "https://talanto.work/jobs/b8df888b-57fa-41df-898b-c37c906e6350"],
            result.Pages.Select(p => p.Page.Url.AbsoluteUri));
        Assert.Equal([1, 2], result.Pages.Select(p => p.Depth));
        Assert.Equal("Netevia", result.Pages[1].Page.JobPosting!.Company);
        Assert.Equal(0, _web.GetMatchCount(channel) + _web.GetMatchCount(ad));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task A_platforms_apply_link_leads_to_the_employers_vacancy_but_its_menu_does_not()
    {
        Serve("https://teletype.in/@remoteit/Ze5d1LOPJqx", "teletype_simbirsoft.html");
        // Requested as linked (www and the slash kept: the bare name resolves to a dead address from some networks), known canonically.
        var employer = ServeHtml("https://www.simbirsoft.com/vacancies/qa-automation/", "<article><h1>QA Automation C#</h1><p>Пишите hr@simbirsoft.com</p></article>");
        var menu = _web.When("https://teletype.in/about").Respond("text/html", "");
        var login = _web.When("https://teletype.in/login*").Respond("text/html", "");

        var result = await Crawler().CrawlAsync([Link("https://teletype.in/@remoteit/Ze5d1LOPJqx")], null, Ct);

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(("https://simbirsoft.com/vacancies/qa-automation", 2, "APPLY"),
            (result.Pages[1].Page.Url.AbsoluteUri, result.Pages[1].Depth, result.Pages[1].Via));
        Assert.Equal(0, _web.GetMatchCount(menu) + _web.GetMatchCount(login));
        Assert.Equal(1, _web.GetMatchCount(employer));
    }

    [Fact]
    public async Task The_similar_vacancies_of_a_vacancy_page_are_not_read()
    {
        Serve("https://jobrocket.ru/ru/job/net-engineer-cex-birzha-a24adebc", "jobrocket_job.html");
        var similar = _web.When("https://jobrocket.ru/job/*").Respond("text/html", "<p>other</p>");

        var result = await Crawler().CrawlAsync([Link("https://jobrocket.ru/ru/job/net-engineer-cex-birzha-a24adebc")], null, Ct);

        Assert.Single(result.Pages);
        Assert.Equal(0, _web.GetMatchCount(similar));
    }

    [Fact]
    public async Task Other_openings_on_a_vacancy_page_are_not_read_whatever_their_cards_say()
    {
        ServeHtml("https://h.careers/job/ad5d6ec2", "<main><h1>Разработчик .NET</h1><p>Описание .NET-вакансии</p>"
            + "<a href='https://h.careers/job/ae0d92ca'>Golang-разработчик · Подробнее</a><a href='https://h.careers/job/7eb084c1'>Java · Подробнее</a></main>");
        var others = _web.When("https://h.careers/job/ae0d92ca").Respond("text/html", "<p>Golang</p>");
        var java = _web.When("https://h.careers/job/7eb084c1").Respond("text/html", "<p>Java</p>");

        var result = await Crawler().CrawlAsync([Link("https://h.careers/job/ad5d6ec2", "Подробнее о вакансии на h.careers")], null, Ct);

        Assert.Single(result.Pages);
        Assert.Equal(0, _web.GetMatchCount(others) + _web.GetMatchCount(java));
    }

    [Fact]
    public async Task Each_page_is_read_once_the_post_itself_never_and_depth_stops_the_crawl()
    {
        ServeHtml("https://a.example/start", "<main><p>teaser</p><a href='https://a.example/details'>Подробнее</a></main>");
        var details = ServeHtml("https://a.example/details", "<main><p>details</p><a href='https://a.example/start?utm_source=x'>Подробнее о вакансии</a>"
                                                            + "<a href='https://a.example/deeper'>Подробнее</a></main>");
        var deeper = ServeHtml("https://a.example/deeper", "<p>too deep</p>");
        var self = _web.When("https://t.me/jobs/1*").Respond("text/html", "");

        var result = await Crawler().CrawlAsync(
            [Link("https://a.example/start"), Link("https://a.example/start#top"), Link("https://t.me/jobs/1")], new Uri("https://t.me/jobs/1"), Ct);

        Assert.Equal(["https://a.example/start", "https://a.example/details"], result.Pages.Select(p => p.Page.Url.AbsoluteUri));
        Assert.Equal(1, _web.GetMatchCount(details));
        Assert.Equal(0, _web.GetMatchCount(deeper) + _web.GetMatchCount(self)); // depth 2 is the last; the post is not read back
    }

    [Fact]
    public async Task The_page_limit_holds_whatever_the_post_links()
    {
        for (var i = 0; i < 10; i++) ServeHtml($"https://site{i}.example/job", $"<p>job {i}</p>");

        var result = await Crawler(new EnrichmentOptions { MaxPages = 3 }).CrawlAsync(
            [.. Enumerable.Range(0, 10).Select(i => Link($"https://site{i}.example/job"))], null, Ct);

        Assert.Equal(3, result.Pages.Count);
    }

    [Fact]
    public async Task A_failing_page_costs_only_itself_and_a_blocking_host_is_left_alone()
    {
        ServeHtml("https://ok.example/job", "<p>вакансия</p>");
        _web.When("https://gone.example/job").Respond(HttpStatusCode.NotFound);
        var blocked = _web.When("https://blocks.example/*").Respond(HttpStatusCode.Forbidden);
        _web.When("https://binary.example/cv").Respond("application/pdf", "%PDF-1.7");

        var result = await Crawler().CrawlAsync(
            [Link("https://gone.example/job"), Link("https://blocks.example/a"), Link("https://blocks.example/b"), Link("https://binary.example/cv"), Link("https://ok.example/job")],
            null, Ct);

        Assert.Equal(["https://ok.example/job"], result.Pages.Select(p => p.Page.Url.AbsoluteUri));
        Assert.Equal(1, _web.GetMatchCount(blocked)); // after the 403 the host is not asked again in this crawl
        Assert.Contains(result.Failures, f => f.Contains("gone.example", StringComparison.Ordinal) && f.Contains("404", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.StartsWith("blocks.example", StringComparison.Ordinal));
        Assert.Equal(2, result.Failures.Count); // the PDF is skipped, not a failure
    }

    private sealed class FakeRenderer(string html) : IPageRenderer
    {
        public List<Uri> Rendered { get; } = [];

        public Task<(Uri Url, string Html)> RenderAsync(Uri url, string? proxy, CancellationToken ct)
        {
            Rendered.Add(url);
            return Task.FromResult((url, html));
        }
    }

    [Fact]
    public async Task A_script_built_page_is_drawn_by_the_browser_and_a_full_page_is_not()
    {
        Serve("https://aristeksystems.com/career/full-stack-net-engineer/", "aristek_career.html");
        Serve("https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25", "telegraph_alfa.html");
        var drawn = "<main><h1>Full-Stack .Net Engineer</h1>" + string.Concat(Enumerable.Repeat("<p>Разработка на .NET и React, удалённо. </p>", 30))
                    + "<p>Резюме: cv@aristeksystems.com</p></main>";
        var renderer = new FakeRenderer(drawn);

        var result = await Crawler(renderer: renderer).CrawlAsync(
            [Link("https://aristeksystems.com/career/full-stack-net-engineer/", "Откликнуться"), Link("https://telegra.ph/MIDDLE-CNET-DEVELOPER-09-25")], null, Ct);

        Assert.Equal([true, false], result.Pages.Select(p => p.Rendered));
        Assert.Contains("cv@aristeksystems.com", result.Pages[0].Page.Text);
        Assert.Single(renderer.Rendered);
    }

    [Fact]
    public async Task A_retired_vacancy_that_answers_200_is_a_failure_not_content()
    {
        ServeHtml("https://careers.example/job/old", "<html><head><title>404. Page Not Found | Careers</title></head><body><p>Sorry</p></body></html>");
        Serve("https://aristeksystems.com/career/full-stack-net-engineer/", "aristek_career.html");
        // Seen live: the script-built career site routes a retired vacancy to /404 in the browser.
        var renderer = new RedirectingRenderer(new Uri("https://aristeksystems.com/404/"),
            "<html><head><title>404. Page Not Found | Aristek Systems</title></head><body><main>" + new string('x', 600) + "</main></body></html>");

        var result = await Crawler(renderer: renderer).CrawlAsync(
            [Link("https://careers.example/job/old"), Link("https://aristeksystems.com/career/full-stack-net-engineer/")], null, Ct);

        Assert.Empty(result.Pages);
        Assert.Equal(2, result.Failures.Count(f => f.Contains("not found", StringComparison.Ordinal)));
        Assert.Contains(result.Failures, f => f.Contains("after the scripts ran", StringComparison.Ordinal));
    }

    private sealed class RedirectingRenderer(Uri final, string html) : IPageRenderer
    {
        public Task<(Uri Url, string Html)> RenderAsync(Uri url, string? proxy, CancellationToken ct) => Task.FromResult((final, html));
    }

    [Fact]
    public async Task The_time_budget_ends_the_crawl_with_what_was_read()
    {
        ServeHtml("https://fast.example/job", "<p>вакансия</p>");
        _web.When("https://slow.example/job").Respond(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), Ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("late") };
        });

        var started = DateTimeOffset.UtcNow;
        var result = await Crawler(new EnrichmentOptions { BudgetSeconds = 1 })
            .CrawlAsync([Link("https://fast.example/job"), Link("https://slow.example/job")], null, Ct);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Single(result.Pages);
        Assert.Contains(result.Failures, f => f.Contains("budget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_reads_nothing()
    {
        var any = _web.When("*").Respond("text/html", "<p>x</p>");
        var result = await Crawler(new EnrichmentOptions { Enabled = false }).CrawlAsync([Link("https://a.example/job")], null, Ct);
        Assert.Empty(result.Pages);
        Assert.Equal(0, _web.GetMatchCount(any));
    }
}
