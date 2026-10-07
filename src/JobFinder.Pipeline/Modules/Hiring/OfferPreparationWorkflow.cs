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

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class OfferPreparationWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IOfferPreparationSteps steps, OfferSources sources, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public Task AnalyzeAsync(AnalyzeOffer command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, OfferPreparationStage.Analyzing, envelopeId, ct);
    public Task PublishAsync(PublishOffer command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, OfferPreparationStage.Publishing, envelopeId, ct);

    private async Task RunAsync(Guid id, int version, OfferPreparationStage stage, Guid envelopeId, CancellationToken ct)
    {
        if (version != 1 || id == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedOfferPreparationException();
        ownership.EnsureOwned();
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.OfferPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new UnsupportedOfferPreparationException();
        using var span = Telemetry.StartStage(stage.ToString().ToLowerInvariant(), "offer-preparation").Tag("request.id", id);
        try
        {
            if (!Enum.IsDefined(known.Stage)) throw new UnsupportedOfferPreparationException();
            if (known.Stage != stage) return;
            if (stage == OfferPreparationStage.Analyzing && (known.ResultJson is not null || known.ResultHash is not null || known.OfferId is not null))
                throw new UnsupportedOfferPreparationException();
            var input = OfferPreparationRequests.ReadInput(known);
            steps.EnsureCompatible(input);
            if (stage == OfferPreparationStage.Publishing)
            {
                await FinishAsync(known, ct);
                return;
            }
            if (!await ClaimAsync(known, input, envelopeId, ct)) return;
            ownership.EnsureOwned();
            var result = await steps.AnalyzeAsync(input, ct);
            known.ResultJson = JsonSerializer.Serialize(result, OfferPreparationJsonContext.Default.OfferPreparationResult);
            known.ResultHash = CanonicalJson.Hash(known.ResultJson);
            OfferPreparationRequests.ReadResult(known);
            for (var attempt = 0; ; attempt++)
            {
                try { await CheckpointAsync(known, input, envelopeId, ct); return; }
                catch (Exception error) when (error is IOException or DbException or DbUpdateException)
                {
                    if (attempt >= 2) throw new OfferPreparationCheckpointException(error);
                    ownership.EnsureOwned();
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
                }
            }
        }
        catch (LlmUnavailableException error)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromMinutes(error.Unauthorized ? 30 : 2), false, ct);
        }
        catch (OfferPreparationSupersededException)
        {
            await RetireAsync(known, OfferPreparationStage.Cancelled, "offer source, candidate or recipe changed; raw text retained", ct);
        }
        catch (Exception error) when (error is JsonException or UnsupportedOfferPreparationException or LlmPolicyException or OverflowException)
        {
            await RetireAsync(known, OfferPreparationStage.Failed, "damaged or unsupported offer checkpoint quarantined; raw text retained", ct);
            throw new UnsupportedOfferPreparationException();
        }
        catch (OfferPreparationCheckpointException)
        {
            await RetireAsync(known, OfferPreparationStage.Failed, "paid analysis could not be checkpointed; automatic paid replay refused", ct);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromSeconds(30), true, ct);
        }
    }

    private async Task<bool> ClaimAsync(OfferPreparationRequest known, OfferPreparationInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await OfferPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.OfferPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != OfferPreparationStage.Analyzing || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return false;
        CheckSameInput(current, known);
        if (!await OfferSources.MatchesAsync(context, input.Source, ct)) throw new OfferPreparationSupersededException();
        current.ClaimEnvelopeId = envelopeId;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task CheckpointAsync(OfferPreparationRequest known, OfferPreparationInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await OfferPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.OfferPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != OfferPreparationStage.Analyzing || current.ClaimEnvelopeId != envelopeId) return;
        CheckSameInput(current, known);
        if (!await OfferSources.MatchesAsync(context, input.Source, ct)) throw new OfferPreparationSupersededException();
        steps.EnsureCompatible(input);
        current.ResultJson = known.ResultJson;
        current.ResultHash = known.ResultHash;
        OfferPreparationRequests.ReadResult(current);
        current.Stage = OfferPreparationStage.Publishing;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        await OfferPreparationRequests.PublishAsync(outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task FinishAsync(OfferPreparationRequest known, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await OfferPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.OfferPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != OfferPreparationStage.Publishing || current.RetryAt > time.GetUtcNow()) return;
        CheckSameInput(current, known);
        var input = OfferPreparationRequests.ReadInput(current);
        var result = OfferPreparationRequests.ReadResult(current);
        steps.EnsureCompatible(input);
        var published = await sources.PublishJoinedAsync(context, outbox, input.Source, result.Analysis, result.MonthlyNetUsd, ct, input, result);
        if (!published.Result.Ok) throw new OfferPreparationSupersededException();
        current.OfferId = published.OfferId;
        current.Stage = OfferPreparationStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task PauseAsync(OfferPreparationRequest known, Guid envelopeId, TimeSpan delay, bool failure, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await OfferPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.OfferPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != known.Stage || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return;
        CheckSameInput(current, known);
        current.ClaimEnvelopeId = null;
        if (failure && ++current.Failures >= 4)
        {
            current.Stage = OfferPreparationStage.Failed;
            current.CompletedAt = time.GetUtcNow();
            current.RetryAt = null;
            current.Error = "offer stage failed repeatedly; saved text and checkpoints retained";
            notifier.Add(context, NotificationKind.Alert, $"⚠️ Разбор оффера #{current.Id} остановлен после ошибок; текст сохранён. Работодателю ничего не отправлено.");
        }
        else
        {
            current.RetryAt = time.GetUtcNow().Add(delay);
            current.Error = failure ? "offer stage will resume from saved checkpoint" : "LLM unavailable; saved offer inputs retained";
            await OfferPreparationRequests.PublishAsync(outbox, current, current.RetryAt);
        }
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task RetireAsync(OfferPreparationRequest known, OfferPreparationStage stage, string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await OfferPreparationRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.OfferPreparationRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage is OfferPreparationStage.Completed or OfferPreparationStage.Cancelled or OfferPreparationStage.Failed) return;
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
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Разбор оффера #{current.Id} остановлен: исходные данные изменились или проверка не завершена. Текст сохранён; /stop закрывает этот диалог. Работодателю ничего не отправлено.");
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static void CheckSameInput(OfferPreparationRequest current, OfferPreparationRequest known)
    {
        if (current.InputHash != known.InputHash || current.BusinessKey != known.BusinessKey) throw new UnsupportedOfferPreparationException();
        OfferPreparationRequests.ReadInput(current);
    }
}

public static class OfferPreparationHandler
{
    public static Task Handle(AnalyzeOffer command, Envelope envelope, OfferPreparationWorkflow workflow, CancellationToken ct) => workflow.AnalyzeAsync(command, envelope.Id, ct);
    public static Task Handle(PublishOffer command, Envelope envelope, OfferPreparationWorkflow workflow, CancellationToken ct) => workflow.PublishAsync(command, envelope.Id, ct);
}
