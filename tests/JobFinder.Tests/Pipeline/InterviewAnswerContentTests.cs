using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Options;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed class InterviewAnswerContentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Capture_deeply_detaches_candidate_question_source_and_operation_and_roundtrips_without_secrets()
    {
        var fixture = await CreateAsync();
        var profile = new CandidateProfile { Name = "Synthetic", Title = "Backend" };
        var facts = new CandidateFacts { Facts = [new() { Key = "location", Answer = "Minsk", Confirmed = true }] };
        var vacancy = Vacancy();
        var question = Question();
        var catalog = new Dictionary<string, string> { ["S1"] = "A factual case" };
        var known = new List<string> { "check the claim" };
        var snapshot = InterviewAnswerSnapshot.Capture(10, fixture.Operation, profile, facts, vacancy, "HR", question, catalog, "conversation", "notes", known, Now);
        profile.Name = "Changed";
        facts.Facts[0].Answer = "Changed";
        vacancy.Description = "Changed";
        vacancy.Company!.Description = "Changed";
        question.Question = "Changed";
        catalog["S1"] = "Changed";
        known.Clear();
        fixture.Options.Routes[LlmPurpose.InterviewLow][0].Name = "changed/model";
        Assert.Equal("Synthetic", snapshot.Profile.Name);
        Assert.Equal("Minsk", snapshot.Facts.Facts[0].Answer);
        Assert.Equal("A backend vacancy", snapshot.Vacancy.Description);
        Assert.Equal("A factual company", snapshot.ToVacancy().Company!.Description);
        Assert.Equal("Расскажите о вашем опыте.", snapshot.Question.Question);
        Assert.Equal("A factual case", snapshot.Catalog["S1"]);
        Assert.Single(snapshot.Known);
        var json = JsonSerializer.Serialize(snapshot, InterviewAnswerJsonContext.Default.InterviewAnswerSnapshot);
        Assert.DoesNotContain("synthetic-key", json);
        Assert.DoesNotContain("provider.invalid", json);
        var restored = JsonSerializer.Deserialize(json, InterviewAnswerJsonContext.Default.InterviewAnswerSnapshot)!;
        Assert.Equal(snapshot.Recipe.Hash, InterviewAnswerContent.Recipe(fixture.Operation, restored).Hash);
        Assert.Equal(Now, restored.RenderedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_call_or_rewrite_outage_never_persists_a_placeholder_or_partial_answer(bool rewrite)
    {
        var fixture = await CreateAsync();
        fixture.Script.FailAt = rewrite ? 2 : 1;
        fixture.Script.WeakFirst = rewrite;
        var snapshot = Snapshot(fixture.Operation);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => fixture.Content.PrepareAsync(snapshot, 0, Ct));
        Assert.Empty(fixture.Store.Rows);
        Assert.Equal(rewrite ? 2 : 1, fixture.Script.Calls);
        Assert.Equal(snapshot.Operation.OperationId, Assert.Single(fixture.Usage.Attempts.Select(attempt => attempt.OperationId).Distinct()));
        fixture.Script.FailAt = 0;
        fixture.Script.WeakFirst = false;
        fixture.Health.ReportSuccess("synthetic/model");
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        var result = await fixture.Content.PrepareAsync(snapshot, 0, Ct);
        Assert.NotNull(result.Item.A);
        Assert.Empty(result.Item.StyleProblems);
        Assert.False(result.Item.Reused);
        Assert.Equal(Now, result.Item.GeneratedAt);
        Assert.Single(fixture.Store.Rows);
        var warm = await fixture.Content.PrepareAsync(snapshot, 0, Ct);
        Assert.True(warm.Item.Reused);
        Assert.Equal(0m, warm.CostUsd);
        Assert.Equal(rewrite ? 3 : 2, fixture.Script.Calls);
        Assert.All(fixture.Script.Bodies, body => Assert.Equal("max", body["reasoning"]?["effort"]?.GetValue<string>()));
    }

    [Theory]
    [InlineData("placeholder")]
    [InlineData("truncated")]
    [InlineData("wrong-question")]
    [InlineData("null-position-options")]
    [InlineData("unknown-evidence")]
    public async Task Bad_legacy_rows_are_replaced_but_valid_rows_keep_their_original_cache_key(string damage)
    {
        var fixture = await CreateAsync();
        var snapshot = Snapshot(fixture.Operation);
        var item = new PrepItem { Q = Question(), A = fixture.Script.Good().ToDomain(), Deps = ["location"], GeneratedAt = Now };
        if (damage == "placeholder") item.A = null;
        if (damage == "wrong-question") item.Q.Id = "different";
        if (damage == "null-position-options") item.A!.PositionOptions = null!;
        if (damage == "unknown-evidence") item.A!.EvidenceLevel = "alien";
        var key = "interview:10:about:" + snapshot.Recipe.Hash;
        fixture.Store.Rows[key] = new(damage == "truncated" ? "{" : JsonSerializer.Serialize(item, InterviewJsonContext.Default.PrepItem), ["location"]);
        var result = await fixture.Content.PrepareAsync(snapshot, 0, Ct);
        Assert.False(result.Item.Reused);
        Assert.Equal(1, fixture.Script.Calls);
        Assert.Equal("verified", result.Item.A!.EvidenceLevel);
        Assert.Single(fixture.Store.Rows);
        Assert.Equal(key, Assert.Single(fixture.Store.Rows.Keys));
    }

    [Fact]
    public async Task Used_facts_invalidate_only_their_answer_and_unused_changes_remain_warm()
    {
        var fixture = await CreateAsync();
        var first = Snapshot(fixture.Operation);
        await fixture.Content.PrepareAsync(first, 0, Ct);
        var unrelated = Snapshot(fixture.Operation);
        unrelated.Facts.Facts.Add(new() { Key = "hobby", Answer = "Running", Confirmed = true });
        Assert.True((await fixture.Content.PrepareAsync(unrelated, 0, Ct)).Item.Reused);
        Assert.Equal(1, fixture.Script.Calls);
        var related = Snapshot(fixture.Operation);
        related.Facts.Facts[0].Answer = "Remote from Minsk";
        Assert.False((await fixture.Content.PrepareAsync(related, 0, Ct)).Item.Reused);
        Assert.Equal(2, fixture.Script.Calls);
    }

    [Fact]
    public async Task Warm_output_is_rechecked_against_current_known_blockers_without_another_paid_call()
    {
        var fixture = await CreateAsync();
        await fixture.Content.PrepareAsync(Snapshot(fixture.Operation), 0, Ct);
        var changed = Snapshot(fixture.Operation);
        changed.Known = ["Источник не подтверждает личный вклад"];
        var output = await fixture.Content.PrepareAsync(changed, 0, Ct);
        Assert.True(output.Item.Reused);
        Assert.True(output.Item.RequiresApproval);
        Assert.Equal("blocker", output.Item.CheckLevel);
        Assert.Contains(changed.Known[0], output.Item.ApprovalReasons);
        Assert.Equal(1, fixture.Script.Calls);
    }

    [Fact]
    public async Task A_valid_legacy_value_is_reused_without_a_global_cache_version_bump()
    {
        var fixture = await CreateAsync();
        var snapshot = Snapshot(fixture.Operation);
        var item = new PrepItem { Q = Question(), A = fixture.Script.Good().ToDomain(), Deps = ["location"], GeneratedAt = Now };
        var key = "interview:10:about:" + snapshot.Recipe.Hash;
        fixture.Store.Rows[key] = new(JsonSerializer.Serialize(item, InterviewJsonContext.Default.PrepItem), ["location"]);
        Assert.True((await fixture.Content.PrepareAsync(snapshot, 0, Ct)).Item.Reused);
        Assert.Equal(0, fixture.Script.Calls);
    }

    [Fact]
    public async Task A_legitimate_unknown_personal_position_stays_cached_and_requires_the_candidate()
    {
        var fixture = await CreateAsync();
        var snapshot = Snapshot(fixture.Operation);
        var speech = fixture.Script.Good().ToDomain();
        speech.EvidenceLevel = "personal_unknown";
        speech.Start = "";
        speech.Core = "";
        speech.PositionOptions = ["Хочу больше ответственности за архитектуру", "Хочу углубить техническую экспертизу"];
        var item = new PrepItem { Q = Question(), A = speech, Deps = ["location"], GeneratedAt = Now };
        fixture.Store.Rows["interview:10:about:" + snapshot.Recipe.Hash] = new(JsonSerializer.Serialize(item, InterviewJsonContext.Default.PrepItem), ["location"]);
        var result = await fixture.Content.PrepareAsync(snapshot, 0, Ct);
        Assert.True(result.Item.Reused);
        Assert.True(result.Item.RequiresApproval);
        Assert.Equal("blocker", result.Item.CheckLevel);
        Assert.Equal(2, result.Item.A!.PositionOptions.Count);
        Assert.Equal(0, fixture.Script.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_or_mutated_snapshot_fails_before_a_paid_call(bool mutate)
    {
        var fixture = await CreateAsync();
        var snapshot = Snapshot(fixture.Operation);
        if (mutate) snapshot.Profile.Name = "Different candidate";
        else snapshot.Version = 2;
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Content.PrepareAsync(snapshot, 0, Ct));
        Assert.Equal(0, fixture.Script.Calls);
        Assert.Empty(fixture.Store.Rows);
    }

    [Fact]
    public async Task Native_inputs_detach_all_candidate_sources_and_roundtrip_with_frozen_recipes()
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        var profile = Snapshot(fixture.Operation).Profile;
        var facts = Snapshot(fixture.Operation).Facts;
        var vacancy = Vacancy();
        var catalog = new Dictionary<string, string> { ["S1"] = "a factual case" };
        var input = InterviewPreparationSteps.Capture(seed, fixture.Operation, profile, facts, 3, 4, vacancy, catalog, "conversation", "notes", "support", "research", "market");
        profile.Name = "Changed";
        facts.Facts[0].Answer = "Changed";
        vacancy.Company!.Description = "Changed";
        catalog.Clear();
        fixture.Options.Routes[LlmPurpose.Interview][0].Name = "changed/model";
        var json = JsonSerializer.Serialize(input, InterviewPreparationJsonContext.Default.InterviewPreparationInput);
        var restored = JsonSerializer.Deserialize(json, InterviewPreparationJsonContext.Default.InterviewPreparationInput)!;
        Assert.Equal("Synthetic Candidate", restored.AnswerSource.Profile.Name);
        Assert.Equal("Minsk", restored.AnswerSource.Facts.Facts[0].Answer);
        Assert.Equal("A factual company", restored.AnswerSource.ToVacancy().Company!.Description);
        Assert.Single(restored.AnswerSource.Catalog);
        Assert.DoesNotContain("synthetic-key", json);
        Assert.DoesNotContain("provider.invalid", json);
        Assert.Equal(input.Recipe.Hash, InterviewPreparationSteps.InputRecipe(fixture.Operation, seed, restored).Hash);
        Assert.Equal(5, seed.Recipe.Tasks.Count);
        Assert.Equal(Now, restored.AnswerSource.RenderedAt);
    }

    [Fact]
    public async Task Native_plan_warm_reuse_and_captured_date_survive_changes_to_deployment_routes()
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        var input = Input(seed, fixture.Operation);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        fixture.Options.Routes[LlmPurpose.Interview][0].Name = "changed/model";
        var first = await fixture.Steps.PlanAsync(seed, input, Ct);
        Assert.False(first.Reused);
        Assert.Equal("synthetic/model", first.Model);
        Assert.Equal(0.0001m, first.CostUsd);
        Assert.Equal(6, first.Plan.Questions.Count);
        var warm = await fixture.Steps.PlanAsync(seed, input, Ct);
        Assert.True(warm.Reused);
        Assert.Equal(0m, warm.CostUsd);
        Assert.Equal(1, fixture.Script.Calls);
        var body = Assert.Single(fixture.Script.Bodies);
        Assert.Contains("2026-10-01", body.ToJsonString());
        Assert.DoesNotContain("2026-10-02", body.ToJsonString());
        Assert.Equal("max", body["reasoning"]?["effort"]?.GetValue<string>());
        Assert.Equal(seed.Operation.OperationId, Assert.Single(fixture.Usage.Attempts).OperationId);
    }

    [Fact]
    public async Task Native_plan_outage_never_creates_a_checkpoint_or_bad_cache_value()
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        var input = Input(seed, fixture.Operation);
        fixture.Script.FailAt = 1;
        await Assert.ThrowsAsync<LlmUnavailableException>(() => fixture.Steps.PlanAsync(seed, input, Ct));
        Assert.Empty(fixture.Store.Rows);
        fixture.Script.FailAt = 0;
        fixture.Health.ReportSuccess("synthetic/model");
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.False((await fixture.Steps.PlanAsync(seed, input, Ct)).Reused);
        Assert.Equal(2, fixture.Script.Calls);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("null-questions")]
    [InlineData("null-evidence")]
    [InlineData("too-few")]
    public async Task Native_plan_rejects_bad_persisted_content_before_reuse(string damage)
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        var input = Input(seed, fixture.Operation);
        var plan = fixture.Script.Plan().ToDomain();
        if (damage == "null-questions") plan.Questions = null!;
        if (damage == "null-evidence") plan.Questions[0].Evidence = null!;
        if (damage == "too-few") plan.Questions.Clear();
        var recipe = InterviewPreparationSteps.PlanRecipe(fixture.Operation, input);
        fixture.Store.Rows[$"interview-plan:10:{recipe.Hash}"] = new(damage == "malformed" ? "{" : JsonSerializer.Serialize(plan, InterviewJsonContext.Default.InterviewPlan), []);
        Assert.False((await fixture.Steps.PlanAsync(seed, input, Ct)).Reused);
        Assert.Equal(1, fixture.Script.Calls);
    }

    [Theory]
    [InlineData("research")]
    [InlineData("market")]
    [InlineData("profile")]
    [InlineData("source")]
    [InlineData("date")]
    [InlineData("version")]
    [InlineData("operation")]
    public async Task Native_plan_rejects_changed_or_unsupported_capture_before_paying(string mutation)
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        var input = Input(seed, fixture.Operation);
        if (mutation == "research") input.CompanyResearch = "Changed";
        if (mutation == "market") input.MarketText = "Changed";
        if (mutation == "profile") input.AnswerSource.Profile.Name = "Changed";
        if (mutation == "source") seed.Source.DurationMinutes++;
        if (mutation == "date") seed.RenderedAt = Now.AddDays(1);
        if (mutation == "version") input.Version = 2;
        if (mutation == "operation") input.AnswerSource.Operation.OperationId = Guid.NewGuid();
        var error = await Record.ExceptionAsync(() => fixture.Steps.PlanAsync(seed, input, Ct));
        Assert.True(error is UnsupportedInterviewPreparationException or InterviewPreparationSupersededException);
        Assert.Equal(0, fixture.Script.Calls);
        Assert.Empty(fixture.Store.Rows);
    }

    [Fact]
    public async Task Native_answer_stage_preserves_saved_plan_and_reuses_committed_per_question_work()
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        var input = Input(seed, fixture.Operation);
        var plan = await fixture.Steps.PlanAsync(seed, input, Ct);
        plan.Plan.Questions[0].Id = " Q1 ";
        var saved = JsonSerializer.Serialize(plan, InterviewPreparationJsonContext.Default.InterviewPlanCheckpoint);
        var result = await fixture.Steps.AnswerAsync(seed, input, plan, Ct);
        Assert.Equal(saved, JsonSerializer.Serialize(plan, InterviewPreparationJsonContext.Default.InterviewPlanCheckpoint));
        Assert.Equal(Now, result.GeneratedAt);
        Assert.Equal("synthetic/model", result.Model);
        Assert.NotNull(result.Salary);
        Assert.All(result.Preparation, item => Assert.NotNull(item.A));
        var calls = fixture.Script.Calls;
        var warm = await fixture.Steps.AnswerAsync(seed, input, plan, Ct);
        Assert.Equal(calls, fixture.Script.Calls);
        Assert.All(warm.Preparation, item => Assert.True(item.Reused));
        Assert.True(warm.Salary!.Reused);
        Assert.Equal(plan.CostUsd, warm.CostUsd);
        Assert.All(fixture.Script.Bodies, body => Assert.Equal("max", body["reasoning"]?["effort"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Native_answer_outage_reuses_other_committed_answers_instead_of_rebuilding_the_pack()
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        var input = Input(seed, fixture.Operation);
        var plan = await fixture.Steps.PlanAsync(seed, input, Ct);
        fixture.Script.FailAt = 4;
        await Assert.ThrowsAsync<LlmUnavailableException>(() => fixture.Steps.AnswerAsync(seed, input, plan, Ct));
        var retained = fixture.Store.Rows.Keys.Where(key => key.StartsWith("interview:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(retained);
        fixture.Script.FailAt = 0;
        fixture.Health.ReportSuccess("synthetic/model");
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        var result = await fixture.Steps.AnswerAsync(seed, input, plan, Ct);
        Assert.All(retained, key => Assert.Equal(1, fixture.Store.Writes[key]));
        Assert.Contains(result.Preparation, item => item.Reused);
        Assert.Equal(Now, result.GeneratedAt);
    }

    [Fact]
    public async Task Source_witness_survives_postgres_precision_but_fences_business_edits_not_outputs()
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        var owner = seed.Operation.OperationId;
        var source = new Interview { Id = 10, ApplicationId = 30, Kind = InterviewKind.HrScreen, Status = InterviewStatus.Scheduled,
            CreatedAt = Now.AddTicks(7), PreparationRequestId = owner };
        Assert.True(seed.Source.Matches(source, owner));
        source.PackJson = "{}";
        source.Reminded = true;
        Assert.True(seed.Source.Matches(source, owner));
        source.DurationMinutes++;
        Assert.False(seed.Source.Matches(source, owner));
        source.DurationMinutes--;
        source.PreparationRevision++;
        Assert.False(seed.Source.Matches(source, owner));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("json")]
    [InlineData("owner")]
    [InlineData("revision")]
    [InlineData("policy")]
    public async Task Root_seed_rejects_damage_and_mismatched_ownership(string damage)
    {
        var fixture = await CreateAsync();
        var seed = Seed(fixture.Operation);
        if (damage == "policy") seed.Recipe.PolicyRevision++;
        var json = JsonSerializer.Serialize(seed, InterviewPreparationJsonContext.Default.InterviewPreparationSeed);
        var root = new InterviewPreparationRequest { Id = seed.Operation.OperationId, InterviewId = 10, SeedJson = json, SeedHash = CanonicalJson.Hash(json) };
        if (damage == "hash") root.SeedHash = "wrong";
        if (damage == "json") root.SeedJson = "{";
        if (damage == "owner") root.Id = Guid.NewGuid();
        if (damage == "revision") root.Revision++;
        Assert.Throws<UnsupportedInterviewPreparationException>(() => InterviewPreparationRequests.ReadSeed(root));
    }

    private static InterviewPreparationSeed Seed(JobFinderLlm operation)
    {
        var seed = new InterviewPreparationSeed
        {
            Source = InterviewSourceSnapshot.Capture(new Interview { Id = 10, ApplicationId = 30, Kind = InterviewKind.HrScreen, Status = InterviewStatus.Scheduled, CreatedAt = Now }),
            Operation = operation.CaptureOperation(), RenderedAt = Now,
        };
        seed.Recipe = InterviewPreparationSteps.Recipe(operation, seed);
        return seed;
    }

    private static InterviewPreparationInput Input(InterviewPreparationSeed seed, JobFinderLlm operation)
    {
        var source = Snapshot(operation);
        return InterviewPreparationSteps.Capture(seed, operation, source.Profile, source.Facts, 1, 1, source.ToVacancy(), source.Catalog, source.Conversation, source.Notes, "support", "research", "market");
    }

    private static InterviewAnswerSnapshot Snapshot(JobFinderLlm operation) => InterviewAnswerSnapshot.Capture(10, operation,
        new CandidateProfile { Name = "Synthetic Candidate", Title = "Backend Developer", Reviewed = true },
        new CandidateFacts { Facts = [new() { Key = "location", Answer = "Minsk", Confirmed = true }] }, Vacancy(), "HR", Question(),
        new Dictionary<string, string> { ["S1"] = "Работал над backend-сервисом, добавил атомарное обновление баланса и тесты на конкурентные запросы." }, "conversation", "notes", [], Now);

    private static Vacancy Vacancy() => new()
    {
        Id = 20, Title = "Backend Developer", Description = "A backend vacancy", CompanyName = "Synthetic company",
        Company = new() { Name = "Synthetic company", Description = "A factual company" }, Url = "https://vacancy.invalid/20", Fingerprint = "synthetic",
    };

    private static PlannedQuestion Question() => new() { Id = "about", Question = "Расскажите о вашем опыте.", Category = "hr", CandidateLevel = "strong", Evidence = ["S1"] };

    private static async Task<FixtureState> CreateAsync()
    {
        var time = new FakeTimeProvider(Now);
        var health = new ModelHealth(time);
        var script = new Script();
        Assert.Empty(CopilotRules.SpeechProblems(script.Good().ToDomain(), Question()));
        var options = new LlmOptions
        {
            ApiKey = "synthetic-key", Endpoint = "https://provider.invalid/completions", MaxAttempts = 1,
            Routes = LlmPurpose.All.ToDictionary(purpose => purpose, _ => new List<LlmModelOptions> { new() { Name = "synthetic/model", Reasoning = new() { Effort = "max" } } }),
        };
        var usage = new Usage();
        var llm = new JobFinderLlm(new LlmClient(new OpenRouterTransport(script, options), health, options, usage, NullLogger<LlmClient>.Instance), new PromptCatalog(Fixture.PromptsDir));
        var operation = await llm.ForOperationAsync(Ct);
        var store = new Store();
        var answers = new GeneratedAnswers(store, time);
        var steps = new InterviewPreparationSteps(llm, answers, null!, null!, new PathsOptions(), new LocalClock(time, "Europe/Minsk"), NullLogger<InterviewPreparationSteps>.Instance);
        return new(new InterviewAnswerContent(llm, answers, NullLogger.Instance), operation, options, store, script, health, time, usage, steps);
    }

    private sealed record FixtureState(InterviewAnswerContent Content, JobFinderLlm Operation, LlmOptions Options, Store Store, Script Script, ModelHealth Health, FakeTimeProvider Time, Usage Usage, InterviewPreparationSteps Steps);

    private sealed class Store : IGeneratedAnswerStore
    {
        public ConcurrentDictionary<string, StoredAnswer> Rows { get; } = new();
        public ConcurrentDictionary<string, int> Writes { get; } = new();
        private readonly ConcurrentDictionary<string, string> _hashes = new();
        public Task<StoredAnswer?> GetAsync(string key, Func<IReadOnlyList<string>, string> depsHash, CancellationToken ct) =>
            Task.FromResult(Rows.TryGetValue(key, out var row) && (!_hashes.TryGetValue(key, out var hash) || hash == depsHash(row.Deps)) ? row : null);
        public Task PutAsync(AnswerRequest request, Generated generated, string depsHash, CancellationToken ct)
        {
            Rows[request.Key] = new(generated.Value, generated.Deps);
            Writes.AddOrUpdate(request.Key, 1, (_, count) => count + 1);
            _hashes[request.Key] = depsHash;
            return Task.CompletedTask;
        }
        public Task<decimal> SavedUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Usage : ILlmUsageSink
    {
        public ConcurrentQueue<LlmAttempt> Attempts { get; } = new();
        public Task RecordAsync(LlmAttempt attempt, CancellationToken ct) { Attempts.Enqueue(attempt); return Task.CompletedTask; }
        public Task<decimal> SpentTodayUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private sealed class Script : HttpMessageHandler, IHttpClientFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public int FailAt { get; set; }
        public bool WeakFirst { get; set; }
        public List<JsonNode> Bodies { get; } = [];
        public InterviewPlanWire Plan() => new()
        {
            Role = "Backend Developer", OverallRisk = "low", Questions = Enumerable.Range(1, 6).Select(index => new InterviewPlanWireQuestionsItem
            {
                Id = "question-" + index, Question = "Расскажите об опыте " + index, Category = "hr", Priority = "medium", Likelihood = "medium",
                Basis = "likely", Risk = "medium", CandidateLevel = "strong", Evidence = ["S1"],
            }).ToList(),
        };
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        public SpokenAnswerWire Good() => new()
        {
            Start = "Я занимаюсь backend-разработкой на C# и .NET.",
            Core = "В последнем сервисе я работал с конкурентными запросами к балансу. Я объединил проверку и списание в одну атомарную операцию. Затем добавил тесты на повторные запросы, чтобы изменение баланса нельзя было выполнить дважды.",
            EvidenceLevel = "verified", UsedFacts = ["location"],
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("provider.invalid", request.RequestUri!.Host);
            var call = Interlocked.Increment(ref _calls);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            lock (Bodies) Bodies.Add(body);
            if (call == FailAt) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("synthetic outage") };
            var answer = Good();
            if (WeakFirst && call == 1) answer.Core = "Уточнить модель данных. Проверить индексы.";
            var content = body["response_format"]?["json_schema"]?["name"]?.GetValue<string>() == "interview_plan"
                ? JsonSerializer.Serialize(Plan(), GeneratedWireJsonContext.Default.InterviewPlanWire)
                : JsonSerializer.Serialize(answer, GeneratedWireJsonContext.Default.SpokenAnswerWire);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["model"] = "synthetic/model", ["choices"] = new JsonArray(new JsonObject
                    {
                        ["finish_reason"] = "stop", ["message"] = new JsonObject { ["content"] = content },
                    }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 50, ["cost"] = 0.0001 },
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
