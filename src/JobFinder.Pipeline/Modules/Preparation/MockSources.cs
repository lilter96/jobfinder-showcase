using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class MockSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, LearningProgressSources learning,
    JobFinderLlm llm, Notifier notifier, TimeProvider time)
{
    public async Task<MockStartSnapshot> CaptureStartAsync(InterviewKind kind, long? vacancyId, CancellationToken ct, long? interviewId = null, bool hr = false)
    {
        if (!Enum.IsDefined(kind) || vacancyId is <= 0 || interviewId is <= 0 || hr && interviewId is not null) throw new UnsupportedMockPreparationException();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var raw = await context.Settings.Where(row => row.Key == ConversationStore.Key).Select(row => row.Value).SingleOrDefaultAsync(ct);
        var previous = ReadDialogue(raw);
        if (previous is not null && previous.Mode != MockInterviews.Mode) throw new InvalidOperationException("Сначала завершите текущий диалог через /stop.");
        MockSessionSnapshot? old = null;
        if (previous is not null)
        {
            var source = await context.MockSessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == previous.EntityId, ct) ?? throw new UnsupportedMockPreparationException();
            await ValidateHistoryAsync(context, source, ct);
            if (source.Status != MockStatus.Active || source.Current != previous.Step || source.PreparationRequestId != previous.MockRequestId
                || (source.PreparationRequestId is not null) != (previous.MockPending == true)) throw new UnsupportedMockPreparationException();
            old = MockSessionSnapshot.Capture(source);
        }
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        var snapshot = new MockStartSnapshot { IntentId = Guid.NewGuid(), DialogueJson = raw, Previous = old,
            Profile = profile is null ? new() : ProfileProvider.FromJson(profile.Content), ProfileVersion = profile?.Version ?? 0,
            FactsVersion = await context.Documents.Where(row => row.Key == FactsProvider.Key).Select(row => (long?)row.Version).SingleOrDefaultAsync(ct) ?? 0,
            Kind = kind, InterviewId = interviewId, Hr = hr,
            Stories = await StoriesAsync(context, ct) };
        if (interviewId is { } interview)
        {
            var source = await context.Interviews.AsNoTracking().Include(row => row.Application).ThenInclude(row => row!.Vacancy)
                .SingleOrDefaultAsync(row => row.Id == interview, ct) ?? throw new InvalidOperationException("Интервью не найдено.");
            if (source.Application is null) throw new InvalidOperationException("Интервью не связано с вакансией.");
            vacancyId = source.Application.VacancyId;
            snapshot.Kind = source.Kind;
            snapshot.InterviewRevision = source.PreparationRevision;
            snapshot.InterviewApplicationId = source.ApplicationId;
            snapshot.PreparationJson = source.PackJson;
            var pack = InterviewPrep.Read(source.PackJson);
            if (pack is not { Version: InterviewCopilot.CurrentVersion }) throw new InvalidOperationException("Подготовка к интервью ещё не собрана.");
            var learned = await learning.ReadAsync(InterviewProgress.Scope(interview), ct);
            snapshot.Turns = RehearsalTurns(learned.Scope, pack.Preparation, learned.Learned);
            snapshot.Header = RehearsalHeader("Прогон ответов", pack.Preparation, learned.Learned, snapshot.Turns.Count);
        }
        if (hr)
        {
            snapshot.PreparationJson = await context.Settings.Where(row => row.Key == HrPlaybookService.LatestKey).Select(row => row.Value).SingleOrDefaultAsync(ct);
            var playbook = snapshot.PreparationJson is null ? null : JsonSerializer.Deserialize(snapshot.PreparationJson, InterviewJsonContext.Default.HrPlaybook);
            if (playbook is null) throw new InvalidOperationException("HR-ответы ещё не собраны.");
            var items = playbook.Items.Select(entry => entry.Item).ToList();
            var learned = await learning.ReadAsync(InterviewProgress.HrScope, ct);
            snapshot.Turns = RehearsalTurns(learned.Scope, items, learned.Learned);
            snapshot.Header = RehearsalHeader("Прогон HR-ответов", items, learned.Learned, snapshot.Turns.Count);
        }
        if (vacancyId is { } vacancy)
        {
            var source = await context.Vacancies.AsNoTracking().Include(row => row.Company).Include(row => row.Sources).SingleOrDefaultAsync(row => row.Id == vacancy, ct)
                ?? throw new InvalidOperationException("Вакансия не найдена.");
            snapshot.Vacancy = AssessmentVacancy.From(source);
            if (snapshot.Header.Length > 0) snapshot.Header += "Вакансия: " + TextUtil.EscapeHtml(source.CompanyName ?? "") + " — " + TextUtil.EscapeHtml(source.Title) + "\n\n";
        }
        if (snapshot.Header.Length == 0) snapshot.Header = $"🎤 <b>Тренировка: {CareerCards.InterviewLabel(kind)}</b>"
            + (snapshot.Vacancy is null ? "" : " · " + TextUtil.EscapeHtml(snapshot.Vacancy.CompanyName ?? "") + " — " + TextUtil.EscapeHtml(snapshot.Vacancy.Title))
            + "\nОтвечайте текстом или голосовым, как на собеседовании. /stop — закончить досрочно.\n\n";
        if (!hr && interviewId is null && profile is null) throw new InvalidOperationException("нет профиля");
        if ((hr || interviewId is not null) && snapshot.Turns.Count == 0) throw new InvalidOperationException("Нет подготовленных ответов для прогона.");
        return snapshot;
    }

    public async Task<MockPublication> BeginAsync(MockStartSnapshot expected, CancellationToken ct)
    {
        expected = JsonSerializer.Deserialize(JsonSerializer.Serialize(expected, MockPreparationJsonContext.Default.MockStartSnapshot), MockPreparationJsonContext.Default.MockStartSnapshot)!;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, ct);
        var key = "mock-start:" + expected.IntentId;
        var known = await context.MockPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
        if (known is not null) { MockPreparationRequests.ReadInput(known); return new(ActionResult.Done("Тренировка уже сохранена."), known.Id); }
        if (!await StartMatchesAsync(context, expected, ct)) return Conflict();
        var row = await DialogueRowAsync(context, ct);
        if (row?.Value != expected.DialogueJson) return Conflict();
        if (expected.Previous is { } old)
        {
            var previous = await SessionRowAsync(context, old.Id, ct);
            if (previous is null || Hash(MockSessionSnapshot.Capture(previous)) != Hash(old)) return Conflict();
            await ValidateHistoryAsync(context, previous, ct);
            await CancelPendingAsync(context, previous, ct);
            var before = Json(MockSessionSnapshot.Capture(previous));
            previous.Status = MockStatus.Abandoned;
            previous.FinishedAt = Now();
            previous.PreparationRequestId = null;
            Advance(context, previous, before, "Replace");
        }
        var source = new MockSession { Kind = expected.Kind, VacancyId = expected.Vacancy?.Id, Status = MockStatus.Active,
            TurnsJson = ConversationStore.Serialize(expected.Turns), CreatedAt = Now(), Revision = 1 };
        context.MockSessions.Add(source);
        await notifier.SaveJoinedAsync(context, outbox, ct);
        var dialogue = new ConversationState(MockInterviews.Mode, source.Id, 0, expected.Turns.FirstOrDefault()?.Question, [], null) { Id = Guid.NewGuid() };
        if (expected.Hr || expected.InterviewId is not null)
        {
            SetDialogue(context, row, dialogue);
            context.MockSessionRevisions.Add(History(source, null, "BeginRehearsal"));
            notifier.Add(context, NotificationKind.Info, expected.Header + MockPresentation.Question(expected.Turns, 0), MockPresentation.Buttons(source));
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return new(ActionResult.Done("Прогон сохранён; вопрос придёт в бот."));
        }
        var operation = await llm.ForOperationAsync(ct);
        var input = new MockPreparationInput { Kind = MockPreparationKind.Questions, Start = expected, CapturedAt = Now(), Operation = operation.CaptureOperation() };
        source.PreparationRequestId = input.Operation.OperationId;
        dialogue = dialogue with { MockRequestId = source.PreparationRequestId, MockPending = true };
        input.Source = MockSessionSnapshot.Capture(source);
        input.Waiting = dialogue;
        input.Recipe = MockPreparationSteps.Recipe(operation, input);
        var root = MockPreparationRequests.Root(input, key);
        context.MockPreparationRequests.Add(root);
        SetDialogue(context, row, dialogue);
        context.MockSessionRevisions.Add(History(source, null, "BeginQuestions"));
        notifier.Add(context, NotificationKind.Info, "⏳ Тренировка сохранена. Готовлю вопросы; /stop отменяет подготовку.", MockPresentation.Buttons(source));
        await MockPreparationRequests.PublishAsync(outbox, root);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new(ActionResult.Done("Тренировка сохранена; вопросы придут в бот."), root.Id);
    }

    public async Task<MockPublication> AnswerAsync(ConversationState expected, string answer, CancellationToken ct)
    {
        expected = ConversationStore.Freeze(expected);
        answer = answer.Trim();
        if (!ValidDialogue(expected) || string.IsNullOrWhiteSpace(answer)) return Conflict();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, ct);
        var key = "mock-review:" + Fingerprint.Of(ConversationStore.Capture(expected), answer);
        var old = await context.MockPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
        if (old is not null) { MockPreparationRequests.ReadInput(old); return new(ActionResult.Done("Ответ уже сохранён."), old.Id); }
        var row = await DialogueRowAsync(context, ct);
        if (row?.Value != ConversationStore.Capture(expected)) return Conflict();
        var source = await SessionRowAsync(context, expected.EntityId, ct);
        if (source is null) return Conflict();
        await ValidateHistoryAsync(context, source, ct);
        if (expected.MockPending == true || source.PreparationRequestId is not null) return new(ActionResult.Fail("Разбор ещё выполняется; исходный ответ сохранён."), source.PreparationRequestId);
        var turns = ValidTurns(source);
        if (source.Status != MockStatus.Active || expected.Step != source.Current || source.Current >= turns.Count || expected.Subject != turns[source.Current].Question
            || turns[source.Current].Answer is not null) return Conflict();
        LearningItemSnapshot? item = null;
        if (InterviewProgress.TryParse(turns[source.Current].ItemKey, out var learningScope, out var learningId)) item = (await learning.ReadAsync(learningScope, ct)).Item(learningId);
        var before = Json(MockSessionSnapshot.Capture(source));
        turns[source.Current].Answer = answer;
        source.TurnsJson = ConversationStore.Serialize(turns);
        var operation = await llm.ForOperationAsync(ct);
        source.PreparationRequestId = operation.CaptureOperation().OperationId;
        source.Revision = checked(source.Revision + 1);
        var waiting = expected with { MockRequestId = source.PreparationRequestId, MockPending = true };
        var input = new MockPreparationInput { Kind = MockPreparationKind.Review, Source = MockSessionSnapshot.Capture(source), Waiting = waiting,
            Answer = answer, Learning = item, CapturedAt = Now(), Operation = operation.CaptureOperation() };
        input.Recipe = MockPreparationSteps.Recipe(operation, input);
        var root = MockPreparationRequests.Root(input, key);
        context.MockPreparationRequests.Add(root);
        context.MockSessionRevisions.Add(History(source, before, "AdmitReview"));
        SetDialogue(context, row, waiting);
        notifier.Add(context, NotificationKind.Info, "⏳ Ответ сохранён. Разбор продолжится после перезапуска; /stop завершает тренировку.", MockPresentation.Buttons(source));
        await MockPreparationRequests.PublishAsync(outbox, root);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new(ActionResult.Done("Ответ сохранён; разбор придёт в бот."), root.Id);
    }

    public async Task<MockPublication> FinishAsync(ConversationState expected, CancellationToken ct, long? buttonSession = null, int? buttonRevision = null)
    {
        expected = ConversationStore.Freeze(expected);
        if (!ValidDialogue(expected) || buttonSession is not null && (expected.EntityId != buttonSession || buttonRevision is not > 0)) return Conflict();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, ct);
        var row = await DialogueRowAsync(context, ct);
        if (row?.Value != ConversationStore.Capture(expected)) return Conflict();
        var source = await SessionRowAsync(context, expected.EntityId, ct);
        if (source is null || source.Status != MockStatus.Active || buttonSession is not null && source.Revision != buttonRevision) return Conflict();
        await ValidateHistoryAsync(context, source, ct);
        if (source.PreparationRequestId is { } pending)
        {
            var root = await context.MockPreparationRequests.SingleOrDefaultAsync(request => request.Id == pending, ct) ?? throw new UnsupportedMockPreparationException();
            if (MockPreparationRequests.ReadInput(root).Kind == MockPreparationKind.Summary && root.Stage is MockPreparationStage.Preparing or MockPreparationStage.Publishing)
                return new(ActionResult.Done("Итог уже готовится; ответы сохранены."), root.Id);
        }
        var before = Json(MockSessionSnapshot.Capture(source));
        await CancelPendingAsync(context, source, ct);
        source.Revision = checked(source.Revision + 1);
        if (!ValidTurns(source).Any(turn => turn.Score is not null))
        {
            source.Status = MockStatus.Abandoned;
            source.FinishedAt = Now();
            source.PreparationRequestId = null;
            context.Settings.Remove(row!);
            context.MockSessionRevisions.Add(History(source, before, "Abandon"));
            notifier.Add(context, NotificationKind.Info, "Тренировка остановлена; сохранённые вопросы и ответы не удалены.");
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return new(ActionResult.Done("Тренировка остановлена."));
        }
        var operation = await llm.ForOperationAsync(ct);
        var child = AdmitSummary(context, source, expected, operation, "", "mock-summary:" + Fingerprint.Of(before, ConversationStore.Capture(expected)));
        SetDialogue(context, row, MockPreparationRequests.ReadInput(child).Waiting);
        context.MockSessionRevisions.Add(History(source, before, "AdmitSummary"));
        notifier.Add(context, NotificationKind.Info, "⏳ Ответы сохранены. Подвожу итог тренировки.");
        await MockPreparationRequests.PublishAsync(outbox, child);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new(ActionResult.Done("Итог сохранён в очереди; разбор придёт в бот."), child.Id);
    }

    internal async Task<bool> PublishJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, MockPreparationInput input, MockPreparationResult result, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("mock publication needs the enrolled source transaction");
        await LockAsync(context, ct);
        if (!await MatchesAsync(context, input, ct)) return false;
        var source = await SessionRowAsync(context, input.Source.Id, ct) ?? throw new UnsupportedMockPreparationException();
        var row = await DialogueRowAsync(context, ct) ?? throw new UnsupportedMockPreparationException();
        var before = Json(MockSessionSnapshot.Capture(source));
        var dialogue = input.Waiting with { MockPending = null, MockRequestId = null };
        source.PreparationRequestId = null;
        source.Revision = checked(source.Revision + 1);
        if (input.Kind == MockPreparationKind.Questions)
        {
            source.TurnsJson = ConversationStore.Serialize(result.Questions!);
            dialogue = dialogue with { Subject = result.Questions![0].Question };
            SetDialogue(context, row, dialogue);
            notifier.Add(context, NotificationKind.Info, input.Start!.Header + MockPresentation.Question(result.Questions!, 0), MockPresentation.Buttons(source));
        }
        else if (input.Kind == MockPreparationKind.Review)
        {
            var turns = ValidTurns(source);
            var conflict = false;
            if (input.Learning is { } item) conflict = !(await learning.PublishJoinedAsync(context, item,
                result.Review!.Score >= InterviewProgress.KnownScore ? InterviewProgress.Known : InterviewProgress.Again, null, null, ct)).Result.Ok;
            var current = turns[source.Current];
            current.Score = result.Review!.Score;
            current.Feedback = MockPresentation.Feedback(result.Review, conflict);
            source.Current++;
            source.TurnsJson = ConversationStore.Serialize(turns);
            var prefix = MockPresentation.Reviewed(current);
            if (source.Current == turns.Count)
            {
                var operation = llm.RestoreOperation(input.Operation with { OperationId = Guid.NewGuid() });
                var child = AdmitSummary(context, source, dialogue, operation, prefix, "mock-summary-parent:" + input.Operation.OperationId);
                SetDialogue(context, row, MockPreparationRequests.ReadInput(child).Waiting);
                await MockPreparationRequests.PublishAsync(outbox, child);
            }
            else
            {
                dialogue = dialogue with { Step = source.Current, Subject = turns[source.Current].Question };
                SetDialogue(context, row, dialogue);
                notifier.Add(context, NotificationKind.Info, CareerCards.Truncate(prefix + MockPresentation.Question(turns, source.Current)), MockPresentation.Buttons(source));
            }
        }
        else
        {
            source.Status = MockStatus.Finished;
            source.FinishedAt = Now();
            source.Summary = $"{result.Summary!.Score}/5. {result.Summary.Verdict}\nФокус: {string.Join("; ", result.Summary.Focus)}";
            context.Settings.Remove(row);
            notifier.Add(context, NotificationKind.Info, MockPresentation.Summary(input, result.Summary));
        }
        context.MockSessionRevisions.Add(History(source, before, "Publish" + input.Kind));
        return true;
    }

    private MockPreparationRequest AdmitSummary(JobFinderDbContext context, MockSession source, ConversationState dialogue, JobFinderLlm operation, string prefix, string key)
    {
        source.PreparationRequestId = operation.CaptureOperation().OperationId;
        var input = new MockPreparationInput { Kind = MockPreparationKind.Summary, Source = MockSessionSnapshot.Capture(source),
            Waiting = dialogue with { MockRequestId = source.PreparationRequestId, MockPending = true, Step = source.Current, Subject = null },
            Prefix = prefix, CapturedAt = Now(), Operation = operation.CaptureOperation() };
        input.Recipe = MockPreparationSteps.Recipe(operation, input);
        var root = MockPreparationRequests.Root(input, key);
        context.MockPreparationRequests.Add(root);
        return root;
    }

    internal static async Task<bool> MatchesAsync(JobFinderDbContext context, MockPreparationInput input, CancellationToken ct)
    {
        var source = await SessionRowAsync(context, input.Source.Id, ct);
        if (source is null || source.Status != MockStatus.Active || Hash(MockSessionSnapshot.Capture(source)) != Hash(input.Source)) return false;
        await ValidateHistoryAsync(context, source, ct);
        var row = await DialogueRowAsync(context, ct);
        return row?.Value == ConversationStore.Capture(input.Waiting) && (input.Start is null || await StartMatchesAsync(context, input.Start, ct, dialogue: false));
    }

    private static async Task<bool> StartMatchesAsync(JobFinderDbContext context, MockStartSnapshot start, CancellationToken ct, bool dialogue = true)
    {
        if (start.IntentId == Guid.Empty || start.Profile is null || start.Turns is null || !Enum.IsDefined(start.Kind)) return false;
        var versions = await context.Documents.Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key).Select(row => new { row.Key, row.Version, row.Content }).ToListAsync(ct);
        var profile = versions.SingleOrDefault(row => row.Key == ProfileProvider.Key);
        if ((profile?.Version ?? 0) != start.ProfileVersion || (versions.SingleOrDefault(row => row.Key == FactsProvider.Key)?.Version ?? 0) != start.FactsVersion
            || CanonicalJson.Hash(ProfileProvider.ToJson(profile is null ? new() : ProfileProvider.FromJson(profile.Content))) != CanonicalJson.Hash(ProfileProvider.ToJson(start.Profile))
            || await StoriesAsync(context, ct) != start.Stories) return false;
        if (start.Vacancy is { } vacancy)
        {
            var source = await context.Vacancies.FromSqlInterpolated($"SELECT * FROM vacancies WHERE id = {vacancy.Id} FOR SHARE").AsNoTracking().Include(row => row.Company).Include(row => row.Sources).SingleOrDefaultAsync(ct);
            if (source is null || CanonicalJson.Hash(JsonSerializer.Serialize(AssessmentVacancy.From(source), MockPreparationJsonContext.Default.AssessmentVacancy))
                != CanonicalJson.Hash(JsonSerializer.Serialize(vacancy, MockPreparationJsonContext.Default.AssessmentVacancy))) return false;
        }
        if (start.InterviewId is { } interview)
        {
            var source = await context.Interviews.FromSqlInterpolated($"SELECT * FROM interviews WHERE id = {interview} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
            if (source is null || source.PackJson != start.PreparationJson || source.PreparationRevision != start.InterviewRevision
                || source.ApplicationId != start.InterviewApplicationId || source.Kind != start.Kind) return false;
        }
        if (start.Hr && await context.Settings.FromSqlInterpolated($"SELECT * FROM settings WHERE key = {HrPlaybookService.LatestKey} FOR SHARE").AsNoTracking().Select(row => row.Value).SingleOrDefaultAsync(ct) != start.PreparationJson) return false;
        if (start.Hr || start.InterviewId is not null)
        {
            var items = start.Hr ? JsonSerializer.Deserialize(start.PreparationJson!, InterviewJsonContext.Default.HrPlaybook)?.Items.Select(entry => entry.Item).ToList()
                : InterviewPrep.Read(start.PreparationJson)?.Preparation;
            var scope = start.Hr ? InterviewProgress.HrScope : InterviewProgress.Scope(start.InterviewId!.Value);
            if (items is null || start.Turns.Count is < 1 or > MockInterviews.RehearsalSize || start.Turns.Select(turn => turn.ItemKey).Distinct().Count() != start.Turns.Count
                || start.Turns.Any(turn => !items.Any(item => item.HasSpeech && InterviewProgress.ItemKey(scope, item.Q.Id) == turn.ItemKey
                    && item.Q.Question == turn.Question && item.Q.Tests == turn.Focus && turn.Ideal == Ideal(item)
                    && turn.Answer is null && turn.Score is null && turn.Feedback is null))) return false;
        }
        return !dialogue || (await DialogueRowAsync(context, ct))?.Value == start.DialogueJson;
    }

    private async Task CancelPendingAsync(JobFinderDbContext context, MockSession source, CancellationToken ct)
    {
        if (source.PreparationRequestId is not { } id) return;
        var root = await context.MockPreparationRequests.FromSqlInterpolated($"SELECT * FROM mock_preparation_requests WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new UnsupportedMockPreparationException();
        if (root.SessionId != source.Id || root.Stage == MockPreparationStage.Completed) throw new UnsupportedMockPreparationException();
        root.Stage = MockPreparationStage.Cancelled;
        root.CompletedAt = Now();
        root.RetryAt = null;
        root.ClaimEnvelopeId = null;
        root.Error = "cancelled by captured mock source control; answers retained";
    }

    internal static Task LockAsync(JobFinderDbContext context, CancellationToken ct) => InterviewPreparationCapture.LockCandidateAsync(context, ct);
    private static Task<AppSetting?> DialogueRowAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Settings.FromSqlInterpolated($"SELECT * FROM settings WHERE key = {ConversationStore.Key} FOR UPDATE").SingleOrDefaultAsync(ct);
    private static Task<MockSession?> SessionRowAsync(JobFinderDbContext context, long id, CancellationToken ct) =>
        context.MockSessions.FromSqlInterpolated($"SELECT * FROM mock_sessions WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
    private static Task<string> StoriesAsync(JobFinderDbContext context, CancellationToken ct) => StoriesTextAsync(context, ct);
    private static async Task<string> StoriesTextAsync(JobFinderDbContext context, CancellationToken ct) =>
        ResumeTailor.StoriesText(await context.Stories.AsNoTracking().Where(row => row.Confirmed).OrderBy(row => row.Id).ToListAsync(ct));
    private static ConversationState? ReadDialogue(string? raw)
    {
        if (raw is null) return null;
        var state = JsonSerializer.Deserialize(raw, ConversationJsonContext.Default.ConversationState);
        if (state is null || state.Id == Guid.Empty || string.IsNullOrWhiteSpace(state.Mode)) throw new UnsupportedMockPreparationException();
        return state;
    }
    internal static bool ValidDialogue(ConversationState state) => state.Id != Guid.Empty && state.Mode == MockInterviews.Mode && state.EntityId > 0 && state.Step >= 0;
    internal static List<MockTurn> ValidTurns(MockSession source)
    {
        var turns = ConversationStore.Turns(source.TurnsJson);
        if (!Enum.IsDefined(source.Status) || !Enum.IsDefined(source.Kind) || source.Current < 0 || source.Current > turns.Count || source.Revision < 0
            || turns.Any(turn => turn is null || string.IsNullOrWhiteSpace(turn.Question) || string.IsNullOrWhiteSpace(turn.Ideal) || turn.Score is < 1 or > 5
                || turn.Score is not null && (turn.Answer is null || turn.Feedback is null))) throw new UnsupportedMockPreparationException();
        return turns;
    }
    private static async Task ValidateHistoryAsync(JobFinderDbContext context, MockSession source, CancellationToken ct)
    {
        ValidTurns(source);
        var last = await context.MockSessionRevisions.AsNoTracking().Where(row => row.SessionId == source.Id).OrderByDescending(row => row.Revision).FirstOrDefaultAsync(ct);
        if (source.Revision == 0 ? last is not null || source.PreparationRequestId is not null
            : last is null || last.Revision != source.Revision || Hash(JsonSerializer.Deserialize(last.AfterJson, MockPreparationJsonContext.Default.MockSessionSnapshot) ?? throw new UnsupportedMockPreparationException()) != Hash(MockSessionSnapshot.Capture(source)))
            throw new UnsupportedMockPreparationException();
    }
    private void SetDialogue(JobFinderDbContext context, AppSetting? row, ConversationState dialogue)
    {
        if (row is null) { row = new() { Key = ConversationStore.Key, Value = "" }; context.Settings.Add(row); }
        row.Value = ConversationStore.Capture(dialogue);
        row.UpdatedAt = Now();
    }
    private void Advance(JobFinderDbContext context, MockSession source, string? before, string change)
    {
        source.Revision = checked(source.Revision + 1);
        context.MockSessionRevisions.Add(History(source, before, change));
    }
    private MockSessionRevision History(MockSession source, string? before, string change) => new()
    {
        SessionId = source.Id, Revision = source.Revision, BeforeJson = before, AfterJson = Json(MockSessionSnapshot.Capture(source)), Change = change, RecordedAt = Now(),
    };
    private DateTimeOffset Now() { var value = time.GetUtcNow(); return value.AddTicks(-(value.Ticks % 10)); }
    internal static string Json(MockSessionSnapshot snapshot) => JsonSerializer.Serialize(snapshot, MockPreparationJsonContext.Default.MockSessionSnapshot);
    internal static string Hash(MockSessionSnapshot snapshot) => CanonicalJson.Hash(Json(snapshot with { TurnsJson = CanonicalJson.Normalize(snapshot.TurnsJson) }));
    private static MockPublication Conflict() => new(ActionResult.Fail("Тренировка или диалог изменились; исходный запрос сохранён, обновите данные явно."));
    private static List<MockTurn> RehearsalTurns(string scope, IEnumerable<PrepItem> items, Dictionary<string, string> learned) => items.Where(item => item.HasSpeech)
        .OrderBy(item => learned.GetValueOrDefault(item.Q.Id) == InterviewProgress.Known ? 1 : 0)
        .ThenBy(item => CopilotRules.Rank(item.Q.Priority)).ThenBy(item => CopilotRules.Rank(item.Q.Likelihood)).Take(MockInterviews.RehearsalSize)
        .Select(item => new MockTurn { Question = item.Q.Question, Focus = item.Q.Tests,
            Ideal = Ideal(item),
            ItemKey = InterviewProgress.ItemKey(scope, item.Q.Id) }).ToList();
    private static string RehearsalHeader(string title, IReadOnlyList<PrepItem> items, Dictionary<string, string> learned, int count) =>
        $"🧠 <b>{title}</b>\nВыучено {items.Count(item => learned.GetValueOrDefault(item.Q.Id) == InterviewProgress.Known)} из {items.Count}. {count} вопросов, сначала невыученные. "
        + "Отвечайте голосом, как на звонке: я сверю с подготовленным ответом и покажу его. Ответ на 4+ отмечает вопрос выученным. /stop — закончить.\n\n";
    private static string Ideal(PrepItem item) => string.Join("\n\n", new[] { item.A!.Start, item.A.Core, item.A.Extended }.Where(text => !string.IsNullOrWhiteSpace(text)));
}
