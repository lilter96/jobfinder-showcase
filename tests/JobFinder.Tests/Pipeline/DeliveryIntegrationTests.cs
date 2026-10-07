using System.Security.Cryptography;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Llm;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DeliveryIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly HashSet<long> _vacancyIds = [];
    private IHost? _producer;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.CapturesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var config = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Llm:ApiKey"] = "synthetic-test-key", ["Llm:Endpoint"] = "https://example.invalid/llm",
        };
        foreach (var purpose in LlmPurpose.All)
        {
            config[$"Llm:Routes:{purpose}:0:Name"] = "synthetic/" + purpose.ToLowerInvariant();
            config[$"Llm:Routes:{purpose}:0:Reasoning:Effort"] = "max";
        }
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddJobFinderCore(builder.Configuration);
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        builder.Services.AddSingleton(pg.Db);
        builder.AddJobFinderMessaging(consumer: false);
        _producer = builder.Build();
        await _producer.StartAsync(Ct);
        await _producer.Services.GetRequiredService<RuntimeSettings>().Automation.ReadAsync(Ct);
        var publisher = _producer.Services.GetRequiredService<ICandidatePublisher>();
        if (!await context.Documents.AnyAsync(row => row.Key == ProfileProvider.Key, Ct))
            await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true }, 0, "synthetic-test", Ct);
        if (!await context.Documents.AnyAsync(row => row.Key == FactsProvider.Key, Ct))
            await publisher.PublishFactsAsync(new CandidateFacts(), 0, "synthetic-test", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_producer is not null)
        {
            await _producer.StopAsync(CancellationToken.None);
            _producer.Dispose();
        }
        if (pg.SkipReason is not null || _vacancyIds.Count == 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await pg.Db.CreateDbContextAsync(timeout.Token);
        await context.DeliveryAttempts.Where(attempt => _vacancyIds.Contains(attempt.Application!.VacancyId)).ExecuteDeleteAsync(timeout.Token);
        await context.ApplicationDeliveryRequests.Where(request => context.Applications.Any(application => application.Id == request.ApplicationId
            && _vacancyIds.Contains(application.VacancyId))).ExecuteDeleteAsync(timeout.Token);
        await context.Vacancies.Where(vacancy => _vacancyIds.Contains(vacancy.Id)).ExecuteDeleteAsync(timeout.Token);
        var notices = context.Notifications.Where(notification => notification.EntityId != null && _vacancyIds.Contains(notification.EntityId.Value));
        var roots = context.NotificationDeliveryRequests.Where(request => notices.Any(notice => notice.Id == request.NotificationId));
        await context.NotificationDeliveryAttempts.Where(attempt => roots.Any(request => request.Id == attempt.RequestId)).ExecuteDeleteAsync(timeout.Token);
        await roots.ExecuteDeleteAsync(timeout.Token);
        await context.Notifications.Where(notification => notification.EntityId != null && _vacancyIds.Contains(notification.EntityId.Value))
            .ExecuteDeleteAsync(timeout.Token);
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DeliveryLedger Ledger() => new(pg.Db, TimeProvider.System, _producer!.Services.GetRequiredService<JobFinder.Pipeline.Notifications.Notifier>());

    private ApprovalService Approvals() => new(pg.Db, null!, null!, null!,
        new FunnelService(null!, TimeProvider.System, NullLogger<FunnelService>.Instance),
        new RuntimeSettings(pg.Db, new DecisionOptions(), TimeProvider.System, pg.Policies), new JobTriggers(), TimeProvider.System, NullLogger<ApprovalService>.Instance,
        null!, _producer!.Services.GetRequiredService<ApplicationDeliveryRequests>(), _producer.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.CommentDecisions>(), _producer.Services.GetRequiredService<JobFinder.Pipeline.Notifications.Notifier>(), null!);

    private async Task<ApprovedDispatch> SeedAsync(ApplicationStatus status = ApplicationStatus.Pending)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var application = new Application
        {
            Vacancy = new Vacancy
            {
                Title = "Senior .NET", Url = "https://example.org/jobs/1", Fingerprint = Guid.NewGuid().ToString("N"),
                CoverLetter = "проверенное письмо", Status = VacancyStatus.Scored, FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            },
            Status = status, Channel = ApplicationChannel.HeadHunter, CoverLetter = "проверенное письмо", CreatedAt = DateTimeOffset.UtcNow,
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        _vacancyIds.Add(application.VacancyId);
        return new ApprovedDispatch(application, application.CoverLetter, new ApplicationLetterReview
        {
            Score = 10, TextHash = Fingerprint.Of(application.CoverLetter), ContextHash = "tested-context", CheckedAt = DateTimeOffset.UtcNow,
        }, null, null, false);
    }

    private async Task<Application> ReadAsync(long applicationId)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Applications.AsNoTracking().SingleAsync(application => application.Id == applicationId, Ct);
    }

    private async Task<List<DeliveryAttempt>> AttemptsAsync(long applicationId)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.DeliveryAttempts.AsNoTracking().Where(attempt => attempt.ApplicationId == applicationId).ToListAsync(Ct);
    }

    private sealed class Recipient(Func<ApprovedDispatch, Task<DeliveryReceipt>> receive) : IDeliveryTransport
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<DeliveryReceipt> SendAsync(ApprovedDispatch dispatch, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return receive(dispatch);
        }
    }

    private DeliveryGateway Gateway(Recipient recipient)
    {
        var ownership = new ServerOwnership();
        ownership.Acquired();
        return new DeliveryGateway(Ledger(), recipient, NullLogger<DeliveryGateway>.Instance, ownership);
    }

    [Fact]
    public async Task Editing_during_review_invalidates_the_claim_without_sending()
    {
        var dispatch = await SeedAsync();
        Assert.True((await Approvals().SetLetterAsync(dispatch.Application.VacancyId, "новое письмо", await Approvals().LetterSnapshotAsync(dispatch.Application.VacancyId, Ct), Ct)).Ok);
        var recipient = new Recipient(_ => Task.FromResult(new DeliveryReceipt(ApplicationStatus.Sent)));
        Assert.Null(await Gateway(recipient).DispatchAsync(dispatch, null, Ct));
        var application = await ReadAsync(dispatch.Application.Id);
        Assert.Equal("новое письмо", application.CoverLetter);
        Assert.Equal(ApplicationStatus.Pending, application.Status);
        Assert.True(application.Revision > dispatch.Application.Revision);
        Assert.Equal(0, recipient.Calls);
        Assert.Empty(await AttemptsAsync(application.Id));
    }

    [Fact]
    public async Task Cancellation_during_review_wins_without_an_external_effect()
    {
        var dispatch = await SeedAsync();
        Assert.True((await Approvals().DismissAsync(dispatch.Application.VacancyId, Ct)).Ok);
        var recipient = new Recipient(_ => Task.FromResult(new DeliveryReceipt(ApplicationStatus.Sent)));
        Assert.Null(await Gateway(recipient).DispatchAsync(dispatch, null, Ct));
        Assert.Equal(ApplicationStatus.Cancelled, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Equal(0, recipient.Calls);
    }

    [Fact]
    public async Task A_claimed_delivery_cannot_be_cancelled_edited_or_marked_manual()
    {
        var dispatch = await SeedAsync();
        Assert.NotNull(await Ledger().TryClaimAsync(dispatch, Ct));
        Assert.False((await Approvals().DismissAsync(dispatch.Application.VacancyId, Ct)).Ok);
        Assert.False((await Approvals().SetLetterAsync(dispatch.Application.VacancyId, "replacement", await Approvals().LetterSnapshotAsync(dispatch.Application.VacancyId, Ct), Ct)).Ok);
        Assert.False((await Approvals().MarkManualAsync(dispatch.Application.VacancyId, Ct)).Ok);
        Assert.Equal(ApplicationStatus.Sending, (await ReadAsync(dispatch.Application.Id)).Status);
    }

    [Fact]
    public async Task Concurrent_consumers_of_one_operation_send_exactly_one_fake_message()
    {
        var dispatch = await SeedAsync();
        var recipient = new Recipient(_ => Task.FromResult(new DeliveryReceipt(ApplicationStatus.Sent)));
        var gateway = Gateway(recipient);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => gateway.DispatchAsync(dispatch, null, Ct)));
        Assert.Single(results, receipt => receipt is not null);
        Assert.Equal(1, recipient.Calls);
        Assert.Equal(ApplicationStatus.Sent, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, Assert.Single(await AttemptsAsync(dispatch.Application.Id)).Status);
    }

    [Fact]
    public async Task Duplicate_application_rows_cannot_both_deliver_while_the_first_is_in_flight()
    {
        var first = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var second = new Application
        {
            VacancyId = first.Application.VacancyId, Vacancy = first.Application.Vacancy, Channel = first.Application.Channel,
            Status = ApplicationStatus.Pending, CoverLetter = first.Text, CreatedAt = DateTimeOffset.UtcNow,
        };
        context.Entry(second.Vacancy!).State = EntityState.Unchanged;
        context.Applications.Add(second);
        await context.SaveChangesAsync(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recipient = new Recipient(async _ =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Ct);
            return new DeliveryReceipt(ApplicationStatus.Sent);
        });
        var gateway = Gateway(recipient);
        var sending = gateway.DispatchAsync(first, null, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            Assert.Null(await gateway.DispatchAsync(first with { Application = second }, null, Ct));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(ApplicationStatus.Sent, (await sending)!.Status);
        Assert.Equal(1, recipient.Calls);
    }

    [Fact]
    public async Task Acceptance_followed_by_timeout_is_unknown_and_never_retried_automatically()
    {
        var dispatch = await SeedAsync();
        var recipient = new Recipient(_ => throw new TimeoutException("accepted, response lost"));
        Assert.Equal(ApplicationStatus.OutcomeUnknown, (await Gateway(recipient).DispatchAsync(dispatch, null, Ct))!.Status);
        Assert.Null(await Gateway(recipient).DispatchAsync(dispatch, null, Ct));
        Assert.Equal(1, recipient.Calls);
        Assert.Equal(ApplicationStatus.OutcomeUnknown, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, Assert.Single(await AttemptsAsync(dispatch.Application.Id)).Status);
        Assert.False((await Approvals().ApplyAsync(dispatch.Application.VacancyId, Ct)).Ok);
        Assert.False((await Approvals().DismissAsync(dispatch.Application.VacancyId, Ct)).Ok);
    }

    [Fact]
    public async Task Commit_failure_after_external_acceptance_recovers_to_unknown_not_pending()
    {
        var dispatch = await SeedAsync();
        var recipient = new Recipient(_ => Task.FromResult(new DeliveryReceipt(ApplicationStatus.Sent)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway(recipient).DispatchAsync(dispatch,
            (_, _, _) => throw new InvalidOperationException("transaction failed after external acceptance"), Ct));
        Assert.Equal(ApplicationStatus.Sending, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Equal(DeliveryAttemptStatus.Submitting, Assert.Single(await AttemptsAsync(dispatch.Application.Id)).Status);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Equal(1, await DeliveryLedger.RecoverAsync(context, _producer!.Services.GetRequiredService<JobFinder.Pipeline.Notifications.Notifier>(), DateTimeOffset.UtcNow, Ct));
        Assert.Equal(ApplicationStatus.OutcomeUnknown, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Null(await Gateway(recipient).DispatchAsync(dispatch, null, Ct));
        Assert.Equal(1, recipient.Calls);
        await using var verify = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await verify.Notifications.Where(notification => notification.EntityId == dispatch.Application.VacancyId).ToListAsync(Ct));
    }

    [Fact]
    public async Task Legacy_sending_without_a_receipt_is_also_recovered_conservatively()
    {
        var dispatch = await SeedAsync(ApplicationStatus.Sending);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, await DeliveryLedger.RecoverAsync(context, _producer!.Services.GetRequiredService<JobFinder.Pipeline.Notifications.Notifier>(), DateTimeOffset.UtcNow, Ct));
        Assert.Equal(0, await DeliveryLedger.RecoverAsync(context, _producer.Services.GetRequiredService<JobFinder.Pipeline.Notifications.Notifier>(), DateTimeOffset.UtcNow, Ct));
        Assert.Equal(ApplicationStatus.OutcomeUnknown, (await ReadAsync(dispatch.Application.Id)).Status);
    }

    [Fact]
    public async Task Manual_reconciliation_reuses_the_unknown_application_without_claiming_full_payload_delivery()
    {
        var dispatch = await SeedAsync();
        var recipient = new Recipient(_ => throw new TimeoutException("unknown"));
        await Gateway(recipient).DispatchAsync(dispatch, null, Ct);
        Assert.True((await Approvals().MarkManualAsync(dispatch.Application.VacancyId, Ct)).Ok);
        Assert.Equal(ApplicationStatus.Sent, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, Assert.Single(await AttemptsAsync(dispatch.Application.Id)).Status);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Applications.Where(application => application.VacancyId == dispatch.Application.VacancyId).ToListAsync(Ct));
    }

    [Fact]
    public async Task Preparation_failure_cannot_overwrite_a_newer_draft()
    {
        var dispatch = await SeedAsync();
        await Approvals().SetLetterAsync(dispatch.Application.VacancyId, "replacement", await Approvals().LetterSnapshotAsync(dispatch.Application.VacancyId, Ct), Ct);
        Assert.False(await Ledger().RejectPreparationAsync(dispatch.Application, "old review failed", null, Ct));
        Assert.Equal(ApplicationStatus.Pending, (await ReadAsync(dispatch.Application.Id)).Status);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(10, true)]
    public async Task A_bad_review_or_changed_text_is_rejected_before_a_claim(int score, bool changedText)
    {
        var dispatch = await SeedAsync();
        dispatch.Review.Score = score;
        if (changedText) dispatch = dispatch with { Text = "not the reviewed letter" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ledger().TryClaimAsync(dispatch, Ct));
        Assert.Equal(ApplicationStatus.Pending, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Empty(await AttemptsAsync(dispatch.Application.Id));
    }

    [Fact]
    public async Task Delivery_receipt_pins_actual_resume_id_and_immutable_bytes()
    {
        var dispatch = await SeedAsync();
        var content = new byte[] { 1, 2, 3, 4 };
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, content, Ct);
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            dispatch = dispatch with { Resume = new EmailAttachment(path, "CV.pdf", 41, hash, content) };
            var recipient = new Recipient(async received =>
            {
                await File.WriteAllBytesAsync(path, new byte[] { 9, 9, 9 }, Ct);
                Assert.Equal(content, received.Resume!.Content);
                return new DeliveryReceipt(ApplicationStatus.Sent, EmailMessageId: "receipt-1");
            });
            await Gateway(recipient).DispatchAsync(dispatch, null, Ct);
            var application = await ReadAsync(dispatch.Application.Id);
            Assert.Equal(41, application.TailoredResumeId);
            var attempt = Assert.Single(await AttemptsAsync(application.Id));
            Assert.Equal(41, attempt.TailoredResumeId);
            Assert.Equal(hash, attempt.ResumeHash);
            Assert.Equal("receipt-1", attempt.ExternalId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Tracked_stale_application_updates_fail_optimistic_concurrency()
    {
        var dispatch = await SeedAsync();
        await using var stale = await pg.Db.CreateDbContextAsync(Ct);
        var application = await stale.Applications.SingleAsync(application => application.Id == dispatch.Application.Id, Ct);
        await Approvals().SetLetterAsync(application.VacancyId, "new revision", await Approvals().LetterSnapshotAsync(application.VacancyId, Ct), Ct);
        application.Status = ApplicationStatus.Sending;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Ct));
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("simulated failure before intent commit");
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Intent_save_failure_rolls_back_the_claim_in_the_same_transaction()
    {
        var dispatch = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(new FailSave());
        var ledger = new DeliveryLedger(new Factory(options.Options), TimeProvider.System, _producer!.Services.GetRequiredService<JobFinder.Pipeline.Notifications.Notifier>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.TryClaimAsync(dispatch, Ct));
        Assert.Equal(ApplicationStatus.Pending, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Empty(await AttemptsAsync(dispatch.Application.Id));
    }

    [Fact]
    public async Task A_second_host_is_rejected_before_it_can_recover_the_live_hosts_sending_row()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        IHost BuildHost() => new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(pg.Db);
            services.AddSingleton(new DatabaseOptions { ConnectionString = context.Database.GetConnectionString()! });
            services.AddSingleton<ServerOwnership>();
            services.AddSingleton(TimeProvider.System);
            services.AddHostedService<SingleInstanceGuard>();
            services.AddHostedService<DatabaseInitializer>();
        }).Build();
        using var first = BuildHost();
        using var second = BuildHost();
        await first.StartAsync(Ct);
        try
        {
            var dispatch = await SeedAsync(ApplicationStatus.Sending);
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(Ct));
            Assert.Equal(ApplicationStatus.Sending, (await ReadAsync(dispatch.Application.Id)).Status);
        }
        finally
        {
            await first.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Losing_the_lock_connection_revokes_ownership_and_requests_host_shutdown()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var applicationName = "ownership-test-" + Guid.NewGuid().ToString("N");
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()!) { ApplicationName = applicationName };
        var clock = new FakeTimeProvider();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(new DatabaseOptions { ConnectionString = connection.ConnectionString });
            services.AddSingleton<ServerOwnership>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddHostedService<SingleInstanceGuard>();
        }).Build();
        await host.StartAsync(Ct);
        try
        {
            var ownership = host.Services.GetRequiredService<ServerOwnership>();
            Assert.True(ownership.IsOwned);
            var guard = Assert.Single(host.Services.GetServices<IHostedService>().OfType<SingleInstanceGuard>());
            Assert.True(await guard.VerifyAsync(Ct));
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = {applicationName}", Ct);
            var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            for (var tick = 0; tick < 40 && !stopping.IsCancellationRequested; tick++)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                await Task.Delay(TimeSpan.FromMilliseconds(25), Ct);
            }
            Assert.True(stopping.IsCancellationRequested);
            Assert.False(ownership.IsOwned);
            Assert.Throws<InvalidOperationException>(ownership.EnsureOwned);
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task A_gateway_without_server_ownership_does_not_claim_or_send()
    {
        var dispatch = await SeedAsync();
        var recipient = new Recipient(_ => Task.FromResult(new DeliveryReceipt(ApplicationStatus.Sent)));
        var gateway = new DeliveryGateway(Ledger(), recipient, NullLogger<DeliveryGateway>.Instance, new ServerOwnership());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.DispatchAsync(dispatch, null, Ct));
        Assert.Equal(ApplicationStatus.Pending, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Equal(0, recipient.Calls);
        Assert.Empty(await AttemptsAsync(dispatch.Application.Id));
    }
}
