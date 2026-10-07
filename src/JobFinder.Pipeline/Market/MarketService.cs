using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Market;

public readonly record struct MarketView(string? Note, decimal? RatioToP90, SegmentStats? Segment, decimal? OfferNetUsd);

/// <summary>The salary market from the collected vacancies (rebuilt at most every 30 minutes) and a vacancy's place in it.</summary>
public sealed class MarketService(IDbContextFactory<JobFinderDbContext> db, SalaryEvaluator salary, TimeProvider time)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MarketSnapshot? _snapshot;

    public async Task<MarketSnapshot> SnapshotAsync(CancellationToken ct)
    {
        if (_snapshot is { } fresh && time.GetUtcNow() - fresh.BuiltAt < MaxAge) return fresh;
        await _gate.WaitAsync(ct);
        try
        {
            if (_snapshot is { } again && time.GetUtcNow() - again.BuiltAt < MaxAge) return again;
            var since = time.GetUtcNow().AddDays(-60);
            await using var ctx = await db.CreateDbContextAsync(ct);
            var rows = await ctx.Vacancies.AsNoTracking()
                .Where(v => v.FirstSeenAt >= since && (v.SalaryFrom != null || v.SalaryTo != null))
                .ToListAsync(ct);
            var points = rows.Select(v => PointOf(v) is { } usd
                    ? new SalaryPoint(MarketSnapshot.SeniorityOf(v.Title), MarketSnapshot.GeoOf(v), usd)
                    : (SalaryPoint?)null)
                .OfType<SalaryPoint>();
            _snapshot = MarketSnapshot.Build(points, time.GetUtcNow());
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One number for a range: the middle of "from–to", or the single bound given.</summary>
    public decimal? PointOf(Vacancy v)
    {
        decimal? amount = v switch
        {
            { SalaryFrom: { } from, SalaryTo: { } to } => (from + to) / 2,
            { SalaryFrom: { } from } => from,
            { SalaryTo: { } to } => to,
            _ => null,
        };
        return amount is { } a ? salary.ToMonthlyNetUsd(a, v.Currency, v.SalaryGross) : null;
    }

    public async Task<MarketView> DescribeAsync(Vacancy v, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync(ct);
        var segment = snapshot.Lookup(MarketSnapshot.SeniorityOf(v.Title), MarketSnapshot.GeoOf(v));
        if (segment is null) return new MarketView(null, null, null, PointOf(v));
        var offer = PointOf(v);
        var head = $"рынок {MarketSnapshot.SegmentLabel(segment)}: медиана {MarketSnapshot.Money(segment.P50)} net " +
                   $"(p25–p75 {MarketSnapshot.Money(segment.P25)}–{MarketSnapshot.Money(segment.P75)}, n={segment.Count})";
        if (offer is null) return new MarketView(head + "; вилка не указана", null, segment, null);
        var note = $"{head}; эта вилка ≈ {MarketSnapshot.Money(offer.Value)} — p{segment.PercentileOf(offer.Value)}";
        return new MarketView(note, segment.P90 > 0 ? offer / segment.P90 : null, segment, offer);
    }

    /// <summary>How many times the same opening (company + title) got a new hh id within 90 days, this sighting excluded.</summary>
    public async Task<int> RepostsAsync(JobFinderDbContext ctx, Vacancy v, CancellationToken ct)
    {
        if (v.Fingerprint.StartsWith('~')) return 0;
        var since = time.GetUtcNow().AddDays(-90);
        var ids = await ctx.VacancySources
            .Where(s => s.Source == SourceKind.HeadHunter && s.Vacancy!.Fingerprint == v.Fingerprint && s.Vacancy.FirstSeenAt >= since)
            .Select(s => s.ExternalId).Distinct().CountAsync(ct);
        return Math.Max(0, ids - 1);
    }
}
