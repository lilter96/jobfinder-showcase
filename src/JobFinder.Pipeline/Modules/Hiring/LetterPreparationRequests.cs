using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class LetterPreparationRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    TimeProvider time)
{
    public async Task<LetterPreparationTicket> GetAsync(Guid id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var request = await context.LetterPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        if (request.Stage == LetterPreparationStage.Cancelled) throw new LetterPreparationSupersededException();
        if (request.Stage == LetterPreparationStage.Failed) throw new LetterPreparationFailedException(request.Error ?? "missing result");
        return new(id, request.Stage == LetterPreparationStage.Completed ? ReadCandidate(request.ResultJson).ToDraft() : null);
    }

    public async Task<ApplicationLetterReview?> GetReviewAsync(Guid id, LetterBrief? brief, string? text, CancellationToken ct)
    {
        var ticket = await GetAsync(id, ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var request = await context.LetterPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        var input = ReadInput(request);
        if (input.Version != 2 || input.ExactText is null || text is not null && input.ExactText != text
            || brief is not null && CanonicalJson.Hash(JsonSerializer.Serialize(LetterBriefInput.From(brief), LetterPreparationJsonContext.Default.LetterBriefInput))
                != CanonicalJson.Hash(JsonSerializer.Serialize(input.Brief, LetterPreparationJsonContext.Default.LetterBriefInput)))
            throw new LetterPreparationSupersededException("exact review dependency belongs to different text or context");
        if (ticket.Result is null) return null;
        var candidate = ReadCandidate(request.ResultJson);
        var review = ticket.Result.Review;
        if (ticket.Result.Text != input.ExactText || review is null || review.TextHash != Fingerprint.Of(input.ExactText)
            || review.ContextHash != input.ReviewContextHash || candidate.Score != review.Score || !candidate.Problems.SequenceEqual(review.Problems))
            throw new LetterPreparationFailedException("invalid persisted exact-text review");
        return review;
    }

    public async Task<LetterDraft?> WriteAsync(LetterBrief brief, CandidateProfile profile, CandidateFacts sheet,
        JobFinderLlm operation, CancellationToken ct, bool force = false, AssessmentVacancy? anchor = null)
    {
        var ticket = await RequestAsync(brief, profile, sheet, operation, ct, force, anchor);
        if (ticket.Result is not null) return ticket.Result;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var context = await db.CreateDbContextAsync(ct);
            var request = await context.LetterPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == ticket.RequestId, ct);
            if (request.Stage == LetterPreparationStage.Completed) return ReadCandidate(request.ResultJson).ToDraft();
            if (request.Stage == LetterPreparationStage.Cancelled) throw new LetterPreparationSupersededException();
            if (request.Stage == LetterPreparationStage.Failed) throw new LetterPreparationFailedException(request.Error ?? "missing result");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    public Task<LetterPreparationTicket> RequestAsync(LetterBrief brief, CandidateProfile profile, CandidateFacts sheet,
        JobFinderLlm operation, CancellationToken ct, bool force = false, AssessmentVacancy? anchor = null)
        => RequestCoreAsync(brief, profile, sheet, operation, ct, force, anchor, null);

    public Task<LetterPreparationTicket> RequestReviewAsync(LetterBrief brief, string text, CandidateProfile profile, CandidateFacts sheet,
        JobFinderLlm operation, CancellationToken ct)
        => RequestCoreAsync(brief, profile, sheet, operation, ct, false, null, text);

    private async Task<LetterPreparationTicket> RequestCoreAsync(LetterBrief brief, CandidateProfile profile, CandidateFacts sheet,
        JobFinderLlm operation, CancellationToken ct, bool force, AssessmentVacancy? anchor, string? exactText)
    {
        var input = new LetterPreparationInput
        {
            Brief = LetterBriefInput.From(brief), Anchor = anchor ?? AssessmentVacancy.From(brief.Vacancy),
            Profile = profile, Facts = sheet, CapturedAt = time.GetUtcNow(), Operation = operation.CaptureOperation(),
            Version = exactText is null ? 1 : 2, ExactText = exactText,
            Recipe = exactText is null ? LetterWriter.ContextRecipe(operation, profile, sheet, brief) : LetterWriter.ReviewRecipe(operation, profile, sheet, brief, exactText),
            ReviewContextHash = exactText is null ? "" : LetterWriter.ContextRecipe(operation, profile, sheet, brief).Hash,
        };
        input = JsonSerializer.Deserialize(SerializeInput(input), LetterPreparationJsonContext.Default.LetterPreparationInput)!;
        var businessKey = exactText is not null ? Fingerprint.Of(input.Recipe.Hash + "\n" + Witness(input.Anchor) + "\nletter-exact-review-v2")
            : anchor is not null
            ? Fingerprint.Of("assessment-first-letter\n" + input.Operation.OperationId)
            : Fingerprint.Of(input.Recipe.Hash + "\n" + input.CapturedAt.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"))
                + "\n" + Witness(input.Anchor) + "\nletter-durable-v1");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, businessKey, ct);
        if (input.Anchor.Id > 0)
        {
            await LockSourceAsync(context, input.Anchor.Id, ct);
            var current = await ReadVacancyAsync(context, input.Anchor.Id, ct);
            if (current is null || current.Status == VacancyStatus.Dismissed || Witness(AssessmentVacancy.From(current)) != Witness(input.Anchor))
                throw new LetterPreparationSupersededException();
            input.SourceContext = EmployerMessageSources.Context(current);
        }
        var documents = await LockDocumentsAsync(context, ct);
        var profileRow = documents.SingleOrDefault(row => row.Key == ProfileProvider.Key);
        var factsRow = documents.SingleOrDefault(row => row.Key == FactsProvider.Key);
        if (profileRow is null || factsRow is null || !input.Profile.Reviewed)
            throw new LetterPreparationSupersededException("reviewed candidate documents are required");
        if (CanonicalJson.Hash(ProfileProvider.ToJson(ProfileProvider.FromJson(profileRow.Content))) != CanonicalJson.Hash(ProfileProvider.ToJson(input.Profile)))
            throw new LetterPreparationSupersededException("candidate profile content superseded before capture");
        if (JsonSerializer.Deserialize(factsRow.Content, FactsJsonContext.Default.CandidateFacts)!.WithStandardQuestions().ForPrompt() != input.Facts.ForPrompt())
            throw new LetterPreparationSupersededException("candidate facts content superseded before capture");
        input.ProfileVersion = profileRow.Version;
        input.FactsVersion = factsRow.Version;
        var existing = await context.LetterPreparationRequests.Where(row => row.BusinessKey == businessKey
            && (row.Stage == LetterPreparationStage.Active || row.Stage == LetterPreparationStage.Completed))
            .OrderByDescending(row => row.CreatedAt).FirstOrDefaultAsync(ct);
        if (!force && existing is not null)
        {
            var saved = ReadInput(existing);
            if (saved.ProfileVersion == input.ProfileVersion && saved.FactsVersion == input.FactsVersion && saved.SourceContext == input.SourceContext
                && saved.Recipe.Hash == input.Recipe.Hash && saved.ReviewContextHash == input.ReviewContextHash && Witness(saved.Anchor) == Witness(input.Anchor))
                return new(existing.Id, existing.Stage == LetterPreparationStage.Completed ? ReadCandidate(existing.ResultJson).ToDraft() : null);
        }
        if (existing is { Stage: LetterPreparationStage.Active })
        {
            existing.Stage = LetterPreparationStage.Cancelled;
            existing.CompletedAt = time.GetUtcNow();
            existing.Error = "explicit regeneration superseded active preparation";
            await context.SaveChangesAsync(ct);
        }
        var request = new LetterPreparationRequest
        {
            Id = Guid.NewGuid(), VacancyId = input.Anchor.Id > 0 ? input.Anchor.Id : null,
            BusinessKey = businessKey, InputJson = SerializeInput(input), Stage = LetterPreparationStage.Active, CreatedAt = time.GetUtcNow(),
        };
        request.InputHash = CanonicalJson.Hash(request.InputJson);
        context.LetterPreparationRequests.Add(request);
        if (exactText is null) await StartRoundAsync(context, outbox, request);
        else
        {
            var lane = new LetterPreparationLane
            {
                Id = Guid.NewGuid(), RequestId = request.Id, Round = 1, Lane = 1, Stage = LetterLaneStage.Reviewing,
                DraftJson = SerializeCandidate(new LetterCandidate { Text = exactText, Written = exactText }),
            };
            context.LetterPreparationLanes.Add(lane);
            await outbox.PublishAsync(new ReviewLetterLane { RequestId = request.Id, LaneId = lane.Id });
        }
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return new(request.Id, null);
    }

    internal static async Task StartRoundAsync(JobFinderDbContext context, IDbContextOutbox outbox, LetterPreparationRequest request)
    {
        for (var number = 1; number <= 2; number++)
        {
            var lane = new LetterPreparationLane { Id = Guid.NewGuid(), RequestId = request.Id, Round = request.Round, Lane = number, Stage = LetterLaneStage.Drafting };
            context.LetterPreparationLanes.Add(lane);
            await outbox.PublishAsync(new DraftLetterLane { RequestId = request.Id, LaneId = lane.Id, Round = lane.Round });
        }
    }

    internal static string SerializeInput(LetterPreparationInput input) => JsonSerializer.Serialize(input, LetterPreparationJsonContext.Default.LetterPreparationInput);
    internal static string SerializeCandidate(LetterCandidate candidate) => JsonSerializer.Serialize(candidate, LetterPreparationJsonContext.Default.LetterCandidate);
    internal static LetterCandidate ReadCandidate(string? json) => json is null ? throw new UnsupportedLetterPreparationException()
        : JsonSerializer.Deserialize(json, LetterPreparationJsonContext.Default.LetterCandidate) ?? throw new UnsupportedLetterPreparationException();
    internal static string Witness(AssessmentVacancy source) => CanonicalJson.Hash(JsonSerializer.Serialize(source, LetterPreparationJsonContext.Default.AssessmentVacancy));

    internal static LetterPreparationInput ReadInput(LetterPreparationRequest request)
    {
        var input = JsonSerializer.Deserialize(request.InputJson, LetterPreparationJsonContext.Default.LetterPreparationInput);
        if (input is null || input.Version is not (1 or 2) || (input.Version == 1 ? input.ExactText is not null : input.ExactText is null || string.IsNullOrWhiteSpace(input.ReviewContextHash))
            || input.Brief is null || input.Brief.Vacancy is null || input.Anchor is null
            || input.Operation is null || input.Operation.OperationId == Guid.Empty || input.Operation.SchemaVersion != 1
            || input.Recipe?.OperationId != input.Operation.OperationId
            || input.Recipe is null || input.Profile is null || input.Facts is null || input.ProfileVersion <= 0 || input.FactsVersion <= 0
            || input.Brief.Vacancy.Id != input.Anchor.Id || input.Brief.Vacancy.Title != input.Anchor.Title || input.Brief.Vacancy.Url != input.Anchor.Url
            || !input.Profile.Reviewed || input.Profile.Skills is null || input.Facts.Facts is null || input.CapturedAt == default
            || !Enum.IsDefined(input.Brief.Channel) || input.Brief.Contact is null || input.Brief.Situation is null || input.Brief.Conversation is null
            || (input.Anchor.Id > 0 ? input.Anchor.Id : (long?)null) != request.VacancyId
            || CanonicalJson.Hash(request.InputJson) != request.InputHash) throw new UnsupportedLetterPreparationException();
        return input;
    }

    internal static Task LockAsync(JobFinderDbContext context, string businessKey, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({businessKey}, 0))", ct);

    private static async Task LockSourceAsync(JobFinderDbContext context, long id, CancellationToken ct)
    {
        var source = await context.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (source?.CompanyId is { } companyId) await context.Companies.FromSqlInterpolated($"SELECT * FROM companies WHERE id = {companyId} FOR SHARE").SingleOrDefaultAsync(ct);
        await context.VacancySources.FromSqlInterpolated($"SELECT * FROM vacancy_sources WHERE vacancy_id = {id} FOR SHARE").ToListAsync(ct);
    }

    private static Task<List<AppDocument>> LockDocumentsAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {ProfileProvider.Key} OR key = {FactsProvider.Key} FOR SHARE").ToListAsync(ct);

    internal static Task<Vacancy?> ReadVacancyAsync(JobFinderDbContext context, long id, CancellationToken ct) => context.Vacancies.AsNoTracking()
        .Include(row => row.Company).Include(row => row.Sources).Include(row => row.Applications).ThenInclude(row => row.Events)
        .SingleOrDefaultAsync(row => row.Id == id, ct);

    internal static async Task<bool> MatchesAsync(JobFinderDbContext context, LetterPreparationInput input, CancellationToken ct)
    {
        if (input.Anchor.Id > 0)
        {
            await LockSourceAsync(context, input.Anchor.Id, ct);
            var source = await ReadVacancyAsync(context, input.Anchor.Id, ct);
            if (source is null || source.Status == VacancyStatus.Dismissed || Witness(AssessmentVacancy.From(source)) != Witness(input.Anchor)
                || EmployerMessageSources.Context(source) != input.SourceContext) return false;
        }
        var documents = await LockDocumentsAsync(context, ct);
        return documents.Any(row => row.Key == ProfileProvider.Key && row.Version == input.ProfileVersion)
            && documents.Any(row => row.Key == FactsProvider.Key && row.Version == input.FactsVersion);
    }
}
