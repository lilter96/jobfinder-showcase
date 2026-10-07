using System.Reflection;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed class ApplicationBlockTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Until = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private IHost? _host;
    private ApplicationBlocks Blocks => _host!.Services.GetRequiredService<ApplicationBlocks>();
    private RuntimeSettings Settings => _host!.Services.GetRequiredService<RuntimeSettings>();
    private ApplicationAutomationControls Controls => _host!.Services.GetRequiredService<ApplicationAutomationControls>();

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.MigrateAsync(Ct);
        await context.ApplicationBlockRevisions.ExecuteDeleteAsync(Ct);
        await context.DeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.ApplicationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith("apply.blockedUntil")).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private async Task StartAsync(AdmissionFault? fault = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault);
        await _host.StartAsync(Ct);
    }

    private async Task LegacyAsync(string? value = null)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = RuntimeSettings.ApplyBlockedUntil, Value = value ?? Until.ToString("O"), UpdatedAt = Until.AddDays(-1) });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<Guid> RecordAsync(DateTimeOffset until)
    {
        await using var context = await _host!.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var vacancy = new Vacancy { Title = "Synthetic .NET", Url = "https://vacancy.invalid/block", Fingerprint = Guid.NewGuid().ToString("N"), FirstSeenAt = Until.AddDays(-1) };
        var application = new Application { Vacancy = vacancy, Channel = ApplicationChannel.HeadHunter, Status = ApplicationStatus.Pending, CreatedAt = Until.AddDays(-1) };
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        var root = new ApplicationDeliveryRequest { Id = Guid.NewGuid(), ApplicationId = application.Id, InputJson = "{}", InputHash = "synthetic", CreatedAt = Until.AddDays(-1), Stage = ApplicationDeliveryStage.Completed };
        context.ApplicationDeliveryRequests.Add(root);
        var attempt = new DeliveryAttempt { Id = Guid.NewGuid(), ApplicationId = application.Id, ApplicationDeliveryRequestId = root.Id,
            Status = DeliveryAttemptStatus.KnownNotSent, Text = "Synthetic letter", TextHash = "synthetic", CreatedAt = Until.AddDays(-1), CompletedAt = Until.AddDays(-1) };
        context.DeliveryAttempts.Add(attempt);
        await using var scope = _host!.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await Blocks.RecordJoinedAsync(context, outbox, until, attempt, Ct);
        await _host.Services.GetRequiredService<Notifier>().SaveAndFlushAsync(context, outbox, Ct);
        return attempt.Id;
    }

    [Fact]
    public async Task Legacy_release_retains_preimage_tombstone_and_exact_native_notice_together()
    {
        await StartAsync();
        await LegacyAsync();
        var captured = await Blocks.ReadAsync(Ct);
        Assert.Equal(0, captured.Revision);
        Assert.True((await Blocks.ReleaseAsync(captured, "test", Ct)).Ok);
        var released = await Blocks.ReadAsync(Ct);
        Assert.Equal(1, released.Revision);
        Assert.Null(released.Value);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var history = await context.ApplicationBlockRevisions.SingleAsync(Ct);
        Assert.Equal(captured.Value, history.BeforeValue);
        Assert.Null(history.Value);
        Assert.Equal("test", history.Actor);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Same_expiry_after_release_is_a_new_revision_and_stale_release_refuses()
    {
        await StartAsync();
        await RecordAsync(Until);
        var first = await Blocks.ReadAsync(Ct);
        await Blocks.ReleaseAsync(first, "test", Ct);
        await RecordAsync(Until);
        var third = await Blocks.ReadAsync(Ct);
        Assert.Equal(first.Value, third.Value);
        Assert.Equal(3, third.Revision);
        await Assert.ThrowsAsync<ApplicationBlockConflictException>(() => Blocks.ReleaseAsync(first, "stale", Ct));
        Assert.Equal(third, await Blocks.ReadAsync(Ct));
    }

    [Fact]
    public async Task Captured_absence_cannot_remove_a_block_that_arrived_during_login()
    {
        await StartAsync();
        var absent = await Blocks.ReadAsync(Ct);
        await RecordAsync(Until);
        await Assert.ThrowsAsync<ApplicationBlockConflictException>(() => Blocks.ReleaseAsync(absent, "login", Ct));
        Assert.Equal(Until, (await Blocks.ReadAsync(Ct)).Until);
    }

    [Fact]
    public async Task Concurrent_releases_admit_one_history_and_notice()
    {
        await StartAsync();
        await LegacyAsync();
        var captured = await Blocks.ReadAsync(Ct);
        async Task<bool> ReleaseAsync()
        {
            try { await Blocks.ReleaseAsync(captured, "concurrent", Ct); return true; }
            catch (ApplicationBlockConflictException) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ReleaseAsync()));
        Assert.Equal(1, results.Count(result => result));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ApplicationBlockRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Fresh_runtime_read_does_not_hide_a_block_behind_the_settings_cache()
    {
        await StartAsync();
        await Settings.GetAsync("synthetic-cache-primer", Ct);
        Assert.Null(await Settings.GetInstantAsync(RuntimeSettings.ApplyBlockedUntil, Ct));
        await RecordAsync(Until);
        Assert.Equal(Until, await Settings.GetInstantAsync(RuntimeSettings.ApplyBlockedUntil, Ct));
        await Blocks.ReleaseAsync(await Blocks.ReadAsync(Ct), "test", Ct);
        Assert.Null(await Settings.GetInstantAsync(RuntimeSettings.ApplyBlockedUntil, Ct));
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("history")]
    [InlineData("value")]
    [InlineData("timestamp")]
    [InlineData("malformed")]
    public async Task Damaged_native_evidence_fails_closed_without_release_or_policy_changes(string damage)
    {
        await StartAsync();
        await RecordAsync(Until);
        var captured = await Blocks.ReadAsync(Ct);
        var policy = await Settings.Automation.ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        switch (damage)
        {
            case "marker": await context.Settings.Where(row => row.Key == ApplicationBlocks.RevisionKey).ExecuteDeleteAsync(Ct); break;
            case "history": await context.ApplicationBlockRevisions.ExecuteDeleteAsync(Ct); break;
            case "value": await context.Settings.Where(row => row.Key == RuntimeSettings.ApplyBlockedUntil).ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, Until.AddHours(1).ToString("O")), Ct); break;
            case "timestamp": await context.Settings.Where(row => row.Key == ApplicationBlocks.RevisionKey).ExecuteUpdateAsync(update => update.SetProperty(row => row.UpdatedAt, Until), Ct); break;
            case "malformed": await context.Settings.Where(row => row.Key == RuntimeSettings.ApplyBlockedUntil).ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, "not-a-date"), Ct); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settings.GetInstantAsync(RuntimeSettings.ApplyBlockedUntil, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controls.SetPauseAsync(policy, false, captured, "test", Ct));
        Assert.Equal(policy, await Settings.Automation.ReadAsync(Ct));
    }

    [Theory]
    [InlineData(RuntimeSettings.ApplyBlockedUntil)]
    [InlineData(ApplicationBlocks.RevisionKey)]
    public async Task Generic_setters_cannot_erase_or_rewrite_owned_block_state(string key)
    {
        await StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settings.SetAsync(key, "value", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settings.RemoveAsync(key, Ct));
        Assert.Equal(0, (await Blocks.ReadAsync(Ct)).Revision);
    }

    [Fact]
    public async Task Combined_resume_commits_policy_history_release_and_both_native_notices()
    {
        await StartAsync();
        await LegacyAsync();
        var policy = await Settings.Automation.ReadAsync(Ct);
        var block = await Blocks.ReadAsync(Ct);
        await using var before = await pg.Db.CreateDbContextAsync(Ct);
        var noticeCount = await before.Notifications.CountAsync(Ct);
        var published = await Controls.SetPauseAsync(policy, false, block, "combined", Ct);
        Assert.Equal(policy.Version + 1, published.Version);
        Assert.False(published.Value.AutoApplyPaused);
        Assert.Null((await Blocks.ReadAsync(Ct)).Until);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(noticeCount + 2, await context.Notifications.CountAsync(Ct));
        Assert.Equal("combined", (await context.ApplicationBlockRevisions.SingleAsync(Ct)).Actor);
        Assert.Equal("combined", (await context.PolicyRevisions.SingleAsync(row => row.Key == AutomationPolicyStore.Key && row.Version == published.Version, Ct)).Actor);
        foreach (var notice in await context.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Either_stale_witness_preserves_the_other_source_without_partial_resume(bool changePolicy)
    {
        await StartAsync();
        await LegacyAsync();
        var policy = await Settings.Automation.ReadAsync(Ct);
        var block = await Blocks.ReadAsync(Ct);
        if (changePolicy) await Settings.Automation.PublishAsync(policy.Value with { AutoApplyPaused = true }, policy.Version, "other", Ct);
        else await RecordAsync(Until.AddHours(1));
        var expectedPolicy = await Settings.Automation.ReadAsync(Ct);
        var expectedBlock = await Blocks.ReadAsync(Ct);
        var error = await Record.ExceptionAsync(() => Controls.SetPauseAsync(policy, false, block, "stale", Ct));
        if (changePolicy) Assert.IsType<PolicyConflictException>(error);
        else Assert.IsType<ApplicationBlockConflictException>(error);
        Assert.Equal(expectedPolicy, await Settings.Automation.ReadAsync(Ct));
        Assert.Equal(expectedBlock, await Blocks.ReadAsync(Ct));
    }

    [Fact]
    public async Task Failed_native_admission_rolls_back_policy_update_history_and_block_release()
    {
        var fault = new AdmissionFault();
        await StartAsync(fault);
        await LegacyAsync();
        var policy = await Settings.Automation.ReadAsync(Ct);
        var block = await Blocks.ReadAsync(Ct);
        await using var before = await pg.Db.CreateDbContextAsync(Ct);
        var count = await before.Notifications.CountAsync(Ct);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controls.SetPauseAsync(policy, false, block, "failed", Ct));
        Assert.Equal(policy, await Settings.Automation.ReadAsync(Ct));
        Assert.Equal(block, await Blocks.ReadAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.ApplicationBlockRevisions.ToListAsync(Ct));
        Assert.Equal(count, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Emergency_pause_does_not_need_a_matching_captcha_release_witness()
    {
        await StartAsync();
        var policy = await Settings.Automation.ReadAsync(Ct);
        var absent = await Blocks.ReadAsync(Ct);
        await RecordAsync(Until);
        var published = await Controls.SetPauseAsync(policy, true, absent, "emergency", Ct);
        Assert.True(published.Value.AutoApplyPaused);
        Assert.Equal(Until, (await Blocks.ReadAsync(Ct)).Until);
    }

    [Fact]
    public async Task Automatic_receipts_do_not_shorten_an_existing_later_pause()
    {
        await StartAsync();
        await RecordAsync(Until.AddDays(1));
        await RecordAsync(Until);
        Assert.Equal(Until.AddDays(1), (await Blocks.ReadAsync(Ct)).Until);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.ApplicationBlockRevisions.CountAsync(Ct));
    }

    [Fact]
    public async Task Downgrade_refuses_released_native_history_and_tombstone()
    {
        await StartAsync();
        await LegacyAsync();
        await Blocks.ReleaseAsync(await Blocks.ReadAsync(Ct), "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(context.Database.HasPendingModelChanges());
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004060138_DurableWeeklyReports", Ct));
        Assert.Contains("Cannot downgrade retained application block", error.MessageText);
        Assert.Equal(1, (await Blocks.ReadAsync(Ct)).Revision);
    }

    [Fact]
    public async Task Settings_page_retains_original_block_and_unrelated_policy_draft_until_explicit_reload()
    {
        await StartAsync();
        await LegacyAsync();
        var capture = new PageCapture<JobFinder.App.Components.Pages.SettingsPage>();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture)
            .AddSingleton(Settings).AddSingleton(Blocks).AddSingleton(_host!.Services.GetRequiredService<JobFinder.Pipeline.Profile.ProfileProvider>())
            .AddSingleton(_host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.EngagementPolicyStore>())
            .AddSingleton(_host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.SearchPolicyStore>()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var html = await renderer.RenderComponentAsync<JobFinder.App.Components.Pages.SettingsPage>(ParameterView.Empty);
            var original = Field(capture.Page, "_block");
            SetField(capture.Page, "_auto", 99);
            await RecordAsync(Until.AddHours(1));
            await CallAsync(capture.Page, "Unblock");
            Assert.Equal(original, Field(capture.Page, "_block"));
            Assert.True((bool)Field(capture.Page, "_blockConflict")!);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture.Page, null);
            Assert.Contains("Пауза уже изменилась", System.Net.WebUtility.HtmlDecode(html.ToHtmlString()));
            Assert.Contains("Загрузить актуальную паузу", System.Net.WebUtility.HtmlDecode(html.ToHtmlString()));
            await CallAsync(capture.Page, "ReloadBlock");
            Assert.False((bool)Field(capture.Page, "_blockConflict")!);
            Assert.Equal(99, Field(capture.Page, "_auto"));
            await CallAsync(capture.Page, "Unblock");
            Assert.Null((await Blocks.ReadAsync(Ct)).Until);
            Assert.NotEmpty(html.ToHtmlString());
        });
    }

    [Fact]
    public async Task Facts_auto_reply_preserves_the_desired_value_and_original_policy_on_conflict()
    {
        await StartAsync();
        var page = new JobFinder.App.Components.Pages.Facts();
        Property(page, "Settings", Settings);
        await CallAsync(page, "ReloadAutoReply");
        var original = Assert.IsType<PolicySnapshot<AutomationPolicy>>(Field(page, "_policy"));
        var desired = !original.Value.ChatAutoReply;
        await Settings.Automation.PublishAsync(original.Value with { AutoApplyPaused = true }, original.Version, "other", Ct);
        await CallAsync(page, "ToggleAutoReply", new ChangeEventArgs { Value = desired });
        Assert.Equal(original, Field(page, "_policy"));
        Assert.Equal(desired, Field(page, "_autoReply"));
        Assert.True((bool)Field(page, "_policyConflict")!);
        await CallAsync(page, "ToggleAutoReply", new ChangeEventArgs { Value = !desired });
        Assert.Equal(desired, Field(page, "_autoReply"));
        await CallAsync(page, "ReloadAutoReply");
        Assert.False((bool)Field(page, "_policyConflict")!);
        await CallAsync(page, "ToggleAutoReply", new ChangeEventArgs { Value = desired });
        var saved = await Settings.Automation.ReadAsync(Ct);
        Assert.True(saved.Value.AutoApplyPaused);
        Assert.Equal(desired, saved.Value.ChatAutoReply);
    }

    [Fact]
    public async Task Overview_refresh_keeps_original_policy_block_and_desired_pause_after_conflict()
    {
        await StartAsync();
        var page = new JobFinder.App.Components.Pages.Overview();
        Property(page, "Settings", Settings);
        Property(page, "Blocks", Blocks);
        Property(page, "AutomationControls", Controls);
        await CallAsync(page, "ReloadControls");
        var original = Assert.IsType<PolicySnapshot<AutomationPolicy>>(Field(page, "_policy"));
        var block = Field(page, "_block");
        await Settings.Automation.PublishAsync(original.Value with { CommentsPaused = true }, original.Version, "other", Ct);
        await CallAsync(page, "TogglePause");
        Assert.Equal(original, Field(page, "_policy"));
        Assert.Equal(block, Field(page, "_block"));
        Assert.Equal(!original.Value.AutoApplyPaused, Field(page, "_pauseDraft"));
        Assert.True((bool)Field(page, "_controlConflict")!);
        await CallAsync(page, "RefreshControlsAsync", false);
        Assert.Equal(original, Field(page, "_policy"));
        Assert.Equal(block, Field(page, "_block"));
        await CallAsync(page, "ReloadControls");
        Assert.Null(Field(page, "_pauseDraft"));
        Assert.False((bool)Field(page, "_controlConflict")!);
        await CallAsync(page, "TogglePause");
        var saved = await Settings.Automation.ReadAsync(Ct);
        Assert.Equal(!original.Value.AutoApplyPaused, saved.Value.AutoApplyPaused);
        Assert.True(saved.Value.CommentsPaused);
    }

    [Fact]
    public async Task Login_release_helper_uses_the_original_pre_browser_witness_not_a_newer_block()
    {
        await StartAsync();
        var original = await Blocks.ReadAsync(Ct);
        await RecordAsync(Until);
        var method = typeof(JobFinder.App.Cli.CliHost).GetMethod("TryClearApplyBlockAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await (Task)method.Invoke(null, [_host!.Services, original, Ct])!;
        Assert.Equal(Until, (await Blocks.ReadAsync(Ct)).Until);
        await (Task)method.Invoke(null, [_host.Services, await Blocks.ReadAsync(Ct), Ct])!;
        Assert.Null((await Blocks.ReadAsync(Ct)).Until);
    }

    [Theory]
    [InlineData(DeliveryAttemptStatus.Submitting)]
    [InlineData(DeliveryAttemptStatus.OutcomeUnknown)]
    [InlineData(DeliveryAttemptStatus.Reconciled)]
    public async Task Non_known_unsent_receipts_never_establish_an_automatic_block(DeliveryAttemptStatus status)
    {
        await StartAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var attempt = new DeliveryAttempt { Id = Guid.NewGuid(), Text = "synthetic", TextHash = "synthetic", Status = status,
            ApplicationDeliveryRequestId = Guid.NewGuid(), CompletedAt = Until.AddDays(-1) };
        context.DeliveryAttempts.Add(attempt);
        await using var scope = _host!.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Blocks.RecordJoinedAsync(context, outbox, Until, attempt, Ct));
        Assert.Empty(context.ApplicationBlockRevisions.Local);
        Assert.Empty(context.Notifications.Local);
    }

    [Fact]
    public async Task Compatible_legacy_block_survives_safe_down_and_up_without_new_notices()
    {
        await LegacyAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.GetService<IMigrator>().MigrateAsync("20261004060138_DurableWeeklyReports", Ct);
        Assert.Equal(Until.ToString("O"), (await context.Settings.SingleAsync(row => row.Key == RuntimeSettings.ApplyBlockedUntil, Ct)).Value);
        await context.Database.MigrateAsync(Ct);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.ApplicationBlockRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    private static void Property(object page, string name, object value) => page.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
    [Fact]
    public async Task Failed_receipt_notice_admission_rolls_back_attempt_history_and_pause()
    {
        var fault = new AdmissionFault();
        await StartAsync(fault);
        await LegacyAsync();
        var original = await Blocks.ReadAsync(Ct);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(Until.AddDays(1)));
        Assert.Equal(original, await Blocks.ReadAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationBlockRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Replayed_known_unsent_receipt_never_creates_another_block_revision_or_notice()
    {
        await StartAsync();
        var id = await RecordAsync(Until);
        var original = await Blocks.ReadAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var attempt = await context.DeliveryAttempts.SingleAsync(row => row.Id == id, Ct);
        await using var scope = _host!.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await Blocks.RecordJoinedAsync(context, outbox, Until.AddDays(1), attempt, Ct);
        await _host.Services.GetRequiredService<Notifier>().SaveAndFlushAsync(context, outbox, Ct);
        Assert.Equal(original, await Blocks.ReadAsync(Ct));
        Assert.Single(await context.ApplicationBlockRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("facts")]
    [InlineData("settings")]
    public async Task Admission_failure_preserves_control_witness_and_draft_until_explicit_reload(string screen)
    {
        var fault = new AdmissionFault();
        await StartAsync(fault);
        await LegacyAsync();
        object page;
        string witness;
        string conflict;
        string reload;
        string action;
        object[] arguments;
        switch (screen)
        {
            case "overview":
                page = new JobFinder.App.Components.Pages.Overview();
                Property(page, "Settings", Settings);
                Property(page, "Blocks", Blocks);
                Property(page, "AutomationControls", Controls);
                witness = "_policy"; conflict = "_controlConflict"; reload = "ReloadControls"; action = "TogglePause"; arguments = [];
                break;
            case "facts":
                page = new JobFinder.App.Components.Pages.Facts();
                Property(page, "Settings", Settings);
                witness = "_policy"; conflict = "_policyConflict"; reload = "ReloadAutoReply"; action = "ToggleAutoReply"; arguments = [new ChangeEventArgs { Value = false }];
                break;
            default:
                page = new JobFinder.App.Components.Pages.SettingsPage();
                Property(page, "Blocks", Blocks);
                witness = "_block"; conflict = "_blockConflict"; reload = "ReloadBlock"; action = "Unblock"; arguments = [];
                break;
        }
        await CallAsync(page, reload);
        var original = Field(page, witness);
        var policy = await Settings.Automation.ReadAsync(Ct);
        var block = await Blocks.ReadAsync(Ct);
        fault.Active = true;
        await CallAsync(page, action, arguments);
        Assert.Equal(original, Field(page, witness));
        Assert.True((bool)Field(page, conflict)!);
        Assert.Contains("не подтверждено", Assert.IsType<string>(Field(page, "_flash")));
        Assert.Equal(policy, await Settings.Automation.ReadAsync(Ct));
        Assert.Equal(block, await Blocks.ReadAsync(Ct));
        if (screen == "overview") Assert.Equal(!policy.Value.AutoApplyPaused, Field(page, "_pauseDraft"));
        if (screen == "facts") Assert.False((bool)Field(page, "_autoReply")!);
        fault.Active = false;
        await CallAsync(page, reload);
        Assert.False((bool)Field(page, conflict)!);
    }

    private static object? Field(object page, string name) => page.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
    private static void SetField(object page, string name, object value) => page.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
    private static Task CallAsync(object page, string method, params object[] arguments) => (Task)page.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, arguments)!;

    private sealed class PageCapture<T> : IComponentActivator where T : IComponent, new()
    {
        public T Page { get; } = new();
        public IComponent CreateInstance(Type type) => type == typeof(T) ? Page : (IComponent)Activator.CreateInstance(type)!;
    }

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context is JobFinderDbContext context && context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("synthetic application control admission failure");
            return ValueTask.FromResult(result);
        }
    }
}
