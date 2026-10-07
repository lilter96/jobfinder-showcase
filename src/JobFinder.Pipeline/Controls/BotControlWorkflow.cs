using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Controls;

public sealed class BotControlWorkflow(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    IBotControlExecutor executor, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public async Task RunAsync(ProcessBotControl command, Guid envelopeId, CancellationToken ct)
    {
        if (command.Version != 1 || command.RequestId == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedBotControlException();
        ownership.EnsureOwned();
        BotControlInput input;
        string capturedHash;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            var known = await context.BotControlRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == command.RequestId, ct)
                ?? throw new UnsupportedBotControlException();
            await using var scope = scopes.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            outbox.Enroll(context);
            await BotControls.LockAsync(context, known.BotId, ct);
            var current = await context.BotControlRequests.SingleAsync(row => row.Id == known.Id, ct);
            if (current.Stage is BotControlStage.Completed or BotControlStage.Reconciled or BotControlStage.OutcomeUnknown or BotControlStage.Quarantined) return;
            if (current.Stage == BotControlStage.Executing)
            {
                await StopAsync(context, current, BotControlStage.OutcomeUnknown, "interrupted bot control execution; no automatic replay", ct);
                return;
            }
            try
            {
                if (current.Stage != BotControlStage.Pending) throw new UnsupportedBotControlException();
                input = BotControls.Read(current);
                capturedHash = current.InputHash;
            }
            catch (UnsupportedBotControlException)
            {
                await StopAsync(context, current, BotControlStage.Quarantined, "unsupported bot control capture retained", ct);
                return;
            }
            bool available;
            try { available = executor.CanExecute(input); }
            catch (UnsupportedBotControlException)
            {
                await StopAsync(context, current, BotControlStage.Quarantined, "incompatible bot update or owner witness", ct);
                return;
            }
            if (!available || await context.BotControlRequests.AnyAsync(row => row.BotId == current.BotId && row.UpdateId < current.UpdateId
                && row.Stage != BotControlStage.Completed && row.Stage != BotControlStage.Reconciled, ct))
            {
                await outbox.ScheduleAsync(command, time.GetUtcNow().AddSeconds(30));
                await outbox.SaveChangesAndFlushMessagesAsync(ct);
                return;
            }
            current.Stage = BotControlStage.Executing;
            current.ClaimEnvelopeId = envelopeId;
            ownership.EnsureOwned();
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
        }
        using var span = Telemetry.StartStage("execute", "bot-control").Tag("request.id", command.RequestId).Tag("bot.id", input.BotId).Tag("update.id", input.UpdateId);
        try
        {
            ownership.EnsureOwned();
            await executor.ExecuteAsync(input, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await BotControls.LockAsync(context, input.BotId, ct);
            var current = await context.BotControlRequests.SingleAsync(row => row.Id == command.RequestId, ct);
            if (current.Stage == BotControlStage.Executing && current.ClaimEnvelopeId == envelopeId)
                await StopAsync(context, current, BotControlStage.OutcomeUnknown, "bot handler failed after durable claim; effects require checked evidence", ct);
            return;
        }
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await BotControls.LockAsync(context, input.BotId, ct);
            var current = await context.BotControlRequests.SingleAsync(row => row.Id == command.RequestId, ct);
            if (current.Stage != BotControlStage.Executing || current.ClaimEnvelopeId != envelopeId) return;
            if (current.InputHash != capturedHash || current.InputHash != JobFinder.Core.Text.CanonicalJson.Hash(current.InputJson))
            {
                await StopAsync(context, current, BotControlStage.OutcomeUnknown, "captured bot control changed during execution; no completion certified", ct);
                return;
            }
            current.Stage = BotControlStage.Completed;
            current.CompletedAt = time.GetUtcNow();
            ownership.EnsureOwned();
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
    }

    private async Task StopAsync(JobFinderDbContext context, BotControlRequest current, BotControlStage stage, string error, CancellationToken ct)
    {
        current.Stage = stage;
        current.Error = error;
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Команда бота {current.Id} (update {current.UpdateId}) остановлена. Автоповтор запрещён; последующие команды сохранены и ждут проверки. --bot-update-reconcile {current.Id} --evidence «проверенные сведения»");
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
    }
}

public static class BotControlHandler
{
    public static Task Handle(ProcessBotControl command, Envelope envelope, BotControlWorkflow workflow, CancellationToken ct) =>
        workflow.RunAsync(command, envelope.Id, ct);
}
