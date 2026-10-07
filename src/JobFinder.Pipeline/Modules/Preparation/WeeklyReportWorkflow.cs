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

public sealed class WeeklyReportWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IWeeklyReportSteps steps, WeeklyReportSources sources, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public Task PrepareAsync(PrepareWeeklyReport command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, WeeklyReportStage.Preparing, envelopeId, ct);
    public Task PublishAsync(PublishWeeklyReport command, Guid envelopeId, CancellationToken ct) =>
        RunAsync(command.RequestId, command.Version, WeeklyReportStage.Publishing, envelopeId, ct);

    private async Task RunAsync(Guid id, int version, WeeklyReportStage stage, Guid envelopeId, CancellationToken ct)
    {
        if (version != 1 || id == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedWeeklyReportException();
        ownership.EnsureOwned();
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.WeeklyReportRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new UnsupportedWeeklyReportException();
        using var span = Telemetry.StartStage(stage.ToString().ToLowerInvariant(), "weekly-report").Tag("request.id", id);
        try
        {
            if (!Enum.IsDefined(known.Stage)) throw new UnsupportedWeeklyReportException();
            if (known.Stage != stage) return;
            if (stage == WeeklyReportStage.Preparing && (known.ResultJson is not null || known.ResultHash is not null))
                throw new UnsupportedWeeklyReportException();
            var input = WeeklyReportRequests.ReadInput(known);
            steps.EnsureCompatible(input);
            if (stage == WeeklyReportStage.Publishing)
            {
                await FinishAsync(known, ct);
                return;
            }
            if (!await ClaimAsync(known, input, envelopeId, ct)) return;
            ownership.EnsureOwned();
            var result = await steps.PrepareAsync(input, ct);
            known.ResultJson = JsonSerializer.Serialize(result, WeeklyReportJsonContext.Default.WeeklyReportResult);
            known.ResultHash = CanonicalJson.Hash(known.ResultJson);
            WeeklyReportRequests.ReadResult(known);
            for (var attempt = 0; ; attempt++)
            {
                try { await CheckpointAsync(known, input, envelopeId, ct); return; }
                catch (Exception error) when (error is IOException or DbException or DbUpdateException)
                {
                    if (attempt >= 2) throw new WeeklyReportCheckpointException(error);
                    ownership.EnsureOwned();
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), time, ct);
                }
            }
        }
        catch (LlmUnavailableException error)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromMinutes(error.Unauthorized ? 30 : 2), false, ct);
        }
        catch (WeeklyReportSupersededException)
        {
            await RetireAsync(known, WeeklyReportStage.Cancelled, "Weekly report source, candidate or recipe changed; checkpoints retained", ct);
        }
        catch (Exception error) when (error is JsonException or UnsupportedWeeklyReportException or LlmPolicyException or OverflowException)
        {
            await RetireAsync(known, WeeklyReportStage.Failed, "damaged or unsupported Weekly report checkpoint quarantined; checkpoints retained", ct);
            throw new UnsupportedWeeklyReportException();
        }
        catch (WeeklyReportCheckpointException)
        {
            await RetireAsync(known, WeeklyReportStage.Failed, "Weekly report result could not be checkpointed; automatic paid replay refused", ct);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromSeconds(30), true, ct);
        }
    }

    private async Task<bool> ClaimAsync(WeeklyReportRequest known, WeeklyReportInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await WeeklyReportRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.WeeklyReportRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != WeeklyReportStage.Preparing || current.RetryAt > time.GetUtcNow()
            || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return false;
        CheckSameInput(current, known);
        if (!await sources.MatchesAsync(context, input, ct)) throw new WeeklyReportSupersededException();
        current.ClaimEnvelopeId = envelopeId;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task CheckpointAsync(WeeklyReportRequest known, WeeklyReportInput input, Guid envelopeId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await WeeklyReportRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.WeeklyReportRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != WeeklyReportStage.Preparing || current.ClaimEnvelopeId != envelopeId)
        {
            if (current.Stage is WeeklyReportStage.Cancelled or WeeklyReportStage.Failed && current.ResultJson is null && known.ResultJson is not null)
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
        if (!await sources.MatchesAsync(context, input, ct)) throw new WeeklyReportSupersededException();
        steps.EnsureCompatible(input);
        current.ResultJson = known.ResultJson;
        current.ResultHash = known.ResultHash;
        WeeklyReportRequests.ReadResult(current);
        current.Stage = WeeklyReportStage.Publishing;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        await WeeklyReportRequests.PublishAsync(outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task FinishAsync(WeeklyReportRequest known, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await WeeklyReportRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.WeeklyReportRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != WeeklyReportStage.Publishing || current.RetryAt > time.GetUtcNow()) return;
        CheckSameInput(current, known);
        var input = WeeklyReportRequests.ReadInput(current);
        var result = WeeklyReportRequests.ReadResult(current);
        steps.EnsureCompatible(input);
        if (!await sources.PublishJoinedAsync(context, outbox, input, result, ct)) throw new WeeklyReportSupersededException();
        current.Stage = WeeklyReportStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task PauseAsync(WeeklyReportRequest known, Guid envelopeId, TimeSpan delay, bool failure, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await WeeklyReportRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.WeeklyReportRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != known.Stage || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return;
        CheckSameInput(current, known);
        current.ClaimEnvelopeId = null;
        if (failure && ++current.Failures >= 4)
        {
            current.Stage = WeeklyReportStage.Failed;
            current.CompletedAt = time.GetUtcNow();
            current.RetryAt = null;
            current.Error = "Weekly report stage failed repeatedly; saved text and checkpoints retained";
            notifier.Add(context, NotificationKind.Alert, $"⚠️ Советы hh для отчёта #{current.Id} остановлены после ошибок; статистика уже сохранена.");

        }
        else
        {
            current.RetryAt = time.GetUtcNow().Add(delay);
            current.Error = failure ? "Weekly report stage will resume from saved checkpoint" : "LLM unavailable; saved Weekly report inputs retained";
            await WeeklyReportRequests.PublishAsync(outbox, current, current.RetryAt);
        }
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task RetireAsync(WeeklyReportRequest known, WeeklyReportStage stage, string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await WeeklyReportRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.WeeklyReportRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage is WeeklyReportStage.Completed or WeeklyReportStage.Cancelled or WeeklyReportStage.Failed) return;
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
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Недельный отчёт #{current.Id} остановлен: источник изменился или checkpoint не прошёл проверку. Сохранённые данные не удалены.");
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static void CheckSameInput(WeeklyReportRequest current, WeeklyReportRequest known)
    {
        if (current.InputHash != known.InputHash || current.BusinessKey != known.BusinessKey) throw new UnsupportedWeeklyReportException();
        WeeklyReportRequests.ReadInput(current);
    }
}

public static class WeeklyReportHandler
{
    public static Task Handle(PrepareWeeklyReport command, Envelope envelope, WeeklyReportWorkflow workflow, CancellationToken ct) => workflow.PrepareAsync(command, envelope.Id, ct);
    public static Task Handle(PublishWeeklyReport command, Envelope envelope, WeeklyReportWorkflow workflow, CancellationToken ct) => workflow.PublishAsync(command, envelope.Id, ct);
}
