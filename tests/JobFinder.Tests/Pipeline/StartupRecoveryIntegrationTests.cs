using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

public sealed class StartupRecoveryIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly AdmissionFault _fault = new();
    private IHost _host = null!;
    private string? _inputJson;
    private string? _inputHash;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.DeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.ApplicationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.CommentDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.CommentDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
        await context.Settings.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: _fault);
        await _host.StartAsync(Ct);
        var publisher = _host.Services.GetRequiredService<ICandidatePublisher>();
        if (!await context.Documents.AnyAsync(row => row.Key == "candidate.profile", Ct))
            await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Title = "Backend Developer", Reviewed = true }, 0, "recovery-test", Ct);
        if (!await context.Documents.AnyAsync(row => row.Key == "candidate.facts", Ct))
            await publisher.PublishFactsAsync(new CandidateFacts(), 0, "recovery-test", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None); }
        finally { _host.Dispose(); }
    }

    [Theory]
    [InlineData("application")]
    [InlineData("native-application")]
    [InlineData("message")]
    [InlineData("comment")]
    public async Task Recovery_commits_unknown_source_attempt_and_native_notice_once_without_sending(string kind)
    {
        await SeedAsync(kind);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        Assert.Equal(1, await RecoverAsync(context, kind));
        Assert.Equal(0, await RecoverAsync(context, kind));
        await AssertStateAsync(kind, interrupted: true);
        var notice = Assert.Single(await context.Notifications.AsNoTracking().ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(1, await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
    }

    [Theory]
    [InlineData("application")]
    [InlineData("native-application")]
    [InlineData("message")]
    [InlineData("comment")]
    public async Task Failed_native_admission_rolls_back_unknown_source_attempt_notice_and_pacing(string kind)
    {
        await SeedAsync(kind);
        await using (var setup = await pg.Db.CreateDbContextAsync(Ct))
            await NotificationAdmissionAssertions.ResetAsync(setup, Ct);
        _fault.Active = true;
        await using (var context = await _host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(Ct))
            await Assert.ThrowsAsync<AdmissionFailure>(() => RecoverAsync(context, kind));
        Assert.True(_fault.SourceSaveCompleted);
        await AssertStateAsync(kind, interrupted: false);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.Notifications.ToListAsync(Ct));
        Assert.Empty(await read.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await read.Settings.Where(row => row.Key == CommentDeliveryWorkflow.PacingKey).ToListAsync(Ct));
        Assert.Equal(0, await read.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
    }

    [Fact]
    public async Task Recovery_requires_exclusive_ownership_before_creating_a_context()
    {
        var factory = new RefusingFactory();
        var recovery = new StartupRecovery(factory, null!, TimeProvider.System, new ServerOwnership(),
            _host.Services.GetRequiredService<IHostApplicationLifetime>(), NullLogger<StartupRecovery>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.StartAsync(Ct));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task Host_shutdown_cancels_recovery_before_any_database_access()
    {
        using var lifetimeHost = new HostBuilder().Build();
        var lifetime = lifetimeHost.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();
        var ownership = new ServerOwnership();
        ownership.Acquired();
        var factory = new RefusingFactory();
        var recovery = new StartupRecovery(factory, null!, TimeProvider.System, ownership, lifetime, NullLogger<StartupRecovery>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.StartAsync(Ct));
        Assert.Equal(0, factory.Calls);
    }

    private Task<int> RecoverAsync(JobFinderDbContext context, string kind) => kind == "comment"
        ? CommentDeliveryLedger.RecoverAsync(context, _host.Services.GetRequiredService<Notifier>(), DateTimeOffset.UtcNow, Ct)
        : DeliveryLedger.RecoverAsync(context, _host.Services.GetRequiredService<Notifier>(), DateTimeOffset.UtcNow, Ct);

    private async Task SeedAsync(string kind)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        if (kind == "comment")
        {
            var draft = new CommentDraft
            {
                PostId = "synthetic-recovery", PostUrl = "https://www.threads.com/@synthetic/post/recovery", Author = "synthetic",
                PostText = "Synthetic recruiter post", ChosenText = "Synthetic response", Variants = ["Synthetic response"],
                Status = CommentStatus.Approved, Revision = 1, CreatedAt = now, DecidedAt = now,
            };
            context.CommentDrafts.Add(draft);
            await context.SaveChangesAsync(Ct);
            var id = Assert.IsType<Guid>(await _host.Services.GetRequiredService<CommentDeliveryRequests>().RequestAsync(draft.Id, Ct));
            await context.Entry(draft).ReloadAsync(Ct);
            draft.Status = CommentStatus.Sending;
            var request = await context.CommentDeliveryRequests.SingleAsync(row => row.Id == id, Ct);
            _inputJson = request.InputJson;
            _inputHash = request.InputHash;
            request.Stage = CommentDeliveryStage.Sending;
            var attempt = new CommentDeliveryAttempt
            {
                Id = Guid.NewGuid(), RequestId = id, DraftId = draft.Id, Revision = draft.Revision, Target = draft.PostUrl,
                Text = "Synthetic response", TextHash = DeliveryLedger.Hash("Synthetic response"), InputHash = request.InputHash,
                Status = DeliveryAttemptStatus.Submitting, CreatedAt = now,
            };
            request.AttemptId = attempt.Id;
            context.CommentDeliveryAttempts.Add(attempt);
        }
        else
        {
            var vacancy = new Vacancy
            {
                Title = "Synthetic Backend Developer", CompanyName = "Synthetic Company", Fingerprint = Guid.NewGuid().ToString("N"),
                Url = "https://vacancy.invalid/recovery", Status = VacancyStatus.Scored, Decision = Decision.Approval,
            };
            context.Vacancies.Add(vacancy);
            await context.SaveChangesAsync(Ct);
            var attempt = new DeliveryAttempt
            {
                Id = Guid.NewGuid(), Text = "Synthetic response", TextHash = DeliveryLedger.Hash("Synthetic response"),
                OperationId = Guid.NewGuid(), Channel = ApplicationChannel.HeadHunter, Status = DeliveryAttemptStatus.Submitting, CreatedAt = now,
            };
            if (kind == "message")
            {
                var input = new EmployerMessageInput
                {
                    VacancyId = vacancy.Id, Kind = EmployerMessageKind.Recruiter, Channel = ApplicationChannel.TelegramDm,
                    Text = attempt.Text, Target = "@synthetic_recovery", SourceRevision = 0,
                };
                var message = new EmployerMessage
                {
                    Id = Guid.NewGuid(), VacancyId = vacancy.Id, BusinessKey = "synthetic-recovery",
                    InputJson = JsonSerializer.Serialize(input, EmployerMessageJsonContext.Default.EmployerMessageInput),
                    Status = EmployerMessageStatus.Sending, CreatedAt = now,
                };
                context.EmployerMessages.Add(message);
                attempt.MessageId = message.Id;
            }
            else
            {
                var application = new Application
                {
                    VacancyId = vacancy.Id, Mode = ApplicationMode.Approved, Channel = ApplicationChannel.HeadHunter,
                    Status = ApplicationStatus.Pending, CoverLetter = attempt.Text, Revision = 1, CreatedAt = now,
                };
                context.Applications.Add(application);
                await context.SaveChangesAsync(Ct);
                if (kind == "native-application")
                {
                    var id = Assert.IsType<Guid>(await _host.Services.GetRequiredService<ApplicationDeliveryRequests>().RequestAsync(application.Id, Ct));
                    await context.Entry(application).ReloadAsync(Ct);
                    var request = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == id, Ct);
                    _inputJson = request.InputJson;
                    _inputHash = request.InputHash;
                    request.Stage = ApplicationDeliveryStage.Sending;
                    attempt.ApplicationDeliveryRequestId = id;
                }
                application.Status = ApplicationStatus.Sending;
                application.Revision++;
                attempt.ApplicationId = application.Id;
                attempt.OperationId = application.OperationId;
                attempt.ApplicationRevision = application.Revision;
            }
            context.DeliveryAttempts.Add(attempt);
        }
        await context.SaveChangesAsync(Ct);
    }

    private async Task AssertStateAsync(string kind, bool interrupted)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var status = interrupted ? DeliveryAttemptStatus.OutcomeUnknown : DeliveryAttemptStatus.Submitting;
        if (kind == "comment")
        {
            Assert.Equal(interrupted ? CommentStatus.OutcomeUnknown : CommentStatus.Sending, (await context.CommentDrafts.SingleAsync(Ct)).Status);
            var request = await context.CommentDeliveryRequests.SingleAsync(Ct);
            Assert.Equal(interrupted ? CommentDeliveryStage.OutcomeUnknown : CommentDeliveryStage.Sending, request.Stage);
            Assert.Equal(_inputJson, request.InputJson);
            Assert.Equal(_inputHash, request.InputHash);
            Assert.Equal(request.Id, (await context.CommentDrafts.SingleAsync(Ct)).DeliveryRequestId);
            Assert.Equal(status, (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
            if (interrupted) Assert.True(await context.Settings.AnyAsync(row => row.Key == CommentDeliveryWorkflow.PacingKey, Ct));
        }
        else
        {
            if (kind == "message") Assert.Equal(interrupted ? EmployerMessageStatus.OutcomeUnknown : EmployerMessageStatus.Sending, (await context.EmployerMessages.SingleAsync(Ct)).Status);
            else Assert.Equal(interrupted ? ApplicationStatus.OutcomeUnknown : ApplicationStatus.Sending, (await context.Applications.SingleAsync(Ct)).Status);
            if (kind == "native-application")
            {
                var request = await context.ApplicationDeliveryRequests.SingleAsync(Ct);
                Assert.Equal(interrupted ? ApplicationDeliveryStage.OutcomeUnknown : ApplicationDeliveryStage.Sending, request.Stage);
                Assert.Equal(_inputJson, request.InputJson);
                Assert.Equal(_inputHash, request.InputHash);
                Assert.Equal(request.Id, (await context.Applications.SingleAsync(Ct)).DeliveryRequestId);
            }
            Assert.Equal(status, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
        }
    }

    private sealed class AdmissionFailure : Exception;

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public bool SourceSaveCompleted { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context!.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                SourceSaveCompleted = eventData.Context.ChangeTracker.Entries<Notification>().Any(entry => entry.State == EntityState.Modified
                    && entry.Entity.Id > 0 && entry.OriginalValues.GetValue<Guid?>(nameof(Notification.DeliveryRequestId)) is null
                    && entry.Entity.DeliveryRequestId is not null);
                throw new AdmissionFailure();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RefusingFactory : IDbContextFactory<JobFinderDbContext>
    {
        public int Calls { get; private set; }
        public JobFinderDbContext CreateDbContext()
        {
            Calls++;
            throw new InvalidOperationException("recovery must not access the database without ownership and an active host");
        }
    }
}
