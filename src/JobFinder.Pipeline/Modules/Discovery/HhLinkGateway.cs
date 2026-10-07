using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Ingest;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed record HhLinkReadResult(long? VacancyId, string Summary);
public sealed record HhLinkAccess(bool ViaAccount, DateTimeOffset? PausedUntil);

public interface IHhLinkGateway
{
    Task<HhLinkAccess> AccessAsync(CancellationToken ct);
    Task<HhLinkReadResult> ReadAsync(long hhId, CancellationToken ct);
    Task<IReadOnlyList<LinkedContact>> LinkAsync(long vacancyId, IReadOnlyList<string> contacts, CancellationToken ct);
}

public sealed class HhLinkGateway(IDbContextFactory<JobFinderDbContext> db, HhOptions options, HhClient hh, HhApplier applier,
    PoliteHttp http, VacancyIngestor ingestor, ContactRegistry registry, ServerOwnership ownership, ILogger<HhLinkGateway> log) : IHhLinkGateway
{
    public async Task<HhLinkAccess> AccessAsync(CancellationToken ct) =>
        await applier.HasSessionAsync(ct) && applier.AccountReadsLeft > 0 ? new(true, null) : new(false, await http.PausedUntilAsync("hh.ru", ct));

    public async Task<HhLinkReadResult> ReadAsync(long hhId, CancellationToken ct)
    {
        ownership.EnsureOwned();
        var external = hhId.ToString(CultureInfo.InvariantCulture);
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            var known = await context.VacancySources.AsNoTracking().Where(source => source.Source == SourceKind.HeadHunter && source.ExternalId == external)
                .Select(source => source.Vacancy).FirstOrDefaultAsync(ct);
            if (known is not null) return new(known.Id, $"#{known.Id} {Title(known.Title, known.CompanyName)}: уже есть, {State(known)}");
        }
        try
        {
            HhVacancyDetail? detail;
            try
            {
                ownership.EnsureOwned();
                detail = await applier.ReadVacancyAsync(hhId, ct);
            }
            catch (Exception error) when (error is Microsoft.Playwright.PlaywrightException or TimeoutException or FormatException)
            {
                log.Warn($"[hh-links] {hhId} under the session: {error.Message}; the anonymous reader goes next");
                detail = null;
            }
            if (detail is null)
            {
                ownership.EnsureOwned();
                detail = await hh.GetVacancyAsync(options.ApplyHost, hhId, ct, ScrapePriority.High);
            }
            var result = await ingestor.IngestHhAsync(detail, null, null, ct);
            return new(result.VacancyId, $"#{result.VacancyId} {Title(detail.Name, detail.CompanyName)}: " +
                (detail.Closed ? "вакансия закрыта или в архиве" : "добавлена, оцениваю"));
        }
        catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            return new(null, $"{hhId}: такой вакансии на hh нет");
        }
        catch (FormatException error)
        {
            log.Warn($"[hh-links] {hhId}: {error.Message}");
            return new(null, $"{hhId}: страница не разобралась, добавьте вручную");
        }
    }

    public async Task<IReadOnlyList<LinkedContact>> LinkAsync(long vacancyId, IReadOnlyList<string> contacts, CancellationToken ct)
    {
        var linked = new List<LinkedContact>();
        foreach (var contact in contacts)
        {
            ownership.EnsureOwned();
            if (await registry.LinkAsync(vacancyId, contact, null, ct) is { } result) linked.Add(result);
        }
        return linked;
    }

    private static string Title(string title, string? company) => string.IsNullOrWhiteSpace(company) ? title : $"{title} — {company}";

    private static string State(Vacancy vacancy) => vacancy.Status switch
    {
        VacancyStatus.New => "ждёт оценки",
        VacancyStatus.Rejected => "отсеяна: " + (vacancy.RejectReason ?? "фильтр"),
        VacancyStatus.Scored => $"оценка {vacancy.Score}, " + (vacancy.Decision switch
        {
            Decision.AutoApply => "в очереди на автоотклик", Decision.Approval => "ждёт вашего одобрения", _ => "ниже порога",
        }),
        VacancyStatus.Applied => "отклик отправлен", VacancyStatus.Dismissed => "вы её пропустили",
        _ => "обработка не удалась, перезапустите на дашборде",
    };
}
