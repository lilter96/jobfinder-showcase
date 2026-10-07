using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Profile;
using JobFinder.Llm;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed class FingerprintTests
{
    private static CandidateFacts Facts(string salary, string hobby) => new()
    {
        Facts =
        [
            new CandidateFact { Key = "salary", Question = "q", Answer = salary, Confirmed = true },
            new CandidateFact { Key = "hobby", Question = "q", Answer = hobby, Confirmed = true },
        ],
    };

    [Fact]
    public void An_answer_changes_only_with_the_facts_it_depends_on()
    {
        var before = Fingerprint.OfFacts(Facts("от 3000", "бег"), ["salary"]);
        Assert.Equal(before, Fingerprint.OfFacts(Facts("от 3000", "шахматы"), ["salary"]));
        Assert.NotEqual(before, Fingerprint.OfFacts(Facts("от 3500", "бег"), ["salary"]));
        // Order and repeats of the keys do not matter; an unknown key hashes the same until it appears.
        Assert.Equal(Fingerprint.OfFacts(Facts("a", "b"), ["hobby", "salary", "hobby"]), Fingerprint.OfFacts(Facts("a", "b"), ["salary", "hobby"]));
        Assert.Equal(Fingerprint.OfFacts(Facts("a", "b"), ["hr_x"]), Fingerprint.OfFacts(Facts("c", "d"), ["hr_x"]));
    }

    [Fact]
    public void Confirming_a_fact_or_moving_the_funnel_is_a_change()
    {
        var facts = Facts("от 3000", "бег");
        var confirmed = Fingerprint.OfFacts(facts, ["salary", "funnel"], "2 интервью");
        Assert.NotEqual(confirmed, Fingerprint.OfFacts(facts, ["salary", "funnel"], "3 интервью"));
        facts.Get("salary")!.Confirmed = false;
        Assert.NotEqual(confirmed, Fingerprint.OfFacts(facts, ["salary", "funnel"], "2 интервью"));
    }

    [Fact]
    public void Parts_do_not_run_into_each_other()
    {
        Assert.NotEqual(Fingerprint.Of("ab", "c"), Fingerprint.Of("a", "bc"));
        Assert.NotEqual(Fingerprint.Of((string?)null), Fingerprint.Of(""));
        Assert.Equal(Fingerprint.Of("a", "b"), Fingerprint.Of("a", "b"));
    }
}

public sealed class GeneratedAnswersTests
{
    [Fact]
    public async Task A_content_gate_replaces_invalid_L1_and_L2_without_invalidating_other_keys()
    {
        var store = new MemoryStore();
        var answers = new GeneratedAnswers(store, new FakeTimeProvider());
        var request = new AnswerRequest("gate:key", "test", "key", _ => "deps");
        await answers.GetOrCreateAsync(request, _ => Task.FromResult(new Generated("invalid", [], "model", "purpose", "prompt", 0)), CancellationToken.None);
        var calls = 0;
        var repaired = await answers.GetOrCreateAsync(request, _ =>
        {
            calls++;
            return Task.FromResult(new Generated("valid", [], "model", "purpose", "prompt", 0));
        }, CancellationToken.None, value => value == "valid");
        Assert.Equal(("valid", false), repaired);
        var warm = await answers.GetOrCreateAsync(request, _ => throw new InvalidOperationException("warm cache must not call a provider"), CancellationToken.None, value => value == "valid");
        Assert.Equal(("valid", true), warm);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task An_invalid_new_result_is_not_written_or_remembered()
    {
        var store = new MemoryStore();
        var answers = new GeneratedAnswers(store, new FakeTimeProvider());
        var request = new AnswerRequest("gate:rejected", "test", "key", _ => "deps");
        await Assert.ThrowsAsync<InvalidOperationException>(() => answers.GetOrCreateAsync(request,
            _ => Task.FromResult(new Generated("invalid", [], "model", "purpose", "prompt", 0)), CancellationToken.None, value => value == "valid"));
        Assert.Empty(store.Rows);
        var good = await answers.GetOrCreateAsync(request, _ => Task.FromResult(new Generated("valid", [], "model", "purpose", "prompt", 0)), CancellationToken.None, value => value == "valid");
        Assert.False(good.Reused);
        Assert.Equal("valid", good.Value);
    }

    [Fact]
    public async Task A_stricter_concurrent_reader_cannot_receive_an_unchecked_shared_result()
    {
        var store = new MemoryStore();
        var answers = new GeneratedAnswers(store, new FakeTimeProvider());
        var request = new AnswerRequest("gate:shared", "test", "key", _ => "deps");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loose = answers.GetOrCreateAsync(request, async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return new Generated("invalid", [], "model", "purpose", "prompt", 0);
        }, CancellationToken.None);
        await started.Task;
        var strict = answers.GetOrCreateAsync(request, _ => throw new InvalidOperationException("same flight"), CancellationToken.None, value => value == "valid");
        release.TrySetResult();
        Assert.Equal("invalid", (await loose).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => strict);
    }

    private sealed class MemoryStore : IGeneratedAnswerStore
    {
        public readonly Dictionary<string, (string Value, IReadOnlyList<string> Deps, string Hash)> Rows = [];
        public int Reads;

        public Task<StoredAnswer?> GetAsync(string key, Func<IReadOnlyList<string>, string> depsHash, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Rows.TryGetValue(key, out var r) && r.Hash == depsHash(r.Deps) ? new StoredAnswer(r.Value, r.Deps) : null);
        }

        public Task PutAsync(AnswerRequest request, Generated generated, string depsHash, CancellationToken ct)
        {
            Rows[request.Key] = (generated.Value, generated.Deps, depsHash);
            return Task.CompletedTask;
        }

        public Task<decimal> SavedUsdAsync(CancellationToken ct) => Task.FromResult(0m);
    }

    private static AnswerRequest Request(Func<string> salary) => new("hr:salary:v1", "hr", "salary", deps => string.Join(",", deps) + "=" + salary());

    private static Generated Gen(string value) => new(value, ["salary"], "m", LlmPurpose.InterviewMedium, "p1", 0.01m);

    [Fact]
    public async Task The_same_answer_asked_twice_at_once_costs_one_call()
    {
        var answers = new GeneratedAnswers(new MemoryStore(), new FakeTimeProvider());
        var calls = 0;
        var release = new TaskCompletionSource();
        async Task<Generated> Slow(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return Gen("ответ");
        }

        var ct = TestContext.Current.CancellationToken;
        var first = answers.GetOrCreateAsync(Request(() => "3000"), Slow, ct);
        var second = answers.GetOrCreateAsync(Request(() => "3000"), Slow, ct);
        release.SetResult();
        Assert.Equal(new[] { ("ответ", false), ("ответ", false) }, await Task.WhenAll(first, second));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Memory_then_store_then_the_model()
    {
        var store = new MemoryStore();
        var time = new FakeTimeProvider();
        var salary = "3000";
        var calls = 0;
        Task<Generated> Generate(CancellationToken _) => Task.FromResult(Gen($"ответ {++calls}"));
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(("ответ 1", false), await new GeneratedAnswers(store, time).GetOrCreateAsync(Request(() => salary), Generate, ct));

        // A restart: the memory is gone, the store still has the answer.
        var restarted = new GeneratedAnswers(store, time);
        Assert.Equal(("ответ 1", true), await restarted.GetOrCreateAsync(Request(() => salary), Generate, ct));

        var answers = new GeneratedAnswers(store, time);
        await answers.GetOrCreateAsync(Request(() => salary), Generate, ct);
        var reads = store.Reads;
        Assert.Equal(("ответ 1", true), await answers.GetOrCreateAsync(Request(() => salary), Generate, ct));
        Assert.Equal(reads, store.Reads);

        // The fact the answer used changed: memory and store both miss, the answer is written again.
        salary = "3500";
        Assert.Equal(("ответ 2", false), await answers.GetOrCreateAsync(Request(() => salary), Generate, ct));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_failed_generation_is_not_remembered()
    {
        var answers = new GeneratedAnswers(new MemoryStore(), new FakeTimeProvider());
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            answers.GetOrCreateAsync(Request(() => "3000"), _ => throw new InvalidOperationException("model down"), ct));
        Assert.Equal(("ответ", false), await answers.GetOrCreateAsync(Request(() => "3000"), _ => Task.FromResult(Gen("ответ")), ct));
    }
}

public sealed class GeneratedAnswerStoreIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Rejected_persisted_content_does_not_increment_hits_or_change_the_usage_timestamp()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        IGeneratedAnswerStore store = new PgGeneratedAnswerStore(pg.Db, time);
        var request = new AnswerRequest("test:validated:" + Guid.NewGuid().ToString("N"), "test", "validated", _ => "deps");
        await store.PutAsync(request, new Generated("invalid", [], "model", LlmPurpose.InterviewLow, "prompt", 0), "deps", ct);
        await using var context = await pg.Db.CreateDbContextAsync(ct);
        var before = await context.GeneratedAnswers.AsNoTracking().SingleAsync(row => row.Key == request.Key, ct);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await store.GetAsync(request.Key, request.DepsHash, ct, value => value == "valid"));
        var rejected = await context.GeneratedAnswers.AsNoTracking().SingleAsync(row => row.Key == request.Key, ct);
        Assert.Equal(0, rejected.Hits);
        Assert.Equal(before.LastUsedAt, rejected.LastUsedAt);
        await store.PutAsync(request, new Generated("valid", [], "model", LlmPurpose.InterviewLow, "prompt", 0), "deps", ct);
        Assert.Equal("valid", (await store.GetAsync(request.Key, request.DepsHash, ct, value => value == "valid"))!.Value);
        Assert.Equal(1, (await context.GeneratedAnswers.AsNoTracking().SingleAsync(row => row.Key == request.Key, ct)).Hits);
    }

    [Fact]
    public async Task Recipe_provenance_roundtrips_in_the_migrated_jsonb_column_and_legacy_rows_still_read()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var ct = TestContext.Current.CancellationToken;
        var recipe = GenerationRecipe.Capture(new PromptCatalog(Fixture.PromptsDir), _ => "route-v1",
            [(LlmTaskCatalog.HrAnswer, LlmPurpose.InterviewLow)], new Dictionary<string, string?> { ["profile"] = "synthetic-private-profile" }, policyRevision: 7);
        var store = new PgGeneratedAnswerStore(pg.Db, TimeProvider.System);
        var request = new AnswerRequest("hr:recipe:" + recipe.Hash, "hr", "recipe", _ => "deps");
        await store.PutAsync(request, new Generated("{}", [], "actual-model", LlmPurpose.InterviewLow, "prompt", 0.004m, recipe), "deps", ct);
        await using var context = await pg.Db.CreateDbContextAsync(ct);
        var restored = await context.GeneratedAnswers.FirstAsync(answer => answer.Key == request.Key, ct);
        Assert.Equal(recipe.Hash, restored.Recipe!.Hash);
        Assert.Equal(7, restored.Recipe.PolicyRevision);
        Assert.Equal("actual-model", restored.Model);
        var columnType = await context.Database.SqlQuery<string>(
            $"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'generated_answers' AND column_name = 'recipe'").SingleAsync(ct);
        Assert.Equal("jsonb", columnType);
        Assert.DoesNotContain("synthetic-private-profile", JsonSerializer.Serialize(restored.Recipe, RecipeJsonContext.Default.ArtifactRecipe));
        var legacyRequest = new AnswerRequest("hr:legacy:no-recipe", "hr", "legacy", _ => "deps");
        await store.PutAsync(legacyRequest, new Generated("{}", [], "old-model", LlmPurpose.InterviewLow, "prompt", 0), "deps", ct);
        Assert.Null((await context.GeneratedAnswers.FirstAsync(answer => answer.Key == legacyRequest.Key, ct)).Recipe);
    }

    [Fact]
    public async Task Stored_answers_count_their_reuse_and_expire_with_their_facts()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var ct = TestContext.Current.CancellationToken;
        var store = new PgGeneratedAnswerStore(pg.Db, TimeProvider.System);
        var request = new AnswerRequest("hr:notice:v1", "hr", "notice", deps => string.Join(",", deps) + "=сразу");
        await store.PutAsync(request, new Generated("{}", ["notice", "hr_notice"], "m", LlmPurpose.InterviewLow, "p1", 0.004m), "notice,hr_notice=сразу", ct);

        Assert.Equal(["notice", "hr_notice"], (await store.GetAsync(request.Key, request.DepsHash, ct))!.Deps);
        Assert.Equal("{}", (await store.GetAsync(request.Key, request.DepsHash, ct))!.Value);
        Assert.Null(await store.GetAsync(request.Key, deps => string.Join(",", deps) + "=через месяц", ct));
        Assert.Null(await store.GetAsync("hr:other:v1", request.DepsHash, ct));
        Assert.Equal(0.008m, await store.SavedUsdAsync(ct));
    }
}

public sealed class AnswerTierTests
{
    private static PlannedQuestion Q(string category, string id = "x") => new() { Id = id, Question = "q", Category = category, Tests = "t" };

    [Theory]
    [InlineData("technical_depth", null, LlmPurpose.Interview)]
    [InlineData("system_design", null, LlmPurpose.InterviewHigh)]
    [InlineData("technical", null, LlmPurpose.InterviewHigh)]
    [InlineData("behavioral", null, LlmPurpose.InterviewMedium)]
    [InlineData("negotiation", null, LlmPurpose.InterviewMedium)]
    [InlineData("hr", null, LlmPurpose.InterviewLow)]
    [InlineData("hr", "medium", LlmPurpose.InterviewMedium)]
    [InlineData("technical_depth", "low", LlmPurpose.InterviewLow)]
    public void Reasoning_follows_the_kind_of_question(string category, string? effort, string purpose) =>
        Assert.Equal(purpose, CopilotRules.PurposeFor(Q(category), effort));

    [Fact]
    public void The_pitch_is_never_on_the_lowest_tier() =>
        Assert.Equal(LlmPurpose.InterviewMedium, CopilotRules.PurposeFor(Q("hr", CopilotRules.About)));

    [Fact]
    public void Every_tier_has_a_route()
    {
        var routes = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json")))!["Llm"]!["Routes"]!;
        Assert.All(new[] { LlmPurpose.Interview, LlmPurpose.InterviewHigh, LlmPurpose.InterviewMedium, LlmPurpose.InterviewLow }, p => Assert.NotNull(routes[p]));
    }
}

public sealed class PersonalPositionTests
{
    private static PlannedQuestion Q(string category = "technical", string level = "strong") =>
        new() { Id = "q", Question = "q", Category = category, Priority = "high", CandidateLevel = level, Tests = "t" };

    [Fact]
    public void An_unknown_personal_fact_asks_for_a_position_instead_of_a_speech()
    {
        var item = CopilotRules.PositionItem(Q("hr"), ["Разовые авралы нормально", "Не рассматриваю"], "нужна ваша позиция: переработки");
        Assert.False(item.HasSpeech);
        Assert.True(item.A!.NeedsPosition);
        Assert.Equal("blocker", CopilotRules.EffectiveCheck(item));
        Assert.Empty(CopilotRules.SpeechProblems(item.A, item.Q));
        Assert.NotEmpty(CopilotRules.SpeechProblems(new SpokenAnswer { EvidenceLevel = "personal_unknown", PositionOptions = ["одна"] }, item.Q));
    }

    [Fact]
    public void Inferred_details_are_a_quiet_review_and_decisions_are_blockers()
    {
        const string sources = "Устранил риск двойных списаний";
        var inferred = CopilotRules.Finish(Q(), new SpokenAnswer { Start = "Да.", Core = "Так.", Verify = ["создавал отдельный IServiceScope на каждое сообщение"] },
            [], [], sources);
        Assert.Equal(("review", true), (inferred.CheckLevel, inferred.RequiresApproval));

        var asked = CopilotRules.Finish(Q(), new SpokenAnswer { Start = "Да.", Core = "Так.", RequiresApproval = true, ApprovalReason = "проверьте цифру" },
            [], [], sources);
        Assert.Equal("review", asked.CheckLevel);
        asked = CopilotRules.Finish(Q(), new SpokenAnswer { Start = "Да.", Core = "Так.", EvidenceLevel = "verified", RequiresApproval = true }, [], [], sources);
        Assert.Equal("blocker", asked.CheckLevel);

        Assert.Equal("blocker", CopilotRules.Finish(Q("negotiation"), new SpokenAnswer { Start = "Да.", Core = "Так." }, [], [], sources).CheckLevel);
        Assert.Equal("blocker", CopilotRules.Finish(Q(), new SpokenAnswer { Start = "Да.", Core = "Так.", EvidenceLevel = "unsupported" }, [], [], sources).CheckLevel);
        Assert.Equal("none", CopilotRules.Finish(Q(), new SpokenAnswer { Start = "Да.", Core = "Так." }, [], [], sources).CheckLevel);
    }

    [Fact]
    public void A_build_from_before_the_levels_keeps_its_warning() =>
        Assert.Equal("blocker", CopilotRules.EffectiveCheck(new PrepItem { RequiresApproval = true }));

    [Fact]
    public void Bank_positions_and_efforts_are_well_formed()
    {
        var bank = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Fixture.PromptsDir, "hr_bank.json")), InterviewJsonContext.Default.HrBank)!;
        Assert.All(bank.Questions.Where(q => q.Options.Count > 0), q => Assert.True(q.Options.Count(o => !string.IsNullOrWhiteSpace(o)) >= 2, q.Id));
        Assert.All(bank.Questions.Where(q => q.Effort is not null), q => Assert.Contains(q.Effort!, new[] { "low", "medium", "high", "max" }));
        Assert.All(new[] { "overtime", "trips", "notice" }, id => Assert.NotEmpty(bank.Questions.Single(q => q.Id == id).Options));
        Assert.Equal(LlmPurpose.InterviewMedium, CopilotRules.PurposeFor(HrPlaybookService.ToPlanned(bank.Questions.Single(q => q.Id == "hardest_problem")),
            bank.Questions.Single(q => q.Id == "hardest_problem").Effort));
    }

    [Fact]
    public void A_position_goes_to_the_declared_fact_or_the_question_s_own() =>
        Assert.Equal("hr_conflict", HrPlaybookService.PositionFactKey("conflict"));
}
