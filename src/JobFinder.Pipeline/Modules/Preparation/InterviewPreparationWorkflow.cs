using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Http;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class InterviewPreparationWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    IInterviewPreparationCapture capture, IInterviewPreparationSteps steps, JobFinderLlm llm, ServerOwnership ownership,
    Notifier notifier, TimeProvider time, PathsOptions paths, LocalClock clock)
{
    public async Task RunAsync(PrepareInterview command, Guid envelopeId, CancellationToken ct)
    {
        if (command.RequestId == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedInterviewPreparationException();
        ownership.EnsureOwned();
        if (command.Version != 1 || command.Stage is not (InterviewPreparationStage.Capturing or InterviewPreparationStage.Planning
            or InterviewPreparationStage.Answering or InterviewPreparationStage.Printing or InterviewPreparationStage.Publishing))
        {
            await RetireAsync(command.RequestId, command.Stage, null, InterviewPreparationStage.Failed, "unsupported interview command", ct);
            throw new UnsupportedInterviewPreparationException();
        }
        var request = await ClaimAsync(command, envelopeId, ct);
        if (request is null) return;
        using var span = Telemetry.StartStage(command.Stage.ToString().ToLowerInvariant(), "interview-preparation")
            .Tag("request.id", request.Id).Tag("interview.id", request.InterviewId);
        try
        {
            var seed = InterviewPreparationRequests.ReadSeed(request);
            var operation = llm.RestoreOperation(seed.Operation);
            if (InterviewPreparationSteps.Recipe(operation, seed).Hash != seed.Recipe.Hash) throw new InterviewPreparationSupersededException();
            if (!await ChangeAsync(request, (_, _, current) => { current.ClaimEnvelopeId = request.ClaimEnvelopeId; return Task.CompletedTask; }, ct)) return;
            ownership.EnsureOwned();
            if (command.Stage == InterviewPreparationStage.Capturing)
            {
                var input = await capture.CaptureAsync(seed, ct);
                InterviewPreparationSteps.Restore(llm, seed, input);
                await ChangeAsync(request, async (_, outbox, current) =>
                {
                    current.InputJson = JsonSerializer.Serialize(input, InterviewPreparationJsonContext.Default.InterviewPreparationInput);
                    current.InputHash = CanonicalJson.Hash(current.InputJson);
                    await AdvanceAsync(current, outbox, InterviewPreparationStage.Planning);
                }, ct, input);
            }
            else if (command.Stage == InterviewPreparationStage.Planning)
            {
                var plan = await steps.PlanAsync(seed, Input(request), ct);
                if (!InterviewPreparationSteps.ValidPlan(JsonSerializer.Serialize(plan.Plan, InterviewJsonContext.Default.InterviewPlan))) throw new UnsupportedInterviewPreparationException();
                await ChangeAsync(request, async (_, outbox, current) =>
                {
                    current.PlanJson = JsonSerializer.Serialize(plan, InterviewPreparationJsonContext.Default.InterviewPlanCheckpoint);
                    current.PlanHash = CanonicalJson.Hash(current.PlanJson);
                    await AdvanceAsync(current, outbox, InterviewPreparationStage.Answering);
                }, ct);
            }
            else if (command.Stage == InterviewPreparationStage.Answering)
            {
                var input = Input(request);
                var plan = Plan(request);
                var copilot = await steps.AnswerAsync(seed, input, plan, ct);
                await using (var context = await db.CreateDbContextAsync(ct))
                {
                    var recorded = await context.LlmCalls.Where(call => call.OperationId == request.Id).SumAsync(call => call.CostUsd, ct);
                    copilot.CostUsd = Math.Max(copilot.CostUsd, recorded);
                }
                InterviewPreparationSteps.ValidateResult(seed, input, plan, copilot);
                await ChangeAsync(request, async (_, outbox, current) =>
                {
                    current.CopilotJson = JsonSerializer.Serialize(copilot, InterviewPreparationJsonContext.Default.InterviewCopilot);
                    current.CopilotHash = CanonicalJson.Hash(current.CopilotJson);
                    await AdvanceAsync(current, outbox, InterviewPreparationStage.Printing);
                }, ct);
            }
            else if (command.Stage == InterviewPreparationStage.Printing)
            {
                var input = Input(request);
                var copilot = Copilot(request, seed, input);
                var print = await steps.PrintAsync(seed, input, copilot, ct);
                await InterviewPreparationSteps.VerifyPrintAsync(paths, seed, print, ct);
                await ChangeAsync(request, async (_, outbox, current) =>
                {
                    current.PdfPath = print.Path;
                    current.PdfHash = print.Hash;
                    await AdvanceAsync(current, outbox, InterviewPreparationStage.Publishing);
                }, ct);
            }
            else await PublishAsync(request, seed, ct);
        }
        catch (LlmUnavailableException error) { await DeferAsync(request, error.Message, time.GetUtcNow().AddMinutes(error.BudgetExceeded || error.Unauthorized ? 30 : 2), false, ct); }
        catch (InterviewPreparationPrerequisiteException error) { await DeferAsync(request, error.Message, time.GetUtcNow().AddMinutes(10), false, ct); }
        catch (SourceBlockedException error) { await DeferAsync(request, error.Message, error.Until ?? time.GetUtcNow().AddMinutes(10), false, ct); }
        catch (InterviewPreparationSupersededException) { await RetireAsync(request.Id, request.Stage, request.ClaimEnvelopeId, InterviewPreparationStage.Cancelled, "interview inputs or recipe changed", ct); }
        catch (Exception error) when (error is UnsupportedInterviewPreparationException or JsonException or ArgumentException)
        {
            await RetireAsync(request.Id, request.Stage, request.ClaimEnvelopeId, InterviewPreparationStage.Failed, "unsupported interview checkpoint", ct);
            throw new UnsupportedInterviewPreparationException();
        }
        catch (Exception error) when (error is not OperationCanceledException) { await DeferAsync(request, error.Message, time.GetUtcNow().AddMinutes(1), true, ct); }
    }

    private async Task<InterviewPreparationRequest?> ClaimAsync(PrepareInterview command, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var request = await context.InterviewPreparationRequests.SingleOrDefaultAsync(row => row.Id == command.RequestId, ct);
        if (request is null) return null;
        await InterviewPreparationCapture.LockAsync(context, request.InterviewId, ct);
        await context.Entry(request).ReloadAsync(ct);
        if (request.Stage != command.Stage || request.RetryAt > time.GetUtcNow() || request.ClaimEnvelopeId is { } claim && claim != envelopeId) return null;
        ownership.EnsureOwned();
        request.ClaimEnvelopeId = envelopeId;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return request;
    }

    private async Task<bool> ChangeAsync(InterviewPreparationRequest before, Func<JobFinderDbContext, IDbContextOutbox, InterviewPreparationRequest, Task> change,
        CancellationToken ct, InterviewPreparationInput? captured = null)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await InterviewPreparationCapture.LockAsync(context, before.InterviewId, ct);
        var current = await context.InterviewPreparationRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (!Same(current, before)) return false;
        var seed = InterviewPreparationRequests.ReadSeed(current);
        await capture.EnsureCurrentAsync(context, seed, captured ?? (current.InputJson is null ? null : Input(current)), ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await change(context, outbox, current);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return true;
    }

    private async Task PublishAsync(InterviewPreparationRequest request, InterviewPreparationSeed seed, CancellationToken ct)
    {
        var input = Input(request);
        var copilot = Copilot(request, seed, input);
        var print = new InterviewPrintCheckpoint { Path = request.PdfPath ?? "", Hash = request.PdfHash ?? "" };
        var expectedPath = Path.GetFullPath(Path.Combine(paths.Data, "interviews", request.InterviewId.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Id.ToString("N"), "pack.pdf"));
        if (string.IsNullOrWhiteSpace(print.Path) || StoredPath.Resolve(print.Path) != expectedPath || string.IsNullOrWhiteSpace(print.Hash)) throw new UnsupportedInterviewPreparationException();
        try { await InterviewPreparationSteps.VerifyPrintAsync(paths, seed, print, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ChangeAsync(request, async (_, outbox, current) =>
            {
                current.PdfPath = null;
                current.PdfHash = null;
                await AdvanceAsync(current, outbox, InterviewPreparationStage.Printing);
            }, ct);
            return;
        }
        await ChangeAsync(request, async (context, outbox, current) =>
        {
            try { await InterviewPreparationSteps.VerifyPrintAsync(paths, seed, print, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                current.PdfPath = null;
                current.PdfHash = null;
                await AdvanceAsync(current, outbox, InterviewPreparationStage.Printing);
                return;
            }
            var source = await context.Interviews.SingleAsync(row => row.Id == request.InterviewId, ct);
            source.PackJson = current.CopilotJson;
            source.PackPdfPath = current.PdfPath;
            current.Stage = InterviewPreparationStage.Completed;
            current.CompletedAt = time.GetUtcNow();
            var local = source.ScheduledAt is { } at ? TimeZoneInfo.ConvertTime(at, clock.Zone) : (DateTimeOffset?)null;
            var (text, buttons) = CareerCards.Pack(source, input.AnswerSource.ToVacancy(), copilot, local);
            var company = string.Concat((input.AnswerSource.Vacancy.CompanyName ?? "компания").Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' or ':' ? ' ' : character)).Trim();
            notifier.Add(context, NotificationKind.Info, text, buttons, source.Id, current.PdfPath, $"Интервью — {company} — {input.AnswerSource.Kind}.pdf");
        }, ct);
    }

    private async Task DeferAsync(InterviewPreparationRequest request, string error, DateTimeOffset at, bool countFailure, CancellationToken ct)
    {
        try
        {
            await ChangeAsync(request, async (context, outbox, current) =>
            {
                current.Error = error;
                if (countFailure && ++current.Failures >= 3)
                {
                    current.Stage = InterviewPreparationStage.Failed;
                    current.CompletedAt = time.GetUtcNow();
                    notifier.Add(context, NotificationKind.Alert, "⚠️ Подготовка интервью #" + current.InterviewId + " остановлена: " + TextUtil.EscapeHtml(error), entityId: current.InterviewId);
                    return;
                }
                current.RetryAt = at;
                await outbox.PublishAsync(new PrepareInterview { RequestId = current.Id, Stage = current.Stage }, new DeliveryOptions { ScheduledTime = at });
            }, ct);
        }
        catch (InterviewPreparationSupersededException)
        {
            await RetireAsync(request.Id, request.Stage, request.ClaimEnvelopeId, InterviewPreparationStage.Cancelled, "interview changed at retry boundary", ct);
        }
    }

    private async Task RetireAsync(Guid id, InterviewPreparationStage expectedStage, Guid? expectedClaim, InterviewPreparationStage stage, string error, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var request = await context.InterviewPreparationRequests.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (request is null) return;
        await InterviewPreparationCapture.LockAsync(context, request.InterviewId, ct);
        await context.Entry(request).ReloadAsync(ct);
        if (request.Stage != expectedStage || expectedClaim is not null && request.ClaimEnvelopeId != expectedClaim
            || request.Stage is InterviewPreparationStage.Completed or InterviewPreparationStage.Cancelled or InterviewPreparationStage.Failed) return;
        request.Stage = stage;
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        request.Error = error;
        request.CompletedAt = time.GetUtcNow();
        var source = await context.Interviews.SingleOrDefaultAsync(row => row.Id == request.InterviewId, ct);
        if (stage == InterviewPreparationStage.Cancelled && source?.PreparationRequestId == id && source.PreparationRevision == request.Revision)
        {
            source.PreparationRequestId = null;
            source.PreparationRevision++;
        }
        notifier.Add(context, NotificationKind.Alert, "⚠️ Подготовка интервью #" + request.InterviewId + " остановлена: " + TextUtil.EscapeHtml(error), entityId: request.InterviewId);
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
    }

    private static bool Same(InterviewPreparationRequest current, InterviewPreparationRequest before) => current.Stage == before.Stage
        && current.ClaimEnvelopeId == before.ClaimEnvelopeId && current.SeedHash == before.SeedHash && current.InputHash == before.InputHash
        && current.PlanHash == before.PlanHash && current.CopilotHash == before.CopilotHash && current.PdfPath == before.PdfPath && current.PdfHash == before.PdfHash;
    private static async Task AdvanceAsync(InterviewPreparationRequest current, IDbContextOutbox outbox, InterviewPreparationStage next)
    {
        current.Stage = next;
        await outbox.PublishAsync(new PrepareInterview { RequestId = current.Id, Stage = next });
    }
    private static InterviewPreparationInput Input(InterviewPreparationRequest request) =>
        InterviewPreparationRequests.ReadCheckpoint(request.InputJson, request.InputHash, InterviewPreparationJsonContext.Default.InterviewPreparationInput);
    private static InterviewPlanCheckpoint Plan(InterviewPreparationRequest request) =>
        InterviewPreparationRequests.ReadCheckpoint(request.PlanJson, request.PlanHash, InterviewPreparationJsonContext.Default.InterviewPlanCheckpoint);
    private static InterviewCopilot Copilot(InterviewPreparationRequest request, InterviewPreparationSeed seed, InterviewPreparationInput input)
    {
        var result = InterviewPreparationRequests.ReadCheckpoint(request.CopilotJson, request.CopilotHash, InterviewPreparationJsonContext.Default.InterviewCopilot);
        InterviewPreparationSteps.ValidateResult(seed, input, Plan(request), result);
        return result;
    }
}

[NonTransactional]
public static class InterviewPreparationHandler
{
    public static Task Handle(PrepareInterview command, InterviewPreparationWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command, message.Envelope?.Id ?? throw new UnsupportedInterviewPreparationException(), ct);
}
