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

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class MockPreparationWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IMockPreparationSteps steps, MockSources sources, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public Task PrepareAsync(PrepareMock command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, MockPreparationStage.Preparing, envelopeId, ct);
    public Task PublishAsync(PublishMock command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, MockPreparationStage.Publishing, envelopeId, ct);

    private async Task RunAsync(Guid id, int version, MockPreparationStage stage, Guid envelopeId, CancellationToken ct)
    {
        if (version != 1 || id == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedMockPreparationException();
        ownership.EnsureOwned();
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.MockPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new UnsupportedMockPreparationException();
        using var span = Telemetry.StartStage(stage.ToString().ToLowerInvariant(), "mock-preparation").Tag("request.id", id);
        try
        {
            if (!Enum.IsDefined(known.Stage)) throw new UnsupportedMockPreparationException();
            if (known.Stage != stage) return;
            if (stage == MockPreparationStage.Preparing && (known.ResultJson is not null || known.ResultHash is not null))
                throw new UnsupportedMockPreparationException();
            var input = MockPreparationRequests.ReadInput(known);
            steps.EnsureCompatible(input);
            if (stage == MockPreparationStage.Publishing)
            {
                await FinishAsync(known, ct);
                return;
            }
            if (!await ClaimAsync(known, input, envelopeId, ct)) return;
            ownership.EnsureOwned();
            var result = await steps.PrepareAsync(input, ct);
            known.ResultJson = JsonSerializer.Serialize(result, MockPreparationJsonContext.Default.MockPreparationResult);
            known.ResultHash = CanonicalJson.Hash(known.ResultJson);
            MockPreparationRequests.ReadResult(known);
            for (var attempt = 0; ; attempt++)
            {
                try { await CheckpointAsync(known, input, envelopeId, ct); return; }
                catch (Exception error) when (error is IOException or DbException or DbUpdateException)
                {
                    if (attempt >= 2) throw new MockPreparationCheckpointException(error);
                    ownership.EnsureOwned();
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
                }
            }
        }
        catch (LlmUnavailableException error)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromMinutes(error.Unauthorized ? 30 : 2), false, ct);
        }
        catch (MockPreparationSupersededException)
        {
            await RetireAsync(known, MockPreparationStage.Cancelled, "mock source, candidate or recipe changed; raw answers retained", ct);
        }
        catch (Exception error) when (error is JsonException or UnsupportedMockPreparationException or LlmPolicyException or OverflowException)
        {
            await RetireAsync(known, MockPreparationStage.Failed, "damaged or unsupported mock checkpoint quarantined; raw answers retained", ct);
            throw new UnsupportedMockPreparationException();
        }
        catch (MockPreparationCheckpointException)
        {
            await RetireAsync(known, MockPreparationStage.Failed, "paid analysis could not be checkpointed; automatic paid replay refused", ct);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromSeconds(30), true, ct);
        }
    }

    private async Task<bool> ClaimAsync(MockPreparationRequest known, MockPreparationInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await MockPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.MockPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != MockPreparationStage.Preparing || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return false;
        CheckSameInput(current, known);
        if (!await MockSources.MatchesAsync(context, input, ct)) throw new MockPreparationSupersededException();
        current.ClaimEnvelopeId = envelopeId;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task CheckpointAsync(MockPreparationRequest known, MockPreparationInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await MockPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.MockPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != MockPreparationStage.Preparing || current.ClaimEnvelopeId != envelopeId) return;
        CheckSameInput(current, known);
        if (!await MockSources.MatchesAsync(context, input, ct)) throw new MockPreparationSupersededException();
        steps.EnsureCompatible(input);
        current.ResultJson = known.ResultJson;
        current.ResultHash = known.ResultHash;
        MockPreparationRequests.ReadResult(current);
        current.Stage = MockPreparationStage.Publishing;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        await MockPreparationRequests.PublishAsync(outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task FinishAsync(MockPreparationRequest known, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await MockPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.MockPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != MockPreparationStage.Publishing || current.RetryAt > time.GetUtcNow()) return;
        CheckSameInput(current, known);
        var input = MockPreparationRequests.ReadInput(current);
        var result = MockPreparationRequests.ReadResult(current);
        steps.EnsureCompatible(input);
        if (!await sources.PublishJoinedAsync(context, outbox, input, result, ct)) throw new MockPreparationSupersededException();
        current.Stage = MockPreparationStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task PauseAsync(MockPreparationRequest known, Guid envelopeId, TimeSpan delay, bool failure, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await MockPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.MockPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != known.Stage || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return;
        CheckSameInput(current, known);
        current.ClaimEnvelopeId = null;
        if (failure && ++current.Failures >= 4)
        {
            current.Stage = MockPreparationStage.Failed;
            current.CompletedAt = time.GetUtcNow();
            current.RetryAt = null;
            current.Error = "mock stage failed repeatedly; saved answers and checkpoints retained";
            notifier.Add(context, NotificationKind.Alert, $"⚠️ Тренировка #{current.SessionId} остановлена; вопросы, ответы и контрольные точки сохранены. /stop закрывает только этот диалог.");
        }
        else
        {
            current.RetryAt = time.GetUtcNow().Add(delay);
            current.Error = failure ? "mock stage will resume from saved checkpoint" : "LLM unavailable; saved mock inputs retained";
            await MockPreparationRequests.PublishAsync(outbox, current, current.RetryAt);
        }
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task RetireAsync(MockPreparationRequest known, MockPreparationStage stage, string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await MockPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.MockPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage is MockPreparationStage.Completed or MockPreparationStage.Cancelled or MockPreparationStage.Failed) return;
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
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Тренировка #{current.SessionId} остановлена; вопросы, ответы и контрольные точки сохранены. /stop закрывает только этот диалог.");
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static void CheckSameInput(MockPreparationRequest current, MockPreparationRequest known)
    {
        if (current.InputHash != known.InputHash || current.BusinessKey != known.BusinessKey) throw new UnsupportedMockPreparationException();
        MockPreparationRequests.ReadInput(current);
    }
}

public static class MockPreparationHandler
{
    public static Task Handle(PrepareMock command, Envelope envelope, MockPreparationWorkflow workflow, CancellationToken ct) => workflow.PrepareAsync(command, envelope.Id, ct);
    public static Task Handle(PublishMock command, Envelope envelope, MockPreparationWorkflow workflow, CancellationToken ct) => workflow.PublishAsync(command, envelope.Id, ct);
}
