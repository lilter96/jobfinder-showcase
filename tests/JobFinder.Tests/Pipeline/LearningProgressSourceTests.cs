using System.Reflection;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.App.Components.Shared;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed class LearningProgressSourceTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.LearningProgressRevisions.ExecuteDeleteAsync(Ct);
        await context.MockPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.MockSessionRevisions.ExecuteDeleteAsync(Ct);
        await context.MockSessions.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("""
            DO $cleanup$
            DECLARE storage text;
            BEGIN
              FOREACH storage IN ARRAY ARRAY['jobfinder_queues.wolverine_queue_hiring_prepare', 'jobfinder_queues.wolverine_queue_hiring_prepare_scheduled',
                'jobfinder_queues.wolverine_queue_preparation', 'jobfinder_queues.wolverine_queue_preparation_scheduled',
                'jobfinder_messages.wolverine_incoming_envelopes', 'jobfinder_messages.wolverine_outgoing_envelopes', 'jobfinder_messages.wolverine_dead_letters'] LOOP
                IF to_regclass(storage) IS NOT NULL THEN
                  EXECUTE format('DELETE FROM %s WHERE message_type LIKE ''jobfinder.preparation.prepare-mock.%%'' OR message_type LIKE ''jobfinder.preparation.publish-mock.%%''', storage);
                END IF;
              END LOOP;
            END
            $cleanup$;
            """, Ct);
        await context.Settings.Where(row => row.Key == ConversationStore.Key).ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith("hr.") || row.Key.StartsWith("interview.") || row.Key == "synthetic.learning.parent").ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private async Task<LearningProgressSources> OwnerAsync(SaveChangesInterceptor? fault = null, Action<IServiceCollection>? configure = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault, configure: configure);
        await _host.StartAsync(Ct);
        return _host.Services.GetRequiredService<LearningProgressSources>();
    }

    private async Task SeedAsync(string key, string value)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var row = await context.Settings.FindAsync([key], Ct);
        if (row is null) context.Settings.Add(new() { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        else row.Value = value;
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Independent_concurrent_marks_and_approvals_merge_without_lost_updates()
    {
        var owner = await OwnerAsync();
        var captured = await owner.ReadAsync("hr", Ct);
        var results = await Task.WhenAll(owner.MarkAsync(captured.Item("first"), "known", Ct),
            owner.MarkAsync(captured.Item("second"), "again", Ct), owner.ApproveAsync(captured.Item("third:abc"), true, Ct));
        Assert.All(results, result => Assert.True(result.Result.Ok));
        var saved = await owner.ReadAsync("hr", Ct);
        Assert.Equal(3, saved.Revision);
        Assert.Equal("known", saved.Learned["first"]);
        Assert.Equal("again", saved.Learned["second"]);
        Assert.Contains("third:abc", saved.Approved);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(3, await context.LearningProgressRevisions.CountAsync(Ct));
        foreach (var notice in await context.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Equal(3, await context.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task Same_item_edits_accept_one_witness_and_fence_aba_and_stale_whole_reset()
    {
        var owner = await OwnerAsync();
        var captured = await owner.ReadAsync("hr", Ct);
        var results = await Task.WhenAll(owner.MarkAsync(captured.Item("first"), "known", Ct), owner.MarkAsync(captured.Item("first"), "again", Ct));
        Assert.Single(results, result => result.Result.Ok);
        var current = await owner.ReadAsync("hr", Ct);
        Assert.False((await owner.ResetAsync(captured, Ct)).Result.Ok);
        Assert.True((await owner.ResetAsync(current, Ct)).Result.Ok);
        Assert.False((await owner.MarkAsync(captured.Item("first"), "known", Ct)).Result.Ok);
        Assert.Equal(2, (await owner.ReadAsync("hr", Ct)).Item("first").Revision);
    }

    [Fact]
    public async Task Legacy_preimage_is_preserved_and_reset_retains_text_bound_approvals_and_tombstones()
    {
        const string original = " { \"first\": \"again\" } ";
        const string approved = "z:hash\nfirst:abc\nz:hash\n";
        await SeedAsync("hr.progress", original);
        await SeedAsync("hr.approved", approved);
        var owner = await OwnerAsync();
        var captured = await owner.ReadAsync("hr", Ct);
        Assert.Equal(0, captured.Revision);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.LearningProgressRevisions.ToListAsync(Ct));
        Assert.Null(await context.Settings.FindAsync(["hr.learning"], Ct));
        var changed = await owner.MarkAsync(captured.Item("second"), "known", Ct);
        Assert.True(changed.Result.Ok);
        var history = await context.LearningProgressRevisions.AsNoTracking().SingleAsync(Ct);
        var preimage = JsonSerializer.Deserialize(history.BeforeJson, LearningProgressJsonContext.Default.LearningProgressSnapshot)!;
        Assert.Equal(original, preimage.ProgressRaw);
        Assert.Equal(approved, preimage.ApprovedRaw);
        Assert.True((await owner.ResetAsync(changed.Snapshot!, Ct)).Result.Ok);
        var saved = await owner.ReadAsync("hr", Ct);
        Assert.Empty(saved.Learned);
        Assert.Equal(["first:abc", "z:hash"], saved.Approved.Order(StringComparer.Ordinal));
        Assert.Equal(2, saved.Item("second").Revision);
        Assert.False((await owner.MarkAsync(changed.Snapshot!.Item("second"), "again", Ct)).Result.Ok);
    }

    [Fact]
    public async Task Noop_does_not_revise_or_create_notifications()
    {
        var owner = await OwnerAsync();
        var original = await owner.ReadAsync("hr", Ct);
        Assert.True((await owner.ResetAsync(original, Ct)).Result.Ok);
        Assert.True((await owner.ApproveAsync(original.Item("first"), false, Ct)).Result.Ok);
        var changed = await owner.MarkAsync(original.Item("first"), "known", Ct);
        Assert.True((await owner.MarkAsync(changed.Snapshot!.Item("first"), "known", Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.LearningProgressRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_history_notice_and_native_admission_commit_or_roll_back_together(bool fail)
    {
        var fault = new AdmissionFault { Active = fail };
        var owner = await OwnerAsync(fault);
        var original = await owner.ReadAsync("hr", Ct);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => owner.MarkAsync(original.Item("first"), "known", Ct));
        else Assert.True((await owner.MarkAsync(original.Item("first"), "known", Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(fail ? 0 : 3, await context.Settings.CountAsync(row => row.Key.StartsWith("hr."), Ct));
        Assert.Equal(fail ? 0 : 1, await context.LearningProgressRevisions.CountAsync(Ct));
        Assert.Equal(fail ? 0 : 1, await context.Notifications.CountAsync(Ct));
        Assert.Equal(fail ? 0 : 1, await context.NotificationDeliveryRequests.CountAsync(Ct));
        if (fail)
        {
            fault.Active = false;
            Assert.True((await owner.MarkAsync(original.Item("first"), "known", Ct)).Result.Ok);
        }
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData("hr.progress", "{")]
    [InlineData("hr.progress", "null")]
    [InlineData("hr.progress", "{\"first\":\"future\"}")]
    [InlineData("hr.progress", "{\"first\":null}")]
    [InlineData("hr.approved", "bad key")]
    [InlineData("hr.learning", "null")]
    [InlineData("hr.learning", "{}")]
    [InlineData("hr.learning", "{\"version\":2,\"revision\":1}")]
    public async Task Damaged_or_future_storage_fails_closed_without_erasing_legacy_data(string key, string value)
    {
        await SeedAsync(key, value);
        var owner = await OwnerAsync();
        await Assert.ThrowsAsync<UnsupportedLearningProgressException>(() => owner.ReadAsync("hr", Ct));
        await Assert.ThrowsAsync<UnsupportedLearningProgressException>(() => owner.MarkAsync(new() { Scope = "hr", Key = "first" }, "known", Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(value, (await context.Settings.SingleAsync(row => row.Key == key, Ct)).Value);
        Assert.Empty(await context.LearningProgressRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("metadata")]
    [InlineData("history")]
    [InlineData("history-preimage")]
    public async Task Retained_native_history_and_hashes_refuse_missing_or_modified_evidence(string damage)
    {
        var owner = await OwnerAsync();
        Assert.True((await owner.MarkAsync((await owner.ReadAsync("hr", Ct)).Item("first"), "known", Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (damage == "progress") await SeedAsync("hr.progress", "{}");
        if (damage == "metadata") await context.Settings.Where(row => row.Key == "hr.learning").ExecuteDeleteAsync(Ct);
        if (damage == "history") await context.LearningProgressRevisions.ExecuteDeleteAsync(Ct);
        if (damage == "history-preimage") await context.LearningProgressRevisions.ExecuteUpdateAsync(update => update.SetProperty(row => row.AfterJson, "{}"), Ct);
        await Assert.ThrowsAsync<UnsupportedLearningProgressException>(() => owner.ReadAsync("hr", Ct));
    }

    [Theory]
    [InlineData("hr.progress")]
    [InlineData("hr.approved")]
    [InlineData("hr.learning")]
    [InlineData("interview.5.progress")]
    [InlineData("interview.5.approved")]
    [InlineData("interview.broken.learning")]
    public async Task Generic_settings_writer_and_remover_refuse_owned_namespaces(string key)
    {
        await OwnerAsync();
        var settings = _host!.Services.GetRequiredService<RuntimeSettings>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(key, "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(key, Ct));
    }

    [Theory]
    [InlineData("interview.0", "first", "known")]
    [InlineData("interview.01", "first", "known")]
    [InlineData("interview.1.progress", "first", "known")]
    [InlineData("other", "first", "known")]
    [InlineData("hr", "bad key", "known")]
    [InlineData("hr", "first", "future")]
    public async Task Invalid_commands_refuse_without_writing(string scope, string key, string state)
    {
        var owner = await OwnerAsync();
        Assert.False((await owner.MarkAsync(new() { Scope = scope, Key = key }, state, Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.LearningProgressRevisions.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Joined_progress_does_not_commit_its_parent_and_rollback_preserves_original_witness(bool commit)
    {
        var owner = await OwnerAsync();
        var expected = (await owner.ReadAsync("interview.7", Ct)).Item("first");
        await using var scope = _host!.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        var notifier = _host.Services.GetRequiredService<Notifier>();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.PublishJoinedAsync(context, expected, "known", null, null, Ct));
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        outbox.Enroll(context);
        context.Settings.Add(new() { Key = "synthetic.learning.parent", Value = "parent", UpdatedAt = DateTimeOffset.UtcNow });
        Assert.True((await owner.PublishJoinedAsync(context, expected, "known", null, null, Ct)).Result.Ok);
        notifier.Add(context, NotificationKind.Info, "Synthetic parent publication");
        await using (var observer = await pg.Db.CreateDbContextAsync(Ct)) Assert.Empty(await observer.LearningProgressRevisions.ToListAsync(Ct));
        if (commit) await notifier.SaveAndFlushAsync(context, outbox, Ct);
        else
        {
            await notifier.SaveJoinedAsync(context, outbox, Ct);
            await transaction.RollbackAsync(Ct);
        }
        await using var saved = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(commit ? 1 : 0, await saved.LearningProgressRevisions.CountAsync(Ct));
        Assert.Equal(commit ? 1 : 0, await saved.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(commit, await saved.Settings.AnyAsync(row => row.Key == "synthetic.learning.parent", Ct));
        if (!commit) Assert.True((await owner.MarkAsync(expected, "again", Ct)).Result.Ok);
    }

    [Theory]
    [InlineData("mark")]
    [InlineData("approval")]
    [InlineData("reset")]
    public async Task Editor_retains_displayed_witness_and_draft_until_explicit_reload_and_separate_retry(string change)
    {
        var owner = await OwnerAsync();
        Assert.True((await owner.MarkAsync((await owner.ReadAsync("hr", Ct)).Item("first"), "again", Ct)).Result.Ok);
        var editor = new LearningProgressEditor(new(owner), "hr");
        await editor.LoadAsync(Ct);
        var displayed = editor.Snapshot!;
        Assert.True((await owner.MarkAsync(displayed.Item("first"), "known", Ct)).Result.Ok);
        Assert.False(change == "reset" ? await editor.ResetAsync(Ct) : change == "approval" ? await editor.ApproveAsync("first", true, Ct) : await editor.MarkAsync("first", "again", Ct));
        Assert.Same(displayed, editor.Snapshot);
        var draft = editor.Draft!;
        await editor.LoadAsync(Ct);
        Assert.Same(displayed, editor.Snapshot);
        Assert.False(await editor.MarkAsync("second", "known", Ct));
        Assert.Same(draft, editor.Draft);
        Assert.False(await editor.RetryAsync(Ct));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LearningProgressActions>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Editor"] = editor }))).ToHtmlString());
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains(change == "reset" ? "сброс прогресса" : "first", System.Net.WebUtility.HtmlDecode(html));
        await editor.ReloadAsync(Ct);
        Assert.False(editor.Conflict);
        Assert.NotNull(editor.Draft);
        Assert.Equal("known", (await owner.ReadAsync("hr", Ct)).Learned["first"]);
        Assert.True(await editor.RetryAsync(Ct));
        Assert.Null(editor.Draft);
        var saved = await owner.ReadAsync("hr", Ct);
        if (change == "reset") Assert.Empty(saved.Learned);
        else if (change == "approval") Assert.Contains("first", saved.Approved);
        else Assert.Equal("again", saved.Learned["first"]);
    }

    [Fact]
    public async Task Editor_keeps_draft_on_admission_failure_and_bad_explicit_reload()
    {
        var fault = new AdmissionFault();
        var owner = await OwnerAsync(fault);
        var editor = new LearningProgressEditor(new(owner), "hr");
        await editor.LoadAsync(Ct);
        var displayed = editor.Snapshot;
        fault.Active = true;
        Assert.False(await editor.MarkAsync("first", "known", Ct));
        var draft = editor.Draft;
        Assert.Same(displayed, editor.Snapshot);
        await SeedAsync("hr.progress", "{");
        await editor.ReloadAsync(Ct);
        Assert.True(editor.Conflict);
        Assert.Same(displayed, editor.Snapshot);
        Assert.Same(draft, editor.Draft);
        await SeedAsync("hr.progress", "{}");
        fault.Active = false;
        await editor.ReloadAsync(Ct);
        Assert.True(await editor.RetryAsync(Ct));
    }

    [Fact]
    public async Task Joined_owner_reloads_unchanged_tracked_rows_and_never_accepts_a_stale_item()
    {
        var owner = await OwnerAsync();
        var first = await owner.MarkAsync((await owner.ReadAsync("hr", Ct)).Item("first"), "again", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith("hr.")).LoadAsync(Ct);
        Assert.True((await owner.MarkAsync(first.Snapshot!.Item("first"), "known", Ct)).Result.Ok);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        Assert.False((await owner.PublishJoinedAsync(context, first.Snapshot.Item("first"), "again", null, null, Ct)).Result.Ok);
        await transaction.RollbackAsync(Ct);
        Assert.Equal("known", (await owner.ReadAsync("hr", Ct)).Learned["first"]);
    }

    [Fact]
    public async Task Capture_is_frozen_before_waiting_for_lock_and_recreated_owner_reads_committed_history()
    {
        var owner = await OwnerAsync();
        var expected = (await owner.ReadAsync("hr", Ct)).Item("first");
        await using var blocker = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await blocker.Database.BeginTransactionAsync(Ct);
        await blocker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('jobfinder.learning:hr',0))", Ct);
        var pending = owner.MarkAsync(expected, "known", Ct);
        expected.Key = "second";
        await transaction.CommitAsync(Ct);
        Assert.True((await pending).Result.Ok);
        var recreated = new LearningProgressSources(pg.Db, _host!.Services.GetRequiredService<IServiceScopeFactory>(), _host.Services.GetRequiredService<Notifier>(), TimeProvider.System);
        var saved = await recreated.ReadAsync("hr", Ct);
        Assert.Equal("known", saved.Learned["first"]);
        Assert.DoesNotContain("second", saved.Learned.Keys);
    }

    [Fact]
    public async Task Invalid_reset_shapes_and_parent_owned_row_mutations_refuse_without_overwriting()
    {
        var owner = await OwnerAsync();
        Assert.False((await owner.ResetAsync(new() { Scope = "hr", Learned = null! }, Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        context.Settings.Add(new() { Key = "hr.progress", Value = "{}", UpdatedAt = DateTimeOffset.UtcNow });
        Assert.False((await owner.PublishJoinedAsync(context, new() { Scope = "hr", Key = "first" }, "known", null, null, Ct)).Result.Ok);
        Assert.Empty(await context.LearningProgressRevisions.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rendered_learning_deck_only_advances_after_accepted_publication(bool accepted)
    {
        var item = new PrepItem { Q = new() { Id = "first", Question = "Synthetic question" }, A = new() { Start = "Synthetic answer" } };
        var capture = new DeckCapture();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<LearnDeck>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Items"] = new List<PrepItem> { item }, ["Learned"] = new Dictionary<string, string>(),
                ["OnMark"] = new Func<(PrepItem Item, string State), Task<bool>>(_ => Task.FromResult(accepted)),
            }));
            await (Task)typeof(LearnDeck).GetMethod("Mark", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(capture.Deck, [item, "known"])!;
            var deck = (List<PrepItem>)typeof(LearnDeck).GetField("_deck", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture.Deck)!;
            Assert.Equal(accepted ? 0 : 1, deck.Count);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(capture.Deck, null);
            if (accepted) Assert.DoesNotContain("Synthetic question", rendered.ToHtmlString());
            else Assert.Contains("Synthetic question", rendered.ToHtmlString());
        });
    }

    [Fact]
    public async Task Migration_preserves_legacy_settings_and_downgrade_refuses_retained_ownership()
    {
        await SeedAsync("hr.progress", "{\"first\":\"known\"}");
        await SeedAsync("hr.approved", "first:abc");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var migrator = context.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync("20261004010312_DurableVacancyPreview", Ct);
            Assert.Equal("first:abc", (await context.Settings.SingleAsync(row => row.Key == "hr.approved", Ct)).Value);
            await context.Database.MigrateAsync(Ct);
            Assert.False(context.Database.HasPendingModelChanges());
            await SeedAsync("hr.learning", "{\"version\":99}");
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync("20261004010312_DurableVacancyPreview", Ct));
            Assert.Contains("Cannot downgrade retained learning progress", error.MessageText);
        }
        finally { await context.Database.MigrateAsync(Ct); }
    }

    private sealed class DeckCapture : IComponentActivator
    {
        public LearnDeck Deck { get; } = new();
        public IComponent CreateInstance(Type type) => type == typeof(LearnDeck) ? Deck : (IComponent)Activator.CreateInstance(type)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rehearsal_review_uses_its_original_learning_witness_and_preserves_a_concurrent_manual_mark(bool race)
    {
        using var http = new ReviewHttp();
        var owner = await OwnerAsync(configure: services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(http);
            services.AddHostedService<SingleInstanceGuard>();
        });
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var turns = new List<MockTurn>
        {
            new() { Question = "Synthetic first question", Ideal = "Synthetic prepared answer", ItemKey = InterviewProgress.ItemKey("hr", "first") },
            new() { Question = "Synthetic second question", Ideal = "Synthetic second answer" },
        };
        var session = new MockSession { TurnsJson = ConversationStore.Serialize(turns), Status = MockStatus.Active, CreatedAt = DateTimeOffset.UtcNow };
        context.MockSessions.Add(session);
        await context.SaveChangesAsync(Ct);
        if (race) http.DuringReview = async () => Assert.True((await owner.MarkAsync((await owner.ReadAsync("hr", Ct)).Item("first"), "again", Ct)).Result.Ok);
        var dialogue = new ConversationState(MockInterviews.Mode, session.Id, 0, turns[0].Question, [], null) { Id = Guid.NewGuid() };
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        Assert.True(await conversations.TryStartAsync(dialogue, Ct));
        var reply = await _host.Services.GetRequiredService<MockInterviews>().AnswerAsync(dialogue, "Synthetic spoken answer", Ct);
        var request = (await conversations.GetAsync(Ct))!.MockRequestId!.Value;
        var workflow = _host.Services.GetRequiredService<MockPreparationWorkflow>();
        await workflow.PrepareAsync(new() { RequestId = request }, Guid.NewGuid(), Ct);
        await workflow.PublishAsync(new() { RequestId = request }, Guid.NewGuid(), Ct);
        var saved = await owner.ReadAsync("hr", Ct);
        Assert.Equal(race ? "again" : "known", saved.Learned["first"]);
        Assert.Equal(1, saved.Revision);
        await context.Entry(session).ReloadAsync(Ct);
        var reviewed = ConversationStore.Turns(session.TurnsJson)[0];
        Assert.Equal(4, reviewed.Score);
        Assert.Equal("Synthetic spoken answer", reviewed.Answer);
        Assert.Equal(race, reviewed.Feedback!.Contains("не перезаписана", StringComparison.Ordinal));
        Assert.Contains("Ответ сохранён", reply.Text);
        Assert.Equal(race, (await context.Notifications.AsNoTracking().ToListAsync(Ct)).Any(notice => notice.Text.Contains("не перезаписана", StringComparison.Ordinal)));
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public void Missing_json_fields_keep_learning_defaults_but_do_not_invent_a_native_metadata_version()
    {
        var snapshot = JsonSerializer.Deserialize("{}", LearningProgressJsonContext.Default.LearningProgressSnapshot)!;
        Assert.Empty(snapshot.Learned);
        Assert.Empty(snapshot.Approved);
        Assert.Empty(snapshot.ItemRevisions);
        Assert.Equal(0, snapshot.Revision);
        Assert.Equal(0, JsonSerializer.Deserialize("{}", LearningProgressJsonContext.Default.LearningProgressMetadata)!.Version);
        Assert.Equal(0, JsonSerializer.Deserialize("{}", LearningProgressJsonContext.Default.LearningItemSnapshot)!.Revision);
    }

    [Fact]
    public async Task Downgrade_refuses_retained_history_even_when_ownership_marker_is_missing()
    {
        var owner = await OwnerAsync();
        Assert.True((await owner.MarkAsync((await owner.ReadAsync("hr", Ct)).Item("first"), "known", Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Settings.Where(row => row.Key == "hr.learning").ExecuteDeleteAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004010312_DurableVacancyPreview", Ct));
        Assert.Contains("Cannot downgrade retained learning progress", error.MessageText);
        Assert.Single(await context.LearningProgressRevisions.ToListAsync(Ct));
    }

    private sealed class ReviewHttp : HttpMessageHandler, IHttpClientFactory
    {
        public Func<Task>? DuringReview { get; set; }
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (DuringReview is not null) await DuringReview();
            var review = new MockReview { Score = 4, Good = ["Synthetic evidence"], Missing = [], Tip = "Synthetic next step" };
            var response = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject
                { ["content"] = JsonSerializer.Serialize(review, CareerJsonContext.Default.MockReview) }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 } };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context is JobFinderDbContext context && context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("synthetic learning admission failure");
            return ValueTask.FromResult(result);
        }
    }
}
