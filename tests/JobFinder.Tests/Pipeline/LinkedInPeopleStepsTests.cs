using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class LinkedInPeopleStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LinkedInPeopleSteps Steps, LinkedInPeopleInput Input, LlmOptions Options)> SetupAsync(Http http)
    {
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        options.MaxAttempts = 1;
        options.Routes[LlmPurpose.Extract] = [new() { Name = "frozen-extract", Reasoning = new() { Effort = "max" } }];
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var llm = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var input = new LinkedInPeopleInput { Company = "Example", Operation = operation.CaptureOperation(),
            Cards = [new() { ProfileUrl = "https://www.linkedin.com/in/synthetic-person", Text = "Anna Synthetic | Recruiter at Example" }] };
        input.Recipe = LinkedInPeopleSteps.Recipe(operation, input);
        return (new LinkedInPeopleSteps(llm), input, options);
    }

    [Fact]
    public async Task Paid_adapter_uses_captured_route_company_and_page_with_the_typed_contract()
    {
        using var http = new Http();
        var (steps, input, options) = await SetupAsync(http);
        options.Routes[LlmPurpose.Extract][0].Name = "changed-live-route";
        var result = await steps.ReadAsync(input, Ct);
        Assert.Single(result.Reading!.People);
        var request = Assert.Single(http.Requests);
        Assert.Equal("frozen-extract", request["model"]!.GetValue<string>());
        Assert.Equal("max", request["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", request["response_format"]!["type"]!.GetValue<string>());
        var text = request["messages"]!.ToJsonString();
        Assert.Contains("Anna Synthetic", text);
        Assert.Contains("Example", text);
    }

    [Fact]
    public async Task Changed_company_or_page_recipe_is_refused_before_paid_reading()
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        input.Company = "Changed company";
        await Assert.ThrowsAsync<UnsupportedLinkedInPeopleException>(() => steps.ReadAsync(input, Ct));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Empty_card_page_is_explicit_rule_only_and_makes_no_paid_request()
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        input.Cards.Clear();
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        options.Routes[LlmPurpose.Extract] = [new() { Name = "frozen-extract", Reasoning = new() { Effort = "max" } }];
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var llm = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        input.Recipe = LinkedInPeopleSteps.Recipe(llm.RestoreOperation(input.Operation), input);
        Assert.Null((await steps.ReadAsync(input, Ct)).Reading);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Provider_outage_is_a_resumable_failure_not_an_unreviewed_rule_fallback()
    {
        using var http = new Http { Unavailable = true };
        var (steps, input, _) = await SetupAsync(http);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => steps.ReadAsync(input, Ct));
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
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if (Unavailable) return new(HttpStatusCode.ServiceUnavailable);
            var reading = new LinkedInPeople { People = [new() { Index = 1, Name = "Anna Synthetic", CurrentCompany = "Example",
                CurrentRole = "Recruiter", Headline = "Recruiter at Example", IsResult = true, WorksAtTarget = true, IsRecruiter = true }] };
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject
                    { ["content"] = JsonSerializer.Serialize(reading, CareerJsonContext.Default.LinkedInPeople) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
