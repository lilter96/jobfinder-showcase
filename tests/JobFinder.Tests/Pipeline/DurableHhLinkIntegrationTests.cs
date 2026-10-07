using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Http;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using RichardSzalay.MockHttp;

namespace JobFinder.Tests.Pipeline;

public sealed class DurableHhLinkIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        using var bootstrap = await HostAsync(false, null);
        await bootstrap.StartAsync(Ct);
        try
        {
            await bootstrap.Services.GetRequiredService<RuntimeSettings>().Automation.ReadAsync(Ct);
            await bootstrap.Services.GetRequiredService<LlmPolicyStore>().ReadAsync(Ct);
            await bootstrap.Services.GetRequiredService<SearchPolicyStore>().ReadAsync(Ct);
            await bootstrap.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.EngagementPolicyStore>().ReadAsync(Ct);
        }
        finally { await bootstrap.StopAsync(CancellationToken.None); }
        await context.HhLinkRequests.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_discovery; DELETE FROM jobfinder_queues.wolverine_queue_discovery_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Settings.Where(row => row.Key == HhLinkQueue.Key).ExecuteDeleteAsync(Ct);
        await context.CompanyContacts.ExecuteDeleteAsync(Ct);
        await context.VacancySources.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Companies.ExecuteDeleteAsync(Ct);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IHost> HostAsync(bool consumer, IHhLinkGateway? gateway, IDbContextFactory<JobFinderDbContext>? factory = null,
        Action<IServiceCollection>? configure = null)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddJobFinderCore(builder.Configuration);
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        builder.Services.AddSingleton(factory ?? pg.Db);
        if (gateway is not null)
        {
            builder.Services.RemoveAll<IHhLinkGateway>();
            builder.Services.AddSingleton(gateway);
        }
        configure?.Invoke(builder.Services);
        builder.AddJobFinderMessaging(consumer);
        if (consumer) builder.Services.AddJobFinderInitialization();
        return builder.Build();
    }

    private async Task<HhLinkRequest> RequestAsync(long hhId)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.HhLinkRequests.AsNoTracking().SingleAsync(row => row.HhId == hhId, Ct);
    }

    private async Task<int> NotificationsAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Notifications.CountAsync(Ct);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "durable workflow did not reach its expected state");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task A_producer_exits_then_real_handlers_complete_both_stages_and_one_atomic_notification()
    {
        var gateway = new Gateway();
        using (var producer = await HostAsync(false, gateway))
        {
            await producer.StartAsync(Ct);
            Assert.Empty(producer.Services.GetServices<IHostedService>().OfType<SingleInstanceGuard>());
            Assert.False(producer.Services.GetRequiredService<IWolverineRuntime>().Options.Durability.DurabilityAgentEnabled);
            var added = await producer.Services.GetRequiredService<HhLinkQueue>().AddAsync("https://hh.ru/vacancy/901001 @recruiter_alpha", Ct);
            Assert.Equal([901001L], added.Ids);
            Assert.Empty(gateway.Reads);
            await producer.StopAsync(Ct);
        }
        using var consumer = await HostAsync(true, gateway);
        await consumer.StartAsync(Ct);
        try
        {
            await UntilAsync(async () => (await RequestAsync(901001)).Stage == HhLinkStage.Completed);
            Assert.Equal([901001L], gateway.Reads);
            Assert.Equal(["@recruiter_alpha"], Assert.Single(gateway.Links));
            Assert.Equal(1, await NotificationsAsync());
            var row = await RequestAsync(901001);
            Assert.Equal(901001L, row.VacancyId);
            Assert.NotNull(row.CompletedAt);
            await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new ReadHhLink { RequestId = row.Id });
            await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new LinkHhContacts { RequestId = row.Id });
            await Task.Delay(1500, Ct);
            Assert.Single(gateway.Reads);
            Assert.Single(gateway.Links);
            Assert.Equal(1, await NotificationsAsync());
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Concurrent_producers_merge_contacts_without_losing_work_or_publishing_duplicates()
    {
        using var producer = await HostAsync(false, new Gateway());
        await producer.StartAsync(Ct);
        try
        {
            var requests = producer.Services.GetRequiredService<HhLinkRequests>();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(index => requests.EnqueueAsync([new PendingLink(901002, [$"@contact_{index}"])], Ct)));
            var row = await RequestAsync(901002);
            Assert.Equal(8, row.Contacts.Count);
            Assert.Equal(8, row.Contacts.Distinct().Count());
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Single(await context.HhLinkRequests.ToListAsync(Ct));
            var pending = await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_discovery").SingleAsync(Ct);
            Assert.Equal(1, pending);
        }
        finally { await producer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Legacy_import_is_atomic_idempotent_and_merges_repeated_lines()
    {
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Settings.Add(new AppSetting { Key = HhLinkQueue.Key, Value = "901003 @alpha_hr\n901003 @beta_hr\ninvalid\n-2\n", UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        using var producer = await HostAsync(false, new Gateway());
        await producer.StartAsync(Ct);
        try
        {
            var requests = producer.Services.GetRequiredService<HhLinkRequests>();
            Assert.Equal(1, await requests.ImportLegacyAsync(Ct));
            Assert.Equal(0, await requests.ImportLegacyAsync(Ct));
            Assert.Equal(["@alpha_hr", "@beta_hr"], (await RequestAsync(901003)).Contacts);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.False(await context.Settings.AnyAsync(row => row.Key == HhLinkQueue.Key, Ct));
        }
        finally { await producer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Cancelling_during_read_prevents_the_next_stage_and_completion_notification()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Gateway { OnRead = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Ct); return new(901004, "read"); } };
        using var consumer = await HostAsync(true, gateway);
        await consumer.StartAsync(Ct);
        try
        {
            var requests = consumer.Services.GetRequiredService<HhLinkRequests>();
            await requests.EnqueueAsync([new PendingLink(901004, ["@alpha_hr"])], Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.True(await requests.CancelAsync((await RequestAsync(901004)).Id, Ct));
            release.SetResult();
            await Task.Delay(1500, Ct);
            Assert.Equal(HhLinkStage.Cancelled, (await RequestAsync(901004)).Stage);
            Assert.Empty(gateway.Links);
            Assert.Equal(0, await NotificationsAsync());
        }
        finally { release.TrySetResult(); await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task A_contact_added_during_linking_gets_its_own_continuation_without_repeating_the_read()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var gateway = new Gateway { OnLink = async _ => { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(Ct); } } };
        using var consumer = await HostAsync(true, gateway);
        await consumer.StartAsync(Ct);
        try
        {
            var requests = consumer.Services.GetRequiredService<HhLinkRequests>();
            await requests.EnqueueAsync([new PendingLink(901005, ["@alpha_hr"])], Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            await requests.EnqueueAsync([new PendingLink(901005, ["@beta_hr"])], Ct);
            release.SetResult();
            await UntilAsync(async () => (await RequestAsync(901005)).Stage == HhLinkStage.Completed);
            Assert.Single(gateway.Reads);
            Assert.Equal(2, gateway.Links.Count);
            Assert.Equal(["@alpha_hr", "@beta_hr"], gateway.Links.Last());
            Assert.Equal(1, await NotificationsAsync());
        }
        finally { release.TrySetResult(); await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task A_source_pause_persists_a_scheduled_retry_and_does_not_block_a_healthy_link()
    {
        var gateway = new Gateway { OnRead = id => id == 901006
            ? throw new SourceBlockedException("hh.ru", "isolated source pause", DateTimeOffset.UtcNow.AddHours(1), fresh: false)
            : Task.FromResult(new HhLinkReadResult(id, "healthy")) };
        using var consumer = await HostAsync(true, gateway);
        await consumer.StartAsync(Ct);
        try
        {
            await consumer.Services.GetRequiredService<HhLinkRequests>().EnqueueAsync([new PendingLink(901006, []), new PendingLink(901007, [])], Ct);
            await UntilAsync(async () => (await RequestAsync(901007)).Stage == HhLinkStage.Completed);
            var waiting = await RequestAsync(901006);
            Assert.Equal(HhLinkStage.Reading, waiting.Stage);
            Assert.True(waiting.RetryAt > DateTimeOffset.UtcNow.AddMinutes(59));
            Assert.Equal(0, waiting.Failures);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var scheduled = await context.Database.SqlQuery<int>($"SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_discovery_scheduled) + (SELECT count(*) FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Scheduled'))::int AS \"Value\"").SingleAsync(Ct);
            Assert.True(scheduled > 0);
            Assert.Equal(1, await NotificationsAsync());
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task A_restart_after_read_commit_retries_only_contacts_and_not_the_completed_read()
    {
        var first = new Gateway { OnLink = _ => throw new InvalidOperationException("isolated contact failure") };
        using (var consumer = await HostAsync(true, first))
        {
            await consumer.StartAsync(Ct);
            await consumer.Services.GetRequiredService<HhLinkRequests>().EnqueueAsync([new PendingLink(901008, ["@alpha_hr"])], Ct);
            await UntilAsync(async () => (await RequestAsync(901008)) is { Stage: HhLinkStage.Linking, Failures: 1 });
            await consumer.StopAsync(Ct);
        }
        var second = new Gateway();
        using var restarted = await HostAsync(true, second);
        await restarted.StartAsync(Ct);
        try
        {
            await UntilAsync(async () => (await RequestAsync(901008)).Stage == HhLinkStage.Completed);
            Assert.Single(first.Reads);
            Assert.Empty(second.Reads);
            Assert.Single(second.Links);
            Assert.Equal(1, await NotificationsAsync());
        }
        finally { await restarted.StopAsync(Ct); }
    }

    [Fact]
    public async Task Resubmission_after_account_login_wakes_a_paused_link_but_anonymous_resubmission_does_not()
    {
        var accountReady = false;
        var gateway = new Gateway { OnRead = id => accountReady ? Task.FromResult(new HhLinkReadResult(id, "read under account"))
            : throw new SourceBlockedException("hh.ru", "isolated pause before login", DateTimeOffset.UtcNow.AddHours(1), fresh: false) };
        using var consumer = await HostAsync(true, gateway);
        await consumer.StartAsync(Ct);
        try
        {
            var queue = consumer.Services.GetRequiredService<HhLinkQueue>();
            await queue.AddAsync("https://hh.ru/vacancy/901017 @first_hr", Ct);
            await UntilAsync(async () => (await RequestAsync(901017)).RetryAt > DateTimeOffset.UtcNow);
            await queue.AddAsync("https://hh.ru/vacancy/901017 @second_hr", Ct);
            Assert.Single(gateway.Reads);
            Assert.True((await RequestAsync(901017)).RetryAt > DateTimeOffset.UtcNow.AddMinutes(59));
            accountReady = true;
            gateway.Access = new HhLinkAccess(true, null);
            Assert.True((await queue.AddAsync("https://rabota.by/vacancy/901017", Ct)).ViaAccount);
            await UntilAsync(async () => (await RequestAsync(901017)).Stage == HhLinkStage.Completed);
            Assert.Equal(2, gateway.Reads.Count);
            Assert.Equal(["@first_hr", "@second_hr"], Assert.Single(gateway.Links));
            var completed = await RequestAsync(901017);
            Assert.Null(completed.RetryAt);
            Assert.Equal(0, completed.Failures);
            Assert.Equal(1, await NotificationsAsync());
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task An_outbox_state_save_failure_rolls_back_both_the_request_and_command()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(new FailSave());
        using var producer = await HostAsync(false, new Gateway(), new Factory(options.Options));
        await producer.StartAsync(Ct);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Services.GetRequiredService<HhLinkRequests>().EnqueueAsync([new PendingLink(901009, [])], Ct));
            Assert.False(await context.HhLinkRequests.AnyAsync(row => row.HhId == 901009, Ct));
            var count = await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_discovery").SingleAsync(Ct);
            Assert.Equal(0, count);
        }
        finally { await producer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Another_server_fails_before_recovery_or_native_listener_start()
    {
        var firstGateway = new Gateway();
        using var first = await HostAsync(true, firstGateway);
        await first.StartAsync(Ct);
        try
        {
            using var second = await HostAsync(true, new Gateway());
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(Ct));
            Assert.False(second.Services.GetRequiredService<ServerOwnership>().IsOwned);
            Assert.True(first.Services.GetRequiredService<ServerOwnership>().IsOwned);
        }
        finally { await first.StopAsync(Ct); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_http_parser_ingest_contacts_and_notification_complete_without_any_external_send(bool failReadCommit)
    {
        var html = Fixture.Read("hh_vacancy_byn.html");
        var expected = HhStateParser.ParseVacancy(HhStateParser.ExtractState(html), "rabota.by");
        using var web = new MockHttpMessageHandler();
        var endpoint = web.Expect(HttpMethod.Get, expected.Url).Respond("text/html", html);
        using var client = web.ToHttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        var fault = new FailReadCommit();
        IDbContextFactory<JobFinderDbContext>? factory = null;
        if (failReadCommit)
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var options = new DbContextOptionsBuilder<JobFinderDbContext>();
            JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
            options.AddInterceptors(fault);
            factory = new Factory(options.Options);
        }
        using var consumer = await HostAsync(true, null, factory, services =>
        {
            services.RemoveAll<PoliteHttp>();
            services.AddSingleton(new PoliteHttp(client, new ScrapeOptions { MinDelayMs = 0, MaxDelayMs = 1 }, TimeProvider.System));
        });
        await consumer.StartAsync(Ct);
        try
        {
            var requests = consumer.Services.GetRequiredService<HhLinkRequests>();
            await requests.EnqueueAsync([new PendingLink(expected.Id, ["@synthetic_recruiter"])], Ct);
            await UntilAsync(async () => (await RequestAsync(expected.Id)).Stage == HhLinkStage.Completed);
            var first = await RequestAsync(expected.Id);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var vacancy = await context.Vacancies.AsNoTracking().SingleAsync(row => row.Id == first.VacancyId, Ct);
            Assert.Equal(expected.Name, vacancy.Title);
            Assert.Equal(expected.CompanyName, vacancy.CompanyName);
            Assert.Equal(expected.Description, vacancy.Description);
            Assert.Equal(expected.KeySkills, vacancy.Skills);
            Assert.Equal("BYN", vacancy.Currency);
            Assert.Equal(expected.Url, vacancy.Url);
            Assert.Equal(SourceKind.HeadHunter, (await context.VacancySources.SingleAsync(row => row.VacancyId == vacancy.Id, Ct)).Source);
            var contact = await context.CompanyContacts.SingleAsync(row => row.Value == "@synthetic_recruiter", Ct);
            Assert.Equal(vacancy.CompanyName, contact.CompanyName);
            Assert.Equal(ContactRegistry.ManualSource, contact.Source);
            Assert.Equal(ContactKind.Telegram, contact.Kind);
            var notification = await context.Notifications.SingleAsync(row => row.Text.StartsWith("➕ <b>Вакансии по ссылкам</b>"), Ct);
            Assert.Contains(expected.Name, notification.Text);
            Assert.Contains("@synthetic_recruiter", notification.Text);
            Assert.Empty(await context.Applications.ToListAsync(Ct));
            Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
            Assert.Empty(await context.TelegramSessions.ToListAsync(Ct));
            await requests.EnqueueAsync([new PendingLink(expected.Id, ["@synthetic_recruiter"])], Ct);
            await UntilAsync(async () => await context.HhLinkRequests.CountAsync(row => row.HhId == expected.Id && row.Stage == HhLinkStage.Completed, Ct) == 2);
            Assert.Equal(1, web.GetMatchCount(endpoint));
            Assert.Equal(1, await context.Vacancies.CountAsync(Ct));
            Assert.Equal(1, await context.CompanyContacts.CountAsync(Ct));
            Assert.Equal(2, await context.Notifications.CountAsync(row => row.Text.StartsWith("➕ <b>Вакансии по ссылкам</b>"), Ct));
            Assert.Equal(failReadCommit ? 1 : 0, fault.Failures);
            web.VerifyNoOutstandingExpectation();
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Invalid_policy_stops_startup_before_native_listeners_take_persisted_work()
    {
        var gateway = new Gateway();
        using (var producer = await HostAsync(false, gateway))
        {
            await producer.StartAsync(Ct);
            await producer.Services.GetRequiredService<HhLinkRequests>().EnqueueAsync([new PendingLink(901010, [])], Ct);
            await producer.StopAsync(Ct);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var policy = await context.Documents.SingleOrDefaultAsync(row => row.Key == SearchPolicyStore.Key, Ct);
        var original = policy?.Content;
        if (policy is null)
        {
            policy = new AppDocument { Key = SearchPolicyStore.Key, Content = "{}", Version = 1, UpdatedAt = DateTimeOffset.UtcNow };
            context.Documents.Add(policy);
        }
        else policy.Content = "{}";
        await context.SaveChangesAsync(Ct);
        try
        {
            using var consumer = await HostAsync(true, gateway);
            await Assert.ThrowsAsync<JsonException>(() => consumer.StartAsync(Ct));
            Assert.Empty(gateway.Reads);
            Assert.Equal(HhLinkStage.Reading, (await RequestAsync(901010)).Stage);
            Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_discovery").SingleAsync(Ct));
        }
        finally
        {
            if (original is null) context.Documents.Remove(policy);
            else policy.Content = original;
            await context.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task Future_versions_and_empty_identities_are_dead_lettered_without_retrying_or_mutating_work()
    {
        var id = Guid.NewGuid();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.HhLinkRequests.Add(new HhLinkRequest { Id = id, HhId = 901011, Stage = HhLinkStage.Reading, CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var gateway = new Gateway();
        using var consumer = await HostAsync(true, gateway);
        await consumer.StartAsync(Ct);
        try
        {
            var bus = consumer.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(new ReadHhLink { RequestId = id, Version = 2 });
            await bus.PublishAsync(new LinkHhContacts { RequestId = Guid.Empty });
            await UntilAsync(async () =>
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) == 2;
            });
            Assert.Empty(gateway.Reads);
            Assert.Empty(gateway.Links);
            Assert.Equal(HhLinkStage.Reading, (await RequestAsync(901011)).Stage);
            Assert.Equal(0, (await RequestAsync(901011)).Failures);
            await consumer.Services.GetRequiredService<HhLinkRequests>().EnqueueAsync([new PendingLink(901012, [])], Ct);
            await UntilAsync(async () => (await RequestAsync(901012)).Stage == HhLinkStage.Completed);
            Assert.Equal([901012L], gateway.Reads);
            Assert.Equal(1, await NotificationsAsync());
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_lost_ownership_cannot_advance_work_or_consume_business_retries(bool lostDuringRead)
    {
        ServerOwnership? ownership = null;
        var gateway = new Gateway { OnRead = id => { ownership!.Lost(); return Task.FromResult(new HhLinkReadResult(id, "read before ownership loss")); } };
        using var producer = await HostAsync(false, gateway);
        await producer.StartAsync(Ct);
        try
        {
            var requests = producer.Services.GetRequiredService<HhLinkRequests>();
            await requests.EnqueueAsync([new PendingLink(901013, [])], Ct);
            ownership = producer.Services.GetRequiredService<ServerOwnership>();
            if (lostDuringRead) ownership.Acquired();
            var row = await RequestAsync(901013);
            await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Services.GetRequiredService<HhLinkWorkflow>().ReadAsync(new ReadHhLink { RequestId = row.Id }, Ct));
            var current = await RequestAsync(901013);
            Assert.Equal(HhLinkStage.Reading, current.Stage);
            Assert.Equal(0, current.Failures);
            Assert.Null(current.VacancyId);
            Assert.Equal(lostDuringRead ? 1 : 0, gateway.Reads.Count);
            Assert.Empty(gateway.Links);
            Assert.Equal(0, await NotificationsAsync());
        }
        finally { await producer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Failed_legacy_import_keeps_the_old_queue_and_has_no_native_partial_command()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        const string legacy = "901014 @alpha_hr\n901014 @beta_hr";
        context.Settings.Add(new AppSetting { Key = HhLinkQueue.Key, Value = legacy, UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(new FailSave());
        using var producer = await HostAsync(false, new Gateway(), new Factory(options.Options));
        await producer.StartAsync(Ct);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Services.GetRequiredService<HhLinkRequests>().ImportLegacyAsync(Ct));
            Assert.Empty(await context.HhLinkRequests.AsNoTracking().ToListAsync(Ct));
            Assert.Equal(legacy, await context.Settings.AsNoTracking().Where(row => row.Key == HhLinkQueue.Key).Select(row => row.Value).SingleAsync(Ct));
            Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_discovery").SingleAsync(Ct));
        }
        finally { await producer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Rollback_refuses_request_data_and_preserves_its_durable_command()
    {
        using (var producer = await HostAsync(false, new Gateway()))
        {
            await producer.StartAsync(Ct);
            await producer.Services.GetRequiredService<HhLinkRequests>().EnqueueAsync([new PendingLink(901015, [])], Ct);
            await producer.StopAsync(Ct);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        try
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930080041_LlmCallProvenance", Ct));
            Assert.Equal("P0001", error.SqlState);
            Assert.Contains("hh-link rollback refused", error.MessageText);
            Assert.Equal(HhLinkStage.Reading, (await RequestAsync(901015)).Stage);
            Assert.Contains("20260930084019_DurableHhLinkRequests", await context.Database.GetAppliedMigrationsAsync(Ct));
            Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_discovery").SingleAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Four_business_failures_retire_the_request_once_and_new_envelopes_do_not_repeat_it()
    {
        var gateway = new Gateway { OnRead = _ => throw new InvalidOperationException("isolated repeated read failure") };
        using var producer = await HostAsync(false, gateway);
        await producer.StartAsync(Ct);
        try
        {
            await producer.Services.GetRequiredService<HhLinkRequests>().EnqueueAsync([new PendingLink(901016, [])], Ct);
            producer.Services.GetRequiredService<ServerOwnership>().Acquired();
            var command = new ReadHhLink { RequestId = (await RequestAsync(901016)).Id };
            var workflow = producer.Services.GetRequiredService<HhLinkWorkflow>();
            for (var attempt = 1; attempt < 4; attempt++)
                await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ReadAsync(command, Ct));
            await workflow.ReadAsync(command, Ct);
            await workflow.ReadAsync(command, Ct);
            var failed = await RequestAsync(901016);
            Assert.Equal(HhLinkStage.Failed, failed.Stage);
            Assert.Equal(4, failed.Failures);
            Assert.NotNull(failed.CompletedAt);
            Assert.Equal(4, gateway.Reads.Count);
            Assert.Empty(gateway.Links);
            Assert.Equal(1, await NotificationsAsync());
        }
        finally { await producer.StopAsync(Ct); }
    }

    [Fact]
    public void Old_message_payloads_keep_the_original_version_default()
    {
        var id = Guid.NewGuid();
        var legacy = JsonSerializer.Deserialize($"{{\"requestId\":\"{id}\"}}", DiscoveryMessageJsonContext.Default.ReadHhLink)!;
        Assert.Equal(1, legacy.Version);
        Assert.Equal(id, legacy.RequestId);
    }

    private sealed class Gateway : IHhLinkGateway
    {
        public ConcurrentQueue<long> Reads { get; } = new();
        public ConcurrentQueue<IReadOnlyList<string>> Links { get; } = new();
        public Func<long, Task<HhLinkReadResult>>? OnRead { get; init; }
        public Func<IReadOnlyList<string>, Task>? OnLink { get; init; }
        public HhLinkAccess Access { get; set; } = new(false, null);
        public Task<HhLinkAccess> AccessAsync(CancellationToken ct) => Task.FromResult(Access);
        public Task<HhLinkReadResult> ReadAsync(long hhId, CancellationToken ct)
        {
            Reads.Enqueue(hhId);
            return OnRead?.Invoke(hhId) ?? Task.FromResult(new HhLinkReadResult(hhId, $"#{hhId} Senior .NET — synthetic company"));
        }
        public async Task<IReadOnlyList<LinkedContact>> LinkAsync(long vacancyId, IReadOnlyList<string> contacts, CancellationToken ct)
        {
            Links.Enqueue(contacts.ToArray());
            if (OnLink is not null) await OnLink(contacts);
            return contacts.Select((value, index) => new LinkedContact(index + 1, ContactKind.Telegram, value, "synthetic vacancy")).ToList();
        }
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("isolated outbox save failure");
    }

    private sealed class FailReadCommit : SaveChangesInterceptor
    {
        private int _armed = 1;
        public int Failures { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<HhLinkRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == HhLinkStage.Linking)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Failures++;
                throw new InvalidOperationException("isolated read stage commit failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }
}
