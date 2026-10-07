using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed record CompanyPreferenceSnapshot
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public long? HhId { get; set; }
    public string? HabrAlias { get; set; }
    public bool Watch { get; set; }
    public bool Blacklisted { get; set; }

    public static CompanyPreferenceSnapshot Capture(Company company) => new()
    {
        Id = company.Id, Revision = company.PreferencesRevision, HhId = company.HhId, HabrAlias = company.HabrAlias,
        Watch = company.Watch, Blacklisted = company.Blacklisted,
    };

    public bool Matches(Company company) => this == Capture(company);
}

public sealed class CompanyPreferences(IDbContextFactory<JobFinderDbContext> db, Notifier notifier)
{
    public async Task<ActionResult> SetAsync(CompanyPreferenceSnapshot expected, bool watch, bool blacklisted, CancellationToken ct)
    {
        if (expected is null || expected.Id <= 0 || expected.Revision < 0 || expected.Revision == long.MaxValue) return Conflict();
        using var stage = Telemetry.StartStage("publish", "company-preferences").Tag("company.id", expected.Id).Tag("revision", expected.Revision);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var company = await context.Companies.FromSqlInterpolated($"SELECT * FROM companies WHERE id = {expected.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (company is null || !expected.Matches(company)) return Conflict();
        if (company.Watch == watch && company.Blacklisted == blacklisted) return ActionResult.Done("без изменений");
        company.Watch = watch;
        company.Blacklisted = blacklisted;
        company.PreferencesRevision++;
        notifier.Add(context, NotificationKind.Info, $"Компания {TextUtil.EscapeHtml(company.Name)}: избранное — {(watch ? "да" : "нет")}, чёрный список — {(blacklisted ? "да" : "нет")}.");
        await notifier.SaveAndFlushAsync(context, ct);
        return ActionResult.Done("настройки компании сохранены");
    }

    private static ActionResult Conflict() => ActionResult.Fail("настройки компании изменились или компания недоступна; правка не сохранена — обновите строку явно");
}
