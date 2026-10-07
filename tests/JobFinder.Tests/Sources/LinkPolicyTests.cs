using JobFinder.Core.Domain;
using JobFinder.Integrations.Web;

namespace JobFinder.Tests.Sources;

/// <summary>What a link in a vacancy post is, on links taken from real posts of the monitored channels.</summary>
public sealed class LinkPolicyTests
{
    private static readonly LinkPolicy Policy = new(new EnrichmentOptions { SkipHosts = ["ads.example.net"], Platforms = ["jobs.example.org"] });

    [Theory]
    [InlineData("https://talanto.work/jobs/b8df888b-57fa-41df-898b-c37c906e6350", LinkRole.Page)]
    [InlineData("https://aristeksystems.com/career/full-stack-net-engineer/", LinkRole.Page)]
    [InlineData("https://hr.yandex.ru/vacancies/backend_developer_dotnet", LinkRole.Page)] // a company's careers subdomain is read
    [InlineData("https://t.me/FreeHRarka", LinkRole.Contact)]
    [InlineData("https://telegram.me/all_vakans_bot?start=c_38711", LinkRole.Apply)] // the bot hands out the contact
    [InlineData("https://t.me/plus_payment_bot?startapp=107", LinkRole.Skip)] // a mini-app ad
    [InlineData("mailto:AKHROMOV@ALFABANK.RU", LinkRole.Contact)]
    [InlineData("https://hh.ru/vacancy/49998674", LinkRole.HeadHunter)]
    [InlineData("https://spb.hh.ru/employer/3474068?hhtmFrom=vacancy", LinkRole.Skip)] // not the vacancy, and hh's budget is scarce
    [InlineData("https://rabota.by/vacancy/137863928", LinkRole.HeadHunter)]
    [InlineData("https://docs.google.com/forms/d/e/1FAIpQLSf/viewform", LinkRole.Apply)]
    [InlineData("https://forms.yandex.ru/u/66f1c1e4/", LinkRole.Apply)]
    [InlineData("https://vk.com/job_for_programmers", LinkRole.Skip)]
    [InlineData("https://max.ru/devs_it", LinkRole.Skip)]
    [InlineData("https://updatecv.me/?utm_source=tgbtm", LinkRole.Skip)] // the ad under a vacancy
    [InlineData("https://ads.example.net/banner", LinkRole.Skip)] // configured
    [InlineData("https://example.com/files/vacancy.pdf", LinkRole.Skip)]
    [InlineData("https://t.me/joinchat/AbCdEf", LinkRole.Skip)]
    [InlineData("https://t.me/c/1234567/45", LinkRole.Skip)] // a private channel: no public embed
    [InlineData("ftp://example.com/x", LinkRole.Skip)]
    [InlineData("https://ASP.NET", LinkRole.Skip)] // seen live: Telegram links the word "ASP.NET" as a domain
    [InlineData("http://asp.net/", LinkRole.Skip)]
    [InlineData("https://github.com/acme/test-task", LinkRole.Skip)]
    [InlineData("https://learn.microsoft.com/ru-ru/dotnet/", LinkRole.Skip)]
    [InlineData("javascript:void(0)", LinkRole.Skip)]
    public void Links_of_real_posts_are_classified(string url, LinkRole role) => Assert.Equal(role, Policy.Classify(url, "подпись").Role);

    [Theory]
    [InlineData("https://t.me/devs_it/5810", "https://t.me/devs_it/5810?embed=1&mode=tme")]
    [InlineData("https://t.me/s/rabotacschap/702", "https://t.me/rabotacschap/702?embed=1&mode=tme")]
    [InlineData("t.me/development_vakans/712", "https://t.me/development_vakans/712?embed=1&mode=tme")]
    public void A_public_telegram_post_is_read_through_its_embed(string url, string fetch)
    {
        var d = Policy.Classify(url);
        Assert.Equal(LinkRole.TelegramPost, d.Role);
        Assert.Equal(fetch, d.Fetch!.AbsoluteUri);
    }

    [Fact]
    public void A_google_doc_is_read_as_its_text_export_and_a_form_is_the_apply_link()
    {
        var doc = Policy.Classify("https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789/edit?usp=sharing");
        Assert.Equal(LinkRole.Page, doc.Role);
        Assert.Equal("https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789/export?format=txt", doc.Fetch!.AbsoluteUri);

        var form = Policy.Classify("https://docs.google.com/forms/d/e/1FAIpQLSf/viewform", "Анкета");
        Assert.Equal((ContactKind.Link, "Анкета"), (form.Contact!.Kind, form.Contact.Label));
    }

    [Theory]
    [InlineData("https://www.Talanto.work/jobs/1/?utm_source=tg&utm_medium=post&fbclid=x#apply", "https://talanto.work/jobs/1")]
    [InlineData("https://spb.hh.ru/vacancy/1?hhtmFrom=vacancy&from=share", "https://spb.hh.ru/vacancy/1")]
    [InlineData("https://example.com/job?id=42&ref=tg", "https://example.com/job?id=42")] // meaningful parameters stay
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("https://example.com:443/a/", "https://example.com/a")]
    public void Canonical_urls_drop_what_only_tracks_the_click(string url, string canonical) =>
        Assert.Equal(canonical, LinkPolicy.Canonical(new Uri(url)).AbsoluteUri);

    [Theory]
    [InlineData("Откликнуться", true)]
    [InlineData("👉 Откликнуться на вакансию", true)]
    [InlineData("APPLY", true)]
    [InlineData("Apply now", true)]
    [InlineData("Отправить резюме", true)]
    [InlineData("ASP.NET", false)] // a word of the text that happens to be a link
    [InlineData("Откликнуться в Telegram", false)] // a Telegram contact, taken as one already
    [InlineData("Подробнее", false)]
    [InlineData(null, false)]
    public void A_link_captioned_as_the_response_is_an_apply_link(string? label, bool apply) => Assert.Equal(apply, LinkPolicy.IsApplyLabel(label));

    [Fact]
    public void An_apply_link_is_kept_for_pages_and_hh_but_not_for_what_is_never_read()
    {
        Assert.Equal("https://careers.kaspersky.ru/vacancy/25812",
            Policy.ApplyLink(new PostLink { Url = "https://careers.kaspersky.ru/vacancy/25812?utm_source=tg", Label = "Откликнуться" })!.Value);
        Assert.NotNull(Policy.ApplyLink(new PostLink { Url = "https://rabota.by/vacancy/137863928", Label = "Откликнуться" }));
        Assert.Null(Policy.ApplyLink(new PostLink { Url = "https://vk.com/jobs", Label = "Откликнуться" }));
        Assert.Null(Policy.ApplyLink(new PostLink { Url = "https://careers.kaspersky.ru/vacancy/25812", Label = "Kaspersky" }));
    }

    [Theory]
    [InlineData("https://job.2gis.ru/vacancies/development/backend", "Откликнуться", LinkRole.Page)] // seen live: 2gis.ru is skipped (maps), its careers site is not
    [InlineData("https://2gis.ru/minsk/firm/70000001", "Офис", LinkRole.Skip)]
    [InlineData("https://velvetech.com/net-development-special/", ".NET", LinkRole.Skip)] // seen live: a link on the word ".NET" led to a services page
    [InlineData("https://acme.io/kafka-platform", "Kafka", LinkRole.Skip)]
    [InlineData("https://velvetech.com/careers/net-developer/", "Откликнуться", LinkRole.Page)]
    [InlineData("https://velvetech.com/careers/net-developer/", ".NET Developer", LinkRole.Page)] // a title is not a bare technology name
    public void Career_subdomains_are_read_and_links_on_a_technology_name_are_decoration(string url, string label, LinkRole role) =>
        Assert.Equal(role, Policy.Classify(url, label).Role);

    [Fact]
    public void A_page_is_requested_as_linked_and_known_by_its_canonical_form()
    {
        // Seen live: "simbirsoft.com" resolves to a dead address in the container, "www.simbirsoft.com" works.
        var d = Policy.Classify("https://www.simbirsoft.com/vacancies/qa-automation/?utm_source=teletype#apply");
        Assert.Equal("https://simbirsoft.com/vacancies/qa-automation", d.Url!.AbsoluteUri);
        Assert.Equal("https://www.simbirsoft.com/vacancies/qa-automation/", d.Fetch!.AbsoluteUri);
        Assert.Equal("https://job.alfabank.ru/vacancies/x_(net)?archetype=1", LinkPolicy.Clean(new Uri("https://job.alfabank.ru/vacancies/x_(net)?archetype=1&yclid=5")).AbsoluteUri);
    }

    [Fact]
    public void Urls_written_in_text_come_out_without_the_punctuation_around_them()
    {
        var urls = LinkPolicy.FindUrls("Подробнее: https://talanto.work/jobs/1. Форма (https://forms.gle/abc), вики https://ru.wikipedia.org/wiki/C_Sharp_(язык)! www.aristek.by»")
            .Select(l => l.Url).ToList();
        Assert.Equal(["https://talanto.work/jobs/1", "https://forms.gle/abc", "https://ru.wikipedia.org/wiki/C_Sharp_(язык)", "www.aristek.by"], urls);
    }

    [Theory]
    // A platform's APPLY goes to the employer's own vacancy page.
    [InlineData("https://teletype.in/@remoteit/Ze5d1LOPJqx", "https://www.simbirsoft.com/vacancies/qa-automation/", "APPLY", true, true)]
    // "Similar vacancies" on a vacancy page: a vacancy path, but not this vacancy.
    [InlineData("https://jobrocket.ru/ru/job/net-engineer-cex-birzha-a24adebc", "https://jobrocket.ru/job/business-analyst-spnd-6dd72d0a", "Business Analyst SPND", true, false)]
    // A company page that is not a vacancy itself: its vacancy links lead on.
    [InlineData("https://aristek.by/about", "https://aristek.by/careers/dotnet-engineer", "Full-Stack .NET", false, true)]
    [InlineData("https://company.by/news/hiring", "https://company.by/news/other", "Подробнее о вакансии", false, true)]
    [InlineData("https://teletype.in/@remoteit/Ze5d1LOPJqx", "https://teletype.in/about", "О Teletype", true, false)]
    [InlineData("https://teletype.in/@remoteit/Ze5d1LOPJqx", "https://teletype.in/login?redir=/@remoteit", "Войти", true, false)]
    // An employer site does not hand the crawl over to another site's "details".
    [InlineData("https://aristek.by/careers/x", "https://other.com/vacancy/1", "Подробнее", true, false)]
    // Seen live: the similar-vacancy cards of a vacancy page say "Подробнее" too — Golang and C++ openings were appended to a .NET one.
    [InlineData("https://h.careers/job/ad5d6ec2", "https://h.careers/job/ae0d92ca", "Golang-разработчик · Подробнее", true, false)]
    [InlineData("https://h.careers/job/ad5d6ec2", "https://h.careers/company/omega", "Подробнее о компании", true, false)]
    public void Only_links_into_the_same_vacancy_are_followed(string from, string to, string label, bool fromVacancy, bool follows) =>
        Assert.Equal(follows, Policy.LeadsToDetails(new Uri(from), new Uri(to), label, fromVacancy));

    [Theory]
    [InlineData("https://jobrocket.ru/ru/job/x", ContactKind.Telegram, "@jobrocket", true)] // the aggregator's own channel
    [InlineData("https://jobrocket.ru/ru/job/x", ContactKind.Telegram, "@freehrarka", false)] // the recruiter behind its button
    [InlineData("https://talanto.work/jobs/x", ContactKind.Telegram, "@talantosupport", true)]
    [InlineData("https://telegra.ph/x", ContactKind.Email, "dmca@telegram.org", true)] // a service mailbox anywhere
    [InlineData("https://jobs.example.org/x", ContactKind.Email, "help@example.org", true)] // configured platform
    [InlineData("https://aristeksystems.com/career/x", ContactKind.Email, "hr@aristeksystems.com", false)] // an employer's own site
    public void A_platforms_own_contacts_are_chrome_an_employers_are_not(string page, ContactKind kind, string value, bool chrome) =>
        Assert.Equal(chrome, Policy.IsPlatformContact(new Uri(page), new VacancyContact { Kind = kind, Value = value }));
}
