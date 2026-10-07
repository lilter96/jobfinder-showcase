using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Llm;

public sealed class RouteProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Requests : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Models { get; } = [];
        public List<string> Schemas { get; } = [];
        public bool WrongContract { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Models.Add(body.RootElement.GetProperty("model").GetString()!);
            var schemaName = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString()!;
            Schemas.Add(schemaName);
            var content = schemaName == LlmTaskCatalog.RecruiterMessage.Schema.Name
                ? """{"hook":"API","needs":[],"facts":[],"greeting":"Здравствуйте!","paragraphs":["Оптимизировал API"],"closing":"Обсудим задачу?"}"""
                : """{"claims":[],"projectCases":0,"natural":true,"hasReason":true,"note":"","verdict":{"checks":[],"score":10,"wouldReply":true,"weakest":"","fix":""}}""";
            var completion = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = WrongContract ? "{\"isVacancy\":true,\"isDotnet\":true,\"title\":\"Senior .NET\"}" : content },
                    ["finish_reason"] = "stop",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion.ToJsonString()) };
        }
    }

    private sealed class Usage : ILlmUsageSink
    {
        public List<LlmAttempt> Attempts { get; } = [];
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private static (JobFinderLlm Llm, OpenRouterTransport Transport, Usage Usage) Setup(Requests requests)
    {
        var options = new LlmOptions { ApiKey = "test", Routes = new()
        {
            [LlmPurpose.Application] = [new LlmModelOptions { Name = "application-model", Reasoning = new() { Effort = "xhigh" } }],
            [LlmPurpose.Resume] = [new LlmModelOptions { Name = "resume-model", Reasoning = new() { Effort = "xhigh" } }],
        } };
        var transport = new OpenRouterTransport(requests, options);
        var usage = new Usage();
        var client = new LlmClient(transport, new ModelHealth(TimeProvider.System), options, usage, NullLogger<LlmClient>.Instance);
        return (new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir)), transport, usage);
    }

    [Fact]
    public async Task A_filtered_probe_never_calls_unselected_routes()
    {
        using var requests = new Requests();
        var (llm, transport, usage) = Setup(requests);
        var results = new List<bool>();
        await foreach (var result in llm.ProbeRoutesAsync(transport, Ct, "application")) results.Add(result.Ok);
        Assert.Equal([true, true], results);
        Assert.Equal(["application-model", "application-model"], requests.Models);
        Assert.Equal([LlmTaskCatalog.RecruiterMessage.Schema.Name, LlmTaskCatalog.CheckRecruiterMessage.Schema.Name], requests.Schemas);
        Assert.Equal(2, usage.Attempts.Count);
        Assert.All(usage.Attempts, attempt => Assert.True(attempt.Ok));
        Assert.Single(usage.Attempts.Select(attempt => attempt.OperationId).Distinct());
        Assert.Equal(2, usage.Attempts.Select(attempt => attempt.CallId).Distinct().Count());
        Assert.Equal(LlmTaskCatalog.All.Where(task => task.Purposes.Contains(LlmPurpose.Application)).Select(task => task.Id),
            usage.Attempts.Select(attempt => Assert.Single(attempt.Recipe!.Tasks).TaskId));
    }

    [Fact]
    public async Task A_plausible_extraction_response_does_not_pass_an_application_probe()
    {
        using var requests = new Requests { WrongContract = true };
        var (llm, transport, usage) = Setup(requests);
        var results = new List<bool>();
        await foreach (var result in llm.ProbeRoutesAsync(transport, Ct, LlmPurpose.Application)) results.Add(result.Ok);
        Assert.Equal([false, false], results);
        Assert.Equal(2, usage.Attempts.Count);
        Assert.All(usage.Attempts, attempt => Assert.False(attempt.Ok));
        Assert.All(usage.Attempts, attempt => Assert.Equal(1, attempt.PromptTokens));
    }

    [Fact]
    public async Task An_unknown_route_fails_without_spending_on_other_routes()
    {
        using var requests = new Requests();
        var (llm, transport, usage) = Setup(requests);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var result in llm.ProbeRoutesAsync(transport, Ct, "typo")) Assert.Fail("Unexpected probe");
        });
        Assert.Empty(requests.Models);
        Assert.Empty(usage.Attempts);
    }
}
