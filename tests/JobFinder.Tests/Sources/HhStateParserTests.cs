using JobFinder.Core.Domain;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Threads;

namespace JobFinder.Tests.Sources;

public sealed class HhStateParserTests
{
    [Fact]
    public void Search_page_rows_carry_ids_formats_country_and_flags()
    {
        var (items, total) = HhStateParser.ParseSearch(HhStateParser.ExtractState(Fixture.Read("hh_search.html")), "rabota.by");

        Assert.Equal(4, items.Count);
        Assert.True(total >= 4);
        var pm = items.Single(i => i.Id == 137436500);
        Assert.Equal("IDT Technologies", pm.CompanyName);
        Assert.Equal("BY", pm.CountryCode);
        Assert.Equal(WorkFormat.Office | WorkFormat.Hybrid | WorkFormat.Remote, pm.Formats);
        Assert.True(pm.HasTest);
        Assert.Equal("https://rabota.by/vacancy/137436500", pm.Url);
        Assert.Equal(WorkFormat.Hybrid, items.Single(i => i.Id == 137470689).Formats);
    }

    [Fact]
    public void Vacancy_page_gives_english_requirement_and_no_salary()
    {
        var v = HhStateParser.ParseVacancy(HhStateParser.ExtractState(Fixture.Read("hh_vacancy_english_c1.html")), "rabota.by");

        Assert.Equal(137436500, v.Id);
        Assert.Equal(EnglishLevel.C1, v.English);
        Assert.Null(v.SalaryFrom);
        Assert.Null(v.SalaryTo);
        Assert.Equal("BY", v.CountryCode);
        Assert.Equal("Минск", v.AreaName);
        Assert.False(v.Closed);
        Assert.Contains("Английский язык", v.KeySkills);
        Assert.DoesNotContain("<p>", v.Description);
        Assert.True(v.Description.Length > 200);
    }

    [Fact]
    public void Vacancy_page_salary_in_byr_is_normalized_to_byn_gross()
    {
        var v = HhStateParser.ParseVacancy(HhStateParser.ExtractState(Fixture.Read("hh_vacancy_byn.html")), "rabota.by");

        Assert.Equal(4000m, v.SalaryFrom);
        Assert.Equal(6000m, v.SalaryTo);
        Assert.Equal("BYN", v.Currency);
        Assert.True(v.SalaryGross);
        Assert.Equal(WorkFormat.Office | WorkFormat.Hybrid, v.Formats);
        Assert.Equal("between1And3", v.Experience);
        Assert.Contains("C#", v.KeySkills);
        Assert.Equal(EnglishLevel.Unknown, v.English);
    }

    [Fact]
    public void Employer_page_gives_industries_rating_and_open_vacancies()
    {
        var e = HhStateParser.ParseEmployer(HhStateParser.ExtractState(Fixture.Read("hh_employer.html")));

        Assert.Equal(245592, e.Id);
        Assert.Equal("ROSSKO", e.Name);
        Assert.Contains("Автомобильный бизнес", e.Industries);
        Assert.Equal(4.2m, e.Rating);
        Assert.Equal(286, e.OpenVacancies);
        Assert.False(string.IsNullOrWhiteSpace(e.Description));
    }

    [Fact]
    public void A_page_without_state_is_a_format_error_not_an_empty_result()
    {
        Assert.Throws<FormatException>(() => HhStateParser.ExtractState("<html><body>captcha</body></html>"));
    }

    [Theory]
    [InlineData("смотри https://hh.ru/vacancy/123456789?from=share", "hh.ru", 123456789)]
    [InlineData("https://minsk.rabota.by/vacancy/98765432", "rabota.by", 98765432)]
    [InlineData("spb.hh.ru/vacancy/11223344", "hh.ru", 11223344)]
    public void Vacancy_links_in_posts_are_recognized(string text, string host, long id)
    {
        Assert.True(HhClient.TryParseVacancyUrl(text, out var h, out var i));
        Assert.Equal(host, h);
        Assert.Equal(id, i);
    }

    [Fact]
    public void Links_to_other_pages_are_not_vacancies()
    {
        Assert.False(HhClient.TryParseVacancyUrl("https://hh.ru/employer/245592", out _, out _));
        Assert.False(HhClient.TryParseVacancyUrl(null, out _, out _));
    }

    [Fact]
    public void A_shared_message_can_carry_several_vacancies() =>
        Assert.Equal([137846728L, 137848666L, 137578862L], HhClient.ParseVacancyIds(
            "https://hh.ru/vacancy/137846728?from=share_android https://hh.ru/vacancy/137848666?from=share_android\n" +
            "Senior .NET — https://rabota.by/vacancy/137578862 и ещё раз hh.ru/vacancy/137846728, компания hh.ru/employer/245592"));

    [Fact]
    public void Search_url_carries_every_filter()
    {
        var url = HhClient.SearchUrl(new HhSearch
        {
            Host = "hh.ru", Text = ".NET OR C#", SearchField = "name", Areas = [113], WorkFormats = ["REMOTE"], PeriodDays = 3,
        }, page: 2).AbsoluteUri;

        Assert.StartsWith("https://hh.ru/search/vacancy?", url);
        Assert.Contains("text=.NET%20OR%20C%23", url);
        Assert.Contains("search_field=name", url);
        Assert.Contains("area=113", url);
        Assert.Contains("work_format=REMOTE", url);
        Assert.Contains("order_by=publication_time", url);
        Assert.Contains("page=2", url);
    }
}

public sealed class HabrParserTests
{
    [Fact]
    public void Search_json_rows_are_parsed()
    {
        var (items, pages) = HabrClient.ParseSearch(Fixture.Read("habr_search.json"));

        Assert.Equal(3, items.Count);
        Assert.True(pages >= 1);
        var first = items[0];
        Assert.Equal(1000168633, first.Id);
        Assert.True(first.Remote);
        Assert.Equal(75000m, first.SalaryFrom);
        Assert.Equal("RUB", first.Currency);
        Assert.Equal("https://career.habr.com/vacancies/1000168633", first.Url);
    }

    [Fact]
    public void Description_comes_from_json_ld()
    {
        var text = HabrClient.ParseDescription(Fixture.Read("habr_vacancy.html"));

        Assert.Contains("Bell Integrator", text);
        Assert.DoesNotContain("<p>", text);
    }
}

public sealed class ThreadsParsingTests
{
    [Fact]
    public void Card_chrome_is_removed_from_post_text()
    {
        var text = ThreadsBrowserClient.Clean("anna.hr\n2 ч\nИщем Senior .NET в финтех, удалёнка по РФ\n12\n3\nПеревести", "anna.hr");

        Assert.Equal("Ищем Senior .NET в финтех, удалёнка по РФ", text);
    }

    [Fact]
    public void Search_extraction_json_becomes_posts_and_drops_empty_cards()
    {
        const string json = """
            [{"username":"cto_ivan","code":"DAbc12","url":"https://www.threads.com/@cto_ivan/post/DAbc12","time":"2026-09-25T10:00:00.000Z","text":"cto_ivan\n5 ч\nНанимаем бэкендеров на C#, пишите в личку\n4"},
             {"username":"x","code":"","url":"","time":null,"text":"x"}]
            """;

        var posts = ThreadsBrowserClient.Parse(json).ToList();

        var post = Assert.Single(posts);
        Assert.Equal("DAbc12", post.Id);
        Assert.Equal("Нанимаем бэкендеров на C#, пишите в личку", post.Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero), post.PublishedAt);
    }
}
