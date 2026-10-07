using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record HhHistorySnapshot
{
    public int Version { get; set; } = 1;
    public long ApplicationId { get; set; }
    public long VacancyId { get; set; }
    public long HhVacancyId { get; set; }
    public long ChatId { get; set; }
    public string SourceHash { get; set; } = "";
    public string StateHash { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
}

public sealed record HhHistoryProgress
{
    public int Version { get; set; } = 1;
    public int Failures { get; set; }
    public bool Retired { get; set; }
}

public sealed class HhChatHistorySources(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    Notifier notifier, TimeProvider time)
{
    internal const string ProgressPrefix = "hh.chat.history.";

    public async Task<HhHistorySnapshot?> CaptureAsync(long applicationId, long chatId, long hhVacancyId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var application = await ReadAsync(context, applicationId, ct);
        if (application is null) return null;
        await LockAsync(context, application.VacancyId, applicationId, ct);
        application = await ReadAsync(context, applicationId, ct);
        if (!await EligibleAsync(context, application, chatId, hhVacancyId, ct)) return null;
        var response = await context.HhResponses.AsNoTracking().SingleAsync(row => row.HhVacancyId == hhVacancyId, ct);
        var state = await ProgressAsync(context, applicationId, chatId, ct);
        if (state.Retired) return null;
        return new()
        {
            ApplicationId = applicationId, VacancyId = application!.VacancyId, HhVacancyId = hhVacancyId, ChatId = chatId,
            SourceHash = SourceHash(application, response), StateHash = StateHash(state), CapturedAt = time.GetUtcNow(),
        };
    }

    public async Task<bool> PublishAsync(HhHistorySnapshot snapshot, IReadOnlyList<HhChatMessage> messages, CancellationToken ct)
    {
        Validate(snapshot);
        if (messages.Any(message => string.IsNullOrWhiteSpace(message.Id) || message.Id.Length > 249 || message.Text is null))
            throw new ArgumentException("history messages require an ID and text", nameof(messages));
        using var stage = Telemetry.StartStage("publish-history", "hh-negotiations").Tag("application.id", snapshot.ApplicationId).Tag("message.count", messages.Count);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, snapshot.VacancyId, snapshot.ApplicationId, ct);
        var application = await CurrentAsync(context, snapshot, ct);
        if (application is null) return false;
        var externalIds = messages.Select(message => "hhchat:" + message.Id).ToArray();
        if (await context.ApplicationEvents.AnyAsync(row => row.Channel == EventChannel.HeadHunter && row.ApplicationId != snapshot.ApplicationId
            && externalIds.Contains(row.ExternalId!), ct)) return false;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var ids = application.Events.Where(row => row.ExternalId is not null).Select(row => row.ExternalId!).ToHashSet(StringComparer.Ordinal);
        var texts = application.Events.Where(row => row.Text is not null).Select(row => TextUtil.Normalize(row.Text)).ToList();
        var from = application.SentAt ?? snapshot.CapturedAt;
        var to = application.StageChangedAt is { } changed && changed > from ? changed : from;
        var nonempty = messages.Where(message => !string.IsNullOrWhiteSpace(message.Text)).ToArray();
        var added = 0;
        for (var index = 0; index < nonempty.Length; index++)
        {
            var message = nonempty[index];
            var externalId = "hhchat:" + message.Id;
            var text = TextUtil.Normalize(message.Text);
            var head = text[..Math.Min(60, text.Length)];
            if (ids.Contains(externalId) || head.Length > 0 && texts.Any(stored => stored.Contains(head, StringComparison.Ordinal))) continue;
            context.ApplicationEvents.Add(new()
            {
                ApplicationId = snapshot.ApplicationId, At = from + (to - from) * (index + 1) / (nonempty.Length + 1),
                Kind = message.Mine ? EventKind.MessageOut : EventKind.MessageIn, Channel = EventChannel.HeadHunter,
                Author = message.Mine ? "я" : message.Author ?? "работодатель", Text = message.Text, ExternalId = externalId,
            });
            ids.Add(externalId);
            texts.Add(text);
            added++;
        }
        AddMarker(context, snapshot, to, nonempty.Length == 0 ? "чат hh пуст" : $"история чата hh: {nonempty.Length} сообщений, новых {added}");
        await SetProgressAsync(context, snapshot, new() { Retired = true }, ct);
        notifier.Add(context, NotificationKind.Info,
            $"📥 История чата hh для отклика #{snapshot.ApplicationId}: новых сообщений {added}. Только история, без отправки ответов.", entityId: snapshot.ApplicationId);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return true;
    }

    public async Task<bool> RecordFailureAsync(HhHistorySnapshot snapshot, string reason, CancellationToken ct)
    {
        Validate(snapshot);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, snapshot.VacancyId, snapshot.ApplicationId, ct);
        var application = await CurrentAsync(context, snapshot, ct);
        if (application is null) return false;
        var previous = await ProgressAsync(context, snapshot.ApplicationId, snapshot.ChatId, ct);
        var next = previous with { Failures = previous.Failures + 1, Retired = previous.Failures >= 1 };
        if (next.Retired)
        {
            AddMarker(context, snapshot, application.StageChangedAt ?? snapshot.CapturedAt, "чат hh не читается: " + TextUtil.Truncate(reason, 200));
            notifier.Add(context, NotificationKind.Alert,
                $"⚠️ История чата hh для отклика #{snapshot.ApplicationId} не прочитана после двух попыток. Это не подтверждение доставки; проверьте чат вручную.", entityId: snapshot.ApplicationId);
        }
        await SetProgressAsync(context, snapshot, next, ct);
        await notifier.SaveAndFlushAsync(context, ct);
        return true;
    }

    private static void AddMarker(JobFinderDbContext context, HhHistorySnapshot snapshot, DateTimeOffset at, string text) =>
        context.ApplicationEvents.Add(new() { ApplicationId = snapshot.ApplicationId, At = at, Kind = EventKind.Note,
            Channel = EventChannel.HeadHunter, ExternalId = RejectionReviewService.ChatHistoryMarker + snapshot.ChatId.ToString(CultureInfo.InvariantCulture), Text = text });

    private static string Key(long applicationId, long chatId) => ProgressPrefix + applicationId.ToString(CultureInfo.InvariantCulture) + "." + chatId.ToString(CultureInfo.InvariantCulture);

    private async Task SetProgressAsync(JobFinderDbContext context, HhHistorySnapshot snapshot, HhHistoryProgress progress, CancellationToken ct)
    {
        var key = Key(snapshot.ApplicationId, snapshot.ChatId);
        var row = await context.Settings.SingleOrDefaultAsync(setting => setting.Key == key, ct);
        if (row is null) context.Settings.Add(row = new() { Key = key, Value = "" });
        row.Value = JsonSerializer.Serialize(progress, HhHistoryJsonContext.Default.HhHistoryProgress);
        row.UpdatedAt = time.GetUtcNow();
    }

    private static async Task<HhHistoryProgress> ProgressAsync(JobFinderDbContext context, long applicationId, long chatId, CancellationToken ct)
    {
        var json = await context.Settings.Where(row => row.Key == Key(applicationId, chatId)).Select(row => row.Value).SingleOrDefaultAsync(ct);
        var progress = json is null ? new HhHistoryProgress() : JsonSerializer.Deserialize(json, HhHistoryJsonContext.Default.HhHistoryProgress) ?? throw new JsonException("empty hh history progress");
        if (progress.Version != 1 || progress.Failures is < 0 or > 2 || progress.Failures == 2 && !progress.Retired) throw new JsonException("damaged hh history progress");
        return progress;
    }

    private static string StateHash(HhHistoryProgress progress) => CanonicalJson.Hash(JsonSerializer.Serialize(progress, HhHistoryJsonContext.Default.HhHistoryProgress));

    private static string SourceHash(Application application, HhResponse response) => CanonicalJson.Hash(JsonSerializer.Serialize(new
    {
        application.Id, application.VacancyId, application.Status, application.Channel, application.Revision, application.Stage,
        application.SentAt, application.StageChangedAt, application.Target, application.LastInboundAt, application.LastOutboundAt,
        application.DeliveryRequestId, response.HhVacancyId, response.ChatId,
        Sources = application.Vacancy!.Sources.OrderBy(row => row.Id).Select(row => new { row.Id, row.Source, row.ExternalId }),
        Events = application.Events.OrderBy(row => row.Id).Select(row => new { row.Id, row.Revision, row.Kind, row.Channel, row.ExternalId, row.Text, row.Draft, row.DraftStatus }),
    }));

    private static Task<Application?> ReadAsync(JobFinderDbContext context, long applicationId, CancellationToken ct) => context.Applications.AsNoTracking()
        .Include(row => row.Events).Include(row => row.Vacancy).ThenInclude(row => row!.Sources).SingleOrDefaultAsync(row => row.Id == applicationId, ct);

    private static async Task LockAsync(JobFinderDbContext context, long vacancyId, long applicationId, CancellationToken ct)
    {
        await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM applications WHERE id = {applicationId} FOR UPDATE", ct);
    }

    private static async Task<bool> EligibleAsync(JobFinderDbContext context, Application? application, long chatId, long hhVacancyId, CancellationToken ct) =>
        application is { Status: ApplicationStatus.Sent, Channel: ApplicationChannel.HeadHunter }
        && !application.Events.Any(row => row.ExternalId is not null && row.ExternalId.StartsWith(RejectionReviewService.ChatHistoryMarker, StringComparison.Ordinal))
        && application.Vacancy!.Sources.Any(row => row.Source == SourceKind.HeadHunter && row.ExternalId == hhVacancyId.ToString(CultureInfo.InvariantCulture))
        && await context.HhResponses.AnyAsync(row => row.HhVacancyId == hhVacancyId && row.ChatId == chatId, ct)
        && !await context.ReplyProcessingRequests.AnyAsync(row => row.ApplicationId == application.Id && row.Mode == ReplyProcessingMode.Incoming
            && (row.Stage == ReplyProcessingStage.Capturing || row.Stage == ReplyProcessingStage.Fast || row.Stage == ReplyProcessingStage.Deep || row.Stage == ReplyProcessingStage.Publishing), ct)
        && !await EmployerMessageSources.UnresolvedDeliveryAsync(context, application.VacancyId, ct);

    private static async Task<Application?> CurrentAsync(JobFinderDbContext context, HhHistorySnapshot snapshot, CancellationToken ct)
    {
        var application = await ReadAsync(context, snapshot.ApplicationId, ct);
        if (application?.VacancyId != snapshot.VacancyId || !await EligibleAsync(context, application, snapshot.ChatId, snapshot.HhVacancyId, ct)) return null;
        var response = await context.HhResponses.AsNoTracking().SingleAsync(row => row.HhVacancyId == snapshot.HhVacancyId, ct);
        var state = await ProgressAsync(context, snapshot.ApplicationId, snapshot.ChatId, ct);
        return !state.Retired && StateHash(state) == snapshot.StateHash && SourceHash(application, response) == snapshot.SourceHash ? application : null;
    }

    private static void Validate(HhHistorySnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.ApplicationId <= 0 || snapshot.VacancyId <= 0 || snapshot.ChatId <= 0 || snapshot.HhVacancyId <= 0
            || snapshot.CapturedAt == default || string.IsNullOrWhiteSpace(snapshot.SourceHash) || string.IsNullOrWhiteSpace(snapshot.StateHash))
            throw new ArgumentException("invalid hh history witness", nameof(snapshot));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HhHistorySnapshot))]
[JsonSerializable(typeof(HhHistoryProgress))]
public sealed partial class HhHistoryJsonContext : JsonSerializerContext;
