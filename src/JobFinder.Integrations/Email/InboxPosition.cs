using JobFinder.Core.Text;

namespace JobFinder.Integrations.Email;

public sealed record InboxPosition
{
    public string AccountId { get; set; } = "";
    public uint UidValidity { get; set; }
    public uint LastUid { get; set; }
    public bool ReplayAll { get; set; }

    public uint AfterUidFor(string accountId, uint validity)
    {
        if (string.IsNullOrWhiteSpace(accountId) || validity == 0) throw new ArgumentException("mailbox identity and UIDVALIDITY are required");
        return AccountId == accountId && UidValidity == validity ? LastUid : 0;
    }

    public bool ReplayAllFor(string accountId, uint validity) => AccountId == accountId
        && (ReplayAll || UidValidity != 0 && UidValidity != validity);
}

public sealed record InboxBatch
{
    public string AccountId { get; set; } = "";
    public uint UidValidity { get; set; }
    public uint AfterUid { get; set; }
    public bool ReplayAll { get; set; }
    public bool HasMore { get; set; }
    public List<InboxMessage> Messages { get; set; } = [];
}

public static class InboxIdentity
{
    public static string Account(EmailOptions options) => CanonicalJson.Hash(System.Text.Json.JsonSerializer.Serialize(new
    {
        Host = options.ImapHost.Trim().TrimEnd('.').ToLowerInvariant(), Port = options.ImapPort,
        options.Username, Mailbox = "INBOX",
    }));

    public static string MessageId(string accountId, uint validity, uint uid) => $"imap:{accountId}:{validity}:{uid}";

    public static uint? UidFor(string messageId, string accountId, uint validity)
    {
        var parts = messageId.Split(':');
        return parts.Length == 4 && parts[0] == "imap" && parts[1] == accountId
            && uint.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var savedValidity)
            && savedValidity == validity && uint.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var uid)
            && uid > 0 ? uid : null;
    }
}
