using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class ReplyProcessingStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ReplyProcessingSteps Steps, ReplyProcessingInput Input, LlmOptions Options)> SetupAsync(Http http)
    {
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        options.MaxAttempts = 1;
        options.Routes[LlmPurpose.Reply] = [new() { Name = "frozen-fast", Reasoning = new() { Effort = "max" } }];
        options.Routes[LlmPurpose.ReplyDeep] = [new() { Name = "frozen-deep", Reasoning = new() { Effort = "max" } }];
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var llm = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var input = new ReplyProcessingInput
        {
            ApplicationId = 1, Vacancy = AssessmentVacancy.From(new Vacancy { Id = 2, Title = "Synthetic backend", Description = "Captured vacancy",
                Url = "https://vacancy.invalid/2", Fingerprint = "synthetic" }),
            Profile = new CandidateProfile { Name = "Synthetic Candidate", Summary = "Captured candidate experience" },
            Facts = new CandidateFacts { Facts = [new() { Key = "location", Question = "Location", Answer = "Captured Minsk", Confirmed = true }] },
            Stories = "Captured project story", Message = "Captured incoming question", Conversation = "Captured previous dialogue",
            CapturedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(3)), Operation = operation.CaptureOperation(),
        };
        input.Recipe = ReplyProcessingSteps.Recipe(operation, input);
        return (new ReplyProcessingSteps(llm), input, options);
    }

    [Fact]
    public async Task Fast_and_deep_use_the_same_frozen_inputs_and_routes_and_deep_receives_checkpoint_feedback()
    {
        using var http = new Http();
        var (steps, input, options) = await SetupAsync(http);
        options.Routes[LlmPurpose.Reply][0].Name = "changed-bootstrap";
        options.Routes[LlmPurpose.ReplyDeep][0].Name = "changed-bootstrap-deep";
        var best = await steps.AnalyzeAsync(input, null, false, Ct);
        best.Problems.Add("Specific checkpoint feedback");
        var saved = ReplyProcessingRequests.ReadResult(ReplyProcessingRequests.Serialize(best));
        await steps.AnalyzeAsync(input, saved, true, Ct);
        Assert.Equal(new[] { "frozen-fast", "frozen-deep" }, http.Requests.Select(request => request["model"]!.GetValue<string>()));
        Assert.All(http.Requests, request =>
        {
            Assert.Equal("max", request["reasoning"]!["effort"]!.GetValue<string>());
            var content = string.Join("\n", request["messages"]!.AsArray().Select(message => message!["content"]!.GetValue<string>()));
            foreach (var captured in new[] { input.Profile.Summary, "Captured Minsk", input.Stories, input.Message, input.Conversation, input.Vacancy.Description })
                Assert.Contains(captured!, content);
            Assert.Contains("2026-10-03", content);
        });
        Assert.Contains("Specific checkpoint feedback", http.Requests.Last()["messages"]!.ToJsonString());
    }

    [Fact]
    public async Task Changed_recipe_inputs_are_refused_without_a_paid_request()
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        input.Message = "Changed incoming question";
        await Assert.ThrowsAsync<ReplyProcessingSupersededException>(() => steps.AnalyzeAsync(input, null, false, Ct));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_or_semantic_failure_never_becomes_a_completed_unclassified_reply(bool invalid)
    {
        using var http = new Http { Unavailable = !invalid, Invalid = invalid };
        var (steps, input, _) = await SetupAsync(http);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => steps.AnalyzeAsync(input, null, false, Ct));
        Assert.Single(http.Requests);
    }

    private sealed class Usage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public List<JsonObject> Requests { get; } = [];
        public bool Unavailable { get; set; }
        public bool Invalid { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if (Unavailable) return new(HttpStatusCode.ServiceUnavailable);
            var analysis = new ReplyAnalysis { Classification = "other", Summary = Invalid ? "" : "Synthetic summary",
                Intent = "small_talk", Counterpart = "human", Draft = "Спасибо, буду ждать ответа." };
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject
                    { ["content"] = JsonSerializer.Serialize(analysis, CareerJsonContext.Default.ReplyAnalysis) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
