using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class InterviewSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    InterviewPreparationRequests requests, CandidateDocuments candidate, JobFinderLlm llm, Notifier notifier, TimeProvider time)
{
    public async Task<InterviewEditSnapshot?> CaptureAsync(long id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var source = await context.Interviews.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        return source is null ? null : InterviewEditSnapshot.Capture(source);
    }

    internal async Task<Interview> EnsureAsync(JobFinderDbContext context, IDbContextOutbox outbox, long applicationId, InterviewKind kind,
        DateTimeOffset? at, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || applicationId <= 0 || !Enum.IsDefined(kind))
            throw new InvalidOperationException("interview scheduling requires a persisted application and its transaction");
        if (context.ChangeTracker.Entries<Interview>().Any(entry => entry.State == EntityState.Added && entry.Entity.ApplicationId == applicationId))
            throw new InvalidOperationException("new interviews must be created by their scheduling owner");
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM applications WHERE id = {applicationId} FOR UPDATE", ct);
        if (!await context.Applications.AnyAsync(row => row.Id == applicationId, ct)) throw new InterviewEditConflictException();
        var excluded = context.ChangeTracker.Entries<Interview>().Where(entry => entry.Entity.ApplicationId == applicationId
            && entry.Entity.Status != InterviewStatus.Scheduled).Select(entry => entry.Entity.Id).ToList();
        var persisted = await context.Interviews.FromSqlInterpolated($"SELECT * FROM interviews WHERE application_id = {applicationId} AND kind = {kind.ToString()} AND status = 'Scheduled' FOR UPDATE")
            .AsNoTracking().Where(row => !excluded.Contains(row.Id)).OrderBy(row => row.Id).FirstOrDefaultAsync(ct);
        var source = persisted is null ? null : context.Interviews.Local.SingleOrDefault(row => row.Id == persisted.Id);
        if (source is not null)
        {
            var original = (Interview)context.Entry(source).OriginalValues.ToObject();
            if (!InterviewEditSnapshot.Capture(original).Matches(persisted!)) throw new InterviewEditConflictException();
        }
        else if (persisted is not null)
        {
            source = persisted;
            context.Attach(source);
        }
        var created = source is null;
        if (source is null)
        {
            source = new Interview { ApplicationId = applicationId, Kind = kind, Status = InterviewStatus.Scheduled, ScheduledAt = Normalize(at), CreatedAt = time.GetUtcNow() };
            context.Interviews.Add(source);
            await notifier.SaveJoinedAsync(context, outbox, ct);
        }
        else if (at is { } date && source.ScheduledAt != Normalize(date))
        {
            source.ScheduledAt = Normalize(date);
            source.Reminded = false;
            source.DebriefAsked = false;
            await InvalidateAsync(context, source, ct);
        }
        else if (source.PreparationRequestId is not null || source.PackJson is not null) return source;
        var operation = await llm.ForOperationAsync(ct);
        await requests.QueueAsync(context, outbox, source, operation, ct, force: false);
        notifier.Add(context, NotificationKind.Info, $"📅 Интервью #{source.Id} {(created ? "создано" : "обновлено")}", entityId: source.Id);
        return source;
    }

    internal async Task<Interview> CompleteDebriefAsync(JobFinderDbContext context, InterviewEditSnapshot snapshot, string debrief, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("debrief requires the source transaction");
        var source = await CurrentAsync(context, snapshot, ct);
        source.Debrief = debrief;
        source.Status = InterviewStatus.Done;
        source.DebriefAsked = true;
        await InvalidateAsync(context, source, ct, clearPack: false);
        return source;
    }

    public async Task<InterviewPositionSnapshot?> CapturePositionAsync(long id, string questionId, CancellationToken ct)
    {
        var snapshots = await CapturePositionsAsync(id, [questionId], ct);
        return snapshots.GetValueOrDefault(questionId);
    }

    public async Task<IReadOnlyDictionary<string, InterviewPositionSnapshot>> CapturePositionsAsync(long id,
        IReadOnlyCollection<string> questionIds, CancellationToken ct)
    {
        if (questionIds.Count > 100) throw new ArgumentException("слишком много вопросов", nameof(questionIds));
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await InterviewPreparationCapture.LockAsync(context, id, ct);
        var source = await context.Interviews.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        var snapshots = new Dictionary<string, InterviewPositionSnapshot>();
        if (source is not { Status: InterviewStatus.Scheduled, PackJson: not null }) return snapshots;
        var facts = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == FactsProvider.Key, ct);
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        if (facts is null || profile is null) return snapshots;
        var packHash = CanonicalJson.Hash(source.PackJson);
        foreach (var questionId in questionIds.Distinct())
        {
            if (string.IsNullOrWhiteSpace(questionId)) continue;
            var question = Question(source.PackJson, questionId);
            if (question is null) continue;
            snapshots[questionId] = new InterviewPositionSnapshot
            {
                Interview = InterviewEditSnapshot.Capture(source), ProfileVersion = profile.Version, FactsVersion = facts.Version,
                PackHash = packHash, Question = question,
            };
        }
        return snapshots;
    }

    public Task<ActionResult> SetTimeAsync(InterviewEditSnapshot snapshot, DateTimeOffset? at, CancellationToken ct, ConversationState? prompt = null)
    {
        Validate(snapshot);
        return EditAsync(snapshot, snapshot.Source.Kind, snapshot.Source.DurationMinutes, at, ct, prompt);
    }

    public Task<ActionResult> EditAsync(InterviewEditSnapshot snapshot, InterviewKind kind, int durationMinutes, DateTimeOffset? at, CancellationToken ct,
        ConversationState? prompt = null)
    {
        if (!Enum.IsDefined(kind) || durationMinutes is < 15 or > 480) throw new ArgumentException("некорректный вид или длительность интервью");
        return ChangeAsync(snapshot, source =>
        {
            var normalized = Normalize(at);
            if (source.Kind == kind && source.DurationMinutes == durationMinutes && source.ScheduledAt == normalized) return false;
            source.Kind = kind;
            source.DurationMinutes = durationMinutes;
            source.ScheduledAt = normalized;
            source.Reminded = false;
            source.DebriefAsked = false;
            return true;
        }, ct, prompt: prompt is null ? null : ConversationStore.Freeze(prompt));
    }

    public Task<ActionResult> SetStatusAsync(InterviewEditSnapshot snapshot, InterviewStatus status, CancellationToken ct)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("некорректный статус интервью", nameof(status));
        return ChangeAsync(snapshot, source =>
        {
            if (source.Status == status) return false;
            source.Status = status;
            return true;
        }, ct, preserveClosedPack: true);
    }

    public Task<ActionResult> RebuildAsync(InterviewEditSnapshot snapshot, CancellationToken ct) => ChangeAsync(snapshot, source =>
    {
        source.Status = InterviewStatus.Scheduled;
        return true;
    }, ct);

    public async Task<ActionResult> SavePositionAsync(InterviewPositionSnapshot snapshot, string text, CancellationToken ct)
    {
        if (snapshot.Version != 1 || snapshot.ProfileVersion <= 0 || snapshot.FactsVersion <= 0 || snapshot.Question is null || string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("позиция требует захваченного вопроса, версии фактов и непустого ответа");
        Validate(snapshot.Interview);
        using var stage = Telemetry.StartStage("position", "interview").Tag("interview.id", snapshot.Interview.Source.Id)
            .Tag("candidate.expected_version", snapshot.FactsVersion);
        var operation = await llm.ForOperationAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var source = await CurrentAsync(context, snapshot.Interview, ct);
        if (source.Status != InterviewStatus.Scheduled || source.PackJson is null || CanonicalJson.Hash(source.PackJson) != snapshot.PackHash
            || Question(source.PackJson, snapshot.Question.Id) is not { } currentQuestion
            || QuestionHash(currentQuestion) != QuestionHash(snapshot.Question)) throw new InterviewEditConflictException();
        var document = await context.Documents.SingleOrDefaultAsync(row => row.Key == FactsProvider.Key, ct);
        if (document is null || document.Version != snapshot.FactsVersion) throw new CandidateConflictException(FactsProvider.Key, snapshot.FactsVersion);
        var profileVersion = await context.Documents.Where(row => row.Key == ProfileProvider.Key).Select(row => (long?)row.Version).SingleOrDefaultAsync(ct);
        if (profileVersion != snapshot.ProfileVersion) throw new CandidateConflictException(ProfileProvider.Key, snapshot.ProfileVersion);
        var facts = JsonSerializer.Deserialize(document.Content, FactsJsonContext.Default.CandidateFacts) ?? throw new JsonException("пустые факты");
        var key = HrPlaybookService.PositionFactKey(currentQuestion.Id);
        if (facts.Get(key) is { } fact)
        {
            fact.Question = currentQuestion.Question;
            fact.Answer = text.Trim();
            fact.Confirmed = true;
            fact.Source = "вы";
        }
        else facts.Facts.Add(new CandidateFact { Key = key, Question = currentQuestion.Question, Answer = text.Trim(), Confirmed = true, Source = "вы" });
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await candidate.PublishFactsAsync(context, outbox, facts, snapshot.FactsVersion, "interview-position", ct);
        await InvalidateAsync(context, source, ct);
        await requests.QueueAsync(context, outbox, source, operation, ct, force: false);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("позиция сохранена; подготовка обновляется");
    }

    private async Task<ActionResult> ChangeAsync(InterviewEditSnapshot snapshot, Func<Interview, bool> change, CancellationToken ct, bool preserveClosedPack = false,
        ConversationState? prompt = null)
    {
        Validate(snapshot);
        var operation = await llm.ForOperationAsync(ct);
        using var stage = Telemetry.StartStage("change", "interview").Tag("interview.id", snapshot.Source.Id).Tag("interview.revision", snapshot.Source.Revision);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var source = await CurrentAsync(context, snapshot, ct);
        if (prompt is not null) await CareerPromptSources.CompleteJoinedAsync(context, prompt, snapshot, CareerChat.TimeMode, ct);
        var changed = change(source);
        if (!changed && prompt is null) return ActionResult.Done("изменений нет");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        if (changed)
        {
            await InvalidateAsync(context, source, ct, clearPack: !preserveClosedPack || source.Status == InterviewStatus.Scheduled);
            if (source.Status == InterviewStatus.Scheduled) await requests.QueueAsync(context, outbox, source, operation, ct, force: false);
        }
        notifier.Add(context, NotificationKind.Info, "📅 Интервью #" + source.Id + (changed ? " обновлено" : ": время подтверждено"), entityId: source.Id);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("интервью обновлено");
    }

    private static async Task<Interview> CurrentAsync(JobFinderDbContext context, InterviewEditSnapshot snapshot, CancellationToken ct)
    {
        Validate(snapshot);
        await InterviewPreparationCapture.LockAsync(context, snapshot.Source.Id, ct);
        var source = await context.Interviews.SingleOrDefaultAsync(row => row.Id == snapshot.Source.Id, ct);
        if (source is null || !snapshot.Matches(source)) throw new InterviewEditConflictException();
        return source;
    }

    private async Task InvalidateAsync(JobFinderDbContext context, Interview source, CancellationToken ct, bool clearPack = true)
    {
        if (source.PreparationRequestId is { } id)
        {
            var previous = context.InterviewPreparationRequests.Local.FirstOrDefault(row => row.Id == id)
                ?? await context.InterviewPreparationRequests.SingleOrDefaultAsync(row => row.Id == id, ct);
            if (previous is not null && previous.InterviewId == source.Id && previous.Revision == source.PreparationRevision
                && previous.Stage is not (InterviewPreparationStage.Completed or InterviewPreparationStage.Cancelled or InterviewPreparationStage.Failed))
            {
                previous.Stage = InterviewPreparationStage.Cancelled;
                previous.CompletedAt = time.GetUtcNow();
                previous.Error = "interview source changed";
                previous.ClaimEnvelopeId = null;
            }
        }
        source.PreparationRequestId = null;
        source.PreparationRevision++;
        if (clearPack) { source.PackJson = null; source.PackPdfPath = null; }
    }

    private static PlannedQuestion? Question(string json, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var pack = InterviewPrep.Read(json);
        if (pack is null || pack.Version != InterviewCopilot.CurrentVersion || pack.Preparation is null) return null;
        var questions = pack.Preparation.Where(item => item?.Q is not null).Select(item => item.Q)
            .Concat(pack.Salary is { Q: not null } salary ? [salary.Q] : []).Where(question => question.Id == id).ToList();
        return questions.Count == 1 ? questions[0] : null;
    }

    private static string QuestionHash(PlannedQuestion question) => CanonicalJson.Hash(JsonSerializer.Serialize(question, InterviewJsonContext.Default.PlannedQuestion));

    private static DateTimeOffset? Normalize(DateTimeOffset? value) => value is { } date ? new DateTimeOffset(date.UtcTicks - date.UtcTicks % 10, TimeSpan.Zero) : null;

    private static void Validate(InterviewEditSnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Source is null || snapshot.Source.Id <= 0) throw new InterviewEditConflictException();
    }
}
