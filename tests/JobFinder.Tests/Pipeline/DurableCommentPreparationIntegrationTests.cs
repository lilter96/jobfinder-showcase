using System.Collections.Concurrent;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Messaging;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly CandidateProfile Profile = new() { Name = "Synthetic Candidate", Reviewed = true };
    private const string Post = "Сегодня обсуждаем, как сократить ожидание ответа после собеседования и сделать процесс понятнее кандидату";
    private readonly List<IHost> _hosts = [];
    private readonly Steps _steps = new();

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.CommentDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == CommentDeliveryWorkflow.PacingKey).ExecuteDeleteAsync(Ct);
        await context.CommentPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.CommentSourceRequests.ExecuteDeleteAsync(Ct);
        await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        foreach (var queue in DurableQueues.All)
        {
            var cleanup = $"DELETE FROM jobfinder_queues.wolverine_queue_{queue}; DELETE FROM jobfinder_queues.wolverine_queue_{queue}_scheduled";
            await context.Database.ExecuteSqlRawAsync(cleanup, Ct);
        }
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        using var bootstrap = await BuildAsync(false);
        await bootstrap.StartAsync(Ct);
        try
        {
            var automation = bootstrap.Services.GetRequiredService<RuntimeSettings>().Automation;
            var control = await automation.ReadAsync(Ct);
            if (control.Value.CommentsPaused) await automation.PublishAsync(control.Value with { CommentsPaused = false }, control.Version, "test-isolation", Ct);
            await bootstrap.Services.GetRequiredService<LlmPolicyStore>().ReadAsync(Ct);
            await bootstrap.Services.GetRequiredService<SearchPolicyStore>().ReadAsync(Ct);
            await bootstrap.Services.GetRequiredService<EngagementPolicyStore>().ReadAsync(Ct);
        }
        finally { await bootstrap.StopAsync(CancellationToken.None); }
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _steps.Release.TrySetResult();
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Dispose(); }
        }
    }

    private async Task<IHost> BuildAsync(bool consumer, SaveChangesInterceptor? fault = null, ICommentPreparationSteps? steps = null,
        IHttpClientFactory? http = null, bool fastRecovery = false, TimeProvider? time = null, ICommentDeliveryTransport? delivery = null)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var root = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-comments-synthetic", Guid.NewGuid().ToString("N"));
        var configuration = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Paths:Data"] = Path.Combine(root, "data"), ["Paths:Profile"] = Path.Combine(root, "profile"),
            ["Llm:ApiKey"] = "synthetic-key", ["Llm:Endpoint"] = "https://provider.invalid/completions", ["Llm:Proxy"] = "",
        };
        foreach (var purpose in LlmPurpose.All)
        {
            configuration[$"Llm:Routes:{purpose}:0:Name"] = "synthetic/" + purpose.ToLowerInvariant();
            configuration[$"Llm:Routes:{purpose}:0:Reasoning:Effort"] = "max";
        }
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddJobFinderCore(builder.Configuration);
        if (time is not null)
        {
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton(time);
        }
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        if (fault is null) builder.Services.AddSingleton(pg.Db);
        else
        {
            var options = new DbContextOptionsBuilder<JobFinderDbContext>();
            JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
            options.AddInterceptors(fault);
            builder.Services.AddSingleton<IDbContextFactory<JobFinderDbContext>>(new Factory(options.Options));
        }
        if (http is null || steps is not null)
        {
            builder.Services.RemoveAll<ICommentPreparationSteps>();
            builder.Services.AddSingleton(steps ?? _steps);
        }
        builder.Services.RemoveAll<IHttpClientFactory>();
        builder.Services.AddSingleton(http ?? new NoNetwork());
        builder.Services.RemoveAll<ICommentDeliveryTransport>();
        builder.Services.AddSingleton(delivery ?? new DormantCommentTransport());
        builder.AddJobFinderMessaging(consumer);
        if (fastRecovery) builder.Services.ConfigureWolverine(JobFinder.MessagingProbe.ResumeProbe.FastRecovery);
        if (consumer) builder.Services.AddJobFinderInitialization();
        return builder.Build();
    }

    private async Task<IHost> HostAsync(bool consumer, SaveChangesInterceptor? fault = null)
    {
        var host = await BuildAsync(consumer, fault);
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private Task<Guid> RequestAsync(IHost host, bool force = false, string text = Post) =>
        host.Services.GetRequiredService<CommentPreparationRequests>().RequestAsync(Profile, "synthetic-author", text, Ct, force);

    private async Task<CommentPreparationRequest> ReadAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.CommentPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "native comment preparation did not reach the expected checkpoint");
            await Task.Delay(30, Ct);
        }
    }

    [Fact]
    public async Task Producer_exit_duplicate_requests_and_warm_reuse_make_one_paid_draft()
    {
        var producer = await HostAsync(false);
        var ids = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RequestAsync(producer)));
        Assert.All(ids, id => Assert.Equal(ids[0], id));
        await producer.StopAsync(Ct);
        Assert.Empty(_steps.Drafts);
        var consumer = await HostAsync(true);
        var result = await consumer.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(ids[0], Ct);
        Assert.True(result.Plan.WorthCommenting);
        Assert.Empty(result.Remarks);
        Assert.Equal(1, _steps.Drafts[ids[0]]);
        Assert.Equal(ids[0], await RequestAsync(consumer));
        var request = await ReadAsync(ids[0]);
        Assert.Null(request.ClaimEnvelopeId);
        Assert.NotNull(request.CompletedAt);
        Assert.DoesNotContain("synthetic-key", request.InputJson);
        Assert.DoesNotContain("https://provider.invalid", request.InputJson);
    }

    [Fact]
    public async Task Duplicate_business_commands_do_not_repeat_drafting_or_checking()
    {
        var producer = await HostAsync(false);
        var id = await RequestAsync(producer);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DraftComment { RequestId = id });
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new CheckComment { RequestId = id, Round = 2 });
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(true);
        await consumer.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(id, Ct);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DraftComment { RequestId = id });
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new CheckComment { RequestId = id });
        await Task.Delay(500, Ct);
        Assert.Equal(1, _steps.Drafts[id]);
        Assert.Equal(1, _steps.Checks[id]);
    }

    [Fact]
    public async Task Rewrite_checkpoints_both_rounds_and_receives_the_exact_first_remarks()
    {
        _steps.First = Bad();
        var host = await HostAsync(true);
        var id = await RequestAsync(host);
        var result = await host.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(id, Ct);
        var request = await ReadAsync(id);
        Assert.Equal(2, _steps.Drafts[id]);
        Assert.Equal(2, _steps.Checks[id]);
        var first = JsonSerializer.Deserialize(request.FirstJson!, CommentPreparationJsonContext.Default.CommentPreparationRound)!;
        Assert.Equal(string.Join("\n", first.Remarks), _steps.LastRemarks);
        Assert.NotEmpty(result.Remarks);
        Assert.Equal(Good().Variants, result.Plan.Variants);
        Assert.Equal(2, request.Round);
    }

    [Fact]
    public async Task An_unavailable_rewrite_keeps_only_a_usable_first_set_and_its_metadata()
    {
        _steps.First = Good() with { Reason = "first", Variants = [.. Good().Variants, "Отличный пост!"] };
        _steps.RewriteUnavailable = true;
        var host = await HostAsync(true);
        var id = await RequestAsync(host);
        var result = await host.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(id, Ct);
        Assert.True(result.Plan.WorthCommenting);
        Assert.Equal("first", result.Plan.Reason);
        Assert.Equal(Good().Variants, result.Plan.Variants);
        Assert.NotEmpty(result.Remarks);
        Assert.Equal(2, _steps.Drafts[id]);
        Assert.Null((await ReadAsync(id)).RetryAt);
    }

    [Fact]
    public async Task The_selected_best_set_keeps_its_audience_instead_of_the_worse_rewrite_metadata()
    {
        _steps.First = Good() with { Reason = "first", Variants = [.. Good().Variants, "Отличный пост!"] };
        _steps.Second = Bad() with { Audience = "tech", Reason = "second", Variants = ["Отличный пост!", "Полностью согласен!"] };
        var host = await HostAsync(true);
        var result = await host.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(await RequestAsync(host), Ct);
        Assert.Equal("recruiter", result.Plan.Audience);
        Assert.Equal("first", result.Plan.Reason);
        Assert.Equal(Good().Variants, result.Plan.Variants);
    }

    [Fact]
    public async Task Filtering_out_the_only_question_cannot_publish_a_statement_only_set()
    {
        _steps.First = Bad();
        _steps.Second = Bad();
        var host = await HostAsync(true);
        var result = await host.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(await RequestAsync(host), Ct);
        Assert.False(result.Plan.WorthCommenting);
        Assert.Empty(result.Plan.Variants);
        Assert.Contains("нет варианта с вопросом", result.Plan.Reason);
    }

    [Fact]
    public async Task A_provider_outage_schedules_the_existing_request_without_consuming_a_failure_or_blocking_other_work()
    {
        _steps.Unavailable = true;
        var host = await HostAsync(true);
        var id = await RequestAsync(host);
        await UntilAsync(async () => (await ReadAsync(id)).RetryAt is not null);
        var request = await ReadAsync(id);
        Assert.Equal(0, request.Failures);
        Assert.Null(request.ClaimEnvelopeId);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => host.Services.GetRequiredService<CommentPreparationRequests>().GetAsync(id, Ct));
        _steps.Unavailable = false;
        var healthy = await RequestAsync(host, text: Post + " Ещё один отдельный вопрос.");
        await host.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(healthy, Ct);
        Assert.Equal(1, _steps.Drafts[healthy]);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new DraftComment { RequestId = id });
        await host.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(id, Ct);
        Assert.Equal(2, _steps.Drafts[id]);
    }

    [Fact]
    public async Task Explicit_regeneration_fences_a_late_first_draft_and_does_not_reuse_a_warm_result()
    {
        _steps.HoldFirst = true;
        var host = await HostAsync(true);
        var old = await RequestAsync(host);
        await _steps.Entered.Task.WaitAsync(Ct);
        var fresh = await RequestAsync(host, force: true);
        var requests = host.Services.GetRequiredService<CommentPreparationRequests>();
        await requests.WaitAsync(fresh, Ct);
        _steps.Release.TrySetResult();
        await Assert.ThrowsAsync<CommentPreparationSupersededException>(() => requests.GetAsync(old, Ct));
        Assert.Equal(CommentPreparationStage.Cancelled, (await ReadAsync(old)).Stage);
        Assert.Null((await ReadAsync(old)).ResultJson);
        Assert.NotEqual(fresh, await RequestAsync(host, force: true));
    }

    [Fact]
    public async Task Checkpoint_failure_replays_checking_without_redrafting_the_saved_paid_output()
    {
        _steps.First = Bad();
        var host = await HostAsync(true, new FailRewriteCheckpoint());
        var id = await RequestAsync(host);
        var result = await host.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(id, Ct);
        Assert.True(result.Plan.WorthCommenting);
        Assert.Equal(2, _steps.Drafts[id]);
        Assert.Equal(3, _steps.Checks[id]);
        Assert.Equal(1, (await ReadAsync(id)).Failures);
    }

    [Fact]
    public async Task Cancellation_of_the_waiter_preserves_the_request_for_a_later_consumer()
    {
        var producer = await HostAsync(false);
        var id = await RequestAsync(producer);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(id, cancelled.Token));
        Assert.Equal(CommentPreparationStage.Drafting, (await ReadAsync(id)).Stage);
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(true);
        await consumer.Services.GetRequiredService<CommentPreparationRequests>().WaitAsync(id, Ct);
        Assert.Equal(1, _steps.Drafts[id]);
    }

    [Theory]
    [InlineData(2, 1, false)]
    [InlineData(1, 3, false)]
    [InlineData(1, 1, true)]
    public async Task Unsupported_commands_are_dead_lettered_without_mutating_or_executing_work(int version, int round, bool empty)
    {
        var host = await HostAsync(true);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new DraftComment { RequestId = empty ? Guid.Empty : Guid.NewGuid(), Version = version, Round = round });
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) == 1;
        });
        Assert.Empty(_steps.Drafts);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.CommentPreparationRequests.ToListAsync(Ct));
    }

    private static CommentPlan Good() => new()
    {
        Audience = "recruiter", WorthCommenting = true, Reason = "Короткий вопрос о процессе",
        Variants = ["А какой срок ответа вы обычно называете кандидату?", "Я бы заранее договаривался о дате следующего разговора."],
    };

    private static CommentPlan Bad() => Good() with { Variants = ["Отличный пост! А когда вы отвечаете кандидату?", "Я бы заранее договаривался о дате следующего разговора."] };

    private sealed class Steps : ICommentPreparationSteps
    {
        public ConcurrentDictionary<Guid, int> Drafts { get; } = new();
        public ConcurrentDictionary<Guid, int> Checks { get; } = new();
        public CommentPlan First { get; set; } = Good();
        public CommentPlan Second { get; set; } = Good();
        public bool Unavailable { get; set; }
        public bool RewriteUnavailable { get; set; }
        public bool HoldFirst { get; set; }
        public string? LastRemarks { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CommentPlan> DraftAsync(CommentPreparationInput input, string? remarks, CancellationToken ct)
        {
            var count = Drafts.AddOrUpdate(input.Operation.OperationId, 1, (_, before) => before + 1);
            if (HoldFirst && Drafts.Count == 1 && count == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            if (Unavailable || remarks is not null && RewriteUnavailable) throw new LlmUnavailableException("synthetic provider outage");
            LastRemarks = remarks;
            return remarks is null ? First : Second;
        }

        public CommentPreparationRound Check(CommentPreparationInput input, CommentPlan plan)
        {
            Checks.AddOrUpdate(input.Operation.OperationId, 1, (_, before) => before + 1);
            var (good, remarks) = CommentPlanner.Review(plan, input.ExampleShingles.ToHashSet(StringComparer.Ordinal));
            foreach (var problem in JobFinder.Core.Filtering.CommentStyle.SetProblems(good))
                if (!remarks.Contains(problem, StringComparer.Ordinal)) remarks.Add(problem);
            return new() { Plan = plan, Good = good, Remarks = remarks };
        }
    }

    private sealed class FailRewriteCheckpoint : SaveChangesInterceptor
    {
        private int _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CommentPreparationRequest>().Any(entry => entry.State == EntityState.Modified
                && entry.Entity.Stage == CommentPreparationStage.Drafting && entry.Entity.Round == 2) && Interlocked.Exchange(ref _failed, 1) == 0)
                throw new InvalidOperationException("synthetic rewrite checkpoint fault");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("synthetic comment workflow must not contact a recipient or real provider");
    }
}
