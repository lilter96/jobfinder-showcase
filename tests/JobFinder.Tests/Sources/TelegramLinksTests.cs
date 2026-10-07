using JobFinder.Integrations.Telegram;
using TL;

namespace JobFinder.Tests.Sources;

/// <summary>The links of a Telegram post that its plain text loses — the reason half the channel vacancies had no contact.</summary>
public sealed class TelegramLinksTests
{
    private static MessageEntityTextUrl TextUrl(string text, string slice, string url)
    {
        var at = text.IndexOf(slice, StringComparison.Ordinal);
        return new MessageEntityTextUrl { offset = at, length = slice.Length, url = url };
    }

    [Fact]
    public void Text_links_keep_their_caption_even_after_emoji_in_utf16_offsets()
    {
        // 🔥 and 📩 are surrogate pairs: Telegram counts offsets in UTF-16 units, as .NET strings index.
        const string text = "🔥 Backend .NET / C# Developer\n📩 Посмотреть контакт · Откликнуться";
        var message = new Message
        {
            message = text,
            entities =
            [
                TextUrl(text, "Backend .NET / C# Developer", "https://talanto.work/jobs/b8df888b"),
                TextUrl(text, "Посмотреть контакт", "https://telegram.me/all_vakans_bot?start=c_38711"),
                TextUrl(text, "Откликнуться", "https://aristeksystems.com/career/full-stack-net-engineer/"),
            ],
        };

        var links = TelegramLinks.Extract(message, _ => null);

        Assert.Equal(
        [
            ("https://talanto.work/jobs/b8df888b", "Backend .NET / C# Developer"),
            ("https://telegram.me/all_vakans_bot?start=c_38711", "Посмотреть контакт"),
            ("https://aristeksystems.com/career/full-stack-net-engineer/", "Откликнуться"),
        ], links.Select(l => (l.Url, l.Label)));
    }

    [Fact]
    public void A_bare_url_gets_its_scheme_and_a_mention_is_resolved_to_the_persons_username_when_known()
    {
        const string text = "Детали: talanto.work/jobs/1, пишите Анне или Ивану";
        var message = new Message
        {
            message = text,
            entities =
            [
                new MessageEntityUrl { offset = text.IndexOf("talanto", StringComparison.Ordinal), length = "talanto.work/jobs/1".Length },
                new MessageEntityMentionName { offset = text.IndexOf("Анне", StringComparison.Ordinal), length = 4, user_id = 42 },
                new MessageEntityMentionName { offset = text.IndexOf("Ивану", StringComparison.Ordinal), length = 5, user_id = 43 },
            ],
        };
        var users = new Dictionary<long, User> { [42] = new() { id = 42, username = "HR_Anna", first_name = "Анна" } };

        var links = TelegramLinks.Extract(message, id => users.GetValueOrDefault(id));

        Assert.Equal(
        [
            ("https://talanto.work/jobs/1", (string?)null),
            ("https://t.me/HR_Anna", "Анне"),
            ("tg://user?id=43", "Ивану"), // no public username: still a person the post named
        ], links.Select(l => (l.Url, l.Label)));
    }

    [Fact]
    public void Url_buttons_under_a_post_are_links_and_one_url_is_kept_once()
    {
        const string text = "Senior .NET. Откликнуться";
        var message = new Message
        {
            message = text,
            entities = [TextUrl(text, "Откликнуться", "https://forms.gle/abc")],
            reply_markup = new ReplyInlineMarkup
            {
                rows =
                [
                    new KeyboardButtonRow { buttons = [new KeyboardButtonUrl { text = "📝 Анкета", url = "https://forms.gle/abc" }] },
                    new KeyboardButtonRow { buttons = [new KeyboardButtonUrl { text = "Рекрутер", url = "https://t.me/hr_anna" }, new KeyboardButtonCallback { text = "👍" }] },
                ],
            },
        };

        var links = TelegramLinks.Extract(message, _ => null);

        Assert.Equal([("https://forms.gle/abc", "Откликнуться"), ("https://t.me/hr_anna", "Рекрутер")], links.Select(l => (l.Url, l.Label)));
    }

    [Fact]
    public void Entities_outside_the_text_are_ignored_not_thrown()
    {
        var message = new Message { message = "коротко", entities = [new MessageEntityUrl { offset = 50, length = 10 }, new MessageEntityTextUrl { offset = 3, length = 99, url = "https://x.io" }] };
        Assert.Equal([("https://x.io", "отко")], TelegramLinks.Extract(message, _ => null).Select(l => (l.Url, l.Label)));
    }
}
