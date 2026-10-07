using System.Text.Json;
using JobFinder.Core.Contacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record ContactEditSnapshot
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public string SourceHash { get; set; } = "";

    public static ContactEditSnapshot Capture(CompanyContact source) => new()
    {
        Id = source.Id, Revision = source.Revision,
        SourceHash = CanonicalJson.Hash(JsonSerializer.Serialize(new
        {
            source.Id, source.Revision, source.RetiredAt, source.CompanyKey, source.CompanyId, source.CompanyName,
            source.Kind, source.Value, source.PersonName, source.Role, source.Source, source.SourceRef,
            source.SeenCount, source.FirstSeenAt, source.LastSeenAt,
        })),
    };

    public bool Matches(CompanyContact source) => this == Capture(source);
}

public sealed class ContactSources(IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time)
{
    public const string ManualSource = "вручную";

    internal async Task PublishPostAsync(JobFinderDbContext context, string? company, long? companyId,
        IEnumerable<VacancyContact> contacts, string sourceRef, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("post contacts require the publication transaction");
        var key = VacancyFingerprint.NormalizeCompany(company);
        if (key.Length == 0) return;
        foreach (var contact in contacts.Where(contact => contact.Kind is ContactKind.Telegram or ContactKind.Email or ContactKind.Phone or ContactKind.LinkedIn
            && !contact.Value.StartsWith("tg://", StringComparison.Ordinal)))
        {
            var value = ContactExtractor.Normalize(contact.Kind, contact.Value);
            var person = contact.Label is "автор поста" ? null : contact.Label;
            var now = time.GetUtcNow();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO company_contacts (company_key, company_name, company_id, kind, value, person_name, role, source, source_ref, revision, seen_count, first_seen_at, last_seen_at)
                VALUES ({key}, {company}, {companyId}, {contact.Kind.ToString()}, {value}, {person}, {"рекрутер (из поста)"}, {"пост"}, {contact.Source ?? sourceRef}, 1, 1, {now}, {now})
                ON CONFLICT (company_key, kind, value) DO UPDATE SET
                    revision = company_contacts.revision + 1, seen_count = company_contacts.seen_count + 1, last_seen_at = EXCLUDED.last_seen_at,
                    person_name = COALESCE(company_contacts.person_name, EXCLUDED.person_name),
                    role = COALESCE(company_contacts.role, EXCLUDED.role), company_id = COALESCE(company_contacts.company_id, EXCLUDED.company_id)
                WHERE company_contacts.retired_at IS NULL
                """, ct);
        }
    }

    public async Task<long?> UpsertAsync(string? companyName, long? companyId, ContactKind kind, string value,
        string? person, string? role, string source, string? sourceRef, CancellationToken ct)
    {
        var key = VacancyFingerprint.NormalizeCompany(companyName);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(value)) return null;
        value = ContactExtractor.Normalize(kind, value);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"hiring.outreach.company." + key}, 0))", ct);
        var contact = await context.CompanyContacts.FromSqlInterpolated($"SELECT * FROM company_contacts WHERE company_key = {key} AND kind = {kind.ToString()} AND value = {value} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (contact?.RetiredAt is not null) return null;
        var now = time.GetUtcNow();
        if (contact is null)
        {
            contact = new CompanyContact
            {
                CompanyKey = key, CompanyName = companyName, CompanyId = companyId, Kind = kind, Value = value,
                PersonName = person, Role = role, Source = source, SourceRef = sourceRef, FirstSeenAt = now, LastSeenAt = now,
            };
            context.CompanyContacts.Add(contact);
        }
        contact.Revision++;
        contact.SeenCount++;
        contact.LastSeenAt = now;
        contact.PersonName ??= person;
        contact.Role ??= role;
        contact.CompanyId ??= companyId;
        if (source == ManualSource)
        {
            notifier.Add(context, NotificationKind.Info, $"Контакт {contact.Value} добавлен в реестр {contact.CompanyName ?? contact.CompanyKey}");
            await notifier.SaveAndFlushAsync(context, ct);
        }
        else
        {
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        return contact.Id;
    }

    public async Task<ActionResult> RetireAsync(ContactEditSnapshot expected, CancellationToken ct)
    {
        using var span = Telemetry.StartStage("retire", "contact").Tag("contact.id", expected.Id);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var contact = await context.CompanyContacts.FromSqlInterpolated($"SELECT * FROM company_contacts WHERE id = {expected.Id} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (contact is null || contact.RetiredAt is not null || !expected.Matches(contact))
            return ActionResult.Fail("контакт изменился или уже исключён; обновите реестр перед повторным действием");
        var identity = JsonSerializer.Serialize(new { contactId = contact.Id });
        if (await context.Outreaches.AnyAsync(row => row.ContactId == contact.Id
                && row.Status != OutreachStatus.Draft && row.Status != OutreachStatus.Dismissed, ct)
            || await context.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM employer_messages message
                    WHERE message.input_json @> {identity}::jsonb
                       OR EXISTS (SELECT 1 FROM outreaches source WHERE source.contact_id = {contact.Id}
                           AND message.input_json @> jsonb_build_object('outreachId', source.id))
                ) AS "Value"
                """).SingleAsync(ct))
            return ActionResult.Fail("контакт связан с отправкой или её историей: он сохранён для сверки и входящих ответов");
        contact.RetiredAt = time.GetUtcNow();
        contact.Revision++;
        notifier.Add(context, NotificationKind.Info, $"Контакт {contact.Value} ({contact.CompanyName ?? contact.CompanyKey}) исключён из новых откликов; история сохранена");
        await notifier.SaveAndFlushAsync(context, ct);
        return ActionResult.Done("контакт исключён из новых откликов; история сохранена");
    }
}
