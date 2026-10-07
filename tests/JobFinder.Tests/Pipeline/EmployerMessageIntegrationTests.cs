using System.Collections.Concurrent;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class EmployerMessageIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.DeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.CompanyContacts.ExecuteDeleteAsync(Ct);
        await context.HhResponses.ExecuteDeleteAsync(Ct);
        await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
        await context.Documents.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_hiring_deliver; DELETE FROM jobfinder_queues.wolverine_queue_hiring_deliver_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        context.Documents.AddRange(new AppDocument
        {
            Key = "candidate.profile", Content = JsonSerializer.Serialize(new CandidateProfile { Name = "Synthetic Candidate" }, ProfileJsonContext.Default.CandidateProfile), Version = 1,
        }, new AppDocument
        {
            Key = "candidate.facts", Content = JsonSerializer.Serialize(new CandidateFacts(), FactsJsonContext.Default.CandidateFacts), Version = 1,
        });
        await context.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    private async Task<IHost> HostAsync(bool consumer, Gate gate, Recipient recipient, SaveChangesInterceptor? interceptor = null)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
        });
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddJobFinderCore(builder.Configuration);
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        if (interceptor is null) builder.Services.AddSingleton(pg.Db);
        else
        {
            var options = new DbContextOptionsBuilder<JobFinderDbContext>();
            JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
            options.AddInterceptors(interceptor);
            builder.Services.AddSingleton<IDbContextFactory<JobFinderDbContext>>(new Factory(options.Options));
        }
        builder.Services.RemoveAll<IEmployerMessageGate>();
        builder.Services.AddSingleton<IEmployerMessageGate>(gate);
        builder.Services.RemoveAll<IDeliveryTransport>();
        builder.Services.AddSingleton<IDeliveryTransport>(recipient);
        builder.AddJobFinderMessaging(consumer);
        if (consumer) builder.Services.AddJobFinderInitialization();
        var host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<(Vacancy Vacancy, CompanyContact Contact)> SeedAsync(ContactKind kind = ContactKind.Telegram, bool applied = false)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = new Vacancy
        {
            Title = "Synthetic .NET Developer", CompanyName = "Synthetic Company", Url = "https://example.invalid/jobs/1",
            Fingerprint = Guid.NewGuid().ToString("N"), Status = applied ? VacancyStatus.Applied : VacancyStatus.Scored,
        };
        var contact = new CompanyContact
        {
            CompanyKey = JobFinder.Core.Text.VacancyFingerprint.NormalizeCompany(vacancy.CompanyName), Kind = kind,
            Value = kind == ContactKind.Email ? $"hr-{vacancy.Fingerprint[..8]}@example.invalid" : "@synthetic_" + vacancy.Fingerprint[..8],
            Source = "test", PersonName = "Synthetic HR",
        };
        context.Vacancies.Add(vacancy);
        context.CompanyContacts.Add(contact);
        await context.SaveChangesAsync(Ct);
        return (vacancy, contact);
    }

    private async Task<ApplicationEvent> ReplyAsync(ApplicationChannel channel)
    {
        var (vacancy, contact) = await SeedAsync(channel == ApplicationChannel.Email ? ContactKind.Email : ContactKind.Telegram, true);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var application = new Application
        {
            VacancyId = vacancy.Id, Status = ApplicationStatus.Sent, Channel = channel, Target = contact.Value,
            Stage = ApplicationStage.Replied, CreatedAt = DateTimeOffset.UtcNow, SentAt = DateTimeOffset.UtcNow,
        };
        var source = new ApplicationEvent
        {
            Application = application, Kind = EventKind.MessageIn, Channel = channel == ApplicationChannel.Email ? EventChannel.Email
                : channel == ApplicationChannel.HeadHunter ? EventChannel.HeadHunter : EventChannel.Telegram,
            Author = contact.Value, Text = "Когда можно поговорить?", Draft = "Завтра после 15:00 по Минску.", DraftStatus = DraftStatus.Pending,
            ExternalId = "synthetic-inbound", At = DateTimeOffset.UtcNow,
        };
        context.ApplicationEvents.Add(source);
        if (channel == ApplicationChannel.HeadHunter)
        {
            context.VacancySources.Add(new VacancySource { VacancyId = vacancy.Id, Source = SourceKind.HeadHunter, ExternalId = "923001", Url = "https://hh.ru/vacancy/923001" });
            context.HhResponses.Add(new HhResponse { HhVacancyId = 923001, ChatId = 922001, State = "response" });
        }
        await context.SaveChangesAsync(Ct);
        return source;
    }

    private async Task<EmployerMessage> MessageAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.EmployerMessages.AsNoTracking().SingleAsync(Ct);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "employer message did not reach the expected checkpoint");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Native_first_DM_records_exact_package_and_funnel_once_despite_double_click()
    {
        var (vacancy, contact) = await SeedAsync();
        var gate = new Gate();
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var messages = host.Services.GetRequiredService<EmployerMessages>();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => messages.RecruiterAsync(vacancy.Id, contact.Id, "Проверенный первый отклик.", true, true, null, Ct)));
        Assert.All(results, result => Assert.True(result.Ok, result.Message));
        Assert.Single(recipient.Deliveries);
        Assert.Equal(1, gate.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var attempt = await context.DeliveryAttempts.SingleAsync(Ct);
        var application = await context.Applications.SingleAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, attempt.Status);
        Assert.Equal(application.Id, attempt.ApplicationId);
        Assert.Equal(123, application.TailoredResumeId);
        Assert.Equal(456, application.TargetPeerId);
        Assert.Equal(EmployerMessageStatus.Sent, (await MessageAsync()).Status);
        Assert.Equal("Проверенный первый отклик.", attempt.Text);
        Assert.Equal(attempt.ResumeHash, recipient.Deliveries.Single().Resume!.Sha256);
        Assert.Equal(Gate.Bytes, recipient.Deliveries.Single().Resume!.Content);
        Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Equal(VacancyStatus.Applied, (await context.Vacancies.SingleAsync(Ct)).Status);
        var notice = await context.Notifications.SingleAsync(row => row.EntityId == vacancy.Id && row.Kind == NotificationKind.Info, Ct);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Theory]
    [InlineData(ApplicationChannel.TelegramDm)]
    [InlineData(ApplicationChannel.Email)]
    [InlineData(ApplicationChannel.HeadHunter)]
    public async Task Native_reply_uses_same_gateway_and_updates_the_exact_source(ApplicationChannel channel)
    {
        var source = await ReplyAsync(channel);
        var gate = new Gate();
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var result = await host.Services.GetRequiredService<ReplyService>().SendReplyAsync(source.Id, null, Ct,
            expected: ReplyEditSnapshot.Capture(source, source.Application!));
        Assert.True(result.Ok, result.Message);
        var dispatch = Assert.Single(recipient.Deliveries);
        Assert.Null(dispatch.Resume);
        Assert.False(dispatch.Input!.First);
        if (channel == ApplicationChannel.Email) Assert.Equal("synthetic-inbound", dispatch.Input.InReplyTo);
        if (channel == ApplicationChannel.HeadHunter) Assert.Equal(922001, dispatch.Input.HhChatId);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(DraftStatus.Sent, saved.DraftStatus);
        Assert.Equal(2, saved.Revision);
        Assert.Single(await context.Applications.ToListAsync(Ct));
        Assert.Single(await context.ApplicationEvents.Where(row => row.Kind == EventKind.MessageOut).ToListAsync(Ct));
        Assert.False((await host.Services.GetRequiredService<ReplyService>().SendReplyAsync(source.Id, "Другая попытка", Ct)).Ok);
        Assert.Single(recipient.Deliveries);
    }

    [Fact]
    public async Task Source_dismissal_during_paid_review_cancels_without_external_effect()
    {
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var gate = new Gate { Hold = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var sending = host.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, Ct);
        await gate.Started.Task.WaitAsync(Ct);
        Assert.True((await host.Services.GetRequiredService<ReplyService>().DismissDraftAsync(source.Id, Ct)).Ok);
        gate.Release.TrySetResult();
        Assert.False((await sending).Ok);
        Assert.Equal(EmployerMessageStatus.Cancelled, (await MessageAsync()).Status);
        Assert.Empty(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_duplicate_prepare_and_dispatch_commands_do_not_repeat_effects_or_mark_live_delivery_unknown()
    {
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var gate = new Gate { Hold = true };
        var recipient = new Recipient { Hold = true };
        var host = await HostAsync(true, gate, recipient);
        var sending = host.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, Ct);
        await gate.Started.Task.WaitAsync(Ct);
        var id = (await MessageAsync()).Id;
        var workflow = host.Services.GetRequiredService<EmployerMessageWorkflow>();
        await workflow.PrepareAsync(id, 1, Guid.NewGuid(), Ct);
        Assert.Equal(1, gate.Calls);
        gate.Release.TrySetResult();
        await recipient.Started.Task.WaitAsync(Ct);
        await workflow.DispatchAsync(id, 1, Guid.NewGuid(), Ct);
        Assert.Equal(EmployerMessageStatus.Sending, (await MessageAsync()).Status);
        recipient.Release.TrySetResult();
        Assert.True((await sending).Ok);
        Assert.Single(recipient.Deliveries);
    }

    [Fact]
    public async Task Producer_can_exit_before_native_consumer_starts_without_losing_the_approved_text()
    {
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sending = producer.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, wait.Token);
        await UntilAsync(async () => { await using var context = await pg.Db.CreateDbContextAsync(Ct); return await context.EmployerMessages.AnyAsync(Ct); });
        wait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await producer.StopAsync(Ct);
        await HostAsync(true, gate, recipient);
        await UntilAsync(async () => (await MessageAsync()).Status == EmployerMessageStatus.Sent);
        Assert.Equal(source.Draft, Assert.Single(recipient.Deliveries).Text);
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task Ambiguous_delivery_freezes_reply_and_blocks_every_retry_including_an_override()
    {
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var recipient = new Recipient { Failure = new IOException("synthetic partial text/PDF delivery") };
        var host = await HostAsync(true, new Gate(), recipient);
        var replies = host.Services.GetRequiredService<ReplyService>();
        Assert.False((await replies.SendReplyAsync(source.Id, null, Ct)).Ok);
        Assert.Equal(EmployerMessageStatus.OutcomeUnknown, (await MessageAsync()).Status);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DraftStatus.OutcomeUnknown, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
        Assert.False((await replies.SendReplyAsync(source.Id, "повтор", Ct)).Ok);
        Assert.False((await replies.DismissDraftAsync(source.Id, Ct)).Ok);
        Assert.Single(recipient.Deliveries);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Receipt_commit_failure_recovers_original_native_envelope_without_resending()
    {
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var fault = new ReceiptFault();
        var recipient = new Recipient();
        var host = await HostAsync(true, new Gate(), recipient, fault);
        Assert.False((await host.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, Ct).WaitAsync(TimeSpan.FromSeconds(25), Ct)).Ok);
        Assert.Equal(1, fault.Failures);
        Assert.Equal(EmployerMessageStatus.OutcomeUnknown, (await MessageAsync()).Status);
        Assert.Single(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.ApplicationEvents.Where(row => row.Kind == EventKind.MessageOut).ToListAsync(Ct));
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Emergency_auto_reply_switch_is_rechecked_after_expensive_review()
    {
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var gate = new Gate { Hold = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var sending = host.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, true, 0, Ct);
        await gate.Started.Task.WaitAsync(Ct);
        await host.Services.GetRequiredService<RuntimeSettings>().SetAsync(RuntimeSettings.ChatAutoReply, "false", Ct);
        gate.Release.TrySetResult();
        Assert.False((await sending).Ok);
        Assert.Empty(recipient.Deliveries);
        Assert.Equal(EmployerMessageStatus.Cancelled, (await MessageAsync()).Status);
    }

    [Fact]
    public async Task Changed_candidate_version_during_review_invalidates_preparation()
    {
        var source = await ReplyAsync(ApplicationChannel.Email);
        var gate = new Gate { Hold = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var sending = host.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, Ct);
        await gate.Started.Task.WaitAsync(Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Documents.Where(row => row.Key == "candidate.facts").ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, 2), Ct);
        gate.Release.TrySetResult();
        Assert.False((await sending).Ok);
        Assert.Equal(EmployerMessageStatus.Cancelled, (await MessageAsync()).Status);
        Assert.Empty(recipient.Deliveries);
    }

    [Fact]
    public async Task Weak_message_and_tampered_attachment_never_reach_recipient()
    {
        var (vacancy, contact) = await SeedAsync();
        var gate = new Gate { PreparationFailure = new ResumeQualityException("7/10") };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        Assert.False((await host.Services.GetRequiredService<EmployerMessages>().RecruiterAsync(vacancy.Id, contact.Id, "первый текст", true, true, null, Ct)).Ok);
        Assert.Equal(EmployerMessageStatus.NeedsManual, (await MessageAsync()).Status);
        gate.PreparationFailure = null;
        gate.RestoreFailure = new ResumeQualityException("PDF hash mismatch");
        Assert.False((await host.Services.GetRequiredService<EmployerMessages>().RecruiterAsync(vacancy.Id, contact.Id, "второй текст", true, true, null, Ct)).Ok);
        Assert.Empty(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.All(await context.EmployerMessages.ToListAsync(Ct), row => Assert.Equal(EmployerMessageStatus.NeedsManual, row.Status));
    }

    [Fact]
    public async Task Waiting_for_durable_resume_does_not_block_a_healthy_reply_or_repeat_the_paid_message_review()
    {
        var (vacancy, contact) = await SeedAsync();
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var gate = new Gate { WaitForResume = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var messages = host.Services.GetRequiredService<EmployerMessages>();
        var waiting = messages.RecruiterAsync(vacancy.Id, contact.Id, "первый отклик", true, true, null, Ct);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.EmployerMessages.AnyAsync(row => row.VacancyId == vacancy.Id && row.ResumeRequestId == gate.Dependency && row.Review != null, Ct);
        });
        Assert.True((await messages.ReplyAsync(source.Id, null, false, 0, Ct).WaitAsync(TimeSpan.FromSeconds(12), Ct)).Ok);
        Assert.False(waiting.IsCompleted);
        Assert.Single(recipient.Deliveries);
        gate.ResumeAvailable = true;
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(25), Ct)).Ok);
        Assert.Equal(2, gate.Calls);
        Assert.Equal(2, recipient.Deliveries.Count);
    }

    [Theory]
    [InlineData(ContactKind.Telegram)]
    [InlineData(ContactKind.Email)]
    public async Task Outreach_facade_uses_native_content_and_delivery_gates(ContactKind kind)
    {
        var (vacancy, contact) = await SeedAsync(kind);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var outreach = new Outreach { VacancyId = vacancy.Id, ContactId = contact.Id, Text = "текст карточки", Fresh = true };
        context.Outreaches.Add(outreach);
        await context.SaveChangesAsync(Ct);
        var recipient = new Recipient();
        var host = await HostAsync(true, new Gate(), recipient);
        var result = await host.Services.GetRequiredService<OutreachService>().SendAsync(outreach.Id, Ct, "подтверждённая редакция");
        Assert.True(result.Ok, result.Message);
        Assert.Equal("подтверждённая редакция", Assert.Single(recipient.Deliveries).Text);
        Assert.NotNull(recipient.Deliveries.Single().Resume);
        await context.Entry(outreach).ReloadAsync(Ct);
        Assert.Equal(OutreachStatus.Sent, outreach.Status);
        Assert.Equal(2, outreach.Revision);
        Assert.Equal("подтверждённая редакция", outreach.Text);
        Assert.Single(await context.Applications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Hh_letter_owner_requires_a_synced_chat_and_records_only_the_letter_not_another_application()
    {
        var source = await ReplyAsync(ApplicationChannel.HeadHunter);
        var recipient = new Recipient();
        var host = await HostAsync(true, new Gate(), recipient);
        var result = await host.Services.GetRequiredService<EmployerMessages>().HhAsync(source.Application!.VacancyId, "сопроводительное", true, Ct);
        Assert.True(result.Ok, result.Message);
        var dispatch = Assert.Single(recipient.Deliveries);
        Assert.Equal(EmployerMessageKind.HhLetter, dispatch.Input!.Kind);
        Assert.Equal(922001, dispatch.Input.HhChatId);
        Assert.True(dispatch.Input.First);
        Assert.Null(dispatch.Resume);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Applications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Ready_checkpoint_survives_host_restart_without_another_paid_review()
    {
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sending = producer.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, wait.Token);
        await UntilAsync(async () => { await using var context = await pg.Db.CreateDbContextAsync(Ct); return await context.EmployerMessages.AnyAsync(Ct); });
        producer.Services.GetRequiredService<ServerOwnership>().Acquired();
        await producer.Services.GetRequiredService<EmployerMessageWorkflow>().PrepareAsync((await MessageAsync()).Id, 1, Guid.NewGuid(), Ct);
        Assert.Equal(EmployerMessageStatus.Ready, (await MessageAsync()).Status);
        wait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await producer.StopAsync(Ct);
        await HostAsync(true, gate, recipient);
        await UntilAsync(async () => (await MessageAsync()).Status == EmployerMessageStatus.Sent);
        Assert.Equal(1, gate.Calls);
        Assert.Single(recipient.Deliveries);
    }

    [Fact]
    public async Task Startup_recovers_interrupted_message_and_source_as_unknown_without_automatic_resubmission()
    {
        var source = await ReplyAsync(ApplicationChannel.TelegramDm);
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sending = producer.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, wait.Token);
        await UntilAsync(async () => { await using var context = await pg.Db.CreateDbContextAsync(Ct); return await context.EmployerMessages.AnyAsync(Ct); });
        producer.Services.GetRequiredService<ServerOwnership>().Acquired();
        await producer.Services.GetRequiredService<EmployerMessageWorkflow>().PrepareAsync((await MessageAsync()).Id, 1, Guid.NewGuid(), Ct);
        var message = await MessageAsync();
        message.DispatchEnvelopeId = Guid.NewGuid();
        var input = EmployerMessageSources.Read(message);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = await EmployerMessageSources.VacancyAsync(context, message.VacancyId, Ct);
        var dispatch = await gate.RestoreAsync(message, input, vacancy!, Ct);
        Assert.NotNull(await producer.Services.GetRequiredService<DeliveryLedger>().TryClaimAsync(dispatch, Ct));
        wait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await producer.StopAsync(Ct);
        await HostAsync(true, gate, recipient);
        Assert.Equal(EmployerMessageStatus.OutcomeUnknown, (await MessageAsync()).Status);
        Assert.Equal(DraftStatus.OutcomeUnknown, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
        Assert.Empty(recipient.Deliveries);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_reconciliation_preserves_history_and_does_not_send_or_certify_the_submitted_PDF(bool sent)
    {
        var (vacancy, contact) = await SeedAsync();
        var recipient = new Recipient { Failure = new IOException("synthetic uncertain package") };
        var host = await HostAsync(true, new Gate(), recipient);
        Assert.False((await host.Services.GetRequiredService<EmployerMessages>().RecruiterAsync(vacancy.Id, contact.Id, "первый отклик", true, true, null, Ct)).Ok);
        var id = (await MessageAsync()).Id;
        var ledger = host.Services.GetRequiredService<DeliveryLedger>();
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.ReconcileMessageAsync(id, sent, "", Ct));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => ledger.ReconcileMessageAsync(id, sent, "Пользователь проверил чат и подтвердил результат", Ct)));
        Assert.Single(results, result => result);
        Assert.Single(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var attempt = await context.DeliveryAttempts.SingleAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, attempt.Status);
        Assert.Equal(123, attempt.TailoredResumeId);
        var message = await MessageAsync();
        Assert.Equal(sent ? EmployerMessageStatus.Sent : EmployerMessageStatus.NeedsManual, message.Status);
        var notice = Assert.Single(await context.Notifications.Where(row => row.Text.StartsWith("🔎 Доставка сообщения")).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        if (sent)
        {
            var application = await context.Applications.SingleAsync(Ct);
            Assert.Null(application.TailoredResumeId);
            Assert.DoesNotContain("📎", (await context.ApplicationEvents.SingleAsync(Ct)).Text!);
        }
        else Assert.Empty(await context.Applications.ToListAsync(Ct));
    }

    private sealed class Gate : IEmployerMessageGate
    {
        public static byte[] Bytes => [1, 2, 3, 4];
        public bool Hold { get; init; }
        public bool WaitForResume { get; init; }
        public volatile bool ResumeAvailable;
        public Guid Dependency { get; } = Guid.NewGuid();
        public Exception? PreparationFailure { get; set; }
        public Exception? RestoreFailure { get; set; }
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<EmployerMessagePreparation> PrepareAsync(EmployerMessage message, EmployerMessageInput input, Vacancy vacancy, CancellationToken ct)
        {
            if (message.Review is null) Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            if (PreparationFailure is { } error) throw error;
            var review = message.Review ?? new ApplicationLetterReview
            {
                Score = 10, TextHash = Fingerprint.Of(input.Text), ContextHash = input.ContextHash,
            };
            var package = Package(message, input, vacancy, review);
            return input.AttachResume && WaitForResume && !ResumeAvailable
                ? new EmployerMessagePreparation.ResumePending(Dependency, review) : new EmployerMessagePreparation.Ready(package);
        }

        public Task<ApprovedDispatch> RestoreAsync(EmployerMessage message, EmployerMessageInput input, Vacancy vacancy, CancellationToken ct)
        {
            if (RestoreFailure is { } error) throw error;
            return Task.FromResult(Package(message, input, vacancy, message.Review!));
        }

        private static ApprovedDispatch Package(EmployerMessage message, EmployerMessageInput input, Vacancy vacancy, ApplicationLetterReview review) => new(
            new Application { Id = input.ApplicationId ?? 0, VacancyId = input.VacancyId, Vacancy = vacancy, Channel = input.Channel, Target = input.Target },
            input.Text, review, input.AttachResume ? new EmailAttachment("synthetic.pdf", "Synthetic_CV.pdf", 123,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Bytes)), Bytes) : null, input.Subject, false, message, input);
    }

    private sealed class Recipient : IDeliveryTransport
    {
        public ConcurrentQueue<ApprovedDispatch> Deliveries { get; } = new();
        public bool Hold { get; init; }
        public Exception? Failure { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeliveryReceipt> SendAsync(ApprovedDispatch dispatch, CancellationToken ct)
        {
            Deliveries.Enqueue(dispatch);
            Started.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            if (Failure is { } error) throw error;
            return new DeliveryReceipt(ApplicationStatus.Sent, TargetPeerId: 456, EmailMessageId: "synthetic-outbound");
        }
    }

    private sealed class ReceiptFault : SaveChangesInterceptor
    {
        public int Failures;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Failures == 0 && eventData.Context!.ChangeTracker.Entries<DeliveryAttempt>().Any(entry => entry.Entity.Status == DeliveryAttemptStatus.Confirmed))
            {
                Interlocked.Increment(ref Failures);
                throw new IOException("synthetic receipt commit failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }
}
