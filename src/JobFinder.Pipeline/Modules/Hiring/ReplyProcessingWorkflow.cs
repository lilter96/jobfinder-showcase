using System.Data.Common;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class ReplyProcessingWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, JobFinderLlm llm,
    IReplyProcessingSteps steps, ReplyProcessingSources sources, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public async Task RunAsync(ProcessReply command, Guid envelopeId, CancellationToken ct)
    {
        if (command.Version != 1 || command.RequestId == Guid.Empty || envelopeId == Guid.Empty
            || command.Stage is not (ReplyProcessingStage.Capturing or ReplyProcessingStage.Fast or ReplyProcessingStage.Deep or ReplyProcessingStage.Publishing)
            || command.DeepAttempt is < 0 or > 2) throw new UnsupportedReplyProcessingException();
        ownership.EnsureOwned();
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.ReplyProcessingRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == command.RequestId, ct);
        if (known is null) throw new UnsupportedReplyProcessingException();
        if (!Enum.IsDefined(known.Mode) || !Enum.IsDefined(known.Stage)
            || known.Stage == ReplyProcessingStage.Capturing && known.Mode != ReplyProcessingMode.Incoming)
        {
            await RetireAsync(known, "unsupported reply root quarantined", ReplyProcessingStage.Failed, ct);
            throw new UnsupportedReplyProcessingException();
        }
        if (known.Stage != command.Stage || known.DeepAttempt != command.DeepAttempt) return;
        using var span = Telemetry.StartStage(command.Stage.ToString().ToLowerInvariant(), "reply-processing")
            .Tag("request.id", known.Id).Tag("application.id", known.ApplicationId).Tag("vacancy.id", known.VacancyId).Tag("deep.attempt", known.DeepAttempt);
        try
        {
            if (command.Stage == ReplyProcessingStage.Capturing)
            {
                await CaptureAsync(known, ct);
                return;
            }
            if (command.Stage == ReplyProcessingStage.Publishing)
            {
                await PublishAsync(known, ct);
                return;
            }
            var input = ReplyProcessingRequests.ReadInput(known);
            if (!await ClaimAsync(known, input, envelopeId, ct)) return;
            ownership.EnsureOwned();
            var best = known.BestJson is null ? null : ReplyProcessingRequests.ReadResult(known.BestJson);
            var result = await steps.AnalyzeAsync(input, best, command.Stage == ReplyProcessingStage.Deep, ct);
            ReplyProcessingRequests.ReadResult(ReplyProcessingRequests.Serialize(result));
            ownership.EnsureOwned();
            await CheckpointAsync(known, input, envelopeId, result, ct);
        }
        catch (LlmUnavailableException error)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromMinutes(error.Unauthorized ? 30 : 2), ct);
        }
        catch (ReplyProcessingSupersededException)
        {
            await RetireAsync(known, "reply source, candidate or recipe changed; raw input retained", ReplyProcessingStage.Cancelled, ct);
        }
        catch (Exception error) when (error is JsonException or UnsupportedReplyProcessingException or JobFinder.Llm.Configuration.LlmPolicyException)
        {
            await RetireAsync(known, "damaged or unsupported reply checkpoint quarantined", ReplyProcessingStage.Failed, ct);
            throw new UnsupportedReplyProcessingException();
        }
    }

    private async Task CaptureAsync(ReplyProcessingRequest known, CancellationToken ct)
    {
        var raw = ReplyProcessingRequests.ReadRaw(known);
        var operation = await llm.ForOperationAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ReplyProcessingRequests.LockSourceAsync(context, known.VacancyId, known.ApplicationId, ct);
        await ReplyProcessingRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.ReplyProcessingRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != ReplyProcessingStage.Capturing) return;
        var first = await context.ReplyProcessingRequests.Where(row => row.Mode == ReplyProcessingMode.Incoming && row.ApplicationId == current.ApplicationId
            && (row.Stage == ReplyProcessingStage.Capturing || row.Stage == ReplyProcessingStage.Fast || row.Stage == ReplyProcessingStage.Deep || row.Stage == ReplyProcessingStage.Publishing))
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).Select(row => row.Id).FirstAsync(ct);
        if (first != current.Id)
        {
            current.RetryAt = time.GetUtcNow().AddSeconds(2);
            await ReplyProcessingRequests.PublishAsync(outbox, current, current.RetryAt);
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return;
        }
        var input = await sources.CaptureAsync(context, current, raw, operation, ct);
        current.InputJson = ReplyProcessingRequests.Serialize(input);
        current.InputHash = CanonicalJson.Hash(current.InputJson);
        current.Stage = ReplyProcessingStage.Fast;
        current.RetryAt = null;
        await ReplyProcessingRequests.PublishAsync(outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task<bool> ClaimAsync(ReplyProcessingRequest known, ReplyProcessingInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await ReplyProcessingRequests.LockSourceAsync(context, known.VacancyId, known.ApplicationId, ct);
        await ReplyProcessingRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.ReplyProcessingRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (!SameStage(current, known) || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId
            || current.RetryAt > time.GetUtcNow()) return false;
        if (!await ReplyProcessingRequests.MatchesAsync(context, input, ct)) throw new ReplyProcessingSupersededException();
        steps.EnsureCompatible(input);
        current.ClaimEnvelopeId = envelopeId;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task CheckpointAsync(ReplyProcessingRequest known, ReplyProcessingInput input, Guid envelopeId, ReplyProcessingResult result, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SaveResultAsync(known, input, envelopeId, result, ct);
                return;
            }
            catch (Exception error) when (error is IOException or DbException or DbUpdateException)
            {
                if (attempt >= 2) throw;
                ownership.EnsureOwned();
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
            }
        }
    }

    private async Task SaveResultAsync(ReplyProcessingRequest known, ReplyProcessingInput input, Guid envelopeId, ReplyProcessingResult result, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ReplyProcessingRequests.LockSourceAsync(context, known.VacancyId, known.ApplicationId, ct);
        await ReplyProcessingRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.ReplyProcessingRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (!SameStage(current, known) || current.ClaimEnvelopeId != envelopeId) return;
        if (!await ReplyProcessingRequests.MatchesAsync(context, input, ct)) throw new ReplyProcessingSupersededException();
        steps.EnsureCompatible(input);
        var best = current.BestJson is null ? null : ReplyProcessingRequests.ReadResult(current.BestJson);
        var hasDraft = !string.IsNullOrWhiteSpace(result.Analysis.Draft);
        if (best is null || hasDraft && result.Problems.Count <= best.Problems.Count) best = result;
        current.BestJson = ReplyProcessingRequests.Serialize(best);
        if (current.Stage == ReplyProcessingStage.Fast && hasDraft && (result.Problems.Count > 0 || DialoguePolicy.IsConsidered(result.Analysis)))
        {
            current.Stage = ReplyProcessingStage.Deep;
            current.DeepAttempt = 1;
        }
        else if (current.Stage == ReplyProcessingStage.Deep && hasDraft && result.Problems.Count > 0 && current.DeepAttempt < 2)
            current.DeepAttempt++;
        else current.Stage = ReplyProcessingStage.Publishing;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        await ReplyProcessingRequests.PublishAsync(outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task PublishAsync(ReplyProcessingRequest known, CancellationToken ct)
    {
        var input = ReplyProcessingRequests.ReadInput(known);
        steps.EnsureCompatible(input);
        var result = ReplyProcessingRequests.ReadResult(known.BestJson);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ReplyProcessingRequests.LockSourceAsync(context, known.VacancyId, known.ApplicationId, ct);
        await ReplyProcessingRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.ReplyProcessingRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (!SameStage(current, known)) return;
        if (!await sources.PublishAsync(context, outbox, current, input, result, ct)) throw new ReplyProcessingSupersededException();
        current.Stage = ReplyProcessingStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        current.Error = null;
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task PauseAsync(ReplyProcessingRequest known, Guid envelopeId, TimeSpan delay, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ReplyProcessingRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.ReplyProcessingRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (!SameStage(current, known) || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return;
        current.ClaimEnvelopeId = null;
        current.RetryAt = time.GetUtcNow().Add(delay);
        current.Error = "LLM unavailable; saved reply stage will resume";
        await ReplyProcessingRequests.PublishAsync(outbox, current, current.RetryAt);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task RetireAsync(ReplyProcessingRequest known, string reason, ReplyProcessingStage stage, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await ReplyProcessingRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.ReplyProcessingRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage is ReplyProcessingStage.Completed or ReplyProcessingStage.Cancelled or ReplyProcessingStage.Failed) return;
        current.Stage = stage;
        current.ClaimEnvelopeId = null;
        current.CompletedAt = time.GetUtcNow();
        current.Error = reason;
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Входящий ответ / черновик #{current.Id}: {TextUtil.EscapeHtml(reason)}. Исходное сообщение сохранено. Это не подтверждение доставки.", entityId: current.ApplicationId);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
    }

    private static bool SameStage(ReplyProcessingRequest current, ReplyProcessingRequest known) => current.Stage == known.Stage && current.DeepAttempt == known.DeepAttempt;
}

public static class ReplyProcessingHandler
{
    public static Task Handle(ProcessReply command, Envelope envelope, ReplyProcessingWorkflow workflow, CancellationToken ct) =>
        workflow.RunAsync(command, envelope.Id, ct);
}
