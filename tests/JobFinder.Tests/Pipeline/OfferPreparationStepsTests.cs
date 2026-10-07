using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class OfferPreparationStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(OfferPreparationSteps Steps, OfferPreparationInput Input, LlmOptions Options)> SetupAsync(Http http)
    {
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        options.MaxAttempts = 1;
        options.Routes[LlmPurpose.Resume] = [new() { Name = "frozen-offer", Reasoning = new() { Effort = "max" } }];
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var llm = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var input = new OfferPreparationInput { Operation = operation.CaptureOperation(), CapturedAt = DateTimeOffset.UtcNow,
            Source = new() { PublicationId = operation.CaptureOperation().OperationId, Text = "Synthetic Company: 100000 RUB gross", ProfileVersion = 1, FactsVersion = 1,
                Profile = new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true, Summary = "Supported facts" } },
            MarketText = "Captured market median 3000 USD", OtherOffersText = "No other offers",
            Conversion = new() { NetShareOfGross = 0.87m, RatesToUsd = new() { ["RUB"] = 0.01m } } };
        input.Recipe = OfferPreparationSteps.Recipe(operation, input);
        return (new(llm), input, options);
    }
    private static OfferPreparationRequest Root(OfferPreparationInput input, OfferPreparationResult? result = null)
    {
        var json = JsonSerializer.Serialize(input, OfferPreparationJsonContext.Default.OfferPreparationInput);
        var output = result is null ? null : JsonSerializer.Serialize(result, OfferPreparationJsonContext.Default.OfferPreparationResult);
        return new() { Id = input.Source.PublicationId, BusinessKey = "offer-manual:" + input.Source.PublicationId.ToString("N"),
            InputJson = json, InputHash = CanonicalJson.Hash(json), ResultJson = output, ResultHash = output is null ? null : CanonicalJson.Hash(output), Stage = OfferPreparationStage.Analyzing };
    }

    [Fact]
    public async Task Adapter_uses_frozen_maximum_route_offer_profile_market_and_comparisons_and_conversion_basis()
    {
        using var http = new Http();
        var (steps, input, options) = await SetupAsync(http);
        options.Routes[LlmPurpose.Resume][0].Name = "changed-live-route";
        var result = await steps.AnalyzeAsync(input, Ct);
        Assert.Equal(870m, result.MonthlyNetUsd);
        Assert.Equal("Synthetic Company", OfferPreparationRequests.ReadResult(Root(input, result)).Analysis.Company);
        var request = Assert.Single(http.Requests);
        Assert.Equal("frozen-offer", request["model"]!.GetValue<string>());
        Assert.Equal("max", request["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", request["response_format"]!["type"]!.GetValue<string>());
        var text = request["messages"]!.ToJsonString();
        Assert.Contains("Supported facts", text);
        Assert.Contains("100000 RUB gross", text);
        Assert.Contains("Captured market median", text);
        Assert.Contains("No other offers", text);
        Assert.DoesNotContain('—', result.Analysis.CounterScript);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("market")]
    [InlineData("comparison")]
    [InlineData("conversion")]
    [InlineData("profile")]
    public async Task Changed_frozen_recipe_is_refused_before_a_paid_call(string changed)
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        if (changed == "text") input.Source.Text += "changed";
        if (changed == "market") input.MarketText += "changed";
        if (changed == "comparison") input.OtherOffersText += "changed";
        if (changed == "conversion") input.Conversion.NetShareOfGross = .5m;
        if (changed == "profile") input.Source.Profile.Summary += "changed";
        await Assert.ThrowsAsync<OfferPreparationSupersededException>(() => steps.AnalyzeAsync(input, Ct));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Provider_outage_has_no_rule_based_negotiation_fallback()
    {
        using var http = new Http { Unavailable = true };
        var (steps, input, _) = await SetupAsync(http);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => steps.AnalyzeAsync(input, Ct));
        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("unreviewed")]
    [InlineData("rates")]
    [InlineData("market")]
    [InlineData("null-terms")]
    [InlineData("net")]
    public async Task Checkpoint_readers_refuse_semantically_damaged_inputs_and_results_even_with_fresh_hashes(string damage)
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        var result = await steps.AnalyzeAsync(input, Ct);
        if (damage == "version") input.Version++;
        if (damage == "identity") input.Operation.OperationId = Guid.NewGuid();
        if (damage == "unreviewed") input.Source.Profile.Reviewed = false;
        if (damage == "rates") input.Conversion.RatesToUsd["RUB"] = -1;
        if (damage == "market") input.Market = new() { Count = 2, Sorted = [1000] };
        if (damage == "null-terms") result.Analysis.Terms = null!;
        if (damage == "net") result.MonthlyNetUsd = 1;
        Assert.Throws<UnsupportedOfferPreparationException>(() => OfferPreparationRequests.ReadResult(Root(input, result)));
    }

    [Fact]
    public async Task Presentation_preserves_negotiation_escaping_and_copy_only_behavior_without_an_empty_market_division()
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        var result = await steps.AnalyzeAsync(input, Ct);
        input.Market = new();
        result.Analysis.Company = "<Synthetic & Company>";
        result.Analysis.Currency = "<script>";
        var card = OfferPresentation.Render(input, result, null);
        Assert.Contains("&lt;Synthetic &amp; Company&gt;", card.Text);
        Assert.Contains("&lt;script&gt;", card.Text);
        Assert.Contains("Уточнить до подписания", card.Text);
        Assert.Contains("Не юридическая консультация", card.Text);
        Assert.Contains("скопируйте", card.Text);
        Assert.Null(card.Buttons);
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
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if (Unavailable) return new(HttpStatusCode.ServiceUnavailable);
            var analysis = new OfferAnalysis { Company = "Synthetic Company", Verdict = "Synthetic assessment", MonthlyAmount = 100000, Currency = "RUB", Gross = true,
                Terms = [new() { Name = "Work", Value = "Remote" }], Risks = ["Verify contract"], QuestionsToClarify = ["How is the bonus paid?"],
                CounterTarget = "110000 RUB", WalkAway = "95000 RUB", CounterScript = "Synthetic — counteroffer" };
            var response = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject
                { ["content"] = JsonSerializer.Serialize(analysis, CareerJsonContext.Default.OfferAnalysis) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 } };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
