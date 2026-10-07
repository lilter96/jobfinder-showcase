using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class MockPreparationStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(MockPreparationSteps Steps, MockPreparationInput Input, LlmOptions Options)> SetupAsync(Http http, MockPreparationKind kind)
    {
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        options.MaxAttempts = 1;
        options.Routes[LlmPurpose.Coach] = [new() { Name = "frozen-mock", Reasoning = new() { Effort = "max" } }];
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var llm = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var id = operation.CaptureOperation().OperationId;
        var turns = kind == MockPreparationKind.Questions ? new List<MockTurn>() :
            [new MockTurn { Question = "Captured question", Ideal = "Captured ideal", Answer = "Captured answer",
                Score = kind == MockPreparationKind.Summary ? 4 : null, Feedback = kind == MockPreparationKind.Summary ? "Captured feedback" : null }];
        var input = new MockPreparationInput { Kind = kind, CapturedAt = DateTimeOffset.UtcNow, Operation = operation.CaptureOperation(),
            Source = new() { Id = 77, Kind = InterviewKind.Technical, Status = MockStatus.Active, Revision = 3, PreparationRequestId = id,
                CreatedAt = DateTimeOffset.UtcNow, TurnsJson = ConversationStore.Serialize(turns) },
            Waiting = new(MockInterviews.Mode, 77, 0, kind == MockPreparationKind.Summary ? null : turns.FirstOrDefault()?.Question, [], null)
                { Id = Guid.NewGuid(), MockPending = true, MockRequestId = id },
            Answer = kind == MockPreparationKind.Review ? "Captured answer" : null };
        if (kind == MockPreparationKind.Questions) input.Start = new() { IntentId = Guid.NewGuid(), Kind = InterviewKind.Technical,
            Profile = new() { Name = "Captured Candidate", Reviewed = false }, ProfileVersion = 1, Stories = "Captured supported story", Header = "Captured header" };
        input.Recipe = MockPreparationSteps.Recipe(operation, input);
        return (new(llm), input, options);
    }
    [Theory]
    [InlineData(MockPreparationKind.Questions)]
    [InlineData(MockPreparationKind.Review)]
    [InlineData(MockPreparationKind.Summary)]
    public async Task Real_adapter_uses_original_inputs_route_effort_schema_and_reads_back_checked_result(MockPreparationKind kind)
    {
        using var http = new Http { Kind = kind };
        var (steps, input, options) = await SetupAsync(http, kind);
        options.Routes[LlmPurpose.Coach][0].Name = "changed-deployment";
        var result = await steps.PrepareAsync(input, Ct);
        var root = MockPreparationRequests.Root(input, "synthetic");
        root.ResultJson = JsonSerializer.Serialize(result, MockPreparationJsonContext.Default.MockPreparationResult);
        root.ResultHash = CanonicalJson.Hash(root.ResultJson);
        MockPreparationRequests.ReadResult(root);
        var request = Assert.Single(http.Requests);
        Assert.Equal("frozen-mock", request["model"]!.GetValue<string>());
        Assert.Equal("max", request["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", request["response_format"]!["type"]!.GetValue<string>());
        var text = request["messages"]!.ToJsonString();
        Assert.Contains(kind == MockPreparationKind.Questions ? "Captured Candidate" : "Captured question", text);
        Assert.Contains(kind == MockPreparationKind.Questions ? "Captured supported story" : "Captured answer", text);
    }
    [Theory]
    [InlineData("source")]
    [InlineData("answer")]
    [InlineData("waiting")]
    [InlineData("prefix")]
    public async Task Changed_recipe_refuses_before_paid_call(string changed)
    {
        using var http = new Http { Kind = MockPreparationKind.Review };
        var (steps, input, _) = await SetupAsync(http, http.Kind);
        if (changed == "source") input.Source.TurnsJson = "[]";
        if (changed == "answer") input.Answer = "Replacement answer";
        if (changed == "waiting") input.Waiting.Id = Guid.NewGuid();
        if (changed == "prefix") input.Prefix = "Replacement prefix";
        await Assert.ThrowsAsync<MockPreparationSupersededException>(() => steps.PrepareAsync(input, Ct));
        Assert.Empty(http.Requests);
    }
    [Theory]
    [InlineData("version")]
    [InlineData("score")]
    [InlineData("null")]
    [InlineData("extra-result")]
    public async Task Damaged_semantic_result_fails_after_recomputed_outer_hash(string damage)
    {
        using var http = new Http { Kind = MockPreparationKind.Review };
        var (_, input, _) = await SetupAsync(http, http.Kind);
        var result = new MockPreparationResult { Review = new() { Score = 4, Tip = "Supported tip" } };
        if (damage == "version") result.Version = 99;
        if (damage == "score") result.Review.Score = 8;
        if (damage == "null") result.Review.Good = null!;
        if (damage == "extra-result") result.Summary = new() { Score = 4, Verdict = "Unexpected output" };
        var root = MockPreparationRequests.Root(input, "synthetic");
        root.ResultJson = JsonSerializer.Serialize(result, MockPreparationJsonContext.Default.MockPreparationResult);
        root.ResultHash = CanonicalJson.Hash(root.ResultJson);
        Assert.Throws<UnsupportedMockPreparationException>(() => MockPreparationRequests.ReadResult(root));
    }
    [Fact]
    public async Task Two_coding_questions_are_supported_but_one_question_checkpoint_is_refused()
    {
        var questions = new MockQuestions { Questions = [new() { Question = "First" }, new() { Question = "Second" }] };
        Assert.Null(questions.Validate());
        questions.Questions.RemoveAt(1);
        Assert.NotNull(questions.Validate());
        using var http = new Http { Kind = MockPreparationKind.Questions };
        var (_, input, _) = await SetupAsync(http, http.Kind);
        var root = MockPreparationRequests.Root(input, "synthetic");
        root.ResultJson = JsonSerializer.Serialize(new MockPreparationResult { Questions = [new() { Question = "First", Ideal = "First answer" }] }, MockPreparationJsonContext.Default.MockPreparationResult);
        root.ResultHash = CanonicalJson.Hash(root.ResultJson);
        Assert.Throws<UnsupportedMockPreparationException>(() => MockPreparationRequests.ReadResult(root));
    }

    private sealed class Usage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }
    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public MockPreparationKind Kind { get; set; }
        public List<JsonObject> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            var content = Kind switch
            {
                MockPreparationKind.Questions => JsonSerializer.Serialize(new MockQuestions { Questions = [new() { Question = "Supported question", Focus = "Supported focus", Ideal = "Supported answer" }, new() { Question = "Second supported question", Focus = "Second supported focus", Ideal = "Second supported answer" }] }, CareerJsonContext.Default.MockQuestions),
                MockPreparationKind.Review => JsonSerializer.Serialize(new MockReview { Score = 4, Good = ["Supported evidence"], Tip = "Supported next step" }, CareerJsonContext.Default.MockReview),
                _ => JsonSerializer.Serialize(new MockSummary { Score = 4, Verdict = "Supported summary", Strengths = ["Supported evidence"], Focus = ["Supported focus"] }, CareerJsonContext.Default.MockSummary),
            };
            var response = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 } };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
