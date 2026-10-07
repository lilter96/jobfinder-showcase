using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Time;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class OutreachScanTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Clock _time = new();
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Outreaches.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.CompanyContacts.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith(OutreachScans.Prefix)).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        _host = await PostProcessingTestHost.BuildAsync(pg, false, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);
        });
        await _host.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { _host.Dispose(); }
        }
    }

    private OutreachScans Owner => _host!.Services.GetRequiredService<OutreachScans>();

    private async Task<Vacancy> SeedAsync(int score = 90)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = new Vacancy { Title = "Synthetic backend", CompanyName = "Example", Description = "Captured synthetic vacancy", Score = score,
            Url = "https://vacancy.invalid/1", Fingerprint = Guid.NewGuid().ToString("N"), FirstSeenAt = _time.Now };
        vacancy.Applications.Add(new() { Status = ApplicationStatus.Sent, Stage = ApplicationStage.Sent, SentAt = _time.Now,
            CreatedAt = _time.Now, Channel = ApplicationChannel.HeadHunter });
        context.Vacancies.Add(vacancy);
        await context.SaveChangesAsync(Ct);
        return vacancy;
    }

    private async Task<OutreachScanState> StateAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var json = (await context.Settings.SingleAsync(row => row.Key == OutreachScans.Prefix + id, Ct)).Value;
        return JsonSerializer.Deserialize(json, OutreachScanJsonContext.Default.OutreachScanState)!;
    }

    [Fact]
    public async Task Concurrent_reservation_is_bounded_ranked_deduplicated_and_survives_owner_recreation()
    {
        var first = await SeedAsync(95);
        var second = await SeedAsync(90);
        await SeedAsync(85);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Applications.Add(new() { VacancyId = first.Id, Status = ApplicationStatus.Sent, Stage = ApplicationStage.Viewed, SentAt = _time.Now, CreatedAt = _time.Now });
            await context.SaveChangesAsync(Ct);
        }
        var batches = await Task.WhenAll(Owner.ReserveAsync(80, 1, Ct), Owner.ReserveAsync(80, 1, Ct));
        var tickets = batches.SelectMany(batch => batch.Tickets).ToList();
        Assert.Equal(2, tickets.Count);
        Assert.Equal(new[] { first.Id, second.Id }, tickets.Select(ticket => ticket.VacancyId).Order());
        Assert.Equal(2, tickets.Select(ticket => ticket.Token).Distinct().Count());
        var recreated = ActivatorUtilities.CreateInstance<OutreachScans>(_host!.Services);
        Assert.Single((await recreated.ReserveAsync(80, 1, Ct)).Tickets);
        Assert.Empty((await recreated.ReserveAsync(80, 1, Ct)).Tickets);
    }

    [Fact]
    public async Task Interrupted_reservation_expires_instead_of_excluding_the_vacancy_for_the_process_lifetime()
    {
        var vacancy = await SeedAsync();
        var ticket = Assert.Single((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        Assert.Empty((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        _time.Now = _time.Now.AddMinutes(31);
        var retry = Assert.Single((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        Assert.Equal(vacancy.Id, retry.VacancyId);
        Assert.NotEqual(ticket.Token, retry.Token);
        Assert.False(await Owner.FinishAsync(ticket, 80, false, Ct));
    }

    [Fact]
    public async Task Failure_backoff_is_persisted_bounded_and_duplicate_completion_cannot_extend_it()
    {
        var vacancy = await SeedAsync();
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            var ticket = Assert.Single((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
            Assert.True(await Owner.FinishAsync(ticket, 80, false, Ct));
            var state = await StateAsync(vacancy.Id);
            Assert.Equal(Math.Min(attempt, 6), state.Failures);
            Assert.Equal(TimeSpan.FromMinutes(Math.Min(30 * Math.Pow(2, Math.Min(attempt, 6) - 1), 360)), state.RetryAt - _time.Now);
            Assert.False(await Owner.FinishAsync(ticket, 80, false, Ct));
            Assert.Empty((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
            _time.Now = state.RetryAt.AddSeconds(1);
        }
    }

    [Theory]
    [InlineData("vacancy")]
    [InlineData("application")]
    [InlineData("contact")]
    [InlineData("profile")]
    public async Task Changed_inputs_invalidate_cooldown_and_late_failure_cannot_suppress_the_new_capture(string changed)
    {
        var vacancy = await SeedAsync();
        var old = Assert.Single((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        Assert.True(await Owner.FinishAsync(old, 80, false, Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            if (changed == "vacancy") await context.Vacancies.Where(row => row.Id == vacancy.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Description, "Changed vacancy"), Ct);
            else if (changed == "application") await context.Applications.Where(row => row.VacancyId == vacancy.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Target, "changed-recipient"), Ct);
            else if (changed == "contact")
            {
                context.CompanyContacts.Add(new() { CompanyName = "Example", CompanyKey = VacancyFingerprint.NormalizeCompany("Example"),
                    Kind = ContactKind.Email, Value = "recruiter@contacts.invalid", PersonName = "Synthetic Recruiter", Source = "synthetic" });
                await context.SaveChangesAsync(Ct);
            }
            else
            {
                var publisher = await pg.CandidateAsync();
                await publisher.PublishProfileAsync(new CandidateProfile { Name = "Changed synthetic candidate", Reviewed = true }, 0, "outreach-scan-test", Ct);
            }
        }
        var fresh = Assert.Single((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        Assert.NotEqual(old.SourceHash, fresh.SourceHash);
        Assert.False(await Owner.FinishAsync(old, 80, false, Ct));
        Assert.Equal(fresh.Token, (await StateAsync(vacancy.Id)).Token);
    }

    [Fact]
    public async Task Stale_failure_before_replacement_and_false_success_cannot_overwrite_progress()
    {
        var vacancy = await SeedAsync();
        var ticket = Assert.Single((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        Assert.False(await Owner.FinishAsync(ticket, 80, true, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Vacancies.Where(row => row.Id == vacancy.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Description, "Changed during preparation"), Ct);
        Assert.False(await Owner.FinishAsync(ticket, 80, false, Ct));
        Assert.Equal(0, (await StateAsync(vacancy.Id)).Failures);
    }

    [Fact]
    public async Task Published_draft_finishes_once_and_is_not_prepared_again()
    {
        var vacancy = await SeedAsync();
        var ticket = Assert.Single((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var contact = new CompanyContact { CompanyKey = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName), CompanyName = vacancy.CompanyName!,
            Kind = ContactKind.Email, Value = "recruiter@contacts.invalid", PersonName = "Synthetic Recruiter", Source = "synthetic" };
        context.Outreaches.Add(new() { VacancyId = vacancy.Id, Contact = contact, Text = "Synthetic published draft", CreatedAt = _time.Now });
        await context.SaveChangesAsync(Ct);
        Assert.True(await Owner.FinishAsync(ticket, 80, true, Ct));
        var completed = await StateAsync(vacancy.Id);
        Assert.Equal(0, completed.Failures);
        Assert.Equal(_time.Now.AddHours(6), completed.RetryAt);
        Assert.False(await Owner.FinishAsync(ticket, 80, true, Ct));
        _time.Now = _time.Now.AddHours(7);
        Assert.Empty((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        Assert.Equal(completed.Token, (await StateAsync(vacancy.Id)).Token);
    }

    [Fact]
    public async Task Disabled_limit_and_changed_eligibility_do_not_admit_more_work()
    {
        var vacancy = await SeedAsync();
        Assert.Empty((await Owner.ReserveAsync(80, 0, Ct)).Tickets);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Applications.Where(row => row.VacancyId == vacancy.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Stage, ApplicationStage.TechInterview), Ct);
        Assert.Empty((await Owner.ReserveAsync(80, 1, Ct)).Tickets);
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith(OutreachScans.Prefix)).ToListAsync(Ct));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"version\":99}")]
    public async Task Damaged_progress_is_preserved_without_starving_other_candidates_or_allowing_generic_reset(string json)
    {
        var damaged = await SeedAsync(95);
        var other = await SeedAsync(85);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = OutreachScans.Prefix + damaged.Id;
        context.Settings.Add(new() { Key = key, Value = json });
        await context.SaveChangesAsync(Ct);
        var batch = await Owner.ReserveAsync(80, 1, Ct);
        Assert.Equal(1, batch.Damaged);
        Assert.Equal(other.Id, Assert.Single(batch.Tickets).VacancyId);
        Assert.Equal(json, (await context.Settings.AsNoTracking().SingleAsync(row => row.Key == key, Ct)).Value);
        var settings = _host!.Services.GetRequiredService<RuntimeSettings>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(key, "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(key, Ct));
    }

    [Fact]
    public async Task Job_does_not_mark_missing_candidate_preparation_as_tried_forever()
    {
        var vacancy = await SeedAsync();
        var job = ActivatorUtilities.CreateInstance<OutreachJob>(_host!.Services);
        var run = typeof(OutreachJob).GetMethod("RunOnceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var outcome = await (Task<JobOutcome>)run.Invoke(job, [Ct])!;
        Assert.Equal(0, outcome.Items);
        Assert.Equal(1, (await StateAsync(vacancy.Id)).Failures);
        _time.Now = _time.Now.AddMinutes(31);
        await (Task<JobOutcome>)run.Invoke(job, [Ct])!;
        Assert.Equal(2, (await StateAsync(vacancy.Id)).Failures);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Outreaches.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 11, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
