using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;

namespace JobFinder.Tests.Core;

public sealed class SpokenStyleTests
{
    [Fact]
    public void Live_senior_speech_passes() =>
        Assert.Empty(SpokenStyle.Problems(
            "Да, с этим работал. Самый показательный пример у меня на игровой платформе: ставки шли конкурентно под нагрузкой больше пяти тысяч запросов в секунду, " +
            "и нельзя было списать деньги дважды. Я сделал так, чтобы проверка баланса и списание шли одной атомарной операцией, и двойные списания ушли.",
            SpokenStyle.Core));

    [Theory]
    [InlineData("Уточнить модель данных. Проверить индексы. Рассмотреть блокировки.", "заметка для себя")]
    [InlineData("Идемпотентность - это свойство операции давать тот же результат при повторе.", "определения")]
    [InlineData("Глубоко во внутреннее устройство DI не погружался, но с lifetimes работал.", "самоуничижение")]
    [InlineData("В рамках проекта я осуществлял разработку сервисов.", "канцелярит")]
    [InlineData("- Kafka\n- Outbox\n- ретраи", "список")]
    [InlineData("Работал с C#, .NET, ASP.NET Core, PostgreSQL, Redis, Kafka, RabbitMQ и Docker.", "перечисление")]
    public void Notes_definitions_hedges_and_lists_are_not_speech(string text, string expected) =>
        Assert.Contains(SpokenStyle.Problems(text, SpokenStyle.Core), p => p.Contains(expected));

    [Theory]
    [InlineData("gRPC и WebSocket не буду выдавать за свой production-опыт.")]
    [InlineData("Мой подтверждённый опыт тут в другом: я делал REST.")]
    [InlineData("Сейчас доступен для выхода, как указал в анкете.")]
    [InlineData("Статус занятости уточню на созвоне.")]
    [InlineData("Если для роли это критично, эту разницу лучше обозначить сразу.")]
    public void The_preparation_bookkeeping_is_not_said_aloud(string text) =>
        Assert.Contains(SpokenStyle.Problems(text, SpokenStyle.Core), p => p.Contains("оговорка"));

    [Theory]
    [InlineData("Я переносил данные профилей в Couchbase, и нагрузка на базу данных упала на шестьдесят процентов.")]
    [InlineData("У меня пять лет и семь месяцев коммерческого опыта, до шести лет не доходит, но задачи были сеньорские.")]
    public void Ordinary_words_are_not_flagged(string text) => Assert.Empty(SpokenStyle.Problems(text, SpokenStyle.Core));

    [Fact]
    public void An_honest_gap_is_allowed_only_on_a_confirmed_gap()
    {
        const string text = "С фронтенд-безопасностью в коммерческих проектах глубоко не погружался, но на бэкенде закрывал авторизацию и валидацию входных данных.";
        Assert.NotEmpty(SpokenStyle.Problems(text, SpokenStyle.Core));
        Assert.Empty(SpokenStyle.Problems(text, SpokenStyle.Core, gapAllowed: true));
    }

    [Fact]
    public void The_start_must_fit_its_seconds() =>
        Assert.Contains(SpokenStyle.Problems(string.Join(" ", Enumerable.Repeat("Я делал это на проекте.", 20)), SpokenStyle.Start),
            p => p.Contains("не укладывается"));
}

public sealed class CopilotRulesTests
{
    private static readonly CandidateProfile Profile = new()
    {
        Achievements = ["Устранил риск двойных списаний при конкурентных ставках под нагрузкой 5 000+ RPS.", "Ускорил пакетные расчёты в 6 раз."],
        Experience = [new ProfileJob { Company = "Custom Games Studio", Role = ".NET Developer", Period = "2023-2026", Highlights = "игровая платформа" }],
    };

    private static Dictionary<string, string> Catalog() => CopilotRules.Catalog(Profile, []);

    private static PlannedQuestion Q(string id, string category = "technical", string priority = "high", string level = "strong", StoryChoice? story = null) =>
        new() { Id = id, Question = "Вопрос " + id, Category = category, Priority = priority, Likelihood = "high", CandidateLevel = level, Story = story, Tests = "t" };

    private static SpokenAnswer A(params string[] verify) => new()
    {
        Start = "Да, делал.", Core = "Было так.", DoNotSay = [new DoNotSay { Say = "Я эксперт во всём", Instead = "Я делал вот это" }],
        FollowUps = [new FollowUpAnswer { Question = "Как тестировали?", Start = "Интеграционными тестами." }], Verify = [.. verify],
    };

    [Fact]
    public void The_catalog_cites_achievements_and_jobs()
    {
        var c = Catalog();
        Assert.StartsWith("Устранил риск", c["A1"]);
        Assert.StartsWith("Custom Games Studio", c["E1"]);
    }

    [Fact]
    public void The_fixed_questions_are_always_there_and_a_case_must_exist()
    {
        var plan = new InterviewPlan
        {
            Questions = [Q("idem", story: new StoryChoice { Ref = "A1" }), Q("ghost", story: new StoryChoice { Ref = "S9" }), .. Enumerable.Range(0, 20).Select(i => Q($"x{i}", priority: "low"))],
        };
        var (questions, reasons) = CopilotRules.Normalize(plan, Catalog());
        Assert.Equal(CopilotRules.FixedIds, questions.Take(CopilotRules.FixedIds.Count).Select(q => q.Id));
        Assert.Contains(CopilotRules.WhyHire, CopilotRules.FixedIds);
        Assert.Equal(CopilotRules.FixedIds.Count + CopilotRules.MaxQuestions, questions.Count);
        Assert.Equal("A1", questions.Single(q => q.Id == "idem").Story!.Ref);
        Assert.Null(questions.Single(q => q.Id == "ghost").Story);
        Assert.Contains(reasons["ghost"], r => r.Contains("S9"));
    }

    [Fact]
    public void Depth_without_proof_negotiation_and_gaps_wait_for_the_user()
    {
        const string sources = "Устранил риск двойных списаний";
        Assert.True(CopilotRules.Finish(Q("di", "technical_depth"), A(), [], [], sources).RequiresApproval);
        Assert.True(CopilotRules.Finish(Q(CopilotRules.SalaryId, "negotiation"), A(), [], [], sources).RequiresApproval);
        Assert.True(CopilotRules.Finish(Q("sec", level: "gap"), A(), [], [], sources).RequiresApproval);
        var clean = CopilotRules.Finish(Q("idem", story: new StoryChoice { Ref = "A1", SupportedClaims = ["двойные списания"] }), A(), [], [], sources);
        Assert.False(clean.RequiresApproval);
        var inferred = CopilotRules.Finish(Q("idem"), A("создавал отдельный IServiceScope на каждое сообщение консьюмера"), [], [], sources);
        Assert.True(inferred.RequiresApproval);
    }

    [Fact]
    public void Verify_keeps_only_concrete_inferred_claims() =>
        Assert.Equal(["создавал отдельный IServiceScope на каждое сообщение"],
            CopilotRules.CleanVerify(["PostgreSQL", "async/await", "устранил риск двойных списаний", "создавал отдельный IServiceScope на каждое сообщение"],
                "Устранил риск двойных списаний при конкурентных ставках"));

    [Fact]
    public void The_last_minute_screen_is_built_from_the_answers()
    {
        var c = new InterviewCopilot
        {
            Evidence = Catalog(),
            Priorities = [new InterviewPriority { Topic = "DI internals", Priority = "critical", CandidateLevel = "unknown", Reason = "спросили в чате" }],
            Preparation =
            [
                new PrepItem { Q = Q(CopilotRules.About, "hr", "critical"), A = new SpokenAnswer { Start = "Я бэкенд-разработчик.", Core = "Пять лет на .NET." } },
                new PrepItem { Q = Q("idem", story: new StoryChoice { Ref = "A1", Result = "двойные списания ушли" }), A = A() },
            ],
        };
        var m = CopilotRules.LastMinute(c);
        Assert.Equal("Я бэкенд-разработчик. Пять лет на .NET.", m.About);
        // The case the answers use first, then the resume's achievements; whole jobs (E*) are not "cases".
        Assert.Equal(["A1", "A2"], m.TopStories.Select(x => x.Ref));
        Assert.Equal("Да, делал.", Assert.Single(m.TopTopics).Line);
        Assert.Equal("Как тестировали?", Assert.Single(m.FollowUps).Topic);
        Assert.Equal("DI internals (не ясно)", Assert.Single(m.TopRisks));
        Assert.Single(m.DoNotSay);
    }
}

public sealed class HrPlaybookTests
{
    private static HrBank Bank() =>
        System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Fixture.PromptsDir, "hr_bank.json")), InterviewJsonContext.Default.HrBank)!;

    [Fact]
    public void The_bank_is_complete_and_its_facts_exist()
    {
        var bank = Bank();
        Assert.True(bank.Questions.Count >= 30);
        Assert.Equal(bank.Questions.Count, bank.Questions.Select(q => q.Id).Distinct().Count());
        Assert.All(bank.Questions, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Question));
            Assert.False(string.IsNullOrWhiteSpace(q.Why));
            Assert.False(string.IsNullOrWhiteSpace(q.Approach));
            Assert.False(string.IsNullOrWhiteSpace(q.Avoid));
            Assert.Contains(q.Frequency, new[] { "всегда", "часто", "редко" });
        });
        var keys = CandidateFacts.Draft(new CandidateProfile()).Facts.Select(f => f.Key).ToHashSet();
        Assert.All(bank.Questions.SelectMany(q => q.Facts), k => Assert.Contains(k, keys));
    }

    [Fact]
    public void The_bank_never_advises_lying()
    {
        var text = File.ReadAllText(Path.Combine(Fixture.PromptsDir, "hr_bank.json")).ToLowerInvariant();
        Assert.DoesNotContain("говорите, что есть", text);
        Assert.DoesNotContain("85-90%", text);
        Assert.DoesNotContain("две недели изучаете", text);
        Assert.Contains("не выдумывать", text);
    }

    [Theory]
    [InlineData("всегда", "critical", "high")]
    [InlineData("часто", "high", "high")]
    [InlineData("редко", "medium", "low")]
    public void Frequency_sets_priority_and_likelihood(string frequency, string priority, string likelihood)
    {
        var q = HrPlaybookService.ToPlanned(new HrQuestion { Id = "x", Question = "q", Category = "Работа с людьми", Frequency = frequency, Why = "w" });
        Assert.Equal((priority, likelihood, "behavioral", "w"), (q.Priority, q.Likelihood, q.Category, q.Tests));
    }

    [Fact]
    public void A_rehearsal_key_carries_its_scope()
    {
        Assert.True(InterviewProgress.TryParse(InterviewProgress.ItemKey(InterviewProgress.Scope(4), "di_scopes"), out var scope, out var id));
        Assert.Equal(("interview.4", "di_scopes"), (scope, id));
        Assert.True(InterviewProgress.TryParse(InterviewProgress.ItemKey(InterviewProgress.HrScope, "conflict"), out scope, out id));
        Assert.Equal(("hr", "conflict"), (scope, id));
        Assert.False(InterviewProgress.TryParse("no-scope", out _, out _));
    }

    [Fact]
    public void An_old_facts_file_gets_the_new_questions_and_keeps_its_answers()
    {
        var facts = new CandidateFacts { Facts = [new CandidateFact { Key = "salary", Question = "q", Answer = "от 3000", Confirmed = true }] };
        facts.WithStandardQuestions().WithStandardQuestions();
        Assert.Equal("от 3000", facts.Get("salary")!.Answer);
        Assert.Single(facts.Facts, f => f.Key == "hobby");
        Assert.Null(facts.Get("learning")!.Answer);
        Assert.False(facts.IsConfirmed("overtime"));
    }
}
