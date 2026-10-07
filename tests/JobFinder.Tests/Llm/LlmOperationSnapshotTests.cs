using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Llm;

public sealed class LlmOperationSnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LlmClient Client(LlmOptions options, Http http, Usage usage, ILlmPolicySource? source = null) =>
        new(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, usage, NullLogger<LlmClient>.Instance, source);

    [Fact]
    public async Task A_restored_operation_keeps_its_policy_identity_and_cost_provenance_without_persisting_deployment_secrets()
    {
        var options = LlmRuntimePolicyTests.Bootstrap();
        var policy = new Source(LlmRuntimePolicyCompiler.Import(options));
        using var http = new Http();
        var usage = new Usage();
        var operation = await Client(options, http, usage, policy).ForOperationAsync(Ct);
        var snapshot = operation.CaptureOperation();
        var json = JsonSerializer.Serialize(snapshot, RuntimePolicyJsonContext.Default.LlmOperationSnapshot);
        Assert.DoesNotContain(options.ApiKey, json);
        Assert.DoesNotContain(options.Endpoint, json);
        Assert.DoesNotContain(options.Proxy!, json);
        policy.Policy.Models.Values.Single().Name = "published-later-model";
        policy.Version++;
        var currentDeployment = LlmRuntimePolicyTests.Bootstrap();
        currentDeployment.ApiKey = "synthetic-rotated-deployment-key";
        currentDeployment.Endpoint = "https://example.org/new-endpoint";
        currentDeployment.Routes.Clear();
        var restored = Client(currentDeployment, http, usage, policy).RestoreOperation(
            JsonSerializer.Deserialize(json, RuntimePolicyJsonContext.Default.LlmOperationSnapshot)!);
        await restored.CompleteTextAsync(LlmPurpose.Resume, "system", "input", Ct);
        var request = Assert.Single(http.Requests);
        Assert.Equal(currentDeployment.ApiKey, request.Key);
        Assert.Equal(currentDeployment.Endpoint, request.Url);
        Assert.Equal("shared-model", request.Body["model"]!.ToString());
        Assert.Equal("xhigh", request.Body["reasoning"]!["effort"]!.ToString());
        Assert.Equal(32000, request.Body["max_tokens"]!.GetValue<int>());
        Assert.Equal(1, policy.Reads);
        Assert.Equal(snapshot.PolicyRevision, restored.PolicyRevision);
        Assert.Equal(snapshot.OperationId, restored.OperationId);
        var attempt = Assert.Single(usage.Attempts);
        Assert.Equal(snapshot.OperationId, attempt.OperationId);
        Assert.Equal(0.001m, attempt.CostUsd);
    }

    [Fact]
    public async Task Bootstrap_operations_and_every_exported_snapshot_are_detached_from_mutable_callers()
    {
        var options = LlmRuntimePolicyTests.Bootstrap();
        using var http = new Http();
        var operation = await Client(options, http, new Usage()).ForOperationAsync(Ct);
        var snapshot = operation.CaptureOperation();
        options.Routes[LlmPurpose.Resume][0].Name = "changed-bootstrap";
        snapshot.Policy.Models.Values.Single().Name = "changed-export";
        snapshot.Policy.Routes[LlmPurpose.Resume].Steps[0].Reasoning!.Effort = "none";
        await operation.CompleteTextAsync(LlmPurpose.Resume, "system", "input", Ct);
        Assert.Equal("shared-model", Assert.Single(http.Requests).Body["model"]!.ToString());
        Assert.Equal("shared-model", operation.CaptureOperation().Policy.Models.Values.Single().Name);
        var exported = operation.CaptureOperation();
        var restored = Client(LlmRuntimePolicyTests.Bootstrap(), http, new Usage()).RestoreOperation(exported);
        exported.Policy.Models.Values.Single().Name = "changed-after-restore";
        Assert.Equal("shared-model", restored.CaptureOperation().Policy.Models.Values.Single().Name);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("identity")]
    [InlineData("revision")]
    [InlineData("weak-effort")]
    [InlineData("missing-policy")]
    public async Task Corrupt_or_weakened_snapshots_are_rejected_before_any_HTTP(string invalid)
    {
        using var http = new Http();
        var root = Client(LlmRuntimePolicyTests.Bootstrap(), http, new Usage());
        var snapshot = (await root.ForOperationAsync(Ct)).CaptureOperation();
        switch (invalid)
        {
            case "schema": snapshot.SchemaVersion = 2; break;
            case "identity": snapshot.OperationId = Guid.Empty; break;
            case "revision": snapshot.PolicyRevision = -1; break;
            case "weak-effort": snapshot.Policy.Routes[LlmPurpose.Resume].Steps[0].Reasoning!.Effort = "none"; break;
            case "missing-policy": snapshot.Policy = null!; break;
        }
        Assert.Throws<LlmPolicyException>(() => root.RestoreOperation(snapshot));
        Assert.Empty(http.Requests);
        Assert.Throws<InvalidOperationException>(() => root.CaptureOperation());
    }

    private sealed class Source(LlmRuntimePolicy policy) : ILlmPolicySource
    {
        public LlmRuntimePolicy Policy { get; } = policy;
        public long Version { get; set; } = 7;
        public int Reads { get; private set; }
        public Task<LlmPolicySnapshot> ReadAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new LlmPolicySnapshot(LlmRuntimePolicyCompiler.Clone(Policy), Version));
        }
    }

    private sealed class Usage : ILlmUsageSink
    {
        public ConcurrentQueue<LlmAttempt> Attempts { get; } = new();
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) { Attempts.Enqueue(attempt); return Task.CompletedTask; }
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<(string Key, string Url, JsonObject Body)> Requests { get; } = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue((request.Headers.Authorization!.Parameter!, request.RequestUri!.ToString(),
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject()));
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"model":"provider-model","choices":[{"message":{"content":"Synthetic reviewed content"},"finish_reason":"stop"}],
                 "usage":{"prompt_tokens":100,"completion_tokens":50,"cost":0.001}}
                """) };
        }
    }
}
