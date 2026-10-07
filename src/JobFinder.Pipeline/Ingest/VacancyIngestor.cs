using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JobFinder.Pipeline.Ingest;

public sealed record IngestResult(long VacancyId, bool Created, bool SourceAdded);

/// <summary>
/// The one door into <c>vacancies</c>. Every source builds a draft and a source row; merging decides whether the draft is a new
/// opening, the same source seen again, or another sighting of a known opening (same company + normalized title within 30 days).
/// </summary>
public sealed class VacancyIngestor(IDbContextFactory<JobFinderDbContext> db, HhClient hh, HabrOptions habrOptions, TimeProvider time,
    ILogger<VacancyIngestor> log, VacancyAssessmentCapture captures)
{
    private static readonly TimeSpan MergeWindow = TimeSpan.FromDays(30);
    private const int MaxDescription = 20000;

    public async Task<HashSet<string>> KnownIdsAsync(SourceKind source, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var ctx = await db.CreateDbContextAsync(ct);
        var known = await ctx.VacancySources.Where(s => s.Source == source && ids.Contains(s.ExternalId)).Select(s => s.ExternalId).ToListAsync(ct);
        return known.ToHashSet(StringComparer.Ordinal);
    }

    public const string ClosedReason = "вакансия закрыта или в архиве";

    /// <param name="scoreClosed">A closed vacancy is scored all the same (one the user responded to: the rejection review needs it).</param>
    public async Task<IngestResult> IngestHhAsync(HhVacancyDetail d, HhVacancyBrief? brief, HhEmployer? employer, CancellationToken ct,
        bool scoreClosed = false)
    {
        var now = time.GetUtcNow();
        var draft = HhDraft(d, brief, scoreClosed);
        var source = HhSource(d, brief, now);
        return await MergeAsync(draft, source, async ctx => d.CompanyId is { } hhId ? await CompanyMetadataSources.UpsertHhAsync(ctx, hhId, d.CompanyName, employer, time.GetUtcNow(), ct) : null, ct);
    }

    internal static Vacancy HhDraft(HhVacancyDetail d, HhVacancyBrief? brief, bool scoreClosed) => new()
        {
            Title = d.Name,
            CompanyName = d.CompanyName,
            Description = TextUtil.Truncate(d.Description, MaxDescription),
            SalaryFrom = d.SalaryFrom,
            SalaryTo = d.SalaryTo,
            Currency = d.Currency,
            SalaryGross = d.SalaryGross,
            City = d.AreaName,
            CountryCode = d.CountryCode,
            WorkFormats = d.Formats,
            EnglishRequired = d.English,
            Skills = d.KeySkills,
            Experience = d.Experience,
            Url = d.Url,
            PublishedAt = d.PublishedAt,
            OriginalPostedAt = brief?.CreatedAt,
            ResponsesCount = brief?.ResponsesCount,
            Fingerprint = VacancyFingerprint.Compute(d.CompanyName, d.Name, $"hh:{d.Id}"),
            Status = d.Closed && !scoreClosed ? VacancyStatus.Rejected : VacancyStatus.New,
            RejectReason = d.Closed && !scoreClosed ? ClosedReason : null,
        };

    internal static VacancySource HhSource(HhVacancyDetail d, HhVacancyBrief? brief, DateTimeOffset now) => new()
        {
            Source = SourceKind.HeadHunter,
            ExternalId = d.Id.ToString(CultureInfo.InvariantCulture),
            Host = d.Host,
            Url = d.Url,
            LetterRequired = brief?.LetterRequired ?? false,
            HasEmployerTest = brief?.HasTest ?? false,
            RawJson = d.RawJson,
            FetchedAt = now,
        };

    public async Task<IngestResult> IngestHabrAsync(HabrVacancyBrief b, string description, CancellationToken ct)
    {
        var city = b.Locations.FirstOrDefault();
        var draft = new Vacancy
        {
            Title = b.Title,
            CompanyName = b.Company,
            Description = TextUtil.Truncate(description, MaxDescription),
            SalaryFrom = b.SalaryFrom,
            SalaryTo = b.SalaryTo,
            Currency = b.Currency,
            City = city,
            CountryCode = city is not null && habrOptions.BelarusCities.Contains(city, StringComparer.OrdinalIgnoreCase) ? "BY" : null,
            WorkFormats = HabrClient.Formats(b),
            Skills = b.Skills,
            Experience = b.Qualification,
            Url = b.Url,
            PublishedAt = b.PublishedAt,
            Fingerprint = VacancyFingerprint.Compute(b.Company, b.Title, $"habr:{b.Id}"),
        };
        var source = new VacancySource
        {
            Source = SourceKind.Habr,
            ExternalId = b.Id.ToString(CultureInfo.InvariantCulture),
            Host = HabrClient.Host,
            Url = b.Url,
            RawJson = b.RawJson,
            FetchedAt = time.GetUtcNow(),
        };
        return await MergeAsync(draft, source, async ctx => b.CompanyAlias is { } alias ? await CompanyMetadataSources.UpsertHabrAsync(ctx, alias, b.Company, time.GetUtcNow(), ct) : null, ct);
    }

    /// <summary>A Telegram/Threads post the LLM recognized as a .NET vacancy (and that does not link to hh).</summary>
    /// <param name="facts">The vacancy's contacts and page, checked against the sources (<see cref="PostEnricher.Resolve"/>).</param>
    public Task<IngestResult> IngestPostAsync(RawPost post, PostExtraction x, EnrichedPost enriched, PostFacts facts, CancellationToken ct,
        Func<JobFinderDbContext, IngestResult, CancellationToken, Task>? complete = null) =>
        MergeAsync(PostDraft(post, x, enriched, facts), PostSource(post), _ => Task.FromResult<long?>(null), ct, complete);

    internal Task<IngestResult> IngestHhPostAsync(RawPost post, Vacancy draft, VacancySource source, long? companyId,
        Func<JobFinderDbContext, IngestResult, CancellationToken, Task> complete, CancellationToken ct) =>
        MergeAsync(draft, source, async context => companyId is { } hhId ? await CompanyMetadataSources.UpsertHhAsync(context, hhId, draft.CompanyName, null, time.GetUtcNow(), ct) : null,
            ct, async (context, result, token) =>
            {
                await AddPostSourceAsync(context, post, result.VacancyId, token);
                await complete(context, result, token);
            });

    internal async Task<IngestResult> AttachExistingPostAsync(RawPost post, long vacancyId,
        Func<JobFinderDbContext, IngestResult, CancellationToken, Task> complete, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var vacancy = await context.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {vacancyId} FOR UPDATE").SingleAsync(ct);
        await AddPostSourceAsync(context, post, vacancyId, ct);
        var result = new IngestResult(vacancyId, false, true);
        await complete(context, result, ct);
        await captures.SaveAsync(context, vacancy, ct);
        return result;
    }

    private async Task AddPostSourceAsync(JobFinderDbContext context, RawPost post, long vacancyId, CancellationToken ct)
    {
        var source = PostSource(post);
        if (context.VacancySources.Local.Any(row => row.Source == source.Source && row.ExternalId == source.ExternalId)
            || await context.VacancySources.AnyAsync(row => row.Source == source.Source && row.ExternalId == source.ExternalId, ct)) return;
        source.VacancyId = vacancyId;
        context.VacancySources.Add(source);
    }

    /// <summary>
    /// The vacancy a post (with its pages) describes. The model's fields first; a page's JobPosting fills what the model left empty (the
    /// structured salary, the company, remote); contacts are the grounded ones; the URL is the vacancy's own page when one was read.
    /// </summary>
    public static Vacancy PostDraft(RawPost post, PostExtraction x, EnrichedPost enriched, PostFacts facts)
    {
        var formats = WorkFormat.None;
        foreach (var f in x.WorkFormats)
        {
            formats |= f switch
            {
                "office" => WorkFormat.Office,
                "hybrid" => WorkFormat.Hybrid,
                "remote" => WorkFormat.Remote,
                _ => WorkFormat.None,
            };
        }

        var posting = enriched.Posting;
        if (formats == WorkFormat.None && posting?.Remote == true) formats = WorkFormat.Remote;
        var salaryFromPage = x.SalaryFrom is null && x.SalaryTo is null && (posting?.SalaryFrom is not null || posting?.SalaryTo is not null);
        var title = x.Title ?? posting?.Title;
        var company = x.Company ?? posting?.Company;
        return new Vacancy
        {
            Title = TextUtil.Truncate(title ?? "Вакансия .NET", 500),
            CompanyName = company,
            Description = TextUtil.Truncate(enriched.Description, MaxDescription),
            SalaryFrom = salaryFromPage ? posting!.SalaryFrom : x.SalaryFrom,
            SalaryTo = salaryFromPage ? posting!.SalaryTo : x.SalaryTo,
            Currency = Currency.Normalize(salaryFromPage ? posting!.Currency : x.Currency),
            SalaryGross = salaryFromPage ? null : x.SalaryGross,
            City = x.City,
            CountryCode = x.Country?.ToUpperInvariant(),
            WorkFormats = formats,
            EnglishRequired = EnglishLevels.Parse(x.EnglishLevel),
            Skills = x.Skills,
            Experience = x.Seniority == "unknown" ? null : x.Seniority,
            Contacts = facts.Contacts,
            Url = facts.Url ?? post.Url ?? "",
            PublishedAt = post.PublishedAt,
            Fingerprint = VacancyFingerprint.Compute(company, title ?? "", $"{post.Source}:{post.ChatRef}/{post.ExternalId}"),
        };
    }

    /// <summary>
    /// A post read again (its links were not captured before, or its pages changed): the vacancy gains what the new reading found — the
    /// contacts (union), a fuller description, the vacancy page instead of the post as its URL, a salary it did not have. What the user or
    /// hh set stays: an hh vacancy is only given contacts.
    /// </summary>
    /// <param name="replaceable">
    /// Whether the stored address is the reading's to change: one of this post's links, or one today's rules say is no vacancy page (a
    /// site's root, a product page an earlier reading took). An address from another source (Habr, the user) is not.
    /// </param>
    /// <param name="complete">The reading had all its parts (the model answered): only a complete reading may replace what the vacancy has.</param>
    public async Task<bool> RefreshAsync(long vacancyId, Vacancy draft, Func<string, bool> replaceable, CancellationToken ct, bool complete = true,
        Func<JobFinderDbContext, Vacancy, CancellationToken, Task>? publish = null)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var target = await ctx.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {vacancyId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (target is null) return false;
        var hasHh = await ctx.VacancySources.AnyAsync(s => s.VacancyId == vacancyId && s.Source == SourceKind.HeadHunter, ct);
        var soleSource = await ctx.VacancySources.CountAsync(s => s.VacancyId == vacancyId, ct) <= 1;
        var before = (Keys(target.Contacts), target.Description.Length, target.Url);
        Enrich(target, draft, SourceKind.Telegram, hasHh);
        if (soleSource && !hasHh)
        {
            // This post is all the vacancy knows: today's reading of it is the whole truth, and what an earlier reading got wrong goes —
            // pages that were other vacancies (the description is the crawl's, the model plays no part), and, when the reading was
            // complete, a platform's author taken for a recruiter. A vacancy seen elsewhere too keeps the union (other sightings named others).
            if (draft.Description.Length > 0) target.Description = draft.Description;
            if (complete) target.Contacts = draft.Contacts;
        }

        // The address was the post, or chosen from this post's links by an earlier reading: today's reading decides (it knows which page
        // is gone and that a bot is no page). An address from another source (Habr, the user) stays.
        var ours = IsPostUrl(target.Url) || replaceable(target.Url);
        if (!hasHh && ours && draft.Url.Length > 0) target.Url = draft.Url;
        target.CompanyName ??= draft.CompanyName;
        if (publish is not null) await publish(ctx, target, ct);
        var changed = before != (Keys(target.Contacts), target.Description.Length, target.Url);
        if (changed) target.UpdatedAt = time.GetUtcNow();
        await captures.SaveAsync(ctx, target, ct);
        return changed;

        static string Keys(IEnumerable<VacancyContact> contacts) => string.Join("|", contacts.Select(Core.Contacts.ContactExtractor.Key));

        static bool IsPostUrl(string url) =>
            url.Length == 0 || url.StartsWith("https://t.me/", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://telegram.me/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A post that links to an hh vacancy: the hh page is the source of truth, the post becomes one more sighting of it.</summary>
    public async Task<IngestResult> AttachPostToHhAsync(RawPost post, string host, long hhId, CancellationToken ct)
    {
        var external = hhId.ToString(CultureInfo.InvariantCulture);
        long vacancyId;
        await using (var ctx = await db.CreateDbContextAsync(ct))
        {
            vacancyId = await ctx.VacancySources.Where(s => s.Source == SourceKind.HeadHunter && s.ExternalId == external)
                .Select(s => s.VacancyId).FirstOrDefaultAsync(ct);
        }

        var created = false;
        if (vacancyId == 0)
        {
            var detail = await hh.GetVacancyAsync(host, hhId, ct);
            var result = await IngestHhAsync(detail, null, null, ct);
            vacancyId = result.VacancyId;
            created = result.Created;
        }

        await using var save = await db.CreateDbContextAsync(ct);
        await using var transaction = await save.Database.BeginTransactionAsync(ct);
        var vacancy = await save.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {vacancyId} FOR UPDATE").SingleAsync(ct);
        var source = PostSource(post);
        if (!await save.VacancySources.AnyAsync(s => s.Source == source.Source && s.ExternalId == source.ExternalId, ct))
        {
            source.VacancyId = vacancyId;
            save.VacancySources.Add(source);
            await captures.SaveAsync(save, vacancy, ct);
        }

        return new IngestResult(vacancyId, created, true);
    }

    private VacancySource PostSource(RawPost post) => new()
    {
        Source = post.Source,
        ExternalId = $"{post.ChatRef}/{post.ExternalId}",
        Host = post.ChatTitle,
        Url = post.Url ?? "",
        FetchedAt = time.GetUtcNow(),
    };

    private async Task<IngestResult> MergeAsync(Vacancy draft, VacancySource source, Func<JobFinderDbContext, Task<long?>> company,
        CancellationToken ct, Func<JobFinderDbContext, IngestResult, CancellationToken, Task>? complete = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await MergeOnceAsync(draft, source, company, ct, complete);
            }
            catch (DbUpdateException e) when (attempt == 1 && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another worker inserted the same source a moment ago (a channel post linking to a vacancy the hh job is saving):
                // the second pass finds it and merges instead.
                log.Debug($"[ingest] {source.Source}/{source.ExternalId}: concurrent insert, merging");
            }
        }
    }

    private async Task<IngestResult> MergeOnceAsync(Vacancy draft, VacancySource source, Func<JobFinderDbContext, Task<long?>> company,
        CancellationToken ct, Func<JobFinderDbContext, IngestResult, CancellationToken, Task>? complete)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();

        var same = await ctx.VacancySources.Where(s => s.Source == source.Source && s.ExternalId == source.ExternalId).FirstOrDefaultAsync(ct);
        if (same is not null)
        {
            same.FetchedAt = now;
            var vacancy = await ctx.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {same.VacancyId} FOR UPDATE").SingleAsync(ct);
            if (complete is not null) await complete(ctx, new(same.VacancyId, false, false), ct);
            await captures.SaveAsync(ctx, vacancy, ct);
            return new IngestResult(same.VacancyId, false, false);
        }

        if (!draft.Fingerprint.StartsWith('~'))
        {
            var since = now - MergeWindow;
            var existing = await ctx.Vacancies.Where(v => v.Fingerprint == draft.Fingerprint && v.FirstSeenAt >= since)
                .OrderBy(v => v.Id).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                await ctx.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM vacancies WHERE id = {existing.Id} FOR UPDATE", ct);
                await ctx.Entry(existing).ReloadAsync(ct);
                var companyId = await company(ctx);
                var targetHasHh = await ctx.VacancySources.AnyAsync(s => s.VacancyId == existing.Id && s.Source == SourceKind.HeadHunter, ct);
                Enrich(existing, draft, source.Source, targetHasHh);
                existing.CompanyId ??= companyId;
                existing.UpdatedAt = now;
                source.VacancyId = existing.Id;
                ctx.VacancySources.Add(source);
                if (complete is not null) await complete(ctx, new(existing.Id, false, true), ct);
                await captures.SaveAsync(ctx, existing, ct);
                log.Info($"[ingest] {source.Source}/{source.ExternalId} merged into #{existing.Id} {existing.Title}");
                return new IngestResult(existing.Id, false, true);
            }
        }

        draft.CompanyId = await company(ctx);
        draft.AssessmentCaptureId = null;
        draft.FirstSeenAt = now;
        draft.UpdatedAt = now;
        draft.Sources.Add(source);
        ctx.Vacancies.Add(draft);
        await ctx.SaveChangesAsync(ct);
        if (complete is not null) await complete(ctx, new(draft.Id, true, true), ct);
        await captures.SaveAsync(ctx, draft, ct);
        log.Info($"[ingest] new #{draft.Id} [{source.Source}] {draft.Title} — {draft.CompanyName}");
        return new IngestResult(draft.Id, true, true);
    }

    /// <summary>
    /// A second sighting fills what the first lacked. hh data (structured) wins over a post's extraction in both directions: an hh source
    /// overwrites, and once a vacancy has an hh source, other sightings only fill empty fields.
    /// </summary>
    internal static void Enrich(Vacancy target, Vacancy draft, SourceKind source, bool targetHasHh)
    {
        var authoritative = source == SourceKind.HeadHunter;
        target.City ??= draft.City;
        target.CountryCode ??= draft.CountryCode;
        // Every sighting may name another way to reach the employer: the union, the first sighting's order first.
        target.Contacts = Core.Contacts.ContactExtractor.Merge(target.Contacts.Concat(draft.Contacts));
        if (target.Skills.Count == 0) target.Skills = draft.Skills;
        if (targetHasHh && !authoritative) return;

        if (authoritative || target.SalaryFrom is null && target.SalaryTo is null)
        {
            if (draft.SalaryFrom is not null || draft.SalaryTo is not null)
            {
                target.SalaryFrom = draft.SalaryFrom;
                target.SalaryTo = draft.SalaryTo;
                target.Currency = draft.Currency;
                target.SalaryGross = draft.SalaryGross;
            }
        }

        if (authoritative || draft.Description.Length > target.Description.Length * 1.5) target.Description = draft.Description;
        if (authoritative)
        {
            target.Url = draft.Url;
            target.OriginalPostedAt ??= draft.OriginalPostedAt;
            target.ResponsesCount = draft.ResponsesCount ?? target.ResponsesCount;
        }
        if (target.WorkFormats == WorkFormat.None || authoritative && draft.WorkFormats != WorkFormat.None) target.WorkFormats = draft.WorkFormats;
        if (target.EnglishRequired == EnglishLevel.Unknown || authoritative && draft.EnglishRequired != EnglishLevel.Unknown)
            target.EnglishRequired = draft.EnglishRequired;
    }

    public static void ApplyEmployer(Company company, HhEmployer employer, DateTimeOffset now) =>
        CompanyMetadataSources.ApplyEmployer(company, employer, now);
}
