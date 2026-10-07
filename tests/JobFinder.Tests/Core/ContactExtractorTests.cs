using JobFinder.Core.Contacts;
using JobFinder.Core.Domain;

namespace JobFinder.Tests.Core;

/// <summary>Contacts as real posts write them (samples from the Telegram channels the app reads), and what must not pass for one.</summary>
public sealed class ContactExtractorTests
{
    private static List<(ContactKind, string)> Found(string text) => ContactExtractor.FromText(text).Select(c => (c.Kind, c.Value)).ToList();

    [Fact]
    public void A_recruiter_post_yields_every_contact_normalized_in_text_order()
    {
        const string post = """
            Отклики и вопросы: @Nurbek_Kadirali или +7 (963) 9187609
            Резюме на Dmitrii.Nikitin.777@gmail.com, копию — в t.me/hr_anna
            WhatsApp: https://wa.me/375291234567 · LinkedIn: linkedin.com/in/Anna-Recruiter/
            """;

        Assert.Equal(
        [
            (ContactKind.Telegram, "@nurbek_kadirali"),
            (ContactKind.Phone, "+79639187609"),
            (ContactKind.Email, "dmitrii.nikitin.777@gmail.com"),
            (ContactKind.Telegram, "@hr_anna"),
            (ContactKind.WhatsApp, "+375291234567"),
            (ContactKind.LinkedIn, "https://www.linkedin.com/in/anna-recruiter"),
        ], Found(post));
    }

    [Theory]
    [InlineData("+77476703775", "+77476703775")] // Kazakhstan with the country code
    [InlineData("87083343695", "+77083343695")] // Kazakhstan dialled locally: invalid in RU, valid in KZ
    [InlineData("8 (029) 123-45-67", "+375291234567")] // Belarus dialled locally
    [InlineData("+375 (29) 123-45-67", "+375291234567")]
    [InlineData("8 (963) 918-76-09", "+79639187609")] // Russia dialled locally: invalid in BY, valid in RU
    public void Phones_are_validated_per_country_and_kept_in_e164(string written, string e164) =>
        Assert.Equal([(ContactKind.Phone, e164)], Found($"Звоните: {written}, до 18:00"));

    [Theory]
    [InlineData("Горячая линия 8 800 200-99-24")] // seen live on simbirsoft.com: read as Belarus it became +375 800…, it is Russia's toll-free
    [InlineData("Звоните бесплатно: +7 800 555-35-35")]
    [InlineData("Справочная 8-801-100-12-34")] // Belarus toll-free
    public void Hotlines_are_not_a_recruiters_phone(string text) =>
        Assert.DoesNotContain(Found(text), c => c.Item1 == ContactKind.Phone);

    [Fact]
    public void A_hotline_does_not_hide_a_real_number_next_to_it() =>
        Assert.Equal([(ContactKind.Phone, "+375291234567")], Found("Горячая линия 8 800 200-99-24, рекрутер +375 29 123-45-67"));

    [Theory]
    [InlineData("Зарплата 300 000 – 350 000 ₽ на руки")]
    [InlineData("Опыт 3–6 лет, дедлайн 27.09.2026, 5/2 с 10:00 до 19:00")]
    [InlineData("Вакансия #38711, ИНН 7707083893, 5000+ RPS")]
    public void Salaries_dates_and_ids_are_not_phones(string text) =>
        Assert.DoesNotContain(Found(text), c => c.Item1 == ContactKind.Phone);

    [Theory]
    [InlineData("hr (at) company (dot) ru", "hr@company.ru")]
    [InlineData("cv[собака]company.by", "cv@company.by")]
    [InlineData("Пишите: jobs собака yandex.ru", "jobs@yandex.ru")]
    public void Obfuscated_emails_are_read(string text, string email) =>
        Assert.Contains((ContactKind.Email, email), Found(text));

    [Theory]
    [InlineData("пример email@example.com")] // a form placeholder
    [InlineData("<img src=\"logo@2x.png\">")] // a retina image, not an address
    [InlineData("user@localhost")]
    public void Placeholders_and_file_names_are_not_emails(string text) =>
        Assert.DoesNotContain(Found(text), c => c.Item1 == ContactKind.Email);

    [Fact]
    public void An_email_is_not_also_a_telegram_mention_and_a_post_link_is_not_an_account()
    {
        var found = Found("hr@alfabank.ru · подробнее t.me/devs_it/5810 · чат t.me/joinchat/AbCdEf · t.me/+InviteHash");
        Assert.Equal([(ContactKind.Email, "hr@alfabank.ru")], found);
    }

    [Theory]
    [InlineData("22 сентября · @remoteit · REMOTE RUSSIA", null)] // a Teletype profile on teletype.in, not Telegram
    [InlineData("Follow us @acme_jobs on Instagram", null)]
    [InlineData("Резюме присылайте в Telegram: @hr_anna", "@hr_anna")]
    [InlineData("Вопросы — в тг @hr_anna", "@hr_anna")]
    [InlineData("Связь: t.me/hr_anna", "@hr_anna")] // a t.me link is Telegram anywhere
    public void On_a_web_page_a_bare_handle_counts_only_next_to_a_telegram_word(string text, string? expected) =>
        Assert.Equal(expected, ContactExtractor.FromText(text, "https://page.example", bareMentions: false).SingleOrDefault(c => c.Kind == ContactKind.Telegram)?.Value);

    [Fact]
    public void A_bot_that_hands_out_the_contact_is_a_link_with_its_start_parameter()
    {
        var bot = Assert.Single(ContactExtractor.FromText("📩 Посмотреть контакт: https://telegram.me/all_vakans_bot?start=c_38711"));
        Assert.Equal(ContactKind.Link, bot.Kind);
        Assert.Equal("https://t.me/all_vakans_bot?start=c_38711", bot.Value);
    }

    [Theory]
    [InlineData("mailto:AKHROMOV@ALFABANK.RU?subject=CV", ContactKind.Email, "akhromov@alfabank.ru")]
    [InlineData("tel:+375291234567", ContactKind.Phone, "+375291234567")]
    [InlineData("https://t.me/FreeHRarka", ContactKind.Telegram, "@freehrarka")]
    [InlineData("tg://resolve?domain=hr_anna", ContactKind.Telegram, "@hr_anna")]
    [InlineData("tg://user?id=123456789", ContactKind.Telegram, "tg://user?id=123456789")]
    [InlineData("https://telegram.me/all_vakans_bot?start=c_39000", ContactKind.Link, "https://t.me/all_vakans_bot?start=c_39000")]
    [InlineData("https://www.linkedin.com/in/some-recruiter/", ContactKind.LinkedIn, "https://www.linkedin.com/in/some-recruiter")]
    public void A_link_that_is_a_contact_is_recognized_with_its_label(string url, ContactKind kind, string value)
    {
        var c = ContactExtractor.FromUrl(url, "Откликнуться", "https://example.org/page");
        Assert.NotNull(c);
        Assert.Equal((kind, value), (c.Kind, c.Value));
        Assert.Equal("Откликнуться", c.Label);
        Assert.Equal("https://example.org/page", c.Source);
    }

    [Theory]
    [InlineData("https://t.me/devs_it/5810")] // a post, not an account
    [InlineData("https://talanto.work/jobs/b8df888b")]
    [InlineData("https://t.me/+InviteHash")]
    [InlineData("mailto:not-an-address")]
    public void A_page_link_is_not_a_contact(string url) => Assert.Null(ContactExtractor.FromUrl(url));

    [Fact]
    public void Merge_keeps_one_entry_per_contact_and_fills_the_label_from_a_later_sighting()
    {
        var merged = ContactExtractor.Merge(
        [
            new VacancyContact { Kind = ContactKind.Telegram, Value = "@HR_Anna" },
            new VacancyContact { Kind = ContactKind.Telegram, Value = "@hr_anna", Label = "Анна, HR", Source = "https://page" },
            new VacancyContact { Kind = ContactKind.Phone, Value = "+375 29 123-45-67" },
            new VacancyContact { Kind = ContactKind.Phone, Value = "+375291234567" },
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(("Анна, HR", "https://page"), (merged[0].Label, merged[0].Source));
    }

    [Theory]
    [InlineData("noreply@site.ru", true)]
    [InlineData("dmca@telegram.org", true)]
    [InlineData("support@talanto.work", true)]
    [InlineData("hr@company.ru", false)]
    [InlineData("akhromov@alfabank.ru", false)]
    public void Service_mailboxes_are_told_from_hiring_ones(string email, bool service) =>
        Assert.Equal(service, ContactExtractor.IsServiceMailbox(email));

    [Theory]
    [InlineData("@hr_anna", ContactKind.Telegram, "@hr_anna")]
    [InlineData("t.me/HR_Anna", ContactKind.Telegram, "@hr_anna")]
    [InlineData("Anna@Company.ru", ContactKind.Email, "anna@company.ru")]
    [InlineData("+7 963 918-76-09", ContactKind.Phone, "+79639187609")]
    public void What_the_user_types_is_classified(string raw, ContactKind kind, string value)
    {
        var c = ContactExtractor.Classify(raw);
        Assert.Equal((kind, value), (c!.Kind, c.Value));
    }
}
