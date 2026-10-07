using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Bot;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Storage;
using JobFinder.Pipeline.Settings;
using JobFinder.Integrations.Threads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    [Fact]
    public async Task Startup_recovery_commits_unknown_source_and_native_notice_before_main_consumers_and_delivers_only_the_notice()
    {
        _options.Enabled = false;
        var application = new Application
        {
            Vacancy = new Vacancy { Title = "Synthetic interrupted application", Fingerprint = Guid.NewGuid().ToString("N"), Url = "https://vacancy.invalid/recovery" },
            Status = ApplicationStatus.Sending, Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.Approved,
            Revision = 2, CreatedAt = DateTimeOffset.UtcNow,
        };
        var run = new JobRun { Job = "synthetic.recovery", Status = JobRunStatus.Running, StartedAt = DateTimeOffset.UtcNow };
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Applications.Add(application);
            context.JobRuns.Add(run);
            await context.SaveChangesAsync(Ct);
        }
        var committed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new BootstrapHook(async (services, token) =>
        {
            Assert.Throws<WolverineHasNotStartedException>(() => services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
            services.GetRequiredService<ServerOwnership>().EnsureOwned();
            await using var context = await pg.Db.CreateDbContextAsync(token);
            var recovered = await context.Applications.AsNoTracking().SingleAsync(row => row.Id == application.Id, token);
            Assert.Equal(ApplicationStatus.OutcomeUnknown, recovered.Status);
            Assert.Equal(3, recovered.Revision);
            Assert.Null(recovered.SentAt);
            Assert.Equal(JobRunStatus.Failed, (await context.JobRuns.SingleAsync(row => row.Id == run.Id, token)).Status);
            var notice = Assert.Single(await context.Notifications.Where(row => row.EntityId == application.VacancyId).ToListAsync(token));
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, token);
            Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(token));
            Assert.Empty(_api.Calls);
            committed.SetResult(notice.DeliveryRequestId!.Value);
            await release.Task.WaitAsync(token);
        });
        var host = await BootstrapHostAsync(hook);
        var startup = host.StartAsync(Ct);
        try
        {
            var id = await committed.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
            _options.Enabled = true;
            release.SetResult();
            await startup;
            await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
            Assert.Equal("sendMessage", Assert.Single(_api.Calls).Method);
            await using var read = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(ApplicationStatus.OutcomeUnknown, (await read.Applications.SingleAsync(row => row.Id == application.Id, Ct)).Status);
            Assert.Empty(await read.DeliveryAttempts.ToListAsync(Ct));
        }
        finally
        {
            release.TrySetResult();
            await using var context = await pg.Db.CreateDbContextAsync(CancellationToken.None);
            await context.Applications.Where(row => row.Id == application.Id).ExecuteDeleteAsync(CancellationToken.None);
            await context.Vacancies.Where(row => row.Id == application.VacancyId).ExecuteDeleteAsync(CancellationToken.None);
            await context.JobRuns.Where(row => row.Id == run.Id).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_startup_recovery_admission_preserves_sending_source_and_stops_before_consumers()
    {
        var application = new Application
        {
            Vacancy = new Vacancy { Title = "Synthetic failed recovery", Fingerprint = Guid.NewGuid().ToString("N"), Url = "https://vacancy.invalid/recovery-fault" },
            Status = ApplicationStatus.Sending, Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.Approved,
            Revision = 2, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Applications.Add(application);
            await context.SaveChangesAsync(Ct);
        }
        var entered = false;
        var host = await BootstrapHostAsync(new BootstrapHook((_, _) => { entered = true; return Task.CompletedTask; }),
            new BootstrapSaveFault { Active = true });
        try
        {
            await Assert.ThrowsAsync<BootstrapFailureException>(() => host.StartAsync(Ct));
            Assert.False(entered);
            Assert.Throws<WolverineHasNotStartedException>(() => host.Services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
            await using var read = await pg.Db.CreateDbContextAsync(Ct);
            var unchanged = await read.Applications.AsNoTracking().SingleAsync(row => row.Id == application.Id, Ct);
            Assert.Equal(ApplicationStatus.Sending, unchanged.Status);
            Assert.Equal(2, unchanged.Revision);
            Assert.Empty(await read.Notifications.ToListAsync(Ct));
            Assert.Empty(await read.NotificationDeliveryRequests.ToListAsync(Ct));
            Assert.Empty(_api.Calls);
            await host.StopAsync(Ct);
            Assert.False(host.Services.GetRequiredService<ServerOwnership>().IsOwned);
        }
        finally
        {
            await using var context = await pg.Db.CreateDbContextAsync(CancellationToken.None);
            await context.Applications.Where(row => row.Id == application.Id).ExecuteDeleteAsync(CancellationToken.None);
            await context.Vacancies.Where(row => row.Id == application.VacancyId).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Bootstrap_policy_imports_commit_all_native_admissions_before_main_consumers_and_deliver_once()
    {
        _options.Enabled = false;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.Documents.Where(row => row.Key.StartsWith("policy.")).ExecuteDeleteAsync(Ct);
            await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
        }
        var committed = new TaskCompletionSource<Guid[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new BootstrapHook(async (services, token) =>
        {
            Assert.Throws<WolverineHasNotStartedException>(() => services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
            await using var context = await pg.Db.CreateDbContextAsync(token);
            Assert.Equal(4, await context.Documents.CountAsync(row => row.Key.StartsWith("policy."), token));
            Assert.Equal(4, await context.PolicyRevisions.CountAsync(token));
            var roots = await context.NotificationDeliveryRequests.AsNoTracking().ToListAsync(token);
            Assert.Equal(4, roots.Count);
            Assert.All(roots, row => Assert.Equal(NotificationDeliveryStage.AwaitingTarget, row.Stage));
            Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(token));
            committed.SetResult(roots.Select(row => row.Id).ToArray());
            await release.Task.WaitAsync(token);
        });
        var consumer = await BootstrapHostAsync(hook);
        var startup = consumer.StartAsync(Ct);
        try
        {
            var ids = await committed.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
            Assert.Empty(_api.Calls);
            _options.Enabled = true;
            release.SetResult();
            await startup;
            await UntilAsync(async () =>
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                return await context.NotificationDeliveryRequests.CountAsync(row => ids.Contains(row.Id)
                    && row.Stage == NotificationDeliveryStage.Completed, Ct) == 4;
            });
            Assert.Equal(4, _api.Calls.Count);
            await using var read = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(4, await read.NotificationDeliveryAttempts.CountAsync(row => ids.Contains(row.RequestId)
                && row.Status == DeliveryAttemptStatus.Confirmed, Ct));
        }
        finally
        {
            release.TrySetResult();
            await startup;
        }
    }

    [Fact]
    public async Task Bootstrap_policy_admission_failure_preserves_legacy_overrides_and_prevents_main_consumers()
    {
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.Documents.Where(row => row.Key.StartsWith("policy.")).ExecuteDeleteAsync(Ct);
            await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
            await context.Settings.Where(row => row.Key == RuntimeSettings.AutoApplyThreshold).ExecuteDeleteAsync(Ct);
            context.Settings.Add(new AppSetting { Key = RuntimeSettings.AutoApplyThreshold, Value = "89", UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var fault = new BootstrapSaveFault { Active = true };
        var hook = new BootstrapHook((_, _) => throw new InvalidOperationException("policy failure must stop later startup"));
        var failed = await BootstrapHostAsync(hook, fault);
        await Assert.ThrowsAsync<BootstrapFailureException>(() => failed.StartAsync(Ct));
        Assert.Throws<WolverineHasNotStartedException>(() => failed.Services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
        await failed.StopAsync(Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.Documents.Where(row => row.Key.StartsWith("policy.")).ToListAsync(Ct));
        Assert.Empty(await read.PolicyRevisions.ToListAsync(Ct));
        Assert.Empty(await read.Notifications.ToListAsync(Ct));
        Assert.Empty(await read.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal("89", (await read.Settings.SingleAsync(row => row.Key == RuntimeSettings.AutoApplyThreshold, Ct)).Value);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Bootstrap_file_import_commits_candidate_history_and_native_admission_before_any_main_consumer()
    {
        _options.Enabled = false;
        var committed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new BootstrapHook(async (services, token) =>
        {
            Assert.Throws<WolverineHasNotStartedException>(() => services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
            await using var context = await pg.Db.CreateDbContextAsync(token);
            var notice = await context.Notifications.SingleAsync(row => row.Text.StartsWith("👤 Данные кандидата обновлены:"), token);
            Assert.Equal("file-import", (await context.CandidateRevisions.SingleAsync(token)).Actor);
            committed.SetResult(notice.DeliveryRequestId!.Value);
            await release.Task.WaitAsync(token);
        });
        var consumer = await CandidateImportHostAsync(hook);
        var path = await CandidateImportFileAsync(consumer);
        var startup = consumer.StartAsync(Ct);
        try
        {
            var id = await committed.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
            Assert.Equal(NotificationDeliveryStage.AwaitingTarget, (await RootAsync(id)).Stage);
            Assert.Empty(_api.Calls);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + FileStateImport.ImportedSuffix));
            _options.Enabled = true;
            release.SetResult();
            await startup;
            await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
            Assert.Single(_api.Calls);
        }
        finally
        {
            release.TrySetResult();
            await startup;
        }
    }

    [Fact]
    public async Task Bootstrap_candidate_import_admission_failure_preserves_the_legacy_file_and_rolls_back_the_database()
    {
        var fault = new BootstrapSaveFault { Active = true, CandidateOnly = true };
        var hook = new BootstrapHook((_, _) => throw new InvalidOperationException("import failure must prevent later startup"));
        var failed = await CandidateImportHostAsync(hook, fault);
        var path = await CandidateImportFileAsync(failed);
        await Assert.ThrowsAsync<BootstrapFailureException>(() => failed.StartAsync(Ct));
        Assert.Throws<WolverineHasNotStartedException>(() => failed.Services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
        await failed.StopAsync(Ct);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + FileStateImport.ImportedSuffix));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await context.Documents.AnyAsync(row => row.Key == ProfileProvider.Key, Ct));
        Assert.Empty(await context.CandidateRevisions.ToListAsync(Ct));
        Assert.False(await context.Notifications.AnyAsync(row => row.Text.StartsWith("👤 Данные кандидата обновлены:"), Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Invalid_last_bootstrap_policy_prevents_all_earlier_imports_and_consumption_of_saved_work()
    {
        var producer = await HostAsync(false);
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), Ct);
        await producer.StopAsync(Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.Documents.Where(row => row.Key.StartsWith("policy.")).ExecuteDeleteAsync(Ct);
            await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
            await context.Settings.Where(row => row.Key == RuntimeSettings.AutoApplyThreshold).ExecuteDeleteAsync(Ct);
            context.Settings.Add(new AppSetting { Key = RuntimeSettings.AutoApplyThreshold, Value = "89", UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var holder = new TelegramBotClientHolder(_options, new BotHttp(_api));
        var failed = await PostProcessingTestHost.BuildAsync(pg, consumer: true, configure: services =>
        {
            services.AddSingleton(new ThreadsOptions { MaxDraftsPerDay = 999 });
            services.AddSingleton<INotificationDeliveryTransport>(new NotificationDeliveryTransport(_options, holder));
        });
        _hosts.Add(failed);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.StartAsync(Ct));
        Assert.Contains("existing engagement preferences are invalid", error.Message);
        Assert.Throws<WolverineHasNotStartedException>(() => failed.Services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
        await failed.StopAsync(Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.Documents.Where(row => row.Key.StartsWith("policy.")).ToListAsync(Ct));
        Assert.Empty(await read.PolicyRevisions.ToListAsync(Ct));
        Assert.Equal("89", (await read.Settings.SingleAsync(row => row.Key == RuntimeSettings.AutoApplyThreshold, Ct)).Value);
        Assert.Equal(id, Assert.Single(await read.Notifications.ToListAsync(Ct)).DeliveryRequestId);
        Assert.Equal(NotificationDeliveryStage.Preparing, (await RootAsync(id)).Stage);
        Assert.Empty(await read.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(_api.Calls);
    }

    private async Task<IHost> CandidateImportHostAsync(BootstrapHook hook, IInterceptor? fault = null)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        var holder = new TelegramBotClientHolder(_options, new BotHttp(_api));
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer: true, fault: fault, configure: services =>
        {
            services.AddSingleton<INotificationDeliveryTransport>(new NotificationDeliveryTransport(_options, holder));
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<FileStateImport>());
            services.AddSingleton<IHostedService>(provider => { hook.Services = provider; return hook; });
        });
        _hosts.Add(host);
        return host;
    }

    private static async Task<string> CandidateImportFileAsync(IHost host)
    {
        var directory = host.Services.GetRequiredService<PathsOptions>().Profile;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.json");
        await File.WriteAllTextAsync(path, ProfileProvider.ToJson(new CandidateProfile
        {
            Name = "Synthetic bootstrap candidate", Title = "Backend Developer", Reviewed = true,
        }), Ct);
        return path;
    }

    [Fact]
    public async Task Bootstrap_admission_commits_before_main_consumers_and_returns_publishing_to_the_same_main_runtime()
    {
        _options.Enabled = false;
        var committed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new BootstrapHook(async (services, token) =>
        {
            Assert.Throws<WolverineHasNotStartedException>(() => services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
            services.GetRequiredService<ServerOwnership>().EnsureOwned();
            await using var context = await pg.Db.CreateDbContextAsync(token);
            context.Settings.Add(new AppSetting { Key = "synthetic.notification.bootstrap", Value = "committed", UpdatedAt = DateTimeOffset.UtcNow });
            var notice = await NoticeAsync();
            context.Notifications.Add(notice);
            await services.GetRequiredService<NotificationDeliveryRequests>().SaveAndFlushAsync(context, token);
            committed.SetResult(notice.DeliveryRequestId!.Value);
            await release.Task.WaitAsync(token);
        });
        var consumer = await BootstrapHostAsync(hook);
        var startup = consumer.StartAsync(Ct);
        try
        {
            var id = await committed.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
            Assert.False(startup.IsCompleted);
            var admitted = await RootAsync(id);
            Assert.Equal(NotificationDeliveryStage.AwaitingTarget, admitted.Stage);
            Assert.Empty(_api.Calls);
            await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            {
                Assert.Equal("committed", (await context.Settings.SingleAsync(row => row.Key == "synthetic.notification.bootstrap", Ct)).Value);
                Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
            }
            _options.Enabled = true;
            release.SetResult();
            await startup;
            await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
            Assert.Equal(admitted.AdmissionHash, (await RootAsync(id)).AdmissionHash);
            Assert.Single(_api.Calls);
            await using var scope = consumer.Services.GetRequiredService<NativePublicationScopes>().CreateAsyncScope();
            Assert.Same(consumer.Services.GetRequiredService<IWolverineRuntime>(), scope.ServiceProvider.GetRequiredService<IWolverineRuntime>());
        }
        finally
        {
            release.TrySetResult();
            await startup;
        }
    }

    [Fact]
    public async Task Bootstrap_publisher_refuses_to_start_without_database_ownership()
    {
        var producer = await HostAsync(false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Services.GetRequiredService<NativeBootstrapPublisher>().StartAsync(Ct));
        Assert.Contains("server database ownership is not held", error.Message);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task A_failed_bootstrap_preserves_committed_admission_without_starting_consumers_and_a_restart_delivers_once()
    {
        _options.Enabled = false;
        Guid id = default;
        var hook = new BootstrapHook(async (services, token) =>
        {
            id = await services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(await NoticeAsync(), token);
            throw new BootstrapFailureException();
        });
        var failed = await BootstrapHostAsync(hook);
        await Assert.ThrowsAsync<BootstrapFailureException>(() => failed.StartAsync(Ct));
        Assert.Throws<WolverineHasNotStartedException>(() => failed.Services.GetRequiredService<IWolverineRuntime>().AssertHasStarted());
        var admitted = await RootAsync(id);
        Assert.Equal(NotificationDeliveryStage.AwaitingTarget, admitted.Stage);
        Assert.Empty(_api.Calls);
        await failed.StopAsync(Ct);
        Assert.False(failed.Services.GetRequiredService<ServerOwnership>().IsOwned);
        await using (var scope = failed.Services.GetRequiredService<NativePublicationScopes>().CreateAsyncScope())
            Assert.Same(failed.Services.GetRequiredService<IWolverineRuntime>(), scope.ServiceProvider.GetRequiredService<IWolverineRuntime>());
        _options.Enabled = true;
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal(admitted.AdmissionHash, (await RootAsync(id)).AdmissionHash);
        Assert.Single(_api.Calls);
    }

    [Fact]
    public async Task A_failed_bootstrap_admission_cannot_commit_business_state_source_notice_or_native_command()
    {
        var fault = new BootstrapSaveFault();
        var hook = new BootstrapHook(async (services, token) =>
        {
            fault.Active = true;
            await using var context = await services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(token);
            context.Settings.Add(new AppSetting { Key = "synthetic.notification.bootstrap-fault", Value = "must roll back", UpdatedAt = DateTimeOffset.UtcNow });
            var notice = await NoticeAsync();
            notice.Text = "synthetic bootstrap admission failure";
            context.Notifications.Add(notice);
            await services.GetRequiredService<NotificationDeliveryRequests>().SaveAndFlushAsync(context, token);
        });
        var failed = await BootstrapHostAsync(hook, fault);
        await Assert.ThrowsAsync<BootstrapFailureException>(() => failed.StartAsync(Ct));
        await failed.StopAsync(Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await read.Settings.AnyAsync(row => row.Key == "synthetic.notification.bootstrap-fault", Ct));
        Assert.False(await read.Notifications.AnyAsync(row => row.Text == "synthetic bootstrap admission failure", Ct));
        Assert.Empty(await read.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await read.NotificationDeliveryAttempts.ToListAsync(Ct));
        var pending = await read.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_notifications)
                 + (SELECT count(*) FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.notifications.%'))::int AS "Value"
            """).SingleAsync(Ct);
        Assert.Equal(0, pending);
        Assert.Empty(_api.Calls);
    }

    private async Task<IHost> BootstrapHostAsync(BootstrapHook hook, IInterceptor? fault = null)
    {
        var holder = new TelegramBotClientHolder(_options, new BotHttp(_api));
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer: true, fault: fault, configure: services =>
        {
            services.AddSingleton<INotificationDeliveryTransport>(new NotificationDeliveryTransport(_options, holder));
            services.AddSingleton<IHostedService>(provider => { hook.Services = provider; return hook; });
        });
        _hosts.Add(host);
        return host;
    }

    private sealed class BootstrapFailureException : Exception;

    private sealed class BootstrapSaveFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public bool CandidateOnly { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && (!CandidateOnly || eventData.Context!.ChangeTracker.Entries<CandidateRevision>().Any())
                && eventData.Context!.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
                throw new BootstrapFailureException();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BootstrapHook(Func<IServiceProvider, CancellationToken, Task> run) : IHostedService
    {
        public IServiceProvider Services { get; set; } = null!;
        public Task StartAsync(CancellationToken ct) => run(Services, ct);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
