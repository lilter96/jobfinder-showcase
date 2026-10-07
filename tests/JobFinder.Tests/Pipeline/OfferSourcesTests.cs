using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class OfferSourcesTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;
    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Offers.ExecuteDeleteAsync(Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.ApplicationEvents.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == ConversationStore.Key).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }
    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }
    private async Task<OfferSources> OwnerAsync(SaveChangesInterceptor? fault = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault);
        await _host.StartAsync(Ct);
        var publisher = _host.Services.GetRequiredService<ICandidatePublisher>();
        await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true, Summary = "Synthetic experience" }, 0, "synthetic-test", Ct);
        await publisher.PublishFactsAsync(new CandidateFacts(), 0, "synthetic-test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        return _host.Services.GetRequiredService<OfferSources>();
    }
    private async Task<Application> SeedAsync(string company = "Synthetic Company")
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var application = new Application
        {
            Vacancy = new Vacancy { Title = "Senior .NET", CompanyName = company, Description = "Synthetic vacancy", Fingerprint = Guid.NewGuid().ToString("N"),
                Url = "https://example.invalid/offer", Status = VacancyStatus.Applied, UpdatedAt = DateTimeOffset.UtcNow },
            Status = ApplicationStatus.Sent, Stage = ApplicationStage.TechInterview, StageChangedAt = DateTimeOffset.UtcNow,
            Revision = 4, Channel = ApplicationChannel.Email, Target = "synthetic@example.invalid", CoverLetter = "Actually sent historical letter",
            CreatedAt = DateTimeOffset.UtcNow, SentAt = DateTimeOffset.UtcNow,
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        return application;
    }
    private static OfferAnalysis Analysis() => new()
    {
        Company = "Synthetic Company", Verdict = "Synthetic offer assessment", MonthlyAmount = 5000, Currency = "USD", Gross = false,
        Terms = [new OfferTerm { Name = "Work", Value = "Remote" }], Strengths = ["Synthetic strength"], Risks = ["Synthetic risk"],
        CounterTarget = "5500 USD", WalkAway = "4800 USD", CounterScript = "Synthetic counteroffer",
    };
    private async Task<OfferDraftSnapshot> CaptureAsync(OfferSources owner, ConversationState? conversation = null) =>
        (await owner.CaptureAsync("Synthetic Company offers 5000 USD net", Ct, conversation))!;

    [Fact]
    public async Task Captured_publication_is_idempotent_and_offer_funnel_event_and_native_notice_commit_once()
    {
        var owner = await OwnerAsync();
        var application = await SeedAsync();
        var captured = await CaptureAsync(owner);
        var results = await Task.WhenAll(owner.PublishAsync(captured, Analysis(), 5000, Ct), owner.PublishAsync(captured, Analysis(), 5000, Ct));
        Assert.All(results, result => Assert.True(result.Result.Ok));
        Assert.Equal(results[0].OfferId, results[1].OfferId);
        Assert.False((await owner.PublishAsync(captured, Analysis() with { Verdict = "Changed paid result" }, 5000, Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.Offers.SingleAsync(Ct);
        Assert.Equal(captured.PublicationId, source.PublicationId);
        Assert.Equal(application.Id, source.ApplicationId);
        Assert.Equal(5000, source.MonthlyNetUsd);
        Assert.Equal(captured.ProfileVersion, JsonSerializer.Deserialize(source.InputJson!, OfferSourceJsonContext.Default.OfferDraftSnapshot)!.ProfileVersion);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(ApplicationStage.Offer, saved.Stage);
        Assert.Equal(ApplicationStatus.Sent, saved.Status);
        Assert.Equal(application.CoverLetter, saved.CoverLetter);
        Assert.Equal(application.Revision + 1, saved.Revision);
        Assert.Equal(EventKind.StageChanged, (await context.ApplicationEvents.SingleAsync(Ct)).Kind);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("revision")]
    [InlineData("company")]
    [InlineData("source")]
    [InlineData("profile")]
    [InlineData("facts")]
    [InlineData("new-application")]
    [InlineData("unknown")]
    [InlineData("pending")]
    public async Task A_changed_capture_refuses_publication_without_overwriting_funnel_or_sending(string change)
    {
        var owner = await OwnerAsync();
        var application = await SeedAsync();
        var captured = await CaptureAsync(owner);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var app = await context.Applications.Include(row => row.Vacancy).SingleAsync(Ct);
            if (change == "stage") app.Stage = ApplicationStage.Accepted;
            if (change == "revision") app.Revision++;
            if (change == "company") app.Vacancy!.CompanyName = "New Company";
            if (change == "source") app.Vacancy!.Description = "Changed source";
            if (change is "unknown" or "pending") context.EmployerMessages.Add(new EmployerMessage
            {
                Id = Guid.NewGuid(), VacancyId = application.VacancyId, BusinessKey = "synthetic-unknown", InputJson = "{}",
                Status = change == "unknown" ? EmployerMessageStatus.OutcomeUnknown : EmployerMessageStatus.Ready, CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }
        if (change == "new-application") await SeedAsync("Another Company");
        var publisher = _host!.Services.GetRequiredService<ICandidatePublisher>();
        if (change == "profile") await publisher.PublishProfileAsync(captured.Profile with { Summary = "Changed proof" }, captured.ProfileVersion, "synthetic-change", Ct);
        if (change == "facts") await publisher.PublishFactsAsync(new CandidateFacts { Facts = [new CandidateFact { Key = "notice", Question = "Notice period?", Answer = "New fact", Confirmed = true }] }, captured.FactsVersion, "synthetic-change", Ct);
        Assert.False((await owner.PublishAsync(captured, Analysis(), 5000, Ct)).Result.Ok);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.Offers.ToListAsync(Ct));
        Assert.Empty(await read.ApplicationEvents.ToListAsync(Ct));
        Assert.Equal(change == "stage" ? ApplicationStage.Accepted : ApplicationStage.TechInterview,
            (await read.Applications.SingleAsync(row => row.Id == application.Id, Ct)).Stage);
    }

    [Fact]
    public async Task Admission_failure_preserves_offer_dialogue_and_funnel_and_the_same_capture_can_retry()
    {
        var fault = new AdmissionFault();
        var owner = await OwnerAsync(fault);
        await SeedAsync();
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        var expected = new ConversationState(OfferAdvisor.Mode, 0, 0, null, [], null) { Id = Guid.NewGuid() };
        await conversations.SetAsync(expected, Ct);
        var captured = await CaptureAsync(owner, expected);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishAsync(captured, Analysis(), 5000, Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Empty(await context.Offers.ToListAsync(Ct));
            Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
            Assert.Empty(await context.Notifications.ToListAsync(Ct));
            Assert.Equal(ApplicationStage.TechInterview, (await context.Applications.SingleAsync(Ct)).Stage);
        }
        Assert.Equal(ConversationStore.Capture(expected), ConversationStore.Capture((await conversations.GetAsync(Ct))!));
        fault.Active = false;
        Assert.True((await owner.PublishAsync(captured, Analysis(), 5000, Ct)).Result.Ok);
        Assert.Null(await conversations.GetAsync(Ct));
        var newer = expected with { Id = Guid.NewGuid(), Mode = StoryBank.Mode };
        await conversations.SetAsync(newer, Ct);
        Assert.True((await owner.PublishAsync(captured, Analysis(), 5000, Ct)).Result.Ok);
        Assert.Equal(ConversationStore.Capture(newer), ConversationStore.Capture((await conversations.GetAsync(Ct))!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_dialogue_is_never_cleared_by_an_older_offer_or_a_manual_command(bool fromDialogue)
    {
        var owner = await OwnerAsync();
        await SeedAsync();
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        var original = new ConversationState(OfferAdvisor.Mode, 0, 0, null, [], null) { Id = Guid.NewGuid() };
        await conversations.SetAsync(original, Ct);
        var captured = await CaptureAsync(owner, fromDialogue ? original : null);
        var newer = original with { Id = Guid.NewGuid(), Mode = StoryBank.Mode, Subject = "New achievement" };
        await conversations.SetAsync(newer, Ct);
        Assert.Equal(!fromDialogue, (await owner.PublishAsync(captured, Analysis(), 5000, Ct)).Result.Ok);
        Assert.Equal(ConversationStore.Capture(newer), ConversationStore.Capture((await conversations.GetAsync(Ct))!));
    }

    [Fact]
    public async Task Ambiguous_company_matching_retains_the_offer_without_advancing_an_arbitrary_application()
    {
        var owner = await OwnerAsync();
        await SeedAsync();
        await SeedAsync();
        var captured = await CaptureAsync(owner);
        Assert.Null(OfferSources.Guess(captured));
        var result = await owner.PublishAsync(captured, Analysis(), 5000, Ct);
        Assert.True(result.Result.Ok);
        Assert.Null(result.ApplicationId);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Null((await context.Offers.SingleAsync(Ct)).ApplicationId);
        Assert.All(await context.Applications.ToListAsync(Ct), app => Assert.Equal(ApplicationStage.TechInterview, app.Stage));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
    }

    [Fact]
    public async Task Model_company_fallback_keeps_matching_when_raw_text_has_no_company_and_unknown_offers_remain_supported()
    {
        var owner = await OwnerAsync();
        var application = await SeedAsync();
        var captured = (await owner.CaptureAsync("Offer: 5000 USD net", Ct))!;
        Assert.Null(OfferSources.Guess(captured));
        Assert.Equal(application.Id, (await owner.PublishAsync(captured, Analysis(), 5000, Ct)).ApplicationId);
        var unknown = (await owner.CaptureAsync("Another offer: 4500 USD", Ct))!;
        Assert.Null((await owner.PublishAsync(unknown, Analysis() with { Company = "Unknown Company" }, 4500, Ct)).ApplicationId);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Offers.CountAsync(Ct));
        Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
    }

    [Fact]
    public async Task Migration_retains_legacy_offers_and_refuses_removing_new_publication_evidence()
    {
        var owner = await OwnerAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var migrations = context.Database.GetMigrations().ToList();
        var current = migrations.Single(name => name.EndsWith("_VersionedOfferPublication", StringComparison.Ordinal));
        var prior = migrations[migrations.IndexOf(current) - 1];
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(prior, Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO offers (company, raw_text, monthly_amount, currency, created_at) VALUES ('Legacy Company','Original raw offer',4000,'USD',now())", Ct);
        await migrator.MigrateAsync(current, Ct);
        var legacy = await context.Offers.SingleAsync(Ct);
        Assert.Equal("Original raw offer", legacy.RawText);
        Assert.Null(legacy.PublicationId);
        Assert.Null(legacy.InputJson);
        var captured = (await owner.CaptureAsync("Synthetic Company offers 5000 USD net", Ct))!;
        Assert.True((await owner.PublishAsync(captured, Analysis(), 5000, Ct)).Result.Ok);
        await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(prior, Ct));
        Assert.Equal(2, await context.Offers.AsNoTracking().CountAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("vacancy")]
    [InlineData("model-company")]
    public async Task A_damaged_or_inconsistent_snapshot_cannot_certify_an_offer_for_another_source(string damage)
    {
        var owner = await OwnerAsync();
        await SeedAsync();
        var captured = await CaptureAsync(owner);
        var analysis = Analysis();
        if (damage == "profile") captured.Profile.Summary = "Uncaptured candidate proof";
        if (damage == "vacancy") captured.Applications[0].Vacancy.CompanyName = "Other Company";
        if (damage == "model-company") analysis.Company = "Other Company";
        Assert.False((await owner.PublishAsync(captured, analysis, 5000, Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Offers.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task An_explicit_unique_vacancy_title_resolves_same_company_ambiguity_without_first_row_guessing()
    {
        var owner = await OwnerAsync();
        var first = await SeedAsync();
        var second = await SeedAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Vacancies.Where(row => row.Id == second.VacancyId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Title, "Staff Backend"), Ct);
        var captured = (await owner.CaptureAsync("Synthetic Company: offer for Staff Backend, 5000 USD net", Ct))!;
        Assert.Equal(second.Id, OfferSources.Guess(captured)!.Application.ApplicationId);
        Assert.Equal(second.Id, (await owner.PublishAsync(captured, Analysis(), 5000, Ct)).ApplicationId);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStage.TechInterview, (await read.Applications.SingleAsync(row => row.Id == first.Id, Ct)).Stage);
        Assert.Equal(ApplicationStage.Offer, (await read.Applications.SingleAsync(row => row.Id == second.Id, Ct)).Stage);
    }

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context?.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added) == true)
                throw new InvalidOperationException("synthetic offer notice admission failure");
            return ValueTask.FromResult(result);
        }
    }
}
