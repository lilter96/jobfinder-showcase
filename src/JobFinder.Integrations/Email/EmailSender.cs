using Telemetry = JobFinder.Core.Telemetry.Telemetry;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace JobFinder.Integrations.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";

    public bool Enabled { get; set; } = true;

    /// <summary>Gmail: smtp.gmail.com:587 with STARTTLS and an app password (myaccount.google.com/apppasswords).</summary>
    public string Host { get; set; } = "smtp.gmail.com";

    public int Port { get; set; } = 587;

    /// <summary>Replies and hh notifications are read here (same account, same app password).</summary>
    public string ImapHost { get; set; } = "imap.gmail.com";

    public int ImapPort { get; set; } = 993;

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>Defaults to <see cref="Username"/>.</summary>
    public string FromAddress { get; set; } = "";

    public string FromName { get; set; } = "";

    /// <summary>A copy of every application to the sender's own box: the sent folder of a real mailbox, not only a row in a table.</summary>
    public bool BccSelf { get; set; } = true;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Username)
                                && !string.IsNullOrWhiteSpace(Password);
}

public sealed record EmailAttachment(string Path, string FileName, long? TailoredResumeId = null, string? Sha256 = null, byte[]? Content = null);

/// <summary>A message read from the inbox (IMAP).</summary>
public sealed record InboxMessage(uint Uid, string MessageId, string? InReplyTo, IReadOnlyList<string> References, string FromAddress,
    string? FromName, string Subject, string Text, DateTimeOffset Date);

/// <summary>Applications by e-mail over SMTP (MailKit): plain-text body, the resume attached; replies read back over IMAP.</summary>
public sealed partial class EmailSender(EmailOptions options)
{
    public bool IsConfigured => options.IsConfigured;

    public string InboxAccountId => InboxIdentity.Account(options);

    /// <summary>The sender's own address (test sends go there).</summary>
    public string SelfAddress => string.IsNullOrWhiteSpace(options.FromAddress) ? options.Username : options.FromAddress;

    public Task<string> SendToSelfAsync(string subject, string body, EmailAttachment? attachment, CancellationToken ct) =>
        SendAsync(SelfAddress, subject, body, attachment, ct);

    /// <returns>The Message-ID of the sent message: replies reference it.</returns>
    public async Task<string> SendAsync(string to, string subject, string body, EmailAttachment? attachment, CancellationToken ct,
        string? inReplyTo = null)
    {
        if (!options.IsConfigured) throw new InvalidOperationException("почта не настроена (Email:Username / Email:Password)");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, SelfAddress));
        message.To.Add(MailboxAddress.Parse(to));
        if (options.BccSelf) message.Bcc.Add(new MailboxAddress(options.FromName, SelfAddress));
        message.Subject = subject;
        if (!string.IsNullOrWhiteSpace(inReplyTo))
        {
            message.InReplyTo = inReplyTo;
            message.References.Add(inReplyTo);
        }

        var builder = new BodyBuilder { TextBody = body };
        if (attachment is not null)
        {
            await using Stream stream = attachment.Content is { } content ? new MemoryStream(content, writable: false) : File.OpenRead(attachment.Path);
            await builder.Attachments.AddAsync(attachment.FileName, stream, ContentType.Parse(ContentTypeFor(attachment.FileName)), ct);
        }

        message.Body = builder.ToMessageBody();
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(SelfAddress.Split('@').Last());

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTlsWhenAvailable, ct);
        await smtp.AuthenticateAsync(options.Username, options.Password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
        return message.MessageId;
    }

    /// <summary>
    /// Inbox messages after the saved mailbox position, or from the recent window when its UIDVALIDITY changed.
    /// Read-only: nothing is marked as seen, the user's mailbox looks untouched.
    /// </summary>
    public async Task<InboxBatch> ReadInboxAsync(InboxPosition position, int firstRunDays, int max, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("read-inbox", "email").Tag("mailbox.after_uid", position.LastUid);
        if (!options.IsConfigured) throw new InvalidOperationException("mailbox is not configured");
        if (firstRunDays <= 0 || max <= 0) throw new ArgumentOutOfRangeException(nameof(firstRunDays));
        using var imap = new ImapClient();
        await imap.ConnectAsync(options.ImapHost, options.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(options.Username, options.Password, ct);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var afterUid = position.AfterUidFor(InboxAccountId, inbox.UidValidity);
        var batch = new InboxBatch { AccountId = InboxAccountId, UidValidity = inbox.UidValidity, AfterUid = afterUid,
            ReplayAll = position.ReplayAllFor(InboxAccountId, inbox.UidValidity) };
        stage.Tag("mailbox.uidvalidity", batch.UidValidity).Tag("mailbox.replay", batch.ReplayAll);
        if (afterUid == uint.MaxValue)
        {
            await imap.DisconnectAsync(true, ct);
            return batch;
        }
        SearchQuery query = afterUid == 0
            ? batch.ReplayAll ? SearchQuery.All : SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-firstRunDays))
            : SearchQuery.Uids(new UniqueIdRange(new UniqueId(afterUid + 1), UniqueId.MaxValue));
        var pending = (await inbox.SearchAsync(query, ct)).Where(uid => uid.Id > afterUid).OrderBy(uid => uid.Id).ToList();
        batch.HasMore = pending.Count > max;
        var uids = pending.Take(max);
        foreach (var uid in uids)
        {
            batch.Messages.Add(ToInbox(uid, await inbox.GetMessageAsync(uid, ct), batch.UidValidity));
        }
        CheckValidity(batch.UidValidity, inbox.UidValidity);
        stage.Tag("message.count", batch.Messages.Count);
        await imap.DisconnectAsync(true, ct);
        return batch;
    }

    /// <summary>One message by its Message-ID (re-reading a message whose text was taken badly).</summary>
    public async Task<InboxMessage?> FindAsync(string messageId, CancellationToken ct)
    {
        if (!options.IsConfigured) return null;
        using var imap = new ImapClient();
        await imap.ConnectAsync(options.ImapHost, options.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(options.Username, options.Password, ct);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var validity = imap.Inbox.UidValidity;
        if (messageId.StartsWith("imap:", StringComparison.Ordinal))
        {
            var savedUid = InboxIdentity.UidFor(messageId, InboxAccountId, validity);
            InboxMessage? saved = null;
            if (savedUid is { } value)
            {
                try { saved = ToInbox(new UniqueId(value), await imap.Inbox.GetMessageAsync(new UniqueId(value), ct), validity); }
                catch (MessageNotFoundException) { saved = null; }
            }
            CheckValidity(validity, imap.Inbox.UidValidity);
            await imap.DisconnectAsync(true, ct);
            return saved;
        }
        var uids = await imap.Inbox.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId.Trim('<', '>')), ct);
        InboxMessage? found = uids.Count == 0 ? null : ToInbox(uids[^1], await imap.Inbox.GetMessageAsync(uids[^1], ct), validity);
        CheckValidity(validity, imap.Inbox.UidValidity);
        await imap.DisconnectAsync(true, ct);
        return found;
    }

    internal InboxMessage ToInbox(UniqueId uid, MimeMessage m, uint validity)
    {
        if (!uid.IsValid || validity == 0) throw new ArgumentException("message requires a UID and UIDVALIDITY");
        var from = m.From.Mailboxes.FirstOrDefault();
        return new InboxMessage(uid.Id, string.IsNullOrWhiteSpace(m.MessageId) ? InboxIdentity.MessageId(InboxAccountId, validity, uid.Id) : m.MessageId,
            m.InReplyTo, m.References.ToList(), from?.Address ?? "", from?.Name,
            m.Subject ?? "", BodyText(m.TextBody, m.HtmlBody), m.Date);
    }

    internal static void CheckValidity(uint expected, uint observed)
    {
        if (expected == 0 || expected != observed) throw new InvalidOperationException("UIDVALIDITY changed during mailbox reading; retry without advancing the cursor");
    }

    /// <summary>
    /// What a person reads in the message. The text part when it says something (notification mails often carry only a stub there);
    /// otherwise the HTML without head, styles, scripts and comments — a notification from hh is mostly CSS before the actual message.
    /// </summary>
    public static string BodyText(string? text, string? html)
    {
        var plain = text?.Trim() ?? "";
        if (html is null || plain.Length >= 200) return plain;
        var stripped = System.Text.RegularExpressions.Regex.Replace(html, @"<(head|style|script|title)\b[\s\S]*?</\1\s*>|<!--[\s\S]*?-->", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var fromHtml = Core.Text.TextUtil.HtmlToText(Invisible().Replace(stripped, ""));
        return fromHtml.Length > plain.Length ? fromHtml : plain;
    }

    /// <summary>Preheader padding of mailing templates: zero-width joiners, soft hyphens, Braille blanks.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"[\u200B-\u200F\u00AD\u034F\u2800\uFEFF\u2060]+")]
    private static partial System.Text.RegularExpressions.Regex Invisible();

    /// <summary>Connect + authenticate to SMTP and IMAP, nothing sent (<c>--doctor</c>).</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTlsWhenAvailable, ct);
            await smtp.AuthenticateAsync(options.Username, options.Password, ct);
            await smtp.DisconnectAsync(true, ct);
        }

        using var imap = new ImapClient();
        await imap.ConnectAsync(options.ImapHost, options.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await imap.AuthenticateAsync(options.Username, options.Password, ct);
        await imap.DisconnectAsync(true, ct);
    }

    private static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };
}
