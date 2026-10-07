using System.Data.Common;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Configuration;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Candidate;

public sealed class StoryPreparationWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IStoryPreparationSteps steps, StorySources sources, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public Task DraftAsync(DraftStory command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, StoryPreparationStage.Drafting, envelopeId, ct);
    public Task PublishAsync(PublishStory command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, StoryPreparationStage.Publishing, envelopeId, ct);

    private async Task RunAsync(Guid id, int version, StoryPreparationStage stage, Guid envelopeId, CancellationToken ct)
    {
        if (version != 1 || id == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedStoryPreparationException();
        ownership.EnsureOwned();
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.StoryPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new UnsupportedStoryPreparationException();
        using var span = Telemetry.StartStage(stage.ToString().ToLowerInvariant(), "story-preparation").Tag("request.id", id);
        try
        {
            if (!Enum.IsDefined(known.Stage)) throw new UnsupportedStoryPreparationException();
            if (known.Stage != stage) return;
            if (stage == StoryPreparationStage.Drafting && (known.ResultJson is not null || known.ResultHash is not null))
                throw new UnsupportedStoryPreparationException();
            var input = StoryPreparationRequests.ReadInput(known);
            steps.EnsureCompatible(input);
            if (stage == StoryPreparationStage.Publishing)
            {
                await FinishAsync(known, ct);
                return;
            }
            if (!await ClaimAsync(known, input, envelopeId, ct)) return;
            ownership.EnsureOwned();
            var result = await steps.DraftAsync(input, ct);
            known.ResultJson = JsonSerializer.Serialize(result, StoryPreparationJsonContext.Default.StoryPreparationResult);
            known.ResultHash = CanonicalJson.Hash(known.ResultJson);
            StoryPreparationRequests.ReadResult(known);
            for (var attempt = 0; ; attempt++)
            {
                try { await CheckpointAsync(known, input, envelopeId, ct); return; }
                catch (Exception error) when (error is IOException or DbException or DbUpdateException)
                {
                    if (attempt >= 2) throw new StoryPreparationCheckpointException(error);
                    ownership.EnsureOwned();
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
                }
            }
        }
        catch (LlmUnavailableException error)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromMinutes(error.Unauthorized ? 30 : 2), false, ct);
        }
        catch (StoryPreparationSupersededException)
        {
            await RetireAsync(known, StoryPreparationStage.Cancelled, "story source, candidate or recipe changed; raw answers retained", ct);
        }
        catch (Exception error) when (error is JsonException or UnsupportedStoryPreparationException or LlmPolicyException or OverflowException)
        {
            await RetireAsync(known, StoryPreparationStage.Failed, "damaged or unsupported story checkpoint quarantined; raw answers retained", ct);
            throw new UnsupportedStoryPreparationException();
        }
        catch (StoryPreparationCheckpointException)
        {
            await RetireAsync(known, StoryPreparationStage.Failed, "paid analysis could not be checkpointed; automatic paid replay refused", ct);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromSeconds(30), true, ct);
        }
    }

    private async Task<bool> ClaimAsync(StoryPreparationRequest known, StoryPreparationInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await StoryPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.StoryPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != StoryPreparationStage.Drafting || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return false;
        CheckSameInput(current, known);
        if (!await StorySources.PreparationMatchesAsync(context, input, ct)) throw new StoryPreparationSupersededException();
        current.ClaimEnvelopeId = envelopeId;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task CheckpointAsync(StoryPreparationRequest known, StoryPreparationInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await StoryPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.StoryPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != StoryPreparationStage.Drafting || current.ClaimEnvelopeId != envelopeId) return;
        CheckSameInput(current, known);
        if (!await StorySources.PreparationMatchesAsync(context, input, ct)) throw new StoryPreparationSupersededException();
        steps.EnsureCompatible(input);
        current.ResultJson = known.ResultJson;
        current.ResultHash = known.ResultHash;
        StoryPreparationRequests.ReadResult(current);
        current.Stage = StoryPreparationStage.Publishing;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        await StoryPreparationRequests.PublishAsync(outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task FinishAsync(StoryPreparationRequest known, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await StoryPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.StoryPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != StoryPreparationStage.Publishing || current.RetryAt > time.GetUtcNow()) return;
        CheckSameInput(current, known);
        var input = StoryPreparationRequests.ReadInput(current);
        var result = StoryPreparationRequests.ReadResult(current);
        steps.EnsureCompatible(input);
        if (!await sources.PublishDraftJoinedAsync(context, outbox, input, result, ct)) throw new StoryPreparationSupersededException();
        current.Stage = StoryPreparationStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task PauseAsync(StoryPreparationRequest known, Guid envelopeId, TimeSpan delay, bool failure, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await StoryPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.StoryPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != known.Stage || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return;
        CheckSameInput(current, known);
        current.ClaimEnvelopeId = null;
        if (failure && ++current.Failures >= 4)
        {
            current.Stage = StoryPreparationStage.Failed;
            current.CompletedAt = time.GetUtcNow();
            current.RetryAt = null;
            current.Error = "story stage failed repeatedly; saved answers and checkpoints retained";
            notifier.Add(context, NotificationKind.Alert, $"⚠️ Подготовка истории #{current.Id} остановлен после ошибок; текст сохранён. История не добавлена в подтверждённые факты.");
        }
        else
        {
            current.RetryAt = time.GetUtcNow().Add(delay);
            current.Error = failure ? "story stage will resume from saved checkpoint" : "LLM unavailable; saved story inputs retained";
            await StoryPreparationRequests.PublishAsync(outbox, current, current.RetryAt);
        }
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task RetireAsync(StoryPreparationRequest known, StoryPreparationStage stage, string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await StoryPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.StoryPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage is StoryPreparationStage.Completed or StoryPreparationStage.Cancelled or StoryPreparationStage.Failed) return;
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
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Подготовка истории #{current.Id} остановлен: исходные данные изменились или проверка не завершена. Ответы сохранены; /stop закрывает этот диалог. История не добавлена в подтверждённые факты.");
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static void CheckSameInput(StoryPreparationRequest current, StoryPreparationRequest known)
    {
        if (current.InputHash != known.InputHash || current.BusinessKey != known.BusinessKey) throw new UnsupportedStoryPreparationException();
        StoryPreparationRequests.ReadInput(current);
    }
}

public static class StoryPreparationHandler
{
    public static Task Handle(DraftStory command, Envelope envelope, StoryPreparationWorkflow workflow, CancellationToken ct) => workflow.DraftAsync(command, envelope.Id, ct);
    public static Task Handle(PublishStory command, Envelope envelope, StoryPreparationWorkflow workflow, CancellationToken ct) => workflow.PublishAsync(command, envelope.Id, ct);
}
