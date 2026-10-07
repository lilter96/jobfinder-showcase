using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Resume;
using JobFinder.Core.Time;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;

namespace JobFinder.Tests.Core;

public sealed class LegitimacyRulesTests
{
    private static LegitimacyInput Input(string text = "Senior .NET разработчик в продуктовую команду", bool company = true, int reposts = 0,
        int? age = 5, int? responses = 20, decimal? ratio = 1.0m, string llm = "ok", params string[] reasons) =>
        new(text, company, reposts, age, responses, ratio, llm, reasons);

    [Fact]
    public void Ordinary_vacancy_is_ok() => Assert.Equal(Legitimacy.Ok, LegitimacyRules.Evaluate(Input()).Level);

    [Fact]
    public void Paid_training_is_scam()
    {
        var v = LegitimacyRules.Evaluate(Input("Стажировка .NET, обучение за свой счёт, потом трудоустройство"));
        Assert.Equal(Legitimacy.Scam, v.Level);
        Assert.Contains(v.Reasons, r => r.Contains("скам"));
    }

    [Fact]
    public void Model_scam_verdict_wins_over_clean_signals() =>
        Assert.Equal(Legitimacy.Scam, LegitimacyRules.Evaluate(Input(llm: "scam", reasons: "просят оплатить доступ")).Level);

    [Theory]
    [InlineData(3, 5, 1.0, true)] // reposted again and again
    [InlineData(0, 60, 1.0, true)] // open for two months
    [InlineData(0, 5, 1.6, true)] // pay far above the market
    [InlineData(0, 5, 1.0, false)] // no company name
    public void Ghost_job_signals_are_suspicious(int reposts, int age, double ratio, bool hasCompany)
    {
        var v = LegitimacyRules.Evaluate(Input(company: hasCompany, reposts: reposts, age: age, ratio: (decimal)ratio));
        Assert.Equal(Legitimacy.Suspicious, v.Level);
        Assert.NotEmpty(v.Reasons);
    }

    [Fact]
    public void Every_reason_is_kept_and_the_strongest_level_wins()
    {
        var v = LegitimacyRules.Evaluate(Input("Пассивный доход без опыта", company: false, reposts: 4));
        Assert.Equal(Legitimacy.Scam, v.Level);
        Assert.Equal(3, v.Reasons.Count);
    }
}

public sealed class MarketSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Percentiles_of_a_segment()
    {
        var points = new[] { 2000m, 2500m, 3000m, 3500m, 4000m, 4500m }.Select(p => new SalaryPoint("senior+", "remote-by", p));
        var s = MarketSnapshot.Build(points, Now).Lookup("senior+", "remote-by")!;
        Assert.Equal(6, s.Count);
        Assert.Equal(3250m, s.P50);
        Assert.Equal(50, s.PercentileOf(3000m));
        Assert.Equal(100, s.PercentileOf(9000m));
    }

    [Fact]
    public void Thin_segment_falls_back_to_the_roll_up()
    {
        var points = Enumerable.Range(0, 6).Select(i => new SalaryPoint("senior+", "remote-ru", 3000 + i * 100))
            .Append(new SalaryPoint("senior+", "minsk", 2800));
        var snapshot = MarketSnapshot.Build(points, Now);
        var s = snapshot.Lookup("senior+", "minsk")!;
        Assert.Equal("*", s.Geo);
        Assert.Equal(7, s.Count);
        Assert.Equal("*", snapshot.Lookup("junior", "minsk")!.Seniority);
    }

    [Fact]
    public void Outliers_are_not_market_data() =>
        Assert.Equal(0, MarketSnapshot.Build([new SalaryPoint("senior+", "minsk", 50), new SalaryPoint("senior+", "minsk", 90_000)], Now).Total);

    [Theory]
    [InlineData("Senior .NET Developer", "senior+")]
    [InlineData("Ведущий программист C#", "senior+")]
    [InlineData("Teamlead .NET", "senior+")]
    [InlineData(".NET разработчик (outstaff)", "any")]
    [InlineData("Middle C# developer", "middle")]
    [InlineData("C# разработчик валидации платежей", "any")]
    public void Seniority_from_title(string title, string expected) => Assert.Equal(expected, MarketSnapshot.SeniorityOf(title));

    [Fact]
    public void Geo_of_a_vacancy()
    {
        Assert.Equal("remote-by", MarketSnapshot.GeoOf(new Vacancy { Title = "t", Url = "u", Fingerprint = "f", WorkFormats = WorkFormat.Remote, CountryCode = "BY" }));
        Assert.Equal("minsk", MarketSnapshot.GeoOf(new Vacancy { Title = "t", Url = "u", Fingerprint = "f", WorkFormats = WorkFormat.Hybrid, City = "Минск" }));
    }
}

public sealed class ResumeFaithfulnessTests
{
    private const string Source = """
        Гацуков Терентий. Senior C#/.NET Backend, 5 лет.
        ООО «Альфа Софт»: ASP.NET Core, PostgreSQL, Kafka, Redis. Ускорил отчёты в 3 раза, 1 200 RPS, покрытие тестами 85%.
        ИП Бета: высоконагруженные системы, микросервисы, Docker, Kubernetes.
        """;

    private static ResumeDocument Doc(params string[] bullets) => new()
    {
        Language = "ru",
        Headline = "Senior .NET Backend",
        Summary = "Backend-разработчик, 5 лет в .NET.",
        Skills = [new SkillGroup { Group = "Backend", Items = ["ASP.NET Core", "Kafka", "gRPC", "RabbitMQ"] }],
        Experience = [new ResumeJob { Company = "ООО «Альфа Софт»", Role = "Senior .NET", Bullets = [.. bullets] }],
    };

    [Fact]
    public void Numbers_from_the_resume_pass_invented_ones_are_removed()
    {
        var r = ResumeFaithfulness.Check(Doc("Ускорил отчёты в 3 раза", "Держит 1200 RPS", "Сократил расходы на 40%"), Source, ["Kafka"], fix: true);
        var bullets = r.Document.Experience[0].Bullets;
        Assert.Equal(2, bullets.Count);
        Assert.DoesNotContain(bullets, b => b.Contains("40%"));
        Assert.Contains(r.Violations, v => v.Contains("40"));
    }

    [Fact]
    public void Unknown_skill_becomes_familiar_only_when_the_vacancy_asks_for_it()
    {
        var r = ResumeFaithfulness.Check(Doc("Ускорил отчёты в 3 раза"), Source, ["gRPC", "Kafka"], fix: true);
        var listed = r.Document.Skills.SelectMany(g => g.Items).ToList();
        Assert.Contains("Kafka", listed);
        Assert.DoesNotContain("gRPC", listed);
        Assert.Contains("gRPC", r.Document.Familiar);
        Assert.DoesNotContain("RabbitMQ", r.Document.Familiar);
        Assert.DoesNotContain("RabbitMQ", listed);
    }

    [Fact]
    public void Invented_employer_is_dropped()
    {
        var doc = Doc("Ускорил отчёты в 3 раза") with
        {
            Experience = [new ResumeJob { Company = "Google", Role = "Staff Engineer", Bullets = ["Сделал поиск"] }],
        };
        var r = ResumeFaithfulness.Check(doc, Source, [], fix: true);
        Assert.Empty(r.Document.Experience);
        Assert.Contains(r.Corrections, c => c.Contains("Google"));
    }

    [Fact]
    public void Coverage_counts_vacancy_keywords_in_the_final_text()
    {
        var r = ResumeFaithfulness.Check(Doc("Ускорил отчёты в 3 раза"), Source, ["Kafka", "ASP.NET Core", "Elasticsearch"], fix: true);
        Assert.Contains("Kafka", r.Covered);
        Assert.Contains("ASP.NET Core", r.Covered);
        Assert.Contains("Elasticsearch", r.Missing);
    }

    [Theory]
    [InlineData("1 200 RPS и 85%", new[] { "1200", "85" })]
    [InlineData("время ответа 99,5 мс", new[] { "99.5" })]
    [InlineData(".NET 8 и C# 12", new[] { "8", "12" })]
    public void Numbers_are_normalized(string text, string[] expected) => Assert.Equal(expected, ResumeFaithfulness.Numbers(text).ToArray());
}

public sealed class LocalDateParserTests
{
    private static readonly TimeZoneInfo Minsk = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");

    // Sunday 27.09.2026, 12:00 in Minsk.
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData("завтра 15:00", "2026-09-28 15:00")]
    [InlineData("пт 11:30", "2026-10-02 11:30")]
    [InlineData("28.09 14:00", "2026-09-28 14:00")]
    [InlineData("01.10.2026 9:30", "2026-10-01 09:30")]
    [InlineData("14.00", "2026-09-27 14:00")]
    [InlineData("10:00", "2026-09-28 10:00")] // already past today → tomorrow
    [InlineData("завтра в 15", "2026-09-28 15:00")]
    [InlineData("30.09 в 11", "2026-09-30 11:00")]
    [InlineData("послезавтра 16:15", "2026-09-29 16:15")]
    public void Parses_what_people_type(string input, string expected)
    {
        Assert.True(LocalDateParser.TryParse(input, Now, Minsk, out var at));
        Assert.Equal(expected, at.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromHours(3), at.Offset);
    }

    [Theory]
    [InlineData("когда-нибудь")]
    [InlineData("25:00")]
    [InlineData("")]
    public void Does_not_guess(string input) => Assert.False(LocalDateParser.TryParse(input, Now, Minsk, out _));
}

public sealed class FunnelRulesTests
{
    private static Application App(ApplicationStage stage, DateTimeOffset? inbound = null) => new()
    {
        Stage = stage, LastInboundAt = inbound, Status = ApplicationStatus.Sent, SentAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Funnel_stats_count_answers_interviews_offers()
    {
        var s = FunnelStats.Of(
        [
            App(ApplicationStage.Sent), App(ApplicationStage.Viewed), App(ApplicationStage.Replied), App(ApplicationStage.HrScreen),
            App(ApplicationStage.Offer), App(ApplicationStage.Rejected), App(ApplicationStage.NoResponse),
        ]);
        Assert.Equal(7, s.Sent);
        Assert.Equal(4, s.Replied); // replied, HR, offer, rejection (an answer too)
        Assert.Equal(2, s.Interviews);
        Assert.Equal(1, s.Offers);
        Assert.Equal(1, s.Rejected);
    }

    [Theory]
    [InlineData("2026-09-21", "2026-09-28", 5)] // Monday → next Monday
    [InlineData("2026-09-25", "2026-09-28", 1)] // Friday → Monday
    [InlineData("2026-09-26", "2026-09-27", 0)] // weekend only
    public void Business_days(string from, string to, int expected) =>
        Assert.Equal(expected, FollowUpJob.BusinessDays(DateTime.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            DateTime.Parse(to, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(ApplicationStage.Rejected, true)]
    [InlineData(ApplicationStage.NoResponse, true)]
    [InlineData(ApplicationStage.Offer, false)]
    public void Terminal_stages(ApplicationStage stage, bool terminal) => Assert.Equal(terminal, FunnelService.IsTerminal(stage));

    [Theory]
    [InlineData("@hr_anna", ContactKind.Telegram, "@hr_anna")]
    [InlineData("t.me/recruiter_bob", ContactKind.Telegram, "@recruiter_bob")]
    [InlineData("jobs@acme.io", ContactKind.Email, "jobs@acme.io")]
    [InlineData("https://www.linkedin.com/in/anna-hr-123/", ContactKind.LinkedIn, "https://www.linkedin.com/in/anna-hr-123")]
    [InlineData("+375 (29) 123-45-67", ContactKind.Phone, "+375291234567")] // E.164
    public void Post_contacts_are_classified(string raw, ContactKind kind, string value)
    {
        var c = JobFinder.Core.Contacts.ContactExtractor.Classify(raw);
        Assert.NotNull(c);
        Assert.Equal(kind, c.Kind);
        Assert.Equal(value, c.Value);
    }

    [Fact]
    public void Plain_words_are_not_contacts() => Assert.Null(JobFinder.Core.Contacts.ContactExtractor.Classify("пишите в личку"));
}

public sealed class OutreachMatchTests
{
    [Theory]
    [InlineData("Wargaming", "Recruiter - EPAM Systems | Текущая: Recruiter в EPAM Systems", false)] // LinkedIn returns recruiters in general
    [InlineData("Wargaming", "Talent Acquisition Partner | Текущая: TA Partner в Wargaming", true)]
    [InlineData("EPAM Systems", "Senior Recruiter at EPAM Systems", true)]
    [InlineData("EPAM Systems", "Recruiter at Systems Integration Ltd", false)] // "systems" alone is not the company
    [InlineData("ООО «Рога и Копыта»", "HR-менеджер | Текущая: HR в Рога и Копыта", true)]
    [InlineData("Леста Игры", "Recruiter– Леста Игры", true)]
    [InlineData("iTechArt Group", "HR manager - TechArt", false)] // another brand, not a substring match
    public void Outreach_keeps_only_people_of_the_company(string company, string text, bool expected) =>
        Assert.Equal(expected, OutreachService.WorksAt(company, text));
}

public sealed class TelegramAccountMatchTests
{
    [Theory]
    [InlineData("EPAM Systems", "EPAM HR epam_hr", true)]
    [InlineData("Wargaming", "Wargaming Recruiting wargaminghr", true)]
    [InlineData("EPAM Systems", "IT Recruiter Minsk it_recruiter_by", false)]
    public void Telegram_account_named_after_the_company(string company, string text, bool expected) =>
        Assert.Equal(expected, OutreachService.TelegramAccountOf(company, text));
}

public sealed class OutreachPastEmployerTests
{
    [Theory]
    [InlineData("Wargaming", "Nina Gritsuk | Talent Acquisition Manager | Минск | Прежняя: Senior Technical Recruiter – Wargaming", false)]
    [InlineData("Wargaming", "Anna | Recruiter | Past: Talent Acquisition at Wargaming", false)]
    [InlineData("Wargaming", "Anna | Recruiter, ex-Wargaming | Минск", false)]
    [InlineData("Wargaming", "Anna | Recruiter | Минск | Текущая: Recruiter – Wargaming", true)]
    public void Former_employees_are_not_contacts(string company, string text, bool expected) =>
        Assert.Equal(expected, OutreachService.WorksAt(company, text));
}

public sealed class LinkedInReadingTests
{
    private static readonly List<JobFinder.Integrations.LinkedIn.LinkedInCard> Cards =
    [
        new("https://www.linkedin.com/in/mariia", "Mariia Lukhverchik\n• 2-й\nSenior Recruiter at EPAM Systems\nМинск\nТекущая: Senior Recruiter – EPAM Systems"),
        new("https://www.linkedin.com/in/anastasiya", "Anastasiya Legenkaya\nRecruiter– Леста Игры\nМинск\nТекущая: Recruiter – Lesta Games"),
        new("https://www.linkedin.com/in/antanina", "Antanina Ivanova"),
    ];

    private static JobFinder.Llm.Tasks.LinkedInPersonReading R(int index, string name, string company, bool works = true, bool leave = false) =>
        new() { Index = index, IsResult = true, Name = name, Headline = "", CurrentCompany = company, IsRecruiter = true, WorksAtTarget = works, OnLeave = leave };

    private static List<JobFinder.Pipeline.Career.CompanyPerson> Accept(string target, params JobFinder.Llm.Tasks.LinkedInPersonReading[] readings) =>
        JobFinder.Pipeline.Career.LinkedInPeopleReader.Accept(target, Cards, new JobFinder.Llm.Tasks.LinkedInPeople { People = [.. readings] });

    [Fact]
    public void Grounded_reading_is_accepted_with_the_link_from_the_page()
    {
        var p = Assert.Single(Accept("EPAM Systems", R(1, "Mariia Lukhverchik", "EPAM Systems")));
        Assert.Equal("https://www.linkedin.com/in/mariia", p.ProfileUrl);
    }

    [Fact]
    public void Invented_name_or_employer_is_dropped()
    {
        Assert.Empty(Accept("EPAM Systems", R(1, "Maria Ivanova", "EPAM Systems")));
        Assert.Empty(Accept("EPAM Systems", R(3, "Antanina Ivanova", "EPAM Systems")));
    }

    [Fact]
    public void A_related_company_is_not_the_target_even_if_the_model_says_so() =>
        Assert.Empty(Accept("Wargaming", R(2, "Anastasiya Legenkaya", "Lesta Games")));

    [Fact]
    public void Same_company_in_another_alphabet_is_accepted() =>
        Assert.Single(Accept("Леста Игры", R(2, "Anastasiya Legenkaya", "Lesta Games")));

    [Fact]
    public void On_leave_and_bad_index_are_dropped()
    {
        Assert.Empty(Accept("EPAM Systems", R(1, "Mariia Lukhverchik", "EPAM Systems", leave: true)));
        Assert.Empty(Accept("EPAM Systems", R(9, "Mariia Lukhverchik", "EPAM Systems")));
    }

    [Theory]
    [InlineData("Яндекс", "Yandex", true)]
    [InlineData("EPAM", "EPAM Systems", true)]
    [InlineData("Wargaming", "Lesta Games", false)]
    [InlineData("iTechArt Group", "TechArt", false)]
    public void Same_company(string target, string employer, bool expected) =>
        Assert.Equal(expected, JobFinder.Pipeline.Career.LinkedInPeopleReader.SameCompany(target, employer));
}

public sealed class CommentStyleTests
{
    [Theory]
    [InlineData("Хуже звонка после 19:00 только голосовое без контекста. Если время текстом согласовать не могут, как у них таски ставятся?")]
    [InlineData("Классика: правая рука не знает, что делает левая, но бюджет уже горит 😂 Чем с \"самым главным\" кончилось? Перевесили 25% этот цирк?")]
    [InlineData("Когда компания путается в статусах кандидатов, на проектах обычно тот же бардак. Но ради +25% сходить посмотреть на цирк — святое дело)")]
    public void Live_comments_pass(string variant) => Assert.Empty(CommentStyle.Problems(variant, technicalThread: false));

    [Theory]
    [InlineData("Согласен: первый контакт уже задаёт ожидания.", "менторский")]
    [InlineData("Важен не только час звонка, но и возможность кандидата выбрать формат.", "менторский")]
    [InlineData("Такие истории хорошо показывают, как важна аккуратная фиксация статусов.", "менторский")]
    [InlineData("В работе с распределёнными системами я ценю идемпотентность; в найме тоже полезно.", "профессия")]
    [InlineData("Как разработчик на .NET скажу: это хаос.", "профессия")]
    public void Lectures_and_forced_stack_are_caught(string variant, string expected) =>
        Assert.Contains(CommentStyle.Problems(variant, technicalThread: false), p => p.Contains(expected));

    [Fact]
    public void Stack_words_are_fine_in_a_technical_thread() =>
        Assert.Empty(CommentStyle.Problems("На .NET 8 это решается одним middleware, а транзакции тут вообще ни при чём?", technicalThread: true));

    [Fact]
    public void Long_and_wordy_variants_are_caught()
    {
        var problems = CommentStyle.Problems("Раз. Два. Три. Четыре. " + new string('а', 230), technicalThread: false);
        Assert.Contains(problems, p => p.Contains("длиннее"));
        Assert.Contains(problems, p => p.Contains("предложений"));
    }

    [Fact]
    public void A_set_needs_a_question_and_at_most_one_emoji()
    {
        var problems = CommentStyle.SetProblems(["Цирк 😂", "Классика 🤡", "Бывает."]);
        Assert.Contains(problems, p => p.Contains("вопрос"));
        Assert.Contains(problems, p => p.Contains("эмодзи"));
        Assert.Empty(CommentStyle.SetProblems(["Цирк 😂", "Чем кончилось?", "Бывает."]));
    }

    [Theory]
    [InlineData("Если мне звонит HR в нерабочее время, я считываю это как знак, что переработки у них норма", true)]
    [InlineData("В ответ @laiqforme Знаю и то и другое, но С# увереннее, так что выбираю юнити наверное", false)]
    [InlineData("어떤 언어로 주로 개발하느냐에 따라서 정말로 다른 것 같아요. C/C++/C#/Go/Rust 까지는", false)]
    [InlineData("mano, vocês são chatos demais oh kkkkk Neymar não pode fazer nada", false)]
    public void Only_russian_top_level_posts_are_candidates(string post, bool expected) =>
        Assert.Equal(expected, JobFinder.Pipeline.Workers.CommentPlanner.IsCandidate(post));

    [Fact]
    public void Copying_a_prompt_example_is_caught()
    {
        var examples = JobFinder.Pipeline.Workers.CommentPlanner.ExamplesOf(
            File.ReadAllText(Path.Combine(Fixture.PromptsDir, "threads_comment.md")));
        Assert.NotEmpty(examples);
        Assert.True(CommentStyle.CopiesExample("Звонок без предупреждения в 2026 выглядит как пожар на проде, ну серьёзно", examples));
        Assert.False(CommentStyle.CopiesExample("Вечерний звонок от HR — это как деплой в пятницу: формально можно, но зачем", examples));
    }
}

public sealed class HumanTextTests
{
    [Theory]
    [InlineData("Звонок после 19 — уже собеседование", "Звонок после 19 - уже собеседование")]
    [InlineData("2025–2026, опыт 5+ лет", "2025-2026, опыт 5+ лет")]
    [InlineData("Ну такое…", "Ну такое...")]
    [InlineData("обычный текст - с дефисом", "обычный текст - с дефисом")]
    public void Generated_typography_is_replaced(string input, string expected)
    {
        Assert.Equal(expected, JobFinder.Core.Text.HumanText.Clean(input));
        Assert.True(JobFinder.Core.Text.HumanText.IsClean(expected));
    }

    [Fact]
    public void Comment_prompt_samples_have_no_dashes()
    {
        var prompt = File.ReadAllLines(Path.Combine(Fixture.PromptsDir, "threads_comment.md"));
        Assert.All(prompt.Where(l => l.StartsWith("Хорошо:") || l.StartsWith("Плохо:")), l => Assert.True(JobFinder.Core.Text.HumanText.IsClean(l), l));
    }
}

public sealed class EmailBodyTests
{
    [Fact]
    public void Notification_html_gives_the_message_not_the_css()
    {
        const string html = """
            <html><head><title>Письмо</title><style>#outlook a { padding:0; } body { margin:0; }</style></head>
            <body><!-- preheader --><table><tr><td><p>Новое сообщение от работодателя</p>
            <p>Добрый день! Готовы пригласить вас на собеседование во вторник в 15:00.</p></td></tr></table>
            <script>track()</script></body></html>
            """;
        var text = JobFinder.Integrations.Email.EmailSender.BodyText("", html);
        Assert.Contains("Готовы пригласить вас на собеседование", text);
        Assert.DoesNotContain("padding", text);
        Assert.DoesNotContain("track()", text);
        Assert.DoesNotContain("Письмо", text);
    }

    [Fact]
    public void A_real_text_part_wins() =>
        Assert.StartsWith("Здравствуйте", JobFinder.Integrations.Email.EmailSender.BodyText("Здравствуйте! " + new string('х', 300), "<p>другое</p>"));
}

public sealed class DialogueTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero); // 12:00 in Minsk
    private static readonly DateTimeOffset Local = Now.ToOffset(TimeSpan.FromHours(3));

    private static JobFinder.Core.Profile.CandidateFacts Facts(bool salaryConfirmed = true) => new()
    {
        Facts =
        [
            new() { Key = "salary", Question = "Зарплатные ожидания", Answer = "от 3000 USD на руки", Confirmed = salaryConfirmed },
            new() { Key = "notice", Question = "Когда готовы выйти на работу", Answer = null },
            new() { Key = "call_slots", Question = "Когда удобно созвониться", Answer = "будни после 17:00", Confirmed = true },
        ],
    };

    private static JobFinder.Llm.Tasks.ReplyAnalysis Reply(string intent = "facts", string draft = "Добрый день! Ожидания от 3000 USD на руки.",
        bool routine = true, string[]? used = null, string[]? missing = null) => new()
    {
        Summary = "s", Intent = intent, Routine = routine, Draft = draft, UsedFacts = [.. used ?? ["salary"]], MissingFacts = [.. missing ?? []],
    };

    private static JobFinder.Pipeline.Career.AutoReplyDecision Decide(JobFinder.Llm.Tasks.ReplyAnalysis a, JobFinder.Core.Profile.CandidateFacts? f = null,
        DateTimeOffset? at = null, DateTimeOffset? local = null, int inARow = 0) =>
        JobFinder.Pipeline.Career.DialoguePolicy.Decide(a, f ?? Facts(), true, EventChannel.HeadHunter, at ?? Now.AddMinutes(-5), Now, local ?? Local, inARow);

    [Fact]
    public void Routine_with_confirmed_facts_goes_out_on_its_own() => Assert.True(Decide(Reply()).Send);

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("missing")]
    [InlineData("open")]
    [InlineData("night")]
    [InlineData("third")]
    [InlineData("old")]
    [InlineData("gap")]
    public void Everything_else_waits_for_the_user(string why)
    {
        var d = why switch
        {
            "unconfirmed" => Decide(Reply(), Facts(salaryConfirmed: false)),
            "missing" => Decide(Reply(missing: ["notice"])),
            "open" => Decide(Reply(intent: "open_question", routine: false)),
            "night" => Decide(Reply(), local: new DateTimeOffset(2026, 9, 28, 23, 30, 0, TimeSpan.FromHours(3))),
            "third" => Decide(Reply(), inARow: 2),
            "old" => Decide(Reply(), at: Now.AddDays(-1)),
            _ => Decide(Reply(draft: "Выйти смогу через [срок выхода].")),
        };
        Assert.False(d.Send);
        Assert.False(string.IsNullOrWhiteSpace(d.Reason));
    }

    [Fact]
    public void A_stack_dump_is_not_a_chat_reply()
    {
        const string dump = "Здравствуйте, Ольга! Да, вакансию рассматриваю и готов обсудить детали здесь в чате. У меня более 5 лет опыта разработки на C#/.NET, включая REST API, PostgreSQL и MSSQL, асинхронные сервисы и интеграции через Kafka и RabbitMQ. Буду рад ответить на ваши вопросы.";
        var problems = ChatStyle.Problems(dump);
        Assert.Contains(problems, p => p.Contains("стека"));
        Assert.Contains(problems, p => p.Contains("шаблонная"));
        Assert.Empty(ChatStyle.Problems("Здравствуйте! Да, интересно, давайте обсудим. Я в Беларуси, удалёнка из РБ у вас возможна?"));
    }

    [Theory]
    [InlineData("С ASP.NET Core работал, но в резюме не указаны конкретные нестандартные задачи. [уточнить релевантный практический пример]", "заглушка")]
    [InlineData("С ASP.NET Core работал, но в резюме не указаны конкретные нестандартные задачи.", "пометка")]
    [InlineData("Выйти смогу через (указать срок) после оффера.", "заглушка")]
    [InlineData("Кандидат готов к созвону в среду.", "пометка")]
    [InlineData("Если про исходники контейнера, в них не углублялся, но с lifetimes работал.", "оговорка")]
    [InlineData("С DI работал на базовом уровне.", "оговорка")]
    [InlineData("До изучения внутренней реализации DI-контейнера на уровне исходников не доходило.", "оговорка")]
    public void Gaps_and_notes_are_not_an_answer(string draft, string expected) =>
        Assert.Contains(ChatStyle.Problems(draft, considered: true), p => p.Contains(expected));

    [Fact]
    public void A_considered_answer_from_experience_passes() =>
        Assert.Empty(ChatStyle.Problems(
            "Понял. Я в Минске и переезд не рассматриваю, если можно работать из Беларуси по договору или через ИП, мне подходит. " +
            "Из нестандартного: на платёжном API парковки делал middleware идемпотентности, чтобы повтор запроса с тем же ключом не списывал деньги второй раз. " +
            "В DI чаще всего разбирался с временем жизни, когда scoped-зависимость случайно попадала в singleton.", considered: true));

    [Fact]
    public void Honest_other_stack_is_not_a_hedge() =>
        Assert.Empty(ChatStyle.Problems("С Go в продакшене не работал, у меня всё на .NET.", considered: true));

    [Fact]
    public void Draft_version_is_stable_and_tells_drafts_apart()
    {
        var v = JobFinder.Pipeline.Career.ReplyService.DraftVersion("Понял, что удалёнка только по РФ.");
        Assert.Equal(v, JobFinder.Pipeline.Career.ReplyService.DraftVersion("Понял, что удалёнка только по РФ."));
        Assert.NotEqual(v, JobFinder.Pipeline.Career.ReplyService.DraftVersion("Понял, что удалёнка только по РФ!"));
        Assert.NotEqual(0, JobFinder.Pipeline.Career.ReplyService.DraftVersion(null));
    }

    [Fact]
    public void A_conflict_of_conditions_is_never_answered_automatically()
    {
        var a = Reply();
        a.Conflicts = ["Удалёнка только из РФ, а вы в Минске"];
        var d = Decide(a);
        Assert.False(d.Send);
        Assert.Contains("расхождение", d.Reason);
    }

    [Fact]
    public void Facts_draft_from_the_profile_confirms_nothing()
    {
        var profile = new JobFinder.Core.Profile.CandidateProfile
        {
            Location = "Минск", YearsOfExperience = 5.58, EnglishLevel = "B1", WorkFormats = ["remote", "hybrid"],
            DesiredSalary = new JobFinder.Core.Profile.DesiredSalary { Amount = 3000, Currency = "USD", Net = true },
        };
        var facts = JobFinder.Core.Profile.CandidateFacts.Draft(profile);
        Assert.All(facts.Facts, f => Assert.False(f.Confirmed));
        Assert.Contains("3", facts.Get("salary")!.Answer);
        Assert.Contains("на руки", facts.Get("salary")!.Answer);
        Assert.Equal("5+ лет коммерческой разработки на C#/.NET", facts.Get("experience")!.Answer);
        Assert.Null(facts.Get("notice")!.Answer);
        Assert.False(facts.IsConfirmed("salary"));
    }
}

public sealed class FactsDocumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Facts_round_trip_in_camel_case_with_readable_cyrillic()
    {
        var documents = new MemoryDocumentStore();
        var profiles = new JobFinder.Pipeline.Profile.ProfileProvider(documents, new JobFinder.Core.Options.PathsOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JobFinder.Pipeline.Profile.ProfileProvider>.Instance, documents);
        var store = new JobFinder.Pipeline.Profile.FactsProvider(documents,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JobFinder.Pipeline.Profile.FactsProvider>.Instance, documents);

        await store.SaveAsync(new JobFinder.Core.Profile.CandidateFacts { Facts = [new() { Key = "salary", Question = "q", Answer = "от 3000 USD", Confirmed = true }] }, 0, "test", Ct);

        var json = (await documents.GetAsync(JobFinder.Pipeline.Profile.FactsProvider.Key, Ct))!.Json;
        Assert.Contains("\"facts\"", json);
        Assert.Contains("от 3000 USD", json);
        Assert.True((await store.GetAsync(Ct)).IsConfirmed("salary"));
    }

    [Fact]
    public async Task Without_a_profile_there_is_nothing_to_draft_and_nothing_is_stored()
    {
        var documents = new MemoryDocumentStore();
        var profiles = new JobFinder.Pipeline.Profile.ProfileProvider(documents, new JobFinder.Core.Options.PathsOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JobFinder.Pipeline.Profile.ProfileProvider>.Instance, documents);
        var store = new JobFinder.Pipeline.Profile.FactsProvider(documents,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JobFinder.Pipeline.Profile.FactsProvider>.Instance, documents);

        var facts = await store.GetAsync(Ct);

        Assert.NotEmpty(facts.Facts); // the standard questions, all open
        Assert.DoesNotContain(facts.Facts, f => f.Confirmed);
        Assert.Null(await documents.VersionAsync(JobFinder.Pipeline.Profile.FactsProvider.Key, Ct));
    }
}

public sealed class DocumentCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Doc
    {
        public string Name { get; set; } = "";

        public int Loaded { get; set; }
    }

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<Doc> Type =
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Doc>)new System.Text.Json.JsonSerializerOptions
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        }.GetTypeInfo(typeof(Doc));

    [Fact]
    public async Task A_read_fetches_the_text_only_after_a_write_and_prepares_each_version_once()
    {
        var store = new MemoryDocumentStore();
        var cache = new JobFinder.Core.Storage.DocumentCache<Doc>(store, "t.doc", Type,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, loaded: d => d with { Loaded = d.Loaded + 1 });
        await store.SaveAsync("t.doc", """{"Name":"a"}""", Ct);

        var first = await cache.GetAsync(Ct);
        var again = await cache.GetAsync(Ct);
        Assert.Same(first, again);
        Assert.Equal(("a", 1, 1), (first!.Name, first.Loaded, store.Reads));

        await store.SaveAsync("t.doc", """{"Name":"b"}""", Ct); // another process wrote
        Assert.Equal(("b", 1), ((await cache.GetAsync(Ct))!.Name, (await cache.GetAsync(Ct))!.Loaded));
        Assert.Equal(2, store.Reads);

        await store.DeleteAsync("t.doc", Ct);
        Assert.Null(await cache.GetAsync(Ct));
    }

    [Fact]
    public async Task A_version_that_does_not_parse_is_read_once_and_the_last_good_value_stays()
    {
        var store = new MemoryDocumentStore();
        var cache = new JobFinder.Core.Storage.DocumentCache<Doc>(store, "t.doc", Type, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await store.SaveAsync("t.doc", """{"Name":"good"}""", Ct);
        Assert.Equal("good", (await cache.GetAsync(Ct))!.Name);

        await store.SaveAsync("t.doc", """{"Name": 5}""", Ct);

        Assert.Equal("good", (await cache.GetAsync(Ct))!.Name);
        Assert.Equal("good", (await cache.GetAsync(Ct))!.Name);
        Assert.Equal(2, store.Reads);
    }
}

public sealed class RejectionAnalysisTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static ResponseFacts Fact(ResponseOutcome outcome, int format = 80, string[]? missing = null, double? days = null, bool letter = true,
        EnglishLevel english = EnglishLevel.Unknown, int salary = 0) => new(
        1, 1, "Senior .NET", "Acme", outcome, 70,
        new VacancyEvaluation { StackMatch = 90, SeniorityMatch = 80, DomainMatch = 60, FormatMatch = format, Keywords = ["C#"] },
        salary, english, ["C#"], [.. missing ?? []], days, letter, null);

    [Theory]
    [InlineData(ApplicationStage.Rejected, 1, ResponseOutcome.Rejected)]
    [InlineData(ApplicationStage.Replied, 1, ResponseOutcome.Positive)]
    [InlineData(ApplicationStage.HrScreen, 1, ResponseOutcome.Positive)]
    [InlineData(ApplicationStage.Viewed, 3, ResponseOutcome.Waiting)]
    [InlineData(ApplicationStage.Viewed, 20, ResponseOutcome.Silent)]
    [InlineData(ApplicationStage.NoResponse, 2, ResponseOutcome.Silent)]
    public void Outcome_of_a_response(ApplicationStage stage, int daysAgo, ResponseOutcome expected) =>
        Assert.Equal(expected, RejectionAnalysis.OutcomeOf(stage, Now.AddDays(-daysAgo), Now));

    [Fact]
    public void Keywords_match_across_spelling()
    {
        var (covered, missing) = RejectionAnalysis.Coverage("Опыт: ASP.NET Core, PostgreSQL, Kafka", ["asp.net core", "PostgreSQL", "Kubernetes", "Postgresql"]);
        Assert.Equal(["asp.net core", "PostgreSQL"], covered);
        Assert.Equal(["Kubernetes"], missing);
    }

    [Fact]
    public void Groups_compare_the_same_factors()
    {
        List<ResponseFacts> facts =
        [
            Fact(ResponseOutcome.Rejected, format: 20, missing: ["Kubernetes"], days: 0.5, letter: false),
            Fact(ResponseOutcome.Rejected, format: 30, missing: ["Kubernetes", "gRPC"], days: 4, english: EnglishLevel.B2),
            Fact(ResponseOutcome.Positive, format: 90, days: 2),
        ];
        var rejected = RejectionAnalysis.Group(ResponseOutcome.Rejected, facts);
        Assert.Equal(2, rejected.Count);
        Assert.Equal(25, rejected.Format);
        Assert.Equal(2, rejected.FormatWeak);
        Assert.Equal(1, rejected.EnglishB2Plus);
        Assert.Equal(1, rejected.WithLetter);
        Assert.Equal(("Kubernetes", 2), RejectionAnalysis.MissingKeywords(facts, ResponseOutcome.Rejected)[0]);
        Assert.Equal(1, RejectionAnalysis.RejectionSpeed(facts).Single(b => b.Bucket == "до суток").Count);
        Assert.Equal(0, RejectionAnalysis.Group(ResponseOutcome.Positive, facts).FormatWeak);
    }

    [Fact]
    public void Suspects_name_what_stands_out()
    {
        var suspects = Fact(ResponseOutcome.Rejected, format: 20, missing: ["Kubernetes", "gRPC", "Go"], letter: false, english: EnglishLevel.C1,
            salary: -10).Suspects();
        Assert.Contains("английский C1", suspects);
        Assert.Contains("формат/локация 20", suspects);
        Assert.Contains("зарплата ниже ожиданий", suspects);
        Assert.Contains("без письма", suspects);
        Assert.Contains(suspects, s => s.StartsWith("ATS: нет в резюме Kubernetes", StringComparison.Ordinal));
        Assert.Empty(Fact(ResponseOutcome.Rejected).Suspects());
    }

    [Fact]
    public void Unknown_letter_is_not_a_missing_letter()
    {
        var unknown = Fact(ResponseOutcome.Rejected) with { LetterSent = null };
        Assert.DoesNotContain("без письма", unknown.Suspects());
        var group = RejectionAnalysis.Group(ResponseOutcome.Rejected, [unknown, Fact(ResponseOutcome.Rejected, letter: false)]);
        Assert.Equal((0, 1), (group.WithLetter, group.LetterKnown));
    }

    [Theory]
    [InlineData("discard", true, ApplicationStage.Rejected)]
    [InlineData("response", false, ApplicationStage.Sent)]
    [InlineData("response", true, ApplicationStage.Viewed)]
    [InlineData("interview", true, ApplicationStage.HrScreen)]
    public void Hh_states_map_to_stages(string state, bool viewed, ApplicationStage expected) =>
        Assert.Equal(expected, JobFinder.Pipeline.Career.HhHistory.StageOf(state, viewed));
}
