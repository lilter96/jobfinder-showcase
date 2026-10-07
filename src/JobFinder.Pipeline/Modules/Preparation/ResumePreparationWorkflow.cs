using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class ResumePreparationWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IResumePreparationSteps steps, ServerOwnership ownership, Notifier notifier, JobTriggers triggers, TimeProvider time, ILogger<ResumePreparationWorkflow> log)
{
    public async Task RunAsync(Guid id, int round, int version, ResumePreparationStage expected, Guid envelopeId, CancellationToken ct)
    {
        if (id == Guid.Empty || envelopeId == Guid.Empty || version != 1 || round is < 1 or > 4) throw new UnsupportedResumeCommandException();
        var request = await ClaimAsync(id, round, expected, envelopeId, ct);
        if (request is null) return;
        using var stage = Telemetry.StartStage(expected.ToString().ToLowerInvariant(), "resume-preparation")
            .Tag("operation.id", id).Tag("vacancy.id", request.VacancyId).Tag("round", round);
        ResumeRound? output = null;
        TailoredResume? result = null;
        try
        {
            var input = JsonSerializer.Deserialize(request.InputJson, ResumePreparationJsonContext.Default.ResumePreparationInput)
                ?? throw new JsonException("missing resume preparation input");
            var current = ReadRound(request.CurrentJson);
            var best = ReadRound(request.BestJson);
            ownership.EnsureOwned();
            switch (expected)
            {
                case ResumePreparationStage.Drafting: output = await steps.DraftAsync(input, round, best, ct); break;
                case ResumePreparationStage.Printing: output = await steps.PrintAsync(input, current ?? throw new JsonException("missing resume draft"), ct); break;
                case ResumePreparationStage.Reviewing: output = await steps.ReviewAsync(input, current ?? throw new JsonException("missing resume print"), ct); break;
                case ResumePreparationStage.Finalizing: result = await steps.FinishAsync(input, best ?? throw new JsonException("missing reviewed resume"), ct); break;
                default: throw new UnsupportedResumeCommandException();
            }
            ownership.EnsureOwned();
            await AdvanceAsync(request, output, result, ct);
        }
        catch (LlmUnavailableException error)
        {
            await DeferAsync(request, error.Unauthorized ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(2), error.Message, ct);
        }
        catch (ResumePrintChangedException)
        {
            await ReprintAsync(request, ct);
        }
        catch (ResumePreparationSupersededException)
        {
            await RetireAsync(request, "resume preparation recipe changed", true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (!await RetireAsync(request, error.Message, false, ct)) throw;
        }
    }

    private async Task<ResumePreparationRequest?> ClaimAsync(Guid id, int round, ResumePreparationStage expected, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.ResumePreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (known is null || known.Stage != expected || known.Round != round || known.RetryAt > time.GetUtcNow()) return null;
        ownership.EnsureOwned();
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await ResumePreparationRequests.LockAsync(context, known.VacancyId, ct);
        ownership.EnsureOwned();
        var request = await context.ResumePreparationRequests.SingleAsync(row => row.Id == id, ct);
        if (request.Stage != expected || request.Round != round || request.RetryAt > time.GetUtcNow()
            || request.ClaimEnvelopeId is { } owner && owner != envelopeId) return null;
        request.ClaimEnvelopeId = envelopeId;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return request;
    }

    private async Task AdvanceAsync(ResumePreparationRequest before, ResumeRound? output, TailoredResume? result, CancellationToken ct)
    {
        await ChangeAsync(before, async (context, outbox, request) =>
        {
            request.RetryAt = null;
            request.Error = null;
            request.ClaimEnvelopeId = null;
            if (result is not null)
            {
                result.CostUsd = await context.LlmCalls.Where(call => call.OperationId == request.Id).SumAsync(call => call.CostUsd, ct);
                context.TailoredResumes.Add(result);
                await context.SaveChangesAsync(ct);
                request.ResultId = result.Id;
                request.Stage = ResumePreparationStage.Completed;
                request.CompletedAt = time.GetUtcNow();
                var best = ReadRound(request.BestJson)!;
                notifier.Add(context, NotificationKind.Info, $"📄 Резюме для вакансии #{request.VacancyId}: " +
                    (best.Ready ? $"проверено, {best.Review!.Scored().Score}/10" : "не прошло проверку; отправка заблокирована"), entityId: request.VacancyId);
                return;
            }
            if (output is null || output.Number != request.Round) throw new InvalidOperationException("resume stage returned another round");
            request.CurrentJson = JsonSerializer.Serialize(output, ResumePreparationJsonContext.Default.ResumeRound);
            if (request.Stage == ResumePreparationStage.Drafting) request.Stage = ResumePreparationStage.Printing;
            else if (request.Stage == ResumePreparationStage.Printing) request.Stage = ResumePreparationStage.Reviewing;
            else
            {
                var best = ReadRound(request.BestJson);
                if (best is null || output.Rank > best.Rank)
                {
                    best = output;
                    request.BestJson = request.CurrentJson;
                }
                if (best.Ready || request.Round >= 4) request.Stage = ResumePreparationStage.Finalizing;
                else { request.Round++; request.CurrentJson = null; request.Stage = ResumePreparationStage.Drafting; }
            }
            await PublishAsync(outbox, request);
        }, ct);
        log.Info($"[resume] #{before.VacancyId}: {before.Stage} round {before.Round} checkpointed");
        if (before.Stage == ResumePreparationStage.Finalizing) triggers.Trigger(JobNames.Resumes);
    }

    private async Task ChangeAsync(ResumePreparationRequest before, Func<JobFinderDbContext, IDbContextOutbox, ResumePreparationRequest, Task> change, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await ResumePreparationRequests.LockAsync(context, before.VacancyId, ct);
        ownership.EnsureOwned();
        var request = await context.ResumePreparationRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (request.Stage != before.Stage || request.Round != before.Round || request.ClaimEnvelopeId != before.ClaimEnvelopeId) return;
        await change(context, outbox, request);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private Task DeferAsync(ResumePreparationRequest before, TimeSpan delay, string error, CancellationToken ct) => ChangeAsync(before, async (_, outbox, request) =>
    {
        request.RetryAt = time.GetUtcNow() + delay;
        request.Error = error;
        request.ClaimEnvelopeId = null;
        await PublishAsync(outbox, request, request.RetryAt);
    }, ct);

    private Task ReprintAsync(ResumePreparationRequest before, CancellationToken ct) => ChangeAsync(before, async (_, outbox, request) =>
    {
        var saved = ReadRound(request.Stage == ResumePreparationStage.Finalizing ? request.BestJson : request.CurrentJson)
            ?? throw new JsonException("missing resume for reprint");
        saved.Number = request.Round;
        saved.PdfPath = null;
        saved.PdfHash = null;
        saved.Review = null;
        request.CurrentJson = JsonSerializer.Serialize(saved, ResumePreparationJsonContext.Default.ResumeRound);
        request.BestJson = null;
        request.Stage = ResumePreparationStage.Printing;
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        await PublishAsync(outbox, request);
    }, ct);

    private async Task<bool> RetireAsync(ResumePreparationRequest before, string error, bool superseded, CancellationToken ct)
    {
        var retired = false;
        await ChangeAsync(before, (context, outbox, request) =>
        {
            request.ClaimEnvelopeId = null;
            request.Error = error;
            if (superseded || ++request.Failures >= 4)
            {
                retired = true;
                request.Stage = superseded ? ResumePreparationStage.Cancelled : ResumePreparationStage.Failed;
                request.CompletedAt = time.GetUtcNow();
                notifier.Add(context, NotificationKind.Alert, $"⚠️ Резюме для вакансии #{request.VacancyId}: " + TextUtil.EscapeHtml(error), entityId: request.VacancyId);
            }
            return Task.CompletedTask;
        }, ct);
        return retired;
    }

    private static ResumeRound? ReadRound(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ResumePreparationJsonContext.Default.ResumeRound);

    private static ValueTask PublishAsync(IDbContextOutbox outbox, ResumePreparationRequest request, DateTimeOffset? scheduled = null)
    {
        object command = request.Stage switch
        {
            ResumePreparationStage.Drafting => new DraftResume { RequestId = request.Id, Round = request.Round },
            ResumePreparationStage.Printing => new PrintResume { RequestId = request.Id, Round = request.Round },
            ResumePreparationStage.Reviewing => new ReviewResume { RequestId = request.Id, Round = request.Round },
            ResumePreparationStage.Finalizing => new FinishResume { RequestId = request.Id, Round = request.Round },
            _ => throw new UnsupportedResumeCommandException(),
        };
        return outbox.PublishAsync(command, new DeliveryOptions { ScheduledTime = scheduled });
    }
}

[NonTransactional]
public static class ResumePreparationHandler
{
    public static Task Handle(DraftResume command, ResumePreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Round, command.Version, ResumePreparationStage.Drafting, EnvelopeId(message), ct);
    public static Task Handle(PrintResume command, ResumePreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Round, command.Version, ResumePreparationStage.Printing, EnvelopeId(message), ct);
    public static Task Handle(ReviewResume command, ResumePreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Round, command.Version, ResumePreparationStage.Reviewing, EnvelopeId(message), ct);
    public static Task Handle(FinishResume command, ResumePreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Round, command.Version, ResumePreparationStage.Finalizing, EnvelopeId(message), ct);

    private static Guid EnvelopeId(IMessageContext message) => message.Envelope?.Id ?? throw new UnsupportedResumeCommandException();
}
