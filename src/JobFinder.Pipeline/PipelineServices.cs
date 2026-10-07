using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Browser;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;
using JobFinder.Integrations.LinkedIn;
using JobFinder.Integrations.Speech;
using JobFinder.Integrations.Telegram;
using JobFinder.Integrations.Threads;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Llm;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Telegram;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Pipeline;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    public string ConnectionString { get; set; } = "";
}

public static class PipelineServices
{
    /// <summary>
    /// Every service except the background loops: the CLI commands and the dashboard use the same graph as the pipeline.
    /// Relative paths in the options resolve against the current directory, which Program sets to the JobFinder home.
    /// </summary>
    public static IServiceCollection AddJobFinderCore(this IServiceCollection services, IConfiguration config)
    {
        Bind<DatabaseOptions>(services, config, DatabaseOptions.Section);
        Bind<PathsOptions>(services, config, PathsOptions.Section);
        Bind<FilterOptions>(services, config, FilterOptions.Section);
        Bind<SalaryOptions>(services, config, SalaryOptions.Section);
        Bind<DecisionOptions>(services, config, DecisionOptions.Section);
        Bind<PrefilterOptions>(services, config, PrefilterOptions.Section);
        Bind<LlmOptions>(services, config, LlmOptions.Section);
        Bind<ScrapeOptions>(services, config, ScrapeOptions.Section);
        Bind<HhOptions>(services, config, HhOptions.Section);
        Bind<HabrOptions>(services, config, HabrOptions.Section);
        Bind<TelegramOptions>(services, config, TelegramOptions.Section);
        Bind<ThreadsOptions>(services, config, ThreadsOptions.Section);
        Bind<BrowserOptions>(services, config, BrowserOptions.Section);
        Bind<EmailOptions>(services, config, EmailOptions.Section);
        Bind<DigestOptions>(services, config, DigestOptions.Section);
        Bind<CareerOptions>(services, config, CareerOptions.Section);
        Bind<LinkedInOptions>(services, config, LinkedInOptions.Section);
        Bind<JobFinder.Integrations.Web.EnrichmentOptions>(services, config, JobFinder.Integrations.Web.EnrichmentOptions.Section);
        Bind<SttOptions>(services, config, SttOptions.Section);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new LocalClock(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<DecisionOptions>().TimeZone));
        services.AddDbContextFactory<JobFinderDbContext>((sp, options) => JobFinderDbContext.Configure(options, sp.GetRequiredService<DatabaseOptions>().ConnectionString));

        // LLM: one named client per route (direct / proxy), the model breakers are process-wide.
        services.AddHttpClient(OpenRouterTransport.DirectClient).ConfigurePrimaryHttpMessageHandler(() => LlmHttp.CreateHandler(null))
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient(OpenRouterTransport.ProxyClient).ConfigurePrimaryHttpMessageHandler(sp => LlmHttp.CreateHandler(sp.GetRequiredService<LlmOptions>().Proxy))
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<OpenRouterTransport>();
        services.AddSingleton<ModelHealth>();
        services.AddSingleton<ILlmUsageSink, DbLlmUsageSink>();
        services.AddSingleton<PgPolicyStore>();
        services.AddSingleton<LlmPolicyStore>();
        services.AddSingleton<Modules.Discovery.SearchPolicyStore>();
        services.AddSingleton<Modules.Engagement.EngagementPolicyStore>();
        services.AddSingleton<JobFinder.Llm.Configuration.ILlmPolicySource>(provider => provider.GetRequiredService<LlmPolicyStore>());
        services.AddSingleton<LlmClient>();
        services.AddSingleton(sp => new PromptCatalog(Path.GetFullPath(sp.GetRequiredService<PathsOptions>().Prompts)));
        services.AddSingleton<JobFinderLlm>();

        // Scraping keeps one cookie jar for the process: sites set their anti-bot cookies on the first hit. A block drops the site's cookies
        // and pauses it, and the pause is kept in the settings table so a restart does not hit the blocked site at once.
        services.AddSingleton<IScrapeStateStore, Settings.ScrapeStateStore>();
        services.AddSingleton(sp =>
        {
            var scrape = sp.GetRequiredService<ScrapeOptions>();
            var cookies = new System.Net.CookieContainer();
            HttpClient Client(string? proxy)
            {
                var client = new HttpClient(PoliteHttp.CreateHandler(cookies, proxy)) { Timeout = Timeout.InfiniteTimeSpan };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", scrape.UserAgent);
                return client;
            }

            // The proxy route exists only when configured; a malformed address fails the start here.
            return new PoliteHttp(Client(null), scrape, sp.GetRequiredService<TimeProvider>(), cookies, sp.GetRequiredService<IScrapeStateStore>(),
                string.IsNullOrWhiteSpace(scrape.Proxy) ? null : Client(scrape.Proxy));
        });
        services.AddSingleton<HhClient>();
        // Reading what a vacancy post links to: pure parsing and link rules, the crawler over PoliteHttp, the browser for script-built pages.
        services.AddSingleton<JobFinder.Integrations.Web.LinkPolicy>();
        services.AddSingleton<JobFinder.Integrations.Web.PageReader>();
        services.AddSingleton<JobFinder.Integrations.Web.IPageRenderer, JobFinder.Integrations.Web.BrowserPageRenderer>();
        services.AddSingleton<JobFinder.Integrations.Web.VacancyCrawler>();
        services.AddSingleton<Ingest.PostEnricher>();
        services.AddSingleton<Ingest.PostReparser>();
        services.AddSingleton<JobFinder.Integrations.Telegram.ITelegramUsernames>(sp => sp.GetRequiredService<TelegramUserClient>());
        services.AddSingleton<Ingest.TelegramContactVerifier>(sp => new Ingest.TelegramContactVerifier(sp.GetRequiredService<IDbContextFactory<JobFinder.Data.JobFinderDbContext>>(),
            sp.GetRequiredService<JobFinder.Integrations.Telegram.ITelegramUsernames>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<Ingest.TelegramContactVerifier>>()));
        services.AddSingleton<HabrClient>();
        services.AddSingleton<IBrowserSiteLock, BrowserSiteLock>();
        services.AddSingleton<BrowserSessions>();
        services.AddSingleton<HhApplier>();
        services.AddSingleton<TelegramUserClient>();
        services.AddHttpClient("threads-api", client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false });
        services.AddSingleton(sp => new ThreadsApiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("threads-api"), sp.GetRequiredService<ThreadsOptions>()));
        services.AddSingleton<ThreadsBrowserClient>();
        services.AddSingleton<ThreadsGateway>();
        services.AddSingleton<EmailSender>();
        services.AddSingleton<HhNegotiations>();
        services.AddSingleton<HhChat>();
        services.AddSingleton<LinkedInClient>();
        services.AddSingleton(sp => new DeepgramStt(new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, sp.GetRequiredService<SttOptions>()));

        services.AddSingleton(sp => new SalaryEvaluator(sp.GetRequiredService<SalaryOptions>()));

        services.AddSingleton<RuntimeSettings>();
        services.AddSingleton<Modules.Discovery.ChannelDecisions>();
        services.AddSingleton<Modules.Discovery.ChannelMembershipRequests>();
        services.AddSingleton<Modules.Discovery.ChannelCollectionSources>();
        services.AddSingleton<Modules.Discovery.ChannelMembershipLedger>();
        services.AddSingleton<Modules.Discovery.ChannelMembershipWorkflow>();
        services.AddSingleton<Modules.Discovery.ChannelMonitoring>();
        services.AddSingleton<Modules.Discovery.IChannelMembershipGateway, Modules.Discovery.ChannelMembershipGateway>();
        services.AddSingleton<Modules.Hiring.ApplicationBlocks>();
        services.AddSingleton<Modules.Hiring.ApplicationAutomationControls>();
        services.AddSingleton<ServerOwnership>();
        services.AddSingleton<Modules.Hiring.DeliveryLedger>();
        services.AddSingleton<Modules.Hiring.EmployerContentGate>();
        services.AddSingleton<Modules.Hiring.IEmployerMessageGate>(sp => sp.GetRequiredService<Modules.Hiring.EmployerContentGate>());
        services.AddSingleton<Modules.Hiring.EmployerMessages>();
        services.AddSingleton<Modules.Hiring.EmployerMessageWorkflow>();
        services.AddSingleton<Modules.Hiring.IDeliveryTransport, Modules.Hiring.EmployerDeliveryTransport>();
        services.AddSingleton<Modules.Hiring.DeliveryGateway>();
        // State that is one document (profile, facts, browser logins, Telegram updates) and the MTProto session live in the database.
        services.AddSingleton<JobFinder.Core.Storage.IDocumentStore, Storage.PgDocumentStore>();
        services.AddSingleton<Storage.PgTelegramSessionStore>();
        services.AddSingleton<ITelegramSessionStore>(sp => sp.GetRequiredService<Storage.PgTelegramSessionStore>());
        services.AddSingleton<Storage.FileStateImport>();
        services.AddSingleton<ProfileProvider>();
        services.AddSingleton<FactsProvider>();
        services.AddSingleton<CandidateDocuments>();
        services.AddSingleton<ICandidatePublisher>(provider => provider.GetRequiredService<CandidateDocuments>());
        services.AddSingleton<Modules.Candidate.StorySources>();
        services.AddSingleton<Modules.Candidate.StoryPreparationRequests>();
        services.AddSingleton<Modules.Candidate.StoryPreparationWorkflow>();
        services.AddSingleton<Modules.Candidate.IStoryPreparationSteps, Modules.Candidate.StoryPreparationSteps>();
        services.AddSingleton<Notifier>();
        services.AddSingleton<DigestPublications>();
        services.AddSingleton<VacancyIngestor>();
        services.AddSingleton<HhLinkQueue>();
        services.AddSingleton<Modules.Discovery.HhLinkRequests>();
        services.AddSingleton<Modules.Discovery.CompanyPreferences>();
        services.AddSingleton<Modules.Discovery.IHhLinkGateway, Modules.Discovery.HhLinkGateway>();
        services.AddSingleton<Modules.Discovery.HhLinkWorkflow>();
        services.AddSingleton<Modules.Discovery.VacancyAssessments>();
        services.AddSingleton<Modules.Discovery.VacancyAssessmentCapture>();
        services.AddSingleton<Modules.Discovery.VacancyCaptureWorkflow>();
        services.AddSingleton<Modules.Discovery.VacancyAssessmentWorkflow>();
        services.AddSingleton<Modules.Discovery.IVacancyAssessmentSteps, Modules.Discovery.VacancyAssessmentSteps>();
        services.AddSingleton<Market.MarketService>();
        services.AddSingleton<Documents.DocumentAssets>();
        services.AddSingleton<Documents.ResumeTailor>();
        services.AddSingleton<Modules.Preparation.ResumePreparationRequests>();
        services.AddSingleton<Modules.Preparation.ResumePreparationWorkflow>();
        services.AddSingleton<Modules.Preparation.IResumePreparationSteps, Modules.Preparation.ResumePreparationSteps>();
        services.AddSingleton<ApprovalService>();
        services.AddSingleton<JobTriggers>();

        // Career: the funnel after "sent", interviews, stories, offers, contacts.
        services.AddSingleton<FunnelService>();
        services.AddSingleton<ReplyService>();
        services.AddSingleton<Modules.Hiring.ReplyDraftSources>();
        services.AddSingleton<Modules.Hiring.ReplyProcessingRequests>();
        services.AddSingleton<Modules.Hiring.ReplyProcessingSources>();
        services.AddSingleton<Modules.Hiring.ReplyProcessingWorkflow>();
        services.AddSingleton<Modules.Hiring.IReplyProcessingSteps, Modules.Hiring.ReplyProcessingSteps>();
        services.AddSingleton<Modules.Hiring.OutreachSources>();
        services.AddSingleton<Modules.Hiring.ContactSources>();
        services.AddSingleton<Modules.Hiring.OutreachScans>();
        services.AddSingleton<Modules.Hiring.FollowUpSources>();
        services.AddSingleton<Modules.Hiring.HhNegotiationSources>();
        services.AddSingleton<Modules.Hiring.HhChatHistorySources>();
        services.AddSingleton<Modules.Hiring.TelegramChatHistorySources>();
        services.AddSingleton<Modules.Hiring.EmailInboxSources>();
        services.AddSingleton<Modules.Hiring.HhResponseBackfillSources>();
        services.AddSingleton<ContactRegistry>();
        services.AddSingleton<LetterWriter>();
        services.AddSingleton<Modules.Hiring.LetterPreparationRequests>();
        services.AddSingleton<Modules.Hiring.ApplicationDeliveryRequests>();
        services.AddSingleton<Modules.Hiring.ApplicationDeliveryWorkflow>();
        services.AddSingleton<Modules.Hiring.ApplicationDeliveryPublication>();
        services.AddSingleton<Modules.Hiring.IApplicationDeliveryControls, Modules.Hiring.ApplicationDeliveryControls>();
        services.AddSingleton<Modules.Hiring.IApplicationDeliveryGate>(sp => sp.GetRequiredService<Modules.Hiring.EmployerContentGate>());
        services.AddSingleton<Modules.Hiring.LetterPreparationWorkflow>();
        services.AddSingleton<Modules.Hiring.ILetterPreparationSteps, Modules.Hiring.LetterPreparationSteps>();
        services.AddSingleton<Modules.Hiring.LetterLaneCancellation>();
        services.AddSingleton<RecruiterChatService>();
        services.AddSingleton<CommentPlanner>();
        services.AddSingleton<CommentSources>();
        services.AddSingleton<CommentDecisions>();
        services.AddSingleton<CommentExpiry>();
        services.AddSingleton<CommentSourceWorkflow>();
        services.AddSingleton<CommentDeliveryRequests>();
        services.AddSingleton<CommentDeliveryWorkflow>();
        services.AddSingleton<CommentDeliveryLedger>();
        services.AddSingleton<ICommentDeliveryTransport, CommentDeliveryTransport>();
        services.AddSingleton<Modules.Engagement.CommentPreparationRequests>();
        services.AddSingleton<Modules.Discovery.PostProcessingRequests>();
        services.AddSingleton<Modules.Discovery.IPostProcessingSteps, Modules.Discovery.PostProcessingSteps>();
        services.AddSingleton<Modules.Discovery.PostProcessingWorkflow>();
        services.AddSingleton<Modules.Engagement.ICommentPreparationSteps, Modules.Engagement.CommentPreparationSteps>();
        services.AddSingleton<Modules.Engagement.CommentPreparationWorkflow>();
        services.AddSingleton<LinkedInPeopleReader>();
        services.AddSingleton<Modules.Hiring.LinkedInSearchQuota>();
        services.AddSingleton<Modules.Hiring.LinkedInPeopleRequests>();
        services.AddSingleton<Modules.Hiring.LinkedInPeopleWorkflow>();
        services.AddSingleton<Controls.BotControls>();
        services.AddSingleton<Controls.BotControlWorkflow>();
        services.AddSingleton<Controls.IBotControlExecutor, Controls.DormantBotControlExecutor>();
        services.AddSingleton<Modules.Hiring.ILinkedInPeopleSteps, Modules.Hiring.LinkedInPeopleSteps>();
        services.AddSingleton<OutreachService>();
        services.AddSingleton<InterviewPrep>();
        services.AddSingleton<Modules.Preparation.InterviewPreparationRequests>();
        services.AddSingleton<Modules.Preparation.InterviewSources>();
        services.AddSingleton<Modules.Preparation.InterviewTransitions>();
        services.AddSingleton<Modules.Preparation.InterviewPreparationWorkflow>();
        services.AddSingleton<Modules.Preparation.IInterviewPreparationCapture, Modules.Preparation.InterviewPreparationCapture>();
        services.AddSingleton<Modules.Preparation.IInterviewPreparationSteps, Modules.Preparation.InterviewPreparationSteps>();
        services.AddSingleton<NotificationDeliveryRequests>();
        services.AddSingleton<LegacyNotificationImports>();
        services.AddSingleton<NativePublicationScopes>();
        services.AddSingleton<NativeBootstrapPublisher>();
        services.AddSingleton<NotificationDeliveryWorkflow>();
        services.AddSingleton<NotificationDeliveryLedger>();
        services.AddSingleton<INotificationDeliveryTransport, UnavailableNotificationTransport>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<Maintenance.OperationalRetention>();
        services.AddSingleton<BotEdits>();
        services.AddSingleton<StoryBank>();
        services.AddSingleton<MockInterviews>();
        services.AddSingleton<Modules.Preparation.MockSources>();
        services.AddSingleton<Modules.Preparation.MockPreparationRequests>();
        services.AddSingleton<Modules.Preparation.MockPreparationWorkflow>();
        services.AddSingleton<Modules.Preparation.IMockPreparationSteps, Modules.Preparation.MockPreparationSteps>();
        services.AddSingleton<OfferAdvisor>();
        services.AddSingleton<Modules.Hiring.OfferSources>();
        services.AddSingleton<Modules.Hiring.OfferPreparationRequests>();
        services.AddSingleton<Modules.Hiring.OfferPreparationWorkflow>();
        services.AddSingleton<Modules.Hiring.IOfferPreparationSteps, Modules.Hiring.OfferPreparationSteps>();
        services.AddSingleton<RejectionReviewService>();
        services.AddSingleton<Modules.Preparation.RejectionReviewSources>();
        services.AddSingleton<Modules.Preparation.RejectionReviewRequests>();
        services.AddSingleton<Modules.Preparation.RejectionReviewWorkflow>();
        services.AddSingleton<Modules.Preparation.IRejectionReviewSteps, Modules.Preparation.RejectionReviewSteps>();
        services.AddSingleton<InterviewProgress>();
        services.AddSingleton<Modules.Preparation.LearningProgressSources>();
        services.AddSingleton<HrPlaybookService>();
        services.AddSingleton<Modules.Preparation.HrPlaybookSources>();
        services.AddSingleton<Modules.Preparation.HrPlaybookRequests>();
        services.AddSingleton<Modules.Preparation.HrPlaybookWorkflow>();
        services.AddSingleton<Modules.Preparation.IHrPlaybookPreparationSteps, Modules.Preparation.HrPlaybookPreparationSteps>();
        services.AddSingleton<IGeneratedAnswerStore, PgGeneratedAnswerStore>();
        services.AddSingleton<GeneratedAnswers>();
        services.AddSingleton<CareerChat>();
        services.AddSingleton<Modules.Preparation.CareerPromptSources>();
        services.AddSingleton<WeeklyReportJob>();
        services.AddSingleton<Modules.Preparation.WeeklyReportSources>();
        services.AddSingleton<Modules.Preparation.WeeklyReportCapture>();
        services.AddSingleton<Modules.Preparation.WeeklyReportRequests>();
        services.AddSingleton<Modules.Preparation.WeeklyReportWorkflow>();
        services.AddSingleton<Modules.Preparation.IWeeklyReportSteps, Modules.Preparation.WeeklyReportSteps>();
        return services;
    }

    /// <summary>The background loops acquire exclusive ownership before initialization and recovery.</summary>
    public static IServiceCollection AddJobFinderPipeline(this IServiceCollection services)
    {
        services.AddJobFinderInitialization();
        // Before anything reads state (Telegram connects, workers read the profile): files of the old layout into the database.
        services.Insert(5, ServiceDescriptor.Singleton<IHostedService>(sp => sp.GetRequiredService<Storage.FileStateImport>()));
        services.AddHostedService<HhSearchJob>();
        services.AddHostedService<HhLinksJob>();
        services.AddHostedService<HabrSearchJob>();
        services.AddHostedService<CompanyCollectorJob>();
        services.AddHostedService<ThreadsJob>();
        services.AddHostedService<DigestJob>();
        services.AddHostedService<HousekeepingJob>();
        services.AddHostedService<TelegramService>();
        services.AddHostedService<RawPostWorker>();
        services.AddHostedService<VacancyWorker>();
        services.AddHostedService<ApplyWorker>();
        services.AddHostedService<CommentPublishWorker>();
        services.AddHostedService<ResumeWorker>();
        services.AddHostedService<InterviewWorker>();
        services.AddHostedService<InterviewReminderJob>();
        services.AddHostedService<HrPlaybookJob>();
        services.AddHostedService<EmailInboxJob>();
        services.AddHostedService<HhNegotiationsJob>();
        services.AddHostedService<FollowUpJob>();
        services.AddHostedService<OutreachJob>();
        services.AddHostedService(sp => sp.GetRequiredService<WeeklyReportJob>());
        return services;
    }

    public static IServiceCollection AddJobFinderInitialization(this IServiceCollection services)
    {
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, SingleInstanceGuard>());
        services.Insert(1, ServiceDescriptor.Singleton<IHostedService, DatabaseInitializer>());
        services.Insert(2, ServiceDescriptor.Singleton<IHostedService>(sp => sp.GetRequiredService<NativeBootstrapPublisher>()));
        services.Insert(3, ServiceDescriptor.Singleton<IHostedService, StartupRecovery>());
        services.Insert(4, ServiceDescriptor.Singleton<IHostedService, PolicyInitializer>());
        services.AddHostedService<NativeBootstrapCompletion>();
        services.Configure<HostOptions>(options => options.ServicesStartConcurrently = false);
        return services;
    }

    private static void Bind<T>(IServiceCollection services, IConfiguration config, string section) where T : class, new() =>
        Configuration.DeploymentConfiguration.Add<T>(services, config, section);
}

/// <summary>Applies schema migrations under exclusive ownership before publisher startup.</summary>
public sealed class DatabaseInitializer(IDbContextFactory<JobFinderDbContext> db, ILogger<DatabaseInitializer> log,
    ServerOwnership ownership, IHostApplicationLifetime lifetime) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
        await MigrateAsync(db, log, ownership, stopping.Token);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public static async Task MigrateAsync(IDbContextFactory<JobFinderDbContext> db, ILogger log, ServerOwnership ownership, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ownership.EnsureOwned();
            try
            {
                await using var ctx = await db.CreateDbContextAsync(ct);
                ownership.EnsureOwned();
                await ctx.Database.MigrateAsync(ct);
                ownership.EnsureOwned();
                log.Info("[db] schema is up to date");
                return;
            }
            catch (Npgsql.NpgsqlException e) when (attempt < 10)
            {
                // Postgres in the same compose file may still be starting.
                log.Warn($"[db] not reachable yet ({e.Message}), retry {attempt}/10");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
}

/// <summary>
/// One server per database. Two servers would share the Telegram auth key (AUTH_KEY_DUPLICATED can revoke it) and fight over the bot's
/// long poll (409), so a second one — host or container — refuses to start. The lock is a Postgres advisory lock held by a dedicated
/// connection for the life of the process: it works across machines and containers, and dies with the process.
/// </summary>
public sealed class SingleInstanceGuard(DatabaseOptions options, ILogger<SingleInstanceGuard> log, ServerOwnership ownership,
    IHostApplicationLifetime lifetime, TimeProvider time) : BackgroundService, IAsyncDisposable
{
    private const long LockKey = 0x4A6F6246696E64; // "JobFind"
    private Npgsql.NpgsqlConnection? _connection;

    public override async Task StartAsync(CancellationToken ct)
    {
        // Unpooled: the lock belongs to this physical connection and must end with it, not linger in a pool.
        _connection = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false }.ConnectionString);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _connection.OpenAsync(ct);
                break;
            }
            catch (Npgsql.NpgsqlException) when (attempt < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), time, ct);
            }
        }
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", _connection);
        cmd.Parameters.AddWithValue("key", LockKey);
        if (await cmd.ExecuteScalarAsync(ct) is true)
        {
            ownership.Acquired();
            log.Info("[instance] this is the only JobFinder server on the database");
            await base.StartAsync(ct);
            return;
        }

        await _connection.DisposeAsync();
        _connection = null;
        throw new InvalidOperationException(
            "another JobFinder server is already running on this database (host `dotnet run` or the container): stop it first. " +
            "One-off commands in the container: docker compose run --rm --entrypoint dotnet app JobFinder.App.dll --doctor");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
                if (await VerifyAsync(stoppingToken)) continue;
                throw new InvalidOperationException("the server advisory lock was lost");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ownership.Lost();
            log.Error($"[instance] server ownership lost: {error.Message}");
            lifetime.StopApplication();
        }
    }

    internal async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (_connection?.State != System.Data.ConnectionState.Open) return false;
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND classid = @class AND objid = @object AND objsubid = 1)", _connection);
        command.Parameters.AddWithValue("class", NpgsqlTypes.NpgsqlDbType.Oid, unchecked((uint)(LockKey >> 32)));
        command.Parameters.AddWithValue("object", NpgsqlTypes.NpgsqlDbType.Oid, unchecked((uint)LockKey));
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        ownership.Lost();
        if (_connection is not null) await _connection.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        ownership.Lost();
        base.Dispose();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    public override void Dispose()
    {
        ownership.Lost();
        base.Dispose();
        _connection?.Dispose();
    }
}
