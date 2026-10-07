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

public sealed class LinkedInPeopleWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    ILinkedInPeopleSteps steps, ServerOwnership ownership, Notifier notifier, TimeProvider time)
{
    public async Task RunAsync(ReadLinkedInPeople command, Guid envelopeId, CancellationToken ct)
    {
        if (command.Version != 1 || command.RequestId == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedLinkedInPeopleException();
        ownership.EnsureOwned();
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.LinkedInPeopleRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == command.RequestId, ct)
            ?? throw new UnsupportedLinkedInPeopleException();
        if (known.Stage is LinkedInPeopleStage.Completed or LinkedInPeopleStage.Failed) return;
        using var span = Telemetry.StartStage("read-people", "linkedin-people").Tag("request.id", known.Id);
        try
        {
            if (known.Stage != LinkedInPeopleStage.Reading) throw new UnsupportedLinkedInPeopleException();
            var input = LinkedInPeopleRequests.ReadInput(known);
            steps.EnsureCompatible(input);
            await using (var context = await db.CreateDbContextAsync(ct))
            {
                await using var transaction = await context.Database.BeginTransactionAsync(ct);
                await LinkedInPeopleRequests.LockAsync(context, known.BusinessKey, ct);
                var current = await context.LinkedInPeopleRequests.SingleAsync(row => row.Id == known.Id, ct);
                if (current.Stage != LinkedInPeopleStage.Reading || current.RetryAt > time.GetUtcNow()
                    || current.ClaimEnvelopeId is { } claimant && claimant != envelopeId) return;
                if (current.InputHash != known.InputHash) throw new UnsupportedLinkedInPeopleException();
                LinkedInPeopleRequests.ReadInput(current);
                current.ClaimEnvelopeId = envelopeId;
                ownership.EnsureOwned();
                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            ownership.EnsureOwned();
            var result = await steps.ReadAsync(input, ct);
            var json = JsonSerializer.Serialize(result, LinkedInPeopleJsonContext.Default.LinkedInPeopleResult);
            var hash = CanonicalJson.Hash(json);
            known.ResultJson = json;
            known.ResultHash = hash;
            LinkedInPeopleRequests.ReadResult(known);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await CompleteAsync(known, envelopeId, ct);
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
        catch (LlmUnavailableException error)
        {
            await PauseAsync(known, envelopeId, TimeSpan.FromMinutes(error.Unauthorized ? 30 : 2), ct);
        }
        catch (Exception error) when (error is JsonException or UnsupportedLinkedInPeopleException or LlmPolicyException)
        {
            await RetireAsync(known, ct);
            throw new UnsupportedLinkedInPeopleException();
        }
    }

    private async Task CompleteAsync(LinkedInPeopleRequest known, Guid envelopeId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LinkedInPeopleRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.LinkedInPeopleRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != LinkedInPeopleStage.Reading || current.ClaimEnvelopeId != envelopeId) return;
        if (current.InputHash != known.InputHash) throw new UnsupportedLinkedInPeopleException();
        var input = LinkedInPeopleRequests.ReadInput(current);
        steps.EnsureCompatible(input);
        current.ResultJson = known.ResultJson;
        current.ResultHash = known.ResultHash;
        LinkedInPeopleRequests.ReadResult(current);
        current.Stage = LinkedInPeopleStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        current.Error = null;
        ownership.EnsureOwned();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task PauseAsync(LinkedInPeopleRequest known, Guid envelopeId, TimeSpan delay, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LinkedInPeopleRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.LinkedInPeopleRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage != LinkedInPeopleStage.Reading || current.ClaimEnvelopeId != envelopeId) return;
        current.ClaimEnvelopeId = null;
        current.RetryAt = time.GetUtcNow().Add(delay);
        current.Error = "LLM unavailable; saved LinkedIn page will resume without another search";
        await outbox.PublishAsync(new ReadLinkedInPeople { RequestId = current.Id }, new DeliveryOptions { ScheduledTime = current.RetryAt });
        ownership.EnsureOwned();
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }

    private async Task RetireAsync(LinkedInPeopleRequest known, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LinkedInPeopleRequests.LockAsync(context, known.BusinessKey, ct);
        var current = await context.LinkedInPeopleRequests.SingleAsync(row => row.Id == known.Id, ct);
        if (current.Stage is LinkedInPeopleStage.Completed or LinkedInPeopleStage.Failed) return;
        current.Stage = LinkedInPeopleStage.Failed;
        current.ClaimEnvelopeId = null;
        current.CompletedAt = time.GetUtcNow();
        current.Error = "unsupported LinkedIn people checkpoint quarantined; page retained";
        notifier.Add(context, NotificationKind.Alert, $"⚠️ Разбор контактов LinkedIn #{current.Id} остановлен: сохранённая страница или контракт несовместимы. Отправка работодателю не выполнялась.");
        ownership.EnsureOwned();
        await notifier.SaveAndFlushAsync(context, ct);
    }
}

public static class LinkedInPeopleHandler
{
    public static Task Handle(ReadLinkedInPeople command, Envelope envelope, LinkedInPeopleWorkflow workflow, CancellationToken ct) =>
        workflow.RunAsync(command, envelope.Id, ct);
}
