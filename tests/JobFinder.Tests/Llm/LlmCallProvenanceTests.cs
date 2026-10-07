using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Profile;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Llm.Tasks.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Llm;

public sealed class LlmCallProvenanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Extraction = """
        {"isVacancy":false,"isDotnet":false,"title":null,"company":null,"city":null,"country":null,"workFormats":[],
         "salaryFrom":null,"salaryTo":null,"currency":null,"salaryGross":null,"englishLevel":"unknown","seniority":"unknown",
         "skills":[],"contacts":[],"applyUrl":null}
        """;

    private static HttpResponseMessage Completion(string model, string content = Extraction, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject
        {
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = finish,
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 50, ["cost"] = 0.0001m },
        }.ToJsonString()),
    };

    private static (LlmClient Client, Usage Sink, ModelHealth Health, OpenRouterTransport Transport) Setup(
        IHttpClientFactory http, params string[] models)
    {
        var options = new LlmOptions { ApiKey = "synthetic-deployment-key", Routes = new()
        {
            [LlmPurpose.Extract] = models.Select(name => new LlmModelOptions { Name = name }).ToList(),
            [LlmPurpose.Letter] = models.Select(name => new LlmModelOptions { Name = name }).ToList(),
        } };
        var usage = new Usage();
        var health = new ModelHealth(TimeProvider.System);
        var transport = new OpenRouterTransport(http, options);
        return (new LlmClient(transport, health, options, usage, NullLogger<LlmClient>.Instance), usage, health, transport);
    }

    [Fact]
    public async Task Paid_rotation_attempts_share_a_call_and_frozen_recipe_but_keep_actual_and_requested_models()
    {
        using var http = new Requests((body, _) => Task.FromResult(body["model"]!.ToString() == "first"
            ? Completion("first-provider-alias", "{}", "length") : Completion("second-provider-alias")));
        var (client, usage, _, _) = Setup(http, "first", "second");
        var operation = await new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir)).ForOperationAsync(Ct);
        usage.OnRecord = attempt =>
        {
            if (attempt.AttemptNumber == 1) attempt.Recipe!.Inputs["user"] = "caller-mutated";
        };
        await operation.ExtractPostAsync("synthetic-private-post", "synthetic-private-channel", Ct);
        var attempts = usage.Attempts.OrderBy(attempt => attempt.AttemptNumber).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(attempts[0].OperationId, attempts[1].OperationId);
        Assert.Equal(attempts[0].CallId, attempts[1].CallId);
        Assert.NotNull(attempts[0].CallId);
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(["first", "second"], attempts.Select(attempt => attempt.RequestedModel));
        Assert.Equal(["first-provider-alias", "second-provider-alias"], attempts.Select(attempt => attempt.Model));
        Assert.False(attempts[0].Ok);
        Assert.True(attempts[1].Ok);
        Assert.All(attempts, attempt => Assert.Equal(0.0001m, attempt.CostUsd));
        Assert.NotEqual("caller-mutated", attempts[1].Recipe!.Inputs["user"]);
        var recipe = attempts[1].Recipe!;
        Assert.Equal(attempts[1].OperationId, recipe.OperationId);
        Assert.Equal(ExtractPostTask.Definition.Id, Assert.Single(recipe.Tasks).TaskId);
        var json = JsonSerializer.Serialize(recipe, RecipeJsonContext.Default.ArtifactRecipe);
        Assert.DoesNotContain("synthetic-private-post", json);
        Assert.DoesNotContain("synthetic-private-channel", json);
        Assert.DoesNotContain("synthetic-deployment-key", json);
    }

    [Fact]
    public async Task Parallel_calls_and_distinct_tasks_share_only_the_captured_artifact_operation()
    {
        using var http = new Requests((body, _) => Task.FromResult(Completion("actual-model",
            body.ContainsKey("response_format") ? Extraction : "Здравствуйте! Работал над API.")));
        var (root, usage, _, _) = Setup(http, "model");
        var client = await root.ForOperationAsync(Ct);
        Assert.Same(client, await client.ForOperationAsync(Ct));
        var prompts = new PromptCatalog(Fixture.PromptsDir);
        var operation = new JobFinderLlm(client, prompts);
        await Task.WhenAll(operation.ExtractPostAsync("one", "channel", Ct), operation.ExtractPostAsync("two", "channel", Ct));
        await LlmTaskCatalog.Outreach.ExecuteAsync(client, prompts, TaskProbeInputs.Outreach(LlmPurpose.Letter), Ct);
        var calls = usage.Attempts.ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Equal(3, calls.Select(attempt => attempt.CallId).Distinct().Count());
        Assert.All(calls, attempt => Assert.Equal(client.OperationId, attempt.OperationId));
        Assert.All(calls, attempt => Assert.Equal(1, attempt.AttemptNumber));
        Assert.Equal(2, calls.Select(attempt => Assert.Single(attempt.Recipe!.Tasks).TaskId).Distinct().Count());
        var recipe = operation.Recipe([(LlmTaskCatalog.Outreach, LlmPurpose.Letter)], new Dictionary<string, string?> { ["company"] = "synthetic" });
        Assert.Equal(client.OperationId, recipe.OperationId);
        Assert.NotEqual(client.OperationId, (await root.ForOperationAsync(Ct)).OperationId);
        Assert.Null(root.OperationId);
    }

    [Fact]
    public async Task Separate_uncaptured_task_calls_do_not_share_an_operation_identity()
    {
        using var http = new Requests((_, _) => Task.FromResult(Completion("model")));
        var (root, usage, _, _) = Setup(http, "model");
        var facade = new JobFinderLlm(root, new PromptCatalog(Fixture.PromptsDir));
        await facade.ExtractPostAsync("one", "channel", Ct);
        await facade.ExtractPostAsync("two", "channel", Ct);
        Assert.Equal(2, usage.Attempts.Select(attempt => attempt.OperationId).Distinct().Count());
    }

    [Fact]
    public async Task Binding_or_user_input_changes_change_call_identity_without_persisting_raw_candidate_inputs()
    {
        using var http = new Requests((_, _) => Task.FromResult(Completion("model", "Здравствуйте!")));
        var (root, usage, _, _) = Setup(http, "model");
        var client = await root.ForOperationAsync(Ct);
        var prompts = new PromptCatalog(Fixture.PromptsDir);
        var input = TaskProbeInputs.Outreach(LlmPurpose.Letter);
        await LlmTaskCatalog.Outreach.ExecuteAsync(client, prompts, input, Ct);
        await LlmTaskCatalog.Outreach.ExecuteAsync(client, prompts, input with { Profile = new CandidateProfile { Name = "synthetic-private-name" } }, Ct);
        Assert.Equal(2, usage.Attempts.Select(attempt => attempt.Recipe!.Hash).Distinct().Count());
        Assert.DoesNotContain("synthetic-private-name", JsonSerializer.Serialize(usage.Attempts.Last().Recipe, RecipeJsonContext.Default.ArtifactRecipe));
    }

    [Fact]
    public async Task Caller_cancellation_is_recorded_without_poisoning_model_health_or_faking_a_known_charge()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var http = new Requests((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Completion("model"));
        });
        var (client, usage, health, _) = Setup(http, "model");
        var facade = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facade.ExtractPostAsync("post", "channel", cancellation.Token));
        var attempt = Assert.Single(usage.Attempts);
        Assert.False(attempt.Ok);
        Assert.Contains("provider usage unavailable", attempt.Error);
        Assert.Equal(0, attempt.CostUsd);
        Assert.True(health.IsAvailable("model"));
        Assert.All(usage.RecordTokens, token => Assert.False(token.CanBeCanceled));
    }

    [Fact]
    public async Task Usage_sink_failure_cannot_rotate_a_successful_paid_completion()
    {
        using var http = new Requests((_, _) => Task.FromResult(Completion("model")));
        var (client, usage, health, _) = Setup(http, "model");
        usage.OnRecord = _ => throw new InvalidOperationException("isolated usage sink failure");
        var result = await new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir)).ExtractPostAsync("post", "channel", Ct);
        Assert.Equal("model", result.Model);
        Assert.Single(usage.Attempts);
        Assert.True(health.IsAvailable("model"));
    }

    [Fact]
    public async Task Metadata_from_another_operation_is_rejected_before_any_provider_request()
    {
        using var http = new Requests((_, _) => throw new InvalidOperationException("must not send"));
        var (root, usage, _, _) = Setup(http, "model");
        var client = await root.ForOperationAsync(Ct);
        var recipe = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir)).Recipe(
            [(LlmTaskCatalog.Outreach, LlmPurpose.Letter)], new Dictionary<string, string?>());
        var other = await root.ForOperationAsync(Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => other.CompleteTextAsync(LlmPurpose.Letter, "system", "user", Ct, recipe: recipe));
        Assert.Empty(usage.Attempts);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("contract")]
    [InlineData("route")]
    [InlineData("multiple-tasks")]
    [InlineData("policy")]
    public async Task Provenance_must_describe_the_exact_effective_request(string changed)
    {
        using var http = new Requests((_, _) => throw new InvalidOperationException("must not send"));
        var (root, usage, _, _) = Setup(http, "model");
        var client = await root.ForOperationAsync(Ct);
        var recipe = new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir)).Recipe(
            [(LlmTaskCatalog.Outreach, LlmPurpose.Letter)], new Dictionary<string, string?>());
        switch (changed)
        {
            case "purpose": recipe.Tasks[0].Purpose = LlmPurpose.Score; break;
            case "contract": recipe.Tasks[0].ContractVersion = "changed"; break;
            case "route": recipe.Tasks[0].RouteVersion = "changed"; break;
            case "multiple-tasks": recipe.Tasks.Add(recipe.Tasks[0]); break;
            case "policy": recipe.PolicyRevision++; break;
        }
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteTextAsync(LlmPurpose.Letter, "system", "user", Ct, recipe: recipe));
        Assert.Empty(usage.Attempts);
    }

    private sealed class Usage : ILlmUsageSink
    {
        public ConcurrentQueue<LlmAttempt> Attempts { get; } = new();
        public ConcurrentQueue<CancellationToken> RecordTokens { get; } = new();
        public Action<LlmAttempt>? OnRecord { get; set; }
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct)
        {
            Attempts.Enqueue(attempt);
            RecordTokens.Enqueue(ct);
            OnRecord?.Invoke(attempt);
            return Task.CompletedTask;
        }
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Requests(Func<JsonObject, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            await response(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(), ct);
    }
}
