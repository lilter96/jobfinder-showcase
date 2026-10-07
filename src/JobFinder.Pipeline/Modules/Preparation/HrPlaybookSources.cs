using System.Globalization;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
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

public sealed class HrPlaybookSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, JobFinderLlm llm,
    CandidateDocuments candidate, Notifier notifier, PathsOptions paths, TimeProvider time)
{
    internal const string RequestKey = "hr.playbook.request";
    internal const string OwnerKey = "hr.playbook.owner";
    public static bool OwnsKey(string key) => key is HrPlaybookService.LatestKey or HrPlaybookService.RequestedKey or RequestKey or OwnerKey;
    internal static string QuestionHash(HrQuestion question) => CanonicalJson.Hash(JsonSerializer.Serialize(question, InterviewJsonContext.Default.HrQuestion));
    private static string BankHash(HrBank bank) => CanonicalJson.Hash(JsonSerializer.Serialize(bank, InterviewJsonContext.Default.HrBank));

    public async Task<Guid?> RequestAsync(HrBank bank, CancellationToken ct, bool force)
    {
        bank = Freeze(bank);
        var operation = await llm.ForOperationAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await HrPlaybookRequests.LockAsync(context, HrPlaybookService.LatestKey, ct);
        if (!force)
        {
            var desired = await ReadMarkerAsync(context, RequestKey, ct);
            if (desired is { } id)
            {
                var root = await context.HrPlaybookRequests.SingleOrDefaultAsync(row => row.Id == id, ct) ?? throw new UnsupportedHrPlaybookException();
                HrPlaybookRequests.ReadInput(root);
                if (root.Stage is HrPlaybookStage.Building or HrPlaybookStage.Publishing) return id;
                if (root.Stage == HrPlaybookStage.Failed) return null;
            }
            if (await ReadMarkerAsync(context, OwnerKey, ct) is { } owner)
            {
                var previous = await context.HrPlaybookRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == owner, ct) ?? throw new UnsupportedHrPlaybookException();
                var input = HrPlaybookRequests.ReadInput(previous);
                if (previous.Stage != HrPlaybookStage.Completed || await ValueAsync(context, HrPlaybookService.LatestKey, ct) is not { } latest
                    || CanonicalJson.Hash(latest) != CanonicalJson.Hash(previous.ResultJson ?? "null")) throw new UnsupportedHrPlaybookException();
                HrPlaybookRequests.ReadResult(previous);
                var flag = await LegacyRequestedAsync(context, ct);
                if (!flag && BankHash(input.Bank) == BankHash(bank) && await CandidateMatchesAsync(context, input, ct)
                    && HrPlaybookPreparationSteps.Recipe(operation, input).Hash == input.Recipe.Hash) return null;
            }
        }
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var admitted = await QueueJoinedAsync(context, outbox, bank, operation, ct);
        if (admitted is null) return null;
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return admitted;
    }

    private async Task<Guid?> QueueJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, HrBank bank, JobFinderLlm operation, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || !ReferenceEquals(outbox.ActiveContext, context)) throw new InvalidOperationException("HR admission requires its enrolled source transaction");
        await HrPlaybookRequests.LockAsync(context, HrPlaybookService.LatestKey, ct);
        await LegacyRequestedAsync(context, ct);
        var profile = await context.Documents.SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        if (profile is null) return null;
        var facts = await context.Documents.SingleOrDefaultAsync(row => row.Key == FactsProvider.Key, ct);
        var value = ProfileProvider.FromJson(profile.Content);
        var capturedAt = time.GetUtcNow();
        var previousJson = await ValueAsync(context, HrPlaybookService.LatestKey, ct);
        if (previousJson is not null && JsonSerializer.Deserialize(previousJson, InterviewJsonContext.Default.HrPlaybook) is not { Items: not null })
            throw new UnsupportedHrPlaybookException();
        var input = new HrPlaybookInput
        {
            ProfileVersion = profile.Version, ProfileJson = profile.Content, FactsVersion = facts?.Version ?? 0,
            FactsJson = facts?.Content ?? JsonSerializer.Serialize(new CandidateFacts().WithStandardQuestions(), FactsJsonContext.Default.CandidateFacts),
            Bank = bank, Evidence = await EvidenceAsync(context, value, ct), Funnel = await FunnelAsync(context, capturedAt, ct),
            PreviousJson = previousJson, CapturedAt = capturedAt, Operation = operation.CaptureOperation(),
        };
        input.Recipe = HrPlaybookPreparationSteps.Recipe(operation, input);
        var root = HrPlaybookRequests.Root(input);
        if (await ReadMarkerAsync(context, RequestKey, ct) is { } oldId)
        {
            var old = await context.HrPlaybookRequests.SingleOrDefaultAsync(row => row.Id == oldId, ct) ?? throw new UnsupportedHrPlaybookException();
            HrPlaybookRequests.ReadInput(old);
            if (old.Stage is HrPlaybookStage.Building or HrPlaybookStage.Publishing)
            {
                old.Stage = HrPlaybookStage.Cancelled;
                old.CompletedAt = capturedAt;
                old.ClaimEnvelopeId = null;
                old.Error = "replaced by a new captured HR request; checkpoints retained";
            }
        }
        context.HrPlaybookRequests.Add(root);
        await SetAsync(context, RequestKey, root.Id.ToString("D"), capturedAt, ct);
        await SetAsync(context, HrPlaybookService.RequestedKey, "True", capturedAt, ct);
        notifier.Add(context, NotificationKind.Info, "📘 Подготовка HR-ответов сохранена; готовые ответы переиспользуются, новые придут отдельно.");
        await HrPlaybookRequests.PublishAsync(outbox, root);
        return root.Id;
    }

    public async Task<bool> RequestedAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        if (await ReadMarkerAsync(context, RequestKey, ct) is not { } id) return await LegacyRequestedAsync(context, ct);
        var root = await context.HrPlaybookRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct) ?? throw new UnsupportedHrPlaybookException();
        HrPlaybookRequests.ReadInput(root);
        return root.Stage is HrPlaybookStage.Building or HrPlaybookStage.Publishing;
    }

    public async Task<HrPlaybook?> LatestAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var json = await ValueAsync(context, HrPlaybookService.LatestKey, ct);
        if (await ReadMarkerAsync(context, OwnerKey, ct) is { } id)
        {
            var root = await context.HrPlaybookRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct) ?? throw new UnsupportedHrPlaybookException();
            var result = HrPlaybookRequests.ReadResult(root);
            if (root.Stage != HrPlaybookStage.Completed || json is null || CanonicalJson.Hash(json) != root.ResultHash) throw new UnsupportedHrPlaybookException();
            return result;
        }
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize(json, InterviewJsonContext.Default.HrPlaybook); }
        catch (JsonException) { return null; }
    }

    internal static async Task<bool> MatchesAsync(JobFinderDbContext context, HrPlaybookInput input, CancellationToken ct) =>
        await ReadMarkerAsync(context, RequestKey, ct) == input.Operation.OperationId
        && await ValueAsync(context, HrPlaybookService.LatestKey, ct) == input.PreviousJson && await CandidateMatchesAsync(context, input, ct);

    private static async Task<bool> CandidateMatchesAsync(JobFinderDbContext context, HrPlaybookInput input, CancellationToken ct)
    {
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        var facts = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == FactsProvider.Key, ct);
        return profile is not null && profile.Version == input.ProfileVersion && CanonicalJson.Hash(profile.Content) == CanonicalJson.Hash(input.ProfileJson)
            && (facts?.Version ?? 0) == input.FactsVersion && (facts is null || CanonicalJson.Hash(facts.Content) == CanonicalJson.Hash(input.FactsJson))
            && await EvidenceAsync(context, ProfileProvider.FromJson(profile.Content), ct) == input.Evidence
            && await FunnelAsync(context, input.CapturedAt, ct) == input.Funnel;
    }

    internal async Task<bool> PublishJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, HrPlaybookInput input, HrPlaybook result, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || !ReferenceEquals(outbox.ActiveContext, context)) throw new InvalidOperationException("HR publication requires its enrolled source transaction");
        var bank = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(paths.Prompts, HrPlaybookService.BankFile), ct), InterviewJsonContext.Default.HrBank)
            ?? throw new UnsupportedHrPlaybookException();
        if (BankHash(bank) != BankHash(input.Bank) || !await MatchesAsync(context, input, ct)) return false;
        var now = time.GetUtcNow();
        await SetAsync(context, HrPlaybookService.LatestKey, JsonSerializer.Serialize(result, InterviewJsonContext.Default.HrPlaybook), now, ct);
        await SetAsync(context, OwnerKey, input.Operation.OperationId.ToString("D"), now, ct);
        await SetAsync(context, HrPlaybookService.RequestedKey, "False", now, ct);
        var reused = result.Items.Count(item => item.Item.Reused);
        if (reused < result.Items.Count)
            notifier.Add(context, NotificationKind.Info, $"📘 <b>HR-ответы обновлены</b>: новых или изменённых {result.Items.Count - reused}, без изменений {reused}; ждут вашего решения {result.Items.Count(item => item.Item.CheckLevel == "blocker")}.\nДашборд → «HR-ответы»: заучивание карточками.",
                [[Callback.Button("🧠 Прогнать HR-ответы", CareerCallback.HrRehearse, 0)]]);
        return true;
    }

    internal static async Task RetireJoinedAsync(JobFinderDbContext context, Guid id, DateTimeOffset at, CancellationToken ct)
    {
        if (await ReadMarkerAsync(context, RequestKey, ct) == id) await SetAsync(context, HrPlaybookService.RequestedKey, "False", at, ct);
    }

    public async Task<IReadOnlyDictionary<string, HrPositionEditSnapshot>> CapturePositionsAsync(IReadOnlyList<HrQuestion> questions, HrBank bank, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await HrPlaybookRequests.LockAsync(context, HrPlaybookService.LatestKey, ct);
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        var facts = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == FactsProvider.Key, ct);
        var snapshots = new Dictionary<string, HrPositionEditSnapshot>(StringComparer.Ordinal);
        if (profile is null) return snapshots;
        foreach (var question in questions)
        {
            var hash = QuestionHash(question);
            if (bank.Questions.Count(current => current.Id == question.Id && QuestionHash(current) == hash) != 1) continue;
            snapshots[question.Id] = new HrPositionEditSnapshot { ProfileVersion = profile.Version, FactsVersion = facts?.Version ?? 0,
                FactsJson = facts?.Content ?? JsonSerializer.Serialize(new CandidateFacts().WithStandardQuestions(), FactsJsonContext.Default.CandidateFacts),
                Question = JsonSerializer.Deserialize(JsonSerializer.Serialize(question, InterviewJsonContext.Default.HrQuestion), InterviewJsonContext.Default.HrQuestion)!, QuestionHash = hash };
        }
        return snapshots;
    }

    public async Task<ActionResult> SavePositionAsync(HrPositionEditSnapshot snapshot, string text, HrBank bank, CancellationToken ct)
    {
        snapshot = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, HrPlaybookJsonContext.Default.HrPositionEditSnapshot), HrPlaybookJsonContext.Default.HrPositionEditSnapshot)!;
        if (snapshot.Version != 1 || snapshot.ProfileVersion <= 0 || snapshot.FactsVersion < 0 || snapshot.Question is null || string.IsNullOrWhiteSpace(text)
            || snapshot.QuestionHash != QuestionHash(snapshot.Question) || bank.Questions.Count(question => question.Id == snapshot.Question.Id
                && QuestionHash(question) == snapshot.QuestionHash) != 1) return ActionResult.Fail("Вопрос изменился: сохраните текст и явно обновите страницу.");
        var operation = await llm.ForOperationAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await HrPlaybookRequests.LockAsync(context, HrPlaybookService.LatestKey, ct);
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == FactsProvider.Key, ct);
        var currentFacts = document?.Content ?? JsonSerializer.Serialize(new CandidateFacts().WithStandardQuestions(), FactsJsonContext.Default.CandidateFacts);
        if (profile?.Version != snapshot.ProfileVersion || (document?.Version ?? 0) != snapshot.FactsVersion
            || CanonicalJson.Hash(currentFacts) != CanonicalJson.Hash(snapshot.FactsJson))
            return ActionResult.Fail("Факты или профиль изменились: текст сохраните, затем явно обновите страницу.");
        var value = JsonSerializer.Deserialize(snapshot.FactsJson, FactsJsonContext.Default.CandidateFacts) ?? throw new UnsupportedHrPlaybookException();
        value.WithStandardQuestions();
        var key = snapshot.Question.Facts.FirstOrDefault(fact => fact != "funnel" && !value.IsConfirmed(fact)) ?? HrPlaybookService.PositionFactKey(snapshot.Question.Id);
        if (value.Get(key) is { } fact) { fact.Answer = text.Trim(); fact.Confirmed = true; fact.Source = "вы"; }
        else value.Facts.Add(new CandidateFact { Key = key, Question = snapshot.Question.Question, Answer = text.Trim(), Confirmed = true, Source = "вы" });
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await candidate.PublishFactsAsync(context, outbox, value, snapshot.FactsVersion, "hr-position", ct);
        await QueueJoinedAsync(context, outbox, Freeze(bank), operation, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("Позиция сохранена; подготовка ответов поставлена в очередь.");
    }

    private static HrBank Freeze(HrBank bank) => JsonSerializer.Deserialize(JsonSerializer.Serialize(bank, InterviewJsonContext.Default.HrBank), InterviewJsonContext.Default.HrBank)
        ?? throw new UnsupportedHrPlaybookException();
    private static Task<string?> ValueAsync(JobFinderDbContext context, string key, CancellationToken ct) => context.Settings.AsNoTracking().Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(ct);
    private static async Task<Guid?> ReadMarkerAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        var value = await ValueAsync(context, key, ct);
        return value is null ? null : Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new UnsupportedHrPlaybookException();
    }
    private static async Task<bool> LegacyRequestedAsync(JobFinderDbContext context, CancellationToken ct)
    {
        var value = await ValueAsync(context, HrPlaybookService.RequestedKey, ct);
        return value is null ? false : bool.TryParse(value, out var requested) ? requested : throw new UnsupportedHrPlaybookException();
    }
    private static async Task SetAsync(JobFinderDbContext context, string key, string value, DateTimeOffset at, CancellationToken ct)
    {
        var row = context.Settings.Local.SingleOrDefault(row => row.Key == key) ?? await context.Settings.SingleOrDefaultAsync(row => row.Key == key, ct);
        if (row is null) { row = new() { Key = key, Value = value, UpdatedAt = at }; context.Settings.Add(row); }
        else { row.Value = value; row.UpdatedAt = at; }
    }
    private static async Task<string> EvidenceAsync(JobFinderDbContext context, CandidateProfile profile, CancellationToken ct) =>
        CopilotRules.CatalogText(CopilotRules.Catalog(profile, await context.Stories.AsNoTracking().Where(story => story.Confirmed).OrderBy(story => story.Id).ToListAsync(ct)));

    internal static async Task<string> FunnelAsync(JobFinderDbContext context, DateTimeOffset capturedAt, CancellationToken ct)
    {
        var since = capturedAt.AddDays(-30);
        var stages = await context.Applications.AsNoTracking().Where(app => app.Status == ApplicationStatus.Sent && app.StageChangedAt >= since && app.Stage != null)
            .GroupBy(app => app.Stage).Select(group => new { Stage = group.Key!.Value, Count = group.Count() }).ToListAsync(ct);
        int Of(params ApplicationStage[] kinds) => stages.Where(stage => kinds.Contains(stage.Stage)).Sum(stage => stage.Count);
        var interviews = await context.Interviews.CountAsync(interview => interview.Status == InterviewStatus.Scheduled, ct);
        var text = new StringBuilder();
        text.Append("За 30 дней: откликов без ответа ").Append(Of(ApplicationStage.Sent, ApplicationStage.Viewed).ToString(CultureInfo.InvariantCulture))
            .Append(", диалогов с работодателем ").Append(Of(ApplicationStage.Replied).ToString(CultureInfo.InvariantCulture))
            .Append(", на этапах интервью ").Append(Of(ApplicationStage.HrScreen, ApplicationStage.TechInterview, ApplicationStage.Final).ToString(CultureInfo.InvariantCulture))
            .Append(", офферов ").Append(Of(ApplicationStage.Offer).ToString(CultureInfo.InvariantCulture))
            .Append(", отказов ").Append(Of(ApplicationStage.Rejected).ToString(CultureInfo.InvariantCulture)).Append(".\n")
            .Append("Запланированных интервью сейчас: ").Append(interviews.ToString(CultureInfo.InvariantCulture)).Append('.');
        return text.ToString();
    }
}
