using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class ReplyProcessingRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, JobFinderLlm llm,
    LocalClock clock, TimeProvider time, RuntimeSettings settings)
{
    public async Task<ReplyProcessingTicket> IncomingAsync(InboundReply raw, CancellationToken ct)
    {
        Validate(raw);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        var ticket = await IncomingJoinedAsync(context, outbox, raw, ct);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return ticket;
    }

    internal async Task<ReplyProcessingTicket> IncomingJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, InboundReply raw, CancellationToken ct)
    {
        Validate(raw);
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("incoming admission requires the enrolled source transaction");
        var json = Serialize(raw);
        var key = Fingerprint.Of(raw.ExternalId is { } external ? $"reply-incoming-v1\n{raw.Channel}\n{external}"
            : $"reply-incoming-v1\n{raw.ApplicationId}\n{raw.Channel}\n{raw.Author}\n{raw.Message()}\n{raw.At.UtcTicks / 10}");
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM applications WHERE id = {raw.ApplicationId} FOR UPDATE", ct);
        await LockAsync(context, key, ct);
        var added = context.ChangeTracker.Entries<ReplyProcessingRequest>().Select(entry => entry.Entity).SingleOrDefault(row => row.BusinessKey == key);
        if (added is not null) return new(added.Id);
        var old = await context.ReplyProcessingRequests.SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
        if (old is not null) return new(old.Id);
        if (raw.ExternalId is not null && await context.ApplicationEvents.AnyAsync(row => row.Channel == raw.Channel && row.ExternalId == raw.ExternalId, ct))
            return new(Guid.Empty, true);
        var vacancyId = await context.Applications.Where(row => row.Id == raw.ApplicationId).Select(row => row.VacancyId).SingleAsync(ct);
        var request = new ReplyProcessingRequest
        {
            Id = Guid.NewGuid(), BusinessKey = key, ApplicationId = raw.ApplicationId, VacancyId = vacancyId,
            Mode = ReplyProcessingMode.Incoming, Stage = ReplyProcessingStage.Capturing, RawJson = json, RawHash = CanonicalJson.Hash(json), CreatedAt = time.GetUtcNow(),
        };
        context.ReplyProcessingRequests.Add(request);
        await PublishAsync(outbox, request);
        return new(request.Id);
    }

    public Task<ReplyProcessingTicket> RedraftAsync(CapturedReplyDraft captured, bool preview, CancellationToken ct) =>
        PrepareAsync(captured, preview ? ReplyProcessingMode.Preview : ReplyProcessingMode.Redraft, null, ct);

    public Task<ReplyProcessingTicket> ReprocessAsync(CapturedReplyDraft captured, InboundReply corrected, CancellationToken ct)
    {
        Validate(corrected);
        if (!CorrectionMatches(captured.Event, corrected)) throw new ReplyProcessingSupersededException();
        return PrepareAsync(captured, ReplyProcessingMode.Reprocess, corrected, ct);
    }

    private async Task<ReplyProcessingTicket> PrepareAsync(CapturedReplyDraft captured, ReplyProcessingMode mode, InboundReply? corrected, CancellationToken ct)
    {
        var operation = await llm.ForOperationAsync(ct);
        var source = captured.Event;
        var input = new ReplyProcessingInput
        {
            Version = corrected is null ? 1 : 2, OriginalEventJson = corrected is null ? null : ReplyDraftSources.SourceJson(source),
            ApplicationId = captured.Application.Id, Vacancy = AssessmentVacancy.From(captured.Application.Vacancy!), Profile = captured.Profile,
            Facts = captured.Facts, ProfileVersion = captured.Snapshot.ProfileVersion, FactsVersion = captured.Snapshot.FactsVersion,
            Stories = captured.Stories, ContextHash = captured.Snapshot.ContextHash, DraftSnapshot = captured.Snapshot,
            Conversation = Conversation(captured.Application.Events.Where(row => row.At < source.At && row.Id != source.Id).OrderByDescending(row => row.At).Take(8)),
            Message = corrected?.Message() ?? source.Text ?? "", CapturedAt = clock.Now, TimeZone = clock.Zone.Id, Operation = operation.CaptureOperation(),
            Interviews = corrected is null ? [] : captured.Interviews.ToArray(),
            AutoReply = corrected is not null && await settings.GetBoolAsync(RuntimeSettings.ChatAutoReply, true, ct),
        };
        input.Recipe = ReplyProcessingSteps.Recipe(operation, input);
        var cacheInputs = new Dictionary<string, string?>(input.Recipe.Inputs) { ["date"] = input.CapturedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        var prefix = corrected is null ? $"reply-redraft-v1\n{mode == ReplyProcessingMode.Preview}" : "reply-reprocess-v2";
        var key = Fingerprint.Of($"{prefix}\n{CanonicalJson.Hash(JsonSerializer.Serialize(captured.Snapshot))}\n{(input.Recipe with { Inputs = cacheInputs }).Hash}"
            + (corrected is null ? "" : "\n" + CanonicalJson.Hash(Serialize(corrected))));
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockSourceAsync(context, input.Vacancy.Id, input.ApplicationId, ct);
        await LockAsync(context, key, ct);
        var old = await context.ReplyProcessingRequests.SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
        ReplyProcessingRequest? retry = null;
        if (old is { Mode: ReplyProcessingMode.Reprocess, Stage: ReplyProcessingStage.Failed or ReplyProcessingStage.Cancelled })
            retry = await context.ReplyProcessingRequests.Where(row => row.RetryOfId == old.Id)
                .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).FirstOrDefaultAsync(ct);
        var reusable = retry ?? old;
        if (reusable is { Mode: ReplyProcessingMode.Reprocess, Stage: ReplyProcessingStage.Completed }) return new(reusable.Id, true);
        if (!await MatchesAsync(context, input, ct)) throw new ReplyProcessingSupersededException();
        var restarting = reusable is { Mode: ReplyProcessingMode.Reprocess, Stage: ReplyProcessingStage.Failed or ReplyProcessingStage.Cancelled };
        if (reusable is not null && !restarting) return new(reusable.Id);
        var raw = corrected ?? new InboundReply { ApplicationId = input.ApplicationId, Channel = source.Channel, Author = source.Author ?? "", Text = source.Text ?? "", ExternalId = source.ExternalId, At = source.At };
        var json = Serialize(raw);
        var inputJson = Serialize(input);
        var request = new ReplyProcessingRequest
        {
            Id = Guid.NewGuid(), BusinessKey = restarting ? key + ":retry:" + Guid.NewGuid().ToString("N") : key,
            RetryOfId = restarting ? old!.Id : null, ApplicationId = input.ApplicationId, VacancyId = input.Vacancy.Id, EventId = source.Id,
            Mode = mode, Stage = ReplyProcessingStage.Fast,
            RawJson = json, RawHash = CanonicalJson.Hash(json), InputJson = inputJson, InputHash = CanonicalJson.Hash(inputJson), CreatedAt = time.GetUtcNow(),
        };
        ReadInput(request);
        context.ReplyProcessingRequests.Add(request);
        await PublishAsync(outbox, request);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return new(request.Id);
    }

    public async Task<ReplyProcessingResult> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var request = await context.ReplyProcessingRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            if (request.Stage == ReplyProcessingStage.Completed) return ReadResult(request.BestJson);
            if (request.Stage == ReplyProcessingStage.Cancelled) throw new ReplyProcessingSupersededException();
            if (request.Stage == ReplyProcessingStage.Failed) throw new UnsupportedReplyProcessingException();
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    public async Task<ReplyProcessingTicket> RetryAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        var known = await context.ReplyProcessingRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM applications WHERE id = {known.ApplicationId} FOR UPDATE", ct);
        await LockAsync(context, known.BusinessKey, ct);
        var current = await context.ReplyProcessingRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        if (current.Mode != ReplyProcessingMode.Incoming || current.Stage is not (ReplyProcessingStage.Cancelled or ReplyProcessingStage.Failed))
            throw new InvalidOperationException("повтор доступен только остановленному входящему анализу, не доставке");
        var raw = ReadRaw(current);
        if (raw.ExternalId is not null && await context.ApplicationEvents.AnyAsync(row => row.Channel == raw.Channel && row.ExternalId == raw.ExternalId, ct))
            return new(Guid.Empty, true);
        var old = await context.ReplyProcessingRequests.Where(row => row.RetryOfId == current.Id
            && (row.Stage == ReplyProcessingStage.Capturing || row.Stage == ReplyProcessingStage.Fast || row.Stage == ReplyProcessingStage.Deep || row.Stage == ReplyProcessingStage.Publishing))
            .OrderByDescending(row => row.CreatedAt).FirstOrDefaultAsync(ct);
        if (old is not null) return new(old.Id);
        var request = new ReplyProcessingRequest
        {
            Id = Guid.NewGuid(), BusinessKey = current.BusinessKey + ":retry:" + Guid.NewGuid().ToString("N"), RetryOfId = current.Id,
            ApplicationId = current.ApplicationId, VacancyId = current.VacancyId, Mode = ReplyProcessingMode.Incoming, Stage = ReplyProcessingStage.Capturing,
            RawJson = current.RawJson, RawHash = current.RawHash, CreatedAt = time.GetUtcNow(),
        };
        context.ReplyProcessingRequests.Add(request);
        await PublishAsync(outbox, request);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return new(request.Id);
    }

    internal static string Conversation(IEnumerable<ApplicationEvent> events) => string.Join("\n", events.OrderBy(row => row.At).Select(row =>
        $"[{(row.Kind == EventKind.MessageIn ? "работодатель" : row.Kind == EventKind.MessageOut ? "я" : "событие")}] {TextUtil.Truncate(row.Text, 600)}"));

    internal static void Validate(InboundReply raw)
    {
        if (raw.Version != 1 || raw.ApplicationId <= 0 || !Enum.IsDefined(raw.Channel) || raw.Author is null || raw.Text is null || raw.At == default)
            throw new UnsupportedReplyProcessingException();
    }

    internal static InboundReply ReadRaw(ReplyProcessingRequest request)
    {
        var raw = JsonSerializer.Deserialize(request.RawJson, ReplyProcessingJsonContext.Default.InboundReply) ?? throw new UnsupportedReplyProcessingException();
        Validate(raw);
        if (raw.ApplicationId != request.ApplicationId || CanonicalJson.Hash(request.RawJson) != request.RawHash) throw new UnsupportedReplyProcessingException();
        return raw;
    }

    internal static ReplyProcessingInput ReadInput(ReplyProcessingRequest request)
    {
        var raw = ReadRaw(request);
        if (request.InputJson is null) throw new UnsupportedReplyProcessingException();
        var input = JsonSerializer.Deserialize(request.InputJson, ReplyProcessingJsonContext.Default.ReplyProcessingInput) ?? throw new UnsupportedReplyProcessingException();
        if (input.Version != (request.Mode == ReplyProcessingMode.Reprocess ? 2 : 1)
            || (request.Mode == ReplyProcessingMode.Reprocess ? input.OriginalEventJson is null : input.OriginalEventJson is not null)
            || input.ApplicationId != request.ApplicationId || input.Vacancy?.Id != request.VacancyId
            || input.Profile is null || input.Facts?.Facts is null || input.Stories is null || input.Conversation is null || input.Message is null
            || string.IsNullOrWhiteSpace(input.ContextHash) || input.Interviews is null || input.CapturedAt == default || string.IsNullOrWhiteSpace(input.TimeZone)
            || input.ProfileVersion < 0 || input.FactsVersion < 0
            || input.Operation is null || input.Recipe is null || input.Recipe.OperationId != input.Operation.OperationId
            || input.Operation.SchemaVersion != 1 || input.Operation.OperationId == Guid.Empty || input.Operation.PolicyRevision < 0 || input.Operation.Policy is null
            || input.Recipe.Version != 1 || input.Recipe.Tasks is not { Count: 2 } || input.Recipe.Inputs is null || input.Recipe.Rules is null
            || CanonicalJson.Hash(request.InputJson) != request.InputHash || !Enum.IsDefined(request.Mode)
            || request.Mode == ReplyProcessingMode.Incoming && input.DraftSnapshot is not null
            || request.Mode != ReplyProcessingMode.Incoming && (input.DraftSnapshot is null || input.DraftSnapshot.EventId != request.EventId
                || input.DraftSnapshot.ApplicationId != request.ApplicationId || input.DraftSnapshot.VacancyId != request.VacancyId
                || input.DraftSnapshot.ProfileVersion != input.ProfileVersion || input.DraftSnapshot.FactsVersion != input.FactsVersion
                || input.DraftSnapshot.ContextHash != input.ContextHash || input.DraftSnapshot.StoriesHash != Fingerprint.Of(input.Stories)))
            throw new UnsupportedReplyProcessingException();
        if (input.Message != raw.Message()) throw new UnsupportedReplyProcessingException();
        if (request.Mode == ReplyProcessingMode.Reprocess)
        {
            if (Fingerprint.Of(input.OriginalEventJson!) != input.DraftSnapshot!.SourceHash) throw new UnsupportedReplyProcessingException();
            try
            {
                using var original = JsonDocument.Parse(input.OriginalEventJson!);
                var source = original.RootElement;
                if (source.GetProperty("Id").GetInt64() != request.EventId
                    || source.GetProperty("ApplicationId").GetInt64() != request.ApplicationId
                    || source.GetProperty("Kind").GetInt32() != (int)EventKind.MessageIn
                    || source.GetProperty("Channel").GetInt32() != (int)EventChannel.Email || raw.Channel != EventChannel.Email
                    || source.GetProperty("ExternalId").GetString() != raw.ExternalId || string.IsNullOrWhiteSpace(raw.ExternalId)
                    || string.IsNullOrWhiteSpace(raw.Author)
                    || !string.Equals(source.GetProperty("Author").GetString(), raw.Author, StringComparison.OrdinalIgnoreCase)
                    || source.GetProperty("At").GetDateTimeOffset().UtcTicks / 10 != raw.At.UtcTicks / 10)
                    throw new UnsupportedReplyProcessingException();
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new UnsupportedReplyProcessingException();
            }
        }
        return input;
    }

    internal static ReplyProcessingResult ReadResult(string? json)
    {
        if (json is null) throw new UnsupportedReplyProcessingException();
        var result = JsonSerializer.Deserialize(json, ReplyProcessingJsonContext.Default.ReplyProcessingResult) ?? throw new UnsupportedReplyProcessingException();
        if (result.Analysis?.Validate() is not null || result.Analysis is null || result.Analysis.UsedFacts is null || result.Analysis.MissingFacts is null
            || result.Analysis.Conflicts is null || result.Analysis.Verify is null || result.Problems is null) throw new UnsupportedReplyProcessingException();
        return result;
    }

    internal static string Serialize(InboundReply value) => JsonSerializer.Serialize(value, ReplyProcessingJsonContext.Default.InboundReply);
    internal static string Serialize(ReplyProcessingInput value) => JsonSerializer.Serialize(value, ReplyProcessingJsonContext.Default.ReplyProcessingInput);
    internal static string Serialize(ReplyProcessingResult value) => JsonSerializer.Serialize(value, ReplyProcessingJsonContext.Default.ReplyProcessingResult);

    internal static Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"hiring.reply." + key}, 0))", ct);

    internal static async Task LockSourceAsync(JobFinderDbContext context, long vacancyId, long applicationId, CancellationToken ct)
    {
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM applications WHERE id = {applicationId} FOR UPDATE", ct);
    }

    internal static async Task<bool> MatchesAsync(JobFinderDbContext context, ReplyProcessingInput input, CancellationToken ct)
    {
        var vacancy = await EmployerMessageSources.VacancyAsync(context, input.Vacancy.Id, ct);
        if (vacancy is null || ReplyDraftSources.ContextHash(vacancy) != input.ContextHash) return false;
        var documents = await context.Documents.AsNoTracking().Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key).ToListAsync(ct);
        if ((documents.SingleOrDefault(row => row.Key == ProfileProvider.Key)?.Version ?? 0) != input.ProfileVersion
            || (documents.SingleOrDefault(row => row.Key == FactsProvider.Key)?.Version ?? 0) != input.FactsVersion
            || await ReplyDraftSources.StoriesAsync(context, ct) != input.Stories) return false;
        if (input.Version == 2)
        {
            var source = vacancy.Applications.SingleOrDefault(row => row.Id == input.ApplicationId)?.Events
                .SingleOrDefault(row => row.Id == input.DraftSnapshot?.EventId);
            if (source is null || !ReplyDraftSources.Editable(vacancy, source) || source.Kind != EventKind.MessageIn || source.Channel != EventChannel.Email
                || ReplyDraftSources.SourceHash(source) != input.DraftSnapshot!.SourceHash
                || await EmployerMessageSources.DeliveryPendingAsync(context, vacancy.Id, ct)) return false;
        }
        else if (input.DraftSnapshot is not null) return true;
        var interviews = await context.Interviews.AsNoTracking().Where(row => row.ApplicationId == input.ApplicationId && row.Status == InterviewStatus.Scheduled)
            .OrderBy(row => row.Id).ToListAsync(ct);
        return input.Interviews.SequenceEqual(interviews.Select(row => InterviewEditSnapshot.Capture(row).Source.Hash()));
    }

    private static bool CorrectionMatches(ApplicationEvent source, InboundReply raw) => source.Kind == EventKind.MessageIn
        && source.Channel == EventChannel.Email && raw.Channel == source.Channel && raw.ApplicationId == source.ApplicationId
        && !string.IsNullOrWhiteSpace(source.ExternalId) && raw.ExternalId == source.ExternalId
        && !string.IsNullOrWhiteSpace(raw.Author) && string.Equals(raw.Author, source.Author, StringComparison.OrdinalIgnoreCase)
        && raw.At.UtcTicks / 10 == source.At.UtcTicks / 10;

    internal static ValueTask PublishAsync(IDbContextOutbox outbox, ReplyProcessingRequest request, DateTimeOffset? scheduled = null) =>
        outbox.PublishAsync(new ProcessReply { RequestId = request.Id, Stage = request.Stage, DeepAttempt = request.DeepAttempt }, new DeliveryOptions { ScheduledTime = scheduled });
}
