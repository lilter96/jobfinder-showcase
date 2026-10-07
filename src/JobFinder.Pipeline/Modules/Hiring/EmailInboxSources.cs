using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record EmailInboxSnapshot
{
    public int Version { get; set; } = 1;
    public string AccountId { get; set; } = "";
    public string? StateJson { get; set; }
    public string? LegacyJson { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

public sealed record EmailInboxState
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public InboxPosition Position { get; set; } = new();
    public string BatchHash { get; set; } = "";
    public string? LegacyJson { get; set; }
}

public sealed record EmailInboxObservation
{
    public uint Uid { get; set; }
    public string MessageId { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Disposition { get; set; } = "";
    public Guid? RequestId { get; set; }
}

public sealed record EmailInboxReceipt
{
    public int Version { get; set; } = 1;
    public string? BeforeJson { get; set; }
    public string AfterJson { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public List<EmailInboxObservation> Observations { get; set; } = [];
}

public sealed record EmailHhRefresh
{
    public int Version { get; set; } = 1;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
}

public sealed record EmailInboxPublication(bool Published, int Items, int Matched, bool HhRefreshNeeded);

public sealed class EmailInboxSources(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    ReplyProcessingRequests incoming, Notifier notifier, TimeProvider time)
{
    internal const string Prefix = "email.inbox.";
    internal const string LegacyKey = "email.lastUid";
    internal const string HhRefreshKey = Prefix + "hh-refresh";
    private static readonly string[] HhDomains = ["hh.ru", "rabota.by", "headhunter.ru", "hh.kz", "career.habr.com", "habr.com"];
    private static readonly HashSet<string> PublicDomains =
        ["gmail.com", "yandex.ru", "yandex.by", "ya.ru", "mail.ru", "bk.ru", "inbox.ru", "list.ru", "outlook.com", "hotmail.com", "icloud.com", "tut.by", "proton.me"];

    public static bool OwnsKey(string key) => key == LegacyKey || key.StartsWith(Prefix, StringComparison.Ordinal);

    public async Task<EmailInboxSnapshot> CaptureAsync(string accountId, CancellationToken ct)
    {
        if (!IsHash(accountId)) throw new ArgumentException("invalid mailbox account", nameof(accountId));
        await using var context = await db.CreateDbContextAsync(ct);
        var snapshot = new EmailInboxSnapshot
        {
            AccountId = accountId, CapturedAt = Now(),
            StateJson = await context.Settings.Where(row => row.Key == Key(accountId)).Select(row => row.Value).SingleOrDefaultAsync(ct),
            LegacyJson = await context.Settings.Where(row => row.Key == LegacyKey).Select(row => row.Value).SingleOrDefaultAsync(ct),
        };
        Position(snapshot);
        await CheckReceiptAsync(context, snapshot.StateJson, snapshot.AccountId, ct);
        return snapshot;
    }

    public static InboxPosition Position(EmailInboxSnapshot snapshot)
    {
        if (snapshot.Version != 1 || !IsHash(snapshot.AccountId) || snapshot.CapturedAt == default
            || snapshot.LegacyJson is { } legacy && !uint.TryParse(legacy, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("unsupported or damaged mailbox capture");
        return ReadState(snapshot.StateJson, snapshot.AccountId).Position;
    }

    public async Task<EmailInboxPublication> PublishAsync(EmailInboxSnapshot snapshot, InboxBatch batch, string selfAddress,
        bool hasHhSession, CancellationToken ct)
    {
        var position = Position(snapshot);
        if (batch.AccountId != snapshot.AccountId || batch.UidValidity == 0 || batch.AfterUid != position.AfterUidFor(batch.AccountId, batch.UidValidity)
            || batch.ReplayAll != position.ReplayAllFor(batch.AccountId, batch.UidValidity)
            || batch.HasMore && batch.Messages.Count == 0
            || batch.Messages.Count > 100 || batch.Messages.Any(message => message.Uid == 0 || message.Uid <= batch.AfterUid
                || string.IsNullOrWhiteSpace(message.MessageId) || message.Text is null || message.References is null || message.Date == default)
            || batch.Messages.Select(message => message.Uid).Distinct().Count() != batch.Messages.Count)
            throw new ArgumentException("invalid mailbox batch or position", nameof(batch));
        using var stage = Telemetry.StartStage("admit-inbox", "email").Tag("message.count", batch.Messages.Count)
            .Tag("mailbox.uidvalidity", batch.UidValidity).Tag("mailbox.after_uid", batch.AfterUid);
        var batchHash = CanonicalJson.Hash(JsonSerializer.Serialize(new { snapshot, batch, selfAddress, hasHhSession }));
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Key(snapshot.AccountId)}, 0))", ct);
        var row = await context.Settings.SingleOrDefaultAsync(setting => setting.Key == Key(snapshot.AccountId), ct);
        var current = ReadState(row?.Value, snapshot.AccountId);
        await CheckReceiptAsync(context, row?.Value, snapshot.AccountId, ct);
        if (current.BatchHash == batchHash) return new(true, 0, 0, await NeedsHhRefreshAsync(context, ct));
        if (row?.Value != snapshot.StateJson || await context.Settings.Where(setting => setting.Key == LegacyKey)
                .Select(setting => setting.Value).SingleOrDefaultAsync(ct) != snapshot.LegacyJson) return new(false, 0, 0, false);
        if (batch.Messages.Count == 0 && current.Position.UidValidity == batch.UidValidity && !current.Position.ReplayAll)
            return new(true, 0, 0, await NeedsHhRefreshAsync(context, ct));
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var observations = new List<EmailInboxObservation>();
        var matched = 0;
        var refresh = false;
        foreach (var message in batch.Messages.OrderBy(message => message.Uid))
        {
            var observed = new EmailInboxObservation { Uid = message.Uid, MessageId = message.MessageId,
                ContentHash = CanonicalJson.Hash(JsonSerializer.Serialize(message)), Disposition = "self" };
            observations.Add(observed);
            if (string.Equals(message.FromAddress, selfAddress, StringComparison.OrdinalIgnoreCase)) continue;
            var match = await MatchAsync(context, message, snapshot.CapturedAt, ct);
            refresh |= match.RefreshHh;
            observed.Disposition = match.ApplicationId is null ? "unmatched" : "hh-pointer";
            if (match.ApplicationId is not { } applicationId) continue;
            matched++;
            if (match.Channel == EventChannel.HeadHunter && hasHhSession) continue;
            var ticket = await incoming.IncomingJoinedAsync(context, outbox, new()
            {
                ApplicationId = applicationId, Channel = match.Channel, Author = message.FromAddress,
                Text = TextUtil.Truncate(message.Text, 6000), ExternalId = message.MessageId, At = message.Date, Subject = message.Subject,
                YourChat = batch.ReplayAll,
            }, ct);
            observed.Disposition = ticket.AlreadyPublished ? "existing" : "incoming";
            observed.RequestId = ticket.RequestId == Guid.Empty ? null : ticket.RequestId;
        }
        if (refresh) await RequestHhRefreshJoinedAsync(context, snapshot.CapturedAt, ct);
        var next = new EmailInboxState
        {
            Revision = current.Revision + 1, BatchHash = batchHash, LegacyJson = current.LegacyJson ?? snapshot.LegacyJson,
            Position = new() { AccountId = snapshot.AccountId, UidValidity = batch.UidValidity, ReplayAll = batch.ReplayAll && batch.HasMore,
                LastUid = batch.Messages.Count == 0 ? batch.AfterUid : batch.Messages.Max(message => message.Uid) },
        };
        var json = JsonSerializer.Serialize(next, EmailInboxJsonContext.Default.EmailInboxState);
        context.Settings.Add(new() { Key = HistoryKey(snapshot.AccountId, next.Revision),
            Value = JsonSerializer.Serialize(new EmailInboxReceipt { BeforeJson = row?.Value, AfterJson = json, CapturedAt = snapshot.CapturedAt,
                Observations = observations }, EmailInboxJsonContext.Default.EmailInboxReceipt), UpdatedAt = Now() });
        if (row is null) context.Settings.Add(row = new() { Key = Key(snapshot.AccountId), Value = "" });
        row.Value = json;
        row.UpdatedAt = Now();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new(true, batch.Messages.Count, matched, await NeedsHhRefreshAsync(context, ct));
    }

    internal static async Task AcknowledgeHhRefreshJoinedAsync(JobFinderDbContext context, DateTimeOffset observedAt, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("hh observation requires the source transaction");
        await LockHhAsync(context, ct);
        var row = await context.Settings.SingleOrDefaultAsync(setting => setting.Key == HhRefreshKey, ct);
        if (row is null) return;
        var refresh = ReadRefresh(row.Value);
        if (observedAt < refresh.RequestedAt || refresh.ObservedAt >= observedAt) return;
        refresh.ObservedAt = observedAt;
        row.Value = JsonSerializer.Serialize(refresh, EmailInboxJsonContext.Default.EmailHhRefresh);
        row.UpdatedAt = observedAt;
    }

    private static async Task RequestHhRefreshJoinedAsync(JobFinderDbContext context, DateTimeOffset requestedAt, CancellationToken ct)
    {
        await LockHhAsync(context, ct);
        var row = await context.Settings.SingleOrDefaultAsync(setting => setting.Key == HhRefreshKey, ct);
        var refresh = row is null ? new EmailHhRefresh() : ReadRefresh(row.Value);
        if (requestedAt <= refresh.RequestedAt) return;
        if (row is null) context.Settings.Add(row = new() { Key = HhRefreshKey, Value = "" });
        refresh.RequestedAt = requestedAt;
        row.Value = JsonSerializer.Serialize(refresh, EmailInboxJsonContext.Default.EmailHhRefresh);
        row.UpdatedAt = requestedAt;
    }

    private static async Task<bool> NeedsHhRefreshAsync(JobFinderDbContext context, CancellationToken ct)
    {
        var json = await context.Settings.Where(row => row.Key == HhRefreshKey).Select(row => row.Value).SingleOrDefaultAsync(ct);
        if (json is null) return false;
        var refresh = ReadRefresh(json);
        return refresh.ObservedAt is null || refresh.ObservedAt < refresh.RequestedAt;
    }

    private static EmailHhRefresh ReadRefresh(string json)
    {
        var value = JsonSerializer.Deserialize(json, EmailInboxJsonContext.Default.EmailHhRefresh);
        if (value is null || value.Version != 1 || value.RequestedAt == default) throw new InvalidOperationException("damaged hh mail refresh hint");
        return value;
    }

    private static Task LockHhAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({HhRefreshKey}, 0))", ct);

    private static EmailInboxState ReadState(string? json, string accountId)
    {
        if (json is null) return new() { Position = new() { AccountId = accountId } };
        var state = JsonSerializer.Deserialize(json, EmailInboxJsonContext.Default.EmailInboxState);
        if (state is null || state.Version != 1 || state.Revision <= 0 || state.Position is null || state.Position.AccountId != accountId
            || state.Position.UidValidity == 0 || !IsHash(state.BatchHash)) throw new InvalidOperationException("unsupported or damaged mailbox cursor");
        return state;
    }

    private static async Task CheckReceiptAsync(JobFinderDbContext context, string? json, string accountId, CancellationToken ct)
    {
        if (json is null) return;
        var state = ReadState(json, accountId);
        var key = HistoryKey(accountId, state.Revision);
        var receiptJson = await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(ct);
        var receipt = receiptJson is null ? null : JsonSerializer.Deserialize(receiptJson, EmailInboxJsonContext.Default.EmailInboxReceipt);
        if (receipt is null || receipt.Version != 1 || receipt.AfterJson != json || receipt.CapturedAt == default || receipt.Observations is null
            || receipt.Observations.Any(row => row.Uid == 0 || string.IsNullOrWhiteSpace(row.MessageId) || !IsHash(row.ContentHash))
            || receipt.Observations.Count > 0 && receipt.Observations.Max(row => row.Uid) != state.Position.LastUid
            || ReadState(receipt.BeforeJson, accountId).Revision != state.Revision - 1)
            throw new InvalidOperationException("mailbox cursor does not match its retained receipt");
    }

    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
    private static string Key(string accountId) => Prefix + accountId;
    private static string HistoryKey(string accountId, long revision) => Prefix + "history." + accountId + "." + revision.ToString(CultureInfo.InvariantCulture);
    private DateTimeOffset Now() => new(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);

    private static async Task<(long? ApplicationId, EventChannel Channel, bool RefreshHh)> MatchAsync(JobFinderDbContext context,
        InboxMessage message, DateTimeOffset capturedAt, CancellationToken ct)
    {
        var ids = message.References.Append(message.InReplyTo).OfType<string>().Where(value => value.Length > 0).Distinct().ToList();
        if (ids.Count > 0)
        {
            var application = await context.Applications.Where(row => row.EmailMessageId != null && ids.Contains(row.EmailMessageId))
                .OrderByDescending(row => row.Id).Select(row => (long?)row.Id).FirstOrDefaultAsync(ct)
                ?? await context.ApplicationEvents.Where(row => row.Channel == EventChannel.Email && row.ExternalId != null && ids.Contains(row.ExternalId))
                    .OrderByDescending(row => row.Id).Select(row => (long?)row.ApplicationId).FirstOrDefaultAsync(ct);
            if (application is not null) return (application, EventChannel.Email, false);
        }
        var from = message.FromAddress.Trim().ToLowerInvariant();
        var sender = await context.Applications.Where(row => row.Status == ApplicationStatus.Sent && row.Channel == ApplicationChannel.Email
            && row.Target != null && row.Target.ToLower() == from).OrderByDescending(row => row.SentAt).ThenByDescending(row => row.Id)
            .Select(row => (long?)row.Id).FirstOrDefaultAsync(ct);
        if (sender is not null) return (sender, EventChannel.Email, false);
        var outreach = await context.Outreaches.Where(row => row.Status == OutreachStatus.Sent && row.Contact!.Kind == ContactKind.Email && row.Contact.Value == from)
            .OrderByDescending(row => row.SentAt).Select(row => (long?)row.VacancyId).FirstOrDefaultAsync(ct);
        if (outreach is not null)
        {
            var application = await context.Applications.Where(row => row.VacancyId == outreach && row.Status == ApplicationStatus.Sent)
                .OrderByDescending(row => row.SentAt).ThenByDescending(row => row.Id).Select(row => (long?)row.Id).FirstOrDefaultAsync(ct);
            if (application is not null) return (application, EventChannel.Email, false);
        }
        var domain = from.Contains('@') ? from[(from.IndexOf('@') + 1)..] : "";
        if (HhDomains.Any(host => domain == host || domain.EndsWith("." + host, StringComparison.Ordinal)))
        {
            var text = TextUtil.Normalize($"{message.Subject} {message.Text}");
            var candidates = await context.Applications.Include(row => row.Vacancy).Where(row => row.Status == ApplicationStatus.Sent
                && row.Channel == ApplicationChannel.HeadHunter && row.SentAt >= capturedAt.AddDays(-60)).ToListAsync(ct);
            var hit = candidates.Where(row => text.Contains(TextUtil.Normalize(row.Vacancy!.Title), StringComparison.Ordinal))
                .OrderByDescending(row => row.Vacancy!.CompanyName is { } company && text.Contains(TextUtil.Normalize(company), StringComparison.Ordinal))
                .ThenByDescending(row => row.SentAt).ThenByDescending(row => row.Id).FirstOrDefault();
            return (hit?.Id, EventChannel.HeadHunter, true);
        }
        if (domain.Length > 0 && !PublicDomains.Contains(domain))
        {
            var candidates = await context.Applications.Include(row => row.Vacancy).ThenInclude(row => row!.Company)
                .Where(row => row.Status == ApplicationStatus.Sent && row.SentAt >= capturedAt.AddDays(-60) && row.Vacancy!.Company != null
                    && row.Vacancy.Company.Site != null).OrderByDescending(row => row.SentAt).ThenByDescending(row => row.Id).ToListAsync(ct);
            var hit = candidates.FirstOrDefault(row => CorporateDomain(row.Vacancy!.Company!.Site!, domain));
            if (hit is not null) return (hit.Id, EventChannel.Email, false);
        }
        return (null, EventChannel.Email, false);
    }

    internal static bool CorporateDomain(string site, string domain)
    {
        if (!Uri.TryCreate(site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(domain)) return false;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal) || domain.EndsWith("." + host, StringComparison.Ordinal);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EmailInboxState))]
[JsonSerializable(typeof(EmailInboxReceipt))]
[JsonSerializable(typeof(EmailHhRefresh))]
public sealed partial class EmailInboxJsonContext : JsonSerializerContext;
