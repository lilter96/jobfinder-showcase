using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class CompanyPreferencesTests
{
    [Fact]
    public async Task Collected_metadata_preserves_changed_preferences_and_refuses_a_newer_observation()
    {
        var source = await SourceAsync();
        var captured = await CompanyMetadataSources.CaptureAsync(pg.Db, source.HhId!.Value, Ct);
        var owner = await OwnerAsync();
        // Capture the displayed value from storage, as an actual caller does (PostgreSQL timestamp precision).
        Assert.True((await owner.SetAsync(CompanyPreferenceSnapshot.Capture(await ReadAsync(source.Id)), true, true, Ct)).Ok);
        var employer = new HhEmployer(source.HhId.Value, "Collected synthetic employer", "https://example.invalid", "Minsk", "small",
            ["Software"], "Synthetic .NET backend services", 4m, true, 12);
        var tags = new Dictionary<string, System.Text.RegularExpressions.Regex[]> { ["backend"] = [TextUtil.WordPrefix("backend")] };
        Assert.True(await CompanyMetadataSources.PublishCollectedAsync(pg.Db, captured, source.Name, 7, employer, tags, DateTimeOffset.UtcNow, Ct));
        var saved = await ReadAsync(source.Id);
        Assert.True(saved.Watch);
        Assert.True(saved.Blacklisted);
        Assert.Equal(1, saved.PreferencesRevision);
        Assert.Equal(7, saved.DotnetVacancies);
        Assert.Contains("backend", saved.Tags);
        Assert.False(await CompanyMetadataSources.PublishCollectedAsync(pg.Db, captured, "Old observation", 99, null, tags, DateTimeOffset.UtcNow, Ct));
        Assert.Equal(7, (await ReadAsync(source.Id)).DotnetVacancies);
    }

    [Fact]
    public async Task Search_absence_cannot_clear_newer_metadata_or_a_company_created_after_capture()
    {
        var original = await SourceAsync();
        var cohort = await CompanyMetadataSources.CaptureAbsenceAsync(pg.Db, Ct);
        var current = await CompanyMetadataSources.CaptureAsync(pg.Db, original.HhId!.Value, Ct);
        Assert.True(await CompanyMetadataSources.PublishCollectedAsync(pg.Db, current, original.Name, 8, null, [], DateTimeOffset.UtcNow, Ct));
        var fresh = await CompanyMetadataSources.CaptureAsync(pg.Db, 987655, Ct);
        Assert.True(await CompanyMetadataSources.PublishCollectedAsync(pg.Db, fresh, "New synthetic employer", 5, null, [], DateTimeOffset.UtcNow, Ct));
        Assert.Equal(0, await CompanyMetadataSources.PublishAbsenceAsync(pg.Db, cohort, [], DateTimeOffset.UtcNow, Ct));
        Assert.Equal(8, (await ReadAsync(original.Id)).DotnetVacancies);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(5, (await context.Companies.SingleAsync(row => row.HhId == 987655, Ct)).DotnetVacancies);
        var next = await CompanyMetadataSources.CaptureAbsenceAsync(pg.Db, Ct);
        Assert.Equal(1, await CompanyMetadataSources.PublishAbsenceAsync(pg.Db, next, [987655], DateTimeOffset.UtcNow, Ct));
        Assert.Equal(0, (await ReadAsync(original.Id)).DotnetVacancies);
    }
}
