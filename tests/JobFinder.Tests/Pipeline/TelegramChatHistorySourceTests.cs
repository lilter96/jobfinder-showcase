using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class HhNegotiationSourceIntegrationTests
{
    private async Task<(Application Application, TelegramChatHistorySources Owner, TelegramHistorySnapshot Snapshot)> TelegramHistoryAsync(
        bool external = false, SaveChangesInterceptor? fault = null)
    {
        var application = await SeedAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var key = VacancyFingerprint.NormalizeCompany(application.Vacancy!.CompanyName);
            await context.CompanyContacts.Where(row => row.CompanyKey == key && row.Kind == ContactKind.Telegram && row.Value == "@codeval").ExecuteDeleteAsync(Ct);
            context.CompanyContacts.Add(new()
            {
                CompanyKey = key, Kind = ContactKind.Telegram,
                Value = "@codeval", Source = "Synthetic vacancy", FirstSeenAt = application.CreatedAt, LastSeenAt = application.CreatedAt,
            });
            if (external) await context.Applications.Where(row => row.Id == application.Id).ExecuteDeleteAsync(Ct);
            await context.SaveChangesAsync(Ct);
        }
        await SourcesAsync(fault);
        var owner = _host!.Services.GetRequiredService<TelegramChatHistorySources>();
        var snapshot = Assert.IsType<TelegramHistorySnapshot>(await owner.CaptureAsync(application.VacancyId, "CodeVal", 99, external, Ct));
        return (application, owner, snapshot);
    }

    private static TgDirectHistory TelegramMessages(Application application) => new(555, "CodeVal", "Synthetic Recruiter",
    [
        new(10, true, "Synthetic candidate message", application.CreatedAt.AddDays(1)),
        new(11, false, "Synthetic recruiter reply", application.CreatedAt.AddDays(2)),
        new(99, false, "Excluded live message", application.CreatedAt.AddDays(3)),
        new(9, false, "Outside captured history window", application.CreatedAt.AddDays(-15)),
    ]);

    [Fact]
    public async Task Telegram_history_publishes_exact_events_and_native_notice_without_analysis_or_delivery()
    {
        var (application, owner, snapshot) = await TelegramHistoryAsync();
        var history = TelegramMessages(application);
        history.Messages.Add(history.Messages[0]);
        Assert.Equal(2, await owner.PublishAsync(snapshot, history, Ct));
        Assert.Equal(0, await owner.PublishAsync(snapshot, history, Ct));
        var next = Assert.IsType<TelegramHistorySnapshot>(await owner.CaptureAsync(application.VacancyId, "@codeval", 99, false, Ct));
        Assert.Equal(0, await owner.PublishAsync(next, history, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var events = await context.ApplicationEvents.OrderBy(row => row.ExternalId).ToListAsync(Ct);
        Assert.Equal(["555/10", "555/11"], events.Select(row => row.ExternalId));
        Assert.All(events, row => Assert.Equal(DraftStatus.None, row.DraftStatus));
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(application.Status, saved.Status);
        Assert.Equal(application.Stage, saved.Stage);
        Assert.Equal(application.Revision + 1, saved.Revision);
        Assert.Null(saved.TailoredResumeId);
        Assert.Equal(history.Messages[1].Date, saved.LastInboundAt);
        Assert.Equal(history.Messages[0].Date, saved.LastOutboundAt);
        Assert.Empty(await context.ReplyProcessingRequests.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
        var notice = await context.Notifications.SingleAsync(Ct);
        Assert.Contains("без отправки и подтверждения доставки резюме", notice.Text);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Fact]
    public async Task Telegram_history_explicitly_creates_an_external_application_without_certifying_a_resume()
    {
        var (application, owner, snapshot) = await TelegramHistoryAsync(external: true);
        Assert.Null(await owner.CaptureAsync(application.VacancyId, "@codeval", 99, false, Ct));
        Assert.Equal(2, await owner.PublishAsync(snapshot, TelegramMessages(application), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(ApplicationMode.External, saved.Mode);
        Assert.Equal(ApplicationChannel.TelegramDm, saved.Channel);
        Assert.Equal("@codeval", saved.Target);
        Assert.Equal(555, saved.TargetPeerId);
        Assert.Equal(ApplicationStage.Replied, saved.Stage);
        Assert.Null(saved.DeliveryRequestId);
        Assert.Null(saved.TailoredResumeId);
        Assert.Equal(VacancyStatus.Applied, (await context.Vacancies.SingleAsync(Ct)).Status);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("contact")]
    [InlineData("vacancy")]
    [InlineData("pending")]
    [InlineData("sending")]
    [InlineData("unknown")]
    [InlineData("incoming")]
    public async Task Telegram_history_refuses_changed_sources_and_pending_or_uncertain_delivery(string change)
    {
        var (application, owner, snapshot) = await TelegramHistoryAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (change == "revision") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        if (change == "contact") await context.CompanyContacts.ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        if (change == "vacancy") await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.CompanyName, "Another company"), Ct);
        if (change == "pending") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, ApplicationStatus.Pending), Ct);
        if (change == "sending") await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, ApplicationStatus.Sending), Ct);
        if (change == "unknown")
        {
            context.EmployerMessages.Add(new() { Id = Guid.NewGuid(), VacancyId = application.VacancyId, BusinessKey = "Synthetic uncertain telegram",
                Status = EmployerMessageStatus.OutcomeUnknown, InputJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        if (change == "incoming") await _host!.Services.GetRequiredService<ReplyProcessingRequests>().IncomingAsync(new()
        {
            ApplicationId = application.Id, Channel = EventChannel.Telegram, Author = "@codeval", Text = "Synthetic live reply",
            ExternalId = "555/99", At = DateTimeOffset.UtcNow,
        }, Ct);
        Assert.Equal(0, await owner.PublishAsync(snapshot, TelegramMessages(application), Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Telegram_history_claims_only_exact_unique_text_and_preserves_its_original_event()
    {
        var (application, owner, original) = await TelegramHistoryAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var history = TelegramMessages(application);
        var entry = new ApplicationEvent { ApplicationId = application.Id, At = history.Messages[0].Date,
            Channel = EventChannel.Telegram, Kind = EventKind.MessageOut, Text = history.Messages[0].Text + "\n📎 Synthetic CV.pdf", Revision = 7 };
        context.ApplicationEvents.Add(entry);
        await context.SaveChangesAsync(Ct);
        Assert.Equal(0, await owner.PublishAsync(original, history, Ct));
        var snapshot = Assert.IsType<TelegramHistorySnapshot>(await owner.CaptureAsync(application.VacancyId, "@codeval", 99, false, Ct));
        Assert.Equal(1, await owner.PublishAsync(snapshot, history, Ct));
        await context.Entry(entry).ReloadAsync(Ct);
        Assert.Equal("555/10", entry.ExternalId);
        Assert.Equal(8, entry.Revision);
        Assert.Contains("📎 Synthetic CV.pdf", entry.Text);
        Assert.Equal(2, await context.ApplicationEvents.CountAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Contains("сопоставлено 1", (await context.Notifications.SingleAsync(Ct)).Text);
    }

    [Fact]
    public async Task Telegram_history_holds_the_captured_contact_through_native_notice_commit()
    {
        var barrier = new HistoryBarrier();
        var (application, owner, snapshot) = await TelegramHistoryAsync(fault: barrier);
        var publishing = owner.PublishAsync(snapshot, TelegramMessages(application), Ct);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var changing = context.CompanyContacts.Where(row => row.Value == "@codeval")
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        try { Assert.NotSame(changing, await Task.WhenAny(changing, Task.Delay(150, Ct))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(2, await publishing.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(1, await changing.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(2, await context.ApplicationEvents.CountAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Telegram_history_rejects_peer_changes_and_conflicting_duplicate_messages()
    {
        var (application, owner, _) = await TelegramHistoryAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.Channel, ApplicationChannel.TelegramDm)
            .SetProperty(row => row.Target, "@codeval").SetProperty(row => row.TargetPeerId, 444), Ct);
        var snapshot = Assert.IsType<TelegramHistorySnapshot>(await owner.CaptureAsync(application.VacancyId, "@codeval", null, false, Ct));
        Assert.Equal(0, await owner.PublishAsync(snapshot, TelegramMessages(application), Ct));
        var damaged = TelegramMessages(application) with { UserId = 444 };
        damaged.Messages.Add(damaged.Messages[0] with { Text = "Changed duplicate message" });
        await Assert.ThrowsAsync<ArgumentException>(() => owner.PublishAsync(snapshot, damaged, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => owner.PublishAsync(snapshot with { Version = 99 }, TelegramMessages(application), Ct));
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Telegram_history_capture_refuses_unrelated_or_retired_contacts()
    {
        var (application, owner, _) = await TelegramHistoryAsync();
        Assert.Null(await owner.CaptureAsync(application.VacancyId, "@otherperson", null, true, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CompanyContacts.ExecuteUpdateAsync(update => update.SetProperty(row => row.RetiredAt, DateTimeOffset.UtcNow), Ct);
        Assert.Null(await owner.CaptureAsync(application.VacancyId, "@codeval", null, true, Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Telegram_history_captured_controls_cannot_be_changed_before_publication()
    {
        var (application, owner, snapshot) = await TelegramHistoryAsync();
        var history = TelegramMessages(application);
        Assert.Equal(0, await owner.PublishAsync(snapshot with { ExceptMessageId = null }, history, Ct));
        Assert.Equal(0, await owner.PublishAsync(snapshot with { CreateApplication = true }, history, Ct));
        Assert.Equal(0, await owner.PublishAsync(snapshot with { CapturedAt = snapshot.CapturedAt.AddMinutes(1) }, history, Ct));
        Assert.Equal(2, await owner.PublishAsync(snapshot, history, Ct));
    }

    [Fact]
    public async Task Telegram_history_does_not_reparent_an_external_id_owned_by_another_application()
    {
        var (application, owner, snapshot) = await TelegramHistoryAsync();
        var other = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.ApplicationEvents.Add(new() { ApplicationId = other.Id, At = application.CreatedAt.AddDays(1), Channel = EventChannel.Telegram,
            Kind = EventKind.MessageOut, ExternalId = "555/10", Text = "Existing foreign history" });
        await context.SaveChangesAsync(Ct);
        Assert.Equal(1, await owner.PublishAsync(snapshot, TelegramMessages(application), Ct));
        Assert.Equal(other.Id, (await context.ApplicationEvents.SingleAsync(row => row.ExternalId == "555/10", Ct)).ApplicationId);
        Assert.Equal(application.Id, (await context.ApplicationEvents.SingleAsync(row => row.ExternalId == "555/11", Ct)).ApplicationId);
        Assert.Equal(2, await context.ApplicationEvents.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Telegram_history_source_events_and_native_notice_roll_back_together(bool external)
    {
        var fault = new TelegramHistoryFault();
        var (application, owner, snapshot) = await TelegramHistoryAsync(external, fault);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishAsync(snapshot, TelegramMessages(application), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(external ? 0 : 1, await context.Applications.CountAsync(Ct));
        Assert.Equal(VacancyStatus.Scored, (await context.Vacancies.SingleAsync(Ct)).Status);
        fault.Active = false;
        owner = ActivatorUtilities.CreateInstance<TelegramChatHistorySources>(_host!.Services);
        Assert.Equal(2, await owner.PublishAsync(snapshot, TelegramMessages(application), Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    private sealed class TelegramHistoryFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Active && eventData.Context is JobFinder.Data.JobFinderDbContext context
                && context.ChangeTracker.Entries<Notification>().Any()) throw new InvalidOperationException("Synthetic telegram history rollback");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class TelegramHistoryMergeTests
{
    [Fact]
    public void Shared_greeting_or_attachment_only_is_not_proof_of_the_same_message()
    {
        var at = DateTimeOffset.UtcNow;
        var greeting = "Здравствуйте! Пишу вам по вакансии Senior .NET Developer и хочу уточнить ";
        var stored = new ApplicationEvent { Kind = EventKind.MessageOut, Channel = EventChannel.Telegram, At = at,
            Text = greeting + "формат собеседования." };
        var file = new ApplicationEvent { Kind = EventKind.MessageOut, Channel = EventChannel.Telegram, At = at, Text = "📎 Synthetic CV.pdf" };
        var (added, claimed) = TelegramChatHistorySources.Merge([stored, file], 555, "@codeval",
            [new(10, true, greeting + "дальнейшие шаги.", at), new(11, true, "📎 Synthetic CV.pdf", at)]);
        Assert.Equal(2, added.Count);
        Assert.Empty(claimed);
    }

    [Fact]
    public void Ambiguous_same_text_is_not_claimed_and_duplicate_ids_are_not_added_twice()
    {
        var at = DateTimeOffset.UtcNow;
        var first = new ApplicationEvent { Kind = EventKind.MessageOut, Channel = EventChannel.Telegram, At = at, Text = "Спасибо!" };
        var second = new ApplicationEvent { Kind = EventKind.MessageOut, Channel = EventChannel.Telegram, At = at, Text = "Спасибо!" };
        var message = new TgDirectMessage(10, true, "Спасибо!", at);
        var (added, claimed) = TelegramChatHistorySources.Merge([first, second], 555, "@codeval", [message, message]);
        Assert.Single(added);
        Assert.Empty(claimed);
    }
}
