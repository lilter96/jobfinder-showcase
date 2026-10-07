using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class RejectionReviewSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, JobFinderLlm llm,
    ProfileProvider profiles, Notifier notifier, TimeProvider time)
{
    internal const string RequestKey = "analysis.rejections.request";
    internal const string OwnerKey = "analysis.rejections.owner";
    public static bool OwnsKey(string key) => key is RejectionReviewService.LatestKey or RejectionReviewService.LatestAtKey or RequestKey or OwnerKey;

    public async Task<(Guid? Id, ActionResult Result)> RequestAsync(CancellationToken ct, bool force = false)
    {
        var operation = await llm.ForOperationAsync(ct);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await RejectionReviewRequests.LockAsync(context, RejectionReviewService.LatestKey, ct);
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        if (profile is null) return (null, ActionResult.Fail("нет профиля (--import-resume)"));
        var now = time.GetUtcNow();
        var source = await SourceAsync(context, now, ct);
        await LockSourcesAsync(context, source, ct);
        source = await SourceAsync(context, now, ct);
        var resume = profiles.ResumeText() ?? "";
        var coverage = resume + " " + string.Join(" ", ProfileProvider.FromJson(profile.Content).Skills.Select(skill => skill.Name));
        var applications = await context.Applications.AsNoTracking().Include(app => app.Vacancy).Include(app => app.Events)
            .Where(app => app.Status == ApplicationStatus.Sent && app.Stage != ApplicationStage.Withdrawn && (app.SentAt ?? app.CreatedAt) <= now)
            .OrderBy(app => app.Id).ToListAsync(ct);
        var facts = applications.Where(app => app.Vacancy is not null).Select(app => RejectionReviewService.Facts(app, coverage, now)).ToList();
        var rejected = facts.Count(fact => fact.Outcome == ResponseOutcome.Rejected);
        if (rejected < RejectionReviewService.MinRejections)
            return (null, ActionResult.Fail($"отказов пока {rejected}: для разбора нужно хотя бы {RejectionReviewService.MinRejections}"));
        var previous = await ValueAsync(context, RejectionReviewService.LatestKey, ct);
        var previousAt = await ValueAsync(context, RejectionReviewService.LatestAtKey, ct);
        await ValidatePublishedAsync(context, previous, previousAt, ct);
        var old = await DesiredAsync(context, ct);
        if (!force && old is not null)
        {
            var captured = RejectionReviewRequests.ReadInput(old);
            if (old.Stage is RejectionReviewStage.Analyzing or RejectionReviewStage.Publishing
                && CanonicalJson.Hash(captured.SourceJson) == CanonicalJson.Hash(source)
                && await MatchesAsync(context, captured, ct) && RejectionReviewSteps.Recipe(operation, captured).Hash == captured.Recipe.Hash)
                return (old.Id, ActionResult.Done("Разбор уже сохранён в очереди; результат придёт отдельно."));
            if (old.Stage == RejectionReviewStage.Completed)
            {
                RejectionReviewRequests.ReadResult(old);
                if (await MarkerAsync(context, OwnerKey, ct) == old.Id && previous is not null
                    && CanonicalJson.Hash(previous) == CanonicalJson.Hash(JsonSerializer.Serialize(RejectionReviewRequests.ReadResult(old).Review, CareerJsonContext.Default.RejectionReview))
                    && captured.ProfileVersion == profile.Version && CanonicalJson.Hash(captured.ProfileJson) == CanonicalJson.Hash(profile.Content)
                    && captured.ResumeText == resume && CanonicalJson.Hash(captured.SourceJson) == CanonicalJson.Hash(source)
                    && captured.FactsText == RejectionReviewService.Describe(facts)
                    && captured.CapturedAt.UtcDateTime.Date == now.UtcDateTime.Date
                    && RejectionReviewSteps.Recipe(operation, captured).Hash == captured.Recipe.Hash)
                    return (old.Id, ActionResult.Done("Разбор этих данных уже готов; новый платный вызов не нужен."));
            }
        }
        var input = new RejectionReviewInputSnapshot
        {
            ProfileJson = profile.Content, ProfileVersion = profile.Version, ResumeText = resume, SourceJson = source,
            FactsText = RejectionReviewService.Describe(facts), Responses = facts.Count(fact => fact.Outcome != ResponseOutcome.Waiting), Rejections = rejected,
            CapturedAt = now, PreviousJson = previous, PreviousAt = previousAt, Operation = operation.CaptureOperation(),
        };
        input.Recipe = RejectionReviewSteps.Recipe(operation, input);
        var root = RejectionReviewRequests.Root(input);
        if (old is { Stage: RejectionReviewStage.Analyzing or RejectionReviewStage.Publishing })
        {
            old.Stage = RejectionReviewStage.Cancelled;
            old.CompletedAt = now;
            old.ClaimEnvelopeId = null;
            old.Error = "replaced by a new captured review; checkpoints retained";
        }
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        context.RejectionReviewRequests.Add(root);
        await SetAsync(context, RequestKey, root.Id.ToString("D"), now, ct);
        notifier.Add(context, NotificationKind.Info, $"📊 Разбор {input.Responses} откликов ({rejected} отказов) сохранён в очереди; результат придёт отдельно.");
        await RejectionReviewRequests.PublishAsync(outbox, root);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return (root.Id, ActionResult.Done("Разбор сохранён в очереди; готовый результат придёт отдельно."));
    }

    internal async Task<bool> MatchesAsync(JobFinderDbContext context, RejectionReviewInputSnapshot input, CancellationToken ct)
    {
        await LockSourcesAsync(context, input.SourceJson, ct);
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        return await MarkerAsync(context, RequestKey, ct) == input.Operation.OperationId
            && profile is not null && profile.Version == input.ProfileVersion && CanonicalJson.Hash(profile.Content) == CanonicalJson.Hash(input.ProfileJson)
            && (profiles.ResumeText() ?? "") == input.ResumeText
            && await ValueAsync(context, RejectionReviewService.LatestKey, ct) == input.PreviousJson
            && await ValueAsync(context, RejectionReviewService.LatestAtKey, ct) == input.PreviousAt
            && CanonicalJson.Hash(await SourceAsync(context, input.CapturedAt, ct)) == CanonicalJson.Hash(input.SourceJson);
    }

    internal async Task<bool> PublishJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, RejectionReviewInputSnapshot input,
        RejectionReviewResult result, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || !ReferenceEquals(outbox.ActiveContext, context)) throw new InvalidOperationException("rejection publication requires its enrolled transaction");
        if (!await MatchesAsync(context, input, ct)) return false;
        var now = time.GetUtcNow();
        await SetAsync(context, RejectionReviewService.LatestKey, JsonSerializer.Serialize(result.Review, CareerJsonContext.Default.RejectionReview), now, ct);
        await SetAsync(context, RejectionReviewService.LatestAtKey, result.GeneratedAt.ToString("O", CultureInfo.InvariantCulture), now, ct);
        await SetAsync(context, OwnerKey, input.Operation.OperationId.ToString("D"), now, ct);
        notifier.Add(context, NotificationKind.Info, $"📊 Разобрано откликов: {input.Responses}, отказов: {input.Rejections}. Результат — в «Аналитике» дашборда.");
        return true;
    }

    public async Task<(RejectionReview? Review, DateTimeOffset? At)> LatestAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await RejectionReviewRequests.LockAsync(context, RejectionReviewService.LatestKey, ct);
        var json = await ValueAsync(context, RejectionReviewService.LatestKey, ct);
        var at = await ValueAsync(context, RejectionReviewService.LatestAtKey, ct);
        var review = json is null ? null : JsonSerializer.Deserialize(json, CareerJsonContext.Default.RejectionReview);
        await ValidatePublishedAsync(context, json, at, ct);
        return (review, DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : null);
    }

    private static async Task ValidatePublishedAsync(JobFinderDbContext context, string? json, string? at, CancellationToken ct)
    {
        if (await MarkerAsync(context, OwnerKey, ct) is { } owner)
        {
            var root = await context.RejectionReviewRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == owner, ct) ?? throw new UnsupportedRejectionReviewException();
            var result = RejectionReviewRequests.ReadResult(root);
            if (root.Stage != RejectionReviewStage.Completed || json is null
                || CanonicalJson.Hash(json) != CanonicalJson.Hash(JsonSerializer.Serialize(result.Review, CareerJsonContext.Default.RejectionReview))
                || at != result.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)) throw new UnsupportedRejectionReviewException();
        }
    }

    private static async Task<string> SourceAsync(JobFinderDbContext context, DateTimeOffset at, CancellationToken ct) =>
        await context.Database.SqlQuery<string>($"""
            SELECT COALESCE(jsonb_agg(jsonb_build_object('application', to_jsonb(app), 'vacancy', to_jsonb(vacancy),
                'events', COALESCE((SELECT jsonb_agg(to_jsonb(event) ORDER BY event.id) FROM application_events event WHERE event.application_id = app.id), '[]'::jsonb))
                ORDER BY app.id), '[]'::jsonb)::text AS "Value"
            FROM applications app JOIN vacancies vacancy ON vacancy.id = app.vacancy_id
            WHERE app.status = 'Sent' AND app.stage IS DISTINCT FROM 'Withdrawn' AND COALESCE(app.sent_at, app.created_at) <= {at}
            """).SingleAsync(ct);

    private static async Task LockSourcesAsync(JobFinderDbContext context, string source, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(source);
        var ids = json.RootElement.EnumerateArray().Select(item => item.GetProperty("application").GetProperty("vacancy_id").GetInt64()).Distinct().Order().ToArray();
        foreach (var id in ids) await DeliveryLedger.LockVacancyAsync(context, id, ct);
    }

    private static async Task<RejectionReviewRequest?> DesiredAsync(JobFinderDbContext context, CancellationToken ct)
    {
        if (await MarkerAsync(context, RequestKey, ct) is not { } id) return null;
        var root = await context.RejectionReviewRequests.SingleOrDefaultAsync(row => row.Id == id, ct) ?? throw new UnsupportedRejectionReviewException();
        RejectionReviewRequests.ReadInput(root);
        return root;
    }

    private static async Task<Guid?> MarkerAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        var value = await ValueAsync(context, key, ct);
        if (value is null) return null;
        return Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new UnsupportedRejectionReviewException();
    }

    private static Task<string?> ValueAsync(JobFinderDbContext context, string key, CancellationToken ct) =>
        context.Settings.AsNoTracking().Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(ct);

    private static async Task SetAsync(JobFinderDbContext context, string key, string value, DateTimeOffset at, CancellationToken ct)
    {
        var row = await context.Settings.FindAsync([key], ct);
        if (row is null) context.Settings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = at });
        else { row.Value = value; row.UpdatedAt = at; }
    }
}
