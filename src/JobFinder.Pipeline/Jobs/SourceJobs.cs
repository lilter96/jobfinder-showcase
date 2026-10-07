using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Jobs;

/// <summary>
/// hh.ru / rabota.by search profiles, newest first. The site tolerates little from one address, so a run reads as few pages as it can:
/// page 0 of each profile, the next page only while the current one still had unseen vacancies (the backlog is already collected);
/// only unseen ids cost a detail request; nothing during the quiet hours. hh and rabota.by share ids, so a vacancy found by both
/// searches is fetched once. The site's budget or pause ends the run quietly with what it found; the next run goes on.
/// </summary>
public sealed class HhSearchJob(SearchPolicyStore policies, HhClient hh, VacancyIngestor ingestor, JobTriggers triggers,
    IDbContextFactory<JobFinderDbContext> db, Notifier notifier, Core.Time.LocalClock clock, TimeProvider time, ILogger<HhSearchJob> log)
    : SearchScheduledJob(policies, db, triggers, notifier, time, log)
{
    /// <summary>A search run may wait this long for a slot in the site's budget before leaving the rest to the next run.</summary>
    private static readonly TimeSpan BudgetWait = TimeSpan.FromMinutes(10);

    public override string Name => JobNames.HhSearch;

    protected override bool Enabled => Snapshot.Value.HeadHunter.Enabled && Snapshot.Value.HeadHunter.Searches.Count > 0;

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) =>
        QuietHours.NextAllowed(finishedAt + Jittered(Snapshot.Value.HeadHunter.IntervalMinutes, Snapshot.Value.HeadHunter.JitterMinutes), Snapshot.Value.HeadHunter.QuietHours, clock.Zone);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        var options = Snapshot.Value.HeadHunter;
        var now = Time.GetUtcNow();
        if (QuietHours.NextAllowed(now, options.QuietHours, clock.Zone) > now)
            return new JobOutcome(0, "quiet hours");
        var created = 0;
        var seen = 0;
        var pages = 0;
        var fetchedThisRun = new HashSet<long>();
        try
        {
            foreach (var query in options.Searches)
            {
                var search = SearchPolicyCompiler.ToRequest(query);
                for (var page = 0; page < search.MaxPages; page++)
                {
                    var (items, _) = await hh.SearchAsync(search, page, ct, maxWait: BudgetWait);
                    pages++;
                    seen += items.Count;
                    var ids = items.Select(i => i.Id.ToString(CultureInfo.InvariantCulture)).ToList();
                    var known = await ingestor.KnownIdsAsync(SourceKind.HeadHunter, ids, ct);
                    var unseen = 0;
                    foreach (var brief in items)
                    {
                        if (known.Contains(brief.Id.ToString(CultureInfo.InvariantCulture))) continue;
                        unseen++;
                        if (!fetchedThisRun.Add(brief.Id)) continue;
                        try
                        {
                            var detail = await hh.GetVacancyAsync(brief.Host, brief.Id, ct, maxWait: BudgetWait);
                            var result = await ingestor.IngestHhAsync(detail, brief, null, ct);
                            if (result.Created) created++;
                        }
                        catch (FormatException e)
                        {
                            log.Warn($"[{Name}] {brief.Url}: {e.Message}");
                        }
                        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            log.Info($"[{Name}] {brief.Url}: gone");
                        }
                    }

                    // Newest first: a page with nothing unseen means the rest was collected by earlier runs.
                    if (items.Count < HhClient.PageSize || unseen == 0) break;
                }
            }
        }
        catch (JobFinder.Integrations.Http.SourceBlockedException e) when (!e.Fresh)
        {
            log.Info($"[{Name}] stopped after {pages} pages: {e.Message}");
        }
        finally
        {
            if (created > 0) Triggers.Trigger(JobNames.Vacancies);
        }

        if (created >= BatchNotice)
            await Notifier.SendAsync(NotificationKind.Info,
                $"📥 hh: {created} новых вакансий за один проход. Оцениваю; карточек на одобрение — не больше " +
                $"{Workers.VacancyWorker.MaxApprovalCardsPerHour} в час, остальные подходящие ждут в /top, на дашборде (Вакансии → «на одобрение») " +
                "и в вечернем дайджесте.", ct: ct);
        return new JobOutcome(created, $"{pages} search pages, {seen} in search results");
    }

    /// <summary>A run that finds this many new vacancies (a backfill, a new search) is announced once instead of card by card.</summary>
    private const int BatchNotice = 10;
}

public sealed class HhLinksJob(HhLinkRequests requests, JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db,
    Notifier notifier, TimeProvider time, ILogger<HhLinksJob> log) : ScheduledJob(db, triggers, notifier, time, log)
{
    public override string Name => JobNames.HhLinks;
    protected override bool Enabled => true;
    protected override TimeSpan FirstDelay => TimeSpan.Zero;
    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt + TimeSpan.FromMinutes(10);
    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct) =>
        new(await requests.ImportLegacyAsync(ct), "legacy links imported; durable handlers own execution");
}

/// <summary>Habr Career searches; a row whose title and skills show no .NET marker is not worth a detail request.</summary>
public sealed class HabrSearchJob(SearchPolicyStore policies, HabrClient habr, VacancyIngestor ingestor, JobTriggers triggers,
    IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time, ILogger<HabrSearchJob> log)
    : SearchScheduledJob(policies, db, triggers, notifier, time, log)
{
    public override string Name => JobNames.HabrSearch;

    protected override bool Enabled => Snapshot.Value.Habr.Enabled && Snapshot.Value.Habr.Queries.Count > 0;

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt + Jittered(Snapshot.Value.Habr.IntervalMinutes, Snapshot.Value.Habr.JitterMinutes);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        var options = Snapshot.Value.Habr;
        var markers = Snapshot.Value.Filter.RequiredMarkers.Select(TextUtil.WholeWord).ToArray();
        var created = 0;
        var seen = 0;
        var done = new HashSet<long>();
        foreach (var query in options.Queries)
        foreach (var filterQuery in options.Filters.DefaultIfEmpty(""))
        {
            for (var page = 1; page <= options.MaxPages; page++)
            {
                var (items, totalPages) = await habr.SearchAsync(query, filterQuery, page, ct);
                seen += items.Count;
                var known = await ingestor.KnownIdsAsync(SourceKind.Habr, items.Select(i => i.Id.ToString(CultureInfo.InvariantCulture)).ToList(), ct);
                foreach (var item in items)
                {
                    if (known.Contains(item.Id.ToString(CultureInfo.InvariantCulture)) || !done.Add(item.Id)) continue;
                    var head = TextUtil.Normalize(item.Title + " " + string.Join(' ', item.Skills));
                    if (markers.Length > 0 && !markers.Any(marker => marker.IsMatch(head))) continue;
                    var description = await habr.GetDescriptionAsync(item.Id, ct);
                    var result = await ingestor.IngestHabrAsync(item, description, ct);
                    if (result.Created) created++;
                }

                if (page >= totalPages) break;
            }
        }

        if (created > 0) Triggers.Trigger(JobNames.Vacancies);
        return new JobOutcome(created, $"{seen} in search results");
    }
}

/// <summary>
/// Nightly: every employer with open .NET vacancies (wide searches, all pages), its page refreshed at most every N days, tagged by the
/// configured keywords. The result is the Companies page of the dashboard. Background work: it gets only its share of the site's budget
/// and waits for slots, so the sweep stretches over the night instead of arriving as one burst.
/// </summary>
public sealed class CompanyCollectorJob(SearchPolicyStore policies, HhClient hh, JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db,
    Notifier notifier, Core.Time.LocalClock clock, TimeProvider time, ILogger<CompanyCollectorJob> log)
    : SearchScheduledJob(policies, db, triggers, notifier, time, log)
{
    private readonly IDbContextFactory<JobFinderDbContext> _db = db;

    /// <summary>The sweep may wait this long for each slot in the site's budget.</summary>
    private static readonly TimeSpan SweepWait = TimeSpan.FromHours(1);

    public override string Name => JobNames.Companies;

    protected override bool Enabled => Snapshot.Value.HeadHunter.Enabled && Snapshot.Value.Companies.Enabled && Snapshot.Value.Companies.Searches.Count > 0;

    protected override TimeSpan FirstDelay => clock.NextOccurrenceUtc(RunAt) - Time.GetUtcNow();

    private TimeOnly RunAt => TimeOnly.ParseExact(Snapshot.Value.Companies.RunAt, "HH:mm", CultureInfo.InvariantCulture);

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => clock.NextOccurrenceUtc(RunAt);

    protected override DateTimeOffset? LastSlot(DateTimeOffset now) => clock.NextOccurrenceUtc(RunAt).AddDays(-1);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        var options = Snapshot.Value.Companies;
        var tags = options.Tags.ToDictionary(pair => pair.Key, pair => pair.Value.Select(TextUtil.WordPrefix).ToArray(), StringComparer.OrdinalIgnoreCase);
        // employer id → (host it was seen on, name, .NET vacancies seen)
        var absent = await CompanyMetadataSources.CaptureAbsenceAsync(_db, ct);
        var employers = new Dictionary<long, (string Host, string? Name, int Count)>();
        foreach (var query in options.Searches)
        {
            var search = SearchPolicyCompiler.ToRequest(query);
            for (var page = 0; page < search.MaxPages; page++)
            {
                var (items, _) = await hh.SearchAsync(search, page, ct, ScrapePriority.Low, SweepWait);
                foreach (var item in items.Where(i => i.CompanyId is not null))
                {
                    var id = item.CompanyId!.Value;
                    employers[id] = employers.TryGetValue(id, out var e) ? e with { Count = e.Count + 1 } : (item.Host, item.CompanyName, 1);
                }

                if (items.Count < HhClient.PageSize) break;
            }
        }

        var refreshBefore = Time.GetUtcNow().AddDays(-options.RefreshDays);
        var fetched = 0;
        foreach (var (hhId, info) in employers)
        {
            ct.ThrowIfCancellationRequested();
            var captured = await CompanyMetadataSources.CaptureAsync(_db, hhId, ct);
            var now = Time.GetUtcNow();
            HhEmployer? employer = null;
            if (captured.DetailsFetchedAt is null || captured.DetailsFetchedAt < refreshBefore)
            {
                try
                {
                    employer = await hh.GetEmployerAsync(info.Host, hhId, ct, ScrapePriority.Low, SweepWait);
                    fetched++;
                }
                catch (Exception e) when (e is FormatException or HttpRequestException)
                {
                    log.Warn($"[{Name}] employer {hhId}: {e.Message}");
                }
            }
            if (!await CompanyMetadataSources.PublishCollectedAsync(_db, captured, info.Name, info.Count, employer, tags, now, ct))
                log.Debug($"[{Name}] employer {hhId}: newer metadata preserved");
        }

        // Clear only the original observed cohort; a newer collection is never overwritten.
        await CompanyMetadataSources.PublishAbsenceAsync(_db, absent, employers.Keys.ToArray(), Time.GetUtcNow(), ct);

        return new JobOutcome(employers.Count, $"{fetched} employer pages fetched");
    }

}
