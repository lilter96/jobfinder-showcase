using System.Net;
using System.Text;
using JobFinder.Llm;
using JobFinder.Llm.Client;

namespace JobFinder.Tests.Llm;

/// <summary>
/// The direct route to OpenRouter and the proxy route: a connection that never got an answer (the Docker network dropping TLS for minutes,
/// seen live on 2026-09-29 while the host got through) goes once through the proxy; an HTTP answer is the model's and stays as it is.
/// </summary>
public sealed class LlmRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Completion = """{"model":"m","choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"cost":0.0}}""";

    /// <summary>The direct client fails as scripted; the proxy client answers.</summary>
    private sealed class Routes(Func<HttpResponseMessage>? direct) : IHttpClientFactory
    {
        public List<string> Used { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, name), disposeHandler: true);

        private sealed class Handler(Routes routes, string name) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                routes.Used.Add(name);
                if (name == OpenRouterTransport.DirectClient && routes.Direct is { } direct) return Task.FromResult(direct());
                if (name == OpenRouterTransport.DirectClient && routes.DirectThrows is { } error) throw error;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Completion, Encoding.UTF8, "application/json") });
            }
        }

        public Func<HttpResponseMessage>? Direct { get; } = direct;

        public HttpRequestException? DirectThrows { get; init; }
    }

    private static readonly LlmModelOptions Model = new() { Name = "m" };

    [Fact]
    public async Task A_tls_failure_on_the_direct_route_goes_once_through_the_proxy()
    {
        var routes = new Routes(null) { DirectThrows = new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established") };
        var transport = new OpenRouterTransport(routes, new LlmOptions { ApiKey = "k", Proxy = "http://host.docker.internal:8081" });

        var result = await transport.CompleteAsync(Model, "s", "u", null, Ct);

        Assert.Equal("ok", result.Content);
        Assert.Equal([OpenRouterTransport.DirectClient, OpenRouterTransport.ProxyClient], routes.Used);
    }

    [Fact]
    public async Task Without_a_proxy_the_network_failure_is_reported_as_unavailable()
    {
        var routes = new Routes(null) { DirectThrows = new HttpRequestException(HttpRequestError.ConnectionError, "refused") };
        var transport = new OpenRouterTransport(routes, new LlmOptions { ApiKey = "k" });

        var e = await Assert.ThrowsAsync<LlmCallException>(() => transport.CompleteAsync(Model, "s", "u", null, Ct));

        Assert.Equal(LlmFailureKind.Unavailable, e.Kind);
        Assert.Equal([OpenRouterTransport.DirectClient], routes.Used);
    }

    [Fact]
    public async Task An_http_answer_never_switches_the_route()
    {
        var routes = new Routes(() => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":{"message":"boom"}}""") });
        var transport = new OpenRouterTransport(routes, new LlmOptions { ApiKey = "k", Proxy = "http://host.docker.internal:8081" });

        await Assert.ThrowsAsync<LlmCallException>(() => transport.CompleteAsync(Model, "s", "u", null, Ct));

        Assert.Equal([OpenRouterTransport.DirectClient], routes.Used);
    }
}
