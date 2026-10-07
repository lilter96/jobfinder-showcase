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

public sealed class WeeklyReportStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(WeeklyReportSteps Steps, WeeklyReportInput Input)> SetupAsync(Api api, bool advice = true)
    {
        var options = new LlmOptions { ApiKey = "synthetic-key", Endpoint = "https://provider.invalid/completions", Proxy = "", MaxAttempts = 1,
            Routes = LlmPurpose.All.ToDictionary(purpose => purpose, purpose => new List<LlmModelOptions> { new() { Name = "synthetic-model", Reasoning = new() { Effort = "max" } } }) };
        var llm = new JobFinderLlm(new LlmClient(new OpenRouterTransport(api, options), new ModelHealth(TimeProvider.System), options,
            new Usage(), NullLogger<LlmClient>.Instance), new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var input = new WeeklyReportInput
        {
            ProfileJson = ProfileProvider.ToJson(new CandidateProfile { Name = "Original Candidate", Summary = "Captured original evidence" }),
            ProfileVersion = 1, ResumeText = "Frozen resume text",
            Frame = new() { HeaderHtml = "Captured weekly statistics", CostHtml = "\nCaptured cost", Sent = 3,
                DemandText = "Frozen demand facts", FunnelText = "Frozen funnel facts", HasDemand = advice },
            CapturedAt = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero), Operation = operation.CaptureOperation(),
        };
        input.Recipe = WeeklyReportSteps.Recipe(operation, input);
        return (new(llm, TimeProvider.System), input);
    }

    [Fact]
    public async Task Actual_weekly_adapter_uses_frozen_inputs_maximum_reasoning_and_strict_advice_contract()
    {
        using var api = new Api();
        var (steps, input) = await SetupAsync(api);
        var result = await steps.PrepareAsync(input, Ct);
        Assert.Equal("Evidence-based review", result.Advice!.Title);
        Assert.Single(api.Requests);
        var request = api.Requests[0];
        Assert.DoesNotContain("Original Candidate", request);
        Assert.Contains("Frozen resume text", request);
        Assert.Contains("Frozen demand facts", request);
        Assert.Contains("Frozen funnel facts", request);
        using var json = JsonDocument.Parse(request);
        Assert.Equal("max", json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("json_schema", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("synthetic-model", result.Model);
        Assert.DoesNotContain("synthetic-key", JsonSerializer.Serialize(input, WeeklyReportJsonContext.Default.WeeklyReportInput));
    }

    [Fact]
    public async Task Actual_weekly_adapter_without_demand_returns_frozen_statistics_without_a_provider_call()
    {
        using var api = new Api();
        var (steps, input) = await SetupAsync(api, advice: false);
        var result = await steps.PrepareAsync(input, Ct);
        Assert.Empty(api.Requests);
        Assert.Null(result.Advice);
        Assert.Equal(3, result.Sent);
        Assert.Equal(WeeklyReportSteps.Compose(input.Frame, null), result.Html);
    }

    [Fact]
    public async Task Weekly_adapter_provider_outage_is_resumable_not_a_placeholder_review()
    {
        using var api = new Api { Unavailable = true };
        var (steps, input) = await SetupAsync(api);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => steps.PrepareAsync(input, Ct));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Weekly_adapter_changed_recipe_refuses_paid_work_before_contacting_provider()
    {
        using var api = new Api();
        var (steps, input) = await SetupAsync(api);
        input.Frame.DemandText = "Forged new evidence";
        await Assert.ThrowsAsync<WeeklyReportSupersededException>(() => steps.PrepareAsync(input, Ct));
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
            var review = new HhResumeAdviceWire { Title = "Evidence-based review", About = "Supported candidate evidence" };
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = JsonSerializer.Serialize(review, GeneratedWireJsonContext.Default.HhResumeAdviceWire) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
