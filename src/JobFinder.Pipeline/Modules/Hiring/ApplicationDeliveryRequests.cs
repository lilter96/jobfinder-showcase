using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class ApplicationDeliveryRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    ProfileProvider profiles, FactsProvider facts, RuntimeSettings settings, JobFinderLlm llm, TimeProvider time,
    NotificationDeliveryRequests notifications)
{
    public async Task<Guid?> RequestAsync(long applicationId, CancellationToken ct)
    {
        await using var read = await db.CreateDbContextAsync(ct);
        var application = await ApplicationDeliverySources.ReadAsync(read, applicationId, ct);
        if (application is not { Status: ApplicationStatus.Pending, Vacancy: not null } || application.Channel == ApplicationChannel.Manual
            || application.Mode == ApplicationMode.External) return null;
        var input = await CaptureAsync(application, ct);
        return input is null ? null : await EnqueueAsync(input, ct);
    }

    public async Task<Guid> QueueAsync(JobFinderDbContext context, IDbContextOutbox outbox, Application application, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || context.Entry(application).State == EntityState.Detached)
            throw new InvalidOperationException("application handoff requires the source transaction and its tracked application");
        await notifications.SaveJoinedAsync(context, outbox, ct);
        using var stage = Telemetry.StartStage("capture", "application-delivery").Tag("application.id", application.Id);
        var saved = await ApplicationDeliverySources.ReadAsync(context, application.Id, ct);
        if (saved is not { Status: ApplicationStatus.Pending, Vacancy: not null } || saved.Channel == ApplicationChannel.Manual
            || saved.Mode == ApplicationMode.External) throw new ApplicationDeliverySupersededException();
        var input = await CaptureAsync(saved, ct) ?? throw new ApplicationDeliverySupersededException();
        var id = await QueueInputAsync(context, outbox, input, ct);
        var marker = context.Entry(application).Property(row => row.DeliveryRequestId);
        marker.CurrentValue = id;
        marker.OriginalValue = id;
        marker.IsModified = false;
        return id;
    }

    public async Task<Guid> SaveAsync(JobFinderDbContext context, Application application, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var id = await QueueAsync(context, outbox, application, ct);
        await notifications.SaveAndFlushAsync(context, outbox, ct);
        return id;
    }

    public async Task SavePendingAsync(JobFinderDbContext context, long vacancyId, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("application handoff requires the source transaction");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await notifications.SaveJoinedAsync(context, outbox, ct);
        var pending = await context.Applications.Where(row => row.VacancyId == vacancyId && row.Status == ApplicationStatus.Pending).ToListAsync(ct);
        foreach (var application in pending) await QueueAsync(context, outbox, application, ct);
        await notifications.SaveAndFlushAsync(context, outbox, ct);
    }

    public async Task SaveAsync(JobFinderDbContext context, IReadOnlyCollection<Application> applications, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("application handoff requires the source transaction");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        foreach (var application in applications) await QueueAsync(context, outbox, application, ct);
        await notifications.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task<ApplicationDeliveryInput?> CaptureAsync(Application application, CancellationToken ct)
    {
        var profile = await profiles.GetSnapshotAsync(ct);
        if (profile is not { Value.Reviewed: true }) return null;
        var sheet = await facts.GetSnapshotAsync(ct);
        if (sheet.Version <= 0) return null;
        var policy = await settings.Automation.ReadAsync(ct);
        var operation = await llm.ForOperationAsync(ct);
        var brief = LetterWriter.ResponseBrief(application.Vacancy!, application.Channel, application.Target);
        var input = new ApplicationDeliveryInput
        {
            ApplicationId = application.Id, OperationId = application.OperationId, Revision = application.Revision,
            Mode = application.Mode, Channel = application.Channel, Target = application.Target, OriginalLetter = application.CoverLetter,
            Text = HumanText.Clean(application.CoverLetter ?? application.Vacancy!.CoverLetter ?? ""),
            Vacancy = AssessmentVacancy.From(application.Vacancy!), Evaluation = application.Vacancy!.Evaluation,
            Score = application.Vacancy.Score, Decision = application.Vacancy.Decision, CachedReview = application.Vacancy.LetterReview,
            Profile = profile.Value, ProfileVersion = profile.Version, Facts = sheet.Value, FactsVersion = sheet.Version,
            Automation = policy.Value, AutomationVersion = policy.Version, Operation = operation.CaptureOperation(), CapturedAt = time.GetUtcNow(),
        };
        if (string.IsNullOrWhiteSpace(input.Text) || input.Channel != ApplicationChannel.HeadHunter && string.IsNullOrWhiteSpace(input.Target)) return null;
        input.Recipe = LetterWriter.ReviewRecipe(operation, input.Profile, input.Facts, brief, input.Text);
        input.ReviewContextHash = LetterWriter.ContextRecipe(operation, input.Profile, input.Facts, brief).Hash;
        return input;
    }

    internal async Task<Guid> EnqueueAsync(ApplicationDeliveryInput input, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        var id = await QueueInputAsync(context, outbox, input, ct);
        await notifications.SaveAndFlushAsync(context, outbox, ct);
        return id;
    }

    private async Task<Guid> QueueInputAsync(JobFinderDbContext context, IDbContextOutbox outbox, ApplicationDeliveryInput input, CancellationToken ct)
    {
        var request = new ApplicationDeliveryRequest
        {
            Id = Guid.NewGuid(), ApplicationId = input.ApplicationId, ApplicationRevision = input.Revision,
            InputJson = ApplicationDeliverySources.Serialize(input), InputHash = "", Stage = ApplicationDeliveryStage.Preparing, CreatedAt = time.GetUtcNow(),
        };
        request.InputHash = CanonicalJson.Hash(request.InputJson);
        input = ApplicationDeliverySources.Read(request);
        ApplicationDeliverySources.EnsureCompatible(input, llm);
        await DeliveryLedger.LockVacancyAsync(context, input.Vacancy.Id, ct);
        var existing = await context.ApplicationDeliveryRequests.SingleOrDefaultAsync(row => row.ApplicationId == input.ApplicationId
            && row.ApplicationRevision == input.Revision, ct);
        if (existing is not null) return existing.Id;
        if (!await ApplicationDeliverySources.MatchesAsync(context, input, ct)) throw new ApplicationDeliverySupersededException();
        if (await context.Applications.AnyAsync(row => row.VacancyId == input.Vacancy.Id && row.Id != input.ApplicationId
            && (row.Status == ApplicationStatus.Sending || row.Status == ApplicationStatus.Sent || row.Status == ApplicationStatus.OutcomeUnknown), ct)
            || await context.EmployerMessages.AnyAsync(row => row.VacancyId == input.Vacancy.Id
                && (row.Status == EmployerMessageStatus.Sending || row.Status == EmployerMessageStatus.OutcomeUnknown), ct)
            || await context.ApplicationDeliveryRequests.AnyAsync(row => context.Applications.Any(application => application.Id == row.ApplicationId
                && application.VacancyId == input.Vacancy.Id) && (row.Stage == ApplicationDeliveryStage.Sending || row.Stage == ApplicationDeliveryStage.OutcomeUnknown), ct))
            throw new ApplicationDeliverySupersededException();
        await context.Applications.Where(row => row.Id == input.ApplicationId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.DeliveryRequestId, request.Id), ct);
        context.ApplicationDeliveryRequests.Add(request);
        await outbox.PublishAsync(new PrepareApplicationDelivery { RequestId = request.Id });
        return request.Id;
    }
}
