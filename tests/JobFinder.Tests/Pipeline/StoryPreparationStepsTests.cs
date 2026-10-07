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
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class StoryPreparationStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(StoryPreparationSteps Steps, StoryPreparationInput Input, LlmOptions Options)> SetupAsync(Http http)
    {
        var options = Llm.LlmRuntimePolicyTests.Bootstrap();
        options.MaxAttempts = 1;
        options.Routes[LlmPurpose.Coach] = [new() { Name = "frozen-story", Reasoning = new() { Effort = "max" } }];
        var client = new LlmClient(new OpenRouterTransport(http, options), new ModelHealth(TimeProvider.System), options, new Usage(), NullLogger<LlmClient>.Instance);
        var llm = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var candidate = new StoryCandidateSnapshot { ProfileVersion = 1, FactsVersion = 1, ResumeText = "Captured resume 150 ms", Questions = [.. StoryBank.Questions] };
        candidate.ResumeHash = Fingerprint.Of(candidate.ResumeText);
        var before = new ConversationState(StoryBank.Mode, 0, 4, "Synthetic achievement", ["Captured raw answer 150 ms"], null) { Id = Guid.NewGuid() };
        var input = new StoryPreparationInput { Before = before, Candidate = candidate, Questions = [.. candidate.Questions], CapturedAt = DateTimeOffset.UtcNow,
            Operation = operation.CaptureOperation(), Waiting = before with { StoryCandidate = candidate, StoryPending = true, StoryRequestId = operation.CaptureOperation().OperationId } };
        input.Recipe = StoryPreparationSteps.Recipe(operation, input);
        return (new(llm), input, options);
    }
    private static StoryPreparationRequest Root(StoryPreparationInput input, StoryPreparationResult? result = null)
    {
        var json = JsonSerializer.Serialize(input, StoryPreparationJsonContext.Default.StoryPreparationInput);
        var output = result is null ? null : JsonSerializer.Serialize(result, StoryPreparationJsonContext.Default.StoryPreparationResult);
        return new() { Id = input.Operation.OperationId, BusinessKey = "story-v1:" + Fingerprint.Of(ConversationStore.Capture(input.Before), input.Answer), InputJson = json,
            InputHash = CanonicalJson.Hash(json), ResultJson = output, ResultHash = output is null ? null : CanonicalJson.Hash(output), Stage = StoryPreparationStage.Drafting };
    }

    [Fact]
    public async Task Paid_adapter_uses_frozen_coach_route_questions_answers_achievement_and_resume_only()
    {
        using var http = new Http();
        var (steps, input, options) = await SetupAsync(http);
        options.Routes[LlmPurpose.Coach][0].Name = "changed-live-route";
        var result = await steps.DraftAsync(input, Ct);
        Assert.Equal("Captured action", StoryPreparationRequests.ReadResult(Root(input, result)).Draft.Action);
        var request = Assert.Single(http.Requests);
        Assert.Equal("frozen-story", request["model"]!.GetValue<string>());
        Assert.Equal("max", request["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", request["response_format"]!["type"]!.GetValue<string>());
        var text = request["messages"]!.ToJsonString();
        Assert.Contains("Synthetic achievement", text);
        Assert.Contains("Captured raw answer 150 ms", text);
        Assert.Contains("Captured resume 150 ms", text);
    }

    [Theory]
    [InlineData("answers")]
    [InlineData("resume")]
    [InlineData("profile")]
    [InlineData("questions")]
    public async Task Changed_frozen_recipe_refuses_before_paid_work(string changed)
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        if (changed == "answers") input.Waiting.Answers.Add("new unsupported answer");
        if (changed == "resume") input.Candidate.ResumeText += "changed";
        if (changed == "profile") input.Candidate.Profile.Summary += "changed";
        if (changed == "questions") input.Questions[0] += "changed";
        await Assert.ThrowsAsync<StoryPreparationSupersededException>(() => steps.DraftAsync(input, Ct));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Availability_failure_does_not_substitute_an_unreviewed_rule_story()
    {
        using var http = new Http { Unavailable = true };
        var (steps, input, _) = await SetupAsync(http);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => steps.DraftAsync(input, Ct));
        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("answers")]
    [InlineData("hash")]
    [InlineData("skills")]
    public async Task Readers_reject_damaged_checkpoints_even_after_an_outer_hash_is_recomputed(string damage)
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        var result = await steps.DraftAsync(input, Ct);
        if (damage == "version") input.Version++;
        if (damage == "identity") input.Waiting.StoryRequestId = Guid.NewGuid();
        if (damage == "answers") input.Waiting = input.Waiting with { Answers = ["rewritten captured input"] };
        if (damage == "hash") input.Candidate.ResumeHash = "damaged";
        if (damage == "skills") result.Draft.Skills = null!;
        Assert.Throws<UnsupportedStoryPreparationException>(() => StoryPreparationRequests.ReadResult(Root(input, result)));
    }

    [Fact]
    public async Task Draft_presentation_warns_about_unproven_numbers_and_binds_buttons_to_exact_ready_text()
    {
        using var http = new Http();
        var (steps, input, _) = await SetupAsync(http);
        var result = await steps.DraftAsync(input, Ct);
        result.Draft.Title = "<Synthetic & story>";
        result.Draft.Result = "9999 ms";
        var ready = StoryPresentation.Ready(input, result);
        var card = StoryPresentation.Draft(input, result);
        Assert.Null(ready.StoryPending);
        Assert.Equal(input.Operation.OperationId, ready.StoryRequestId);
        Assert.Contains("&lt;Synthetic &amp; story&gt;", card.Text);
        Assert.Contains("9999", card.Text);
        Assert.Contains("проверьте", card.Text);
        Assert.Contains(Callback.Button("✅ Сохранить", CareerCallback.StorySave, StoryBank.ControlId(ready), StoryBank.Version(ready)), card.Buttons!.SelectMany(row => row));
    }

    private sealed class Usage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }
    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public List<JsonObject> Requests { get; } = [];
        public bool Unavailable;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if (Unavailable) return new(HttpStatusCode.ServiceUnavailable);
            var draft = new StoryDraft { Title = "Synthetic story", Action = "Captured action", Result = "150 ms", Skills = ["C#"] };
            var response = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject
                { ["content"] = JsonSerializer.Serialize(draft, CareerJsonContext.Default.StoryDraft) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 } };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
}
