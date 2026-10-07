using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Profile;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class HrPlaybookStepsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(HrPlaybookPreparationSteps Steps, HrPlaybookInput Input, JobFinderLlm Llm)> SetupAsync(Api api, Store store)
    {
        var options = new LlmOptions { ApiKey = "synthetic-key", Endpoint = "https://provider.invalid/completions", Proxy = "", MaxAttempts = 1,
            Routes = LlmPurpose.All.ToDictionary(purpose => purpose, purpose => new List<LlmModelOptions> { new() { Name = "synthetic-model", Reasoning = new() { Effort = "max" } } }) };
        var llm = new JobFinderLlm(new LlmClient(new OpenRouterTransport(api, options), new ModelHealth(TimeProvider.System), options,
            new Usage(), NullLogger<LlmClient>.Instance), new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var input = new HrPlaybookInput
        {
            ProfileJson = ProfileProvider.ToJson(new CandidateProfile { Name = "Synthetic Candidate", Summary = "Обрабатывал сообщения, проверял повторную доставку." }),
            ProfileVersion = 1, FactsVersion = 1, FactsJson = JsonSerializer.Serialize(new CandidateFacts(), FactsJsonContext.Default.CandidateFacts),
            CapturedAt = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero), Operation = operation.CaptureOperation(),
            Bank = new HrBank { Questions = [new() { Id = "about_a", Question = "Вопрос A о проверке доставки", Category = "Самопрезентация" },
                new() { Id = "about_b", Question = "Вопрос B о границах обработки", Category = "Работа с людьми" }] },
        };
        input.Recipe = HrPlaybookPreparationSteps.Recipe(operation, input);
        return (new HrPlaybookPreparationSteps(llm, new GeneratedAnswers(store, TimeProvider.System), TimeProvider.System,
            NullLogger<HrPlaybookPreparationSteps>.Instance), input, llm);
    }

    [Fact]
    public async Task Actual_hr_step_reuses_paid_question_checkpoints_and_a_fact_change_rebuilds_only_its_dependent_answer()
    {
        using var api = new Api();
        var store = new Store();
        var (steps, input, llm) = await SetupAsync(api, store);
        var first = await steps.BuildAsync(input, Ct);
        var coldCalls = api.Requests.Count;
        Assert.Equal(2, store.Count);
        Assert.InRange(coldCalls, 2, 4);
        var warm = await steps.BuildAsync(input, Ct);
        Assert.Equal(coldCalls, api.Requests.Count);
        Assert.All(warm.Items, item => Assert.True(item.Item.Reused));
        var facts = new CandidateFacts { Facts = [new() { Key = "hr_about_a", Question = "Own position", Answer = "Проверял повторную доставку в тестах", Confirmed = true }] };
        input.FactsJson = JsonSerializer.Serialize(facts, FactsJsonContext.Default.CandidateFacts);
        input.FactsVersion++;
        input.Recipe = HrPlaybookPreparationSteps.Recipe(llm.RestoreOperation(input.Operation), input);
        var changed = await steps.BuildAsync(input, Ct);
        Assert.False(changed.Items[0].Item.Reused);
        Assert.True(changed.Items[1].Item.Reused);
        Assert.InRange(api.Requests.Count - coldCalls, 1, 2);
        Assert.Equal(first.Items[1].Item.Fingerprint, changed.Items[1].Item.Fingerprint);
    }

    [Fact]
    public async Task Hr_provider_outage_does_not_cache_a_placeholder_as_a_completed_answer()
    {
        using var api = new Api { Unavailable = true };
        var store = new Store();
        var (steps, input, _) = await SetupAsync(api, store);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => steps.BuildAsync(input, Ct));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Paid_hr_cache_write_failure_retains_raw_output_and_refuses_automatic_paid_replay()
    {
        using var api = new Api();
        var store = new Store { FailWrites = true };
        var (steps, input, _) = await SetupAsync(api, store);
        var error = await Assert.ThrowsAsync<HrPlaybookCheckpointException>(() => steps.BuildAsync(input, Ct));
        Assert.NotNull(error.PartialJson);
        var retained = JsonSerializer.Deserialize(error.PartialJson, InterviewJsonContext.Default.HrPlaybook)!;
        Assert.NotEmpty(retained.Items);
        Assert.Contains("Работал над сервисом", retained.Items[0].Item.A!.Start);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Missing_hr_fact_uses_a_position_card_without_calling_a_model()
    {
        using var api = new Api();
        var (steps, input, llm) = await SetupAsync(api, new Store());
        input.Bank.Questions = [new() { Id = "overtime", Question = "Ваше отношение к переработкам?", Facts = ["overtime"], Options = ["Нет", "Редкие авралы"] }];
        input.Recipe = HrPlaybookPreparationSteps.Recipe(llm.RestoreOperation(input.Operation), input);
        var result = await steps.BuildAsync(input, Ct);
        Assert.True(Assert.Single(result.Items).Item.A!.NeedsPosition);
        Assert.Empty(api.Requests);
    }

    private sealed class Usage : ILlmUsageSink
    {
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }
    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public bool Unavailable;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("provider.invalid", request.RequestUri!.Host);
            if (Unavailable) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            var answer = new SpokenAnswerWire { Start = "Работал над сервисом обработки сообщений.",
                Core = "Разделил обработку на этапы и проверял результат на тестовых сценариях.",
                DeepDive = "Сначала определял границы обработки, затем проверял повторную доставку и устойчивость системы.", EvidenceLevel = "verified" };
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = JsonSerializer.Serialize(answer, GeneratedWireJsonContext.Default.SpokenAnswerWire) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }
    private sealed class Store : IGeneratedAnswerStore
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, (Generated Value, string Hash)> _rows = new(StringComparer.Ordinal);
        public bool FailWrites;
        public int Count { get { lock (_lock) return _rows.Count; } }
        public Task<StoredAnswer?> GetAsync(string key, Func<IReadOnlyList<string>, string> depsHash, CancellationToken ct)
        {
            lock (_lock) return Task.FromResult<StoredAnswer?>(_rows.TryGetValue(key, out var row) && row.Hash == depsHash(row.Value.Deps)
                ? new StoredAnswer(row.Value.Value, row.Value.Deps) : null);
        }
        public Task PutAsync(AnswerRequest request, Generated generated, string depsHash, CancellationToken ct)
        {
            if (FailWrites) throw new IOException("synthetic paid-answer checkpoint fault");
            lock (_lock) _rows[request.Key] = (generated, depsHash);
            return Task.CompletedTask;
        }
        public Task<decimal> SavedUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }
}
