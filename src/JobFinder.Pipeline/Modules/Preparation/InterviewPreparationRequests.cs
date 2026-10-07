using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Options;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class InterviewPreparationRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    JobFinderLlm llm, Notifier notifier, TimeProvider time, PathsOptions paths)
{
    internal static IQueryable<long> Waiting(JobFinderDbContext context) => context.Interviews
        .Where(source => source.Status == InterviewStatus.Scheduled && source.PackJson == null && (source.PreparationRequestId == null
            || !context.InterviewPreparationRequests.Any(root => root.Id == source.PreparationRequestId && root.InterviewId == source.Id && root.Revision == source.PreparationRevision)))
        .OrderBy(source => source.Id).Select(source => source.Id);

    internal async Task<int> ImportAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var context = await db.CreateDbContextAsync(ct);
        var ids = await Waiting(context).Take(limit).ToListAsync(ct);
        var admitted = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var revision = await context.Interviews.Where(source => source.Id == id).Select(source => (long?)source.PreparationRevision).SingleOrDefaultAsync(ct);
            if (await RequestAsync(id, ct) is not null
                || !await Waiting(context).ContainsAsync(id, ct)
                || await context.Interviews.AnyAsync(source => source.Id == id && source.PreparationRevision != revision, ct)) admitted++;
        }
        return admitted;
    }

    public async Task<Guid?> RequestAsync(long interviewId, CancellationToken ct, bool force = false)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var source = await context.Interviews.FromSqlInterpolated($"SELECT * FROM interviews WHERE id = {interviewId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (source is null || source.Status != InterviewStatus.Scheduled
            || !force && source.PackJson is not null && source.PreparationRequestId is null) return null;
        if (!Enum.IsDefined(source.Kind) || source.DurationMinutes is < 15 or > 480 || source.PreparationRevision is < 0 or long.MaxValue
            || source.CreatedAt == default || source.ApplicationId <= 0)
        {
            source.Status = InterviewStatus.Cancelled;
            source.PreparationRequestId = null;
            notifier.Add(context, NotificationKind.Alert, "⚠️ Подготовка интервью #" + source.Id + ": некорректный источник, автоматический импорт остановлен", entityId: source.Id);
            await notifier.SaveAndFlushAsync(context, ct);
            return null;
        }
        if (!force && source.PreparationRequestId is { } currentId)
        {
            if (await context.InterviewPreparationRequests.AnyAsync(row => row.Id == currentId && row.InterviewId == source.Id && row.Revision == source.PreparationRevision, ct)) return currentId;
            notifier.Add(context, NotificationKind.Alert, "⚠️ Подготовка интервью #" + source.Id + ": владелец отсутствует или не соответствует источнику");
            source.PreparationRequestId = null;
            source.PreparationRevision++;
            await notifier.SaveAndFlushAsync(context, ct);
            return null;
        }
        var operation = await llm.ForOperationAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var id = await QueueAsync(context, outbox, source, operation, ct, force);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return id;
    }

    internal async Task<Guid> QueueAsync(JobFinderDbContext context, IDbContextOutbox outbox, Interview source, JobFinderLlm operation,
        CancellationToken ct, bool force)
    {
        if (context.Database.CurrentTransaction is null || source.Id <= 0 || context.Entry(source).State == EntityState.Detached)
            throw new InvalidOperationException("interview handoff requires a tracked source transaction");
        if (source.PreparationRequestId is { } oldId)
        {
            var previous = context.InterviewPreparationRequests.Local.FirstOrDefault(row => row.Id == oldId)
                ?? await context.InterviewPreparationRequests.SingleOrDefaultAsync(row => row.Id == oldId, ct);
            if (previous is not null && previous.InterviewId == source.Id && previous.Revision == source.PreparationRevision
                && previous.Stage is not (InterviewPreparationStage.Completed or InterviewPreparationStage.Cancelled or InterviewPreparationStage.Failed))
            {
                previous.Stage = InterviewPreparationStage.Cancelled;
                previous.CompletedAt = time.GetUtcNow();
                previous.Error = "preparation explicitly superseded";
                previous.ClaimEnvelopeId = null;
            }
        }
        if (force || source.PreparationRequestId is not null) source.PreparationRevision++;
        var seed = new InterviewPreparationSeed { Source = InterviewSourceSnapshot.Capture(source), Operation = operation.CaptureOperation(), RenderedAt = time.GetUtcNow() };
        seed.Recipe = InterviewPreparationSteps.Recipe(operation, seed);
        var json = JsonSerializer.Serialize(seed, InterviewPreparationJsonContext.Default.InterviewPreparationSeed);
        var root = new InterviewPreparationRequest
        {
            Id = seed.Operation.OperationId, InterviewId = source.Id, Revision = source.PreparationRevision, Stage = InterviewPreparationStage.Capturing,
            SeedJson = json, SeedHash = CanonicalJson.Hash(json), CreatedAt = time.GetUtcNow(),
        };
        ReadSeed(root);
        context.InterviewPreparationRequests.Add(root);
        source.PreparationRequestId = root.Id;
        if (force) { source.PackJson = null; source.PackPdfPath = null; }
        await outbox.PublishAsync(new PrepareInterview { RequestId = root.Id, Stage = root.Stage });
        return root.Id;
    }

    internal static InterviewPreparationSeed ReadSeed(InterviewPreparationRequest root)
    {
        try
        {
            if (CanonicalJson.Hash(root.SeedJson) != root.SeedHash) throw new UnsupportedInterviewPreparationException();
            var seed = JsonSerializer.Deserialize(root.SeedJson, InterviewPreparationJsonContext.Default.InterviewPreparationSeed) ?? throw new UnsupportedInterviewPreparationException();
            if (seed.Version != 1 || seed.Source is null || seed.Operation is null || seed.Recipe is null || seed.Source.Id != root.InterviewId || root.InterviewId <= 0
                || root.Id == Guid.Empty || root.Revision < 0 || seed.RenderedAt == default || seed.Source.ApplicationId <= 0
                || seed.Source.Revision != root.Revision || seed.Source.Status != InterviewStatus.Scheduled || !Enum.IsDefined(seed.Source.Kind)
                || seed.Operation.OperationId != root.Id || seed.Recipe.OperationId != root.Id || seed.Recipe.PolicyRevision != seed.Operation.PolicyRevision)
                throw new UnsupportedInterviewPreparationException();
            return seed;
        }
        catch (JsonException) { throw new UnsupportedInterviewPreparationException(); }
    }

    public async Task WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.InterviewPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            var seed = ReadSeed(root);
            if (root.Stage == InterviewPreparationStage.Completed)
            {
                var input = ReadCheckpoint(root.InputJson, root.InputHash, InterviewPreparationJsonContext.Default.InterviewPreparationInput);
                InterviewPreparationSteps.Restore(llm, seed, input);
                var plan = ReadCheckpoint(root.PlanJson, root.PlanHash, InterviewPreparationJsonContext.Default.InterviewPlanCheckpoint);
                if (!InterviewPreparationSteps.ValidPlan(JsonSerializer.Serialize(plan.Plan, InterviewJsonContext.Default.InterviewPlan))) throw new UnsupportedInterviewPreparationException();
                var result = ReadCheckpoint(root.CopilotJson, root.CopilotHash, InterviewPreparationJsonContext.Default.InterviewCopilot);
                InterviewPreparationSteps.ValidateResult(seed, input, plan, result);
                if (root.CompletedAt is null) throw new UnsupportedInterviewPreparationException();
                var source = await context.Interviews.AsNoTracking().SingleOrDefaultAsync(row => row.Id == root.InterviewId, ct);
                if (source is null || !seed.Source.Matches(source, root.Id) || source.PackJson is null || CanonicalJson.Hash(source.PackJson) != root.CopilotHash
                    || source.PackPdfPath != root.PdfPath) throw new InterviewPreparationSupersededException();
                await InterviewPreparationSteps.VerifyPrintAsync(paths, seed, new() { Path = root.PdfPath ?? "", Hash = root.PdfHash ?? "" }, ct);
                return;
            }
            if (root.Stage == InterviewPreparationStage.Cancelled) throw new InterviewPreparationSupersededException();
            if (root.Stage == InterviewPreparationStage.Failed || root.RetryAt > time.GetUtcNow()) throw new LlmUnavailableException("interview preparation is saved: " + root.Error);
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static T ReadCheckpoint<T>(string? json, string? hash, JsonTypeInfo<T> type)
    {
        try
        {
            if (json is null || hash is null || CanonicalJson.Hash(json) != hash) throw new UnsupportedInterviewPreparationException();
            return JsonSerializer.Deserialize(json, type) ?? throw new UnsupportedInterviewPreparationException();
        }
        catch (JsonException) { throw new UnsupportedInterviewPreparationException(); }
    }
}
