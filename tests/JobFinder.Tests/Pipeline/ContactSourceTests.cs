using JobFinder.Core.Domain;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.Json;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Documents;
using ContactsPage = JobFinder.App.Components.Pages.Contacts;
using VacancyPage = JobFinder.App.Components.Pages.VacancyDetail;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_contact_publication_and_native_notice_share_the_source_transaction(bool fail)
    {
        var host = await HostAsync();
        var registry = host.Services.GetRequiredService<ContactRegistry>();
        _fault.Active = fail;
        Task<long?> PublishAsync() => registry.UpsertAsync("Synthetic manual company", null, ContactKind.Email,
            "recruiter@example.invalid", "Synthetic recruiter", "HR", ContactRegistry.ManualSource, null, Ct);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(PublishAsync);
        else Assert.NotNull(await PublishAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (fail)
        {
            Assert.Empty(await context.CompanyContacts.ToListAsync(Ct));
            await NoAdmissionAsync(context);
        }
        else
        {
            var contact = await context.CompanyContacts.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(1, contact.Revision);
            Assert.Equal(1, contact.SeenCount);
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        }
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contact_retirement_preserves_opaque_future_delivery_even_when_the_direct_contact_marker_is_missing(bool outreachOnly)
    {
        var (source, contact) = await OutreachDecisionAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var message = new EmployerMessage
        {
            Id = Guid.NewGuid(), BusinessKey = Guid.NewGuid().ToString("N"), VacancyId = source.VacancyId,
            Status = EmployerMessageStatus.OutcomeUnknown, CreatedAt = DateTimeOffset.UtcNow,
            InputJson = outreachOnly ? JsonSerializer.Serialize(new { version = 99, outreachId = source.Id })
                : JsonSerializer.Serialize(new { version = 99, contactId = contact.Id }),
        };
        context.EmployerMessages.Add(message);
        await context.SaveChangesAsync(Ct);
        var before = (await context.EmployerMessages.AsNoTracking().SingleAsync(Ct)).InputJson;
        Assert.False((await _host!.Services.GetRequiredService<ContactRegistry>().RemoveAsync(ContactEditSnapshot.Capture(contact), Ct)).Ok);
        Assert.Null((await context.CompanyContacts.AsNoTracking().SingleAsync(Ct)).RetiredAt);
        Assert.Equal(before, (await context.EmployerMessages.AsNoTracking().SingleAsync(Ct)).InputJson);
        await NoAdmissionAsync(context);
    }

    [Fact]
    public async Task Contact_retirement_refuses_an_unsupported_outreach_status_without_deserializing_or_mutating_it()
    {
        var (source, contact) = await OutreachDecisionAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE outreaches SET status = 'FutureSubmitted' WHERE id = {source.Id}", Ct);
        Assert.False((await _host!.Services.GetRequiredService<ContactRegistry>().RemoveAsync(ContactEditSnapshot.Capture(contact), Ct)).Ok);
        Assert.Equal("FutureSubmitted", await context.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM outreaches WHERE id = {source.Id}").SingleAsync(Ct));
        Assert.Null((await context.CompanyContacts.AsNoTracking().SingleAsync(Ct)).RetiredAt);
        await NoAdmissionAsync(context);
    }

    [Fact]
    public async Task Contact_post_observation_joins_the_source_transaction_without_an_independent_commit()
    {
        var (_, contact) = await OutreachSourceAsync();
        var registry = _host!.Services.GetRequiredService<ContactRegistry>();
        var found = new VacancyContact { Kind = contact.Kind, Value = contact.Value };
        await using var publication = await pg.Db.CreateDbContextAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.FromPostAsync(publication, contact.CompanyName, null, [found], "https://example.invalid/post", Ct));
        await using (var transaction = await publication.Database.BeginTransactionAsync(Ct))
        {
            await registry.FromPostAsync(publication, contact.CompanyName, null, [found], "https://example.invalid/post", Ct);
            var staged = await publication.CompanyContacts.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(contact.Revision + 1, staged.Revision);
            await using var outside = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(ContactEditSnapshot.Capture(contact), ContactEditSnapshot.Capture(await outside.CompanyContacts.AsNoTracking().SingleAsync(Ct)));
            await transaction.RollbackAsync(Ct);
        }
        Assert.Equal(ContactEditSnapshot.Capture(contact), ContactEditSnapshot.Capture(await publication.CompanyContacts.AsNoTracking().SingleAsync(Ct)));
        await using (var transaction = await publication.Database.BeginTransactionAsync(Ct))
        {
            await registry.FromPostAsync(publication, contact.CompanyName, null, [found], "https://example.invalid/post", Ct);
            await transaction.CommitAsync(Ct);
        }
        var saved = await publication.CompanyContacts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(contact.Revision + 1, saved.Revision);
        Assert.Equal(contact.SeenCount + 1, saved.SeenCount);
        await NoAdmissionAsync(publication);
    }

    [Fact]
    public async Task Contact_migration_upgrades_existing_identity_without_remapping_it_or_losing_legacy_fields()
    {
        var (_, contact) = await OutreachSourceAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.GetService<IMigrator>().MigrateAsync("20261003172927_DurableBotControls", Ct);
        var before = await context.Database.SqlQuery<string>($"SELECT to_jsonb(contact)::text AS \"Value\" FROM company_contacts contact WHERE id = {contact.Id}").SingleAsync(Ct);
        await context.Database.MigrateAsync(Ct);
        var after = await context.Database.SqlQuery<string>($"SELECT (to_jsonb(contact) - 'revision' - 'retired_at')::text AS \"Value\" FROM company_contacts contact WHERE id = {contact.Id}").SingleAsync(Ct);
        Assert.Equal(before, after);
        var upgraded = await context.CompanyContacts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(0, upgraded.Revision);
        Assert.Null(upgraded.RetiredAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contact_pages_retain_displayed_witness_and_typed_draft_on_conflict_until_explicit_reload(bool vacancyPage)
    {
        var (source, contact) = await OutreachDecisionAsync();
        var capture = new ContactPageCapture(vacancyPage);
        var registration = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture).AddSingleton(pg.Db);
        foreach (var type in new[] { typeof(ContactRegistry), typeof(OutreachService), typeof(ApprovalService), typeof(ResumeTailor),
            typeof(FunnelService), typeof(RecruiterChatService), typeof(RejectionReviewService) })
            registration.AddSingleton(type, _host!.Services.GetRequiredService(type));
        using var services = registration.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = vacancyPage
                ? await renderer.RenderComponentAsync<VacancyPage>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = source.VacancyId }))
                : await renderer.RenderComponentAsync<ContactsPage>(ParameterView.Empty);
            var expected = ((Dictionary<long, ContactEditSnapshot>)OutreachPageField(capture.Page, "_contactSnapshots"))[contact.Id];
            if (vacancyPage) capture.Page.GetType().GetField("_dm", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(capture.Page, "Unsaved recruiter draft");
            else ((Dictionary<long, string>)OutreachPageField(capture.Page, "_texts"))[source.Id] = "Unsaved recruiter draft";
            var registry = _host!.Services.GetRequiredService<ContactRegistry>();
            await registry.UpsertAsync(contact.CompanyName, null, contact.Kind, contact.Value, null, null, "synthetic observation", null, Ct);
            await OutreachPageCall(capture.Page, vacancyPage ? "RemoveContact" : "Remove", contact.Id);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture.Page, null);
            Assert.Contains("контакт изменился", System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString()), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expected, ((Dictionary<long, ContactEditSnapshot>)OutreachPageField(capture.Page, "_contactSnapshots"))[contact.Id]);
            Assert.Equal("Unsaved recruiter draft", vacancyPage ? OutreachPageField(capture.Page, "_dm") : ((Dictionary<long, string>)OutreachPageField(capture.Page, "_texts"))[source.Id]);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Null((await context.CompanyContacts.AsNoTracking().SingleAsync(Ct)).RetiredAt);
            await NoAdmissionAsync(context);
            await OutreachPageCall(capture.Page, vacancyPage ? "ReloadContacts" : "ReloadAsync");
            var reloaded = ((Dictionary<long, ContactEditSnapshot>)OutreachPageField(capture.Page, "_contactSnapshots"))[contact.Id];
            Assert.NotEqual(expected, reloaded);
            await OutreachPageCall(capture.Page, vacancyPage ? "RemoveContact" : "Remove", contact.Id);
            Assert.NotNull((await context.CompanyContacts.AsNoTracking().SingleAsync(Ct)).RetiredAt);
            Assert.Single(await context.Outreaches.ToListAsync(Ct));
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
            Assert.Equal(0, _provider.Calls);
        });
    }

    private sealed class ContactPageCapture(bool vacancyPage) : IComponentActivator
    {
        public IComponent Page { get; } = vacancyPage ? new VacancyPage() : new ContactsPage();
        public IComponent CreateInstance(Type type) => type == Page.GetType() ? Page : (IComponent)Activator.CreateInstance(type)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contact_retirement_preserves_draft_history_and_joins_notice_or_rolls_back(bool fail)
    {
        var (source, contact) = await OutreachDecisionAsync();
        var snapshot = ContactEditSnapshot.Capture(contact);
        var registry = _host!.Services.GetRequiredService<ContactRegistry>();
        _fault.Active = fail;
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RemoveAsync(snapshot, Ct));
        else Assert.True((await registry.RemoveAsync(snapshot, Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.CompanyContacts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(contact.Revision + (fail ? 0 : 1), saved.Revision);
        Assert.Equal(!fail, saved.RetiredAt is not null);
        Assert.Equal(source.Text, (await context.Outreaches.AsNoTracking().SingleAsync(Ct)).Text);
        Assert.Equal(OutreachStatus.Draft, (await context.Outreaches.AsNoTracking().SingleAsync(Ct)).Status);
        if (fail) await NoAdmissionAsync(context);
        else
        {
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
            Assert.False((await registry.RemoveAsync(snapshot, Ct)).Ok);
            Assert.Single(await context.Notifications.ToListAsync(Ct));
            Assert.Empty(await registry.ForCompanyAsync(contact.CompanyName, Ct));
            Assert.False((await _host.Services.GetRequiredService<OutreachService>().SendAsync(source.Id, Ct)).Ok);
            Assert.True((await _host.Services.GetRequiredService<OutreachService>().DismissAsync(source.Id, Ct)).Ok);
        }
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Contact_revision_fences_stale_controls_and_concurrent_upserts_without_revival()
    {
        var (_, contact) = await OutreachSourceAsync();
        var registry = _host!.Services.GetRequiredService<ContactRegistry>();
        var captured = ContactEditSnapshot.Capture(contact);
        var writes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => registry.UpsertAsync(contact.CompanyName, null,
            contact.Kind, contact.Value, null, null, "synthetic-observation", null, Ct)));
        Assert.All(writes, id => Assert.Equal(contact.Id, id));
        Assert.False((await registry.RemoveAsync(captured, Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var current = await context.CompanyContacts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(4, current.Revision);
        Assert.Equal(4, current.SeenCount);
        Assert.Null(current.RetiredAt);
        await NoAdmissionAsync(context);
        Assert.True((await registry.RemoveAsync(ContactEditSnapshot.Capture(current), Ct)).Ok);
        Assert.Null(await registry.UpsertAsync(contact.CompanyName, null, contact.Kind, contact.Value, null, null, "вручную", null, Ct));
        var retired = await context.CompanyContacts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(5, retired.Revision);
        Assert.Equal(4, retired.SeenCount);
        Assert.NotNull(retired.RetiredAt);
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            await registry.FromPostAsync(context, contact.CompanyName, null,
                [new VacancyContact { Kind = contact.Kind, Value = contact.Value }], "https://example.invalid/post", Ct);
            await transaction.CommitAsync(Ct);
        }
        Assert.Equal(ContactEditSnapshot.Capture(retired), ContactEditSnapshot.Capture(await context.CompanyContacts.AsNoTracking().SingleAsync(Ct)));
    }

    [Theory]
    [InlineData(OutreachStatus.Sent)]
    [InlineData(OutreachStatus.Sending)]
    [InlineData(OutreachStatus.OutcomeUnknown)]
    public async Task Contact_retirement_cannot_destroy_delivery_or_inbound_matching_evidence(OutreachStatus status)
    {
        var (source, contact) = await OutreachDecisionAsync(ContactKind.Telegram);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Outreaches.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, status), Ct);
        Assert.False((await _host!.Services.GetRequiredService<ContactRegistry>().RemoveAsync(ContactEditSnapshot.Capture(contact), Ct)).Ok);
        Assert.Null((await context.CompanyContacts.AsNoTracking().SingleAsync(Ct)).RetiredAt);
        Assert.Equal(status, (await context.Outreaches.AsNoTracking().SingleAsync(Ct)).Status);
        await NoAdmissionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Retired_contact_cannot_publish_a_prepaid_outreach_or_admit_a_direct_message()
    {
        var (vacancy, contact) = await OutreachSourceAsync(kind: ContactKind.Telegram);
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        var captured = Assert.IsType<CapturedOutreachDraft>(await owner.CaptureAsync(vacancy.Id, contact.Id, false, Ct));
        Assert.True((await _host.Services.GetRequiredService<ContactRegistry>().RemoveAsync(ContactEditSnapshot.Capture(contact), Ct)).Ok);
        Assert.Null(await owner.PublishAsync(captured.Snapshot, "Already paid synthetic note", null, null, Ct));
        Assert.Null(await owner.CaptureAsync(vacancy.Id, contact.Id, false, Ct));
        Assert.False((await _host.Services.GetRequiredService<EmployerMessages>().RecruiterAsync(vacancy.Id, contact.Id,
            "Previously drafted synthetic message", false, true, null, Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Outreaches.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Contact_retirement_and_native_message_admission_serialize_both_race_orders(bool admissionFirst, bool email)
    {
        var (source, contact) = await OutreachDecisionAsync(email ? ContactKind.Email : ContactKind.Telegram);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _host!.Services.GetRequiredService<JobFinder.Pipeline.Profile.FactsProvider>().GetSnapshotAsync(Ct);
        await using (var reset = await pg.Db.CreateDbContextAsync(Ct)) await NotificationAdmissionAssertions.ResetAsync(reset, Ct);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task BarrierAsync() { ready.TrySetResult(); await release.Task.WaitAsync(Ct); }
        if (admissionFirst) _fault.BeforeEmployerAdmission = BarrierAsync;
        else _fault.BeforeContactRetirement = BarrierAsync;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var registry = _host!.Services.GetRequiredService<ContactRegistry>();
        var messages = _host.Services.GetRequiredService<EmployerMessages>();
        Task<ActionResult> AdmitAsync() => email
            ? messages.OutreachAsync(source.Id, "Synthetic native admission", wait.Token)
            : messages.RecruiterAsync(source.VacancyId, contact.Id, "Synthetic native admission", false, true, null, wait.Token);
        Task<JobFinder.Pipeline.Actions.ActionResult>? retirement = null;
        Task<JobFinder.Pipeline.Actions.ActionResult>? sending = null;
        try
        {
            if (admissionFirst) sending = AdmitAsync();
            else retirement = registry.RemoveAsync(ContactEditSnapshot.Capture(contact), Ct);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            if (admissionFirst) retirement = registry.RemoveAsync(ContactEditSnapshot.Capture(contact), Ct);
            else sending = AdmitAsync();
            await WaitForContactLockAsync();
            release.TrySetResult();
            var result = await retirement!.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(!admissionFirst, result.Ok);
            if (admissionFirst)
            {
                wait.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending!);
            }
            else Assert.False((await sending!.WaitAsync(TimeSpan.FromSeconds(15), Ct)).Ok);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(admissionFirst ? 1 : 0, await context.EmployerMessages.CountAsync(Ct));
            Assert.Equal(!admissionFirst, (await context.CompanyContacts.AsNoTracking().SingleAsync(Ct)).RetiredAt is not null);
            Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
            Assert.Equal(0, _provider.Calls);
        }
        finally
        {
            release.TrySetResult();
            wait.Cancel();
            if (sending is not null) try { await sending.WaitAsync(TimeSpan.FromSeconds(15), Ct); } catch (OperationCanceledException) { }
            if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        }
    }

    private async Task WaitForContactLockAsync()
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        while (DateTimeOffset.UtcNow < until)
        {
            if (await context.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%company_contacts%' AND (query LIKE '%FOR SHARE%' OR query LIKE '%FOR UPDATE%')) AS \"Value\"").SingleAsync(Ct)) return;
            await Task.Delay(20, Ct);
        }
        Assert.Fail("The competing contact command did not reach its PostgreSQL row lock");
    }

    [Fact]
    public async Task Contact_migration_preserves_legacy_rows_and_refuses_downgrade_after_a_retirement()
    {
        var (_, contact) = await OutreachSourceAsync();
        Assert.Equal(0, contact.Revision);
        Assert.Null(contact.RetiredAt);
        Assert.True((await _host!.Services.GetRequiredService<ContactRegistry>().RemoveAsync(ContactEditSnapshot.Capture(contact), Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003172927_DurableBotControls", Ct));
        Assert.Contains("Cannot downgrade retained contact revisions", error.MessageText);
        Assert.NotNull((await context.CompanyContacts.AsNoTracking().SingleAsync(Ct)).RetiredAt);
        Assert.Contains("20261003190627_RetiredCompanyContacts", await context.Database.GetAppliedMigrationsAsync(Ct));
    }
}
