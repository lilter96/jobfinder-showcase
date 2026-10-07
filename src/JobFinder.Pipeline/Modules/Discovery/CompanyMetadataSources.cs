using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed record CompanyCollectionSnapshot(long HhId, long? Id, DateTimeOffset? UpdatedAt, DateTimeOffset? DetailsFetchedAt, int DotnetVacancies);

/// <summary>Owns observed employer metadata; user flags belong to CompanyPreferences.</summary>
public static class CompanyMetadataSources
{
    public static async Task<CompanyCollectionSnapshot> CaptureAsync(IDbContextFactory<JobFinderDbContext> db, long hhId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var company = await context.Companies.AsNoTracking().SingleOrDefaultAsync(row => row.HhId == hhId, ct);
        return Snapshot(hhId, company);
    }

    public static async Task<IReadOnlyList<CompanyCollectionSnapshot>> CaptureAbsenceAsync(IDbContextFactory<JobFinderDbContext> db, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var companies = await context.Companies.AsNoTracking().Where(row => row.HhId != null && row.DotnetVacancies > 0).ToListAsync(ct);
        return companies.Select(row => Snapshot(row.HhId!.Value, row)).ToArray();
    }

    public static async Task<bool> PublishCollectedAsync(IDbContextFactory<JobFinderDbContext> db, CompanyCollectionSnapshot captured,
        string? name, int count, HhEmployer? employer, Dictionary<string, System.Text.RegularExpressions.Regex[]> tags, DateTimeOffset now, CancellationToken ct)
    {
        if (captured.HhId <= 0 || count < 0 || now == default || employer is not null && employer.Id != captured.HhId) return false;
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var company = await context.Companies.FromSqlInterpolated($"SELECT * FROM companies WHERE hh_id = {captured.HhId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (captured != Snapshot(captured.HhId, company)) return false;
        if (company is null)
        {
            company = new Company { HhId = captured.HhId, Name = name ?? $"hh:{captured.HhId}", FirstSeenAt = now, UpdatedAt = now };
            context.Companies.Add(company);
        }
        company.DotnetVacancies = count;
        if (employer is not null) ApplyEmployer(company, employer, now);
        var text = TextUtil.Normalize($"{company.Description} {string.Join(' ', company.Industries)}");
        company.Tags = tags.Where(pair => pair.Value.Any(expression => expression.IsMatch(text))).Select(pair => pair.Key).ToList();
        company.UpdatedAt = now;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public static async Task<int> PublishAbsenceAsync(IDbContextFactory<JobFinderDbContext> db, IReadOnlyList<CompanyCollectionSnapshot> captured,
        IReadOnlyCollection<long> seen, DateTimeOffset now, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var changed = 0;
        foreach (var prior in captured.Where(row => !seen.Contains(row.HhId)).OrderBy(row => row.Id))
        {
            var company = await context.Companies.FromSqlInterpolated($"SELECT * FROM companies WHERE hh_id = {prior.HhId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (company is null || prior != Snapshot(prior.HhId, company)) continue;
            company.DotnetVacancies = 0;
            company.UpdatedAt = now;
            changed++;
        }
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return changed;
    }

    private static CompanyCollectionSnapshot Snapshot(long hhId, Company? company) =>
        new(hhId, company?.Id, company?.UpdatedAt, company?.DetailsFetchedAt, company?.DotnetVacancies ?? 0);

    public static async Task<long?> UpsertHhAsync(JobFinderDbContext ctx, long hhId, string? name, HhEmployer? employer, DateTimeOffset now, CancellationToken ct)
    {
        if (ctx.Database.CurrentTransaction is null) throw new InvalidOperationException("company metadata joins its vacancy source transaction");
        var company = await ctx.Companies.FirstOrDefaultAsync(c => c.HhId == hhId, ct);
        if (company is null)
        {
            company = new Company { Name = name ?? employer?.Name ?? $"hh:{hhId}", HhId = hhId, FirstSeenAt = now, UpdatedAt = now };
            ctx.Companies.Add(company);
        }

        if (employer is not null) ApplyEmployer(company, employer, now);
        await ctx.SaveChangesAsync(ct);
        return company.Id;
    }

    public static async Task<long?> UpsertHabrAsync(JobFinderDbContext ctx, string alias, string? name, DateTimeOffset now, CancellationToken ct)
    {
        if (ctx.Database.CurrentTransaction is null) throw new InvalidOperationException("company metadata joins its vacancy source transaction");
        var company = await ctx.Companies.FirstOrDefaultAsync(c => c.HabrAlias == alias, ct);
        if (company is null)
        {
            // The same employer is often already known from hh: link by normalized name before creating a duplicate.
            var normalized = VacancyFingerprint.NormalizeCompany(name);
            if (normalized.Length > 0)
            {
                var candidates = await ctx.Companies.Where(c => c.HabrAlias == null && c.Name.ToLower() == (name ?? "").ToLower()).ToListAsync(ct);
                company = candidates.FirstOrDefault(c => VacancyFingerprint.NormalizeCompany(c.Name) == normalized);
            }

            if (company is null)
            {
                company = new Company { Name = name ?? alias, FirstSeenAt = now, UpdatedAt = now };
                ctx.Companies.Add(company);
            }

            company.HabrAlias = alias;
            await ctx.SaveChangesAsync(ct);
        }

        return company.Id;
    }

    public static void ApplyEmployer(Company company, HhEmployer e, DateTimeOffset now)
    {
        company.Name = string.IsNullOrWhiteSpace(e.Name) ? company.Name : e.Name;
        company.Site = e.Site ?? company.Site;
        company.Area = e.Area ?? company.Area;
        company.SizeCategory = e.SizeCategory ?? company.SizeCategory;
        if (e.Industries.Count > 0) company.Industries = e.Industries;
        company.Description = TextUtil.Truncate(e.Description, 8000);
        company.Rating = e.Rating ?? company.Rating;
        company.ItAccredited = e.ItAccredited;
        company.OpenVacancies = e.OpenVacancies ?? company.OpenVacancies;
        company.DetailsFetchedAt = now;
        company.UpdatedAt = now;
    }
}
