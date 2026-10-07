using System.Data.Common;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Llm.Configuration;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class RejectionReviewWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IRejectionReviewSteps steps, RejectionReviewSources sources, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public Task AnalyzeAsync(AnalyzeRejectionReview command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, RejectionReviewStage.Analyzing, envelopeId, ct);
    public Task PublishAsync(PublishRejectionReview command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, RejectionReviewStage.Publishing, envelopeId, ct);

    private async Task RunAsync(Guid id, int version, RejectionReviewStage stage, Guid envelopeId, CancellationToken ct)
    {
        if (version != 1 || id == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedRejectionReviewException();
        ownership.EnsureOwned();
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.RejectionReviewRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new UnsupportedRejectionReviewException();
        using var span = Telemetry.StartStage(stage.ToString().ToLowerInvariant(), "rejection-review").Tag("request.id", id);
        try
        {
            if (!Enum.IsDefined(known.Stage)) throw new UnsupportedRejectionReviewException();
            if (known.Stage != stage) return;
            if (stage == RejectionReviewStage.Analyzing && (known.ResultJson is not null || known.ResultHash is not null))
                throw new UnsupportedRejectionReviewException();
            var input = RejectionReviewRequests.ReadInput(known);
            steps.EnsureCompatible(input);
            if (stage == RejectionReviewStage.Publishing)
            {
                await FinishAsync(known, ct);
                return;
            }
            if (!await ClaimAsync(known, input, envelopeId, ct)) return;
            ownership.EnsureOwned();
            var result = await steps.AnalyzeAsync(input, ct);
            known.ResultJson = JsonSerializer.Serialize(result, RejectionReviewJsonContext.Default.RejectionReviewResult);
            known.ResultHash = CanonicalJson.Hash(known.ResultJson);
            RejectionReviewRequests.ReadResult(known);
            for (var attempt = 0; ; attempt++)
            {
                try { await CheckpointAsync(known, input, envelopeId, ct); return; }
                catch (Exception error) when (error is IOException or DbException or DbUpdateException)
                {
                    if (attempt >= 2) throw new RejectionReviewCheckpointException(error);
                    ownership.EnsureOwned();
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
                }
            }
        }
        catch (LlmUnavailableException error)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromMinutes(error.Unauthorized ? 30 : 2), false, ct);
        }
        catch (RejectionReviewSupersededException)
        {
            await RetireAsync(known, RejectionReviewStage.Cancelled, "Rejection review source, candidate or recipe changed; checkpoints retained", ct);
        }
        catch (Exception error) when (error is JsonException or UnsupportedRejectionReviewException or LlmPolicyException or OverflowException)
        {
            await RetireAsync(known, RejectionReviewStage.Failed, "damaged or unsupported Rejection review checkpoint quarantined; checkpoints retained", ct);
            throw new UnsupportedRejectionReviewException();
        }
        catch (RejectionReviewCheckpointException)
        {
            await RetireAsync(known, RejectionReviewStage.Failed, "Rejection review result could not be checkpointed; automatic paid replay refused", ct);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromSeconds(30), true, ct);
        }
    }

    private async Task<bool> ClaimAsync(RejectionReviewRequest known, RejectionReviewInputSnapshot input, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await RejectionReviewRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.RejectionReviewRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != RejectionReviewStage.Analyzing || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return false;
        CheckSameInput(current, known);
        if (!await sources.MatchesAsync(context, input, ct)) throw new RejectionReviewSupersededException();
        current.ClaimEnvelopeId = envelopeId;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task CheckpointAsync(RejectionReviewRequest known, RejectionReviewInputSnapshot input, Guid envelopeId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await RejectionReviewRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.RejectionReviewRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != RejectionReviewStage.Analyzing || current.ClaimEnvelopeId != envelopeId)
        {
            if (current.Stage is RejectionReviewStage.Cancelled or RejectionReviewStage.Failed && current.ResultJson is null && known.ResultJson is not null)
            {
                CheckSameInput(current, known);
                current.ResultJson = known.ResultJson;
                current.ResultHash = known.ResultHash;
                ownership.EnsureOwned();
                await notifier.SaveAndFlushAsync(context, outbox, ct);
            }
            return;
        }
        CheckSameInput(current, known);
        if (!await sources.MatchesAsync(context, input, ct)) throw new RejectionReviewSupersededException();
        steps.EnsureCompatible(input);
        current.ResultJson = known.ResultJson;
        current.ResultHash = known.ResultHash;
        RejectionReviewRequests.ReadResult(current);
        current.Stage = RejectionReviewStage.Publishing;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        await RejectionReviewRequests.PublishAsync(outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task FinishAsync(RejectionReviewRequest known, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await RejectionReviewRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.RejectionReviewRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != RejectionReviewStage.Publishing || current.RetryAt > time.GetUtcNow()) return;
        CheckSameInput(current, known);
        var input = RejectionReviewRequests.ReadInput(current);
        var result = RejectionReviewRequests.ReadResult(current);
        steps.EnsureCompatible(input);
        if (!await sources.PublishJoinedAsync(context, outbox, input, result, ct)) throw new RejectionReviewSupersededException();
        current.Stage = RejectionReviewStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task PauseAsync(RejectionReviewRequest known, Guid envelopeId, TimeSpan delay, bool failure, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await RejectionReviewRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.RejectionReviewRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != known.Stage || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return;
        CheckSameInput(current, known);
        current.ClaimEnvelopeId = null;
        if (failure && ++current.Failures >= 4)
        {
            current.Stage = RejectionReviewStage.Failed;
            current.CompletedAt = time.GetUtcNow();
            current.RetryAt = null;
            current.Error = "Rejection review stage failed repeatedly; saved text and checkpoints retained";
            notifier.Add(context, NotificationKind.Alert, $"⚠️ Разбор отказов #{current.Id} остановлен после ошибок; результаты и исходные данные сохранены.");
        }
        else
        {
            current.RetryAt = time.GetUtcNow().Add(delay);
            current.Error = failure ? "Rejection review stage will resume from saved checkpoint" : "LLM unavailable; saved Rejection review inputs retained";
            await RejectionReviewRequests.PublishAsync(outbox, current, current.RetryAt);
        }
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task RetireAsync(RejectionReviewRequest known, RejectionReviewStage stage, string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await RejectionReviewRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.RejectionReviewRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage is RejectionReviewStage.Completed or RejectionReviewStage.Cancelled or RejectionReviewStage.Failed) return;
        if (current.ResultJson is null && known.ResultJson is not null)
        {
            current.ResultJson = known.ResultJson;
            current.ResultHash = known.ResultHash;
        }
        current.Stage = stage;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = reason;
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Разбор отказов #{current.Id} остановлен: источник изменился или checkpoint не прошёл проверку. Сохранённые данные не удалены.");
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static void CheckSameInput(RejectionReviewRequest current, RejectionReviewRequest known)
    {
        if (current.InputHash != known.InputHash || current.BusinessKey != known.BusinessKey) throw new UnsupportedRejectionReviewException();
        RejectionReviewRequests.ReadInput(current);
    }
}

public static class RejectionReviewHandler
{
    public static Task Handle(AnalyzeRejectionReview command, Envelope envelope, RejectionReviewWorkflow workflow, CancellationToken ct) => workflow.AnalyzeAsync(command, envelope.Id, ct);
    public static Task Handle(PublishRejectionReview command, Envelope envelope, RejectionReviewWorkflow workflow, CancellationToken ct) => workflow.PublishAsync(command, envelope.Id, ct);
}
