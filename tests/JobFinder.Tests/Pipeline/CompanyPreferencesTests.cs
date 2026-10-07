using System.Reflection;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CompanyPage = JobFinder.App.Components.Pages.Companies;

namespace JobFinder.Tests.Pipeline;

public sealed partial class CompanyPreferencesTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Companies.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private async Task<CompanyPreferences> OwnerAsync(SaveChangesInterceptor? fault = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault);
        await _host.StartAsync(Ct);
        return _host.Services.GetRequiredService<CompanyPreferences>();
    }

    private async Task<Company> SourceAsync(bool watch = false, bool blacklisted = false)
    {
        var company = new Company { Name = "Synthetic <company>", HhId = 987654, HabrAlias = "synthetic-company",
            Watch = watch, Blacklisted = blacklisted, DotnetVacancies = 3, FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Companies.Add(company);
        await context.SaveChangesAsync(Ct);
        return company;
    }

    private async Task<Company> ReadAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Companies.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preference_publication_and_native_admission_commit_or_roll_back_together(bool fail)
    {
        var fault = new AdmissionFault { Active = fail };
        var owner = await OwnerAsync(fault);
        var source = await SourceAsync();
        var expected = CompanyPreferenceSnapshot.Capture(source);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SetAsync(expected, true, true, Ct));
        else Assert.True((await owner.SetAsync(expected, true, true, Ct)).Ok);
        var saved = await ReadAsync(source.Id);
        Assert.Equal(fail ? 0 : 1, saved.PreferencesRevision);
        Assert.Equal(!fail, saved.Watch);
        Assert.Equal(!fail, saved.Blacklisted);
        Assert.Equal(source.Name, saved.Name);
        Assert.Equal(source.UpdatedAt.UtcTicks / 10, saved.UpdatedAt.UtcTicks / 10);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (fail)
        {
            Assert.Equal(1, fault.Failures);
            Assert.Empty(await context.Notifications.ToListAsync(Ct));
            Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
            fault.Active = false;
            Assert.True((await owner.SetAsync(expected, true, true, Ct)).Ok);
        }
        var notice = await context.Notifications.SingleAsync(Ct);
        Assert.Contains("&lt;company&gt;", notice.Text);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_preference_edits_accept_one_displayed_witness_and_never_rebase_the_loser()
    {
        var owner = await OwnerAsync();
        var source = await SourceAsync();
        var expected = CompanyPreferenceSnapshot.Capture(source);
        var results = await Task.WhenAll(owner.SetAsync(expected, true, false, Ct), owner.SetAsync(expected, false, true, Ct));
        Assert.Single(results, result => result.Ok);
        Assert.Single(results, result => !result.Ok);
        var saved = await ReadAsync(source.Id);
        Assert.Equal(1, saved.PreferencesRevision);
        Assert.True(saved.Watch ^ saved.Blacklisted);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Preference_revision_fences_aba_even_when_flags_return_to_the_original_values()
    {
        var owner = await OwnerAsync();
        var source = await SourceAsync();
        var original = CompanyPreferenceSnapshot.Capture(source);
        Assert.True((await owner.SetAsync(original, true, false, Ct)).Ok);
        Assert.True((await owner.SetAsync(CompanyPreferenceSnapshot.Capture(await ReadAsync(source.Id)), false, false, Ct)).Ok);
        Assert.False((await owner.SetAsync(original, true, true, Ct)).Ok);
        var saved = await ReadAsync(source.Id);
        Assert.Equal(2, saved.PreferencesRevision);
        Assert.False(saved.Watch);
        Assert.False(saved.Blacklisted);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task A_metadata_refresh_neither_invalidates_displayed_preferences_nor_overwrites_them_after_a_slow_read()
    {
        var owner = await OwnerAsync();
        var source = await SourceAsync();
        var expected = CompanyPreferenceSnapshot.Capture(source);
        await using var collector = await pg.Db.CreateDbContextAsync(Ct);
        var tracked = await collector.Companies.SingleAsync(Ct);
        VacancyIngestor.ApplyEmployer(tracked, new HhEmployer(source.HhId!.Value, "Refreshed synthetic company", "https://synthetic.invalid",
            "Minsk", "small", ["Software"], "Refreshed metadata", 4.5m, true, 12), DateTimeOffset.UtcNow);
        await collector.SaveChangesAsync(Ct);
        Assert.True((await owner.SetAsync(expected, true, true, Ct)).Ok);
        tracked.DotnetVacancies = 9;
        tracked.Description = "Second slow metadata refresh";
        await collector.SaveChangesAsync(Ct);
        var saved = await ReadAsync(source.Id);
        Assert.True(saved.Watch);
        Assert.True(saved.Blacklisted);
        Assert.Equal(1, saved.PreferencesRevision);
        Assert.Equal("Refreshed synthetic company", saved.Name);
        Assert.Equal("Second slow metadata refresh", saved.Description);
        Assert.Equal(9, saved.DotnetVacancies);
        Assert.Equal(4.5m, saved.Rating);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("missing")]
    [InlineData("revision")]
    public async Task Invalid_or_changed_company_identity_refuses_publication_without_native_admission(string damage)
    {
        var owner = await OwnerAsync();
        var source = await SourceAsync();
        var expected = CompanyPreferenceSnapshot.Capture(source);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (damage == "identity")
            await context.Companies.ExecuteUpdateAsync(update => update.SetProperty(row => row.HhId, 123456L), Ct);
        else if (damage == "missing") await context.Companies.ExecuteDeleteAsync(Ct);
        else expected.Revision = long.MaxValue;
        Assert.False((await owner.SetAsync(expected, true, true, Ct)).Ok);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Noop_keeps_revision_and_does_not_publish_a_notice()
    {
        var owner = await OwnerAsync();
        var source = await SourceAsync(watch: true, blacklisted: true);
        Assert.True((await owner.SetAsync(CompanyPreferenceSnapshot.Capture(source), true, true, Ct)).Ok);
        Assert.Equal(0, (await ReadAsync(source.Id)).PreferencesRevision);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public void Checkpoint_materialization_preserves_company_preferences_and_legacy_defaults_without_publishing()
    {
        var source = new Company { Name = "Synthetic serialized company", Watch = true, Blacklisted = true, PreferencesRevision = 7 };
        var json = JsonSerializer.Serialize(source, PostProcessingJsonContext.Default.Company);
        var restored = JsonSerializer.Deserialize(json, PostProcessingJsonContext.Default.Company)!;
        Assert.True(restored.Watch);
        Assert.True(restored.Blacklisted);
        Assert.Equal(7, restored.PreferencesRevision);
        var legacy = JsonSerializer.Deserialize("""{"name":"Synthetic legacy company","watch":true,"blacklisted":true}""", PostProcessingJsonContext.Default.Company)!;
        Assert.True(legacy.Watch);
        Assert.True(legacy.Blacklisted);
        Assert.Equal(0, legacy.PreferencesRevision);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_the_company_lock_keeps_flags_and_outbox_unchanged()
    {
        var owner = await OwnerAsync();
        var source = await SourceAsync();
        await using var held = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await held.Database.BeginTransactionAsync(Ct);
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM companies WHERE id = {source.Id} FOR UPDATE", Ct);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.SetAsync(CompanyPreferenceSnapshot.Capture(source), true, true, cancelled.Token));
        await transaction.RollbackAsync(Ct);
        Assert.Equal(CompanyPreferenceSnapshot.Capture(source), CompanyPreferenceSnapshot.Capture(await ReadAsync(source.Id)));
        Assert.Empty(await held.Notifications.ToListAsync(Ct));
        Assert.Empty(await held.NotificationDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Migration_preserves_legacy_flags_identity_and_metadata_without_a_native_publication()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.GetService<IMigrator>().MigrateAsync("20261003190627_RetiredCompanyContacts", Ct);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO companies (name, hh_id, watch, blacklisted, industries, tags, it_accredited, dotnet_vacancies, first_seen_at, updated_at)
            VALUES ('Synthetic legacy company', 987654, true, true, '{{}}', '{{}}', false, 3, now(), now())
            """, Ct);
        var before = await context.Database.SqlQueryRaw<string>("SELECT to_jsonb(company)::text AS \"Value\" FROM companies company").SingleAsync(Ct);
        await context.Database.MigrateAsync(Ct);
        var after = await context.Database.SqlQueryRaw<string>("SELECT (to_jsonb(company) - 'preferences_revision')::text AS \"Value\" FROM companies company").SingleAsync(Ct);
        Assert.Equal(before, after);
        var saved = await context.Companies.SingleAsync(Ct);
        Assert.Equal(0, saved.PreferencesRevision);
        Assert.True(saved.Watch);
        Assert.True(saved.Blacklisted);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Downgrade_refuses_retained_preference_revisions()
    {
        var owner = await OwnerAsync();
        var source = await SourceAsync();
        Assert.True((await owner.SetAsync(CompanyPreferenceSnapshot.Capture(source), true, false, Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003190627_RetiredCompanyContacts", Ct));
        Assert.Contains("Cannot downgrade retained company preference revisions", error.MessageText);
        Assert.Equal(1, (await context.Companies.SingleAsync(Ct)).PreferencesRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_page_preserves_captured_flags_and_desired_change_after_conflict_or_admission_failure(bool fail)
    {
        var fault = new AdmissionFault();
        var owner = await OwnerAsync(fault);
        var source = await SourceAsync();
        var capture = new PageCapture();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture)
            .AddSingleton(pg.Db).AddSingleton(owner).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<CompanyPage>(ParameterView.Empty);
            var original = ((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_snapshots"))[source.Id];
            if (fail) fault.Active = true;
            else Assert.True((await owner.SetAsync(original, false, true, Ct)).Ok);
            await CallAsync(capture.Page, "Toggle", source.Id, true);
            var desired = ((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_drafts"))[source.Id];
            Assert.True(desired.Watch);
            Assert.False(desired.Blacklisted);
            await CallAsync(capture.Page, "LoadAsync");
            Assert.Equal(original, ((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_snapshots"))[source.Id]);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture.Page, null);
            Assert.Contains("Не сохранено", System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString()));
            Assert.Empty((HashSet<long>)Field(capture.Page, "_saving"));
            fault.Active = false;
            await CallAsync(capture.Page, "ReloadCompany", source.Id);
            Assert.Empty((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_drafts"));
            var refreshed = ((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_snapshots"))[source.Id];
            Assert.Equal(CompanyPreferenceSnapshot.Capture(await ReadAsync(source.Id)), refreshed);
            await CallAsync(capture.Page, "Toggle", source.Id, true);
            var saved = await ReadAsync(source.Id);
            Assert.True(saved.Watch);
            Assert.Equal(!fail, saved.Blacklisted);
            Assert.Empty((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_drafts"));
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(fail ? 1 : 2, await context.Notifications.CountAsync(Ct));
            foreach (var notice in await context.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        });
    }

    private static object Field(CompanyPage page, string name) => typeof(CompanyPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;

    [Fact]
    public async Task A_conflicted_company_hidden_by_reload_keeps_its_visible_intent_until_explicit_discard()
    {
        var owner = await OwnerAsync();
        var source = await SourceAsync();
        var capture = new PageCapture();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture)
            .AddSingleton(pg.Db).AddSingleton(owner).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<CompanyPage>(ParameterView.Empty);
            var original = ((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_snapshots"))[source.Id];
            await using (var context = await pg.Db.CreateDbContextAsync(Ct)) await context.Companies.ExecuteDeleteAsync(Ct);
            await CallAsync(capture.Page, "Toggle", source.Id, true);
            await CallAsync(capture.Page, "LoadAsync");
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture.Page, null);
            Assert.Contains($"Компания #{source.Id}: правка не сохранена", System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString()));
            Assert.Equal(original, ((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_snapshots"))[source.Id]);
            Assert.True(((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_drafts"))[source.Id].Watch);
            await CallAsync(capture.Page, "ReloadCompany", source.Id);
            Assert.Empty((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_drafts"));
            Assert.Empty((Dictionary<long, CompanyPreferenceSnapshot>)Field(capture.Page, "_snapshots"));
            await using var final = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await final.Notifications.ToListAsync(Ct));
            Assert.Empty(await final.NotificationDeliveryRequests.ToListAsync(Ct));
        });
    }

    private static Task CallAsync(CompanyPage page, string method, params object[] arguments) =>
        (Task)typeof(CompanyPage).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, arguments)!;

    private sealed class PageCapture : IComponentActivator
    {
        public CompanyPage Page { get; } = new();
        public IComponent CreateInstance(Type type) => type == typeof(CompanyPage) ? Page : (IComponent)Activator.CreateInstance(type)!;
    }

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context is JobFinderDbContext context
                && context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                Failures++;
                throw new InvalidOperationException("synthetic preference admission failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
