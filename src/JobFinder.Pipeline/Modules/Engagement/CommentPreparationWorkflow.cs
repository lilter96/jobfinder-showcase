using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class CommentPreparationWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    ICommentPreparationSteps steps, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public async Task RunAsync(Guid id, int round, int version, CommentPreparationStage stage, Guid envelopeId, CancellationToken ct)
    {
        if (id == Guid.Empty || envelopeId == Guid.Empty || version != 1 || round is < 1 or > 2
            || stage is not (CommentPreparationStage.Drafting or CommentPreparationStage.Checking)) throw new UnsupportedCommentPreparationException();
        await using var read = await db.CreateDbContextAsync(ct);
        var before = await read.CommentPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (before is null || before.Stage != stage || before.Round != round || before.RetryAt > time.GetUtcNow()) return;
        ownership.EnsureOwned();
        await using (var transaction = await read.Database.BeginTransactionAsync(ct))
        {
            await CommentPreparationRequests.LockAsync(read, before.BusinessKey, ct);
            var current = await read.CommentPreparationRequests.SingleAsync(row => row.Id == id, ct);
            if (current.Stage != stage || current.Round != round || current.RetryAt > time.GetUtcNow()
                || current.ClaimEnvelopeId is { } owner && owner != envelopeId) return;
            ownership.EnsureOwned();
            current.ClaimEnvelopeId = envelopeId;
            await read.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            before = current;
        }
        using var span = Telemetry.StartStage(stage.ToString().ToLowerInvariant(), "comment-preparation").Tag("operation.id", id).Tag("round", round);
        try
        {
            if (CanonicalJson.Hash(before.InputJson) != before.InputHash) throw new UnsupportedCommentPreparationException();
            var input = JsonSerializer.Deserialize(before.InputJson, CommentPreparationJsonContext.Default.CommentPreparationInput)
                ?? throw new UnsupportedCommentPreparationException();
            if (input.Version != 1 || input.Operation.OperationId != before.Id || input.Recipe.OperationId != before.Id
                || input.Recipe.Hash != before.BusinessKey) throw new UnsupportedCommentPreparationException();
            var first = ReadRound(before.FirstJson);
            if (stage == CommentPreparationStage.Drafting)
            {
                var plan = await steps.DraftAsync(input, round == 1 ? null : string.Join("\n", first?.Remarks ?? throw new JsonException("missing first comment check")), ct);
                ownership.EnsureOwned();
                await ChangeAsync(before, async (_, outbox, current) =>
                {
                    current.CurrentJson = JsonSerializer.Serialize(new CommentPreparationRound { Plan = plan }, CommentPreparationJsonContext.Default.CommentPreparationRound);
                    current.Stage = CommentPreparationStage.Checking;
                    await PublishAsync(outbox, current);
                }, ct);
            }
            else
            {
                var checkedPlan = steps.Check(input, ReadRound(before.CurrentJson)?.Plan ?? throw new JsonException("missing comment draft"));
                ownership.EnsureOwned();
                await ChangeAsync(before, async (_, outbox, current) =>
                {
                    current.CurrentJson = JsonSerializer.Serialize(checkedPlan, CommentPreparationJsonContext.Default.CommentPreparationRound);
                    if (round == 1 && checkedPlan.Plan.WorthCommenting && checkedPlan.Remarks.Count > 0)
                    {
                        current.FirstJson = current.CurrentJson;
                        current.Round = 2;
                        current.Stage = CommentPreparationStage.Drafting;
                        await PublishAsync(outbox, current);
                    }
                    else Finish(current, CommentPreparationSteps.Finish(checkedPlan, first));
                }, ct);
            }
        }
        catch (LlmUnavailableException error)
        {
            await ChangeAsync(before, async (_, outbox, current) =>
            {
                var first = ReadRound(current.FirstJson);
                if (round == 2 && first is not null && CommentPreparationSteps.Usable(first))
                    Finish(current, CommentPreparationSteps.Finish(first));
                else
                {
                    current.Error = error.Message;
                    current.RetryAt = time.GetUtcNow() + (error.Unauthorized ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(2));
                    await PublishAsync(outbox, current, current.RetryAt);
                }
            }, ct);
        }
        catch (CommentPreparationSupersededException)
        {
            await RetireAsync(before, "comment preparation recipe changed", CommentPreparationStage.Cancelled, ct);
        }
        catch (UnsupportedCommentPreparationException)
        {
            await RetireAsync(before, "unsupported comment preparation checkpoint", CommentPreparationStage.Failed, ct);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var terminal = before.Failures + 1 >= 4;
            await ChangeAsync(before, (context, _, current) =>
            {
                current.Failures++;
                current.Error = error.Message;
                if (terminal)
                {
                    current.Stage = CommentPreparationStage.Failed;
                    current.CompletedAt = time.GetUtcNow();
                    notifier.Add(context, NotificationKind.Alert, "⚠️ Подготовка комментария остановлена: " + TextUtil.EscapeHtml(error.Message));
                }
                return Task.CompletedTask;
            }, ct);
            if (!terminal) throw;
        }
    }

    private async Task ChangeAsync(CommentPreparationRequest before,
        Func<JobFinderDbContext, IDbContextOutbox, CommentPreparationRequest, Task> change, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await CommentPreparationRequests.LockAsync(context, before.BusinessKey, ct);
        ownership.EnsureOwned();
        var current = await context.CommentPreparationRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (current.Stage != before.Stage || current.Round != before.Round || current.ClaimEnvelopeId != before.ClaimEnvelopeId) return;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        await change(context, outbox, current);
        if (current.Stage is CommentPreparationStage.Completed or CommentPreparationStage.Cancelled or CommentPreparationStage.Failed)
            await CommentPreparationRequests.PublishDependentsAsync(context, outbox, current.Id, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private Task RetireAsync(CommentPreparationRequest before, string error, CommentPreparationStage stage, CancellationToken ct) =>
        ChangeAsync(before, (context, _, current) =>
        {
            current.Stage = stage;
            current.CompletedAt = time.GetUtcNow();
            current.Error = error;
            notifier.Add(context, NotificationKind.Alert, "⚠️ Подготовка комментария остановлена: " + TextUtil.EscapeHtml(error));
            return Task.CompletedTask;
        }, ct);

    private void Finish(CommentPreparationRequest request, CommentPreparationResult result)
    {
        request.ResultJson = JsonSerializer.Serialize(result, CommentPreparationJsonContext.Default.CommentPreparationResult);
        request.Stage = CommentPreparationStage.Completed;
        request.CompletedAt = time.GetUtcNow();
    }

    private static CommentPreparationRound? ReadRound(string? json) => json is null ? null
        : JsonSerializer.Deserialize(json, CommentPreparationJsonContext.Default.CommentPreparationRound);

    private static ValueTask PublishAsync(IDbContextOutbox outbox, CommentPreparationRequest request, DateTimeOffset? scheduled = null) =>
        outbox.PublishAsync(request.Stage == CommentPreparationStage.Drafting
            ? (object)new DraftComment { RequestId = request.Id, Round = request.Round }
            : new CheckComment { RequestId = request.Id, Round = request.Round }, new DeliveryOptions { ScheduledTime = scheduled });
}

[NonTransactional]
public static class CommentPreparationHandler
{
    public static Task Handle(DraftComment command, CommentPreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Round, command.Version, CommentPreparationStage.Drafting,
            message.Envelope?.Id ?? throw new UnsupportedCommentPreparationException(), ct);
    public static Task Handle(CheckComment command, CommentPreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Round, command.Version, CommentPreparationStage.Checking,
            message.Envelope?.Id ?? throw new UnsupportedCommentPreparationException(), ct);
}
