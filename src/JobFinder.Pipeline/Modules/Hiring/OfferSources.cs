using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record OfferApplicationSnapshot
{
    public FollowUpSnapshot Application { get; set; } = new();
    public AssessmentVacancy Vacancy { get; set; } = new();
}

public sealed record OfferDraftSnapshot
{
    public int Version { get; set; } = 1;
    public Guid PublicationId { get; set; }
    public string Text { get; set; } = "";
    public CandidateProfile Profile { get; set; } = new();
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public List<OfferApplicationSnapshot> Applications { get; set; } = [];
    public ConversationState? Conversation { get; set; }
}

public sealed record OfferPublication(ActionResult Result, long? OfferId = null, long? ApplicationId = null);

public sealed class OfferSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, ProfileProvider profiles,
    FactsProvider facts, InterviewTransitions transitions, Notifier notifier, TimeProvider time)
{
    public async Task<OfferDraftSnapshot?> CaptureAsync(string text, CancellationToken ct, ConversationState? conversation = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        conversation = conversation is null ? null : ConversationStore.Freeze(conversation);
        if (conversation is not null && (conversation.Mode != OfferAdvisor.Mode || conversation.Id == Guid.Empty)) return null;
        var profile = await profiles.GetSnapshotAsync(ct);
        if (profile is null) return null;
        var sheet = await facts.GetSnapshotAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var open = await OpenAsync(context, ct);
        var snapshot = new OfferDraftSnapshot
        {
            PublicationId = Guid.NewGuid(), Text = text, Profile = profile.Value, ProfileVersion = profile.Version,
            FactsVersion = sheet.Version, Conversation = conversation,
            Applications = open.Select(application => new OfferApplicationSnapshot
            {
                Application = FollowUpSnapshot.Capture(application), Vacancy = AssessmentVacancy.From(application.Vacancy!),
            }).ToList(),
        };
        if (conversation is not null && !await context.Settings.AnyAsync(row => row.Key == ConversationStore.Key
            && row.Value == ConversationStore.Capture(conversation), ct)) return null;
        return Freeze(snapshot);
    }

    internal static Task<List<Application>> OpenAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Applications.AsNoTracking().Include(application => application.Vacancy).ThenInclude(vacancy => vacancy!.Company)
            .Include(application => application.Vacancy).ThenInclude(vacancy => vacancy!.Sources)
            .Where(application => application.Stage == ApplicationStage.HrScreen || application.Stage == ApplicationStage.TechInterview
                || application.Stage == ApplicationStage.Final || application.Stage == ApplicationStage.Offer || application.Stage == ApplicationStage.Replied)
            .OrderBy(application => application.Id).ToListAsync(ct);

    public static OfferApplicationSnapshot? Guess(OfferDraftSnapshot captured, string? company = null)
    {
        var text = TextUtil.Normalize(captured.Text);
        var matches = captured.Applications.Where(item => item.Vacancy.CompanyName is { } name && !string.IsNullOrWhiteSpace(name)
            && (company is null ? text.Contains(TextUtil.Normalize(name), StringComparison.Ordinal)
                : VacancyFingerprint.NormalizeCompany(name) == VacancyFingerprint.NormalizeCompany(company))).ToList();
        if (matches.Count == 1) return matches[0];
        var titled = matches.Where(item => text.Contains(TextUtil.Normalize(item.Vacancy.Title), StringComparison.Ordinal)).ToList();
        return titled.Count == 1 ? titled[0] : null;
    }

    public async Task<OfferPublication> PublishAsync(OfferDraftSnapshot snapshot, OfferAnalysis analysis, decimal? netUsd, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        var published = await PublishJoinedAsync(context, outbox, snapshot, analysis, netUsd, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return published;
    }

    internal async Task<OfferPublication> PublishJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox,
        OfferDraftSnapshot snapshot, OfferAnalysis analysis, decimal? netUsd, CancellationToken ct,
        OfferPreparationInput? preparation = null, OfferPreparationResult? prepared = null)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("offer publication requires its source transaction");
        if (snapshot is null || snapshot.Version != 1 || snapshot.PublicationId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Text)
            || snapshot.Profile is null || snapshot.ProfileVersion <= 0 || snapshot.FactsVersion <= 0 || snapshot.Applications is null
            || snapshot.Applications.Any(item => item is null || item.Application is null || item.Vacancy is null
                || item.Application.ApplicationId <= 0 || item.Application.VacancyId != item.Vacancy.Id)
            || snapshot.Applications.Select(item => item.Application.ApplicationId).Distinct().Count() != snapshot.Applications.Count
            || analysis is null || analysis.Validate() is not null || string.IsNullOrWhiteSpace(analysis.Company)
            || analysis.MonthlyAmount is < 0 || analysis.CounterScript is null || analysis.Terms is null
            || analysis.Terms.Any(term => term is null || term.Name is null || term.Value is null)
            || analysis.Strengths is null || analysis.Strengths.Any(item => item is null) || analysis.Risks is null || analysis.Risks.Any(item => item is null)
            || analysis.QuestionsToClarify is null || analysis.QuestionsToClarify.Any(item => item is null) || netUsd is < 0) return new(Conflict());
        snapshot = Freeze(snapshot);
        if (snapshot.Applications.Any(item => item.Application.VacancyHash != CanonicalJson.Hash(
            JsonSerializer.Serialize(item.Vacancy, OfferSourceJsonContext.Default.AssessmentVacancy)))) return new(Conflict());
        analysis = JsonSerializer.Deserialize(JsonSerializer.Serialize(analysis, CareerJsonContext.Default.OfferAnalysis), CareerJsonContext.Default.OfferAnalysis)!;
        analysis.CounterScript = HumanText.Clean(analysis.CounterScript);
        var input = JsonSerializer.Serialize(snapshot, OfferSourceJsonContext.Default.OfferDraftSnapshot);
        var output = JsonSerializer.Serialize(analysis, CareerJsonContext.Default.OfferAnalysis);
        var hash = CanonicalJson.Hash(JsonSerializer.Serialize(new[] { input, output, netUsd?.ToString(CultureInfo.InvariantCulture) }));
        using var stage = Telemetry.StartStage("publish", "offer").Tag("operation.id", snapshot.PublicationId);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"jobfinder.offer." + snapshot.PublicationId.ToString("N")}, 0))", ct);
        var old = await context.Offers.SingleOrDefaultAsync(row => row.PublicationId == snapshot.PublicationId, ct);
        if (old is not null) return old.PublicationHash == hash ? new(ActionResult.Done("разбор уже сохранён"), old.Id, old.ApplicationId) : new(Conflict());
        if (!await MatchesAsync(context, snapshot, ct)) return new(Conflict());
        var guessed = Guess(snapshot);
        if (guessed is not null && VacancyFingerprint.NormalizeCompany(guessed.Vacancy.CompanyName)
            != VacancyFingerprint.NormalizeCompany(analysis.Company)) return new(Conflict());
        var matched = guessed ?? Guess(snapshot, analysis.Company);
        var application = matched is null ? null : context.Applications.Local.Single(row => row.Id == matched.Application.ApplicationId);
        if (application is not null && await EmployerMessageSources.DeliveryPendingAsync(context, application.VacancyId, ct)) return new(Conflict());
        var offer = new Offer
        {
            PublicationId = snapshot.PublicationId, PublicationHash = hash, InputJson = input,
            ApplicationId = application?.Id, Company = analysis.Company, RawText = snapshot.Text, MonthlyAmount = analysis.MonthlyAmount,
            Currency = analysis.Currency, Gross = analysis.Gross, MonthlyNetUsd = netUsd, AnalysisJson = output, CreatedAt = time.GetUtcNow(),
        };
        context.Offers.Add(offer);
        if (application is not null)
            await transitions.AdvanceAsync(context, outbox, application, ApplicationStage.Offer, EventChannel.Manual, "получен оффер", manual: false, ct);
        if (snapshot.Conversation is { } finished && !await ConversationStore.TryClearAsync(context, finished, ct)) throw new InvalidOperationException("offer dialogue changed under its lock");
        if (preparation is null)
            notifier.Add(context, NotificationKind.Info, "Оффер сохранён: " + TextUtil.EscapeHtml(analysis.Company)
                + (application is null ? "; однозначная связь с откликом не найдена, его стадия не менялась." : $" · отклик #{application.Id}"));
        else
        {
            var card = OfferPresentation.Render(preparation, prepared ?? throw new InvalidOperationException("missing prepared offer"), application?.Id);
            notifier.Add(context, NotificationKind.Info, card.Text, card.Buttons);
        }
        await notifier.SaveJoinedAsync(context, outbox, ct);
        return new(ActionResult.Done("оффер сохранён"), offer.Id, application?.Id);
    }

    internal static async Task<bool> MatchesAsync(JobFinderDbContext context, OfferDraftSnapshot snapshot, CancellationToken ct)
    {
        foreach (var vacancyId in snapshot.Applications.Select(item => item.Vacancy.Id).Distinct().Order())
        {
            var vacancy = await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
            if (vacancy is null) return false;
            if (vacancy.CompanyId is { } companyId)
                await context.Companies.FromSqlInterpolated($"SELECT * FROM companies WHERE id = {companyId} FOR SHARE").ToListAsync(ct);
            await context.VacancySources.FromSqlInterpolated($"SELECT * FROM vacancy_sources WHERE vacancy_id = {vacancyId} FOR SHARE").ToListAsync(ct);
        }
        foreach (var expected in snapshot.Applications.OrderBy(item => item.Application.ApplicationId))
            await context.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {expected.Application.ApplicationId} FOR UPDATE").ToListAsync(ct);
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        var documents = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {ProfileProvider.Key} OR key = {FactsProvider.Key} FOR SHARE").ToListAsync(ct);
        if (!documents.Any(row => row.Key == ProfileProvider.Key && row.Version == snapshot.ProfileVersion)
            || !documents.Any(row => row.Key == FactsProvider.Key && row.Version == snapshot.FactsVersion)) return false;
        if (CanonicalJson.Hash(ProfileProvider.ToJson(snapshot.Profile)) != CanonicalJson.Hash(ProfileProvider.ToJson(
            ProfileProvider.FromJson(documents.Single(row => row.Key == ProfileProvider.Key).Content)))) return false;
        var open = await OpenAsync(context, ct);
        if (!open.Select(application => application.Id).SequenceEqual(snapshot.Applications.OrderBy(item => item.Application.ApplicationId).Select(item => item.Application.ApplicationId))) return false;
        foreach (var item in snapshot.Applications)
            if (!item.Application.Matches(open.Single(application => application.Id == item.Application.ApplicationId))) return false;
        foreach (var vacancyId in snapshot.Applications.Select(item => item.Vacancy.Id).Distinct())
            if (await EmployerMessageSources.DeliveryPendingAsync(context, vacancyId, ct)) return false;
        if (snapshot.Conversation is { } conversation)
        {
            if (conversation.Id == Guid.Empty || conversation.Mode != OfferAdvisor.Mode
                || (await context.Settings.FromSqlInterpolated($"SELECT * FROM settings WHERE key = {ConversationStore.Key} FOR UPDATE").SingleOrDefaultAsync(ct))?.Value
                    != ConversationStore.Capture(conversation)) return false;
        }
        return true;
    }

    internal static OfferDraftSnapshot Freeze(OfferDraftSnapshot snapshot) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(snapshot, OfferSourceJsonContext.Default.OfferDraftSnapshot), OfferSourceJsonContext.Default.OfferDraftSnapshot)!;
    private static ActionResult Conflict() => ActionResult.Fail("исходный отклик, профиль или диалог изменился; оффер и стадия не перезаписаны");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(OfferDraftSnapshot))]
public sealed partial class OfferSourceJsonContext : JsonSerializerContext;
