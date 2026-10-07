using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Market;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class OfferPreparationRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    OfferSources sources, MarketService market, JobFinderLlm llm, IOptions<SalaryOptions> salary, Notifier notifier, TimeProvider time)
{
    public async Task<Guid?> RequestAsync(string text, CancellationToken ct, ConversationState? conversation = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        conversation = conversation is null ? null : ConversationStore.Freeze(conversation);
        if (conversation is not null && (conversation.Id == Guid.Empty || conversation.Mode != OfferAdvisor.Mode)) return null;
        var key = Key(conversation, text, Guid.NewGuid());
        await using (var lookup = await db.CreateDbContextAsync(ct))
        {
            var reusable = await lookup.OfferPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
            if (reusable is not null) { ReadInput(reusable); return reusable.Id; }
        }
        if (conversation?.OfferRequestId is not null) return null;
        var source = await sources.CaptureAsync(text, ct, conversation);
        if (source?.Profile.Reviewed != true)
        {
            // Another producer can commit the same intent between the first lookup and source capture.
            // Return only that retained exact business key; never recapture a newer dialogue to create work.
            await using var lookup = await db.CreateDbContextAsync(ct);
            var admitted = await lookup.OfferPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
            if (admitted is null) return null;
            ReadInput(admitted);
            return admitted.Id;
        }
        var operation = await llm.ForOperationAsync(ct);
        source.PublicationId = operation.CaptureOperation().OperationId;
        var original = source.Conversation;
        if (original is not null) source.Conversation = original with { Draft = text, Step = original.Step + 1, OfferRequestId = source.PublicationId };
        var now = time.GetUtcNow();
        var guessed = OfferSources.Guess(source)?.Vacancy.ToVacancy();
        var view = guessed is null ? default : await market.DescribeAsync(guessed, ct);
        var segment = view.Segment ?? (await market.SnapshotAsync(ct)).Lookup("senior+", "*");
        await using var comparisons = await db.CreateDbContextAsync(ct);
        var others = await comparisons.Offers.AsNoTracking().Where(row => row.CreatedAt >= now.AddDays(-90)).OrderBy(row => row.Id).ToListAsync(ct);
        var input = new OfferPreparationInput
        {
            Source = source, CapturedAt = now, Operation = operation.CaptureOperation(),
            Market = segment is null ? null : new OfferMarketEvidence { Label = MarketSnapshot.SegmentLabel(segment), Count = segment.Count,
                P25 = segment.P25, P50 = segment.P50, P75 = segment.P75, P90 = segment.P90, Sorted = segment.Sorted.ToList() },
            MarketText = segment is null ? "данных рынка мало" : $"{MarketSnapshot.SegmentLabel(segment)}: p25 {MarketSnapshot.Money(segment.P25)}, медиана {MarketSnapshot.Money(segment.P50)}, "
                + $"p75 {MarketSnapshot.Money(segment.P75)}, p90 {MarketSnapshot.Money(segment.P90)} net в месяц (n={segment.Count})",
            OtherOffersText = others.Count == 0 ? "(других офферов нет)" : string.Join("\n", others.Select(row =>
                $"- {row.Company}: {row.MonthlyAmount?.ToString(CultureInfo.InvariantCulture)} {row.Currency} {(row.Gross == true ? "gross" : "net")}" +
                (row.MonthlyNetUsd is { } usd ? $" (≈ {MarketSnapshot.Money(usd)} net)" : ""))),
            Conversion = new OfferConversionBasis { NetShareOfGross = salary.Value.NetShareOfGross,
                RatesToUsd = salary.Value.RatesToUsd.ToDictionary(pair => pair.Key.ToUpperInvariant(), pair => pair.Value, StringComparer.Ordinal) },
        };
        input.MarketText += $"\nОжидания кандидата: {source.Profile.DesiredSalary?.Amount.ToString(CultureInfo.InvariantCulture)} {source.Profile.DesiredSalary?.Currency} net";
        input.Recipe = OfferPreparationSteps.Recipe(operation, input);
        var json = JsonSerializer.Serialize(input, OfferPreparationJsonContext.Default.OfferPreparationInput);
        var request = new OfferPreparationRequest { Id = source.PublicationId, BusinessKey = conversation is null ? Key(null, text, source.PublicationId) : key,
            InputJson = json, InputHash = CanonicalJson.Hash(json), CreatedAt = now, Stage = OfferPreparationStage.Analyzing };
        ReadInput(request);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, request.BusinessKey, ct);
        var old = await context.OfferPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessKey == request.BusinessKey, ct);
        if (old is not null) { ReadInput(old); return old.Id; }
        var before = OfferSources.Freeze(source);
        before.Conversation = original;
        if (!await OfferSources.MatchesAsync(context, before, ct)) return null;
        if (source.Conversation is not null)
        {
            var row = context.Settings.Local.Single(setting => setting.Key == ConversationStore.Key);
            row.Value = ConversationStore.Capture(source.Conversation);
            row.UpdatedAt = now;
        }
        context.OfferPreparationRequests.Add(request);
        notifier.Add(context, NotificationKind.Info, "⏳ Оффер сохранён для разбора; результат придёт после проверки."
            + (source.Conversation is null ? "" : " /stop отменяет текущий разбор."));
        await PublishAsync(outbox, request);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return request.Id;
    }

    public async Task<ActionResult> StopAsync(ConversationState expected, CancellationToken ct)
    {
        expected = ConversationStore.Freeze(expected);
        if (expected.Id == Guid.Empty || expected.Mode != OfferAdvisor.Mode) return ActionResult.Fail("Старый диалог; новый не изменён.");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        OfferPreparationRequest? request = null;
        if (expected.OfferRequestId is { } id)
        {
            var known = await context.OfferPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
            if (known is null) return ActionResult.Fail("Разбор не найден; диалог сохранён.");
            await LockAsync(context, known.BusinessKey, ct);
            request = await context.OfferPreparationRequests.SingleAsync(row => row.Id == id, ct);
            var input = ReadInput(request);
            if (input.Source.Conversation is null || ConversationStore.Capture(input.Source.Conversation) != ConversationStore.Capture(expected)
                || request.Stage == OfferPreparationStage.Completed) return ActionResult.Fail("Разбор или диалог изменился; данные сохранены.");
        }
        await context.Settings.FromSqlInterpolated($"SELECT * FROM settings WHERE key = {ConversationStore.Key} FOR UPDATE").ToListAsync(ct);
        if (!await ConversationStore.TryClearAsync(context, expected, ct)) return ActionResult.Fail("Диалог изменился; новый не остановлен.");
        if (request is not null)
        {
            request.Stage = OfferPreparationStage.Cancelled;
            request.CompletedAt = time.GetUtcNow();
            request.ClaimEnvelopeId = null;
            request.RetryAt = null;
            request.Error = "cancelled by captured owner dialogue; saved input retained";
        }
        notifier.Add(context, NotificationKind.Info, "Разбор оффера остановлен; исходный текст сохранён, работодателю ничего не отправлено.");
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("Остановлено.");
    }

    public async Task<(OfferPreparationInput Input, OfferPreparationResult Result)> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.OfferPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            if (root.Stage == OfferPreparationStage.Completed) return (ReadInput(root), ReadResult(root));
            if (root.Stage is not (OfferPreparationStage.Analyzing or OfferPreparationStage.Publishing)) throw new OfferPreparationSupersededException();
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static OfferPreparationInput ReadInput(OfferPreparationRequest root)
    {
        if (string.IsNullOrWhiteSpace(root.InputJson) || CanonicalJson.Hash(root.InputJson) != root.InputHash) throw new UnsupportedOfferPreparationException();
        var input = JsonSerializer.Deserialize(root.InputJson, OfferPreparationJsonContext.Default.OfferPreparationInput) ?? throw new UnsupportedOfferPreparationException();
        var source = input.Source;
        if (root.Id == Guid.Empty || input.Version != 1 || input.CapturedAt == default || !Enum.IsDefined(root.Stage) || root.Failures < 0
            || source is null || source.Version != 1 || source.PublicationId != root.Id || string.IsNullOrWhiteSpace(source.Text)
            || source.Profile is null || !source.Profile.Reviewed || source.ProfileVersion <= 0 || source.FactsVersion <= 0 || source.Applications is null
            || source.Applications.Any(item => item is null || item.Application is null || item.Vacancy is null || item.Application.ApplicationId <= 0
                || item.Vacancy.Id <= 0 || item.Application.VacancyId != item.Vacancy.Id || item.Application.VacancyHash != CanonicalJson.Hash(
                    JsonSerializer.Serialize(item.Vacancy, OfferSourceJsonContext.Default.AssessmentVacancy)))
            || source.Applications.Select(item => item.Application.ApplicationId).Distinct().Count() != source.Applications.Count
            || source.Conversation is { } conversation && (conversation.Id == Guid.Empty || conversation.Mode != OfferAdvisor.Mode
                || conversation.OfferRequestId != root.Id || conversation.Draft != source.Text || conversation.Answers is null || conversation.Step <= 0)
            || root.BusinessKey != Key(source.Conversation, source.Text, root.Id)
            || input.MarketText is null || input.OtherOffersText is null || input.Conversion?.RatesToUsd is null
            || input.Conversion.NetShareOfGross is <= 0 or > 1 || input.Conversion.RatesToUsd.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key != pair.Key.ToUpperInvariant() || pair.Value <= 0)
            || input.Market is { } market && (market.Sorted is null || market.Count <= 0 || market.Count != market.Sorted.Count
                || market.Label is null || market.Sorted.Any(amount => amount <= 0) || !market.Sorted.SequenceEqual(market.Sorted.Order())
                || market.P25 > market.P50 || market.P50 > market.P75 || market.P75 > market.P90)
            || input.Operation is null || input.Operation.SchemaVersion != 1 || input.Operation.OperationId != root.Id || input.Operation.Policy is null
            || input.Recipe is null || input.Recipe.Version != 1 || input.Recipe.OperationId != root.Id || input.Recipe.Tasks is not { Count: 1 }
            || input.Recipe.Tasks.Any(task => task is null) || input.Recipe.Inputs is null || input.Recipe.Rules is null)
            throw new UnsupportedOfferPreparationException();
        return input;
    }

    internal static OfferPreparationResult ReadResult(OfferPreparationRequest root)
    {
        var input = ReadInput(root);
        if (root.ResultJson is null || CanonicalJson.Hash(root.ResultJson) != root.ResultHash) throw new UnsupportedOfferPreparationException();
        var result = JsonSerializer.Deserialize(root.ResultJson, OfferPreparationJsonContext.Default.OfferPreparationResult) ?? throw new UnsupportedOfferPreparationException();
        var analysis = result.Analysis;
        if (result.Version != 1 || analysis is null || analysis.Validate() is not null || string.IsNullOrWhiteSpace(analysis.Company)
            || analysis.MonthlyAmount is < 0 || analysis.CounterTarget is null || analysis.WalkAway is null || analysis.CounterScript is null
            || HumanText.Clean(analysis.CounterScript) != analysis.CounterScript || analysis.Terms is null
            || analysis.Terms.Any(term => term is null || term.Name is null || term.Value is null)
            || analysis.Strengths is null || analysis.Strengths.Any(item => item is null) || analysis.Risks is null || analysis.Risks.Any(item => item is null)
            || analysis.QuestionsToClarify is null || analysis.QuestionsToClarify.Any(item => item is null) || result.MonthlyNetUsd is < 0
            || result.MonthlyNetUsd != (analysis.MonthlyAmount is { } amount ? input.Conversion.Convert(amount, analysis.Currency, analysis.Gross) : null))
            throw new UnsupportedOfferPreparationException();
        return result;
    }

    private static string Key(ConversationState? conversation, string text, Guid id) => conversation is null
        ? "offer-manual:" + id.ToString("N") : "offer:" + conversation.Id.ToString("N") + ":" + Fingerprint.Of(text);
    internal static Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"hiring.offer." + key},0))", ct);
    internal static ValueTask PublishAsync(IDbContextOutbox outbox, OfferPreparationRequest root, DateTimeOffset? at = null) => root.Stage switch
    {
        OfferPreparationStage.Analyzing => outbox.PublishAsync(new AnalyzeOffer { RequestId = root.Id }, new DeliveryOptions { ScheduledTime = at }),
        OfferPreparationStage.Publishing => outbox.PublishAsync(new PublishOffer { RequestId = root.Id }, new DeliveryOptions { ScheduledTime = at }),
        _ => throw new UnsupportedOfferPreparationException(),
    };
}
