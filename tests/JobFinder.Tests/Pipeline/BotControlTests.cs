using System.Text.Json;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Controls;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Telegram.Bot.Types;

namespace JobFinder.Tests.Pipeline;

public sealed partial class BotControlTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Executor _executor = new();
    private IHost? _host;
    private BotControls Controls => _host!.Services.GetRequiredService<BotControls>();
    private BotControlWorkflow Workflow => _host!.Services.GetRequiredService<BotControlWorkflow>();

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.BotControlRequests.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith(BotControls.CursorPrefix)).ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_bot_controls; DELETE FROM jobfinder_queues.wolverine_queue_bot_controls_scheduled", Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        _host = await HostAsync(false);
        await _host.StartAsync(Ct);
        _host.Services.GetRequiredService<ServerOwnership>().Acquired();
    }

    private Task<IHost> HostAsync(bool consumer, SaveChangesInterceptor? fault = null) => PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: services =>
    {
        services.RemoveAll<IBotControlExecutor>();
        services.AddSingleton<IBotControlExecutor>(_executor);
    });

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { _host.Dispose(); }
        }
    }

    private static BotPollUpdate Update(int id) => new() { UpdateId = id, PayloadJson = "{\"command\":\"synthetic\"}" };
    private async Task<BotControlRequest> RootAsync(int updateId)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.BotControlRequests.AsNoTracking().SingleAsync(row => row.UpdateId == updateId, Ct);
    }
    private async Task RunAsync(int updateId)
    {
        var root = await RootAsync(updateId);
        await Workflow.RunAsync(new() { RequestId = root.Id }, Guid.NewGuid(), Ct);
    }

    [Fact]
    public async Task Poll_batch_cursor_roots_and_native_commands_commit_together_before_acknowledgement()
    {
        Assert.Null(await Controls.OffsetAsync(123456, Ct));
        Assert.Equal(13, await Controls.AdmitAsync(123456, 777, null, [Update(10), new() { UpdateId = 11 }, Update(12)], Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.BotControlRequests.CountAsync(Ct));
        Assert.Equal(13, await Controls.OffsetAsync(123456, Ct));
        var count = await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM jobfinder_queues.wolverine_queue_bot_controls").SingleAsync(Ct);
        Assert.Equal(2, count);
        Assert.Equal(13, await Controls.AdmitAsync(123456, 777, null, [Update(10), Update(12)], Ct));
        Assert.Equal(2, await context.BotControlRequests.CountAsync(Ct));
        Assert.Empty(_executor.Calls);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)JobFinder.Core.Messaging.DurableQueues.All).Clear());
    }

    [Fact]
    public async Task Damaged_capture_rolls_back_the_entire_batch_and_cursor()
    {
        await Assert.ThrowsAsync<UnsupportedBotControlException>(() => Controls.AdmitAsync(123456, 777, null,
            [Update(10), new() { UpdateId = 11, PayloadJson = "[]" }], Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.BotControlRequests.ToListAsync(Ct));
        Assert.Null(await Controls.OffsetAsync(123456, Ct));
        Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM jobfinder_queues.wolverine_queue_bot_controls").SingleAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_poll_admission_keeps_one_root_and_one_command()
    {
        var results = await Task.WhenAll(Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct), Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct));
        Assert.All(results, result => Assert.Equal(11, result));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.BotControlRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Producer_exit_then_native_consumer_executes_the_saved_update_once()
    {
        await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
        await _host!.StopAsync(Ct);
        _host.Dispose();
        _host = null;
        using var consumer = await HostAsync(true);
        await consumer.StartAsync(Ct);
        try
        {
            var until = DateTimeOffset.UtcNow.AddSeconds(25);
            while ((await RootAsync(10)).Stage != BotControlStage.Completed && DateTimeOffset.UtcNow < until) await Task.Delay(100, Ct);
            Assert.Equal(BotControlStage.Completed, (await RootAsync(10)).Stage);
            Assert.Single(_executor.Calls);
            Assert.Equal(777, _executor.Calls[0].OwnerId);
            Assert.Equal(Update(10).PayloadJson, _executor.Calls[0].PayloadJson);
        }
        finally { await consumer.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct); }
    }

    [Fact]
    public async Task Missing_bot_account_defers_without_claim_or_dispatch()
    {
        await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
        _executor.Available = false;
        await RunAsync(10);
        Assert.Equal(BotControlStage.Pending, (await RootAsync(10)).Stage);
        Assert.Null((await RootAsync(10)).ClaimEnvelopeId);
        Assert.Empty(_executor.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, await context.Database.SqlQueryRaw<long>("SELECT (SELECT count(*) FROM jobfinder_queues.wolverine_queue_bot_controls_scheduled) + (SELECT count(*) FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND message_type = 'jobfinder.controls.process-bot.v1') AS \"Value\"").SingleAsync(Ct));
    }

    [Fact]
    public async Task Failed_handler_is_not_replayed_and_later_controls_wait_for_checked_reconciliation()
    {
        await Controls.AdmitAsync(123456, 777, null, [Update(10), Update(11)], Ct);
        _executor.Fail = true;
        await RunAsync(10);
        var failed = await RootAsync(10);
        Assert.Equal(BotControlStage.OutcomeUnknown, failed.Stage);
        await RunAsync(10);
        await RunAsync(11);
        Assert.Single(_executor.Calls);
        Assert.Equal(BotControlStage.Pending, (await RootAsync(11)).Stage);
        Assert.True(await Controls.ReconcileAsync(failed.Id, "Synthetic operator inspected the committed source; no repeat authorized", Ct));
        Assert.False(await Controls.ReconcileAsync(failed.Id, "Duplicate assertion", Ct));
        _executor.Fail = false;
        await RunAsync(11);
        await RunAsync(11);
        Assert.Equal(2, _executor.Calls.Count);
        Assert.Equal(BotControlStage.Completed, (await RootAsync(11)).Stage);
        var reconciled = await RootAsync(10);
        Assert.Equal(BotControlStage.Reconciled, reconciled.Stage);
        Assert.Equal(failed.InputJson, reconciled.InputJson);
        Assert.Equal(failed.ClaimEnvelopeId, reconciled.ClaimEnvelopeId);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        foreach (var notice in await context.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Interrupted_claim_is_unknown_on_startup_or_envelope_replay_without_a_second_call(bool startup)
    {
        await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
        _executor.Cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(10));
        Assert.Equal(BotControlStage.Executing, (await RootAsync(10)).Stage);
        if (startup)
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(1, await BotControls.RecoverAsync(context, _host!.Services.GetRequiredService<Notifier>(), Ct));
            Assert.Equal(0, await BotControls.RecoverAsync(context, _host.Services.GetRequiredService<Notifier>(), Ct));
        }
        else await RunAsync(10);
        await RunAsync(10);
        Assert.Equal(BotControlStage.OutcomeUnknown, (await RootAsync(10)).Stage);
        Assert.Single(_executor.Calls);
    }

    [Fact]
    public async Task Tampered_capture_is_retained_quarantined_and_never_executed()
    {
        await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.BotControlRequests.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.InputJson, "{}"), Ct);
        await RunAsync(10);
        Assert.Equal(BotControlStage.Quarantined, (await RootAsync(10)).Stage);
        Assert.Equal("{}", (await RootAsync(10)).InputJson);
        Assert.Empty(_executor.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":1}")]
    public async Task Cursor_is_owned_and_corruption_is_not_silently_reset(string damaged)
    {
        await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
        var settings = _host!.Services.GetRequiredService<RuntimeSettings>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("bot.poll.123456", "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync("bot.poll.123456", Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Settings.Where(row => row.Key == "bot.poll.123456").ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Value, damaged), Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => Controls.OffsetAsync(123456, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_Telegram_update_roundtrip_checks_the_captured_owner_and_identity_before_dispatch(bool callback)
    {
        var options = new BotOptions { OwnerUserId = 777, Token = "123456:synthetic-bot-token" };
        var holder = new TelegramBotClientHolder(options, _host!.Services.GetRequiredService<IHttpClientFactory>());
        var constructor = typeof(BotService).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(BotOptions) ? (object)options
            : parameter.ParameterType == typeof(TelegramBotClientHolder) ? holder : _host.Services.GetService(parameter.ParameterType)).ToArray();
        var service = (BotService)constructor.Invoke(arguments);
        var update = new Update { Id = 10, Message = new() { Id = 42, From = new() { Id = 777, FirstName = "Synthetic" }, Chat = new() { Id = 777, Type = Telegram.Bot.Types.Enums.ChatType.Private }, Text = "/status" } };
        if (callback) update = new() { Id = 10, CallbackQuery = new() { Id = "synthetic-callback", From = update.Message.From!, Message = update.Message,
            ChatInstance = "synthetic-chat", Data = "noop:0:0" } };
        var input = new BotControlInput { BotId = 123456, OwnerId = 777, UpdateId = 10, PayloadJson = JsonSerializer.Serialize(update) };
        Assert.True(service.CanExecute(input));
        input.UpdateId = 11;
        Assert.Throws<UnsupportedBotControlException>(() => service.CanExecute(input));
        input.UpdateId = 10;
        (update.Message?.From ?? update.CallbackQuery!.From).Id = 778;
        input.PayloadJson = JsonSerializer.Serialize(update);
        Assert.Throws<UnsupportedBotControlException>(() => service.CanExecute(input));
        input.OwnerId = 778;
        Assert.False(service.CanExecute(input));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("cursor")]
    [InlineData("future-command")]
    public async Task Downgrade_refuses_control_evidence_and_future_orphan_commands(string evidence)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (evidence == "future-command")
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_queues.wolverine_queue_bot_controls (id,body,message_type) VALUES ({Guid.NewGuid()},{new byte[] { 1 }},{"jobfinder.controls.process-bot.v99"})", Ct);
        else
        {
            await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
            await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_bot_controls", Ct);
            if (evidence == "cursor") await context.BotControlRequests.ExecuteDeleteAsync(Ct);
            else await context.Settings.Where(row => row.Key.StartsWith(BotControls.CursorPrefix)).ExecuteDeleteAsync(Ct);
        }
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003160007_LegacyNotificationReconciliation", Ct));
        Assert.Contains("Cannot roll back bot controls", error.Message, StringComparison.Ordinal);
        Assert.Contains("20261003172927_DurableBotControls", await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Actual_native_bot_handler_keeps_committed_pause_and_admits_response_without_inline_SDK_delivery()
    {
        await _host!.StopAsync(Ct);
        _host.Dispose();
        _host = null;
        using var api = new LostResponseApi();
        using var consumer = await PostProcessingTestHost.BuildAsync(pg, true, configure: services =>
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bot:Token"] = "123456:synthetic-bot-token", ["Bot:OwnerUserId"] = "777", ["Bot:Enabled"] = "true",
            }).Build();
            services.AddJobFinderBot(config, false);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new ApiFactory(api));
            services.RemoveAll<INotificationDeliveryTransport>();
            services.AddSingleton<INotificationDeliveryTransport, UnavailableNotificationTransport>();
        });
        await consumer.StartAsync(Ct);
        try
        {
            var controls = consumer.Services.GetRequiredService<BotControls>();
            var update = new Update { Id = 10, Message = new() { Id = 42, From = new() { Id = 777, FirstName = "Synthetic" },
                Chat = new() { Id = 777, Type = Telegram.Bot.Types.Enums.ChatType.Private }, Text = "/pause" } };
            await controls.AdmitAsync(123456, 777, null, [new() { UpdateId = 10, PayloadJson = JsonSerializer.Serialize(update) }], Ct);
            var until = DateTimeOffset.UtcNow.AddSeconds(25);
            while ((await RootAsync(10)).Stage != BotControlStage.Completed && DateTimeOffset.UtcNow < until) await Task.Delay(100, Ct);
            var root = await RootAsync(10);
            Assert.Equal(BotControlStage.Completed, root.Stage);
            Assert.True((await consumer.Services.GetRequiredService<RuntimeSettings>().GetDecisionAsync(Ct)).AutoApplyPaused);
            Assert.Equal(0, api.Calls);
            await consumer.Services.GetRequiredService<BotControlWorkflow>().RunAsync(new() { RequestId = root.Id }, Guid.NewGuid(), Ct);
            Assert.Equal(0, api.Calls);
            Assert.NotNull(root.ClaimEnvelopeId);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
            Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
            Assert.NotEmpty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
            Assert.DoesNotContain(await context.NotificationDeliveryRequests.ToListAsync(Ct), row => row.Stage == NotificationDeliveryStage.Completed);
        }
        finally { await consumer.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct); }
    }

    private sealed class ApiFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class LostResponseApi : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.EndsWith("/sendMessage", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Interlocked.Increment(ref Calls);
            throw new HttpRequestException("synthetic SDK acceptance with lost response");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SQL_fault_does_not_lose_admission_or_replay_an_already_executed_control(bool completion)
    {
        await _host!.StopAsync(Ct);
        _host.Dispose();
        _host = await HostAsync(false, new SaveFault(completion));
        await _host.StartAsync(Ct);
        _host.Services.GetRequiredService<ServerOwnership>().Acquired();
        if (!completion)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct));
            Assert.Null(await Controls.OffsetAsync(123456, Ct));
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await context.BotControlRequests.ToListAsync(Ct));
            Assert.Equal(0, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM jobfinder_queues.wolverine_queue_bot_controls").SingleAsync(Ct));
            await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
            await RunAsync(10);
            Assert.Equal(BotControlStage.Completed, (await RootAsync(10)).Stage);
        }
        else
        {
            await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(10));
            Assert.Equal(BotControlStage.Executing, (await RootAsync(10)).Stage);
            await RunAsync(10);
            Assert.Equal(BotControlStage.OutcomeUnknown, (await RootAsync(10)).Stage);
        }
        Assert.Single(_executor.Calls);
    }

    private sealed class SaveFault(bool completion) : SaveChangesInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_failed && eventData.Context!.ChangeTracker.Entries<BotControlRequest>().Any(entry => completion
                ? entry.State == EntityState.Modified && entry.Entity.Stage == BotControlStage.Completed : entry.State == EntityState.Added))
            {
                _failed = true;
                throw new InvalidOperationException("synthetic control commit failure");
            }
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task Changed_execution_witness_cannot_be_certified_as_a_completed_control()
    {
        await Controls.AdmitAsync(123456, 777, null, [Update(10)], Ct);
        _executor.During = async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var root = await context.BotControlRequests.SingleAsync(Ct);
            var input = BotControls.Read(root);
            input.PayloadJson = "{\"command\":\"changed-after-claim\"}";
            root.InputJson = JsonSerializer.Serialize(input, BotControlJsonContext.Default.BotControlInput);
            root.InputHash = JobFinder.Core.Text.CanonicalJson.Hash(root.InputJson);
            await context.SaveChangesAsync(Ct);
        };
        await RunAsync(10);
        Assert.Equal(BotControlStage.OutcomeUnknown, (await RootAsync(10)).Stage);
        Assert.Single(_executor.Calls);
        await RunAsync(10);
        Assert.Single(_executor.Calls);
    }

    private sealed class Executor : IBotControlExecutor
    {
        public List<BotControlInput> Calls { get; } = [];
        public bool Available { get; set; } = true;
        public bool Fail { get; set; }
        public bool Cancel { get; set; }
        public Func<Task>? During { get; set; }
        public bool CanExecute(BotControlInput input) => Available;
        public Task ExecuteAsync(BotControlInput input, CancellationToken ct)
        {
            Calls.Add(input);
            if (Cancel) throw new OperationCanceledException();
            if (Fail) throw new HttpRequestException("synthetic lost SDK receipt");
            return During?.Invoke() ?? Task.CompletedTask;
        }
    }
}
