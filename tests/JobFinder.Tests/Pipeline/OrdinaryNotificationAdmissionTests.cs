using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Telegram;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;
    private readonly AdmissionFault _fault = new();
    private readonly OutreachProvider _provider = new();
    private readonly string _seedFile = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-channel-seed-" + Guid.NewGuid().ToString("N") + ".json");

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.DeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.ReplyProcessingRequests.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_hiring_replies, jobfinder_queues.wolverine_queue_hiring_replies_scheduled", Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.ApplicationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Outreaches.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.CompanyContacts.ExecuteDeleteAsync(Ct);
        await context.ChannelMembershipAttempts.ExecuteDeleteAsync(Ct);
        await context.ChannelMembershipRequests.ExecuteDeleteAsync(Ct);
        await ChannelMembershipTests.ClearMembershipEnvelopesAsync(context);
        await context.TelegramChannelDecisionRevisions.ExecuteDeleteAsync(Ct);
        await context.TelegramChannelApprovalBatches.ExecuteDeleteAsync(Ct);
        await context.TelegramChannels.ExecuteDeleteAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("bot.edit.")).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { _host.Dispose(); }
        }
        _provider.Dispose();
    }

    private async Task<IHost> HostAsync(bool consumer = false)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: _fault, configure: services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(_provider);
            services.AddSingleton(new TelegramOptions { SeedFile = _seedFile });
            services.AddSingleton(provider => new JobFinder.Bot.OwnerBotMessages(new JobFinder.Bot.BotOptions { OwnerUserId = 123456 },
                provider.GetRequiredService<NotificationDeliveryRequests>(), TimeProvider.System));
        });
        await _host.StartAsync(Ct);
        return _host;
    }

    private async Task<Vacancy> VacancyAsync(int attempts = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var vacancy = new Vacancy
        {
            Title = "Synthetic .NET developer", CompanyName = "Synthetic Company", Description = "Synthetic backend vacancy",
            Url = "https://example.invalid/jobs/synthetic", Fingerprint = Guid.NewGuid().ToString("N"), Status = VacancyStatus.New,
            Attempts = attempts, FirstSeenAt = now, UpdatedAt = now,
        };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Vacancies.Add(vacancy);
        await context.SaveChangesAsync(Ct);
        return vacancy;
    }

    private async Task<Application> ApplicationAsync(ApplicationStatus status = ApplicationStatus.Pending, bool old = false)
    {
        var vacancy = await VacancyAsync();
        var application = new Application
        {
            VacancyId = vacancy.Id, Channel = ApplicationChannel.Email, Mode = ApplicationMode.Approved, Status = status,
            Revision = 3, CreatedAt = DateTimeOffset.UtcNow.AddDays(old ? -30 : -1), CoverLetter = "Synthetic letter", Target = null,
        };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        return application;
    }

    [Fact]
    public async Task Invalid_legacy_application_retirement_and_native_notice_publish_once_without_paid_preparation()
    {
        var application = await ApplicationAsync();
        var host = await HostAsync();
        var worker = ActivatorUtilities.CreateInstance<ApplyWorker>(host.Services);
        Assert.Equal(1, await worker.ReconcileAsync(Ct));
        Assert.Equal(0, await worker.ReconcileAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(ApplicationStatus.NeedsManual, saved.Status);
        Assert.Equal(application.Revision + 1, saved.Revision);
        var notice = await context.Notifications.SingleAsync(Ct);
        Assert.Equal(NotificationKind.ApplyFailed, notice.Kind);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Equal(0, _provider.Calls);
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Legacy_retirement_rolls_back_the_source_if_native_admission_fails_after_its_intermediate_save()
    {
        var application = await ApplicationAsync();
        var host = await HostAsync();
        _fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ActivatorUtilities.CreateInstance<ApplyWorker>(host.Services).ReconcileAsync(Ct));
        Assert.True(_fault.SourceWasSaved);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(ApplicationStatus.Pending, saved.Status);
        Assert.Equal(application.Revision, saved.Revision);
        Assert.Null(saved.Error);
        await NoAdmissionAsync(context);
        _fault.Active = false;
        Assert.Equal(1, await ActivatorUtilities.CreateInstance<ApplyWorker>(host.Services).ReconcileAsync(Ct));
    }

    [Theory]
    [InlineData(ApplicationDeliveryStage.Sending)]
    [InlineData(ApplicationDeliveryStage.OutcomeUnknown)]
    public async Task An_unlinked_unresolved_native_delivery_is_not_retired_and_does_not_starve_other_legacy_work(ApplicationDeliveryStage stage)
    {
        var unresolved = await ApplicationAsync();
        var healthy = await ApplicationAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = new ApplicationDeliveryRequest
        {
            Id = Guid.NewGuid(), ApplicationId = unresolved.Id, ApplicationRevision = unresolved.Revision,
            Stage = stage, CreatedAt = DateTimeOffset.UtcNow, InputJson = "{}", InputHash = CanonicalJson.Hash("{}"),
        };
        context.ApplicationDeliveryRequests.Add(root);
        await context.SaveChangesAsync(Ct);
        var host = await HostAsync();
        Assert.Equal(1, await ActivatorUtilities.CreateInstance<ApplyWorker>(host.Services).ReconcileAsync(Ct));
        Assert.Equal(ApplicationStatus.Pending, (await context.Applications.SingleAsync(row => row.Id == unresolved.Id, Ct)).Status);
        Assert.Equal(ApplicationStatus.NeedsManual, (await context.Applications.SingleAsync(row => row.Id == healthy.Id, Ct)).Status);
        Assert.Equal(stage, (await context.ApplicationDeliveryRequests.AsNoTracking().SingleAsync(Ct)).Stage);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(ApplicationDeliveryStage.Sending)]
    [InlineData(ApplicationDeliveryStage.OutcomeUnknown)]
    public async Task A_delivery_admitted_after_the_import_snapshot_is_rechecked_before_retiring_that_source(ApplicationDeliveryStage stage)
    {
        var application = await ApplicationAsync();
        var snapshot = new ApplicationImportSources.PendingApplication(application.Id, application.VacancyId, application.Revision,
            null, application.Channel, null, application.CoverLetter);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.ApplicationDeliveryRequests.Add(new ApplicationDeliveryRequest
        {
            Id = Guid.NewGuid(), ApplicationId = application.Id, ApplicationRevision = application.Revision,
            Stage = stage, CreatedAt = DateTimeOffset.UtcNow, InputJson = "{}", InputHash = CanonicalJson.Hash("{}"),
        });
        await context.SaveChangesAsync(Ct);
        var host = await HostAsync();
        var retired = ApplicationImportSources.RetireInvalidAsync(host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(),
            host.Services.GetRequiredService<Notifier>(), snapshot, Ct);
        Assert.False(await retired);
        var saved = await context.Applications.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(ApplicationStatus.Pending, saved.Status);
        Assert.Equal(application.Revision, saved.Revision);
        Assert.Equal(stage, (await context.ApplicationDeliveryRequests.SingleAsync(Ct)).Stage);
        await NoAdmissionAsync(context);
    }

    [Theory]
    [InlineData(DeliveryAttemptStatus.Submitting)]
    [InlineData(DeliveryAttemptStatus.OutcomeUnknown)]
    public async Task An_unowned_unresolved_attempt_also_fences_legacy_retirement_without_starving_healthy_work(DeliveryAttemptStatus status)
    {
        var unresolved = await ApplicationAsync();
        var healthy = await ApplicationAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), ApplicationId = unresolved.Id, ApplicationRevision = unresolved.Revision,
            OperationId = Guid.NewGuid(), Channel = ApplicationChannel.Email, Text = "Synthetic attempt", TextHash = new string('a', 64),
            Status = status, CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        var host = await HostAsync();
        Assert.Equal(1, await ActivatorUtilities.CreateInstance<ApplyWorker>(host.Services).ReconcileAsync(Ct));
        Assert.Equal(ApplicationStatus.Pending, (await context.Applications.SingleAsync(row => row.Id == unresolved.Id, Ct)).Status);
        Assert.Equal(ApplicationStatus.NeedsManual, (await context.Applications.SingleAsync(row => row.Id == healthy.Id, Ct)).Status);
        Assert.Equal(status, (await context.DeliveryAttempts.AsNoTracking().SingleAsync(Ct)).Status);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_dry_run_disposition_and_native_notice_are_one_transaction(bool fail)
    {
        var application = await ApplicationAsync(ApplicationStatus.DryRun, old: true);
        var host = await HostAsync();
        _fault.Active = fail;
        await using var context = await host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(Ct);
        var notifier = host.Services.GetRequiredService<Notifier>();
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyWorker.ReleaseHeldAsync(context, notifier, DateTimeOffset.UtcNow, Ct));
        else Assert.Equal((0, 1), await ApplyWorker.ReleaseHeldAsync(context, notifier, DateTimeOffset.UtcNow, Ct));
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await read.Applications.SingleAsync(Ct);
        Assert.Equal(fail ? ApplicationStatus.DryRun : ApplicationStatus.Cancelled, saved.Status);
        Assert.Equal(application.Revision + (fail ? 0 : 1), saved.Revision);
        if (fail) await NoAdmissionAsync(read);
        else await NotificationAdmissionAssertions.JoinedAsync(read, await read.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_assessment_import_failure_and_native_alert_are_atomic(bool fail)
    {
        var vacancy = await VacancyAsync(attempts: 3);
        var host = await HostAsync();
        _fault.Active = fail;
        var captures = host.Services.GetRequiredService<VacancyAssessmentCapture>();
        var expected = await CaptureImportFailureAsync(vacancy.Id);
        var publish = captures.FailPublishAsync(expected, new InvalidOperationException("synthetic capture failure"), Ct);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => publish);
        else await publish;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Vacancies.SingleAsync(Ct);
        Assert.Equal(fail ? VacancyStatus.New : VacancyStatus.Failed, saved.Status);
        Assert.Equal(fail ? 3 : 4, saved.Attempts);
        if (fail) await NoAdmissionAsync(context);
        else await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(0, _provider.Calls);
    }

    private async Task<VacancyAssessmentCapture.PendingVacancy> CaptureImportFailureAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var persisted = await context.Vacancies.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
        return new(persisted.Id, persisted.AssessmentRevision, persisted.AssessmentCaptureId, persisted.Attempts, persisted.UpdatedAt);
    }

    [Fact]
    public async Task Old_assessment_import_failure_does_not_retire_a_newer_vacancy()
    {
        var vacancy = await VacancyAsync(attempts: 3);
        var expected = await CaptureImportFailureAsync(vacancy.Id);
        await using (var edit = await pg.Db.CreateDbContextAsync(Ct))
        {
            var changed = await edit.Vacancies.SingleAsync(Ct);
            changed.AssessmentRevision++;
            await edit.SaveChangesAsync(Ct);
        }
        var host = await HostAsync();
        Assert.False(await host.Services.GetRequiredService<VacancyAssessmentCapture>()
            .FailPublishAsync(expected, new InvalidOperationException("old synthetic failure"), Ct));
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await read.Vacancies.SingleAsync(Ct);
        Assert.Equal(VacancyStatus.New, saved.Status);
        Assert.Equal(3, saved.Attempts);
        await NoAdmissionAsync(read);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_channel_proposals_keep_existing_decisions_and_join_native_admission(bool fail)
    {
        await File.WriteAllTextAsync(_seedFile, """
            [{"username":"synthetic_existing","title":"Existing"},{"username":"synthetic_new","title":"New","kind":"channel"}]
            """, new UTF8Encoding(false), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.TelegramChannels.Add(new TelegramChannel
        {
            Username = "synthetic_existing", Title = "Existing", Status = ChannelStatus.Rejected,
            Kind = ChannelKind.Channel, AddedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        var host = await HostAsync();
        var service = ActivatorUtilities.CreateInstance<TelegramService>(host.Services);
        _fault.Active = fail;
        Task SeedAsync() => (Task)typeof(TelegramService).GetMethod("SeedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [Ct])!;
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(SeedAsync);
        else { await SeedAsync(); await SeedAsync(); }
        Assert.Equal(ChannelStatus.Rejected, (await context.TelegramChannels.SingleAsync(row => row.Username == "synthetic_existing", Ct)).Status);
        Assert.Equal(fail ? 1 : 2, await context.TelegramChannels.CountAsync(Ct));
        if (fail) await NoAdmissionAsync(context);
        else
        {
            Assert.Equal(ChannelStatus.Proposed, (await context.TelegramChannels.SingleAsync(row => row.Username == "synthetic_new", Ct)).Status);
            var notice = await context.Notifications.SingleAsync(Ct);
            Assert.Equal(NotificationKind.ChannelsProposed, notice.Kind);
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        }
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_outreach_preparation_joins_allocated_source_and_card_or_rolls_both_back(bool fail)
    {
        var vacancy = await VacancyAsync();
        var host = await HostAsync(consumer: true);
        var profiles = host.Services.GetRequiredService<ProfileProvider>();
        await profiles.SaveAsync(new CandidateProfile { Name = "Synthetic Candidate", Summary = OutreachProvider.Proof, Reviewed = true },
            (await profiles.GetSnapshotAsync(Ct))?.Version ?? 0, "synthetic-producer", Ct);
        await host.Services.GetRequiredService<FactsProvider>().GetAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.CompanyContacts.Add(new CompanyContact
        {
            CompanyKey = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName), CompanyName = vacancy.CompanyName,
            Kind = ContactKind.LinkedIn, Value = "https://linkedin.invalid/in/synthetic", Source = "manual", Role = "Recruiter",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        _fault.Active = fail;
        var outreach = host.Services.GetRequiredService<OutreachService>();
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => outreach.PrepareAsync(vacancy.Id, Ct));
        else Assert.NotNull(await outreach.PrepareAsync(vacancy.Id, Ct));
        var paidCalls = _provider.Calls;
        Assert.InRange(paidCalls, 2, 4);
        Assert.All(_provider.Requests, request =>
        {
            using var json = JsonDocument.Parse(request);
            Assert.Equal("synthetic/application", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("max", json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        });
        if (fail)
        {
            Assert.True(_fault.SourceWasSaved);
            Assert.Empty(await context.Outreaches.ToListAsync(Ct));
            await NoAdmissionAsync(context);
            _fault.Active = false;
            Assert.NotNull(await outreach.PrepareAsync(vacancy.Id, Ct));
            Assert.Equal(paidCalls, _provider.Calls);
        }
        else
        {
            var source = await context.Outreaches.SingleAsync(Ct);
            var notice = await context.Notifications.SingleAsync(Ct);
            Assert.Contains(notice.Buttons.SelectMany(row => row), button => button.Contains("|" + CareerCallback.OutreachSkip + ":" + source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal));
            Assert.Equal(vacancy.Id, notice.EntityId);
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
            Assert.Null(await outreach.PrepareAsync(vacancy.Id, Ct));
            Assert.Equal(paidCalls, _provider.Calls);
        }
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    private static async Task NoAdmissionAsync(JobFinderDbContext context)
    {
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_notifications) +
              (SELECT count(*) FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.notifications.%'))::int AS "Value"
            """).SingleAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_reply_redraft_revision_and_native_card_commit_or_roll_back_together(bool fail)
    {
        var application = await ApplicationAsync(ApplicationStatus.Sent);
        var host = await HostAsync(consumer: true);
        await host.Services.GetRequiredService<FactsProvider>().GetAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = new ApplicationEvent
        {
            ApplicationId = application.Id, Kind = EventKind.MessageIn, Channel = EventChannel.Email,
            Author = "synthetic@example.invalid", Text = "We will get back to you", At = DateTimeOffset.UtcNow,
            Draft = "Original draft", DraftStatus = DraftStatus.Pending, Revision = 2,
        };
        context.ApplicationEvents.Add(source);
        await context.SaveChangesAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        _provider.Response = JsonSerializer.Serialize(new ReplyAnalysis
        {
            Classification = "other", Summary = "Synthetic acknowledgement", Intent = "small_talk", Counterpart = "human",
            Draft = "Спасибо, буду ждать ответа.",
        }, CareerJsonContext.Default.ReplyAnalysis);
        _fault.Active = fail;
        var replies = host.Services.GetRequiredService<ReplyService>();
        var pending = replies.RedraftAsync(source.Id, Ct);
        if (fail)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            while (!_fault.SourceWasSaved) await Task.Delay(100, timeout.Token);
            var rolledBack = await context.ApplicationEvents.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct);
            Assert.Equal(source.Draft, rolledBack.Draft);
            Assert.Equal(source.Revision, rolledBack.Revision);
            await NoAdmissionAsync(context);
            Assert.Equal(ReplyProcessingStage.Publishing, (await context.ReplyProcessingRequests.SingleAsync(Ct)).Stage);
            _fault.Active = false;
        }
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(45), Ct)).Ok);
        var saved = await context.ApplicationEvents.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal("Спасибо, буду ждать ответа.", saved.Draft);
        Assert.Equal(source.Revision + 1, saved.Revision);
        Assert.Equal(DraftStatus.Pending, saved.DraftStatus);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(1, _provider.Calls);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public bool SourceWasSaved { get; private set; }
        public Func<Task>? BeforeEmployerAdmission { get; set; }
        public Func<Task>? BeforeContactRetirement { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is JobFinderDbContext pending)
            {
                if (BeforeEmployerAdmission is { } admission && pending.ChangeTracker.Entries<EmployerMessage>().Any(entry => entry.State == EntityState.Added))
                {
                    BeforeEmployerAdmission = null;
                    await admission();
                }
                if (BeforeContactRetirement is { } retirement && pending.ChangeTracker.Entries<CompanyContact>()
                    .Any(entry => entry.State == EntityState.Modified && entry.Entity.RetiredAt is not null))
                {
                    BeforeContactRetirement = null;
                    await retirement();
                }
            }
            if (Active && eventData.Context is JobFinderDbContext context && context.ChangeTracker.Entries<NotificationDeliveryRequest>()
                .Any(entry => entry.State == EntityState.Added))
            {
                SourceWasSaved = context.ChangeTracker.Entries<Application>().Any(entry => entry.State == EntityState.Unchanged)
                    || context.ChangeTracker.Entries<Outreach>().Any(entry => entry.State == EntityState.Unchanged)
                    || context.ChangeTracker.Entries<TelegramChannel>().Any(entry => entry.State == EntityState.Unchanged)
                    || context.ChangeTracker.Entries<Vacancy>().Any(entry => entry.State == EntityState.Unchanged);
                throw new InvalidOperationException("synthetic ordinary notification admission failure");
            }
            return result;
        }
    }

    private sealed class OutreachProvider : HttpMessageHandler, IHttpClientFactory
    {
        public const string Proof = "Разработал сервис обработки платежей с защитой от повторных списаний.";
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Response { get; set; } = "Synthetic outreach note";
        private Func<Task>? _beforeResponse;
        public Func<Task>? BeforeResponse { get => Volatile.Read(ref _beforeResponse); set => Volatile.Write(ref _beforeResponse, value); }
        public string LastRequest { get; private set; } = "";
        public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.Host != "provider.invalid") throw new InvalidOperationException("ordinary producer fixture refuses external hosts");
            Interlocked.Increment(ref _calls);
            var body = await request.Content!.ReadAsStringAsync(ct);
            LastRequest = body;
            Requests.Enqueue(body);
            var before = Interlocked.Exchange(ref _beforeResponse, null);
            if (before is not null) await before();
            using var json = JsonDocument.Parse(body);
            var schema = json.RootElement.TryGetProperty("response_format", out var format)
                && format.TryGetProperty("json_schema", out var schemaObject) ? schemaObject.GetProperty("name").GetString() : null;
            var response = schema switch
            {
                "recruiter_message" => JsonSerializer.Serialize(new
                {
                    hook = "Надёжность платежей", needs = new[] { "Надёжность платежей" },
                    facts = new[] { new { fact = Proof, need = "Надёжность платежей", whyInteresting = "Защита от повторов", sourceQuote = Proof } },
                    greeting = "Здравствуйте!", paragraphs = new[] { Proof }, closing = "Обсудим задачи?",
                }),
                "recruiter_check" => JsonSerializer.Serialize(new
                {
                    claims = new[] { new { claim = Proof, kind = "experience", supported = true, quote = Proof } },
                    projectCases = 1, natural = true, hasReason = true, note = "",
                    verdict = new { checks = RecruiterVerdict.Criteria.Keys.Select(id => new { id, passed = true, note = "" }),
                        score = 10, wouldReply = true, weakest = "", fix = "" },
                }),
                _ => Response,
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = "synthetic/outreach", choices = new[] { new { message = new { content = response }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 100, completion_tokens = 20, cost = 0.0001 },
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
