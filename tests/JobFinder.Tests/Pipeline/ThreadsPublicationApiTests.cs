using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Integrations.Threads;

namespace JobFinder.Tests.Pipeline;

public sealed class ThreadsPublicationApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ThreadsPost Target = new("901", "author", "Original post", null, "https://www.threads.com/@author/post/original");
    private static readonly ThreadsPublicationRoute Route = new() { Mode = ThreadsPublicationMode.Api, Endpoint = "https://graph.threads.net/v1.0" };
    private static readonly ThreadsPreparedPublication Ready = new() { State = ThreadsPreparationState.Ready, AccountId = "101", Username = "candidate", ContainerId = "202" };

    [Fact]
    public async Task Preparation_creates_only_an_unpublished_container_and_resumes_its_status_without_recreation()
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var client = new ThreadsApiClient(http, new() { AccessToken = "synthetic-secret" });
        var created = await client.PreparePublicationAsync(Target, "Полный текст комментария", Route, null, Ct);
        Assert.Equal(ThreadsPreparationState.Waiting, created.State);
        Assert.Equal("202", created.ContainerId);
        Assert.Equal("101", created.AccountId);
        var resumed = await client.PreparePublicationAsync(Target, "Полный текст комментария", Route, created, Ct);
        Assert.Equal(ThreadsPreparationState.Ready, resumed.State);
        var create = Assert.Single(handler.Requests, request => request.Path.EndsWith("/me/threads", StringComparison.Ordinal));
        Assert.Contains("auto_publish_text=false", create.Body);
        Assert.Contains("reply_to_id=901", create.Body);
        Assert.DoesNotContain(handler.Requests, request => request.Path.EndsWith("threads_publish", StringComparison.Ordinal));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer synthetic-secret", request.Authorization);
            Assert.DoesNotContain("synthetic-secret", request.Path + request.Body);
        });
    }

    [Theory]
    [InlineData("FINISHED", ThreadsPreparationState.Ready)]
    [InlineData("IN_PROGRESS", ThreadsPreparationState.Waiting)]
    [InlineData("ERROR", ThreadsPreparationState.NotSent)]
    [InlineData("EXPIRED", ThreadsPreparationState.NotSent)]
    [InlineData("PUBLISHED", ThreadsPreparationState.Unknown)]
    public async Task Saved_container_status_is_not_a_publication_receipt(string status, ThreadsPreparationState expected)
    {
        using var handler = new ApiHandler { ContainerStatus = status };
        using var http = new HttpClient(handler);
        var client = new ThreadsApiClient(http, new() { AccessToken = "synthetic-secret" });
        Assert.Equal(expected, (await client.PreparePublicationAsync(Target, "Комментарий", Route, Ready, Ct)).State);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("http400")]
    [InlineData("http500")]
    [InlineData("json")]
    [InlineData("missing")]
    [InlineData("invalid-id")]
    public async Task Every_unreliable_response_after_publication_is_unknown_not_retryable(string failure)
    {
        using var handler = new ApiHandler { PublicationFailure = failure };
        using var http = new HttpClient(handler);
        var client = new ThreadsApiClient(http, new() { AccessToken = "synthetic-secret" });
        var receipt = await client.PublishPreparedAsync(Route, Ready, _ => Task.FromResult(true), Ct);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, receipt.Status);
        Assert.False(receipt.Retryable);
        Assert.Null(receipt.ExternalId);
        Assert.DoesNotContain("synthetic-secret", receipt.Error ?? "");
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Permission_is_checked_before_the_only_publication_POST_and_a_real_id_is_required()
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var client = new ThreadsApiClient(http, new() { AccessToken = "synthetic-secret" });
        var denied = await client.PublishPreparedAsync(Route, Ready, _ => Task.FromResult(false), Ct);
        Assert.Equal(DeliveryAttemptStatus.KnownNotSent, denied.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
        var calls = 0;
        var sent = await client.PublishPreparedAsync(Route, Ready, _ => { calls++; return Task.FromResult(true); }, Ct);
        Assert.Equal(1, calls);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, sent.Status);
        Assert.Equal("303", sent.ExternalId);
        Assert.Equal("creation_id=202", Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post).Body);
    }

    [Fact]
    public async Task Account_change_and_cancellation_before_effect_are_known_not_sent()
    {
        using var handler = new ApiHandler { Username = "other" };
        using var http = new HttpClient(handler);
        var client = new ThreadsApiClient(http, new() { AccessToken = "synthetic-secret" });
        Assert.Equal(DeliveryAttemptStatus.KnownNotSent, (await client.PublishPreparedAsync(Route, Ready, _ => Task.FromResult(true), Ct)).Status);
        handler.Username = "candidate";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = await client.PublishPreparedAsync(Route, Ready, _ => { stop.Cancel(); return Task.FromResult(true); }, stop.Token);
        Assert.Equal(DeliveryAttemptStatus.KnownNotSent, cancelled.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("author")]
    [InlineData("post")]
    [InlineData("text")]
    public async Task A_numeric_browser_shortcode_or_changed_post_cannot_prepare_a_reply_to_a_different_API_target(string changed)
    {
        using var handler = new ApiHandler
        {
            TargetUsername = changed == "author" ? "different" : "author",
            TargetPermalink = changed == "post" ? "https://www.threads.com/@author/post/different" : Target.Url,
            TargetText = changed == "text" ? "Edited original post" : Target.Text,
        };
        using var http = new HttpClient(handler);
        var client = new ThreadsApiClient(http, new() { AccessToken = "synthetic-secret" });
        Assert.Equal(ThreadsPreparationState.NotSent, (await client.PreparePublicationAsync(Target, "Комментарий", Route, null, Ct)).State);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_transient_preparation_outage_resumes_without_treating_an_empty_checkpoint_as_an_account_change()
    {
        using var handler = new ApiHandler { AccountUnavailable = true };
        using var http = new HttpClient(handler);
        var client = new ThreadsApiClient(http, new() { AccessToken = "synthetic-secret" });
        var unavailable = await client.PreparePublicationAsync(Target, "Комментарий", Route, null, Ct);
        Assert.Equal(ThreadsPreparationState.Waiting, unavailable.State);
        handler.AccountUnavailable = false;
        var resumed = await client.PreparePublicationAsync(Target, "Комментарий", Route, unavailable, Ct);
        Assert.Equal("202", resumed.ContainerId);
        Assert.Equal("candidate", resumed.Username);
    }

    [Theory]
    [InlineData("https://other.invalid/v1.0")]
    [InlineData("http://graph.threads.net/v1.0")]
    [InlineData("https://graph.threads.net/v1.0?token=x")]
    [InlineData("https://user:password@graph.threads.net/v1.0")]
    public async Task Unsafe_API_routes_never_receive_a_token(string endpoint)
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var client = new ThreadsApiClient(http, new() { AccessToken = "synthetic-secret", ApiBase = endpoint });
        var route = Route with { Endpoint = endpoint };
        Assert.Equal(ThreadsPreparationState.Waiting, (await client.PreparePublicationAsync(Target, "Комментарий", route, null, Ct)).State);
        Assert.Equal(DeliveryAttemptStatus.KnownNotSent, (await client.PublishPreparedAsync(route, Ready, _ => Task.FromResult(true), Ct)).Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://graph.threads.net/v1.0/", "https://graph.threads.net/v1.0")]
    [InlineData("https://graph.threads.com/v1.0", "https://graph.threads.com/v1.0")]
    [InlineData("https://user:synthetic-secret@graph.threads.net/v1.0", "")]
    [InlineData("https://graph.threads.net/v1.0?token=synthetic-secret", "")]
    [InlineData("https://graph.threads.net/v1.0#synthetic-secret", "")]
    [InlineData("https://other.invalid/v1.0", "")]
    public void Captured_API_routes_never_persist_endpoint_credentials(string endpoint, string expected)
    {
        var route = ThreadsPublicationRoute.Api(endpoint);
        Assert.Equal(ThreadsPublicationMode.Api, route.Mode);
        Assert.Equal(expected, route.Endpoint);
        Assert.DoesNotContain("synthetic-secret", System.Text.Json.JsonSerializer.Serialize(route));
    }

    private sealed class ApiHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, string Authorization)> Requests { get; } = [];
        public string Username { get; set; } = "candidate";
        public string ContainerStatus { get; set; } = "FINISHED";
        public bool AccountUnavailable { get; set; }
        public string TargetUsername { get; set; } = "author";
        public string TargetPermalink { get; set; } = Target.Url;
        public string TargetText { get; set; } = Target.Text;
        public string? PublicationFailure { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString() ?? ""));
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/me", StringComparison.Ordinal) && AccountUnavailable) throw new HttpRequestException("synthetic account outage");
            if (path.EndsWith("threads_publish", StringComparison.Ordinal))
            {
                if (PublicationFailure == "network") throw new HttpRequestException("synthetic-secret must not leak");
                var code = PublicationFailure switch { "http400" => HttpStatusCode.BadRequest, "http500" => HttpStatusCode.InternalServerError, _ => HttpStatusCode.OK };
                var body = PublicationFailure switch { "json" => "synthetic-secret", "missing" => "{}", "invalid-id" => "{\"id\":\"not-an-id\"}", _ => "{\"id\":\"303\"}" };
                return new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
            var node = path.EndsWith("/901", StringComparison.Ordinal) ? new JsonObject { ["id"] = "901", ["username"] = TargetUsername, ["permalink"] = TargetPermalink, ["text"] = TargetText }
                : path.EndsWith("/me", StringComparison.Ordinal) ? new JsonObject { ["id"] = "101", ["username"] = Username }
                : request.Method == HttpMethod.Post ? new JsonObject { ["id"] = "202" } : new JsonObject { ["status"] = ContainerStatus };
            return new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
