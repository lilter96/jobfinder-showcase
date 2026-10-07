using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurablePostProcessingTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.PostProcessingRequests.ExecuteDeleteAsync(Ct);
        await context.RawPosts.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.CompanyContacts.ExecuteDeleteAsync(Ct);
        await context.TelegramChannels.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_discovery, jobfinder_queues.wolverine_queue_discovery_scheduled, jobfinder_queues.wolverine_queue_scoring, jobfinder_queues.wolverine_queue_scoring_scheduled", Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Dispose(); }
        }
    }

    private async Task<IHost> HostAsync(bool consumer, Steps steps, SaveChangesInterceptor? fault = null, Action<IServiceCollection>? configure = null)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, steps, fault, configure);
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<RawPost> PostAsync(SourceKind source = SourceKind.Telegram)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var post = NewPost(source);
        context.RawPosts.Add(post);
        await context.SaveChangesAsync(Ct);
        return post;
    }

    private static RawPost NewPost(SourceKind source = SourceKind.Telegram) => new()
    {
        Source = source, ChatRef = "987654", ExternalId = Guid.NewGuid().ToString("N"), ChatTitle = "Synthetic vacancies",
        Url = "https://t.me/synthetic/101", Text = "Ищем Senior C#/.NET разработчика. Удалённо. Резюме jobs@acme.io.",
        FetchedAt = DateTimeOffset.UtcNow, Status = RawPostStatus.New,
    };

    private async Task<PostProcessingRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.PostProcessingRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<Guid> RequestAsync(IHost host, RawPost post) =>
        (await host.Services.GetRequiredService<PostProcessingRequests>().RequestAsync(post.Id, Ct))!.Value;

    private async Task WakeAsync(IHost host, Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.PostProcessingRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        var root = await RootAsync(id);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new ProcessPost { RequestId = id, Stage = root.Stage });
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("native post checkpoint did not arrive");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Duplicate_producers_and_stage_replay_create_one_vacancy_contact_and_assessment_intent()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var post = await PostAsync();
        var id = await RequestAsync(producer, post);
        Assert.Equal(id, await RequestAsync(producer, post));
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(true, steps);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new ProcessPost { RequestId = id, Stage = PostProcessingStage.Extracting });
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Vacancies.ToListAsync(Ct));
        Assert.Single(await context.CompanyContacts.ToListAsync(Ct));
        Assert.Single(await context.VacancySources.ToListAsync(Ct));
        Assert.Equal(1, steps.Reads);
        Assert.Equal(1, steps.Extractions);
        Assert.Equal(1, steps.Verifications);
        Assert.Equal(RawPostStatus.Vacancy, (await context.RawPosts.SingleAsync(Ct)).Status);
        Assert.NotNull((await context.Vacancies.SingleAsync(Ct)).AssessmentCaptureId);
    }

    [Fact]
    public async Task A_model_outage_resumes_extraction_without_recrawling_or_consuming_failure_budget()
    {
        var steps = new Steps { Unavailable = true };
        var host = await HostAsync(true, steps);
        var id = await RequestAsync(host, await PostAsync());
        await UntilAsync(async () => (await RootAsync(id)).RetryAt is not null);
        var held = await RootAsync(id);
        Assert.Equal(PostProcessingStage.Extracting, held.Stage);
        Assert.Equal(0, held.Failures);
        Assert.NotNull(held.ReadingJson);
        Assert.Null(held.ExtractionJson);
        steps.Unavailable = false;
        await WakeAsync(host, id);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        Assert.Equal(1, steps.Reads);
        Assert.Equal(2, steps.Extractions);
    }

    [Fact]
    public async Task Verification_outage_does_not_repeat_the_committed_paid_extraction()
    {
        var steps = new Steps { VerifyUnavailable = true };
        var host = await HostAsync(true, steps);
        var id = await RequestAsync(host, await PostAsync());
        await UntilAsync(async () => (await RootAsync(id)).RetryAt is not null);
        Assert.Equal(PostProcessingStage.Verifying, (await RootAsync(id)).Stage);
        steps.VerifyUnavailable = false;
        await WakeAsync(host, id);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        Assert.Equal(1, steps.Reads);
        Assert.Equal(1, steps.Extractions);
        Assert.Equal(2, steps.Verifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Negative_reading_or_model_classification_never_creates_a_vacancy(bool model)
    {
        var steps = new Steps { Skip = !model, NotVacancy = model };
        var host = await HostAsync(true, steps);
        var id = await RequestAsync(host, await PostAsync());
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Vacancies.ToListAsync(Ct));
        Assert.Equal(model ? RawPostStatus.NotVacancy : RawPostStatus.Skipped, (await context.RawPosts.SingleAsync(Ct)).Status);
        Assert.Equal(model ? 1 : 0, steps.Extractions);
        Assert.Equal(0, steps.Verifications);
    }

    [Fact]
    public async Task Source_changed_while_reading_cannot_publish_the_old_paid_command()
    {
        var steps = new Steps { HoldRead = true };
        var host = await HostAsync(true, steps);
        var post = await PostAsync();
        var id = await RequestAsync(host, post);
        await steps.Entered.Task.WaitAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.RawPosts.Where(row => row.Id == post.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Text, "Changed post"), Ct);
        steps.Release.TrySetResult();
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Cancelled);
        Assert.Equal("Changed post", (await context.RawPosts.SingleAsync(Ct)).Text);
        Assert.Null((await context.RawPosts.SingleAsync(Ct)).ProcessingRequestId);
        Assert.Equal(0, steps.Extractions);
        Assert.Empty(await context.Vacancies.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_rollback_preserves_source_counters_contacts_and_root_atomically(bool storeFault)
    {
        var steps = new Steps();
        var host = await HostAsync(true, steps, new Fault(storeFault));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.TelegramChannels.Add(new() { Username = "synthetic", Title = "Synthetic", Kind = ChannelKind.Channel, PeerId = 987654, Status = ChannelStatus.Joined, AddedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        var post = NewPost();
        if (storeFault)
        {
            await using var owned = await host.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<PostProcessingRequests>().StoreAsync(owned, post, Ct));
            await using var clean = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await clean.RawPosts.ToListAsync(Ct));
            Assert.Empty(await clean.PostProcessingRequests.ToListAsync(Ct));
            Assert.Equal(0, (await clean.TelegramChannels.SingleAsync(Ct)).PostsSeen);
            return;
        }
        context.RawPosts.Add(post);
        await context.SaveChangesAsync(Ct);
        var id = await RequestAsync(host, post);
        await UntilAsync(async () => (await RootAsync(id)).RetryAt is not null);
        Assert.Equal(PostProcessingStage.Publishing, (await RootAsync(id)).Stage);
        Assert.Empty(await context.Vacancies.ToListAsync(Ct));
        Assert.Empty(await context.CompanyContacts.ToListAsync(Ct));
        Assert.Equal(0, (await context.TelegramChannels.SingleAsync(Ct)).VacanciesFound);
        Assert.Equal(RawPostStatus.New, (await context.RawPosts.AsNoTracking().SingleAsync(Ct)).Status);
        await WakeAsync(host, id);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        Assert.Equal(1, steps.Extractions);
        Assert.Equal(1, (await context.TelegramChannels.AsNoTracking().SingleAsync(Ct)).VacanciesFound);
        Assert.Equal(1, (await context.CompanyContacts.SingleAsync(Ct)).SeenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_version_or_checkpoint_retires_once_and_enters_dead_letter(bool checkpoint)
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var id = await RequestAsync(producer, await PostAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_discovery; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes", Ct);
        if (checkpoint) await context.PostProcessingRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.InputHash, new string('a', 64)), Ct);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new ProcessPost { RequestId = id, Stage = PostProcessingStage.Reading, Version = checkpoint ? 1 : 2 });
        await producer.StopAsync(Ct);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Failed);
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%ProcessPost%'").SingleAsync(Ct) == 1);
        Assert.Equal(0, steps.Reads);
        var notice = Assert.Single(await context.Notifications.Where(row => row.Text.Contains("Обработка поста")).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_refuses_saved_roots_or_orphaned_native_envelopes(bool orphaned)
    {
        var host = await HostAsync(false, new());
        await RequestAsync(host, await PostAsync());
        await host.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (orphaned) { await context.PostProcessingRequests.ExecuteDeleteAsync(Ct); await context.RawPosts.ExecuteDeleteAsync(Ct); }
        try
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261001100738_DurableCommentDelivery", Ct));
            Assert.Contains("Cannot roll back post processing", error.MessageText);
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Bounded_importer_retires_malformed_sources_without_starving_the_healthy_post()
    {
        var steps = new Steps();
        var host = await HostAsync(true, steps);
        for (var index = 0; index < 20; index++) await PostAsync(SourceKind.Habr);
        var good = await PostAsync();
        using var worker = new RawPostWorker(pg.Db, host.Services.GetRequiredService<PostProcessingRequests>(), host.Services.GetRequiredService<JobTriggers>(), TimeProvider.System, NullLogger<RawPostWorker>.Instance);
        await worker.StartAsync(Ct);
        try
        {
            await UntilAsync(async () =>
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                return await context.RawPosts.AnyAsync(row => row.Id == good.Id && row.Status == RawPostStatus.Vacancy, Ct);
            });
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(20, await context.RawPosts.CountAsync(row => row.Status == RawPostStatus.Failed, Ct));
            Assert.Equal(1, steps.Extractions);
        }
        finally { await worker.StopAsync(Ct); }
    }

    private sealed class Steps : IPostProcessingSteps
    {
        private int _reads;
        private int _extractions;
        private int _verifications;
        public int Reads => Volatile.Read(ref _reads);
        public int Extractions => Volatile.Read(ref _extractions);
        public int Verifications => Volatile.Read(ref _verifications);
        public bool Unavailable { get; set; }
        public bool VerifyUnavailable { get; set; }
        public bool Skip { get; set; }
        public bool NotVacancy { get; set; }
        public bool HoldRead { get; set; }
        public bool HoldExtraction { get; set; }
        public bool HeadHunter { get; set; }
        public long? ExistingHhId { get; set; }
        public bool Partial { get; set; }
        public bool HoldVerification { get; set; }
        public IDbContextFactory<JobFinderDbContext>? Database { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void EnsureCompatible(PostProcessingInput input) { }
        public async Task<PostReading> ReadAsync(PostProcessingInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            if (HoldRead) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            if (HeadHunter) return new()
            {
                Post = PostWitness.Capture(input.Source), HhHost = "hh.ru", HhId = 77777, ExistingHhVacancyId = ExistingHhId,
                HhDraft = new() { Title = "Senior .NET Developer", CompanyName = "Acme", Description = "Structured hh vacancy", Url = "https://hh.ru/vacancy/77777", Fingerprint = "hh-77777", Status = VacancyStatus.New },
                HhSource = new() { Source = SourceKind.HeadHunter, ExternalId = "77777", Host = "hh.ru", Url = "https://hh.ru/vacancy/77777", FetchedAt = DateTimeOffset.UtcNow },
            };
            return new() { Post = PostWitness.Capture(input.Source), Description = input.Source.Text, TerminalStatus = Skip ? RawPostStatus.Skipped : null, Reason = Skip ? "synthetic prefilter" : null };
        }
        public async Task<PostExtractionCheckpoint> ExtractAsync(PostProcessingInput input, PostReading reading, CancellationToken ct)
        {
            Interlocked.Increment(ref _extractions);
            if (HoldExtraction) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            if (Unavailable) throw new LlmUnavailableException("synthetic extraction outage");
            return new PostExtractionCheckpoint { Complete = !Partial, Extraction = new() { IsVacancy = !NotVacancy, IsDotnet = true, Title = "Senior .NET Developer", Company = "Acme" } };
        }
        public async Task<PostVerification> VerifyAsync(PostProcessingInput input, PostReading reading, PostExtractionCheckpoint extraction, CancellationToken ct)
        {
            Interlocked.Increment(ref _verifications);
            if (VerifyUnavailable) throw new LlmUnavailableException("synthetic verification outage");
            var result = new PostVerification { Contacts = [new() { Kind = ContactKind.Email, Value = "jobs@acme.io" }], Url = "https://jobs.invalid/senior-dotnet" };
            if (input.Refresh && Database is not null)
            {
                await using var context = await Database.CreateDbContextAsync(ct);
                var contacts = await context.Vacancies.Where(row => row.Id == input.Source.VacancyId).Select(row => row.Contacts).SingleAsync(ct);
                result.StoredContactsHash = PostWitness.ContactHash(contacts);
                result.StoredContacts = [.. contacts];
                if (HoldVerification && Verifications == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            }
            return result;
        }
    }

    private sealed class Fault(bool store) : SaveChangesInterceptor
    {
        private int _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PostProcessingRequest>().Any(entry => store ? entry.State == EntityState.Added
                : entry.State == EntityState.Modified && entry.Entity.Stage == PostProcessingStage.Completed)
                && Interlocked.Exchange(ref _failed, 1) == 0) throw new InvalidOperationException("synthetic post checkpoint fault");
            return ValueTask.FromResult(result);
        }
    }
}
