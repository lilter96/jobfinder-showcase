using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed partial class VacancyAssessments
{
    public async Task<Guid> RequestPreviewAsync(AssessmentVacancy vacancy, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("capture", "vacancy-preview");
        var frozen = JsonSerializer.Deserialize(JsonSerializer.Serialize(vacancy, VacancyAssessmentJsonContext.Default.AssessmentVacancy),
            VacancyAssessmentJsonContext.Default.AssessmentVacancy) ?? throw new UnsupportedVacancyAssessmentException();
        var profile = await profiles.GetSnapshotAsync(ct) ?? throw new InvalidOperationException("нет профиля — сначала импортируйте резюме");
        await using var read = await db.CreateDbContextAsync(ct);
        var factsDocument = await read.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == FactsProvider.Key, ct);
        var sheet = factsDocument is null ? new CandidateFacts().WithStandardQuestions()
            : JsonSerializer.Deserialize(factsDocument.Content, FactsJsonContext.Default.CandidateFacts)?.WithStandardQuestions()
                ?? throw new JsonException("пустые факты");
        var policy = await search.ReadAsync(ct);
        var operation = await llm.ForOperationAsync(ct);
        var captured = frozen with
        {
            Id = 0, CompanyId = null, Blacklisted = false, Industries = [], Sources = [], Status = VacancyStatus.New,
            Revision = 0, CoverLetter = null, LetterRevision = 0,
        };
        var money = salary.Evaluate(captured.ToVacancy(), profile.Value.DesiredSalary);
        var input = new VacancyAssessmentInput
        {
            Version = 3, Preview = new(), Vacancy = captured, Profile = profile.Value, ProfileVersion = profile.Version,
            Facts = sheet, FactsVersion = factsDocument?.Version ?? 0, Search = policy.Value, SearchVersion = policy.Version,
            Operation = operation.CaptureOperation(), SalaryNote = money.Note, SalaryAdjustment = money.Adjustment,
            CapturedAt = time.GetUtcNow(),
        };
        input.Recipe = VacancyAssessmentSteps.Recipe(operation, input);
        input.Preview.Key = PreviewKey(input);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await Modules.Preparation.InterviewPreparationCapture.LockCandidateAsync(context, ct);
        var documents = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {ProfileProvider.Key} OR key = {FactsProvider.Key} FOR SHARE").ToListAsync(ct);
        if (documents.SingleOrDefault(row => row.Key == ProfileProvider.Key)?.Version != input.ProfileVersion
            || (documents.SingleOrDefault(row => row.Key == FactsProvider.Key)?.Version ?? 0) != input.FactsVersion)
            throw new VacancyAssessmentSupersededException();
        await MatchesAsync(context, input, ct);
        var old = await context.VacancyAssessmentRequests.AsNoTracking().SingleOrDefaultAsync(row => row.PreviewKey == input.Preview.Key, ct);
        if (old is not null) { ReadInput(old); return old.Id; }
        var root = new VacancyAssessmentRequest
        {
            Id = input.Operation.OperationId, PreviewKey = input.Preview.Key, VacancyId = 0,
            InputJson = JsonSerializer.Serialize(input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput),
            Stage = VacancyAssessmentStage.Scoring, CreatedAt = time.GetUtcNow(),
        };
        root.InputHash = CanonicalJson.Hash(root.InputJson);
        ReadInput(root);
        context.VacancyAssessmentRequests.Add(root);
        await outbox.PublishAsync(new ScoreVacancyPreview { RequestId = root.Id });
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return root.Id;
    }

    internal static string PreviewKey(VacancyAssessmentInput input) => Fingerprint.Of(input.Recipe.Hash,
        input.ProfileVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), input.FactsVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        input.SearchVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonSerializer.Serialize(input.Search, VacancyAssessmentJsonContext.Default.SearchPolicy),
        input.SalaryAdjustment.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async Task<(VacancyAssessmentInput Input, AssessmentScore Score)> WaitPreviewAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.VacancyAssessmentRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            var input = ReadInput(root);
            if (input.Preview is null) throw new UnsupportedVacancyAssessmentException();
            if (root.Stage == VacancyAssessmentStage.Completed) return (input, ReadPreviewScore(root));
            if (root.Stage is VacancyAssessmentStage.Failed or VacancyAssessmentStage.Cancelled) throw new VacancyAssessmentSupersededException();
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static AssessmentScore ReadPreviewScore(VacancyAssessmentRequest root)
    {
        if (ReadInput(root).Preview is null || root.ScoreJson is null || CanonicalJson.Hash(root.ScoreJson) != root.ScoreHash)
            throw new UnsupportedVacancyAssessmentException();
        var score = JsonSerializer.Deserialize(root.ScoreJson, VacancyAssessmentJsonContext.Default.AssessmentScore);
        if (score is not { Score: >= 0 and <= 100, CostUsd: >= 0, Status: VacancyStatus.Scored, Decision: Decision.None,
                Evaluation: { Strengths: not null, Gaps: not null, RedFlags: not null, Summary: not null, Model: not null } })
            throw new UnsupportedVacancyAssessmentException();
        return score;
    }
}
