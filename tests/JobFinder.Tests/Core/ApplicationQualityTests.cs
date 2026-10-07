using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Core.Text;

namespace JobFinder.Tests.Core;

/// <summary>
/// What a recruiter gets: the message (which vacancy, laid out for a phone, no worn phrases) and the tailored resume (no talk about gaps,
/// the source's roles and dates, the vacancy's words where they are true). The cases are the real ones of 2026-09-29.
/// </summary>
public sealed class ApplicationQualityTests
{
    /// <summary>What @CodeVal got on 2026-09-29: which vacancy — unknown, one block of text, the closing every letter had.</summary>
    private const string SentToCodeVal =
        "Здравствуйте! Зацепило, что на проекте нужно развивать высоконагруженный backend и интеграции с другими системами. В игровом проекте я " +
        "вынес транзакционные события в асинхронную обработку, чтобы сократить задержки игровых операций. Более пяти лет разрабатываю backend на " +
        "C#/.NET. Нахожусь в Минске, удалённый формат подходит. Буду рад обсудить задачи и рассказать подробнее о проектах, резюме прикрепил.";

    private static Vacancy KaCareer() => new()
    {
        Title = "Senior C#/.NET Developer", CompanyName = "KA Career", Url = "https://t.me/DotNetRuJobsFeed/5885", Fingerprint = "f",
    };

    [Fact]
    public void The_message_that_went_to_CodeVal_would_not_pass_now()
    {
        var problems = RecruiterStyle.Problems(SentToCodeVal, first: true);
        problems.AddRange(RecruiterStyle.LayoutProblems("Здравствуйте!", [SentToCodeVal["Здравствуйте! ".Length..]], "", first: true));

        Assert.Contains(problems, p => p.Contains("резюме прикрепил"));
        Assert.Contains(problems, p => p.Contains("сплошной абзац"));
        Assert.Contains(problems, p => p.Contains("нет завершения"));
        Assert.Contains(RecruiterStyle.LayoutProblems("Здравствуйте!", ["Текст."], "Буду рад обсудить задачи и рассказать подробнее о проектах.", true),
            p => p.Contains("заезженное завершение"));
    }

    [Fact]
    public void The_first_line_says_which_vacancy_with_the_link_and_the_body_comes_in_paragraphs()
    {
        var text = LetterLayout.Compose("Здравствуйте, Валентина!", LetterLayout.VacancyReference(KaCareer()),
            ["Главное в проекте: надёжность денежных операций под нагрузкой.\n Этим я и занимался.", "Работаю удалённо из Минска."],
            "Если профиль подходит, удобно созвониться на этой неделе.");

        Assert.Equal(
            "Здравствуйте, Валентина! Пишу по вакансии «Senior C#/.NET Developer» (KA Career): https://t.me/DotNetRuJobsFeed/5885\n\n" +
            "Главное в проекте: надёжность денежных операций под нагрузкой. Этим я и занимался.\n\n" +
            "Работаю удалённо из Минска.\n\n" +
            "Если профиль подходит, удобно созвониться на этой неделе.", text);
    }

    [Theory]
    [InlineData("Не указана", null, "Пишу по вакансии «.NET developer».")]
    [InlineData("Ozon Tech", "https://hh.ru/vacancy/137094817", "Пишу по вакансии «.NET developer» (Ozon Tech): https://hh.ru/vacancy/137094817")]
    public void The_vacancy_line_leaves_out_what_is_unknown(string company, string? url, string expected) =>
        Assert.Equal(expected, LetterLayout.VacancyReference(new Vacancy { Title = ".NET developer", CompanyName = company, Url = url ?? "", Fingerprint = "f" }));

    [Fact]
    public void A_well_laid_out_first_message_passes_the_layout_check()
    {
        Assert.Empty(RecruiterStyle.LayoutProblems("Здравствуйте, Валентина!",
            ["Главное в проекте: корректность денег под нагрузкой. В игровом backend на 5M+ пользователей я убрал двойные списания при 5 000+ RPS.",
             "Работаю удалённо из Минска, есть разрешение на работу в России."],
            "Если профиль подходит, удобно созвониться в любой день на этой неделе.", first: true));
    }

    [Fact]
    public void LinkedIn_reference_bounds_display_names_but_never_truncates_the_vacancy_url()
    {
        var vacancy = new Vacancy
        {
            Title = new string('т', 200), CompanyName = new string('к', 150),
            Url = "https://example.invalid/vacancy/123?source=telegram", Fingerprint = "synthetic",
        };
        var reference = LetterLayout.LinkedInReference(vacancy);
        Assert.Contains(vacancy.Url, reference);
        Assert.True(reference.Length < 150);
        vacancy.Url = "https://example.invalid/" + new string('x', 400);
        Assert.EndsWith(vacancy.Url, LetterLayout.LinkedInReference(vacancy));
    }

    [Fact]
    public void A_condition_about_oneself_reads_as_a_problem_and_is_caught()
    {
        // The facts sheet says "remote_from: Да, при подходящем варианте оформления": the letter says what fits instead.
        Assert.Contains(RecruiterStyle.Problems("Нахожусь в Минске, удалённо работать могу при подходящем варианте оформления.", first: false),
            p => p.Contains("условие вместо утверждения"));
        Assert.DoesNotContain(RecruiterStyle.Problems("Работаю удалённо из Минска, подойдут ТД, ГПХ или ИП.", first: false),
            p => p.Contains("условие"));
    }

    [Theory]
    [InlineData("MS SQL Server (T-SQL)", true)]
    [InlineData("Redis (distributed locks)", true)]
    [InlineData("MS SQL Server (Oracle)", false)]
    [InlineData("Transactional Outbox/Inbox", true)]
    [InlineData("Unit/Integration-тесты", true)] // seen on #96: xUnit and Testcontainers are exactly that
    [InlineData("Memory Management (управление памятью .NET)", true)] // Span<T>, ArrayPool<T>, fewer GC allocations
    [InlineData("Unit/Performance-тесты", false)]
    [InlineData("Redis/Memcached", false)]
    public void A_composite_skill_is_proven_by_its_parts(string item, bool proven) =>
        Assert.Equal(proven, TechThesaurus.Proven(item, new TechThesaurus.Text("MS SQL Server, Redis, distributed locks, ASP.NET Core, Transactional Outbox и Inbox, xUnit, Testcontainers, Span<T>, ArrayPool<T>, .NET 8")));

    [Theory]
    [InlineData(null, 10)]
    [InlineData("brevity", 9)] // minor
    [InlineData("clarity", 8)] // major: "в Минске… из Минска" is not a 9
    [InlineData("main_need", 7)] // critical
    public void A_messages_score_is_its_checklist(string? failed, int score)
    {
        var verdict = new JobFinder.Llm.Tasks.RecruiterVerdict
        {
            Score = 3, // the model's own number does not count
            Checks = [.. JobFinder.Llm.Tasks.RecruiterVerdict.Criteria.Keys.Select(id => new JobFinder.Llm.Tasks.VerdictCheck { Id = id, Passed = id != failed })],
        };

        Assert.Equal(score, verdict.Scored().Score);
    }

    [Fact]
    public void The_body_does_not_name_the_vacancy_again()
    {
        var problems = RecruiterStyle.LayoutProblems("Здравствуйте!", ["Пишу по вашей вакансии: у меня пять лет в .NET."], "Созвонимся?", true);

        Assert.Contains(problems, p => p.Contains("вакансию уже называет"));
    }

    // ---- the tailored resume ----

    private const string Source = """
        Senior C# / .NET Backend Developer. ASP.NET Core, MS SQL Server, PostgreSQL, Redis, Apache Kafka, Microservices, Highload, GitHub: github.com/lilter96.
        .NET Developer · Custom Games Studio (Минск) Ноя 2023 — Июл 2026. Устранил риск двойных списаний при нагрузке 5 000+ RPS.
        C#/.NET-разработчик · Solvintech Июл 2022 — Окт 2023. Гарантировал доставку событий в Kafka через Transactional Outbox и Inbox, 99.99%.
        C#/.NET-разработчик · Elgrow Янв 2021 — Июн 2022. Внедрил строгую идемпотентность платежных транзакций.
        """;

    private static readonly List<ProfileJob> Jobs =
    [
        new() { Company = "Custom Games Studio", Role = ".NET Developer", Period = "Ноя 2023 — Июл 2026 · 2 года 9 мес" },
        new() { Company = "Solvintech", Role = "C#/.NET-разработчик", Period = "Июл 2022 — Окт 2023 · 1 год 4 мес" },
        new() { Company = "Elgrow", Role = "C#/.NET-разработчик", Period = "Янв 2021 — Июн 2022 · 1 год 6 мес", Highlights = "Внедрил строгую идемпотентность платежных транзакций." },
    ];

    private static ResumeDocument Tailored() => new()
    {
        Language = "ru",
        Headline = "Senior C#/.NET Developer",
        // The sentence that went to @CodeVal.
        Summary = "Senior C#/.NET-разработчик: высоконагруженные системы и микросервисы. Устранил риск двойных списаний при нагрузке 5 000+ RPS. " +
                  "Банковские дистанционные сервисы и межведомственное электронное взаимодействие в предоставленном опыте не указаны.",
        Skills = [new SkillGroup { Group = "Базы данных", Items = ["MS SQL Server", "PostgreSQL"] }],
        Familiar = ["T-SQL", "Web API"],
        Experience =
        [
            new ResumeJob { Company = "Solvintech", Role = "C#/.NET-разработчик", Period = "Июл 2022 — Окт 2023", Bullets = ["Гарантировал доставку событий в Kafka через Transactional Outbox."] },
            new ResumeJob { Company = "Custom Games Studio", Role = "Senior .NET Developer", Period = "Ноя 2023 — Авг 2026", Bullets = ["Устранил риск двойных списаний при нагрузке 5 000+ RPS."] },
        ],
    };

    private static readonly string[] Keywords =
        ["PostgreSQL", "MS SQL Server", "T-SQL", "Web API", "микросервисов", "высоконагруженных систем", "IoC-контейнеры", "Git", "межведомственного электронного взаимодействия"];

    [Fact]
    public void Talk_about_gaps_never_reaches_the_resume()
    {
        var r = ResumeFaithfulness.Check(Tailored(), Source, Keywords, fix: true, Jobs);

        Assert.DoesNotContain("не указаны", r.Document.Summary);
        Assert.Contains("двойных списаний", r.Document.Summary);
        Assert.Contains(r.Violations, v => v.Contains("о пробелах"));
        Assert.True(ResumeFaithfulness.IsGapTalk("Опыта с Oracle нет, но готов изучить"));
        Assert.True(ResumeFaithfulness.IsGapTalk("Banking experience is not mentioned in the provided resume"));
        Assert.False(ResumeFaithfulness.IsGapTalk("Устранил риск двойных списаний при нагрузке 5 000+ RPS"));
    }

    [Fact]
    public void What_the_candidate_has_is_a_skill_not_also_familiar()
    {
        var r = ResumeFaithfulness.Check(Tailored(), Source, Keywords, fix: true, Jobs);

        Assert.Empty(r.Document.Familiar);
        var databases = r.Document.Skills.Single(g => g.Group == "Базы данных").Items;
        Assert.Contains("T-SQL", databases);
        Assert.Contains("Web API", r.Document.Skills.SelectMany(g => g.Items));
    }

    [Fact]
    public void Roles_and_dates_are_the_sources_and_no_job_is_dropped_or_out_of_order()
    {
        var r = ResumeFaithfulness.Check(Tailored(), Source, Keywords, fix: true, Jobs);

        Assert.Equal(["Custom Games Studio", "Solvintech", "Elgrow"], r.Document.Experience.Select(j => j.Company));
        var latest = r.Document.Experience[0];
        Assert.Equal((".NET Developer", "Ноябрь 2023 – Июль 2026"), (latest.Role, latest.Period));
        Assert.Equal("Июль 2022 – Октябрь 2023", r.Document.Experience[1].Period);
        Assert.Equal(["Внедрил строгую идемпотентность платежных транзакций."], r.Document.Experience[2].Bullets);
        Assert.Contains(r.Violations, v => v.Contains("пропущено место работы «Elgrow»"));
    }

    [Fact]
    public void Coverage_reads_word_forms_and_tells_the_unshown_from_the_gaps()
    {
        var r = ResumeFaithfulness.Check(Tailored(), Source, Keywords, fix: true, Jobs);

        // «микросервисов» (vacancy) is «микросервисы» (resume); T-SQL and Web API are in the skills now.
        Assert.Contains("микросервисов", r.Covered);
        Assert.Contains("высоконагруженных систем", r.Covered);
        Assert.Contains("T-SQL", r.Covered);
        // ASP.NET Core means DI, GitHub means Git: true, but not written yet.
        Assert.Contains("IoC-контейнеры", r.Unshown);
        Assert.Contains("Git", r.Unshown);
        Assert.Equal(["межведомственного электронного взаимодействия"], r.Missing);
    }

    [Fact]
    public void Additional_does_not_repeat_the_header_or_state_conditions_and_the_summary_needs_evidence()
    {
        var doc = Tailored() with
        {
            Summary = "Senior C#/.NET-разработчик с 5+ годами опыта. Проектирую высоконагруженные системы и микросервисы.",
            Extras =
            [
                "Гацуков Терентий Алексеевич · +375 33 385-44-32 · lilter96dotnet@gmail.com", "Минск", "Гражданство: Беларусь",
                "Формат работы: удалённо по Беларуси и России при подходящем варианте оформления", "Удалённо из Минска по Беларуси и России",
                "Зарплатные ожидания: от 3000 USD в месяц на руки, обсуждаемо",
            ],
        };

        var r = ResumeFaithfulness.Check(doc, Source, Keywords, fix: true, Jobs,
            ["Гацуков Терентий Алексеевич", "Минск", "+375 33 385-44-32", "lilter96dotnet@gmail.com", "@lilter96"]);

        Assert.Equal(["Гражданство: Беларусь", "Формат работы: удалённо по Беларуси и России", "Удалённо из Минска по Беларуси и России"], r.Document.Extras);
        Assert.Contains(r.Violations, v => v.Contains("summary без доказательств"));
        Assert.Contains(r.Violations, v => v.Contains("зарплатные ожидания"));
        Assert.DoesNotContain(ResumeFaithfulness.Check(Tailored(), Source, Keywords, fix: true, Jobs).Violations, v => v.Contains("без доказательств"));
    }

    [Fact]
    public void A_result_the_summary_names_must_also_be_in_the_experience()
    {
        // Seen live on #371: a revision kept «5 000+ RPS» in the summary and dropped its point from the job.
        var doc = Tailored() with
        {
            Experience = [.. Tailored().Experience.Select(j => j with { Bullets = j.Bullets.Where(b => !b.Contains("5 000", StringComparison.Ordinal)).ToList() })],
        };

        var r = ResumeFaithfulness.Check(doc, Source, Keywords, fix: true, Jobs);

        Assert.Contains(r.Violations, v => v.Contains("summary называет результаты, которых нет в опыте (5000)"));
        Assert.DoesNotContain(ResumeFaithfulness.Check(Tailored(), Source, Keywords, fix: true, Jobs).Violations, v => v.Contains("которых нет в опыте"));
    }

    [Fact]
    public void The_writer_and_the_reviewer_are_told_what_the_experience_implies()
    {
        var implied = TechThesaurus.ImpliedBy(new TechThesaurus.Text(Source));

        Assert.Contains("ASP.NET Core → Web API, Dependency Injection (IoC-контейнеры), middleware", implied);
        Assert.Contains("MS SQL Server → T-SQL, SQL", implied);
        Assert.Contains("GitHub → Git", implied);
    }

    [Fact]
    public void Git_hosting_platforms_are_not_the_same_skill()
    {
        var source = new TechThesaurus.Text("GitHub, Git");
        Assert.True(TechThesaurus.Proven("Git", source));
        Assert.False(TechThesaurus.Proven("GitLab", source));
        Assert.False(TechThesaurus.Visible("GitLab", source));
    }

    [Theory]
    [InlineData("DI", "Redis, RabbitMQ", false)]
    [InlineData("Git", "digital platform", false)]
    [InlineData("MS SQL", "MSSQL", true)]
    [InlineData("high-load", "highload systems", true)]
    [InlineData("C#", "C#/.NET", true)]
    [InlineData("высоконагруженных систем", "Высоконагруженные системы", true)]
    public void A_term_is_found_as_a_whole_word_in_any_form(string term, string text, bool found) =>
        Assert.Equal(found, TechThesaurus.Visible(term, new TechThesaurus.Text(text)));

    [Theory]
    [InlineData("Ноя 2023 — Июл 2026 · 2 года 9 мес", "Ноябрь 2023 – Июль 2026", "Nov 2023 – Jul 2026")]
    [InlineData("11.2023 - 07.2026", "Ноябрь 2023 – Июль 2026", "Nov 2023 – Jul 2026")]
    [InlineData("ноябрь 2023 – н.в.", "Ноябрь 2023 – по настоящее время", "Nov 2023 – Present")]
    [InlineData("2021 — 2022", "2021 – 2022", "2021 – 2022")]
    [InlineData("since 2021", "2021", "2021")]
    public void Periods_come_in_one_format(string period, string ru, string en) =>
        Assert.Equal((ru, en), (ResumePeriod.Normalize(period, en: false), ResumePeriod.Normalize(period, en: true)));

    [Fact]
    public void Stems_match_a_requirement_in_any_case()
    {
        var resume = Stems.Of("Проектирую высоконагруженные системы и микросервисы").ToHashSet();

        Assert.True(Stems.Covers(resume, "высоконагруженных систем"));
        Assert.True(Stems.Covers(resume, "микросервисов"));
        Assert.False(Stems.Covers(resume, "банковских сервисов"));
    }
}
