using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed record AssessmentAnnotationSnapshot
{
    public VacancyEvaluation Evaluation { get; set; } = new();
    public int? Score { get; set; }
    public int? LlmScore { get; set; }
    public int SalaryAdjustment { get; set; }
    public Decision Decision { get; set; }
    public Legitimacy Legitimacy { get; set; }
    public string? RejectReason { get; set; }
    public bool Notified { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? ScoredAt { get; set; }
    public Guid? CaptureId { get; set; }
    public ApplicationLetterReview? LetterReview { get; set; }

    public static AssessmentAnnotationSnapshot From(Vacancy vacancy) => new()
    {
        Evaluation = vacancy.Evaluation!, Score = vacancy.Score, LlmScore = vacancy.LlmScore,
        SalaryAdjustment = vacancy.SalaryAdjustment, Decision = vacancy.Decision, Legitimacy = vacancy.Legitimacy,
        RejectReason = vacancy.RejectReason, Notified = vacancy.Notified, Attempts = vacancy.Attempts,
        LastError = vacancy.LastError, ScoredAt = vacancy.ScoredAt, CaptureId = vacancy.AssessmentCaptureId,
        LetterReview = vacancy.LetterReview,
    };
}

public sealed partial class VacancyAssessments
{
    internal static bool Supported(VacancyAssessmentInput input) => input.Preview is null && (input.Version == 1 && input.Annotation is null
        || input.Version == 2 && input.Annotation is { Evaluation.Keywords.Count: 0 }
            && input.Vacancy is { Status: VacancyStatus.Scored or VacancyStatus.Applied })
        || input.Version == 3 && input.Annotation is null && input.Preview is { Key.Length: > 0 }
            && input.Vacancy is { Id: 0, CompanyId: null, Sources.Count: 0, Status: VacancyStatus.New }
            && !input.Responded && !input.CanAutoApply && !input.EmailReady;

    internal static bool AnnotationEligible(Vacancy vacancy) => vacancy.Status is VacancyStatus.Scored or VacancyStatus.Applied
        && vacancy.Evaluation is { Keywords.Count: 0 } && vacancy.AssessmentRevision is >= 0 and < long.MaxValue
        && vacancy.AssessmentCaptureId is null;

    internal static string AnnotationHash(AssessmentAnnotationSnapshot annotation) =>
        CanonicalJson.Hash(JsonSerializer.Serialize(annotation, VacancyAssessmentJsonContext.Default.AssessmentAnnotationSnapshot));

    public async Task<Guid?> RequestAnnotationsAsync(long vacancyId, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("capture", "evaluation-annotations").Tag("vacancy.id", vacancyId);
        var profile = await profiles.GetSnapshotAsync(ct);
        if (profile?.Value is not { Reviewed: true }) return null;
        var sheet = await facts.GetSnapshotAsync(ct);
        await using var read = await db.CreateDbContextAsync(ct);
        var vacancy = await ReadVacancyAsync(read, vacancyId, ct);
        if (vacancy is null || !AnnotationEligible(vacancy)) return null;
        var policy = await search.ReadAsync(ct);
        var automation = await settings.Automation.ReadAsync(ct);
        var operation = await llm.ForOperationAsync(ct);
        var money = salary.Evaluate(vacancy, profile.Value.DesiredSalary);
        var view = await market.DescribeAsync(vacancy, ct);
        var input = new VacancyAssessmentInput
        {
            Version = 2, Annotation = AssessmentAnnotationSnapshot.From(vacancy), Vacancy = AssessmentVacancy.From(vacancy),
            Profile = profile.Value, ProfileVersion = profile.Version, Facts = sheet.Value, FactsVersion = sheet.Version,
            Search = policy.Value, SearchVersion = policy.Version, Automation = automation.Value, AutomationVersion = automation.Version,
            Operation = operation.CaptureOperation(), SalaryNote = money.Note, SalaryAdjustment = money.Adjustment,
            MarketNote = view.Note, RatioToP90 = view.RatioToP90, Reposts = await market.RepostsAsync(read, vacancy, ct),
            CapturedAt = time.GetUtcNow(),
        };
        input.Recipe = VacancyAssessmentSteps.Recipe(operation, input);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        if (!await MatchesAsync(context, input, ct)) return null;
        var active = await context.VacancyAssessmentRequests.SingleOrDefaultAsync(request => request.VacancyId == vacancyId
            && (request.Stage == VacancyAssessmentStage.Scoring || request.Stage == VacancyAssessmentStage.Lettering
                || request.Stage == VacancyAssessmentStage.Finishing), ct);
        if (active is not null)
        {
            var old = ReadInput(active);
            if (old.Annotation is not null && Witness(old.Vacancy) == Witness(input.Vacancy)
                && AnnotationHash(old.Annotation) == AnnotationHash(input.Annotation)
                && old.ProfileVersion == input.ProfileVersion && old.FactsVersion == input.FactsVersion) return active.Id;
            return null;
        }
        var request = new VacancyAssessmentRequest
        {
            Id = input.Operation.OperationId, VacancyId = vacancyId,
            InputJson = JsonSerializer.Serialize(input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput),
            Stage = VacancyAssessmentStage.Scoring, CreatedAt = time.GetUtcNow(),
        };
        request.InputHash = CanonicalJson.Hash(request.InputJson);
        context.VacancyAssessmentRequests.Add(request);
        await outbox.PublishAsync(new ScoreVacancy { RequestId = request.Id });
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return request.Id;
    }

    public async Task<VacancyAssessmentRequest> WaitAnnotationsAsync(Guid id, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("wait", "evaluation-annotations").Tag("operation.id", id);
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var request = await context.VacancyAssessmentRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            if (ReadInput(request).Annotation is null) throw new InvalidOperationException("request is not an evaluation annotation");
            if (request.Stage is VacancyAssessmentStage.Completed or VacancyAssessmentStage.Cancelled or VacancyAssessmentStage.Failed) return request;
            if (request.RetryAt > time.GetUtcNow()) return request;
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, ct);
        }
    }
}
