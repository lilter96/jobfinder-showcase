using System.Reflection;
using System.Text.Json;
using System.Net;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
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
using StoryPage = JobFinder.App.Components.Pages.Stories;

namespace JobFinder.Tests.Pipeline;

public sealed class CandidateStoryTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;
    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.StoryRevisions.ExecuteDeleteAsync(Ct);
        await context.StoryPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.Stories.IgnoreQueryFilters().ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == ConversationStore.Key || row.Key == StorySources.SkippedKey).ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE 'jobfinder.candidate.%'; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.candidate.%'; DELETE FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE 'jobfinder.candidate.%'", Ct);
    }
    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }
    private async Task<StorySources> OwnerAsync(SaveChangesInterceptor? fault = null, StoryProvider? provider = null, bool owned = false)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault, configure: services =>
        {
            if (owned) services.AddHostedService<SingleInstanceGuard>();
            if (provider is null) return;
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(provider);
        });
        await _host.StartAsync(Ct);
        return _host.Services.GetRequiredService<StorySources>();
    }
    private static StoryContent Content() => new() { Title = "Synthetic evidence", Action = "Built a synthetic service", Result = "Measured 150 ms",
        Skills = [" C# ", "c#", ""], Confirmed = true, Source = StorySource.Resume, RawNotes = "Captured raw notes" };
    private async Task<Story> ReadAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Stories.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }
    private async Task<Story> LegacyAsync()
    {
        var content = Content();
        var source = new Story { Title = content.Title, Action = content.Action, Result = content.Result, Skills = ["C#"], Source = content.Source,
            RawNotes = content.RawNotes, Confirmed = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Stories.Add(source);
        await context.SaveChangesAsync(Ct);
        return await ReadAsync(source.Id);
    }
    private static ConversationState DraftState() => new(StoryBank.Mode, 0, 4, "Synthetic achievement", ["Captured answer"],
        JsonSerializer.Serialize(new StoryDraft { Title = "Synthetic bot story", Action = "Captured candidate action", Result = "Captured result", Skills = ["C#"] }, CareerJsonContext.Default.StoryDraft))
        { Id = Guid.NewGuid() };

    [Fact]
    public async Task Creation_is_idempotent_and_exact_input_history_and_notice_commit_once()
    {
        var owner = await OwnerAsync();
        var intent = Guid.NewGuid();
        var results = await Task.WhenAll(owner.CreateAsync(Content(), intent, Ct), owner.CreateAsync(Content(), intent, Ct));
        Assert.All(results, result => Assert.True(result.Result.Ok));
        Assert.Equal(results[0].StoryId, results[1].StoryId);
        Assert.False((await owner.CreateAsync(Content() with { Result = "Different result" }, intent, Ct)).Result.Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.Stories.SingleAsync(Ct);
        Assert.Equal(1, source.Revision);
        Assert.Equal(["C#"], source.Skills);
        var history = await context.StoryRevisions.SingleAsync(Ct);
        Assert.Null(history.BeforeJson);
        Assert.Equal(CanonicalJson.Hash(StorySources.Json(source)), CanonicalJson.Hash(history.AfterJson));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Edits_preserve_provenance_record_legacy_preimage_and_fence_aba_and_retirement()
    {
        var owner = await OwnerAsync();
        var source = await LegacyAsync();
        var original = StoryEditSnapshot.Capture(source);
        var content = StoryContent.From(source);
        Assert.False((await owner.SaveAsync(original, content with { RawNotes = "Fabricated provenance" }, Ct)).Ok);
        Assert.True((await owner.SaveAsync(original, content with { Result = "Updated measured result" }, Ct)).Ok);
        var updated = await ReadAsync(source.Id);
        Assert.True((await owner.SaveAsync(StoryEditSnapshot.Capture(updated), content, Ct)).Ok);
        Assert.False((await owner.SaveAsync(original, content with { Title = "Stale overwrite" }, Ct)).Ok);
        var latest = await ReadAsync(source.Id);
        Assert.True((await owner.RetireAsync(StoryEditSnapshot.Capture(latest), Ct)).Ok);
        Assert.False((await owner.SaveAsync(StoryEditSnapshot.Capture(await ReadAsync(source.Id)), content, Ct)).Ok);
        Assert.False((await owner.RetireAsync(StoryEditSnapshot.Capture(await ReadAsync(source.Id)), Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Stories.ToListAsync(Ct));
        var archived = await context.Stories.IgnoreQueryFilters().SingleAsync(Ct);
        Assert.Equal(source.RawNotes, archived.RawNotes);
        Assert.True(archived.Confirmed);
        var history = await context.StoryRevisions.OrderBy(row => row.Revision).ToListAsync(Ct);
        Assert.Equal(3, history.Count);
        Assert.Equal(CanonicalJson.Hash(StorySources.Json(source)), CanonicalJson.Hash(history[0].BeforeJson!));
        Assert.Equal(CanonicalJson.Hash(history[0].AfterJson), CanonicalJson.Hash(history[1].BeforeJson!));
        Assert.Equal(CanonicalJson.Hash(history[1].AfterJson), CanonicalJson.Hash(history[2].BeforeJson!));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("edit")]
    [InlineData("retire")]
    public async Task Native_admission_failure_rolls_back_rows_history_and_notice_with_a_retryable_witness(string operation)
    {
        var fault = new AdmissionFault { Active = true };
        var owner = await OwnerAsync(fault);
        var source = operation == "create" ? null : await LegacyAsync();
        var expected = source is null ? null : StoryEditSnapshot.Capture(source);
        var intent = Guid.NewGuid();
        async Task PublishAsync()
        {
            if (operation == "create") Assert.True((await owner.CreateAsync(Content(), intent, Ct)).Result.Ok);
            else if (operation == "edit") Assert.True((await owner.SaveAsync(expected!, StoryContent.From(source!) with { Title = "Edited title" }, Ct)).Ok);
            else Assert.True((await owner.RetireAsync(expected!, Ct)).Ok);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(PublishAsync);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.StoryRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        if (source is null) Assert.Empty(await context.Stories.ToListAsync(Ct));
        else Assert.Equal(expected, StoryEditSnapshot.Capture(await ReadAsync(source.Id)));
        fault.Active = false;
        await PublishAsync();
        Assert.Single(await context.StoryRevisions.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bot_creation_and_original_conversation_completion_are_atomic_and_cannot_clear_a_new_dialogue(bool fail)
    {
        var fault = new AdmissionFault { Active = fail };
        var owner = await OwnerAsync(fault);
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        var state = DraftState();
        await conversations.SetAsync(state, Ct);
        if (fail)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateFromConversationAsync(state, Ct));
            Assert.Equal(ConversationStore.Capture(state), ConversationStore.Capture((await conversations.GetAsync(Ct))!));
            await using var rolledBack = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await rolledBack.Stories.ToListAsync(Ct));
            Assert.Empty(await rolledBack.StoryRevisions.ToListAsync(Ct));
            Assert.Empty(await rolledBack.Notifications.ToListAsync(Ct));
            fault.Active = false;
        }
        var published = await owner.CreateFromConversationAsync(state, Ct);
        Assert.True(published.Result.Ok);
        Assert.Null(await conversations.GetAsync(Ct));
        var newer = DraftState();
        await conversations.SetAsync(newer, Ct);
        Assert.Equal(published.StoryId, (await owner.CreateFromConversationAsync(state, Ct)).StoryId);
        Assert.Equal(ConversationStore.Capture(newer), ConversationStore.Capture((await conversations.GetAsync(Ct))!));
        Assert.False(await conversations.TryStartAsync(DraftState(), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.Stories.SingleAsync(Ct);
        Assert.Equal("Captured answer", source.RawNotes);
        Assert.Equal(StorySource.Interview, source.Source);
        Assert.Equal(state.Subject, source.Achievement);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("draft")]
    [InlineData("dialogue")]
    public async Task Stale_or_unversioned_story_controls_never_act_on_the_current_draft(string change)
    {
        await OwnerAsync();
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        var bank = _host.Services.GetRequiredService<StoryBank>();
        var original = DraftState();
        var newer = change == "dialogue" ? DraftState() : original with { Draft = original.Draft!.Replace("Synthetic bot story", "Newer bot story", StringComparison.Ordinal) };
        await conversations.SetAsync(newer, Ct);
        var id = change == "legacy" ? 0 : StoryBank.ControlId(original);
        var version = change == "legacy" ? 0 : StoryBank.Version(original);
        foreach (var action in new Func<Task<BotReply>>[] { () => bank.SaveAsync(id, version, Ct), () => bank.RedoAsync(id, version, Ct),
            () => bank.SkipAsync(id, version, Ct), () => bank.StopAsync(id, version, Ct) })
            Assert.Contains("изменился", (await action()).Text);
        Assert.Equal(ConversationStore.Capture(newer), ConversationStore.Capture((await conversations.GetAsync(Ct))!));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Stories.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Skip_and_stop_join_the_original_dialogue_cooldown_and_native_notice_or_preserve_them(bool skip, bool fail)
    {
        var fault = new AdmissionFault { Active = fail };
        var owner = await OwnerAsync(fault);
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        var state = DraftState();
        await conversations.SetAsync(state, Ct);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CloseConversationAsync(state, skip, Ct));
        else Assert.True((await owner.CloseConversationAsync(state, skip, Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var skipped = await context.Settings.SingleOrDefaultAsync(row => row.Key == StorySources.SkippedKey, Ct);
        if (skip && !fail) Assert.Contains(state.Subject!, ConversationStore.Strings(skipped!.Value));
        else Assert.Null(skipped);
        if (fail)
        {
            Assert.Equal(ConversationStore.Capture(state), ConversationStore.Capture((await conversations.GetAsync(Ct))!));
            Assert.Empty(await context.Notifications.ToListAsync(Ct));
        }
        else
        {
            Assert.Null(await conversations.GetAsync(Ct));
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        }
    }

    [Fact]
    public async Task Retirement_removes_future_candidate_evidence_and_reopens_the_achievement_without_erasing_history()
    {
        var owner = await OwnerAsync();
        await (await pg.CandidateAsync()).PublishProfileAsync(new CandidateProfile { Name = "Synthetic candidate", Achievements = ["Synthetic achievement"] }, 0, "story-test", Ct);
        var content = Content() with { Achievement = "Synthetic achievement" };
        var publication = await owner.CreateAsync(content, Guid.NewGuid(), Ct);
        var source = await ReadAsync(publication.StoryId!.Value);
        var bank = _host!.Services.GetRequiredService<StoryBank>();
        Assert.Empty(await bank.PendingAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var before = await ReplyDraftSources.StoriesAsync(context, Ct);
        Assert.Contains("150 ms", before);
        Assert.True((await owner.RetireAsync(StoryEditSnapshot.Capture(source), Ct)).Ok);
        Assert.DoesNotContain("150 ms", await ReplyDraftSources.StoriesAsync(context, Ct));
        Assert.Equal(["Synthetic achievement"], await bank.PendingAsync(Ct));
        Assert.Single(await context.Stories.IgnoreQueryFilters().ToListAsync(Ct));
        Assert.Equal(2, await context.StoryRevisions.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_editor_retains_typed_fields_and_the_displayed_witness_through_conflict_or_failure(bool fail)
    {
        var fault = new AdmissionFault();
        var owner = await OwnerAsync(fault);
        var source = await LegacyAsync();
        var capture = new PageCapture();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture).AddSingleton(pg.Db)
            .AddSingleton(owner).AddSingleton(_host!.Services.GetRequiredService<StoryBank>()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<StoryPage>(ParameterView.Empty);
            Call(capture.Page, "Edit", source);
            var expected = (StoryEditSnapshot)Field(capture.Page, "_expected");
            ((StoryContent)Field(capture.Page, "_editing")).Title = "My unsaved story title";
            typeof(StoryPage).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(capture.Page, "C#, PostgreSQL");
            if (fail) fault.Active = true;
            else Assert.True((await owner.SaveAsync(expected, StoryContent.From(source) with { Title = "Concurrent title" }, Ct)).Ok);
            await (Task)Call(capture.Page, "Save")!;
            Assert.Equal("My unsaved story title", ((StoryContent)Field(capture.Page, "_editing")).Title);
            Assert.Equal(expected, Field(capture.Page, "_expected"));
            Assert.Equal("C#, PostgreSQL", Field(capture.Page, "_skills"));
            await (Task)Call(capture.Page, "LoadAsync")!;
            Assert.Equal(expected, Field(capture.Page, "_expected"));
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(capture.Page, null);
            Assert.Contains("My unsaved story title", rendered.ToHtmlString());
            fault.Active = false;
            await (Task)Call(capture.Page, "ReloadEdit")!;
            Assert.NotEqual("My unsaved story title", ((StoryContent)Field(capture.Page, "_editing")).Title);
            ((StoryContent)Field(capture.Page, "_editing")).Title = "Explicit updated title";
            await (Task)Call(capture.Page, "Save")!;
            Assert.Equal("Explicit updated title", (await ReadAsync(source.Id)).Title);
        });
    }

    [Fact]
    public async Task Migration_preserves_legacy_story_and_retains_original_preimage_then_refuses_destructive_downgrade()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.GetService<IMigrator>().MigrateAsync("20261003215725_CompanyPreferenceRevision", Ct);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO stories (title, situation, task, action, result, reflection, skills, source, confirmed, created_at, updated_at)
            VALUES ('Synthetic legacy story', '', '', 'Legacy action', 'Legacy measured result', '', '{{}}', 'Resume', true, now(), now())
            """, Ct);
        var before = await context.Database.SqlQueryRaw<string>("SELECT to_jsonb(story)::text AS \"Value\" FROM stories story").SingleAsync(Ct);
        await context.Database.MigrateAsync(Ct);
        var after = await context.Database.SqlQueryRaw<string>("SELECT (to_jsonb(story) - 'revision' - 'retired_at' - 'creation_key' - 'creation_hash' - 'preparation_request_id')::text AS \"Value\" FROM stories story").SingleAsync(Ct);
        Assert.Equal(before, after);
        var source = await context.Stories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(0, source.Revision);
        var owner = await OwnerAsync();
        Assert.True((await owner.RetireAsync(StoryEditSnapshot.Capture(source), Ct)).Ok);
        var history = await context.StoryRevisions.SingleAsync(Ct);
        Assert.Equal(CanonicalJson.Hash(StorySources.Json(source)), CanonicalJson.Hash(history.BeforeJson!));
        try
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003215725_CompanyPreferenceRevision", Ct));
            Assert.Contains("Cannot downgrade retained candidate story history", error.MessageText);
        }
        finally { await context.Database.MigrateAsync(Ct); }
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Current_story_save_control_uses_the_owner_and_never_duplicates_a_completed_creation()
    {
        await OwnerAsync();
        var state = DraftState();
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        await conversations.SetAsync(state, Ct);
        var bank = _host.Services.GetRequiredService<StoryBank>();
        var reply = await bank.SaveAsync(StoryBank.ControlId(state), StoryBank.Version(state), Ct);
        Assert.Contains("История сохранена", reply.Text);
        Assert.Contains("изменился", (await bank.SaveAsync(StoryBank.ControlId(state), StoryBank.Version(state), Ct)).Text);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Stories.ToListAsync(Ct));
        Assert.Single(await context.StoryRevisions.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Story_composition_fences_before_and_after_paid_work_without_erasing_a_newer_dialogue(bool duringPaid)
    {
        using var provider = new StoryProvider();
        await OwnerAsync(provider: provider, owned: true);
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        var bank = _host.Services.GetRequiredService<StoryBank>();
        var original = DraftState();
        var newer = DraftState();
        await conversations.SetAsync(original, Ct);
        if (duringPaid) provider.BeforeReturn = () => conversations.SetAsync(newer, Ct);
        else await conversations.SetAsync(newer, Ct);
        var reply = await bank.ComposeAsync(original, Ct);
        if (duringPaid)
        {
            Assert.Empty(reply.Text);
            var waiting = (await conversations.GetAsync(Ct))!;
            await _host.Services.GetRequiredService<StoryPreparationWorkflow>().DraftAsync(new() { RequestId = waiting.StoryRequestId!.Value }, Guid.NewGuid(), Ct);
        }
        else Assert.Contains("изменился", reply.Text);
        Assert.Equal(duringPaid ? 1 : 0, provider.Calls);
        Assert.Equal(ConversationStore.Capture(newer), ConversationStore.Capture((await conversations.GetAsync(Ct))!));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Stories.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.Where(row => row.Text.Contains("<b>S</b>")).ToListAsync(Ct));
    }

    [Fact]
    public async Task A_paid_story_draft_persists_the_exact_version_bound_buttons_without_saving_unconfirmed_evidence()
    {
        using var provider = new StoryProvider();
        await OwnerAsync(provider: provider, owned: true);
        var conversations = _host!.Services.GetRequiredService<ConversationStore>();
        var state = DraftState() with { Draft = null };
        await conversations.SetAsync(state, Ct);
        var bank = _host.Services.GetRequiredService<StoryBank>();
        var reply = await bank.ComposeAsync(state, Ct);
        Assert.Empty(reply.Text);
        var waiting = (await conversations.GetAsync(Ct))!;
        var workflow = _host.Services.GetRequiredService<StoryPreparationWorkflow>();
        await workflow.DraftAsync(new() { RequestId = waiting.StoryRequestId!.Value }, Guid.NewGuid(), Ct);
        await workflow.PublishAsync(new() { RequestId = waiting.StoryRequestId.Value }, Guid.NewGuid(), Ct);
        var ready = (await conversations.GetAsync(Ct))!;
        Assert.NotNull(ready.Draft);
        Assert.NotEqual(StoryBank.Version(state), StoryBank.Version(ready));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var card = await context.Notifications.SingleAsync(row => row.Text.Contains("<b>S</b>"), Ct);
        Assert.Contains(Callback.Button("✅ Сохранить", CareerCallback.StorySave, StoryBank.ControlId(ready), StoryBank.Version(ready)), card.Buttons.SelectMany(row => row));
        Assert.Single(card.Buttons.SelectMany(row => row), button => button.Contains(CareerCallback.StorySave, StringComparison.Ordinal));
        Assert.Empty(await context.Stories.ToListAsync(Ct));
    }

    [Fact]
    public async Task Retirement_during_editing_keeps_the_unsaved_text_visible_and_does_not_revive_the_archived_source()
    {
        var owner = await OwnerAsync();
        var source = await LegacyAsync();
        var capture = new PageCapture();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture).AddSingleton(pg.Db)
            .AddSingleton(owner).AddSingleton(_host!.Services.GetRequiredService<StoryBank>()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<StoryPage>(ParameterView.Empty);
            Call(capture.Page, "Edit", source);
            ((StoryContent)Field(capture.Page, "_editing")).Title = "Unsaved archived story edit";
            Assert.True((await owner.RetireAsync(StoryEditSnapshot.Capture(source), Ct)).Ok);
            await (Task)Call(capture.Page, "LoadAsync")!;
            await (Task)Call(capture.Page, "Save")!;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(capture.Page, null);
            Assert.Contains("Unsaved archived story edit", rendered.ToHtmlString());
            Assert.NotNull((await ReadAsync(source.Id)).RetiredAt);
            Assert.Equal(source.Title, (await ReadAsync(source.Id)).Title);
        });
    }

    private static object Field(StoryPage page, string field) => typeof(StoryPage).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
    private static object? Call(StoryPage page, string method, params object[] args) => typeof(StoryPage).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, args);
    private sealed class PageCapture : IComponentActivator
    {
        public StoryPage Page { get; } = new();
        public IComponent CreateInstance(Type type) => type == typeof(StoryPage) ? Page : (IComponent)Activator.CreateInstance(type)!;
    }
    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && data.Context is JobFinderDbContext context && context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("synthetic story native admission failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StoryProvider : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public Func<Task>? BeforeReturn { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (BeforeReturn is { } callback) { BeforeReturn = null; await callback(); }
            var draft = JsonSerializer.Serialize(new StoryDraft { Title = "Synthetic provider story", Action = "Captured action", Result = "Captured result", Skills = ["C#"] }, CareerJsonContext.Default.StoryDraft);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { model = "synthetic/story", choices = new[] { new { message = new { content = draft }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 10, completion_tokens = 20, cost = 0.0001m } }), Encoding.UTF8, "application/json") };
        }
    }
}
