using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Profile;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class RejectionReviewStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(RejectionReviewSteps Steps, RejectionReviewInputSnapshot Input)> SetupAsync(Api api)
    {
        var options = new LlmOptions { ApiKey = "synthetic-key", Endpoint = "https://provider.invalid/completions", Proxy = "", MaxAttempts = 1,
            Routes = LlmPurpose.All.ToDictionary(purpose => purpose, purpose => new List<LlmModelOptions> { new() { Name = "synthetic-model", Reasoning = new() { Effort = "max" } } }) };
        var llm = new JobFinderLlm(new LlmClient(new OpenRouterTransport(api, options), new ModelHealth(TimeProvider.System), options,
            new Usage(), NullLogger<LlmClient>.Instance), new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var input = new RejectionReviewInputSnapshot
        {
            ProfileJson = ProfileProvider.ToJson(new CandidateProfile { Name = "Original Candidate", Summary = "Captured original evidence" }),
            ProfileVersion = 1, ResumeText = "Frozen resume text", FactsText = "Frozen response facts", SourceJson = "[]", Responses = 5, Rejections = 5,
            CapturedAt = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero), Operation = operation.CaptureOperation(),
        };
        input.Recipe = RejectionReviewSteps.Recipe(operation, input);
        return (new(llm, TimeProvider.System), input);
    }

    [Fact]
    public async Task Actual_rejection_adapter_uses_frozen_inputs_maximum_reasoning_and_strict_review_contract()
    {
        using var api = new Api();
        var (steps, input) = await SetupAsync(api);
        var result = await steps.AnalyzeAsync(input, Ct);
        Assert.Equal("Evidence-based review", result.Review.Summary);
        Assert.Single(api.Requests);
        var request = api.Requests[0];
        Assert.Contains("Original Candidate", request);
        Assert.Contains("Frozen resume text", request);
        Assert.Contains("Frozen response facts", request);
        using var json = JsonDocument.Parse(request);
        Assert.Equal("max", json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("json_schema", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("synthetic-model", result.Model);
        Assert.DoesNotContain("synthetic-key", JsonSerializer.Serialize(input, RejectionReviewJsonContext.Default.RejectionReviewInputSnapshot));
    }

    [Fact]
    public async Task Rejection_adapter_provider_outage_is_resumable_not_a_placeholder_review()
    {
        using var api = new Api { Unavailable = true };
        var (steps, input) = await SetupAsync(api);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => steps.AnalyzeAsync(input, Ct));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Rejection_adapter_changed_recipe_refuses_paid_work_before_contacting_provider()
    {
        using var api = new Api();
        var (steps, input) = await SetupAsync(api);
        input.FactsText = "Forged new evidence";
        await Assert.ThrowsAsync<RejectionReviewSupersededException>(() => steps.AnalyzeAsync(input, Ct));
        Assert.Empty(api.Requests);
    }

    private sealed class Usage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];
        public bool Unavailable;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("provider.invalid", request.RequestUri!.Host);
            if (Unavailable) return new(System.Net.HttpStatusCode.ServiceUnavailable);
            var review = new RejectionReviewWire { Summary = "Evidence-based review" };
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = JsonSerializer.Serialize(review, GeneratedWireJsonContext.Default.RejectionReviewWire) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
