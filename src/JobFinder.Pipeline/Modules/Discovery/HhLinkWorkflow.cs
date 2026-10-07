using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Http;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class HhLinkWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, IHhLinkGateway gateway,
    ServerOwnership ownership, Notifier notifier, JobTriggers triggers, TimeProvider time, ILogger<HhLinkWorkflow> log)
{
    public async Task ReadAsync(ReadHhLink command, CancellationToken ct)
    {
        Validate(command.RequestId, command.Version);
        var request = await GetAsync(command.RequestId, ct);
        if (request is not { Stage: HhLinkStage.Reading } || request.RetryAt > time.GetUtcNow()) return;
        ownership.EnsureOwned();
        using var stage = Telemetry.StartStage("read", "hh-link").Tag("request.id", command.RequestId).Tag("hh.id", request.HhId);
        HhLinkReadResult result;
        try
        {
            result = await gateway.ReadAsync(request.HhId, ct);
        }
        catch (SourceBlockedException blocked)
        {
            await DeferAsync(command.RequestId, blocked.Until ?? time.GetUtcNow().AddMinutes(10), blocked.Message, ct);
            if (blocked.Fresh && !blocked.Announced)
                await notifier.AlertAsync("blocked:hh", "hh блокирует сбор", blocked.Message + ". Ссылки ждут конца паузы.", TimeSpan.FromHours(1), ct);
            return;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (await FailAsync(command.RequestId, HhLinkStage.Reading, error, ct)) return;
            throw;
        }
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await HhLinkRequests.LockAsync(context, ct);
        ownership.EnsureOwned();
        var current = await context.HhLinkRequests.SingleAsync(row => row.Id == command.RequestId, ct);
        if (current.Stage != HhLinkStage.Reading) return;
        current.VacancyId = result.VacancyId;
        current.Summary = result.Summary;
        current.Stage = HhLinkStage.Linking;
        current.RetryAt = null;
        current.LastError = null;
        await outbox.PublishAsync(new LinkHhContacts { RequestId = current.Id });
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    public async Task LinkAsync(LinkHhContacts command, CancellationToken ct)
    {
        Validate(command.RequestId, command.Version);
        var request = await GetAsync(command.RequestId, ct);
        if (request is not { Stage: HhLinkStage.Linking }) return;
        ownership.EnsureOwned();
        using var stage = Telemetry.StartStage("contacts", "hh-link").Tag("request.id", command.RequestId).Tag("contacts", request.Contacts.Count);
        IReadOnlyList<Career.LinkedContact> linked;
        try
        {
            linked = request.VacancyId is { } vacancyId ? await gateway.LinkAsync(vacancyId, request.Contacts, ct) : [];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (await FailAsync(command.RequestId, HhLinkStage.Linking, error, ct)) return;
            throw;
        }
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await HhLinkRequests.LockAsync(context, ct);
        ownership.EnsureOwned();
        var current = await context.HhLinkRequests.SingleAsync(row => row.Id == command.RequestId, ct);
        if (current.Stage != HhLinkStage.Linking) return;
        if (!current.Contacts.SequenceEqual(request.Contacts, StringComparer.OrdinalIgnoreCase))
        {
            await outbox.PublishAsync(new LinkHhContacts { RequestId = current.Id });
            await notifier.SaveAndFlushAsync(context, outbox, ct);
            return;
        }
        current.Stage = HhLinkStage.Completed;
        current.CompletedAt = time.GetUtcNow();
        var suffix = linked.Count == 0 ? "" : $"; рекрутер {string.Join(", ", linked.Select(contact => contact.Value))} привязан";
        var buttons = linked.Where(contact => contact.Kind == ContactKind.Telegram).Select(contact => new List<string>
        {
            Callback.Button($"✈️ Написать {contact.Value} (#{current.VacancyId})", CareerCallback.RecruiterDraft, current.VacancyId!.Value, (int)contact.ContactId),
        }).ToList();
        notifier.Add(context, NotificationKind.Info, "➕ <b>Вакансии по ссылкам</b>\n" + TextUtil.EscapeHtml(current.Summary + suffix), buttons);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        triggers.Trigger(JobNames.Vacancies);
        log.Info($"[hh-links] request {command.RequestId}: completed ({current.HhId})");
    }

    private async Task DeferAsync(Guid id, DateTimeOffset until, string reason, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await HhLinkRequests.LockAsync(context, ct);
        ownership.EnsureOwned();
        var request = await context.HhLinkRequests.SingleAsync(row => row.Id == id, ct);
        if (request.Stage != HhLinkStage.Reading) return;
        request.RetryAt = (until > time.GetUtcNow() ? until : time.GetUtcNow()).AddSeconds(20);
        request.LastError = reason;
        await outbox.PublishAsync(new ReadHhLink { RequestId = id }, new DeliveryOptions { ScheduledTime = request.RetryAt });
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private async Task<bool> FailAsync(Guid id, HhLinkStage expected, Exception error, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await HhLinkRequests.LockAsync(context, ct);
        ownership.EnsureOwned();
        var request = await context.HhLinkRequests.SingleAsync(row => row.Id == id, ct);
        if (request.Stage != expected) return true;
        request.Failures++;
        request.LastError = error.Message;
        var failed = request.Failures >= 4;
        if (failed)
        {
            request.Stage = HhLinkStage.Failed;
            request.CompletedAt = time.GetUtcNow();
            notifier.Add(context, NotificationKind.Alert, $"⚠️ Не удалось обработать ссылку на вакансию {request.HhId}: " + TextUtil.EscapeHtml(error.Message));
        }
        await notifier.SaveAndFlushAsync(context, ct);
        return failed;
    }

    private async Task<HhLinkRequest?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.HhLinkRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
    }

    private static void Validate(Guid id, int version)
    {
        if (id == Guid.Empty || version != 1) throw new UnsupportedHhLinkCommandException();
    }
}

[NonTransactional]
public static class HhLinkReadHandler
{
    public static Task Handle(ReadHhLink command, HhLinkWorkflow workflow, CancellationToken ct) => workflow.ReadAsync(command, ct);
}

[NonTransactional]
public static class HhLinkContactsHandler
{
    public static Task Handle(LinkHhContacts command, HhLinkWorkflow workflow, CancellationToken ct) => workflow.LinkAsync(command, ct);
}
