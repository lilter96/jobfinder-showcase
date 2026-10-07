using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record HhVacancyReadSnapshot
{
    public int Version { get; set; } = 1;
    public long HhVacancyId { get; set; }
    public string SourceHash { get; set; } = "";
}

public sealed record HhUnavailableVacancy
{
    public int Version { get; set; } = 1;
    public string SourceHash { get; set; } = "";
    public DateTimeOffset RetryAt { get; set; }
}

public sealed class HhResponseBackfillSources(IDbContextFactory<JobFinderDbContext> db, TimeProvider time)
{
    internal const string UnavailablePrefix = "hh.response.unavailable.";

    public async Task<IReadOnlyList<HhVacancyReadSnapshot>> MissingAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var context = await db.CreateDbContextAsync(ct);
        var responses = await context.HhResponses.AsNoTracking()
            .Where(row => !context.VacancySources.Any(source => source.Source == SourceKind.HeadHunter && source.ExternalId == row.HhVacancyId.ToString()))
            .OrderByDescending(row => row.UpdatedAt).ThenBy(row => row.HhVacancyId).ToListAsync(ct);
        var keys = responses.Select(row => Key(row.HhVacancyId)).ToArray();
        var saved = await context.Settings.AsNoTracking().Where(row => keys.Contains(row.Key)).ToDictionaryAsync(row => row.Key, ct);
        var result = new List<HhVacancyReadSnapshot>();
        foreach (var response in responses)
        {
            var hash = SourceHash(response);
            if (saved.TryGetValue(Key(response.HhVacancyId), out var row))
            {
                var state = Read(row.Value);
                if (state.SourceHash == hash && state.RetryAt > time.GetUtcNow()) continue;
            }
            result.Add(new() { HhVacancyId = response.HhVacancyId, SourceHash = hash });
            if (result.Count == limit) break;
        }
        return result;
    }

    public async Task<bool> MarkUnavailableAsync(HhVacancyReadSnapshot snapshot, bool gone, CancellationToken ct)
    {
        if (snapshot.Version != 1 || snapshot.HhVacancyId <= 0 || string.IsNullOrWhiteSpace(snapshot.SourceHash)) throw new ArgumentException("invalid hh vacancy witness", nameof(snapshot));
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var key = Key(snapshot.HhVacancyId);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
        var response = await context.HhResponses.FromSqlInterpolated($"SELECT * FROM hh_responses WHERE hh_vacancy_id = {snapshot.HhVacancyId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (response is null || SourceHash(response) != snapshot.SourceHash || await context.VacancySources.AnyAsync(row => row.Source == SourceKind.HeadHunter
            && row.ExternalId == snapshot.HhVacancyId.ToString(CultureInfo.InvariantCulture), ct)) return false;
        var row = await context.Settings.SingleOrDefaultAsync(setting => setting.Key == key, ct);
        var previous = row is null ? null : Read(row.Value);
        if (row is null) context.Settings.Add(row = new() { Key = key, Value = "" });
        var retryAt = time.GetUtcNow().Add(gone ? TimeSpan.FromDays(7) : TimeSpan.FromHours(1));
        if (previous?.SourceHash == snapshot.SourceHash && previous.RetryAt > retryAt) retryAt = previous.RetryAt;
        row.Value = JsonSerializer.Serialize(new HhUnavailableVacancy { SourceHash = snapshot.SourceHash,
            RetryAt = retryAt }, HhBackfillJsonContext.Default.HhUnavailableVacancy);
        row.UpdatedAt = time.GetUtcNow();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static string SourceHash(HhResponse response) => CanonicalJson.Hash(JsonSerializer.Serialize(new
        { response.HhVacancyId, response.State, response.UpdatedAt, response.RespondedAt, response.ChatId, response.VacancyName, response.CompanyName }));

    private static string Key(long id) => UnavailablePrefix + id.ToString(CultureInfo.InvariantCulture);

    private static HhUnavailableVacancy Read(string json)
    {
        var state = JsonSerializer.Deserialize(json, HhBackfillJsonContext.Default.HhUnavailableVacancy) ?? throw new JsonException("empty hh unavailable state");
        if (state.Version != 1 || state.RetryAt == default || string.IsNullOrWhiteSpace(state.SourceHash)) throw new JsonException("damaged hh unavailable state");
        return state;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HhVacancyReadSnapshot))]
[JsonSerializable(typeof(HhUnavailableVacancy))]
public sealed partial class HhBackfillJsonContext : JsonSerializerContext;
