using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record TelegramHistorySnapshot
{
    public int Version { get; set; } = 1;
    public long VacancyId { get; set; }
    public long? ApplicationId { get; set; }
    public long? TargetPeerId { get; set; }
    public string Handle { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset Since { get; set; }
    public int? ExceptMessageId { get; set; }
    public bool CreateApplication { get; set; }
}

public sealed class TelegramChatHistorySources(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    Notifier notifier, TimeProvider time)
{
    public async Task<TelegramHistorySnapshot?> CaptureAsync(long vacancyId, string handle, int? exceptMessageId,
        bool createApplication, CancellationToken ct)
    {
        handle = NormalizeHandle(handle);
        if (vacancyId <= 0 || exceptMessageId is <= 0) throw new ArgumentException("invalid Telegram history source");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, vacancyId, handle, ct);
        var vacancy = await ReadAsync(context, vacancyId, ct);
        if (vacancy is null || !await EligibleAsync(context, vacancy, handle, ct)) return null;
        var peers = vacancy.Applications.Where(row => SameHandle(row.Target, handle) && row.TargetPeerId != null)
            .Select(row => row.TargetPeerId!.Value).Distinct().ToArray();
        if (peers.Length > 1) return null;
        var application = Latest(vacancy);
        if (application is null && !createApplication) return null;
        var snapshot = new TelegramHistorySnapshot
        {
            VacancyId = vacancyId, ApplicationId = application?.Id, Handle = handle,
            TargetPeerId = peers.Length == 1 ? peers[0] : null,
            Since = (application?.CreatedAt ?? vacancy.OriginalPostedAt ?? vacancy.FirstSeenAt).AddDays(-14),
            CapturedAt = time.GetUtcNow(), ExceptMessageId = exceptMessageId, CreateApplication = createApplication,
        };
        snapshot.SourceHash = WitnessHash(await SourceHashAsync(context, vacancy, handle, ct), snapshot);
        return snapshot;
    }

    public async Task<int> PublishAsync(TelegramHistorySnapshot snapshot, TgDirectHistory history, CancellationToken ct)
    {
        Validate(snapshot, history);
        using var stage = Telemetry.StartStage("publish-history", "telegram-chat").Tag("vacancy.id", snapshot.VacancyId)
            .Tag("message.count", history.Messages.Count);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, snapshot.VacancyId, snapshot.Handle, ct);
        var vacancy = await ReadAsync(context, snapshot.VacancyId, ct);
        if (vacancy is null || !await EligibleAsync(context, vacancy, snapshot.Handle, ct)
            || WitnessHash(await SourceHashAsync(context, vacancy, snapshot.Handle, ct), snapshot) != snapshot.SourceHash) return 0;
        var application = Latest(vacancy);
        if (application?.Id != snapshot.ApplicationId || snapshot.TargetPeerId is { } peerId && peerId != history.UserId) return 0;
        var expectedSince = (application?.CreatedAt ?? vacancy.OriginalPostedAt ?? vacancy.FirstSeenAt).AddDays(-14);
        if (expectedSince != snapshot.Since || application is null && !snapshot.CreateApplication) return 0;
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"hiring.telegram-history." + history.UserId}, 0))", ct);
        var candidates = history.Messages.Where(message => message.Id != snapshot.ExceptMessageId && message.Date >= snapshot.Since)
            .DistinctBy(message => message.Id).OrderBy(message => message.Date).ThenBy(message => message.Id).ToArray();
        var ids = candidates.Select(message => RecruiterChatService.ExternalId(history.UserId, message.Id)).ToArray();
        var stored = (await context.ApplicationEvents.Where(row => row.Channel == EventChannel.Telegram && ids.Contains(row.ExternalId!))
            .Select(row => row.ExternalId!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var fresh = candidates.Where(message => !stored.Contains(RecruiterChatService.ExternalId(history.UserId, message.Id))).ToArray();
        if (fresh.Length == 0) return 0;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        if (application is null)
        {
            var first = fresh[0].Date;
            application = new()
            {
                VacancyId = snapshot.VacancyId, Channel = ApplicationChannel.TelegramDm, Mode = ApplicationMode.External,
                Status = ApplicationStatus.Sent, Target = snapshot.Handle, TargetPeerId = history.UserId,
                CreatedAt = first, SentAt = fresh.FirstOrDefault(message => message.Mine)?.Date ?? first,
                Stage = fresh.Any(message => !message.Mine) ? ApplicationStage.Replied : ApplicationStage.Sent, StageChangedAt = first,
            };
            context.Applications.Add(application);
            vacancy.Status = VacancyStatus.Applied;
            vacancy.UpdatedAt = time.GetUtcNow();
        }
        var (added, claimed) = Merge(application.Events, history.UserId, snapshot.Handle, fresh);
        foreach (var (entry, externalId) in claimed)
        {
            entry.ExternalId = externalId;
            entry.Revision++;
        }
        foreach (var entry in added)
        {
            entry.Application = application;
            context.ApplicationEvents.Add(entry);
        }
        if (fresh.Where(message => !message.Mine).MaxBy(message => message.Date) is { } inbound
            && (application.LastInboundAt is null || inbound.Date > application.LastInboundAt)) application.LastInboundAt = inbound.Date;
        if (fresh.Where(message => message.Mine).MaxBy(message => message.Date) is { } outbound
            && (application.LastOutboundAt is null || outbound.Date > application.LastOutboundAt)) application.LastOutboundAt = outbound.Date;
        application.Revision++;
        notifier.Add(context, NotificationKind.Info,
            $"📥 История Telegram для вакансии #{snapshot.VacancyId}: новых сообщений {added.Count}, сопоставлено {claimed.Count}. Только история, без отправки и подтверждения доставки резюме.",
            entityId: snapshot.VacancyId);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return added.Count;
    }

    public static (List<ApplicationEvent> Added, List<(ApplicationEvent Event, string ExternalId)> Claimed) Merge(
        IEnumerable<ApplicationEvent> existing, long userId, string handle, IEnumerable<TgDirectMessage> messages)
    {
        var entries = existing.ToArray();
        var ids = entries.Where(entry => entry.ExternalId is not null).Select(entry => entry.ExternalId!).ToHashSet(StringComparer.Ordinal);
        var unclaimed = entries.Where(entry => entry is { Kind: EventKind.MessageOut, Channel: EventChannel.Telegram, ExternalId: null }).ToList();
        var added = new List<ApplicationEvent>();
        var claimed = new List<(ApplicationEvent, string)>();
        foreach (var message in messages.OrderBy(message => message.Id))
        {
            var externalId = RecruiterChatService.ExternalId(userId, message.Id);
            if (!ids.Add(externalId)) continue;
            var body = Body(message.Text);
            var matches = message.Mine && body.Length > 0
                ? unclaimed.Where(entry => Math.Abs((entry.At - message.Date).TotalHours) < 24 && Body(entry.Text) == body).ToArray()
                : [];
            if (matches.Length == 1)
            {
                claimed.Add((matches[0], externalId));
                unclaimed.Remove(matches[0]);
                continue;
            }
            added.Add(new()
            {
                At = message.Date, Kind = message.Mine ? EventKind.MessageOut : EventKind.MessageIn, Channel = EventChannel.Telegram,
                Author = message.Mine ? "я" : "@" + handle.TrimStart('@').ToLowerInvariant(), Text = message.Text, ExternalId = externalId,
            });
        }
        return (added, claimed);
    }

    private static string Body(string? text) => TextUtil.Normalize(string.Join(' ', (text ?? "").Split('\n')
        .Where(line => !line.TrimStart().StartsWith("📎", StringComparison.Ordinal))));

    private static Application? Latest(Vacancy vacancy) => vacancy.Applications.Where(row => row.Status == ApplicationStatus.Sent)
        .OrderByDescending(row => row.SentAt ?? row.CreatedAt).ThenByDescending(row => row.Id).FirstOrDefault();

    private static Task<Vacancy?> ReadAsync(JobFinderDbContext context, long vacancyId, CancellationToken ct) => context.Vacancies
        .Include(row => row.Applications).ThenInclude(row => row.Events).SingleOrDefaultAsync(row => row.Id == vacancyId, ct);

    private static async Task LockAsync(JobFinderDbContext context, long vacancyId, string handle, CancellationToken ct)
    {
        var vacancy = await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM applications WHERE vacancy_id = {vacancyId} ORDER BY id FOR UPDATE", ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM outreaches WHERE vacancy_id = {vacancyId} ORDER BY id FOR SHARE", ct);
        if (vacancy is not null)
        {
            var key = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName);
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM company_contacts WHERE company_key = {key} AND kind = {ContactKind.Telegram.ToString()} AND lower(value) = {handle} ORDER BY id FOR SHARE", ct);
        }
    }

    private static async Task<bool> EligibleAsync(JobFinderDbContext context, Vacancy vacancy, string handle, CancellationToken ct)
    {
        if (vacancy.Status == VacancyStatus.Dismissed || vacancy.Applications.Any(row => row.Status == ApplicationStatus.Pending)
            || await EmployerMessageSources.UnresolvedDeliveryAsync(context, vacancy.Id, ct)
            || await context.ReplyProcessingRequests.AnyAsync(row => row.VacancyId == vacancy.Id && row.Mode == ReplyProcessingMode.Incoming
                && (row.Stage == ReplyProcessingStage.Capturing || row.Stage == ReplyProcessingStage.Fast
                    || row.Stage == ReplyProcessingStage.Deep || row.Stage == ReplyProcessingStage.Publishing), ct)) return false;
        var companyKey = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName);
        return vacancy.Applications.Any(row => row.Channel == ApplicationChannel.TelegramDm && SameHandle(row.Target, handle))
            || await context.CompanyContacts.AnyAsync(row => row.CompanyKey == companyKey && row.Kind == ContactKind.Telegram
                && row.RetiredAt == null && row.Value.ToLower() == handle, ct)
            || await context.Outreaches.AnyAsync(row => row.VacancyId == vacancy.Id && row.Status == OutreachStatus.Sent
                && row.Contact!.Kind == ContactKind.Telegram && row.Contact.Value.ToLower() == handle, ct);
    }

    private static async Task<string> SourceHashAsync(JobFinderDbContext context, Vacancy vacancy, string handle, CancellationToken ct)
    {
        var companyKey = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName);
        var contacts = await context.CompanyContacts.AsNoTracking().Where(row => row.CompanyKey == companyKey && row.Kind == ContactKind.Telegram
            && row.Value.ToLower() == handle).OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.Revision, row.CompanyKey, row.Value, row.RetiredAt }).ToArrayAsync(ct);
        var outreach = await context.Outreaches.AsNoTracking().Where(row => row.VacancyId == vacancy.Id && row.Contact!.Kind == ContactKind.Telegram
            && row.Contact.Value.ToLower() == handle).OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.Revision, row.Status, row.SentAt, row.ContactId, ContactRevision = row.Contact!.Revision, row.Contact.Value }).ToArrayAsync(ct);
        return CanonicalJson.Hash(JsonSerializer.Serialize(new
        {
            vacancy.Id, vacancy.CompanyName, vacancy.Title, vacancy.Description, vacancy.Url, vacancy.Status, vacancy.UpdatedAt,
            vacancy.OriginalPostedAt, vacancy.FirstSeenAt, Contacts = contacts, Outreach = outreach,
            Applications = vacancy.Applications.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.Revision, row.Status, row.Channel, row.Mode, row.Target, row.TargetPeerId, row.CreatedAt, row.SentAt,
                row.Stage, row.StageChangedAt, row.LastInboundAt, row.LastOutboundAt, row.DeliveryRequestId,
                Events = row.Events.OrderBy(entry => entry.Id).Select(entry => new
                {
                    entry.Id, entry.Revision, entry.At, entry.Kind, entry.Channel, entry.Author, entry.Text, entry.ExternalId,
                    entry.Draft, entry.DraftStatus, entry.Classification,
                }),
            }),
        }));
    }

    private static bool SameHandle(string? value, string handle) => value is not null
        && "@" + value.Trim().TrimStart('@').ToLowerInvariant() == handle;

    private static string WitnessHash(string sourceHash, TelegramHistorySnapshot snapshot) => CanonicalJson.Hash(JsonSerializer.Serialize(new
    {
        SourceHash = sourceHash, snapshot.Version, snapshot.VacancyId, snapshot.ApplicationId, snapshot.TargetPeerId,
        snapshot.Handle, snapshot.CapturedAt, snapshot.Since, snapshot.ExceptMessageId, snapshot.CreateApplication,
    }));

    private static string NormalizeHandle(string handle)
    {
        var name = handle.Trim().TrimStart('@');
        if (name.Length is < 5 or > 32 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("invalid Telegram history handle", nameof(handle));
        return "@" + name.ToLowerInvariant();
    }

    private static void Validate(TelegramHistorySnapshot snapshot, TgDirectHistory history)
    {
        if (snapshot.Version != 1 || snapshot.VacancyId <= 0 || snapshot.ApplicationId is <= 0 || snapshot.TargetPeerId is <= 0
            || snapshot.CapturedAt == default || snapshot.Since == default || snapshot.ExceptMessageId is <= 0
            || snapshot.Handle != NormalizeHandle(snapshot.Handle) || snapshot.SourceHash.Length != 64
            || history.UserId <= 0 || history.Messages.Any(message => message.Id <= 0 || message.Date == default || string.IsNullOrWhiteSpace(message.Text))
            || history.Messages.GroupBy(message => message.Id).Any(group => group.Distinct().Count() != 1))
            throw new ArgumentException("invalid Telegram history witness or messages");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TelegramHistorySnapshot))]
public sealed partial class TelegramHistoryJsonContext : JsonSerializerContext;
