using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Market;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed partial class VacancyAssessments(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, ProfileProvider profiles,
    FactsProvider facts, SearchPolicyStore search, RuntimeSettings settings, JobFinderLlm llm, SalaryEvaluator salary,
    MarketService market, HhApplier hh, EmailSender email, ContactRegistry contacts, LocalClock clock, TimeProvider time,
    ServerOwnership ownership, ILogger<VacancyAssessments> log)
{
    public async Task<Guid?> RequestAsync(long vacancyId, CancellationToken ct, Guid? captureId = null)
    {
        var profile = await profiles.GetSnapshotAsync(ct);
        if (profile?.Value is not { Reviewed: true }) return null;
        var sheet = await facts.GetSnapshotAsync(ct);
        await using var read = await db.CreateDbContextAsync(ct);
        var vacancy = await ReadVacancyAsync(read, vacancyId, ct);
        if (vacancy is not { Status: VacancyStatus.New }) return null;
        if (captureId is not null && vacancy.AssessmentCaptureId != captureId) return null;
        if (await read.Applications.AnyAsync(application => application.VacancyId == vacancyId
                && (application.Status == ApplicationStatus.Pending || application.Status == ApplicationStatus.Sending
                    || application.Status == ApplicationStatus.OutcomeUnknown || application.Status == ApplicationStatus.DryRun), ct)) return null;
        if (vacancy.Company?.Blacklisted != true)
        {
            try { await contacts.FromDescriptionAsync(vacancy, ct); }
            catch (Exception error) when (!ct.IsCancellationRequested) { log.Warn($"[vacancies] #{vacancyId} optional contacts: {error.Message}"); }
        }
        var policy = await search.ReadAsync(ct);
        var automation = await settings.Automation.ReadAsync(ct);
        var money = salary.Evaluate(vacancy, profile.Value.DesiredSalary);
        var view = await market.DescribeAsync(vacancy, ct);
        var operation = await llm.ForOperationAsync(ct);
        var blockedUntil = await settings.GetInstantAsync(RuntimeSettings.ApplyBlockedUntil, ct);
        var input = new VacancyAssessmentInput
        {
            Vacancy = AssessmentVacancy.From(vacancy), Profile = profile.Value, ProfileVersion = profile.Version,
            Facts = sheet.Value, FactsVersion = sheet.Version, Search = policy.Value, SearchVersion = policy.Version,
            Automation = automation.Value, AutomationVersion = automation.Version, Operation = operation.CaptureOperation(),
            SalaryNote = money.Note, SalaryAdjustment = money.Adjustment, MarketNote = view.Note, RatioToP90 = view.RatioToP90,
            Reposts = await market.RepostsAsync(read, vacancy, ct), CapturedAt = time.GetUtcNow(),
            Responded = await RespondedAsync(read, vacancy, ct), EmailReady = email.IsConfigured,
            CanAutoApply = ApplyRoutes.HhApplicable(vacancy) && (blockedUntil is null || blockedUntil <= time.GetUtcNow()) && await hh.HasSessionAsync(ct),
            AutoToday = await read.Applications.CountAsync(ApplyWorker.CountsTowardAutoLimit(clock.StartOfTodayUtc()), ct),
        };
        input.Recipe = VacancyAssessmentSteps.Recipe(operation, input);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        if (captureId is not null)
        {
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('jobfinder.vacancy-assessment.admission', 0))", ct);
            var now = time.GetUtcNow();
            if (await context.VacancyAssessmentRequests.CountAsync(request =>
                (request.Stage == VacancyAssessmentStage.Scoring || request.Stage == VacancyAssessmentStage.Lettering || request.Stage == VacancyAssessmentStage.Finishing)
                && (request.RetryAt == null || request.RetryAt <= now), ct) >= 2) return null;
        }
        if (!await MatchesAsync(context, input, ct)) return null;
        var captured = context.Vacancies.Local.Single(row => row.Id == vacancyId);
        if (captureId is not null && captured.AssessmentCaptureId != captureId) return null;
        var active = await context.VacancyAssessmentRequests.SingleOrDefaultAsync(request => request.VacancyId == vacancyId
            && (request.Stage == VacancyAssessmentStage.Scoring || request.Stage == VacancyAssessmentStage.Lettering
                || request.Stage == VacancyAssessmentStage.Finishing), ct);
        if (active is not null)
        {
            var existing = ReadInput(active);
            if (Witness(existing.Vacancy) == Witness(input.Vacancy) && existing.ProfileVersion == input.ProfileVersion
                && existing.FactsVersion == input.FactsVersion)
            {
                if (captureId is not null)
                {
                    ownership.EnsureOwned();
                    captured.AssessmentCaptureId = null;
                    await context.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                return active.Id;
            }
            active.Stage = VacancyAssessmentStage.Cancelled;
            active.ClaimEnvelopeId = null;
            active.CompletedAt = time.GetUtcNow();
            active.Error = "vacancy or candidate snapshot superseded";
            await context.SaveChangesAsync(ct);
        }
        var request = new VacancyAssessmentRequest
        {
            Id = input.Operation.OperationId, VacancyId = vacancyId,
            InputJson = JsonSerializer.Serialize(input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput),
            Stage = VacancyAssessmentStage.Scoring, CreatedAt = time.GetUtcNow(),
        };
        request.InputHash = CanonicalJson.Hash(request.InputJson);
        if (captureId is not null)
        {
            ownership.EnsureOwned();
            captured.AssessmentCaptureId = null;
        }
        context.VacancyAssessmentRequests.Add(request);
        await outbox.PublishAsync(new ScoreVacancy { RequestId = request.Id });
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return request.Id;
    }

    internal static string Witness(AssessmentVacancy vacancy) => Fingerprint.Of(JsonSerializer.Serialize(vacancy, VacancyAssessmentJsonContext.Default.AssessmentVacancy));

    internal static VacancyAssessmentInput ReadInput(VacancyAssessmentRequest request)
    {
        var input = JsonSerializer.Deserialize(request.InputJson, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput);
        if (input is null || !Supported(input) || input.Vacancy is null || input.Operation is null || input.Recipe is null
            || input.Profile is null || input.Facts is null || input.Search is null || input.Automation is null
            || input.Vacancy.Id != request.VacancyId || input.Operation.OperationId != request.Id
            || input.Recipe.OperationId != request.Id || input.ProfileVersion <= 0 || (input.Preview is null ? input.FactsVersion <= 0 : input.FactsVersion < 0)
            || input.Preview is null && request.PreviewKey is not null
            || input.Preview is not null && (request.PreviewKey != input.Preview.Key || request.PreviewKey != PreviewKey(input)
                || request.Stage == VacancyAssessmentStage.Lettering || request.VacancyId != 0
                || request.Stage == VacancyAssessmentStage.Scoring && (request.ScoreJson is not null || request.ScoreHash is not null))
            || input.Annotation is not null && request.Stage == VacancyAssessmentStage.Lettering
            || CanonicalJson.Hash(request.InputJson) != request.InputHash) throw new UnsupportedVacancyAssessmentException();
        return input;
    }

    internal static Task<Vacancy?> ReadVacancyAsync(JobFinderDbContext context, long id, CancellationToken ct) =>
        context.Vacancies.AsNoTracking().Include(vacancy => vacancy.Company).Include(vacancy => vacancy.Sources).SingleOrDefaultAsync(vacancy => vacancy.Id == id, ct);

    internal static async Task<bool> MatchesAsync(JobFinderDbContext context, VacancyAssessmentInput input, CancellationToken ct)
    {
        if (input.Preview is { } preview)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"jobfinder.vacancy-preview:" + preview.Key},0))", ct);
            return true;
        }
        var locked = await context.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {input.Vacancy.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (locked is null || (input.Annotation is null ? locked.Status != VacancyStatus.New
            : !AnnotationEligible(locked) || AnnotationHash(AssessmentAnnotationSnapshot.From(locked)) != AnnotationHash(input.Annotation))) return false;
        if (await context.Applications.AnyAsync(application => application.VacancyId == locked.Id
            && (application.Status == ApplicationStatus.Pending || application.Status == ApplicationStatus.Sending
                || application.Status == ApplicationStatus.OutcomeUnknown || application.Status == ApplicationStatus.DryRun), ct)) return false;
        if (input.Annotation is not null && await Modules.Hiring.EmployerMessageSources.DeliveryPendingAsync(context, locked.Id, ct)) return false;
        if (locked.CompanyId is { } companyId)
            await context.Companies.FromSqlInterpolated($"SELECT * FROM companies WHERE id = {companyId} FOR SHARE").SingleOrDefaultAsync(ct);
        await context.VacancySources.FromSqlInterpolated($"SELECT * FROM vacancy_sources WHERE vacancy_id = {input.Vacancy.Id} FOR SHARE").ToListAsync(ct);
        var vacancy = await ReadVacancyAsync(context, input.Vacancy.Id, ct);
        var documents = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {ProfileProvider.Key} OR key = {FactsProvider.Key} FOR SHARE").ToListAsync(ct);
        return vacancy is not null && Witness(AssessmentVacancy.From(vacancy)) == Witness(input.Vacancy)
            && documents.Any(document => document.Key == ProfileProvider.Key && document.Version == input.ProfileVersion)
            && documents.Any(document => document.Key == FactsProvider.Key && document.Version == input.FactsVersion);
    }

    internal static async Task<bool> RespondedAsync(JobFinderDbContext context, Vacancy vacancy, CancellationToken ct)
    {
        var hhId = ApplyRoutes.HhId(vacancy);
        return await context.Applications.AnyAsync(application => application.VacancyId == vacancy.Id && application.Status == ApplicationStatus.Sent, ct)
            || await context.HhResponses.AnyAsync(response => hhId != null && response.HhVacancyId == hhId || response.Fingerprint == vacancy.Fingerprint, ct);
    }
}
