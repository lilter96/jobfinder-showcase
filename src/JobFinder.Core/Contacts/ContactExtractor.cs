using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using JobFinder.Core.Domain;
using PhoneNumbers;

namespace JobFinder.Core.Contacts;

/// <summary>
/// The one place that finds and normalizes contacts — in a post, a vacancy page, a link, or what the user typed:
/// <list type="bullet">
/// <item>e-mails, also written as "name (at) domain (dot) ru" / "name [собака] domain.ru", checked by <see cref="MailAddress"/>;</item>
/// <item>Telegram usernames and t.me links; a bot link is a <see cref="ContactKind.Link"/> (it hands out a contact, it is not one);</item>
/// <item>phones through libphonenumber: a number counts only when it is valid for its country (salaries, dates and ids are not), and is
/// kept in E.164 — a number without a country code is read as dialled from <see cref="PhoneRegions"/>;</item>
/// <item>LinkedIn profiles and WhatsApp links.</item>
/// </list>
/// Contacts come back in the order of the text: the first Telegram of a post is the first one its author wrote.
/// </summary>
public static partial class ContactExtractor
{
    /// <summary>Where a number without a country code is dialled from: the markets the posts come from, the likeliest first.</summary>
    public static readonly IReadOnlyList<string> PhoneRegions = ["BY", "RU", "KZ", "UZ", "UA"];

    private static readonly PhoneNumberUtil Phones = PhoneNumberUtil.GetInstance();

    /// <summary>t.me paths that are not accounts.</summary>
    private static readonly HashSet<string> ReservedTelegramPaths =
        new(["joinchat", "share", "addstickers", "addemoji", "addlist", "addtheme", "proxy", "socks", "iv", "s", "c", "setlanguage", "login", "confirmphone"],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Mailboxes nobody hires from: a vacancy page's footer carries them, a recruiter never does.</summary>
    private static readonly HashSet<string> ServiceMailboxes =
        new(["noreply", "no-reply", "no_reply", "donotreply", "do-not-reply", "dmca", "abuse", "privacy", "legal", "security", "postmaster", "webmaster",
             "mailbox", "press", "billing", "support", "help", "helpdesk", "feedback", "unsubscribe", "report"], StringComparer.OrdinalIgnoreCase);

    /// <param name="source">The page the text is from; null = a Telegram post.</param>
    /// <param name="bareMentions">
    /// "@name" alone is a Telegram username. True for a Telegram post; on a web page an "@name" may be a Teletype, Twitter or Instagram
    /// handle (teletype.in/@remoteit), so there it counts only after a Telegram word ("Telegram: @hr_anna", "тг @hr_anna").
    /// </param>
    public static List<VacancyContact> FromText(string? text, string? source = null, bool bareMentions = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<(int At, VacancyContact Contact)>();

        foreach (Match m in Email().Matches(Deobfuscate(text)))
            if (NormalizeEmail(m.Value) is { } mail)
                found.Add((m.Index, new VacancyContact { Kind = ContactKind.Email, Value = mail, Source = source }));

        foreach (Match m in TelegramLink().Matches(text))
            if (Telegram(m.Groups["user"].Value, m.Groups["start"].Success ? m.Groups["start"].Value : null, null, source) is { } tg)
                found.Add((m.Index, tg));

        foreach (Match m in Mention().Matches(text))
            if ((bareMentions || TelegramWord().IsMatch(text[Math.Max(0, m.Index - 40)..m.Index]))
                && Telegram(m.Groups["user"].Value, null, null, source) is { } tg)
                found.Add((m.Index, tg));

        foreach (Match m in LinkedIn().Matches(text))
            found.Add((m.Index, new VacancyContact { Kind = ContactKind.LinkedIn, Value = LinkedInUrl(m.Groups["id"].Value), Source = source }));

        foreach (Match m in WhatsApp().Matches(text))
            found.Add((m.Index, new VacancyContact { Kind = ContactKind.WhatsApp, Value = "+" + m.Groups["n"].Value.TrimStart('+'), Source = source }));

        foreach (var (at, phone) in FindPhones(MaskLinks(text)))
            found.Add((at, new VacancyContact { Kind = ContactKind.Phone, Value = phone, Source = source }));

        return Merge(found.OrderBy(f => f.At).Select(f => f.Contact));
    }

    /// <summary>A link that is itself a contact (mailto:, tel:, t.me/user, a bot, wa.me, a LinkedIn profile); null for any other page.</summary>
    public static VacancyContact? FromUrl(string? url, string? label = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var raw = url.Trim();
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var address = Uri.UnescapeDataString(raw[7..].Split('?', 2)[0]).Split(',', 2)[0];
            return NormalizeEmail(address) is { } mail ? new VacancyContact { Kind = ContactKind.Email, Value = mail, Label = label, Source = source } : null;
        }

        if (raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return NormalizePhone(Uri.UnescapeDataString(raw[4..])) is { } phone
                ? new VacancyContact { Kind = ContactKind.Phone, Value = phone, Label = label, Source = source }
                : null;

        if (TgResolve().Match(raw) is { Success: true } resolve)
            return Telegram(resolve.Groups["user"].Value, resolve.Groups["start"].Success ? resolve.Groups["start"].Value : null, label, source);

        if (TgUser().Match(raw) is { Success: true } user)
            return new VacancyContact { Kind = ContactKind.Telegram, Value = "tg://user?id=" + user.Groups["id"].Value, Label = label, Source = source };

        // A mini app (?startapp=, ?startattach=) is an ad next to vacancies (a wallet, a game), not a way to reach an employer.
        if (WholeTelegramLink().Match(raw) is { Success: true } tme)
            return MiniApp().IsMatch(raw) ? null : Telegram(tme.Groups["user"].Value, tme.Groups["start"].Success ? tme.Groups["start"].Value : null, label, source);

        if (WhatsApp().Match(raw) is { Success: true, Index: < 12 } wa)
            return new VacancyContact { Kind = ContactKind.WhatsApp, Value = "+" + wa.Groups["n"].Value.TrimStart('+'), Label = label, Source = source };

        if (LinkedIn().Match(raw) is { Success: true, Index: < 16 } li)
            return new VacancyContact { Kind = ContactKind.LinkedIn, Value = LinkedInUrl(li.Groups["id"].Value), Label = label, Source = source };

        return null;
    }

    /// <summary>What the user typed as a contact ("@hr_anna", "t.me/hr_anna", an e-mail, a phone, a LinkedIn URL): the first one found.</summary>
    public static VacancyContact? Classify(string? raw) => FromUrl(raw) ?? FromText(raw).FirstOrDefault();

    /// <summary>One entry per contact (kind + normalized value), in the given order; a later sighting fills a missing label or source.</summary>
    public static List<VacancyContact> Merge(IEnumerable<VacancyContact> contacts)
    {
        var merged = new List<VacancyContact>();
        var index = new Dictionary<(ContactKind, string), VacancyContact>();
        foreach (var c in contacts)
        {
            var key = (c.Kind, Key(c));
            if (index.TryGetValue(key, out var seen))
            {
                seen.Label ??= c.Label;
                seen.Source ??= c.Source;
                continue;
            }

            var copy = new VacancyContact { Kind = c.Kind, Value = c.Value, Label = c.Label, Source = c.Source };
            index[key] = copy;
            merged.Add(copy);
        }

        return merged;
    }

    /// <summary>The comparable form of a value (what <see cref="Merge"/> and the grounding of model output compare).</summary>
    public static string Key(VacancyContact c) => c.Kind is ContactKind.Link or ContactKind.LinkedIn ? c.Value.TrimEnd('/').ToLowerInvariant() : Normalize(c.Kind, c.Value).ToLowerInvariant();

    /// <summary>The stored form of a value of a known kind (what the company contacts registry keys by).</summary>
    public static string Normalize(ContactKind kind, string value) => kind switch
    {
        ContactKind.Email => value.Trim().ToLowerInvariant(),
        // Telegram usernames are case-insensitive: "@HR_Anna" and "@hr_anna" are one person.
        ContactKind.Telegram when !value.StartsWith("tg://", StringComparison.Ordinal) => "@" + value.Trim().TrimStart('@').ToLowerInvariant(),
        ContactKind.Phone or ContactKind.WhatsApp => NormalizePhone(value) ?? value.Trim(),
        _ => value.Trim(),
    };

    /// <summary>A company's general mailbox (info@, request@, sales@): a way in, but after a person's or HR's address.</summary>
    public static bool IsGenericMailbox(string email) =>
        email.IndexOf('@') is > 0 and var at && GenericMailboxes.Contains(email[..at]);

    private static readonly HashSet<string> GenericMailboxes =
        new(["info", "request", "requests", "sales", "office", "hello", "contact", "contacts", "mail", "marketing", "pr", "partners", "partner", "zakaz", "order"],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>A hotline, not a person: toll-free (8-800), premium and shared-cost numbers, universal access numbers.</summary>
    private static bool IsServiceNumber(PhoneNumber number) =>
        Phones.GetNumberType(number) is PhoneNumberType.TOLL_FREE or PhoneNumberType.PREMIUM_RATE or PhoneNumberType.SHARED_COST
            or PhoneNumberType.UAN or PhoneNumberType.VOICEMAIL;

    /// <summary>A mailbox of the site itself, not of a person who hires (noreply, dmca, support, …).</summary>
    public static bool IsServiceMailbox(string email) =>
        email.IndexOf('@') is > 0 and var at && ServiceMailboxes.Contains(email[..at]);

    /// <summary>"name@domain" in lower case, or null for a malformed address, a placeholder (example.com) or a file name (logo@2x.png).</summary>
    public static string? NormalizeEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().Trim('.', ',', ';', ':').ToLowerInvariant();
        if (!MailAddress.TryCreate(value, out var address) || address.Address != value) return null;
        var domain = address.Host;
        if (domain.StartsWith("example.", StringComparison.Ordinal) || domain.EndsWith(".example", StringComparison.Ordinal)) return null;
        var tld = domain[(domain.LastIndexOf('.') + 1)..];
        return tld is "png" or "jpg" or "jpeg" or "gif" or "svg" or "webp" or "css" or "js" ? null : value;
    }

    /// <summary>A phone in E.164 when libphonenumber finds it valid (with its country code, or dialled from one of <see cref="PhoneRegions"/>).</summary>
    public static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var region in PhoneRegions)
        {
            try
            {
                var number = Phones.Parse(raw, region);
                if (Phones.IsValidNumber(number)) return IsServiceNumber(number) ? null : Phones.Format(number, PhoneNumberFormat.E164);
            }
            catch (NumberParseException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The text with URLs and addresses blanked out (same length, so positions hold): the digits of wa.me/375…, a tracking id in a link or
    /// a numeric e-mail are not a phone to call.
    /// </summary>
    private static string MaskLinks(string text)
    {
        var chars = text.ToCharArray();
        foreach (var pattern in (Regex[])[AnyUrl(), Email(), TelegramLink()])
            foreach (Match m in pattern.Matches(text))
                Array.Fill(chars, ' ', m.Index, m.Length);
        return new string(chars);
    }

    /// <summary>Phones in a text: each span is read in the first region it is valid for; the same number twice is one.</summary>
    private static IEnumerable<(int At, string Phone)> FindPhones(string text)
    {
        var covered = new List<(int Start, int End)>();
        foreach (var region in PhoneRegions)
        {
            foreach (var match in Phones.FindNumbers(text, region, PhoneNumberUtil.Leniency.VALID, long.MaxValue))
            {
                var (start, end) = (match.Start, match.Start + match.Length);
                if (covered.Any(c => start < c.End && c.Start < end)) continue;
                // Short service numbers and extensions are not a way to reach a recruiter; a hotline (8-800 …) is not one either — and it
                // is valid in more than one country, so it cannot even be read right.
                if (match.RawString.Count(char.IsAsciiDigit) < 9) continue;
                covered.Add((start, end));
                if (IsServiceNumber(match.Number)) continue;
                yield return (start, Phones.Format(match.Number, PhoneNumberFormat.E164));
            }
        }
    }

    private static VacancyContact? Telegram(string user, string? start, string? label, string? source)
    {
        if (user.Length == 0 || ReservedTelegramPaths.Contains(user) || user.EndsWith('_')) return null;
        var username = user.ToLowerInvariant();
        if (username.EndsWith("bot", StringComparison.Ordinal))
        {
            // A bot that hands out the contact ("Посмотреть контакт"): the way to it, with its start parameter.
            var link = "https://t.me/" + username + (string.IsNullOrEmpty(start) ? "" : "?start=" + start);
            return new VacancyContact { Kind = ContactKind.Link, Value = link, Label = label ?? "Telegram-бот @" + username, Source = source };
        }

        return new VacancyContact { Kind = ContactKind.Telegram, Value = "@" + username, Label = label, Source = source };
    }

    private static string LinkedInUrl(string id) => "https://www.linkedin.com/in/" + Uri.UnescapeDataString(id).TrimEnd('/').ToLower(CultureInfo.InvariantCulture);

    /// <summary>"name (at) domain (dot) ru", "name[собака]domain.ru" → an address the pattern finds; the text around keeps its length roughly.</summary>
    private static string Deobfuscate(string text) => ObfuscatedDot().Replace(ObfuscatedAt().Replace(text, "@"), ".");

    [GeneratedRegex(@"(?<![\w.%+-])[A-Za-z0-9](?:[A-Za-z0-9._%+-]{0,62}[A-Za-z0-9_%+-])?@(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,24}(?![\w-])")]
    private static partial Regex Email();

    [GeneratedRegex(@"\s*[\[({<]\s*(?:at|собака|эт)\s*[\])}>]\s*|\s+собака\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ObfuscatedAt();

    [GeneratedRegex(@"\s*[\[({<]\s*(?:dot|точка)\s*[\])}>]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ObfuscatedDot();

    [GeneratedRegex(@"(?<![\w@./])@(?<user>[A-Za-z][A-Za-z0-9_]{3,31})(?![\w@])")]
    private static partial Regex Mention();

    [GeneratedRegex(@"(?<![\w.])(?:https?://)?(?:www\.)?(?:t|telegram)\.me/(?<user>[A-Za-z][A-Za-z0-9_]{3,31})(?:\?start=(?<start>[\w-]{1,64}))?(?![\w/]|\?start(?:app|attach|group|channel)=)", RegexOptions.IgnoreCase)]
    private static partial Regex TelegramLink();

    [GeneratedRegex(@"[?&](?:startapp|startattach|startgroup|startchannel|game)=", RegexOptions.IgnoreCase)]
    private static partial Regex MiniApp();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?:t|telegram)\.me/(?<user>[A-Za-z][A-Za-z0-9_]{3,31})/?(?:\?start=(?<start>[\w-]{1,64}))?(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WholeTelegramLink();

    [GeneratedRegex(@"^tg://resolve\?domain=(?<user>[A-Za-z][A-Za-z0-9_]{3,31})(?:&start=(?<start>[\w-]{1,64}))?", RegexOptions.IgnoreCase)]
    private static partial Regex TgResolve();

    [GeneratedRegex(@"^tg://user\?id=(?<id>\d{1,20})$", RegexOptions.IgnoreCase)]
    private static partial Regex TgUser();

    [GeneratedRegex(@"(?:https?://)?(?:[a-z]{2,3}\.)?linkedin\.com/in/(?<id>[\w\-%.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedIn();

    [GeneratedRegex(@"(?:https?://)?(?:wa\.me/|api\.whatsapp\.com/send/?\?phone=)(?<n>\+?\d{7,15})", RegexOptions.IgnoreCase)]
    private static partial Regex WhatsApp();

    [GeneratedRegex(@"(?:https?://|www\.|wa\.me/)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AnyUrl();

    [GeneratedRegex(@"(?:telegram|телеграм\w*|телег\w*|\bтг\b|\btg\b)", RegexOptions.IgnoreCase)]
    private static partial Regex TelegramWord();
}
