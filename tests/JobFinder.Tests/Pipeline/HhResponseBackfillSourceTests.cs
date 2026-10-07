using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class HhNegotiationSourceIntegrationTests
{
    private async Task<(HhResponseBackfillSources Owner, HhVacancyReadSnapshot Snapshot, BackfillTime Time)> BackfillAsync()
    {
        var time = new BackfillTime(DateTimeOffset.UtcNow);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.HhResponses.Add(new() { HhVacancyId = 555, State = "response", UpdatedAt = time.GetUtcNow(), SyncedAt = time.GetUtcNow() });
        await context.SaveChangesAsync(Ct);
        var owner = new HhResponseBackfillSources(pg.Db, time);
        return (owner, Assert.Single(await owner.MissingAsync(3, Ct)), time);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Negative_backfill_cache_survives_recreation_but_expires_without_hiding_a_vacancy_forever(bool gone)
    {
        var (owner, snapshot, time) = await BackfillAsync();
        Assert.True(await owner.MarkUnavailableAsync(snapshot, gone, Ct));
        owner = new(pg.Db, time);
        Assert.Empty(await owner.MissingAsync(3, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.HhResponses.ExecuteUpdateAsync(update => update.SetProperty(row => row.SyncedAt, time.GetUtcNow().AddMinutes(30)), Ct);
        Assert.Empty(await owner.MissingAsync(3, Ct));
        time.Now = time.Now.Add(gone ? TimeSpan.FromDays(7) : TimeSpan.FromHours(1));
        Assert.Equal(snapshot, Assert.Single(await owner.MissingAsync(3, Ct)));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task Changed_response_invalidates_suppression_and_refuses_late_unavailable_publication()
    {
        var (owner, snapshot, _) = await BackfillAsync();
        Assert.True(await owner.MarkUnavailableAsync(snapshot, true, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = (await context.Settings.SingleAsync(row => row.Key.StartsWith(HhResponseBackfillSources.UnavailablePrefix), Ct)).Value;
        await context.HhResponses.ExecuteUpdateAsync(update => update.SetProperty(row => row.State, "invitation"), Ct);
        Assert.False(await owner.MarkUnavailableAsync(snapshot, true, Ct));
        var fresh = Assert.Single(await owner.MissingAsync(3, Ct));
        Assert.NotEqual(snapshot.SourceHash, fresh.SourceHash);
        Assert.Equal(saved, (await context.Settings.AsNoTracking().SingleAsync(row => row.Key.StartsWith(HhResponseBackfillSources.UnavailablePrefix), Ct)).Value);
    }

    [Fact]
    public async Task Imported_response_vacancy_is_not_suppressed_by_a_late_read_failure()
    {
        var (owner, snapshot, _) = await BackfillAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Vacancies.Add(new() { Title = "Synthetic restored vacancy", Url = "https://vacancy.invalid/555", Fingerprint = "backfill-restored",
            Sources = [new() { Source = SourceKind.HeadHunter, ExternalId = "555", Url = "https://vacancy.invalid/555" }] });
        await context.SaveChangesAsync(Ct);
        Assert.False(await owner.MarkUnavailableAsync(snapshot, true, Ct));
        Assert.Empty(await owner.MissingAsync(3, Ct));
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith(HhResponseBackfillSources.UnavailablePrefix)).ToListAsync(Ct));
    }

    [Fact]
    public async Task A_format_failure_does_not_shorten_an_existing_known_gone_backoff()
    {
        var (owner, snapshot, time) = await BackfillAsync();
        Assert.True(await owner.MarkUnavailableAsync(snapshot, true, Ct));
        time.Now = time.Now.AddMinutes(1);
        Assert.True(await owner.MarkUnavailableAsync(snapshot, false, Ct));
        time.Now = time.Now.AddHours(2);
        Assert.Empty(await owner.MissingAsync(3, Ct));
    }

    [Theory]
    [InlineData("{\"version\":99}")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Damaged_negative_backfill_state_is_not_overwritten_or_silently_ignored(string json)
    {
        var (owner, snapshot, _) = await BackfillAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = HhResponseBackfillSources.UnavailablePrefix + snapshot.HhVacancyId, Value = json, UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<JsonException>(() => owner.MissingAsync(3, Ct));
        await Assert.ThrowsAsync<JsonException>(() => owner.MarkUnavailableAsync(snapshot, true, Ct));
        Assert.Equal(json, (await context.Settings.SingleAsync(row => row.Key.StartsWith(HhResponseBackfillSources.UnavailablePrefix), Ct)).Value);
    }

    [Fact]
    public async Task Missing_backfill_selection_is_bounded_and_skips_suppressed_rows_before_applying_the_limit()
    {
        var (owner, snapshot, time) = await BackfillAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.HhResponses.AddRange(new HhResponse { HhVacancyId = 556, State = "response", UpdatedAt = time.Now.AddDays(-1) },
            new HhResponse { HhVacancyId = 557, State = "response", UpdatedAt = time.Now.AddDays(-2) });
        await context.SaveChangesAsync(Ct);
        Assert.True(await owner.MarkUnavailableAsync(snapshot, true, Ct));
        Assert.Equal(556, Assert.Single(await owner.MissingAsync(1, Ct)).HhVacancyId);
        Assert.Equal(2, (await owner.MissingAsync(3, Ct)).Count);
    }

    private sealed class BackfillTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
