using JobFinder.App;
using JobFinder.Core.Domain;

namespace JobFinder.Tests.Core;

/// <summary>How the vacancy page shows a contact (the cases are real rows).</summary>
public sealed class ContactDisplayTests
{
    private static VacancyContact C(ContactKind kind, string value, string? label = null) => new() { Kind = kind, Value = value, Label = label };

    [Fact]
    public void An_apply_link_reads_as_its_caption_with_its_site_not_as_a_long_url()
    {
        var apply = C(ContactKind.Link, "https://job.alfabank.ru/vacancies/moskva/remote-job/razrabotchik-c_-_-_net-(middle-_-middle+)_38098?archetype=1004&text=c", "APPLY");
        Assert.Equal(("APPLY", "job.alfabank.ru", apply.Value), (Ui.ContactText(apply), Ui.ContactNote(apply), Ui.ContactHref(apply)));

        var bot = C(ContactKind.Link, "https://t.me/all_vakans_bot?start=c_38711", "Посмотреть контакт");
        Assert.Equal(("Посмотреть контакт", "t.me"), (Ui.ContactText(bot), Ui.ContactNote(bot)));

        var bare = C(ContactKind.Link, "https://forms.gle/abc");
        Assert.Equal(("https://forms.gle/abc", (string?)null), (Ui.ContactText(bare), Ui.ContactNote(bare)));
    }

    [Theory]
    [InlineData(ContactKind.Email, "akhromov@alfabank.ru", "AKHROMOV@ALFABANK.RU", null)] // the mailto's own text
    [InlineData(ContactKind.Email, "info@baitun.kz", "info@baitun.kz", null)]
    [InlineData(ContactKind.Telegram, "@remote_hr", "@Remote_HR", null)]
    [InlineData(ContactKind.Telegram, "@nurbek_kadirali", "Nurbek Kadirali", "Nurbek Kadirali")] // a name says more
    [InlineData(ContactKind.Phone, "+77750963600", "+7 702 075 00 88", "+7 702 075 00 88")] // the site's caption differs from its href: shown
    [InlineData(ContactKind.Telegram, "@courierus", null, null)]
    public void A_label_is_shown_only_when_it_says_more_than_the_value(ContactKind kind, string value, string? label, string? note) =>
        Assert.Equal(note, Ui.ContactNote(C(kind, value, label)));

    [Theory]
    [InlineData(ContactKind.Telegram, "@hr_anna", "https://t.me/hr_anna")]
    [InlineData(ContactKind.Telegram, "tg://user?id=42", null)] // known by id only: no link to open
    [InlineData(ContactKind.Email, "hr@acme.io", "mailto:hr@acme.io")]
    [InlineData(ContactKind.Phone, "+375291234567", "tel:+375291234567")]
    [InlineData(ContactKind.WhatsApp, "+375291234567", "https://wa.me/375291234567")]
    public void A_contact_opens_where_it_should(ContactKind kind, string value, string? href) => Assert.Equal(href, Ui.ContactHref(C(kind, value)));
}
