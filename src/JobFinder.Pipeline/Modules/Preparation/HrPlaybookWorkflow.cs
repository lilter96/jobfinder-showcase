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

public sealed class HrPlaybookWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IHrPlaybookPreparationSteps steps, HrPlaybookSources sources, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public Task BuildAsync(BuildHrPlaybook command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, HrPlaybookStage.Building, envelopeId, ct);
    public Task PublishAsync(PublishHrPlaybook command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, HrPlaybookStage.Publishing, envelopeId, ct);

    private async Task RunAsync(Guid id, int version, HrPlaybookStage stage, Guid envelopeId, CancellationToken ct)
    {
        if (version != 1 || id == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedHrPlaybookException();
        ownership.EnsureOwned();
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.HrPlaybookRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new UnsupportedHrPlaybookException();
        using var span = Telemetry.StartStage(stage.ToString().ToLowerInvariant(), "hr-playbook").Tag("request.id", id);
        try
        {
            if (!Enum.IsDefined(known.Stage)) throw new UnsupportedHrPlaybookException();
            if (known.Stage != stage) return;
            if (stage == HrPlaybookStage.Building && (known.ResultJson is not null || known.ResultHash is not null))
                throw new UnsupportedHrPlaybookException();
            var input = HrPlaybookRequests.ReadInput(known);
            steps.EnsureCompatible(input);
            if (stage == HrPlaybookStage.Publishing)
            {
                await FinishAsync(known, ct);
                return;
            }
            if (!await ClaimAsync(known, input, envelopeId, ct)) return;
            ownership.EnsureOwned();
            var result = await steps.BuildAsync(input, ct);
            known.ResultJson = JsonSerializer.Serialize(result, InterviewJsonContext.Default.HrPlaybook);
            known.ResultHash = CanonicalJson.Hash(known.ResultJson);
            HrPlaybookRequests.ReadResult(known);
            for (var attempt = 0; ; attempt++)
            {
                try { await CheckpointAsync(known, input, envelopeId, ct); return; }
                catch (Exception error) when (error is IOException or DbException or DbUpdateException)
                {
                    if (attempt >= 2) throw new HrPlaybookCheckpointException(error);
                    ownership.EnsureOwned();
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
                }
            }
        }
        catch (LlmUnavailableException error)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromMinutes(error.Unauthorized ? 30 : 2), false, ct);
        }
        catch (HrPlaybookSupersededException)
        {
            await RetireAsync(known, HrPlaybookStage.Cancelled, "HR source, candidate or recipe changed; checkpoints retained", ct);
        }
        catch (Exception error) when (error is JsonException or UnsupportedHrPlaybookException or LlmPolicyException or OverflowException)
        {
            await RetireAsync(known, HrPlaybookStage.Failed, "damaged or unsupported HR checkpoint quarantined; checkpoints retained", ct);
            throw new UnsupportedHrPlaybookException();
        }
        catch (HrPlaybookCheckpointException error)
        {
            if (error.PartialJson is not null)
            {
                known.ResultJson = error.PartialJson;
                known.ResultHash = CanonicalJson.Hash(known.ResultJson);
            }
            await RetireAsync(known, HrPlaybookStage.Failed, "HR result could not be checkpointed; automatic paid replay refused", ct);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromSeconds(30), true, ct);
        }
    }

    private async Task<bool> ClaimAsync(HrPlaybookRequest known, HrPlaybookInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await HrPlaybookRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.HrPlaybookRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != HrPlaybookStage.Building || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return false;
        CheckSameInput(current, known);
        if (!await HrPlaybookSources.MatchesAsync(context, input, ct)) throw new HrPlaybookSupersededException();
        current.ClaimEnvelopeId = envelopeId;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task CheckpointAsync(HrPlaybookRequest known, HrPlaybookInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await HrPlaybookRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.HrPlaybookRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != HrPlaybookStage.Building || current.ClaimEnvelopeId != envelopeId)
        {
            if (current.Stage is HrPlaybookStage.Cancelled or HrPlaybookStage.Failed && current.ResultJson is null && known.ResultJson is not null)
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
        if (!await HrPlaybookSources.MatchesAsync(context, input, ct)) throw new HrPlaybookSupersededException();
        steps.EnsureCompatible(input);
        current.ResultJson = known.ResultJson;
        current.ResultHash = known.ResultHash;
        HrPlaybookRequests.ReadResult(current);
        current.Stage = HrPlaybookStage.Publishing;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        await HrPlaybookRequests.PublishAsync(outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task FinishAsync(HrPlaybookRequest known, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await HrPlaybookRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.HrPlaybookRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != HrPlaybookStage.Publishing || current.RetryAt > time.GetUtcNow()) return;
        CheckSameInput(current, known);
        var input = HrPlaybookRequests.ReadInput(current);
        var result = HrPlaybookRequests.ReadResult(current);
        steps.EnsureCompatible(input);
        if (!await sources.PublishJoinedAsync(context, outbox, input, result, ct)) throw new HrPlaybookSupersededException();
        current.Stage = HrPlaybookStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task PauseAsync(HrPlaybookRequest known, Guid envelopeId, TimeSpan delay, bool failure, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await HrPlaybookRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.HrPlaybookRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != known.Stage || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return;
        CheckSameInput(current, known);
        current.ClaimEnvelopeId = null;
        if (failure && ++current.Failures >= 4)
        {
            current.Stage = HrPlaybookStage.Failed;
            current.CompletedAt = time.GetUtcNow();
            current.RetryAt = null;
            current.Error = "HR stage failed repeatedly; saved text and checkpoints retained";
            await HrPlaybookSources.RetireJoinedAsync(context, current.Id, time.GetUtcNow(), ct);
            notifier.Add(context, NotificationKind.Alert, $"⚠️ HR-подготовка #{current.Id} остановлена после ошибок; результаты и исходные данные сохранены.");
        }
        else
        {
            current.RetryAt = time.GetUtcNow().Add(delay);
            current.Error = failure ? "HR stage will resume from saved checkpoint" : "LLM unavailable; saved HR inputs retained";
            await HrPlaybookRequests.PublishAsync(outbox, current, current.RetryAt);
        }
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task RetireAsync(HrPlaybookRequest known, HrPlaybookStage stage, string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await HrPlaybookRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.HrPlaybookRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage is HrPlaybookStage.Completed or HrPlaybookStage.Cancelled or HrPlaybookStage.Failed) return;
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
        await HrPlaybookSources.RetireJoinedAsync(context, current.Id, time.GetUtcNow(), ct);
        notifier.Add(context, NotificationKind.Alert, $"⚠️ HR-подготовка #{current.Id} остановлена: источник изменился или checkpoint не прошёл проверку. Сохранённые данные не удалены.");
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static void CheckSameInput(HrPlaybookRequest current, HrPlaybookRequest known)
    {
        if (current.InputHash != known.InputHash || current.BusinessKey != known.BusinessKey) throw new UnsupportedHrPlaybookException();
        HrPlaybookRequests.ReadInput(current);
    }
}

public static class HrPlaybookHandler
{
    public static Task Handle(BuildHrPlaybook command, Envelope envelope, HrPlaybookWorkflow workflow, CancellationToken ct) => workflow.BuildAsync(command, envelope.Id, ct);
    public static Task Handle(PublishHrPlaybook command, Envelope envelope, HrPlaybookWorkflow workflow, CancellationToken ct) => workflow.PublishAsync(command, envelope.Id, ct);
}
