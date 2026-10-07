using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class LetterLaneCancellation
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, CancellationTokenSource>> _requests = new();

    public CancellationTokenSource Register(Guid request, Guid lane, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_requests.GetOrAdd(request, _ => new()).TryAdd(lane, source))
        {
            source.Dispose();
            throw new InvalidOperationException("letter lane is already running locally");
        }
        return source;
    }

    public void Remove(Guid request, Guid lane)
    {
        if (_requests.TryGetValue(request, out var lanes) && lanes.TryRemove(lane, out var source)) source.Dispose();
    }

    public void Cancel(Guid request)
    {
        if (!_requests.TryRemove(request, out var lanes)) return;
        foreach (var source in lanes.Values)
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}

public sealed class LetterPreparationWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    ILetterPreparationSteps steps, ServerOwnership ownership, Notifier notifier, LetterLaneCancellation cancellation,
    TimeProvider time, ILogger<LetterPreparationWorkflow> log)
{
    public async Task RunAsync(Guid id, Guid laneId, int round, int version, LetterLaneStage expected, Guid envelopeId, CancellationToken ct)
    {
        Validate(id, round, version);
        if (laneId == Guid.Empty || envelopeId == Guid.Empty || expected is not (LetterLaneStage.Drafting or LetterLaneStage.Reviewing))
            throw new UnsupportedLetterPreparationException();
        await ChooseAsync(id, round, version, ct);
        var claimed = await ClaimAsync(id, laneId, round, expected, envelopeId, ct);
        if (claimed is null) return;
        var (request, lane, input) = claimed.Value;
        using var stage = Telemetry.StartStage(expected.ToString().ToLowerInvariant(), "letter-preparation")
            .Tag("operation.id", input.Operation.OperationId).Tag("request.id", id).Tag("vacancy.id", request.VacancyId)
            .Tag("round", round).Tag("lane", lane.Lane);
        using var tokenSource = cancellation.Register(id, laneId, ct);
        try
        {
            await using (var context = await db.CreateDbContextAsync(ct))
                if (!await context.LetterPreparationRequests.AnyAsync(row => row.Id == id && row.Stage == LetterPreparationStage.Active && row.Round == round, ct)) return;
            tokenSource.Token.ThrowIfCancellationRequested();
            ownership.EnsureOwned();
            var result = expected == LetterLaneStage.Drafting
                ? await steps.DraftAsync(input, request.BestJson is null ? null : LetterPreparationRequests.ReadCandidate(request.BestJson), tokenSource.Token)
                : await steps.ReviewAsync(input, LetterPreparationRequests.ReadCandidate(lane.DraftJson), tokenSource.Token);
            ownership.EnsureOwned();
            await CheckpointAsync(request, lane, async (context, outbox, current, saved) =>
            {
                var json = LetterPreparationRequests.SerializeCandidate(result);
                if (expected == LetterLaneStage.Drafting)
                {
                    saved.DraftJson = json;
                    saved.Stage = LetterLaneStage.Reviewing;
                    await PublishLaneAsync(outbox, saved);
                }
                else
                {
                    if (result.Review is not null && result.Review.ContextHash != (input.ExactText is null ? input.Recipe.Hash : input.ReviewContextHash))
                        result.Problems.Add("проверка письма относится к другим исходным данным");
                    if (!Ready(result) && result.Problems.Count == 0) result.Problems.Add("нет действующей проверки письма ≥ 9/10");
                    saved.ResultJson = LetterPreparationRequests.SerializeCandidate(result);
                    saved.Stage = LetterLaneStage.Completed;
                    await ArbitrateAsync(context, outbox, current, ct);
                }
                saved.Failures = 0;
            }, ct);
            log.Info($"[letter] #{request.VacancyId}: round {round} lane {lane.Lane} {expected} checkpointed");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && tokenSource.IsCancellationRequested) { }
        catch (LlmUnavailableException error)
        {
            await ChangeAsync(request, lane, async (_, outbox, _, saved) =>
            {
                saved.RetryAt = time.GetUtcNow().AddMinutes(error.Unauthorized ? 30 : 2);
                saved.Error = TextUtil.Truncate(error.Message, 900);
                await PublishLaneAsync(outbox, saved, saved.RetryAt);
            }, ct);
        }
        catch (LetterPreparationSupersededException)
        {
            await RetireAsync(request, "letter recipe superseded", LetterPreparationStage.Cancelled, ct);
        }
        catch (Exception error) when (error is not (OperationCanceledException or UnsupportedLetterPreparationException or LetterPreparationCheckpointException))
        {
            var terminal = false;
            await ChangeAsync(request, lane, async (context, outbox, current, saved) =>
            {
                saved.Error = TextUtil.Truncate(error.Message, 900);
                saved.Failures++;
                if (saved.Failures >= 4)
                {
                    terminal = true;
                    saved.Stage = LetterLaneStage.Failed;
                    await ArbitrateAsync(context, outbox, current, ct);
                }
                else saved.ClaimEnvelopeId = envelopeId;
            }, ct);
            if (!terminal) throw;
        }
        finally { cancellation.Remove(id, laneId); }
    }

    public async Task ChooseAsync(Guid id, int round, int version, CancellationToken ct)
    {
        Validate(id, round, version);
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.LetterPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (known is null || known.Stage != LetterPreparationStage.Active || known.Round != round) return;
        var input = await ValidInputAsync(known, ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LetterPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var matches = await LetterPreparationRequests.MatchesAsync(context, input, ct);
        var request = await context.LetterPreparationRequests.SingleAsync(row => row.Id == id, ct);
        if (request.Stage != LetterPreparationStage.Active || request.Round != round) return;
        ownership.EnsureOwned();
        if (matches)
        {
            try { steps.EnsureCompatible(input); await ArbitrateAsync(context, outbox, request, ct); }
            catch (LetterPreparationSupersededException) { Retire(context, request, "letter recipe superseded", LetterPreparationStage.Cancelled); }
        }
        else Retire(context, request, "letter source or candidate superseded", LetterPreparationStage.Cancelled);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        if (request.Stage != LetterPreparationStage.Active) cancellation.Cancel(id);
    }

    private async Task<(LetterPreparationRequest Request, LetterPreparationLane Lane, LetterPreparationInput Input)?> ClaimAsync(
        Guid id, Guid laneId, int round, LetterLaneStage stage, Guid envelopeId, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.LetterPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (known is null || known.Stage != LetterPreparationStage.Active || known.Round != round) return null;
        var input = await ValidInputAsync(known, ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LetterPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var matches = await LetterPreparationRequests.MatchesAsync(context, input, ct);
        var request = await context.LetterPreparationRequests.SingleAsync(row => row.Id == id, ct);
        var lane = await context.LetterPreparationLanes.SingleOrDefaultAsync(row => row.Id == laneId, ct);
        if (request.Stage != LetterPreparationStage.Active || request.Round != round || lane is null || lane.RequestId != id
            || lane.Round != round || lane.Stage != stage || lane.RetryAt > time.GetUtcNow()
            || lane.ClaimEnvelopeId is { } claim && claim != envelopeId) return null;
        ownership.EnsureOwned();
        if (!matches)
        {
            Retire(context, request, "letter source or candidate superseded", LetterPreparationStage.Cancelled);
            await notifier.SaveAndFlushAsync(context, ct);
            cancellation.Cancel(id);
            return null;
        }
        steps.EnsureCompatible(input);
        lane.ClaimEnvelopeId = envelopeId;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (request, lane, input);
    }

    private async Task ChangeAsync(LetterPreparationRequest before, LetterPreparationLane laneBefore,
        Func<JobFinderDbContext, IDbContextOutbox, LetterPreparationRequest, LetterPreparationLane, Task> change, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LetterPreparationRequests.LockAsync(context, before.BusinessKey, ct);
        var input = LetterPreparationRequests.ReadInput(before);
        var matches = await LetterPreparationRequests.MatchesAsync(context, input, ct);
        var current = await context.LetterPreparationRequests.SingleAsync(row => row.Id == before.Id, ct);
        var lane = await context.LetterPreparationLanes.SingleAsync(row => row.Id == laneBefore.Id, ct);
        if (current.Stage != LetterPreparationStage.Active || current.Round != before.Round || lane.Stage != laneBefore.Stage
            || lane.ClaimEnvelopeId != laneBefore.ClaimEnvelopeId) return;
        ownership.EnsureOwned();
        lane.ClaimEnvelopeId = null;
        lane.RetryAt = null;
        lane.Error = null;
        if (!matches) Retire(context, current, "letter source or candidate superseded", LetterPreparationStage.Cancelled);
        else
        {
            try
            {
                steps.EnsureCompatible(input);
                if (current.GraceUntil <= time.GetUtcNow() && current.BestJson is not null && Ready(LetterPreparationRequests.ReadCandidate(current.BestJson)))
                    await CompleteAsync(context, current, LetterPreparationRequests.ReadCandidate(current.BestJson), ct);
                else await change(context, outbox, current, lane);
            }
            catch (LetterPreparationSupersededException) { Retire(context, current, "letter recipe superseded", LetterPreparationStage.Cancelled); }
        }
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        if (current.Stage != LetterPreparationStage.Active) cancellation.Cancel(current.Id);
    }

    private async Task CheckpointAsync(LetterPreparationRequest before, LetterPreparationLane lane,
        Func<JobFinderDbContext, IDbContextOutbox, LetterPreparationRequest, LetterPreparationLane, Task> change, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await ChangeAsync(before, lane, change, ct); return; }
            catch (Exception error) when (error is IOException or DbException or DbUpdateException)
            {
                if (attempt >= 2) throw new LetterPreparationCheckpointException(error);
                ownership.EnsureOwned();
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
            }
        }
    }

    private async Task ArbitrateAsync(JobFinderDbContext context, IDbContextOutbox outbox, LetterPreparationRequest request, CancellationToken ct)
    {
        var lanes = await context.LetterPreparationLanes.Where(row => row.RequestId == request.Id && row.Round == request.Round).OrderBy(row => row.Lane).ToListAsync(ct);
        var best = request.BestJson is null ? null : LetterPreparationRequests.ReadCandidate(request.BestJson);
        foreach (var lane in lanes.Where(row => row.Stage == LetterLaneStage.Completed))
        {
            var candidate = LetterPreparationRequests.ReadCandidate(lane.ResultJson);
            if (best is null || Better(candidate, best)) best = candidate;
        }
        if (best is not null) request.BestJson = LetterPreparationRequests.SerializeCandidate(best);
        var input = LetterPreparationRequests.ReadInput(request);
        if (input.ExactText is not null)
        {
            if (lanes.Count != 1) throw new UnsupportedLetterPreparationException();
            if (lanes[0].Stage == LetterLaneStage.Failed) Retire(context, request, "exact-text review failed", LetterPreparationStage.Failed);
            else if (best is not null)
            {
                if (best.Text != input.ExactText || best.Review is null) throw new UnsupportedLetterPreparationException();
                await CompleteAsync(context, request, best, ct);
            }
            return;
        }
        var finished = lanes.Count == 2 && lanes.All(row => row.Stage is LetterLaneStage.Completed or LetterLaneStage.Failed);
        if (best is not null && Ready(best))
        {
            if (best.Score == 10 || finished || request.GraceUntil <= time.GetUtcNow())
            {
                await CompleteAsync(context, request, best, ct);
                return;
            }
            if (request.GraceUntil is null)
            {
                request.GraceUntil = time.GetUtcNow().AddSeconds(20);
                await outbox.PublishAsync(new ChooseLetterRound { RequestId = request.Id, Round = request.Round },
                    new DeliveryOptions { ScheduledTime = request.GraceUntil });
            }
            return;
        }
        if (!finished) return;
        if (request.Round >= 4)
        {
            if (best is null) Retire(context, request, "all letter lanes failed", LetterPreparationStage.Failed);
            else await CompleteAsync(context, request, best, ct);
            return;
        }
        request.Round++;
        request.GraceUntil = null;
        await LetterPreparationRequests.StartRoundAsync(context, outbox, request);
    }

    internal static bool Better(LetterCandidate candidate, LetterCandidate best) => candidate.Problems.Count < best.Problems.Count
        || candidate.Problems.Count == best.Problems.Count && candidate.Score > best.Score;
    internal static bool Ready(LetterCandidate candidate) => candidate.Problems.Count == 0 && candidate.Score is >= LetterWriter.MinVerdict and <= 10
        && candidate.Review is { Score: >= LetterWriter.MinVerdict and <= 10, Problems.Count: 0 }
        && candidate.Review.Score == candidate.Score && candidate.Review.TextHash == Fingerprint.Of(candidate.Text);

    private async Task CompleteAsync(JobFinderDbContext context, LetterPreparationRequest request, LetterCandidate best, CancellationToken ct)
    {
        request.Stage = LetterPreparationStage.Completed;
        request.CompletedAt = time.GetUtcNow();
        request.ResultJson = LetterPreparationRequests.SerializeCandidate(best);
        var unfinished = await context.LetterPreparationLanes.Where(row => row.RequestId == request.Id
            && (row.Stage == LetterLaneStage.Drafting || row.Stage == LetterLaneStage.Reviewing)).ToListAsync(ct);
        foreach (var lane in unfinished.Where(row => row.Stage is LetterLaneStage.Drafting or LetterLaneStage.Reviewing))
        { lane.Stage = LetterLaneStage.Abandoned; lane.ClaimEnvelopeId = null; lane.RetryAt = null; }
    }

    private async Task<LetterPreparationInput> ValidInputAsync(LetterPreparationRequest request, CancellationToken ct)
    {
        try { return LetterPreparationRequests.ReadInput(request); }
        catch (Exception error) when (error is JsonException or UnsupportedLetterPreparationException)
        {
            await RetireAsync(request, "corrupt or unsupported letter input quarantined without paid work", LetterPreparationStage.Failed, ct);
            throw new UnsupportedLetterPreparationException();
        }
    }

    private async Task RetireAsync(LetterPreparationRequest known, string reason, LetterPreparationStage stage, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LetterPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var request = await context.LetterPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (request.Stage != LetterPreparationStage.Active) return;
        Retire(context, request, reason, stage);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
        cancellation.Cancel(known.Id);
    }

    private void Retire(JobFinderDbContext context, LetterPreparationRequest request, string reason, LetterPreparationStage stage)
    {
        request.Stage = stage;
        request.Error = reason;
        request.CompletedAt = time.GetUtcNow();
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Подготовка письма для вакансии #{request.VacancyId}: " + TextUtil.EscapeHtml(reason), entityId: request.VacancyId);
    }

    private static ValueTask PublishLaneAsync(IDbContextOutbox outbox, LetterPreparationLane lane, DateTimeOffset? scheduled = null) =>
        outbox.PublishAsync(lane.Stage == LetterLaneStage.Drafting
            ? (object)new DraftLetterLane { RequestId = lane.RequestId, LaneId = lane.Id, Round = lane.Round }
            : new ReviewLetterLane { RequestId = lane.RequestId, LaneId = lane.Id, Round = lane.Round }, new DeliveryOptions { ScheduledTime = scheduled });

    private static void Validate(Guid id, int round, int version)
    {
        if (id == Guid.Empty || round is < 1 or > 4 || version != 1) throw new UnsupportedLetterPreparationException();
    }
}

[NonTransactional]
public static class LetterPreparationHandler
{
    public static Task Handle(DraftLetterLane command, LetterPreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.LaneId, command.Round, command.Version, LetterLaneStage.Drafting, EnvelopeId(message), ct);
    public static Task Handle(ReviewLetterLane command, LetterPreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.LaneId, command.Round, command.Version, LetterLaneStage.Reviewing, EnvelopeId(message), ct);
    public static Task Handle(ChooseLetterRound command, LetterPreparationWorkflow workflow, CancellationToken ct) => workflow.ChooseAsync(command.RequestId, command.Round, command.Version, ct);
    private static Guid EnvelopeId(IMessageContext context) => context.Envelope?.Id ?? throw new UnsupportedLetterPreparationException();
}
