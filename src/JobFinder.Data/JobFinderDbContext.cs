using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JobFinder.Data;

/// <remarks>
/// Also the dashboard's data-protection key ring (<see cref="IDataProtectionKeyContext"/>): antiforgery and circuit keys survive a container
/// re-create with the rest of the state, and the backup carries them.
/// </remarks>
public sealed class JobFinderDbContext(DbContextOptions<JobFinderDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Vacancy> Vacancies => Set<Vacancy>();

    public DbSet<VacancySource> VacancySources => Set<VacancySource>();

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<Application> Applications => Set<Application>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    public DbSet<EmployerMessage> EmployerMessages => Set<EmployerMessage>();

    public DbSet<RawPost> RawPosts => Set<RawPost>();
    public DbSet<PostProcessingRequest> PostProcessingRequests => Set<PostProcessingRequest>();
    public DbSet<InterviewPreparationRequest> InterviewPreparationRequests => Set<InterviewPreparationRequest>();

    public DbSet<CommentDraft> CommentDrafts => Set<CommentDraft>();

    public DbSet<TelegramChannel> TelegramChannels => Set<TelegramChannel>();
    public DbSet<ChannelMembershipRequest> ChannelMembershipRequests => Set<ChannelMembershipRequest>();
    public DbSet<ChannelMembershipAttempt> ChannelMembershipAttempts => Set<ChannelMembershipAttempt>();

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationDeliveryRequest> NotificationDeliveryRequests => Set<NotificationDeliveryRequest>();
    public DbSet<NotificationDeliveryAttempt> NotificationDeliveryAttempts => Set<NotificationDeliveryAttempt>();

    public DbSet<AppSetting> Settings => Set<AppSetting>();

    public DbSet<PolicyRevision> PolicyRevisions => Set<PolicyRevision>();

    public DbSet<CandidateRevision> CandidateRevisions => Set<CandidateRevision>();

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();
    public DbSet<HhLinkRequest> HhLinkRequests => Set<HhLinkRequest>();
    public DbSet<ResumePreparationRequest> ResumePreparationRequests => Set<ResumePreparationRequest>();
    public DbSet<VacancyAssessmentRequest> VacancyAssessmentRequests => Set<VacancyAssessmentRequest>();
    public DbSet<LetterPreparationRequest> LetterPreparationRequests => Set<LetterPreparationRequest>();
    public DbSet<ReplyProcessingRequest> ReplyProcessingRequests => Set<ReplyProcessingRequest>();
    public DbSet<LinkedInPeopleRequest> LinkedInPeopleRequests => Set<LinkedInPeopleRequest>();
    public DbSet<OfferPreparationRequest> OfferPreparationRequests => Set<OfferPreparationRequest>();
    public DbSet<StoryPreparationRequest> StoryPreparationRequests => Set<StoryPreparationRequest>();
    public DbSet<MockPreparationRequest> MockPreparationRequests => Set<MockPreparationRequest>();
    public DbSet<HrPlaybookRequest> HrPlaybookRequests => Set<HrPlaybookRequest>();
    public DbSet<RejectionReviewRequest> RejectionReviewRequests => Set<RejectionReviewRequest>();
    public DbSet<WeeklyReportRequest> WeeklyReportRequests => Set<WeeklyReportRequest>();
    public DbSet<ApplicationBlockRevision> ApplicationBlockRevisions => Set<ApplicationBlockRevision>();
    public DbSet<TelegramChannelDecisionRevision> TelegramChannelDecisionRevisions => Set<TelegramChannelDecisionRevision>();
    public DbSet<TelegramChannelApprovalBatch> TelegramChannelApprovalBatches => Set<TelegramChannelApprovalBatch>();
    public DbSet<MockSessionRevision> MockSessionRevisions => Set<MockSessionRevision>();
    public DbSet<LearningProgressRevision> LearningProgressRevisions => Set<LearningProgressRevision>();
    public DbSet<BotControlRequest> BotControlRequests => Set<BotControlRequest>();
    public DbSet<CommentPreparationRequest> CommentPreparationRequests => Set<CommentPreparationRequest>();
    public DbSet<CommentSourceRequest> CommentSourceRequests => Set<CommentSourceRequest>();
    public DbSet<CommentDeliveryRequest> CommentDeliveryRequests => Set<CommentDeliveryRequest>();
    public DbSet<CommentDeliveryAttempt> CommentDeliveryAttempts => Set<CommentDeliveryAttempt>();
    public DbSet<ApplicationDeliveryRequest> ApplicationDeliveryRequests => Set<ApplicationDeliveryRequest>();
    public DbSet<LetterPreparationLane> LetterPreparationLanes => Set<LetterPreparationLane>();

    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();

    public DbSet<TailoredResume> TailoredResumes => Set<TailoredResume>();

    public DbSet<Story> Stories => Set<Story>();
    public DbSet<StoryRevision> StoryRevisions => Set<StoryRevision>();

    public DbSet<Interview> Interviews => Set<Interview>();

    public DbSet<CompanyContact> CompanyContacts => Set<CompanyContact>();

    public DbSet<TelegramUsername> TelegramUsernames => Set<TelegramUsername>();

    public DbSet<Outreach> Outreaches => Set<Outreach>();

    public DbSet<Offer> Offers => Set<Offer>();

    public DbSet<MockSession> MockSessions => Set<MockSession>();

    public DbSet<HhResponse> HhResponses => Set<HhResponse>();

    public DbSet<GeneratedAnswer> GeneratedAnswers => Set<GeneratedAnswer>();

    public DbSet<AppDocument> Documents => Set<AppDocument>();

    public DbSet<TelegramSession> TelegramSessions => Set<TelegramSession>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // Single queries on purpose: the rows are few (tens of vacancies, a handful of sources and applications each), so a join is cheaper
    // than a round trip per collection. Stating it silences EF's warning about several collection includes.
    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)).UseSnakeCaseNamingConvention();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Npgsql writes timestamptz from UTC offsets only; sources give local times (hh: +03:00), so every instant is normalized on write.
        b.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
        b.Properties<DateTimeOffset?>().HaveConversion<UtcConverter>();

        // Enums are stored by name: a row stays readable in psql, and reordering members never corrupts data.
        b.Properties<SourceKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<VacancyStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<Decision>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<EnglishLevel>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ApplicationChannel>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ApplicationMode>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ApplicationStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<DeliveryAttemptStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<EmployerMessageStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ApplicationDeliveryStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<RawPostStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<AudienceKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<CommentStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ChannelKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ChannelStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ChannelMembershipStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ChannelMembershipOutcome>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<NotificationKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<NotificationStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<NotificationDeliveryStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<NotificationEffectKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<JobRunStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<HhLinkStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ResumePreparationStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<VacancyAssessmentStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<LetterPreparationStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<CommentPreparationStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<CommentSourceStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<CommentDeliveryStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<PostProcessingStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<InterviewPreparationStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<LetterLaneStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ApplicationStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<Legitimacy>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<EventKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<EventChannel>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<TelegramPeerKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ReplyClass>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<DraftStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ReplyProcessingMode>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ReplyProcessingStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<LinkedInPeopleStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<OfferPreparationStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<StoryPreparationStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<MockPreparationStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<HrPlaybookStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<RejectionReviewStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<WeeklyReportStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<BotControlStage>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<InterviewKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<InterviewStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<StorySource>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<ContactKind>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<OutreachStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<MockStatus>().HaveConversion<string>().HaveMaxLength(24);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<NotificationDeliveryRequest>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.NotificationId).IsUnique();
            entity.HasIndex(row => new { row.Stage, row.CreatedAt });
            entity.Property(row => row.InputJson).HasColumnType("jsonb");
            entity.Property(row => row.AdmissionJson).HasColumnType("jsonb");
            entity.Property(row => row.AdmissionHash).HasMaxLength(64);
            entity.Property(row => row.LegacyReconciliationJson).HasColumnType("jsonb");
            entity.Property(row => row.LegacyReconciliationHash).HasMaxLength(64);
            entity.Property(row => row.PlanJson).HasColumnType("jsonb");
            entity.Property(row => row.InputHash).HasMaxLength(64);
            entity.Property(row => row.PlanHash).HasMaxLength(64);
        });
        m.Entity<NotificationDeliveryAttempt>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.RequestId, row.EffectIndex, row.Number }).IsUnique();
            entity.Property(row => row.EffectJson).HasColumnType("jsonb");
            entity.Property(row => row.EffectHash).HasMaxLength(64);
            entity.Property(row => row.InputHash).HasMaxLength(64);
            entity.Property(row => row.PlanHash).HasMaxLength(64);
        });
        m.Entity<Vacancy>(e =>
        {
            e.Property(v => v.Title).HasMaxLength(512);
            e.Property(v => v.CompanyName).HasMaxLength(512);
            e.Property(v => v.Url).HasMaxLength(1024);
            e.Property(v => v.Fingerprint).HasMaxLength(512);
            e.Property(v => v.Currency).HasMaxLength(8);
            e.Property(v => v.CountryCode).HasMaxLength(8);
            e.Property(v => v.SalaryFrom).HasPrecision(14, 2);
            e.Property(v => v.SalaryTo).HasPrecision(14, 2);
            e.Property(v => v.Evaluation).HasColumnType("jsonb").HasConversion(Json(DataJsonContext.Default.VacancyEvaluation));
            e.Property(v => v.LetterReview).HasColumnType("jsonb").HasConversion(Json(DataJsonContext.Default.ApplicationLetterReview));
            e.Property(v => v.LetterRevision).IsConcurrencyToken();
            e.Property(v => v.Contacts).HasColumnType("jsonb")
                .HasConversion(Json(DataJsonContext.Default.ListVacancyContact), JsonComparer(DataJsonContext.Default.ListVacancyContact));
            e.HasIndex(v => v.Fingerprint);
            e.HasIndex(v => v.Status);
            e.HasIndex(v => v.FirstSeenAt);
            e.HasIndex(v => v.Score);
            e.HasIndex(v => v.Legitimacy);
            e.HasOne(v => v.Company).WithMany().HasForeignKey(v => v.CompanyId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(v => v.Sources).WithOne(s => s.Vacancy).HasForeignKey(s => s.VacancyId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(v => v.Applications).WithOne(a => a.Vacancy).HasForeignKey(a => a.VacancyId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<VacancySource>(e =>
        {
            e.Property(s => s.ExternalId).HasMaxLength(128);
            e.Property(s => s.Url).HasMaxLength(1024);
            e.Property(s => s.Host).HasMaxLength(128);
            e.Property(s => s.RawJson).HasColumnType("jsonb");
            e.HasIndex(s => new { s.Source, s.ExternalId }).IsUnique();
        });

        m.Entity<Company>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(512);
            e.Property(c => c.Rating).HasPrecision(4, 2);
            e.HasIndex(c => c.HhId).IsUnique().HasFilter("hh_id IS NOT NULL");
            e.HasIndex(c => c.HabrAlias).IsUnique().HasFilter("habr_alias IS NOT NULL");
            e.HasIndex(c => c.Name);
        });

        m.Entity<Application>(e =>
        {
            e.Property(a => a.OperationId).HasDefaultValueSql("gen_random_uuid()");
            e.Property(a => a.Revision).IsConcurrencyToken();
            e.HasIndex(a => a.OperationId).IsUnique();
            e.HasIndex(a => a.Status);
            e.HasIndex(a => a.CreatedAt);
            e.HasIndex(a => a.Stage);
            e.HasIndex(a => a.TargetPeerId);
            e.HasMany(a => a.Events).WithOne(ev => ev.Application).HasForeignKey(ev => ev.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<PolicyRevision>(e =>
        {
            e.HasKey(revision => new { revision.Key, revision.Version });
            e.Property(revision => revision.Content).HasColumnType("json");
            e.Property(revision => revision.Actor).HasMaxLength(128);
        });

        m.Entity<CandidateRevision>(e =>
        {
            e.HasKey(revision => new { revision.Key, revision.Version });
            e.Property(revision => revision.Content).HasColumnType("json");
            e.Property(revision => revision.Actor).HasMaxLength(128);
        });

        m.Entity<DeliveryAttempt>(e =>
        {
            e.Property(attempt => attempt.Review).HasColumnType("jsonb").HasConversion(Json(DataJsonContext.Default.ApplicationLetterReview));
            e.Property(attempt => attempt.TextHash).HasMaxLength(64);
            e.Property(attempt => attempt.ResumeHash).HasMaxLength(64);
            e.HasIndex(attempt => new { attempt.ApplicationId, attempt.ApplicationRevision }).IsUnique().HasFilter("message_id IS NULL");
            e.HasIndex(attempt => attempt.Status);
            e.HasIndex(attempt => attempt.MessageId).IsUnique();
            e.HasOne(attempt => attempt.Application).WithMany().HasForeignKey(attempt => attempt.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(attempt => attempt.Message).WithMany().HasForeignKey(attempt => attempt.MessageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationDeliveryRequest>().WithMany().HasForeignKey(attempt => attempt.ApplicationDeliveryRequestId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<EmployerMessage>(e =>
        {
            e.HasIndex(message => message.BusinessKey).IsUnique();
            e.HasIndex(message => new { message.VacancyId, message.Status });
            e.Property(message => message.InputJson).HasColumnType("jsonb");
            e.Property(message => message.Review).HasColumnType("jsonb").HasConversion(Json(DataJsonContext.Default.ApplicationLetterReview));
        });

        m.Entity<ApplicationEvent>(e =>
        {
            e.Property(row => row.Revision).IsConcurrencyToken();
            e.Property(ev => ev.ExternalId).HasMaxLength(256);
            e.HasIndex(ev => new { ev.ApplicationId, ev.At });
            e.HasIndex(ev => new { ev.Channel, ev.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
            e.HasIndex(ev => ev.DraftStatus);
        });

        m.Entity<TailoredResume>(e =>
        {
            e.Property(t => t.ContentJson).HasColumnType("jsonb");
            e.Property(t => t.ReportJson).HasColumnType("jsonb");
            e.HasOne(t => t.Vacancy).WithMany(v => v.TailoredResumes).HasForeignKey(t => t.VacancyId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => t.VacancyId);
        });

        m.Entity<Story>(e =>
        {
            e.HasIndex(s => s.Confirmed);
            e.HasIndex(s => s.PreparationRequestId).IsUnique().HasFilter("preparation_request_id IS NOT NULL");
            e.HasQueryFilter(s => s.RetiredAt == null);
            e.Property(s => s.CreationKey).HasMaxLength(128);
            e.Property(s => s.CreationHash).HasMaxLength(64);
            e.HasIndex(s => s.CreationKey).IsUnique().HasFilter("creation_key IS NOT NULL");
        });
        m.Entity<StoryRevision>(e =>
        {
            e.HasKey(s => new { s.StoryId, s.Revision });
            e.Property(s => s.BeforeJson).HasColumnType("jsonb");
            e.Property(s => s.AfterJson).HasColumnType("jsonb");
            e.Property(s => s.Change).HasMaxLength(32);
        });

        m.Entity<Interview>(e =>
        {
            e.Property(i => i.PackJson).HasColumnType("jsonb");
            e.HasOne(i => i.Application).WithMany().HasForeignKey(i => i.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => i.ScheduledAt);
        });

        m.Entity<GeneratedAnswer>(e =>
        {
            e.Property(c => c.Recipe).HasColumnType("jsonb").HasConversion(Json(Core.Artifacts.RecipeJsonContext.Default.ArtifactRecipe));
            e.HasKey(c => c.Key);
            e.Property(c => c.Key).HasMaxLength(200);
            e.Property(c => c.Scope).HasMaxLength(32);
            e.Property(c => c.QuestionId).HasMaxLength(100);
            e.Property(c => c.DepsHash).HasMaxLength(64);
            e.Property(c => c.Model).HasMaxLength(100);
            e.Property(c => c.Purpose).HasMaxLength(32);
            e.Property(c => c.PromptVersion).HasMaxLength(64);
            e.Property(c => c.CostUsd).HasPrecision(12, 6);
            e.HasIndex(c => new { c.Scope, c.QuestionId });
            e.HasIndex(c => c.LastUsedAt);
        });

        m.Entity<HhResponse>(e =>
        {
            e.HasKey(r => r.HhVacancyId);
            e.Property(r => r.HhVacancyId).ValueGeneratedNever();
            e.Property(r => r.State).HasMaxLength(32);
            e.Property(r => r.Fingerprint).HasMaxLength(512);
            e.HasIndex(r => r.Fingerprint);
        });

        m.Entity<CompanyContact>(e =>
        {
            e.Property(c => c.CompanyKey).HasMaxLength(256);
            e.Property(c => c.Value).HasMaxLength(512);
            e.HasIndex(c => new { c.CompanyKey, c.Kind, c.Value }).IsUnique();
            e.HasIndex(c => c.CompanyId);
        });

        m.Entity<Outreach>(e =>
        {
            e.Property(row => row.Revision).IsConcurrencyToken();
            e.HasOne(o => o.Vacancy).WithMany().HasForeignKey(o => o.VacancyId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.Contact).WithMany().HasForeignKey(o => o.ContactId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(o => o.Status);
        });

        m.Entity<Offer>(e =>
        {
            e.Property(o => o.InputJson).HasColumnType("jsonb");
            e.Property(o => o.PublicationHash).HasMaxLength(64);
            e.HasIndex(o => o.PublicationId).IsUnique().HasFilter("publication_id IS NOT NULL");
            e.Property(o => o.AnalysisJson).HasColumnType("jsonb");
            e.Property(o => o.MonthlyAmount).HasPrecision(14, 2);
            e.Property(o => o.MonthlyNetUsd).HasPrecision(14, 2);
            e.HasOne(o => o.Application).WithMany().HasForeignKey(o => o.ApplicationId).OnDelete(DeleteBehavior.SetNull);
        });

        m.Entity<MockSession>(e =>
        {
            e.Property(ms => ms.TurnsJson).HasColumnType("jsonb");
            e.HasIndex(ms => ms.Status);
        });

        m.Entity<MockPreparationRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ResultJson).HasColumnType("jsonb");
            e.HasIndex(row => row.BusinessKey).IsUnique();
            e.HasIndex(row => new { row.SessionId, row.Stage });
            e.HasOne<MockSession>().WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<ApplicationBlockRevision>(e =>
        {
            e.HasKey(row => row.Revision);
            e.Property(row => row.Revision).ValueGeneratedNever();
            e.HasIndex(row => row.AttemptId).IsUnique();
            e.HasOne<DeliveryAttempt>().WithMany().HasForeignKey(row => row.AttemptId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<WeeklyReportRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ResultJson).HasColumnType("jsonb");
            e.HasIndex(row => row.BusinessKey).IsUnique();
            e.HasIndex(row => new { row.Stage, row.CreatedAt });
            e.HasOne<JobRun>().WithMany().HasForeignKey(row => row.JobRunId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<RejectionReviewRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ResultJson).HasColumnType("jsonb");
            e.HasIndex(row => row.BusinessKey).IsUnique();
            e.HasIndex(row => new { row.Stage, row.CreatedAt });
        });
        m.Entity<HrPlaybookRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ResultJson).HasColumnType("jsonb");
            e.HasIndex(row => row.BusinessKey).IsUnique();
            e.HasIndex(row => new { row.Stage, row.CreatedAt });
        });
        m.Entity<MockSessionRevision>(e =>
        {
            e.Property(row => row.BeforeJson).HasColumnType("jsonb");
            e.Property(row => row.AfterJson).HasColumnType("jsonb");
            e.HasIndex(row => new { row.SessionId, row.Revision }).IsUnique();
            e.HasOne<MockSession>().WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<TelegramUsername>(e =>
        {
            e.HasKey(u => u.Username);
            e.Property(u => u.Username).HasMaxLength(64);
            e.Property(u => u.Name).HasMaxLength(256);
        });

        m.Entity<RawPost>(e =>
        {
            e.Property(p => p.ChatRef).HasMaxLength(128);
            e.Property(p => p.ExternalId).HasMaxLength(128);
            e.Property(p => p.Links).HasColumnType("jsonb")
                .HasConversion(Json(DataJsonContext.Default.ListPostLink), JsonComparer(DataJsonContext.Default.ListPostLink));
            e.HasIndex(p => new { p.Source, p.ChatRef, p.ExternalId }).IsUnique();
            e.HasIndex(p => p.Status);
        });

        m.Entity<PostProcessingRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ReadingJson).HasColumnType("jsonb");
            e.Property(row => row.ExtractionJson).HasColumnType("jsonb");
            e.Property(row => row.VerificationJson).HasColumnType("jsonb");
            e.Property(row => row.ResultJson).HasColumnType("jsonb");
            e.Property(row => row.InputHash).HasMaxLength(64);
            e.Property(row => row.ReadingHash).HasMaxLength(64);
            e.Property(row => row.ExtractionHash).HasMaxLength(64);
            e.Property(row => row.VerificationHash).HasMaxLength(64);
            e.Property(row => row.ResultHash).HasMaxLength(64);
            e.HasIndex(row => new { row.PostId, row.Revision }).IsUnique();
            e.HasIndex(row => new { row.Stage, row.CreatedAt });
        });

        m.Entity<InterviewPreparationRequest>(entity =>
        {
            entity.Property(row => row.SeedJson).HasColumnType("jsonb");
            entity.Property(row => row.InputJson).HasColumnType("jsonb");
            entity.Property(row => row.PlanJson).HasColumnType("jsonb");
            entity.Property(row => row.CopilotJson).HasColumnType("jsonb");
            entity.Property(row => row.SeedHash).HasMaxLength(64);
            entity.Property(row => row.InputHash).HasMaxLength(64);
            entity.Property(row => row.PlanHash).HasMaxLength(64);
            entity.Property(row => row.CopilotHash).HasMaxLength(64);
            entity.Property(row => row.PdfHash).HasMaxLength(64);
            entity.HasIndex(row => new { row.InterviewId, row.Revision }).IsUnique();
            entity.HasIndex(row => new { row.Stage, row.CreatedAt });
        });

        m.Entity<CommentDraft>(e =>
        {
            e.Property(c => c.PostId).HasMaxLength(128);
            e.HasIndex(c => c.PostId).IsUnique();
            e.HasIndex(c => c.Status);
            e.HasIndex(c => c.Author);
        });

        m.Entity<TelegramChannel>(e =>
        {
            e.Property(row => row.DecisionRevision).IsConcurrencyToken();
            e.Property(row => row.MembershipRequestId).IsConcurrencyToken();
            e.Property(row => row.Status).IsConcurrencyToken();
            e.Property(c => c.Username).HasMaxLength(64);
            e.HasIndex(c => c.Username).IsUnique().HasFilter("username IS NOT NULL");
            e.HasIndex(c => c.PeerId).IsUnique().HasFilter("peer_id IS NOT NULL");
        });

        m.Entity<ChannelMembershipRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ChatJson).HasColumnType("jsonb");
            e.Property(row => row.HistoryJson).HasColumnType("jsonb");
            e.HasIndex(row => new { row.ChannelId, row.DecisionRevision }).IsUnique();
            e.HasOne<TelegramChannel>().WithMany().HasForeignKey(row => row.ChannelId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<ChannelMembershipAttempt>(e =>
        {
            e.HasOne<ChannelMembershipRequest>().WithMany().HasForeignKey(row => row.RequestId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(row => new { row.Stage, row.StartedAt });
        });

        m.Entity<TelegramChannelApprovalBatch>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.InputHash).HasMaxLength(64);
            e.HasOne(row => row.Notification).WithMany().HasForeignKey(row => row.NotificationId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TelegramChannelDecisionRevision>(e =>
        {
            e.HasKey(row => new { row.ChannelId, row.Revision });
            e.Property(row => row.BeforeJson).HasColumnType("jsonb");
            e.HasOne<TelegramChannel>().WithMany().HasForeignKey(row => row.ChannelId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TelegramChannelApprovalBatch>().WithMany().HasForeignKey(row => row.BatchId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Notification>(e =>
        {
            e.Property(n => n.Buttons).HasColumnType("jsonb").HasConversion(Json(DataJsonContext.Default.ListListString), JsonComparer(DataJsonContext.Default.ListListString));
            e.HasIndex(n => n.Status);
        });

        m.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(128);
        });

        m.Entity<AppDocument>(e =>
        {
            e.ToTable("app_documents");
            e.HasKey(d => d.Key);
            e.Property(d => d.Key).HasMaxLength(128);
            // json, not jsonb: a document is read and written whole, never queried inside; json keeps its text as written (the profile's
            // field order, readable in psql) and accepts every string a browser keeps in its local storage (jsonb refuses \u0000).
            e.Property(d => d.Content).HasColumnType("json");
            e.Property(d => d.Version).IsConcurrencyToken();
        });

        m.Entity<TelegramSession>(e =>
        {
            e.HasKey(s => s.Name);
            e.Property(s => s.Name).HasMaxLength(64);
        });

        m.Entity<JobRun>(e =>
        {
            e.Property(r => r.Job).HasMaxLength(64);
            e.HasIndex(r => new { r.Job, r.StartedAt });
        });

        m.Entity<LlmCall>(e =>
        {
            e.Property(c => c.Purpose).HasMaxLength(32);
            e.Property(c => c.Model).HasMaxLength(128);
            e.Property(c => c.RequestedModel).HasMaxLength(128);
            e.Property(c => c.Recipe).HasColumnType("jsonb").HasConversion(Json(Core.Artifacts.RecipeJsonContext.Default.ArtifactRecipe));
            e.Property(c => c.CostUsd).HasPrecision(12, 6);
            e.HasIndex(c => c.At);
            e.HasIndex(c => new { c.OperationId, c.At });
            e.HasIndex(c => new { c.CallId, c.AttemptNumber }).IsUnique();
        });

        m.Entity<HhLinkRequest>(e =>
        {
            e.Property(request => request.Contacts).HasColumnType("jsonb")
                .HasConversion(Json(DataJsonContext.Default.ListString), JsonComparer(DataJsonContext.Default.ListString));
            e.HasIndex(request => request.HhId).IsUnique().HasFilter("stage IN ('Reading', 'Linking')");
            e.HasIndex(request => new { request.Stage, request.CreatedAt });
        });

        m.Entity<CommentPreparationRequest>(e =>
        {
            e.Property(request => request.InputJson).HasColumnType("jsonb");
            e.Property(request => request.CurrentJson).HasColumnType("jsonb");
            e.Property(request => request.FirstJson).HasColumnType("jsonb");
            e.Property(request => request.ResultJson).HasColumnType("jsonb");
            e.Property(request => request.InputHash).HasMaxLength(64);
            e.Property(request => request.BusinessKey).HasMaxLength(64);
            e.HasIndex(request => request.BusinessKey).IsUnique().HasFilter("stage IN ('Drafting', 'Checking')");
            e.HasIndex(request => new { request.Stage, request.CreatedAt });
        });

        m.Entity<CommentDraft>(e =>
        {
            e.Property(draft => draft.Revision).IsConcurrencyToken();
            e.HasIndex(draft => draft.SourceRequestId).IsUnique();
        });

        m.Entity<CommentSourceRequest>(e =>
        {
            e.Property(request => request.InputJson).HasColumnType("jsonb");
            e.Property(request => request.InputHash).HasMaxLength(64);
            e.Property(request => request.SourceKey).HasMaxLength(64);
            e.HasIndex(request => request.SourceKey).IsUnique().HasFilter("stage IN ('Queued', 'Waiting')");
            e.HasIndex(request => new { request.Stage, request.CreatedAt });
        });

        m.Entity<CommentDeliveryRequest>(e =>
        {
            e.Property(request => request.InputJson).HasColumnType("jsonb");
            e.Property(request => request.PreparedJson).HasColumnType("jsonb");
            e.Property(request => request.InputHash).HasMaxLength(64);
            e.Property(request => request.PreparedHash).HasMaxLength(64);
            e.HasIndex(request => new { request.DraftId, request.Revision }).IsUnique();
            e.HasIndex(request => new { request.Stage, request.CreatedAt });
        });

        m.Entity<CommentDeliveryAttempt>(e =>
        {
            e.Property(attempt => attempt.TextHash).HasMaxLength(64);
            e.Property(attempt => attempt.InputHash).HasMaxLength(64);
            e.Property(attempt => attempt.PreparedHash).HasMaxLength(64);
            e.HasIndex(attempt => new { attempt.RequestId, attempt.CreatedAt });
        });

        m.Entity<ResumePreparationRequest>(e =>
        {
            e.Property(request => request.InputJson).HasColumnType("jsonb");
            e.Property(request => request.CurrentJson).HasColumnType("jsonb");
            e.Property(request => request.BestJson).HasColumnType("jsonb");
            e.HasIndex(request => request.VacancyId).IsUnique().HasFilter("stage IN ('Drafting', 'Printing', 'Reviewing', 'Finalizing')");
            e.HasIndex(request => new { request.Stage, request.CreatedAt });
        });

        m.Entity<VacancyAssessmentRequest>(e =>
        {
            e.Property(request => request.InputJson).HasColumnType("jsonb");
            e.Property(request => request.ScoreJson).HasColumnType("jsonb");
            e.Property(request => request.LetterJson).HasColumnType("jsonb");
            e.HasIndex(request => request.VacancyId).IsUnique().HasFilter("vacancy_id > 0 AND stage IN ('Scoring', 'Lettering', 'Finishing')");
            e.HasIndex(request => request.PreviewKey).IsUnique().HasFilter("preview_key IS NOT NULL");
            e.HasIndex(request => new { request.Stage, request.CreatedAt });
        });

        m.Entity<LetterPreparationRequest>(e =>
        {
            e.Property(request => request.InputJson).HasColumnType("jsonb");
            e.Property(request => request.BestJson).HasColumnType("jsonb");
            e.Property(request => request.ResultJson).HasColumnType("jsonb");
            e.HasIndex(request => request.BusinessKey).IsUnique().HasFilter("stage = 'Active'");
            e.HasIndex(request => new { request.BusinessKey, request.CreatedAt });
        });
        m.Entity<LinkedInPeopleRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ResultJson).HasColumnType("jsonb");
            e.HasIndex(row => row.BusinessKey).IsUnique();
            e.HasIndex(row => new { row.Stage, row.CreatedAt });
        });
        m.Entity<OfferPreparationRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ResultJson).HasColumnType("jsonb");
            e.HasIndex(row => row.BusinessKey).IsUnique();
            e.HasIndex(row => new { row.Stage, row.CreatedAt });
        });
        m.Entity<LearningProgressRevision>(e =>
        {
            e.Property(revision => revision.BeforeJson).HasColumnType("jsonb");
            e.Property(revision => revision.AfterJson).HasColumnType("jsonb");
            e.HasIndex(revision => new { revision.Scope, revision.Revision }).IsUnique();
        });

        m.Entity<StoryPreparationRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ResultJson).HasColumnType("jsonb");
            e.HasIndex(row => row.BusinessKey).IsUnique();
            e.HasIndex(row => new { row.Stage, row.CreatedAt });
        });
        m.Entity<BotControlRequest>(e =>
        {
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.ReconciliationJson).HasColumnType("jsonb");
            e.HasIndex(row => new { row.BotId, row.UpdateId }).IsUnique();
            e.HasIndex(row => new { row.BotId, row.Stage, row.UpdateId });
        });
        m.Entity<ReplyProcessingRequest>(e =>
        {
            e.Property(row => row.RawJson).HasColumnType("jsonb");
            e.Property(row => row.InputJson).HasColumnType("jsonb");
            e.Property(row => row.BestJson).HasColumnType("jsonb");
            e.HasIndex(row => row.BusinessKey).IsUnique();
            e.HasIndex(row => new { row.ApplicationId, row.Stage, row.CreatedAt });
            e.HasIndex(row => row.RetryOfId);
        });
        m.Entity<ApplicationDeliveryRequest>(e =>
        {
            e.Property(request => request.InputJson).HasColumnType("jsonb");
            e.Property(request => request.Review).HasColumnType("jsonb").HasConversion(Json(DataJsonContext.Default.ApplicationLetterReview));
            e.HasOne<Application>().WithMany().HasForeignKey(request => request.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(request => new { request.ApplicationId, request.ApplicationRevision }).IsUnique();
            e.HasIndex(request => new { request.Stage, request.CreatedAt });
        });
        m.Entity<LetterPreparationLane>(e =>
        {
            e.Property(lane => lane.DraftJson).HasColumnType("jsonb");
            e.Property(lane => lane.ResultJson).HasColumnType("jsonb");
            e.HasOne<LetterPreparationRequest>().WithMany().HasForeignKey(lane => lane.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(lane => new { lane.RequestId, lane.Round, lane.Lane }).IsUnique();
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AdvanceApplicationRevisions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AdvanceApplicationRevisions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AdvanceApplicationRevisions()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<Application>().Where(entry => entry.State == EntityState.Modified))
            entry.Entity.Revision = entry.Property(application => application.Revision).OriginalValue + 1;
    }

    /// <summary>Compares by serialized form: a list column is a value, not a reference, for change tracking.</summary>
    private static ValueComparer<T> JsonComparer<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        new((a, b) => JsonSerializer.Serialize(a!, info) == JsonSerializer.Serialize(b!, info),
            v => JsonSerializer.Serialize(v, info).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize(JsonSerializer.Serialize(v, info), info)!);

    private static ValueConverter<T, string> Json<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        new(v => JsonSerializer.Serialize(v, info), s => JsonSerializer.Deserialize(s, info)!);
}

internal sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VacancyEvaluation))]
[JsonSerializable(typeof(ApplicationLetterReview))]
[JsonSerializable(typeof(List<List<string>>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<VacancyContact>))]
[JsonSerializable(typeof(List<PostLink>))]
internal sealed partial class DataJsonContext : JsonSerializerContext;

/// <summary>For <c>dotnet ef migrations add</c>: the model only, no live database needed.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<JobFinderDbContext>
{
    public JobFinderDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(builder, Environment.GetEnvironmentVariable("JOBFINDER_DB")
                                              ?? "Host=localhost;Port=5432;Database=jobfinder;Username=jobfinder;Password=jobfinder");
        return new JobFinderDbContext(builder.Options);
    }
}
