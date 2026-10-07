using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Ingest;

public sealed record ReparseLine(long VacancyId, string Title, int Before, int After, int Pages, IReadOnlyList<string> NewContacts, IReadOnlyList<string> Failures);

public sealed class PostReparser(IDbContextFactory<JobFinderDbContext> db, PostProcessingRequests requests)
{
    public async Task<List<ReparseLine>> ReparseAsync(IReadOnlyCollection<long> vacancyIds, int limit, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        IReadOnlyCollection<long> wanted = vacancyIds.Count > 0 ? vacancyIds
            : await context.Database.SqlQuery<long>($"SELECT id AS \"Value\" FROM vacancies WHERE jsonb_array_length(contacts) = 0").ToListAsync(ct);
        var posts = await context.RawPosts.AsNoTracking().Where(post => post.Source == SourceKind.Telegram && post.Status == RawPostStatus.Vacancy
            && post.VacancyId != null && wanted.Contains(post.VacancyId.Value)).OrderByDescending(post => post.VacancyId).Take(limit).ToListAsync(ct);
        var ids = posts.Select(post => post.VacancyId!.Value).ToList();
        var titles = await context.Vacancies.Where(vacancy => ids.Contains(vacancy.Id)).ToDictionaryAsync(vacancy => vacancy.Id, vacancy => vacancy.Title, ct);
        var report = new List<ReparseLine>();
        foreach (var post in posts)
        {
            ct.ThrowIfCancellationRequested();
            if (!titles.TryGetValue(post.VacancyId!.Value, out var title) || await requests.RequestAsync(post.Id, ct, refresh: true) is not { } id) continue;
            var result = await requests.WaitAsync(id, ct);
            report.Add(new(post.VacancyId.Value, title, result.Before, result.After, result.Pages, result.NewContacts, result.Failures));
        }
        return report;
    }

    public static string Report(IReadOnlyList<ReparseLine> lines)
    {
        if (lines.Count == 0) return "Нечего перечитывать: у всех вакансий из Telegram уже есть контакты.";
        var report = new StringBuilder($"🔁 <b>Перечитано постов: {lines.Count}</b>, контакты найдены у {lines.Count(line => line.After > 0)}\n");
        foreach (var line in lines)
        {
            report.Append("\n#").Append(line.VacancyId).Append(' ').Append(TextUtil.EscapeHtml(TextUtil.Truncate(line.Title, 60)))
                .Append(": контакты ").Append(line.Before).Append(" → ").Append(line.After).Append(", страниц ").Append(line.Pages);
            if (line.NewContacts.Count > 0) report.Append("\n  + ").Append(TextUtil.EscapeHtml(string.Join(", ", line.NewContacts.Take(5))));
            if (line.Failures.Count > 0) report.Append("\n  ⚠️ ").Append(TextUtil.EscapeHtml(TextUtil.Truncate(string.Join("; ", line.Failures), 200)));
        }
        return TextUtil.Truncate(report.ToString(), 3900);
    }
}
