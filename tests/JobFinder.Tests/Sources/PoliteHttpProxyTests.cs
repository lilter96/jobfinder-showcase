using System.Net;
using System.Net.Http.Headers;
using JobFinder.Integrations.Http;
using RichardSzalay.MockHttp;

namespace JobFinder.Tests.Sources;

/// <summary>
/// The proxy route (the host proxy ShadowPrompter uses) for sites this machine cannot reach, and what a crawl of arbitrary hosts must not
/// cost: bodies it cannot read, a settings write per request.
/// </summary>
public sealed class PoliteHttpProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScrapeOptions Options(params string[] proxyHosts) => new()
    {
        MinDelayMs = 0, MaxDelayMs = 1, Proxy = "http://127.0.0.1:8081", ProxyHosts = [.. proxyHosts],
        Sites = { ["hh"] = new ScrapeSiteOptions { MinDelayMs = 0, MaxDelayMs = 1, Limits = [new ScrapeLimit { Minutes = 10, Max = 5 }] } },
    };

    private static HttpRequestException Network(HttpRequestError error) => new(error, $"network: {error}");

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)] // a firewall drops the connection
    [InlineData(HttpRequestError.NameResolutionError)] // DNS says no
    [InlineData(HttpRequestError.SecureConnectionError)] // a TLS reset in the middle
    public async Task A_site_out_of_reach_is_read_through_the_proxy_and_keeps_that_route(HttpRequestError error)
    {
        var direct = new MockHttpMessageHandler();
        var viaDirect = direct.When("https://blocked.example/*").Throw(Network(error));
        var proxy = new MockHttpMessageHandler();
        proxy.When("https://blocked.example/*").Respond("text/html", "<p>vacancy</p>");
        var http = new PoliteHttp(new HttpClient(direct), Options(), TimeProvider.System, proxied: new HttpClient(proxy));

        var first = await http.GetPageAsync(new Uri("https://blocked.example/job/1"), Ct);
        var second = await http.GetPageAsync(new Uri("https://blocked.example/job/2"), Ct);

        Assert.Equal(("<p>vacancy</p>", true), (first.Body, first.ViaProxy));
        Assert.True(second.ViaProxy);
        Assert.Equal(1, direct.GetMatchCount(viaDirect)); // the direct route was tried once, then left
        Assert.True(http.UsesProxy("blocked.example"));
        Assert.Equal("http://127.0.0.1:8081", http.ProxyFor("blocked.example"));
        Assert.Null(http.ProxyFor("other.example"));
    }

    [Fact]
    public async Task A_block_is_an_answer_of_the_site_not_a_route_problem_the_proxy_is_not_asked()
    {
        var direct = new MockHttpMessageHandler();
        direct.When("*").Respond(HttpStatusCode.Forbidden);
        var proxy = new MockHttpMessageHandler();
        var viaProxy = proxy.When("*").Respond("text/html", "never");
        var http = new PoliteHttp(new HttpClient(direct), Options(), TimeProvider.System, proxied: new HttpClient(proxy));

        await Assert.ThrowsAsync<SourceBlockedException>(() => http.GetPageAsync(new Uri("https://site.example/a"), Ct));
        Assert.Equal(0, proxy.GetMatchCount(viaProxy));
    }

    [Fact]
    public async Task Configured_hosts_and_their_subdomains_always_go_through_the_proxy()
    {
        var direct = new MockHttpMessageHandler();
        var viaDirect = direct.When("*").Respond("text/html", "direct");
        var proxy = new MockHttpMessageHandler();
        proxy.When("*").Respond("text/html", "proxied");
        var http = new PoliteHttp(new HttpClient(direct), Options("alfabank.ru"), TimeProvider.System, proxied: new HttpClient(proxy));

        Assert.Equal("proxied", (await http.GetPageAsync(new Uri("https://job.alfabank.ru/vacancies/1"), Ct)).Body);
        Assert.Equal("direct", (await http.GetPageAsync(new Uri("https://notalfabank.ru/x"), Ct)).Body);
        Assert.Equal(1, direct.GetMatchCount(viaDirect));
    }

    [Fact]
    public async Task Without_a_proxy_route_the_network_failure_surfaces()
    {
        var direct = new MockHttpMessageHandler();
        direct.When("*").Throw(Network(HttpRequestError.ConnectionError));
        var http = new PoliteHttp(new HttpClient(direct), Options(), TimeProvider.System);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetPageAsync(new Uri("https://blocked.example/"), Ct));
        Assert.Equal(HttpRequestError.ConnectionError, e.HttpRequestError);
        Assert.False(http.UsesProxy("blocked.example"));
    }

    [Fact]
    public async Task A_binary_or_oversized_body_is_not_downloaded()
    {
        var handler = new MockHttpMessageHandler();
        handler.When("https://site.example/cv.pdf").Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x25, 0x50, 0x44, 0x46]) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } },
        });
        handler.When("https://site.example/huge").Respond(_ =>
        {
            var content = new StringContent(new string('x', 2048), System.Text.Encoding.UTF8, "text/html");
            content.Headers.ContentLength = 50L * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var http = new PoliteHttp(new HttpClient(handler), new ScrapeOptions { MinDelayMs = 0, MaxDelayMs = 1, MaxPageBytes = 1024 * 1024 }, TimeProvider.System);

        var pdf = await http.GetPageAsync(new Uri("https://site.example/cv.pdf"), Ct);
        Assert.Equal(("application/pdf", "", false), (pdf.MediaType, pdf.Body, pdf.IsHtml));
        Assert.Equal("", (await http.GetPageAsync(new Uri("https://site.example/huge"), Ct)).Body);
    }

    /// <summary>What the redirect handler reports after a short link: the page it ended on.</summary>
    private sealed class RedirectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://careers.example/job/1"),
                Content = new StringContent("<p>job</p>", System.Text.Encoding.UTF8, "text/html"),
            });
    }

    [Fact]
    public async Task A_short_link_reports_the_page_it_ended_on()
    {
        var http = new PoliteHttp(new HttpClient(new RedirectingHandler()), new ScrapeOptions { MinDelayMs = 0, MaxDelayMs = 1 }, TimeProvider.System);
        var page = await http.GetPageAsync(new Uri("https://clck.ru/abc"), Ct);
        Assert.Equal(("https://careers.example/job/1", "<p>job</p>", "text/html"), (page.FinalUri.AbsoluteUri, page.Body, page.MediaType));
    }

    [Fact]
    public async Task Only_a_site_with_budgets_keeps_a_request_log_a_crawled_host_costs_no_settings_write()
    {
        var store = new MemoryState();
        var handler = new MockHttpMessageHandler();
        handler.When("*").Respond("text/html", "page");
        var http = new PoliteHttp(new HttpClient(handler), Options(), TimeProvider.System, null, store);

        await http.GetPageAsync(new Uri("https://telegra.ph/some-vacancy"), Ct);
        await http.GetPageAsync(new Uri("https://rabota.by/vacancy/1"), Ct);

        Assert.False(store.Saved.ContainsKey("telegra.ph"));
        Assert.Single(store.Saved["hh"].Requests);
    }

    private sealed class MemoryState : IScrapeStateStore
    {
        public Dictionary<string, ScrapeState> Saved { get; } = [];

        public Task<ScrapeState> LoadAsync(string family, CancellationToken ct) => Task.FromResult(Saved.GetValueOrDefault(family, ScrapeState.Empty));

        public Task SaveAsync(string family, ScrapeState state, CancellationToken ct)
        {
            Saved[family] = state;
            return Task.CompletedTask;
        }
    }
}
