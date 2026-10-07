using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record OutreachScanTicket
{
    public int Version { get; set; } = 1;
    public long VacancyId { get; set; }
    public Guid Token { get; set; }
    public string SourceHash { get; set; } = "";
}

public sealed record OutreachScanState
{
    public int Version { get; set; } = 1;
    public long VacancyId { get; set; }
    public Guid Token { get; set; }
    public string SourceHash { get; set; } = "";
    public int Failures { get; set; }
    public DateTimeOffset RetryAt { get; set; }
}

public sealed record OutreachScanBatch
{
    public List<OutreachScanTicket> Tickets { get; set; } = [];
    public int Damaged { get; set; }
}

public sealed class OutreachScans(IDbContextFactory<JobFinderDbContext> db, JobFinderLlm llm, TimeProvider time)
{
    internal const string Prefix = "outreach.scan.";

    public async Task<OutreachScanBatch> ReserveAsync(int threshold, int limit, CancellationToken ct)
    {
        if (threshold is < 0 or > 100 || limit is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (limit == 0) return new();
        var recipe = await RecipeAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        using var span = Telemetry.StartStage("reserve", "outreach-scan");
        var since = time.GetUtcNow().AddDays(-4);
        var vacancies = await context.Vacancies.AsNoTracking().Where(vacancy => vacancy.Score >= threshold
            && vacancy.Legitimacy != Legitimacy.Suspicious && vacancy.Legitimacy != Legitimacy.Scam
            && !context.Outreaches.Any(outreach => outreach.VacancyId == vacancy.Id)
            && vacancy.Applications.Any(application => application.Status == ApplicationStatus.Sent && application.SentAt >= since
                && (application.Stage == null || application.Stage == ApplicationStage.Sent || application.Stage == ApplicationStage.Viewed)))
            .Include(vacancy => vacancy.Company).Include(vacancy => vacancy.Sources).Include(vacancy => vacancy.Applications).ThenInclude(application => application.Events)
            .AsSplitQuery().OrderByDescending(vacancy => vacancy.Score).ThenBy(vacancy => vacancy.Id).ToListAsync(ct);
        var shared = await SharedAsync(context, recipe, ct);
        var keys = vacancies.Select(vacancy => Key(vacancy.Id)).ToArray();
        var saved = await context.Settings.Where(row => keys.Contains(row.Key)).ToDictionaryAsync(row => row.Key, ct);
        var batch = new OutreachScanBatch();
        foreach (var vacancy in vacancies)
        {
            OutreachScanState? previous = null;
            if (saved.TryGetValue(Key(vacancy.Id), out var row))
            {
                try { previous = Read(row.Value, vacancy.Id); }
                catch (Exception error) when (error is JsonException or InvalidDataException) { batch.Damaged++; continue; }
            }
            var hash = Hash(vacancy, shared, threshold);
            if (previous?.SourceHash == hash && previous.RetryAt > time.GetUtcNow()) continue;
            var state = new OutreachScanState { VacancyId = vacancy.Id, Token = Guid.NewGuid(), SourceHash = hash,
                Failures = previous?.SourceHash == hash ? previous.Failures : 0, RetryAt = time.GetUtcNow().AddMinutes(30) };
            if (row is null) context.Settings.Add(row = new() { Key = Key(vacancy.Id), Value = "" });
            row.Value = Serialize(state);
            row.UpdatedAt = time.GetUtcNow();
            batch.Tickets.Add(new() { VacancyId = vacancy.Id, Token = state.Token, SourceHash = hash });
            if (batch.Tickets.Count == limit) break;
        }
        span.Tag("selected.count", batch.Tickets.Count).Tag("damaged.count", batch.Damaged);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return batch;
    }

    public async Task<bool> FinishAsync(OutreachScanTicket ticket, int threshold, bool prepared, CancellationToken ct)
    {
        if (ticket.Version != 1 || ticket.VacancyId <= 0 || ticket.Token == Guid.Empty || ticket.SourceHash is not { Length: 64 } || threshold is < 0 or > 100)
            throw new ArgumentException("invalid outreach scan witness", nameof(ticket));
        var recipe = await RecipeAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        var row = await context.Settings.SingleOrDefaultAsync(setting => setting.Key == Key(ticket.VacancyId), ct);
        if (row is null) return false;
        var state = Read(row.Value, ticket.VacancyId);
        if (state.Token != ticket.Token || state.SourceHash != ticket.SourceHash) return false;
        if (prepared)
        {
            if (!await context.Outreaches.AnyAsync(outreach => outreach.VacancyId == ticket.VacancyId, ct)) return false;
            state.Failures = 0;
            state.RetryAt = time.GetUtcNow().AddHours(6);
        }
        else
        {
            var vacancy = await EmployerMessageSources.VacancyAsync(context, ticket.VacancyId, ct);
            if (vacancy is null || Hash(vacancy, await SharedAsync(context, recipe, ct), threshold) != ticket.SourceHash) return false;
            state.Failures = Math.Min(state.Failures + 1, 6);
            state.RetryAt = time.GetUtcNow().AddMinutes(Math.Min(30 * Math.Pow(2, state.Failures - 1), 360));
        }
        state.Token = Guid.NewGuid();
        row.Value = Serialize(state);
        row.UpdatedAt = time.GetUtcNow();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<string> RecipeAsync(CancellationToken ct) => (await llm.ForOperationAsync(ct)).Recipe(
        [(LlmTaskCatalog.RecruiterMessage, LlmPurpose.Application), (LlmTaskCatalog.CheckRecruiterMessage, LlmPurpose.Application),
            (LlmTaskCatalog.RecruiterMessage, LlmPurpose.ReplyDeep), (LlmTaskCatalog.CheckRecruiterMessage, LlmPurpose.ReplyDeep)],
        new Dictionary<string, string?>(), new Dictionary<string, string> { ["outreach-scan"] = "v1" }).Hash;

    private static async Task<SharedInputs> SharedAsync(JobFinderDbContext context, string recipe, CancellationToken ct) => new(
        CanonicalJson.Hash(JsonSerializer.Serialize(await context.Documents.AsNoTracking().Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key
                || row.Key == "policy.automation" || row.Key == "policy.llm").OrderBy(row => row.Key).Select(row => new { row.Key, row.Version }).ToListAsync(ct))),
        await OutreachSources.StoriesHashAsync(context, ct), recipe,
        await context.CompanyContacts.AsNoTracking().OrderBy(contact => contact.Id).ToListAsync(ct),
        await context.Outreaches.AsNoTracking().Include(outreach => outreach.Contact).OrderBy(outreach => outreach.Id).ToListAsync(ct));

    private static string Hash(Vacancy vacancy, SharedInputs shared, int threshold)
    {
        var company = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName);
        return CanonicalJson.Hash(JsonSerializer.Serialize(new { Context = OutreachSources.ContextHash(vacancy), threshold, vacancy.Score, vacancy.Legitimacy,
            shared.Documents, shared.Stories, shared.Recipe,
            Contacts = shared.Contacts.Where(contact => contact.CompanyKey == company).Select(OutreachSources.ContactHash),
            Previous = shared.Outreaches.Where(outreach => outreach.Contact?.CompanyKey == company || outreach.VacancyId == vacancy.Id)
                .Select(outreach => new { outreach.Id, outreach.VacancyId, outreach.ContactId, outreach.Revision, outreach.Status, outreach.CreatedAt, outreach.SentAt }) }));
    }

    private sealed record SharedInputs(string Documents, string Stories, string Recipe, List<CompanyContact> Contacts, List<Outreach> Outreaches);

    private static OutreachScanState Read(string json, long id)
    {
        var state = JsonSerializer.Deserialize(json, OutreachScanJsonContext.Default.OutreachScanState);
        if (state is not { Version: 1 } || state.VacancyId != id || state.Token == Guid.Empty || state.SourceHash is not { Length: 64 }
            || state.Failures is < 0 or > 6 || state.RetryAt == default) throw new InvalidDataException("damaged outreach scan state retained");
        return state;
    }

    private static string Serialize(OutreachScanState state) => JsonSerializer.Serialize(state, OutreachScanJsonContext.Default.OutreachScanState);
    private static string Key(long id) => Prefix + id.ToString(CultureInfo.InvariantCulture);
    private static Task LockAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('hiring.outreach.scan',0))", ct);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OutreachScanState))]
public sealed partial class OutreachScanJsonContext : JsonSerializerContext;
