using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Llm;

public sealed class LlmClientTests
{
    private const string ValidExtraction = """
        {"isVacancy":true,"isDotnet":true,"title":"Senior .NET","company":"Acme","city":"Минск","country":"BY","workFormats":["hybrid"],
         "salaryFrom":4000,"salaryTo":5000,"currency":"USD","salaryGross":false,"englishLevel":"none","seniority":"senior",
         "skills":["C#"],"contacts":["@hr"],"applyUrl":null}
        """;

    [Fact]
    public async Task Rate_limited_model_is_parked_and_the_next_one_answers()
    {
        var handler = new ScriptedHandler()
            .Add("model-a", HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limited","code":429}}""")
            .Add("model-b", HttpStatusCode.OK, Completion("model-b", ValidExtraction));
        var (llm, health, usage) = Create(handler, "model-a", "model-b");

        var result = await llm.ExtractPostAsync("post", "chat", TestContext.Current.CancellationToken);

        Assert.Equal("model-b", result.Model);
        Assert.Equal("Senior .NET", result.Value.Title);
        Assert.False(health.IsAvailable("model-a"));
        Assert.Equal(2, usage.Attempts.Count);
        Assert.False(usage.Attempts[0].Ok);
    }

    [Fact]
    public async Task Invalid_json_is_the_model_failure_and_rotates()
    {
        var handler = new ScriptedHandler()
            .Add("model-a", HttpStatusCode.OK, Completion("model-a", "Конечно! Вот ответ: это вакансия."))
            .Add("model-b", HttpStatusCode.OK, Completion("model-b", "```json\n" + ValidExtraction + "\n```"));
        var (llm, _, _) = Create(handler, "model-a", "model-b");

        var result = await llm.ExtractPostAsync("post", "chat", TestContext.Current.CancellationToken);

        Assert.Equal("model-b", result.Model);
    }

    [Fact]
    public async Task Validation_failure_rotates_too()
    {
        const string bad = """{"score":150,"stackMatch":1,"seniorityMatch":1,"domainMatch":1,"formatMatch":1,"englishRequired":"none","strengths":[],"gaps":[],"redFlags":[],"summary":"x"}""";
        const string good = """{"score":82,"stackMatch":90,"seniorityMatch":80,"domainMatch":70,"formatMatch":100,"englishRequired":"none","strengths":["C#"],"gaps":[],"redFlags":[],"summary":"Подходит","keywords":["C#"],"language":"ru","legitimacy":"ok","legitimacyReasons":[]}""";
        var handler = new ScriptedHandler()
            .Add("model-a", HttpStatusCode.OK, Completion("model-a", bad))
            .Add("model-b", HttpStatusCode.OK, Completion("model-b", good));
        var (llm, _, _) = Create(handler, "model-a", "model-b");

        var profile = new JobFinder.Core.Profile.CandidateProfile { Name = "Иван" };
        var vacancy = new JobFinder.Core.Domain.Vacancy { Title = "Senior .NET", Url = "u", Fingerprint = "f" };
        var result = await llm.ScoreAsync(profile, vacancy, "вилка не указана", TestContext.Current.CancellationToken);

        Assert.Equal(82, result.Value.Score);
    }

    [Fact]
    public async Task Rejected_key_stops_the_route_immediately()
    {
        var handler = new ScriptedHandler()
            .Add("model-a", HttpStatusCode.Unauthorized, """{"error":{"message":"bad key","code":401}}""")
            .Add("model-b", HttpStatusCode.OK, Completion("model-b", ValidExtraction));
        var (llm, _, _) = Create(handler, "model-a", "model-b");

        var e = await Assert.ThrowsAsync<LlmUnavailableException>(() => llm.ExtractPostAsync("p", "c", TestContext.Current.CancellationToken));

        Assert.True(e.Unauthorized);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Spent_budget_blocks_background_calls()
    {
        var handler = new ScriptedHandler().Add("model-a", HttpStatusCode.OK, Completion("model-a", ValidExtraction));
        var (llm, _, usage) = Create(handler, "model-a");
        usage.Spent = 5m;

        var e = await Assert.ThrowsAsync<LlmUnavailableException>(() => llm.ExtractPostAsync("p", "c", TestContext.Current.CancellationToken));

        Assert.True(e.BudgetExceeded);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Upstream_error_inside_a_200_body_is_classified()
    {
        var handler = new ScriptedHandler()
            .Add("model-a", HttpStatusCode.OK, """{"error":{"message":"Provider returned error","code":429}}""")
            .Add("model-b", HttpStatusCode.OK, Completion("model-b", ValidExtraction));
        var (llm, health, _) = Create(handler, "model-a", "model-b");

        await llm.ExtractPostAsync("p", "c", TestContext.Current.CancellationToken);

        Assert.False(health.IsAvailable("model-a"));
    }

    [Fact]
    public void Body_omits_temperature_when_unset_and_requires_parameters_for_strict_schema()
    {
        var transport = new OpenRouterTransport(new StubFactory(new ScriptedHandler()), new LlmOptions());
        var schema = new JsonSchemaSpec("s", """{"type":"object","additionalProperties":false,"required":[],"properties":{}}""");
        var body = transport.BuildBody(new LlmModelOptions
        {
            Name = "m", Structured = "json_schema", ExtraBody = """{"provider":{"data_collection":"deny"},"reasoning":{"effort":"none"}}""",
        }, "sys", "user", schema);

        Assert.False(body.ContainsKey("temperature"));
        Assert.Equal("json_schema", body["response_format"]!["type"]!.ToString());
        Assert.True(body["provider"]!["require_parameters"]!.GetValue<bool>());
        Assert.Equal("deny", body["provider"]!["data_collection"]!.ToString());
        Assert.Equal("none", body["reasoning"]!["effort"]!.ToString());
    }

    [Fact]
    public void Json_object_mode_puts_the_schema_into_the_system_prompt()
    {
        var transport = new OpenRouterTransport(new StubFactory(new ScriptedHandler()), new LlmOptions());
        var body = transport.BuildBody(new LlmModelOptions { Name = "m", Structured = "json_object", Temperature = 0.1 }, "sys", "user",
            new JsonSchemaSpec("s", """{"type":"object"}"""));

        Assert.Equal(0.1, body["temperature"]!.GetValue<double>());
        Assert.Equal("json_object", body["response_format"]!["type"]!.ToString());
        Assert.Contains("JSON Schema", body["messages"]![0]!["content"]!.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isVacancy\":false}")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task Incomplete_or_wrong_root_extraction_rotates_instead_of_silently_discarding_the_post(string invalid)
    {
        var handler = new ScriptedHandler()
            .Add("model-a", HttpStatusCode.OK, Completion("model-a", invalid))
            .Add("model-b", HttpStatusCode.OK, Completion("model-b", ValidExtraction));
        var (llm, _, usage) = Create(handler, "model-a", "model-b");
        var result = await llm.ExtractPostAsync("post", "chat", TestContext.Current.CancellationToken);
        Assert.Equal("model-b", result.Model);
        Assert.True(result.Value.IsVacancy);
        Assert.False(usage.Attempts[0].Ok);
        Assert.Equal(0.0001m, usage.Attempts[0].CostUsd);
    }

    [Theory]
    [InlineData("skills", "null")]
    [InlineData("isVacancy", "null")]
    [InlineData("isVacancy", "\"true\"")]
    [InlineData("englishLevel", "\"fluent\"")]
    [InlineData("workFormats", "[\"anywhere\"]")]
    [InlineData("salaryFrom", "\"4000\"")]
    [InlineData("unexpected", "true")]
    public async Task Null_enum_type_and_extra_field_violations_cannot_pass_tolerant_deserialization(string field, string invalidValue)
    {
        var invalid = JsonNode.Parse(ValidExtraction)!.AsObject();
        invalid[field] = JsonNode.Parse(invalidValue);
        var handler = new ScriptedHandler()
            .Add("model-a", HttpStatusCode.OK, Completion("model-a", invalid.ToJsonString()))
            .Add("model-b", HttpStatusCode.OK, Completion("model-b", ValidExtraction));
        var (llm, _, usage) = Create(handler, "model-a", "model-b");
        Assert.Equal("model-b", (await llm.ExtractPostAsync("post", "chat", TestContext.Current.CancellationToken)).Model);
        Assert.False(usage.Attempts[0].Ok);
    }

    [Theory]
    [InlineData("", "stop")]
    [InlineData("", "length")]
    [InlineData("{\"isVacancy\":true}", "length")]
    public async Task Empty_or_truncated_paid_responses_keep_usage_when_rotating(string content, string finish)
    {
        var paid = JsonNode.Parse(Completion("model-a", content))!.AsObject();
        paid["choices"]![0]!["finish_reason"] = finish;
        var handler = new ScriptedHandler()
            .Add("model-a", HttpStatusCode.OK, paid.ToJsonString())
            .Add("model-b", HttpStatusCode.OK, Completion("model-b", ValidExtraction));
        var (llm, _, usage) = Create(handler, "model-a", "model-b");
        await llm.ExtractPostAsync("post", "chat", TestContext.Current.CancellationToken);
        Assert.False(usage.Attempts[0].Ok);
        Assert.Equal(100, usage.Attempts[0].PromptTokens);
        Assert.Equal(50, usage.Attempts[0].CompletionTokens);
        Assert.Equal(0.0001m, usage.Attempts[0].CostUsd);
    }

    [Theory]
    [InlineData("{\"model\":\"other\"}")]
    [InlineData("{\"messages\":[]}")]
    [InlineData("{\"response_format\":null}")]
    [InlineData("{\"usage\":{\"include\":false}}")]
    [InlineData("{\"max_tokens\":1}")]
    [InlineData("{\"provider\":null}")]
    [InlineData("{\"provider\":{\"require_parameters\":false}}")]
    [InlineData("{\"reasoning\":false}")]
    [InlineData("{")]
    [InlineData("[]")]
    public void Extra_body_cannot_replace_protected_request_fields_or_disable_strict_output(string extra)
    {
        var transport = new OpenRouterTransport(new StubFactory(new ScriptedHandler()), new LlmOptions());
        var error = Assert.Throws<LlmCallException>(() => transport.BuildBody(new LlmModelOptions { Name = "test", ExtraBody = extra },
            "system", "user", Schemas.PostExtraction));
        Assert.Equal(LlmFailureKind.Rejected, error.Kind);
    }

    [Fact]
    public void Unknown_purpose_is_a_configuration_error_instead_of_a_score_fallback()
    {
        var options = new LlmOptions { Routes = new() { [LlmPurpose.Score] = [new LlmModelOptions { Name = "score" }] } };
        var client = new LlmClient(new OpenRouterTransport(new StubFactory(new ScriptedHandler()), options),
            new ModelHealth(TimeProvider.System), options, new FakeUsage(), NullLogger<LlmClient>.Instance);
        Assert.Throws<ArgumentException>(() => client.Route("Resuem"));
    }

    private static string Completion(string model, string content) =>
        new JsonObject
        {
            ["model"] = model,
            ["choices"] = new JsonArray { new JsonObject { ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = "stop" } },
            ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 50, ["cost"] = 0.0001 },
        }.ToJsonString();

    [Fact]
    public void Extraction_may_be_asked_only_while_a_model_of_its_route_is_not_parked()
    {
        // The post worker checks this before reading a post's pages: work a missing model would throw away.
        var (llm, health, _) = Create(new ScriptedHandler(), "a", "b");
        Assert.True(llm.MayExtract);
        health.ReportFailure("a", LlmFailureKind.RateLimited, TimeSpan.FromMinutes(5), "429");
        Assert.True(llm.MayExtract);
        health.ReportFailure("b", LlmFailureKind.RateLimited, TimeSpan.FromMinutes(5), "429");
        Assert.False(llm.MayExtract);
    }

    private static (JobFinderLlm Llm, ModelHealth Health, FakeUsage Usage) Create(ScriptedHandler handler, params string[] models)
    {
        var options = new LlmOptions
        {
            ApiKey = "k",
            DailyBudgetUsd = 1m,
            Routes = new(StringComparer.OrdinalIgnoreCase)
            {
                [LlmPurpose.Extract] = models.Select(m => new LlmModelOptions { Name = m }).ToList(),
                [LlmPurpose.Score] = models.Select(m => new LlmModelOptions { Name = m }).ToList(),
            },
        };
        var health = new ModelHealth(new FakeTimeProvider());
        var usage = new FakeUsage();
        var client = new LlmClient(new OpenRouterTransport(new StubFactory(handler), options), health, options, usage, NullLogger<LlmClient>.Instance);
        return (new JobFinderLlm(client, new PromptCatalog(Fixture.PromptsDir)), health, usage);
    }

    private sealed class FakeUsage : ILlmUsageSink
    {
        public List<LlmAttempt> Attempts { get; } = [];

        public decimal Spent { get; set; }

        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }

        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(Spent);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers by the "model" field of the request body.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new();

        public int Calls { get; private set; }

        public ScriptedHandler Add(string model, HttpStatusCode status, string body)
        {
            _answers[model] = (status, body);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var (status, text) = _answers[body["model"]!.ToString()];
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}

public sealed class ModelHealthTests
{
    [Fact]
    public void One_timeout_is_tolerated_two_park_the_model_and_success_resets()
    {
        var time = new FakeTimeProvider();
        var health = new ModelHealth(time);

        Assert.Equal(TimeSpan.Zero, health.ReportFailure("m", LlmFailureKind.Unavailable, null, "timeout"));
        Assert.True(health.IsAvailable("m"));
        var cooldown = health.ReportFailure("m", LlmFailureKind.Unavailable, null, "timeout");
        Assert.True(cooldown > TimeSpan.Zero);
        Assert.False(health.IsAvailable("m"));

        time.Advance(cooldown + TimeSpan.FromSeconds(1));
        Assert.True(health.IsAvailable("m"));
        health.ReportSuccess("m");
        Assert.Equal(TimeSpan.Zero, health.ReportFailure("m", LlmFailureKind.Unavailable, null, "timeout"));
    }

    [Fact]
    public void Retry_after_is_obeyed()
    {
        var health = new ModelHealth(new FakeTimeProvider());
        Assert.Equal(TimeSpan.FromSeconds(42), health.ReportFailure("m", LlmFailureKind.RateLimited, TimeSpan.FromSeconds(42), "429"));
    }

    [Fact]
    public void Repeated_rate_limits_escalate_up_to_the_cap()
    {
        var health = new ModelHealth(new FakeTimeProvider());
        var cooldowns = Enumerable.Range(0, 12).Select(_ => health.ReportFailure("m", LlmFailureKind.RateLimited, null, "429")).ToList();

        Assert.True(cooldowns[1] > cooldowns[0]);
        Assert.Equal(TimeSpan.FromMinutes(15), cooldowns[^1]);
    }
}

public sealed class PromptCatalogTests
{
    [Fact]
    public void Real_prompts_render_and_inserted_text_may_contain_braces()
    {
        var catalog = new PromptCatalog(Fixture.PromptsDir);

        var text = catalog.Render("score_vacancy", new Dictionary<string, string> { ["profile"] = "{\"name\":\"{{not_a_placeholder}}\"}" });

        Assert.Contains("{{not_a_placeholder}}", text);
    }

    [Fact]
    public void A_missing_value_fails_loudly()
    {
        var catalog = new PromptCatalog(Fixture.PromptsDir);
        Assert.Throws<InvalidOperationException>(() => catalog.Render("score_vacancy", new Dictionary<string, string>()));
    }
}

public sealed class ExtractJsonTests
{
    [Theory]
    [InlineData("{\"a\":\"x\"}\"", "{\"a\":\"x\"}")] // a stray quote after a valid object
    [InlineData("{\"a\":{\"b\":1}}\n}", "{\"a\":{\"b\":1}}")] // a stray brace
    [InlineData("Вот ответ: {\"a\":\"скобка } в строке\"} конец", "{\"a\":\"скобка } в строке\"}")] // braces inside strings
    [InlineData("{\"a\":\"кавычка \\\" внутри\"}", "{\"a\":\"кавычка \\\" внутри\"}")] // escaped quote
    public void First_complete_object_is_taken(string raw, string expected) =>
        Assert.Equal(expected, JobFinder.Llm.Client.LlmClient.ExtractJson(raw));
}
