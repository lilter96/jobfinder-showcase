using JobFinder.Core.Domain;
using TL;

namespace JobFinder.Integrations.Telegram;

/// <summary>
/// The links of a message that its plain text loses: a text link («Откликнуться» → URL), a URL as Telegram marked it, a mention of a
/// person without a public username (their id, and the username when the message's users carry one), and the URL buttons under a post.
/// Entity offsets are UTF-16 code units, as .NET strings index.
/// </summary>
public static class TelegramLinks
{
    /// <param name="user">The message's users by id (from the update or history batch); null when unknown.</param>
    public static List<PostLink> Extract(Message message, Func<long, User?> user)
    {
        var text = message.message ?? "";
        var links = new List<PostLink>();
        foreach (var entity in message.entities ?? [])
        {
            var slice = Slice(text, entity.offset, entity.length);
            switch (entity)
            {
                case MessageEntityTextUrl textUrl:
                    links.Add(new PostLink { Url = textUrl.url, Label = Label(slice) });
                    break;
                case MessageEntityUrl when slice.Length > 0:
                    links.Add(new PostLink { Url = slice.Contains("://", StringComparison.Ordinal) ? slice : "https://" + slice });
                    break;
                case MessageEntityMentionName mention:
                    var person = user(mention.user_id);
                    links.Add(new PostLink
                    {
                        Url = person?.MainUsername is { } username ? "https://t.me/" + username : $"tg://user?id={mention.user_id}",
                        Label = Label(slice) ?? Name(person),
                    });
                    break;
            }
        }

        if (message.reply_markup is ReplyInlineMarkup markup)
            foreach (var button in markup.rows.SelectMany(r => r.buttons))
                switch (button)
                {
                    case KeyboardButtonUrl url:
                        links.Add(new PostLink { Url = url.url, Label = Label(url.text) });
                        break;
                    case KeyboardButtonUrlAuth auth:
                        links.Add(new PostLink { Url = auth.url, Label = Label(auth.text) });
                        break;
                }

        // One link once (a URL written in the text and repeated as a button), the first label kept.
        return links.Where(l => l.Url.Length > 0).GroupBy(l => l.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PostLink { Url = g.Key, Label = g.Select(l => l.Label).FirstOrDefault(l => l is not null) }).ToList();
    }

    private static string Slice(string text, int offset, int length) =>
        offset < 0 || length <= 0 || offset >= text.Length ? "" : text.Substring(offset, Math.Min(length, text.Length - offset));

    private static string? Label(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Name(User? user) => user is null ? null : Label($"{user.first_name} {user.last_name}");
}
