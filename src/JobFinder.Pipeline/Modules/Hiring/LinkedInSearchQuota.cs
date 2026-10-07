using System.Globalization;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.LinkedIn;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class LinkedInSearchQuota(IDbContextFactory<JobFinderDbContext> db, LocalClock clock, LinkedInOptions options)
{
    internal const string Prefix = "linkedin.searches.";
    public bool Enabled => options.Enabled && options.MaxSearchesPerDay > 0;

    public async Task<bool> TakeAsync(CancellationToken ct)
    {
        if (!Enabled) return false;
        var key = Prefix + clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
        var row = await context.Settings.SingleOrDefaultAsync(setting => setting.Key == key, ct);
        var used = 0;
        if (row is not null && (!int.TryParse(row.Value, NumberStyles.None, CultureInfo.InvariantCulture, out used) || used < 0))
            throw new InvalidOperationException("damaged LinkedIn search budget; evidence retained");
        if (used >= options.MaxSearchesPerDay) return false;
        if (row is null) context.Settings.Add(row = new() { Key = key, Value = "" });
        row.Value = (used + 1).ToString(CultureInfo.InvariantCulture);
        row.UpdatedAt = clock.UtcNow;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
