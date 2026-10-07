using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

/// <summary>A dry run checks a response and holds it; it must never use the response up (seen live 2026-09-28, vacancy #347).</summary>
public sealed class DryRunIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.CapturesAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private RuntimeSettings Settings() => new(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies);

    // Apply, skip and mark-as-sent on a held response touch only the database: the LLM, profile and mail dependencies stay unused.
    private ApprovalService Approvals()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        return new ApprovalService(pg.Db, null!, null!, null!, new FunnelService(null!, TimeProvider.System, NullLogger<FunnelService>.Instance),
            Settings(), new JobTriggers(), TimeProvider.System, NullLogger<ApprovalService>.Instance, null!, null!, null!, null!, null!);
    }

    private async Task<(long VacancyId, long AppId)> HeldAsync(DateTimeOffset createdAt, ApplicationMode mode = ApplicationMode.Auto,
        ApplicationStatus status = ApplicationStatus.DryRun)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var v = new Vacancy
        {
            Title = "C#/.NET-разработчик", CompanyName = "Agency", Url = $"https://rabota.by/vacancy/{Guid.NewGuid():N}", Fingerprint = Guid.NewGuid().ToString("N"),
            Status = VacancyStatus.Scored, Decision = Decision.AutoApply, FirstSeenAt = createdAt, UpdatedAt = createdAt,
        };
        var app = new Application
        {
            Vacancy = v, Channel = ApplicationChannel.HeadHunter, Mode = mode, Status = status, CoverLetter = "письмо",
            Error = status == ApplicationStatus.DryRun ? "dry-run: likely a quick response" : null, CreatedAt = createdAt,
        };
        ctx.Applications.Add(app);
        await ctx.SaveChangesAsync(Ct);
        return (v.Id, app.Id);
    }

    private async Task<Application> AppAsync(long id)
    {
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        return await ctx.Applications.AsNoTracking().FirstAsync(a => a.Id == id, Ct);
    }

    [Fact]
    public async Task Turning_dry_run_off_queues_fresh_held_responses_and_cancels_stale_ones()
    {
        var now = DateTimeOffset.UtcNow;
        var (_, fresh) = await HeldAsync(now.AddDays(-1));
        var (_, edge) = await HeldAsync(now.AddDays(-ApplyWorker.DryRunHoldDays).AddMinutes(5));
        var (_, stale) = await HeldAsync(now.AddDays(-ApplyWorker.DryRunHoldDays - 1));
        var (_, pending) = await HeldAsync(now, status: ApplicationStatus.Pending);
        var (_, sent) = await HeldAsync(now, status: ApplicationStatus.Sent);
        var infoBefore = await CountInfoAsync();

        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            var (released, expired) = await ApplyWorker.ReleaseHeldAsync(ctx, new Notifier(pg.Db, TimeProvider.System, await pg.NotificationsAsync()), now, Ct);
            Assert.True(released >= 2);
            Assert.True(expired >= 1);
        }

        Assert.Equal(ApplicationStatus.Pending, (await AppAsync(fresh)).Status);
        Assert.Null((await AppAsync(fresh)).Error);
        Assert.Equal(ApplicationStatus.Pending, (await AppAsync(edge)).Status);
        var cancelled = await AppAsync(stale);
        Assert.Equal(ApplicationStatus.Cancelled, cancelled.Status);
        Assert.StartsWith("dry-run:", cancelled.Error);
        Assert.Equal(ApplicationStatus.Pending, (await AppAsync(pending)).Status);
        Assert.Equal(ApplicationStatus.Sent, (await AppAsync(sent)).Status);
        Assert.Equal(infoBefore + 1, await CountInfoAsync());

        // Nothing left to release: a second pass changes nothing and says nothing.
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Equal((0, 0), await ApplyWorker.ReleaseHeldAsync(ctx, new Notifier(pg.Db, TimeProvider.System, await pg.NotificationsAsync()), now, Ct));
        Assert.Equal(infoBefore + 1, await CountInfoAsync());
    }

    private async Task<int> CountInfoAsync()
    {
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        return await ctx.Notifications.CountAsync(n => n.Kind == NotificationKind.Info && n.Text.Contains("Dry-run"), Ct);
    }

    [Fact]
    public async Task A_held_response_keeps_its_place_in_the_daily_auto_limit_and_a_cancelled_one_frees_it()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var (_, held) = await HeldAsync(DateTimeOffset.UtcNow);
        var (_, dead) = await HeldAsync(DateTimeOffset.UtcNow, status: ApplicationStatus.Cancelled);
        var (_, approved) = await HeldAsync(DateTimeOffset.UtcNow, ApplicationMode.Approved);

        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var counted = await ctx.Applications.Where(ApplyWorker.CountsTowardAutoLimit(since)).Select(a => a.Id).ToListAsync(Ct);
        Assert.Contains(held, counted);
        Assert.DoesNotContain(dead, counted);
        Assert.DoesNotContain(approved, counted);
    }

    [Fact]
    public async Task Pressing_apply_on_a_held_response_does_not_queue_a_second_one()
    {
        var (vacancyId, appId) = await HeldAsync(DateTimeOffset.UtcNow, ApplicationMode.Approved);
        var result = await Approvals().ApplyAsync(vacancyId, Ct);

        Assert.False(result.Ok);
        Assert.Contains("dry-run", result.Message);
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(appId, Assert.Single(await ctx.Applications.Where(a => a.VacancyId == vacancyId).ToListAsync(Ct)).Id);
    }

    [Fact]
    public async Task Skipping_a_vacancy_cancels_its_held_response()
    {
        var (vacancyId, appId) = await HeldAsync(DateTimeOffset.UtcNow);
        Assert.True((await Approvals().DismissAsync(vacancyId, Ct)).Ok);
        Assert.Equal(ApplicationStatus.Cancelled, (await AppAsync(appId)).Status);
    }

    [Fact]
    public async Task Marking_as_sent_by_hand_completes_the_held_response_instead_of_adding_one()
    {
        var (vacancyId, appId) = await HeldAsync(DateTimeOffset.UtcNow, ApplicationMode.Approved);
        Assert.True((await Approvals().MarkManualAsync(vacancyId, Ct)).Ok);

        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var app = Assert.Single(await ctx.Applications.Where(a => a.VacancyId == vacancyId).ToListAsync(Ct));
        Assert.Equal(appId, app.Id);
        Assert.Equal(ApplicationStatus.Sent, app.Status);
    }

    [Fact]
    public async Task A_new_letter_reaches_the_held_response()
    {
        var (vacancyId, appId) = await HeldAsync(DateTimeOffset.UtcNow, ApplicationMode.Approved);
        Assert.True((await Approvals().SetLetterAsync(vacancyId, "  новое письмо ", await Approvals().LetterSnapshotAsync(vacancyId, Ct), Ct)).Ok);
        Assert.Equal("новое письмо", (await AppAsync(appId)).CoverLetter);
    }

    [Fact]
    public async Task A_response_found_on_the_site_cancels_the_held_one()
    {
        var (vacancyId, appId) = await HeldAsync(DateTimeOffset.UtcNow);
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            var v = await ctx.Vacancies.Include(x => x.Applications).FirstAsync(x => x.Id == vacancyId, Ct);
            HhHistory.Link(ctx, v, new HhResponse { HhVacancyId = 137863928, State = "response" }, ApplicationStage.Sent, DateTimeOffset.UtcNow);
            await ctx.SaveChangesAsync(Ct);
        }

        var held = await AppAsync(appId);
        Assert.Equal(ApplicationStatus.Cancelled, held.Status);
        Assert.Equal("вы уже откликнулись на сайте", held.Error);
    }
}
