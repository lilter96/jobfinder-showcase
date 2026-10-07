using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

internal static class EmployerMessageSources
{
    public static EmployerMessageInput Read(EmployerMessage message)
    {
        var input = JsonSerializer.Deserialize(message.InputJson, EmployerMessageJsonContext.Default.EmployerMessageInput)
            ?? throw new UnsupportedEmployerMessageException();
        if (input.Version != 1 || input.VacancyId != message.VacancyId || string.IsNullOrWhiteSpace(input.Text)
            || string.IsNullOrWhiteSpace(input.Target)) throw new UnsupportedEmployerMessageException();
        return input;
    }

    public static Task<Vacancy?> VacancyAsync(JobFinderDbContext context, long id, CancellationToken ct) =>
        context.Vacancies.AsNoTracking().Include(row => row.Company).Include(row => row.Sources).Include(row => row.Applications).ThenInclude(row => row.Events)
            .SingleOrDefaultAsync(row => row.Id == id, ct);

    public static string Context(Vacancy vacancy) => DeliveryLedger.Hash(JsonSerializer.Serialize(new
    {
        vacancy.Title, vacancy.CompanyName, vacancy.Description, vacancy.Url, vacancy.Status,
        Sources = vacancy.Sources.OrderBy(row => row.Id).Select(row => new { row.Source, row.ExternalId, row.Url }),
        Applications = vacancy.Applications.OrderBy(row => row.Id).Select(row => new
        {
            row.Id, row.Revision, row.Status, row.Stage, row.Target, row.Channel, row.LastOutboundAt, row.CoverLetter,
            Events = row.Events.OrderBy(ev => ev.Id).Select(ev => new { ev.Id, ev.Revision, ev.Kind, ev.Channel, ev.Author, ev.Text, ev.Draft, ev.DraftStatus }),
        }),
    }));

    public static string ContactHash(CompanyContact contact) => DeliveryLedger.Hash(JsonSerializer.Serialize(new
        { contact.Id, contact.CompanyKey, contact.Kind, contact.Value, contact.PersonName, contact.Role }));

    public static string EventHash(ApplicationEvent source) => DeliveryLedger.Hash(JsonSerializer.Serialize(new
        { source.Id, source.ApplicationId, source.Revision, source.Text, source.Draft, source.Channel, source.Author, source.ExternalId }));

    public static string OutreachHash(Outreach source, CompanyContact contact) => DeliveryLedger.Hash(JsonSerializer.Serialize(new
        { source.Id, source.Revision, source.Text, source.Fresh, Contact = ContactHash(contact) }));

    internal static async Task<bool> DeliveryPendingAsync(JobFinderDbContext context, long vacancyId, CancellationToken ct) =>
        await context.EmployerMessages.AnyAsync(row => row.VacancyId == vacancyId && (row.Status == EmployerMessageStatus.Pending
            || row.Status == EmployerMessageStatus.Ready), ct)
        || await UnresolvedDeliveryAsync(context, vacancyId, ct);

    internal static async Task<bool> UnresolvedDeliveryAsync(JobFinderDbContext context, long vacancyId, CancellationToken ct) =>
        await context.EmployerMessages.AnyAsync(row => row.VacancyId == vacancyId
            && (row.Status == EmployerMessageStatus.Sending || row.Status == EmployerMessageStatus.OutcomeUnknown), ct)
        || await context.Applications.AnyAsync(row => row.VacancyId == vacancyId
            && (row.Status == ApplicationStatus.Sending || row.Status == ApplicationStatus.OutcomeUnknown), ct)
        || await context.ApplicationDeliveryRequests.AnyAsync(row => context.Applications.Any(app => app.Id == row.ApplicationId && app.VacancyId == vacancyId)
            && (row.Stage == ApplicationDeliveryStage.Sending || row.Stage == ApplicationDeliveryStage.OutcomeUnknown), ct)
        || await context.DeliveryAttempts.AnyAsync(row => (row.Application != null && row.Application.VacancyId == vacancyId
            || row.Message != null && row.Message.VacancyId == vacancyId)
            && (row.Status == DeliveryAttemptStatus.Submitting || row.Status == DeliveryAttemptStatus.OutcomeUnknown), ct);

    public static async Task WitnessAsync(JobFinderDbContext context, EmployerMessageInput input, CancellationToken ct)
    {
        input.ProfileVersion = await VersionAsync(context, ProfileProvider.Key, ct);
        input.FactsVersion = await VersionAsync(context, FactsProvider.Key, ct);
        if (input.ProfileVersion == 0 || input.FactsVersion == 0) throw new InvalidOperationException("нет профиля или фактов кандидата");
    }

    private static async Task<long> VersionAsync(JobFinderDbContext context, string key, CancellationToken ct) =>
        await context.Documents.Where(row => row.Key == key).Select(row => (long?)row.Version).SingleOrDefaultAsync(ct) ?? 0;

    public static async Task<bool> MatchesAsync(JobFinderDbContext context, EmployerMessageInput input, CancellationToken ct)
    {
        var vacancy = await VacancyAsync(context, input.VacancyId, ct);
        if (vacancy is null || vacancy.Status == VacancyStatus.Dismissed || Context(vacancy) != input.ContextHash
            || await VersionAsync(context, ProfileProvider.Key, ct) != input.ProfileVersion
            || await VersionAsync(context, FactsProvider.Key, ct) != input.FactsVersion) return false;
        if (input.Channel == ApplicationChannel.HeadHunter && (input.HhChatId is null || input.HhVacancyId is null
            || !await context.HhResponses.AnyAsync(row => row.ChatId == input.HhChatId && row.HhVacancyId == input.HhVacancyId, ct))) return false;
        if (input.EventId is { } eventId)
        {
            var source = await context.ApplicationEvents.AsNoTracking().SingleOrDefaultAsync(row => row.Id == eventId, ct);
            return source is { DraftStatus: DraftStatus.Pending } && source.Revision == input.SourceRevision && EventHash(source) == input.SourceHash;
        }
        if (input.ContactId is not { } contactId) return true;
        var contact = await context.CompanyContacts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == contactId, ct);
        if (contact is null || contact.RetiredAt is not null || contact.CompanyKey != VacancyFingerprint.NormalizeCompany(vacancy.CompanyName)
            || contact.Value != input.Target) return false;
        if (input.OutreachId is not { } outreachId) return ContactHash(contact) == input.SourceHash;
        var outreach = await context.Outreaches.AsNoTracking().SingleOrDefaultAsync(row => row.Id == outreachId, ct);
        return outreach is { Status: OutreachStatus.Draft } && outreach.Revision == input.SourceRevision
            && outreach.VacancyId == input.VacancyId && outreach.ContactId == contactId && OutreachHash(outreach, contact) == input.SourceHash;
    }

    public static async Task<bool> FreezeAsync(JobFinderDbContext context, EmployerMessageInput input, CancellationToken ct)
    {
        if (input.EventId is { } eventId)
            return await context.ApplicationEvents.Where(row => row.Id == eventId && row.Revision == input.SourceRevision && row.DraftStatus == DraftStatus.Pending)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.DraftStatus, DraftStatus.Sending)
                    .SetProperty(row => row.Revision, row => row.Revision + 1), ct) == 1;
        if (input.OutreachId is { } outreachId)
            return await context.Outreaches.Where(row => row.Id == outreachId && row.Revision == input.SourceRevision && row.Status == OutreachStatus.Draft)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, OutreachStatus.Sending)
                    .SetProperty(row => row.Revision, row => row.Revision + 1), ct) == 1;
        return true;
    }
}
