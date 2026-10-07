using JobFinder.Core.Contacts;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Workers;

namespace JobFinder.Tests.Pipeline;

public sealed class ContactTests
{
    [Theory]
    [InlineData("Писать: @hr_anna", "@hr_anna")]
    [InlineData("hr@company.com\nhttps://t.me/recruiter_bob", "@recruiter_bob")]
    [InlineData("(@Maria_IT)", "@maria_it")] // usernames are case-insensitive: stored lower-case
    [InlineData("anna@company.com", null)]
    [InlineData("https://t.me/c/123/45", null)]
    [InlineData(null, null)]
    public void Telegram_contact_is_a_username_not_an_email_or_a_post_link(string? contacts, string? expected) =>
        Assert.Equal(expected, ApplyRoutes.TelegramContact(ContactExtractor.FromText(contacts)));

    [Fact]
    public void A_merged_vacancy_is_answered_through_its_posting_without_a_test()
    {
        // Seen live on #21: the first hh posting had an employer test, the second did not; the applier opened the first.
        var v = new Vacancy
        {
            Title = "C#", Url = "u", Fingerprint = "f",
            Sources =
            [
                new VacancySource { Source = SourceKind.HeadHunter, ExternalId = "137787407", Url = "a", HasEmployerTest = true },
                new VacancySource { Source = SourceKind.HeadHunter, ExternalId = "137238259", Url = "b" },
            ],
        };

        Assert.True(ApplyRoutes.HhApplicable(v));
        Assert.Equal(137238259, ApplyRoutes.HhId(v));
    }
}

public sealed class CardTests
{
    private static Vacancy Sample() => new()
    {
        Id = 42,
        Title = "Senior .NET <Backend> & Co",
        CompanyName = "Acme",
        City = "Минск",
        WorkFormats = WorkFormat.Hybrid | WorkFormat.Remote,
        SalaryFrom = 4000,
        SalaryTo = 5000,
        Currency = "USD",
        Score = 84,
        Url = "https://hh.ru/vacancy/1?a=1&b=2",
        Fingerprint = "f",
        CoverLetter = "Здравствуйте! <b>не тег</b>",
        Evaluation = new VacancyEvaluation { Summary = "Подходит", Strengths = ["C#"], Gaps = ["Kafka"] },
        Sources = [new VacancySource { Source = SourceKind.HeadHunter, ExternalId = "1", Url = "u" }],
    };

    [Fact]
    public void Approval_card_escapes_user_text_and_offers_apply_through_hh()
    {
        var (text, buttons) = Cards.VacancyApproval(Sample(), new ApplyRoute(ApplicationChannel.HeadHunter, null));

        Assert.Contains("Senior .NET &lt;Backend&gt; &amp; Co", text);
        Assert.Contains("&lt;b&gt;не тег&lt;/b&gt;", text);
        Assert.Contains("href=\"https://hh.ru/vacancy/1?a=1&amp;b=2\"", text);
        Assert.Contains("🟢 84", text);
        Assert.Equal("🚀 Откликнуться|v:apply:42", buttons[0][0]);
        Assert.All(buttons.SelectMany(r => r), b => Assert.True(b.Split('|')[1].Length <= 64));
    }

    [Theory]
    [InlineData(false, "Быстрый отклик")]
    [InlineData(true, "Письмо обязательно")]
    public void A_dry_run_card_says_held_not_failed_and_offers_manual_actions(bool letterRequired, string letterLine)
    {
        var (text, buttons) = Cards.DryRunHeld(Sample(), letterRequired, 7);

        Assert.StartsWith("🧪 <b>Dry-run</b>", text);
        Assert.DoesNotContain("не отправлен</b>", text);
        Assert.DoesNotContain("вручную", text);
        Assert.Contains(letterLine, text);
        Assert.Contains("/dryrun off", text);
        Assert.Contains("Senior .NET &lt;Backend&gt; &amp; Co", text);
        Assert.Equal(["✅ Откликнулся сам|v:done:42", "🙈 Пропустить|v:skip:42"], Assert.Single(buttons));
    }

    [Theory]
    [InlineData("no apply button", "на странице нет кнопки «Откликнуться»")]
    [InlineData("the employer asks questions or a test", "работодатель просит ответить на вопросы или пройти тест")]
    [InlineData("vacancy is closed or archived", "вакансия закрыта или в архиве")]
    [InlineData("работодатель просит ответить на вопросы или пройти тест", "работодатель просит ответить на вопросы или пройти тест")]
    [InlineData("Timeout 30000ms exceeded.", "Timeout 30000ms exceeded.")]
    public void Applier_reasons_reach_the_card_in_russian_and_unknown_ones_as_is(string message, string expected) =>
        Assert.Equal(expected, ApplyWorker.UserText(message));

    [Fact]
    public void Without_hh_the_card_offers_a_dm_an_email_or_a_manual_mark()
    {
        var (dmText, dm) = Cards.VacancyApproval(Sample(), new ApplyRoute(ApplicationChannel.TelegramDm, "@hr_anna"));
        Assert.StartsWith("✉️", dm[0][0]);
        Assert.Contains("резюме PDF", dmText);
        var (_, mail) = Cards.VacancyApproval(Sample(), new ApplyRoute(ApplicationChannel.Email, "hr@acme.io"));
        Assert.StartsWith("📧", mail[0][0]);
        var (_, manual) = Cards.VacancyApproval(Sample(), ApplyRoute.Manual);
        Assert.StartsWith("✅ Откликнулся сам", manual[0][0]);
    }

    [Fact]
    public void A_huge_card_is_cut_below_the_telegram_limit_with_quotes_closed()
    {
        var v = Sample();
        v.CoverLetter = string.Join('\n', Enumerable.Repeat("очень длинное письмо", 400));
        var (text, _) = Cards.VacancyApproval(v, new ApplyRoute(ApplicationChannel.HeadHunter, null));

        Assert.True(text.Length <= 4096);
        Assert.Equal(text.Split("<blockquote").Length, text.Split("</blockquote>").Length);
    }

    [Fact]
    public void A_letter_that_did_not_pass_is_marked_with_its_problems_each_cut_short()
    {
        // #74: the recruiter's note with its fix ran to a paragraph; the warning keeps the head of each problem.
        var reviewer = "рекрутер: 8/10 — не выполнено: у кейса понятный результат " + new string('ж', 600);
        var (text, _) = Cards.VacancyApproval(Sample(), new ApplyRoute(ApplicationChannel.HeadHunter, null),
            letterProblems: [reviewer, "утверждение без опоры в профиле: «X»"]);

        Assert.Contains("⚠️ Письмо не прошло проверку", text);
        Assert.Contains("у кейса понятный результат", text);
        Assert.Contains("утверждение без опоры в профиле", text);
        Assert.DoesNotContain(new string('ж', 300), text);
    }
}

public sealed class ApplyRouteTests
{
    private static Vacancy V(string? contacts, bool hh = false, bool test = false) => new()
    {
        Title = "t", Url = "u", Fingerprint = "f", Contacts = ContactExtractor.FromText(contacts),
        Sources = hh ? [new VacancySource { Source = SourceKind.HeadHunter, ExternalId = "1", Url = "u", HasEmployerTest = test }] : [],
    };

    [Fact]
    public void Hh_wins_then_telegram_then_email()
    {
        Assert.Equal(ApplicationChannel.HeadHunter, ApplyRoutes.Resolve(V("@hr_anna\nhr@acme.io", hh: true), true).Channel);
        Assert.Equal(new ApplyRoute(ApplicationChannel.TelegramDm, "@hr_anna"), ApplyRoutes.Resolve(V("hr@acme.io\n@hr_anna"), true));
        Assert.Equal(new ApplyRoute(ApplicationChannel.Email, "hr@acme.io"), ApplyRoutes.Resolve(V("Резюме на hr@acme.io"), true));
    }

    [Fact]
    public void Email_needs_a_configured_mailbox_and_an_employer_test_needs_a_human()
    {
        Assert.Null(ApplyRoutes.Resolve(V("hr@acme.io"), emailConfigured: false).Channel);
        Assert.Null(ApplyRoutes.Resolve(V(null, hh: true, test: true), true).Channel);
    }

    [Fact]
    public void Only_dm_and_email_attach_the_resume()
    {
        Assert.True(new ApplyRoute(ApplicationChannel.TelegramDm, "@x").AttachesResume);
        Assert.True(new ApplyRoute(ApplicationChannel.Email, "a@b.c").AttachesResume);
        Assert.False(new ApplyRoute(ApplicationChannel.HeadHunter, null).AttachesResume);
    }
}
