using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;

namespace JobFinder.Tests.Core;

public sealed class HardFilterTests
{
    private static readonly HardFilter Filter = new(new FilterOptions
    {
        TitleStopWords = ["junior", "стажер", "frontend", "qa", "1с", "java"],
        RequiredMarkers = [".net", "c#", "dotnet", "asp.net"],
        OfficeCities = ["Минск"],
        RemoteCountries = ["BY", "RU"],
        MaxEnglish = EnglishLevel.B1,
        CompanyBlacklist = ["ООО «Рога и копыта»"],
    });

    private static Vacancy V(string title, WorkFormat formats = WorkFormat.Remote, string? city = "Москва", string? country = "RU",
        EnglishLevel english = EnglishLevel.Unknown, string description = "", string? company = "Acme", List<string>? skills = null) => new()
    {
        Title = title, WorkFormats = formats, City = city, CountryCode = country, EnglishRequired = english, Description = description,
        CompanyName = company, Skills = skills ?? [], Url = "u", Fingerprint = "f",
    };

    [Fact]
    public void Senior_dotnet_remote_in_russia_passes() => Assert.True(Filter.Check(V("Senior .NET Developer"), false).Passed);

    [Fact]
    public void Stop_word_is_matched_as_a_whole_word_only()
    {
        Assert.False(Filter.Check(V("Junior C# разработчик"), false).Passed);
        // "javascript" is not "java"; the .NET marker is in the skills.
        Assert.True(Filter.Check(V("Fullstack (JavaScript)", skills: ["C#", "React"]), false).Passed);
    }

    [Fact]
    public void Dotnet_marker_may_come_from_the_description()
    {
        Assert.True(Filter.Check(V("Backend developer", description: "Стек: C#, ASP.NET Core, PostgreSQL"), false).Passed);
        Assert.False(Filter.Check(V("Backend developer", description: "Стек: Go, Kafka"), false).Passed);
    }

    [Fact]
    public void A_dot_net_domain_name_is_not_a_dotnet_marker() =>
        Assert.False(Filter.Check(V("Backend developer", description: "Подробнее на company.net"), false).Passed);

    [Fact]
    public void Office_only_outside_minsk_is_rejected_but_minsk_hybrid_passes()
    {
        var moscow = Filter.Check(V("Senior .NET", WorkFormat.Office, "Москва"), false);
        Assert.False(moscow.Passed);
        Assert.Contains("Москва", moscow.Reason);
        Assert.True(Filter.Check(V("Senior .NET", WorkFormat.Hybrid, "Минск", "BY"), false).Passed);
    }

    [Fact]
    public void Remote_for_a_company_outside_by_ru_is_rejected_unknown_country_passes()
    {
        Assert.False(Filter.Check(V("Senior .NET", WorkFormat.Remote, "Warsaw", "PL"), false).Passed);
        Assert.True(Filter.Check(V("Senior .NET", WorkFormat.Remote, null, null), false).Passed);
    }

    [Fact]
    public void Unknown_format_passes_to_the_llm() => Assert.True(Filter.Check(V("Senior .NET", WorkFormat.None, "Казань"), false).Passed);

    [Theory]
    [InlineData(EnglishLevel.B2, false)]
    [InlineData(EnglishLevel.C1, false)]
    [InlineData(EnglishLevel.B1, true)]
    [InlineData(EnglishLevel.NotRequired, true)]
    [InlineData(EnglishLevel.Unknown, true)]
    public void English_above_b1_is_rejected(EnglishLevel level, bool passes) =>
        Assert.Equal(passes, Filter.Check(V("Senior .NET", english: level), false).Passed);

    [Fact]
    public void Blacklist_matches_normalized_company_names() =>
        Assert.False(Filter.Check(V("Senior .NET", company: "Рога и Копыта"), false).Passed);

    [Fact]
    public void Already_applied_is_rejected() => Assert.False(Filter.Check(V("Senior .NET"), alreadyApplied: true).Passed);
}

public sealed class SalaryEvaluatorTests
{
    private static readonly SalaryEvaluator Evaluator = new(new SalaryOptions
    {
        RatesToUsd = new(StringComparer.OrdinalIgnoreCase) { ["RUB"] = 0.0125m, ["BYN"] = 0.3m },
        NetShareOfGross = 0.87m,
        MaxPenalty = 25,
        MaxBonus = 5,
    });

    private static readonly DesiredSalary Want = new() { Amount = 4000, Currency = "USD", Net = true };

    private static Vacancy V(decimal? from, decimal? to, string currency, bool? gross = false) => new()
    {
        Title = "t", Url = "u", Fingerprint = "f", SalaryFrom = from, SalaryTo = to, Currency = currency, SalaryGross = gross,
    };

    [Fact]
    public void Unknown_salary_changes_nothing() => Assert.Equal(0, Evaluator.Evaluate(V(null, null, "RUB"), Want).Adjustment);

    [Fact]
    public void Offer_at_expectation_is_neutral() => Assert.Equal(0, Evaluator.Evaluate(V(null, 320000, "RUB"), Want).Adjustment);

    [Fact]
    public void Offer_below_expectation_is_penalized_proportionally()
    {
        // 240k RUB = 3000 USD = 75 % → −15
        Assert.Equal(-15, Evaluator.Evaluate(V(200000, 240000, "RUB"), Want).Adjustment);
    }

    [Fact]
    public void Gross_is_converted_to_net_before_comparing()
    {
        // 10000 BYN gross = 3000 USD gross = 2610 net = 65 % → −21
        Assert.Equal(-21, Evaluator.Evaluate(V(null, 10000, "BYN", gross: true), Want).Adjustment);
    }

    [Fact]
    public void Penalty_and_bonus_are_capped()
    {
        Assert.Equal(-25, Evaluator.Evaluate(V(null, 50000, "RUB"), Want).Adjustment);
        Assert.Equal(5, Evaluator.Evaluate(V(null, 1_000_000, "RUB"), Want).Adjustment);
    }

    [Fact]
    public void Currency_without_a_rate_is_ignored() => Assert.Equal(0, Evaluator.Evaluate(V(null, 5000, "GBP"), Want).Adjustment);
}

public sealed class DecisionPolicyTests
{
    private static readonly DecisionSettings Settings = new(80, 60, 25, false);

    [Theory]
    [InlineData(85, true, 0, Decision.AutoApply)]
    [InlineData(85, false, 0, Decision.Approval)] // no hh route (Telegram post, employer test) → the user decides
    [InlineData(85, true, 25, Decision.Approval)] // daily limit reached
    [InlineData(70, true, 0, Decision.Approval)]
    [InlineData(59, true, 0, Decision.Ignore)]
    public void Decides_by_score_route_and_limit(int score, bool canAuto, int today, Decision expected) =>
        Assert.Equal(expected, DecisionPolicy.Decide(new DecisionInput(score, canAuto, today), Settings));

    [Fact]
    public void Pause_turns_auto_into_approval() =>
        Assert.Equal(Decision.Approval, DecisionPolicy.Decide(new DecisionInput(95, true, 0), Settings with { AutoApplyPaused = true }));
}

public sealed class PostPrefilterTests
{
    private static readonly PostPrefilter Prefilter = new(new PrefilterOptions
    {
        TechMarkers = [".net", "c#", "dotnet"],
        VacancyMarkers = ["ваканс", "ищем", "зп", "#vacancy"],
        RejectMarkers = ["#резюме", "ищу работу"],
        MinLength = 40,
    });

    [Fact]
    public void Dotnet_vacancy_passes_with_inflected_markers() =>
        Assert.True(Prefilter.LooksLikeDotnetVacancy("Открыта вакансия: Senior C# разработчик в финтех, удалёнка, зп 4-5k$", out _));

    [Fact]
    public void Other_stack_is_dropped()
    {
        Assert.False(Prefilter.LooksLikeDotnetVacancy("Ищем Senior Java разработчика в финтех, удалёнка, зп 4-5k$", out var reason));
        Assert.Equal("нет .NET/C#", reason);
    }

    [Fact]
    public void A_resume_is_dropped() =>
        Assert.False(Prefilter.LooksLikeDotnetVacancy("#резюме Senior .NET разработчик, ищу работу, 7 лет опыта, C#, ASP.NET", out _));

    [Fact]
    public void Short_posts_are_dropped() => Assert.False(Prefilter.LooksLikeDotnetVacancy("C# вакансия", out _));
}

public sealed class TextTests
{
    [Fact]
    public void Same_opening_from_two_sources_has_one_fingerprint()
    {
        var hh = VacancyFingerprint.Compute("ООО «Рога и Копыта»", "Senior .NET разработчик (удалённо)", "a");
        var tg = VacancyFingerprint.Compute("Рога и копыта", "Senior .NET Developer", "b");
        Assert.Equal(hh, tg);
    }

    [Fact]
    public void Different_companies_do_not_merge() =>
        Assert.NotEqual(VacancyFingerprint.Compute("Acme", "Senior .NET Developer", "a"), VacancyFingerprint.Compute("Globex", "Senior .NET Developer", "b"));

    [Fact]
    public void No_company_means_no_merge() =>
        Assert.NotEqual(VacancyFingerprint.Compute(null, "Senior .NET Developer", "a"), VacancyFingerprint.Compute(null, "Senior .NET Developer", "b"));

    [Theory]
    [InlineData("b2", EnglishLevel.B2)]
    [InlineData("Upper-Intermediate", EnglishLevel.B2)]
    [InlineData("l1", EnglishLevel.Native)]
    [InlineData("none", EnglishLevel.NotRequired)]
    [InlineData("какой-то", EnglishLevel.Unknown)]
    public void English_levels(string raw, EnglishLevel expected) => Assert.Equal(expected, EnglishLevels.Parse(raw));

    [Theory]
    [InlineData("RUR", "RUB")]
    [InlineData("BYR", "BYN")]
    [InlineData("$", "USD")]
    [InlineData("руб", "RUB")]
    public void Currencies(string raw, string expected) => Assert.Equal(expected, Currency.Normalize(raw));

    [Fact]
    public void Html_becomes_readable_text()
    {
        var text = TextUtil.HtmlToText("<p><strong>Мы</strong> ищем&nbsp;разработчика</p><ul><li>C#</li><li>SQL</li></ul>");
        Assert.Equal("Мы ищем разработчика\n\n• C#\n• SQL", text);
    }
}
