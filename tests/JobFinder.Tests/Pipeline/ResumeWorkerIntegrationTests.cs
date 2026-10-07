using JobFinder.Core.Domain;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

/// <summary>
/// Which vacancies get a resume built, on a real Postgres (the review is looked up inside the jsonb report): a waiting vacancy whose resume
/// is of the old pipeline or was never reviewed (the models were unreachable) is built again; a reviewed one, an applied one or a
/// dismissed one is not.
/// </summary>
public sealed class ResumeWorkerIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Reviewed = """{"ready":true,"requirements":[],"review":{"score":10,"verdict":"ok","checks":[],"issues":[]},"pages":2,"layout":[],"unshown":[]}""";
    private const string Unreviewed = """{"requirements":[],"pages":2,"layout":[],"unshown":[]}""";

    private async Task<long> SeedAsync(VacancyStatus status, int score, params string?[] reports)
    {
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var v = new Vacancy
        {
            Title = "Senior .NET", Url = "https://t.me/x/" + Guid.NewGuid().ToString("N"), Fingerprint = Guid.NewGuid().ToString("N"), Status = status,
            Decision = Decision.Approval, Score = score, FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Vacancies.Add(v);
        await ctx.SaveChangesAsync(Ct);
        foreach (var report in reports)
            ctx.TailoredResumes.Add(new TailoredResume
            {
                VacancyId = v.Id, Language = "ru", ContentJson = "{}", PdfPath = "data/resumes/x.pdf", FileName = "x.pdf", ReportJson = report,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await ctx.SaveChangesAsync(Ct);
        return v.Id;
    }

    [Fact]
    public async Task A_waiting_vacancy_is_rebuilt_until_a_reviewed_resume_exists()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var none = await SeedAsync(VacancyStatus.Scored, 95);
        var old = await SeedAsync(VacancyStatus.Scored, 94, [null]);
        var unreviewed = await SeedAsync(VacancyStatus.Scored, 93, Unreviewed);
        var reviewed = await SeedAsync(VacancyStatus.Scored, 92, Unreviewed, Reviewed);
        var appliedOld = await SeedAsync(VacancyStatus.Applied, 91, [null]);
        var appliedNone = await SeedAsync(VacancyStatus.Applied, 90);
        var dismissed = await SeedAsync(VacancyStatus.Dismissed, 99);

        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var waiting = await ResumeWorker.Waiting(ctx, DateTimeOffset.UtcNow.AddDays(-1)).ToListAsync(Ct);

        Assert.Equal([none, old, unreviewed, appliedNone], waiting.Where(id => id >= none && id <= dismissed));
        Assert.DoesNotContain(reviewed, waiting);
        Assert.DoesNotContain(appliedOld, waiting);
    }
}
