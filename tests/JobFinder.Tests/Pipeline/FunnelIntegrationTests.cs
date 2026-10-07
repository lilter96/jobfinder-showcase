using JobFinder.Core.Domain;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class FunnelIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private IHost? _contactHost;
    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.CapturesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_contactHost is null) return;
        try { await _contactHost.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _contactHost.Dispose(); }
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FunnelService Funnel()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        return new FunnelService(null!, TimeProvider.System, NullLogger<FunnelService>.Instance);
    }

    private async Task<long> SentApplicationAsync(string company)
    {
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var v = new Vacancy
        {
            Title = "Senior .NET", CompanyName = company, Url = $"https://example.com/{Guid.NewGuid():N}", Fingerprint = Guid.NewGuid().ToString("N"),
            Status = VacancyStatus.Applied, FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Vacancies.Add(v);
        var app = new Application
        {
            Vacancy = v, Channel = ApplicationChannel.Manual, Mode = ApplicationMode.Approved, Status = ApplicationStatus.Sent,
            CreatedAt = DateTimeOffset.UtcNow, SentAt = DateTimeOffset.UtcNow,
        };
        ctx.Applications.Add(app);
        Funnel().Entered(ctx, app, EventChannel.Manual);
        await ctx.SaveChangesAsync(Ct);
        return app.Id;
    }

    [Fact]
    public async Task Automatic_signals_only_move_forward_and_outcomes_always_apply()
    {
        var funnel = Funnel();
        var id = await SentApplicationAsync("Acme");
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var app = await ctx.Applications.FirstAsync(a => a.Id == id, Ct);
        Assert.Equal(ApplicationStage.Sent, app.Stage);

        Assert.True(funnel.Advance(ctx, app, ApplicationStage.Replied, EventChannel.Email, "вопрос", manual: false));
        Assert.False(funnel.Advance(ctx, app, ApplicationStage.Viewed, EventChannel.HeadHunter, null, manual: false));
        Assert.Equal(ApplicationStage.Replied, app.Stage);

        Assert.True(funnel.Advance(ctx, app, ApplicationStage.Rejected, EventChannel.Email, "отказ", manual: false));
        Assert.False(funnel.Advance(ctx, app, ApplicationStage.Replied, EventChannel.Email, null, manual: false));
        // A rejected application can still turn into an offer; the user's own choice applies as given.
        Assert.True(funnel.Advance(ctx, app, ApplicationStage.Offer, EventChannel.Email, null, manual: false));
        Assert.True(funnel.Advance(ctx, app, ApplicationStage.Viewed, EventChannel.Manual, "ошибся", manual: true));
        Assert.Equal(ApplicationStage.Viewed, app.Stage);
        await ctx.SaveChangesAsync(Ct);

        var events = await ctx.ApplicationEvents.Where(e => e.ApplicationId == id).OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal(EventKind.MessageOut, events[0].Kind); // the entry: the letter that was sent
        Assert.Equal(4, events.Count(e => e.Kind == EventKind.StageChanged));
    }

    // Skip and mark-as-sent touch only the database: the LLM, profile and mail dependencies stay unused.
    private JobFinder.Pipeline.Actions.ApprovalService Approvals() => new(pg.Db, null!, null!, null!, Funnel(),
        new JobFinder.Pipeline.Settings.RuntimeSettings(pg.Db, new JobFinder.Core.Options.DecisionOptions(), TimeProvider.System, pg.Policies), new JobTriggers(), TimeProvider.System,
        NullLogger<JobFinder.Pipeline.Actions.ApprovalService>.Instance, null!, null!, null!, null!, null!);

    [Fact]
    public async Task A_sent_response_is_neither_skipped_nor_doubled()
    {
        var approvals = Approvals();
        var id = await SentApplicationAsync("Gamma");
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var vacancyId = (await ctx.Applications.FirstAsync(a => a.Id == id, Ct)).VacancyId;

        Assert.False((await approvals.DismissAsync(vacancyId, Ct)).Ok);
        Assert.False((await approvals.MarkManualAsync(vacancyId, Ct)).Ok);
        Assert.Equal(VacancyStatus.Applied, (await ctx.Vacancies.AsNoTracking().FirstAsync(v => v.Id == vacancyId, Ct)).Status);
        Assert.Equal(1, await ctx.Applications.CountAsync(a => a.VacancyId == vacancyId, Ct));
    }

    [Fact]
    public async Task Finishing_a_response_on_the_site_completes_the_unfinished_one()
    {
        var approvals = Approvals();
        long vacancyId, appId;
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            var v = new Vacancy
            {
                Title = "Senior .NET", CompanyName = "Delta", Url = $"https://example.com/{Guid.NewGuid():N}", Fingerprint = Guid.NewGuid().ToString("N"),
                Status = VacancyStatus.Scored, FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            var app = new Application
            {
                Vacancy = v, Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.Approved, Status = ApplicationStatus.NeedsManual,
                Error = "тест работодателя", CreatedAt = DateTimeOffset.UtcNow,
            };
            ctx.Applications.Add(app);
            await ctx.SaveChangesAsync(Ct);
            (vacancyId, appId) = (v.Id, app.Id);
        }

        Assert.True((await approvals.MarkManualAsync(vacancyId, Ct)).Ok);
        await using var check = await pg.Db.CreateDbContextAsync(Ct);
        var apps = await check.Applications.Where(a => a.VacancyId == vacancyId).ToListAsync(Ct);
        Assert.Equal(appId, Assert.Single(apps).Id);
        Assert.Equal(ApplicationStatus.Sent, apps[0].Status);
        Assert.Equal(ApplicationStage.Sent, apps[0].Stage);
        Assert.Null(apps[0].Error);
    }

    [Fact]
    public async Task Interview_stage_opens_one_interview_and_a_time_updates_it()
    {
        using var host = await PostProcessingTestHost.BuildAsync(pg, consumer: false);
        await host.StartAsync(Ct);
        var transitions = host.Services.GetRequiredService<InterviewTransitions>();
        var id = await SentApplicationAsync("Beta");
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(Ct);
            await using var scope = host.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(ctx);
            var app = await ctx.Applications.FirstAsync(a => a.Id == id, Ct);
            await transitions.AdvanceAsync(ctx, outbox, app, ApplicationStage.HrScreen, EventChannel.Telegram, "приглашение", manual: false, Ct);
            await outbox.SaveChangesAndFlushMessagesAsync(Ct);
        }

        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(Ct);
            await using var scope = host.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(ctx);
            var app = await ctx.Applications.FirstAsync(a => a.Id == id, Ct);
            // The same invitation again, now with a time: no second interview, the time is set.
            await transitions.AdvanceAsync(ctx, outbox, app, ApplicationStage.HrScreen, EventChannel.Email, "время", manual: false, Ct, at, InterviewKind.HrScreen);
            await outbox.SaveChangesAndFlushMessagesAsync(Ct);
            var interviews = await ctx.Interviews.Where(i => i.ApplicationId == id).ToListAsync(Ct);
            Assert.Single(interviews);
            Assert.Equal(at, interviews[0].ScheduledAt);
        }
        await host.StopAsync(Ct);
    }

    private static List<VacancyContact> Contacts(params string[] raw) => [.. raw.Select(JobFinder.Core.Contacts.ContactExtractor.Classify).OfType<VacancyContact>()];

    [Fact]
    public async Task Contact_registry_counts_sightings_instead_of_duplicating()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var registry = await RegistryAsync();
        var ct = Ct;
        await registry.FromPostAsync("ООО «Гамма»", null, Contacts("@HR_Gamma", "hr@gamma.by"), "https://t.me/jobs/1", ct);
        await registry.FromPostAsync("Гамма", null, Contacts("t.me/hr_gamma"), "https://t.me/jobs/2", ct);
        await registry.FromPostAsync("Гамма", null, Contacts("@hr_gamma", "https://t.me/jobs_bot?start=1"), "https://t.me/jobs/3", ct);

        await using var ctx = await pg.Db.CreateDbContextAsync(ct);
        var rows = await ctx.CompanyContacts.Where(c => c.CompanyName!.Contains("Гамма")).ToListAsync(ct);
        Assert.Equal(2, rows.Count); // a bot link is not a person: not in the registry
        Assert.Single(rows.Select(r => r.CompanyKey).Distinct());
        // "@HR_Gamma", "t.me/hr_gamma", "@hr_gamma": one person seen three times.
        Assert.Equal(3, rows.Single(r => r.Kind == ContactKind.Telegram).SeenCount);
    }

    /// <summary>Telegram is not connected in tests: names are not resolved, description handles are kept as unchecked.</summary>
    private async Task<ContactRegistry> RegistryAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var telegram = new JobFinder.Integrations.Telegram.TelegramUserClient(new JobFinder.Integrations.Telegram.TelegramOptions(), new NoTelegramSession(), new MemoryDocumentStore(),
            NullLogger<JobFinder.Integrations.Telegram.TelegramUserClient>.Instance);
        await pg.SeedPoliciesAsync();
        _contactHost = await PostProcessingTestHost.BuildAsync(pg, false);
        await _contactHost.StartAsync(Ct);
        return new ContactRegistry(pg.Db, telegram,
            new JobFinder.Pipeline.Ingest.TelegramContactVerifier(pg.Db, telegram, TimeProvider.System, NullLogger<JobFinder.Pipeline.Ingest.TelegramContactVerifier>.Instance),
            _contactHost.Services.GetRequiredService<ContactSources>(), NullLogger<ContactRegistry>.Instance);
    }

    [Fact]
    public async Task A_recruiter_found_by_hand_or_in_the_description_belongs_to_the_vacancys_company()
    {
        var registry = await RegistryAsync();
        var appId = await SentApplicationAsync("ООО «Дельта Софт»");
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        var v = await ctx.Vacancies.FirstAsync(x => x.Applications.Any(a => a.Id == appId), Ct);

        var linked = await registry.LinkAsync(v.Id, "https://t.me/Delta_HR", null, Ct);
        Assert.Equal(("Senior .NET — ООО «Дельта Софт»", "@delta_hr", ContactKind.Telegram), (linked!.Vacancy, linked.Value, linked.Kind));
        Assert.Null(await registry.LinkAsync(v.Id, "просто текст", null, Ct));
        v.Description = "Резюме присылайте в Telegram @anna_delta или пишите боту @delta_jobs_bot, канал t.me/joinchat/xyz.";
        Assert.Equal(1, await registry.FromDescriptionAsync(v, Ct));

        var contacts = await registry.ForCompanyAsync("Дельта Софт", Ct);
        Assert.Equal(["@anna_delta", "@delta_hr"], contacts.Select(c => c.Value).Order());
        Assert.Equal(ContactRegistry.ManualSource, contacts.Single(c => c.Value == "@delta_hr").Source);
        Assert.Contains("не проверен", contacts.Single(c => c.Value == "@anna_delta").Role);

        // A wrong contact goes, unless a conversation was sent to it from here.
        Assert.True((await registry.RemoveAsync(ContactEditSnapshot.Capture(contacts.Single(c => c.Value == "@anna_delta")), Ct)).Ok);
        ctx.Outreaches.Add(new Outreach { VacancyId = v.Id, ContactId = linked.ContactId, Text = "привет", Status = OutreachStatus.Sent, CreatedAt = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync(Ct);
        Assert.False((await registry.RemoveAsync(ContactEditSnapshot.Capture(contacts.Single(c => c.Id == linked.ContactId)), Ct)).Ok);
        Assert.Equal(["@delta_hr"], (await registry.ForCompanyAsync("Дельта Софт", Ct)).Select(c => c.Value));
    }
}
