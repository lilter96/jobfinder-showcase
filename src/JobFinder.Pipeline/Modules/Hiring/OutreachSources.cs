using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record OutreachDraftSnapshot
{
    public long VacancyId { get; set; }
    public long ContactId { get; set; }
    public bool Automatic { get; set; }
    public string CompanyKey { get; set; } = "";
    public string ContextHash { get; set; } = "";
    public string ContactHash { get; set; } = "";
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public string StoriesHash { get; set; } = "";
    public string PriorOutreachHash { get; set; } = "";
}

public sealed record CapturedOutreachDraft(OutreachDraftSnapshot Snapshot, Vacancy Vacancy, CompanyContact Contact, CandidateProfile Profile, CandidateFacts Facts);

public sealed record OutreachEditSnapshot
{
    public long Id { get; set; }
    public long VacancyId { get; set; }
    public long ContactId { get; set; }
    public string SourceHash { get; set; } = "";

    public int Version => ReplyService.DraftVersion(SourceHash);

    public static OutreachEditSnapshot Capture(Outreach source, CompanyContact contact) => new()
    {
        Id = source.Id, VacancyId = source.VacancyId, ContactId = source.ContactId,
        SourceHash = CanonicalJson.Hash(JsonSerializer.Serialize(new
        {
            source.Id, source.VacancyId, source.ContactId, source.Revision, source.Status, source.Text, source.Fresh,
            Contact = OutreachSources.ContactHash(contact),
        })),
    };

    public bool Matches(Outreach source, CompanyContact contact) => this == Capture(source, contact);
}

public sealed class OutreachSources(IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time)
{
    public async Task<OutreachEditSnapshot?> CaptureEditAsync(long sourceId, int version, CancellationToken ct)
    {
        if (version <= 0) return null;
        await using var context = await db.CreateDbContextAsync(ct);
        var source = await context.Outreaches.AsNoTracking().Include(row => row.Contact).SingleOrDefaultAsync(row => row.Id == sourceId, ct);
        if (source is not { Status: OutreachStatus.Draft, Contact: not null }) return null;
        var captured = OutreachEditSnapshot.Capture(source, source.Contact);
        return captured.Version == version ? captured : null;
    }

    public async Task<ActionResult> DecideAsync(OutreachEditSnapshot snapshot, bool manualSent, string? text, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await DeliveryLedger.LockVacancyAsync(context, snapshot.VacancyId, ct);
        var source = await context.Outreaches.FromSqlInterpolated($"SELECT * FROM outreaches WHERE id = {snapshot.Id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        var contact = await ContactAsync(context, snapshot.ContactId, ct);
        if (source is not { Status: OutreachStatus.Draft } || contact is null || !snapshot.Matches(source, contact)
            || manualSent && contact.RetiredAt is not null
            || manualSent && contact.Kind != ContactKind.LinkedIn
            || await EmployerMessageSources.UnresolvedDeliveryAsync(context, snapshot.VacancyId, ct))
            return ActionResult.Fail("черновик или контакт изменились, либо отправка требует сверки; действие не применено");
        var pending = await context.EmployerMessages.Where(row => row.VacancyId == snapshot.VacancyId
            && (row.Status == EmployerMessageStatus.Pending || row.Status == EmployerMessageStatus.Ready)).ToListAsync(ct);
        var cancel = new List<EmployerMessage>();
        try
        {
            foreach (var message in pending)
                if (EmployerMessageSources.Read(message).OutreachId == snapshot.Id) cancel.Add(message);
        }
        catch (Exception error) when (error is JsonException or UnsupportedEmployerMessageException)
        {
            return ActionResult.Fail("переданная на отправку операция требует проверки; действие не применено");
        }
        if (manualSent && string.IsNullOrWhiteSpace(text ?? source.Text)) return ActionResult.Fail("пустой текст");
        var now = time.GetUtcNow();
        foreach (var message in cancel)
        {
            message.Status = EmployerMessageStatus.Cancelled;
            message.CompletedAt = now;
            message.Error = "outreach closed by a captured source decision";
        }
        context.Entry(source).State = EntityState.Unchanged;
        source.Status = manualSent ? OutreachStatus.Sent : OutreachStatus.Dismissed;
        source.Revision++;
        if (manualSent)
        {
            source.Text = TextUtil.Truncate(HumanText.Clean((text ?? source.Text).Trim()), 300);
            source.SentAt = now;
        }
        notifier.Add(context, NotificationKind.Info,
            $"Аутрич #{source.Id}: " + (manualSent ? "отмечено как отправленное вручную; автоматическая доставка не подтверждается" : "пропущен"),
            null, source.VacancyId);
        await notifier.SaveAndFlushAsync(context, ct);
        return ActionResult.Done(manualSent ? "отмечено как отправленное вручную" : "пропущено");
    }

    public async Task<CapturedOutreachDraft?> CaptureAsync(long vacancyId, long contactId, bool automatic, CancellationToken ct)
    {
        using var span = Telemetry.StartStage("capture", "outreach").Tag("vacancy.id", vacancyId).Tag("contact.id", contactId);
        await using var context = await db.CreateDbContextAsync(ct);
        var company = await context.Vacancies.Where(row => row.Id == vacancyId).Select(row => row.CompanyName).SingleOrDefaultAsync(ct);
        var key = VacancyFingerprint.NormalizeCompany(company);
        if (key.Length == 0) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, key, vacancyId, ct);
        var vacancy = await EmployerMessageSources.VacancyAsync(context, vacancyId, ct);
        var contact = await ContactAsync(context, contactId, ct);
        if (!Eligible(vacancy, contact, key, automatic) || await EmployerMessageSources.DeliveryPendingAsync(context, vacancyId, ct)
            || automatic && await LimitedAsync(context, vacancyId, key, ct)) return null;
        var documents = await context.Documents.AsNoTracking().Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key).ToListAsync(ct);
        var profile = documents.SingleOrDefault(row => row.Key == ProfileProvider.Key);
        if (profile is null) return null;
        var snapshot = new OutreachDraftSnapshot
        {
            VacancyId = vacancyId, ContactId = contactId, Automatic = automatic, CompanyKey = key,
            ContextHash = ContextHash(vacancy!), ContactHash = ContactHash(contact!), ProfileVersion = profile.Version,
            FactsVersion = documents.SingleOrDefault(row => row.Key == FactsProvider.Key)?.Version ?? 0,
            StoriesHash = await StoriesHashAsync(context, ct), PriorOutreachHash = await PriorHashAsync(context, key, vacancyId, ct),
        };
        await transaction.CommitAsync(ct);
        var facts = documents.SingleOrDefault(row => row.Key == FactsProvider.Key) is { } document
            ? JsonSerializer.Deserialize(document.Content, FactsJsonContext.Default.CandidateFacts) ?? throw new JsonException("empty captured facts")
            : new CandidateFacts();
        return new CapturedOutreachDraft(snapshot, vacancy!, contact!, ProfileProvider.FromJson(profile.Content), facts.WithStandardQuestions());
    }

    public async Task<Outreach?> PublishAsync(OutreachDraftSnapshot snapshot, string text, bool? fresh, RecruiterDraft? draft, CancellationToken ct,
        LetterDraft? reviewed = null)
    {
        if (string.IsNullOrWhiteSpace(text) || draft is not null && (draft.Text != text || draft.Fresh != fresh)
            || snapshot.Automatic && draft is { Problems.Count: > 0 }) return null;
        using var span = Telemetry.StartStage("publish", "outreach").Tag("vacancy.id", snapshot.VacancyId).Tag("contact.id", snapshot.ContactId);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, snapshot.CompanyKey, snapshot.VacancyId, ct);
        var vacancy = await EmployerMessageSources.VacancyAsync(context, snapshot.VacancyId, ct);
        var contact = await ContactAsync(context, snapshot.ContactId, ct);
        if (contact is null) return null;
        if (contact.Kind == ContactKind.LinkedIn && (text.Length > LetterLayout.LinkedInLimit
            || snapshot.Automatic && (reviewed is not { Problems.Count: 0, Review: { } review } || reviewed.Text != text
                || string.IsNullOrWhiteSpace(review.ContextHash) || !LetterWriter.Matches(review, text, review.ContextHash)))) return null;
        if (!Eligible(vacancy, contact, snapshot.CompanyKey, snapshot.Automatic)
            || ContextHash(vacancy!) != snapshot.ContextHash || ContactHash(contact!) != snapshot.ContactHash
            || await EmployerMessageSources.DeliveryPendingAsync(context, snapshot.VacancyId, ct)
            || snapshot.Automatic && await LimitedAsync(context, snapshot.VacancyId, snapshot.CompanyKey, ct)) return null;
        var documents = await context.Documents.AsNoTracking().Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key).ToListAsync(ct);
        if ((documents.SingleOrDefault(row => row.Key == ProfileProvider.Key)?.Version ?? 0) != snapshot.ProfileVersion
            || (documents.SingleOrDefault(row => row.Key == FactsProvider.Key)?.Version ?? 0) != snapshot.FactsVersion
            || await StoriesHashAsync(context, ct) != snapshot.StoriesHash
            || await PriorHashAsync(context, snapshot.CompanyKey, snapshot.VacancyId, ct) != snapshot.PriorOutreachHash) return null;
        var source = new Outreach
        {
            VacancyId = snapshot.VacancyId, ContactId = snapshot.ContactId, Fresh = fresh, Status = OutreachStatus.Draft,
            Text = text,
            CreatedAt = time.GetUtcNow(),
        };
        context.Outreaches.Add(source);
        await context.SaveChangesAsync(ct);
        var (card, buttons) = CareerCards.Outreach(source, contact, vacancy!, draft);
        notifier.Add(context, NotificationKind.Info, card, buttons, snapshot.VacancyId);
        await notifier.SaveAndFlushAsync(context, ct);
        return source;
    }

    private static async Task LockAsync(JobFinderDbContext context, string key, long vacancyId, CancellationToken ct)
    {
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"hiring.outreach.company." + key}, 0))", ct);
        await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
    }

    private static Task<CompanyContact?> ContactAsync(JobFinderDbContext context, long contactId, CancellationToken ct) =>
        context.CompanyContacts.FromSqlInterpolated($"SELECT * FROM company_contacts WHERE id = {contactId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);

    private static bool Eligible(Vacancy? vacancy, CompanyContact? contact, string key, bool automatic) =>
        vacancy is not null && vacancy.Status != VacancyStatus.Dismissed && contact is not null
        && contact.RetiredAt is null
        && VacancyFingerprint.NormalizeCompany(vacancy.CompanyName) == key && contact.CompanyKey == key
        && contact.Kind is ContactKind.Telegram or ContactKind.Email or ContactKind.LinkedIn
        && (!automatic || !vacancy.Applications.Any(row => row.Target is not null
            && NormalizeTarget(row.Target) == NormalizeTarget(contact.Value)));

    private static string NormalizeTarget(string value) => value.Trim().TrimStart('@').ToLowerInvariant();

    private Task<bool> LimitedAsync(JobFinderDbContext context, long vacancyId, string key, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-7);
        return context.Outreaches.AnyAsync(row => row.VacancyId == vacancyId || row.Contact!.CompanyKey == key && row.CreatedAt >= since, ct);
    }

    private static async Task<string> PriorHashAsync(JobFinderDbContext context, string key, long vacancyId, CancellationToken ct)
    {
        var sources = await context.Outreaches.AsNoTracking().Where(row => row.VacancyId == vacancyId || row.Contact!.CompanyKey == key)
            .OrderBy(row => row.Id).Select(row => new { row.Id, row.VacancyId, row.ContactId, row.Revision, row.Status, row.Text, row.Fresh, row.CreatedAt, row.SentAt }).ToListAsync(ct);
        return CanonicalJson.Hash(JsonSerializer.Serialize(sources));
    }

    internal static async Task<string> StoriesHashAsync(JobFinderDbContext context, CancellationToken ct) =>
        DeliveryLedger.Hash(ResumeTailor.StoriesText(await context.Stories.AsNoTracking().Where(row => row.Confirmed).OrderBy(row => row.Id).ToListAsync(ct)));

    internal static string ContactHash(CompanyContact contact) => CanonicalJson.Hash(JsonSerializer.Serialize(new
    {
        contact.Id, contact.CompanyId, contact.CompanyKey, contact.CompanyName, contact.Kind, contact.Value,
        contact.PersonName, contact.Role, contact.Source, contact.SourceRef,
    }));

    internal static string ContextHash(Vacancy vacancy) => CanonicalJson.Hash(JsonSerializer.Serialize(new
    {
        Context = EmployerMessageSources.Context(vacancy),
        VacancyText = JobFinderLlm.VacancyText(vacancy),
        Applications = vacancy.Applications.OrderBy(row => row.Id).Select(row => new
        {
            row.Id, row.CreatedAt, row.SentAt, row.LastInboundAt, row.StageChangedAt,
            Events = row.Events.OrderBy(ev => ev.Id).Select(ev => new { ev.Id, ev.At, ev.ExternalId, ev.Classification }),
        }),
    }));
}
