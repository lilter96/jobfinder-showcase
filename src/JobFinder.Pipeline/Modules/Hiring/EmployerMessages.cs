using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class EmployerMessages(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, TimeProvider time)
{
    public Task<ActionResult> HhAsync(long vacancyId, string? text, bool asLetter, CancellationToken ct) => EnqueueAndWaitAsync(vacancyId, async context =>
    {
        var vacancy = await EmployerMessageSources.VacancyAsync(context, vacancyId, ct) ?? throw new InvalidOperationException("вакансия не найдена");
        var application = vacancy.Applications.Where(row => row.Status == ApplicationStatus.Sent).MaxBy(row => row.SentAt ?? row.CreatedAt)
            ?? throw new InvalidOperationException("нет отправленного отклика");
        var (chatId, hhId) = await ReplyService.HhChatIdAsync(context, application, ct);
        if (chatId is null) throw new InvalidOperationException("чат hh ещё не синхронизирован; отправка остановлена");
        return new EmployerMessageInput
        {
            VacancyId = vacancyId, ApplicationId = application.Id, Kind = asLetter ? EmployerMessageKind.HhLetter : EmployerMessageKind.HhChat,
            Channel = ApplicationChannel.HeadHunter, HhChatId = chatId, HhVacancyId = hhId,
            Target = chatId.Value.ToString(CultureInfo.InvariantCulture), Text = HumanText.Clean((text ?? vacancy.CoverLetter ?? "").Trim()),
            First = asLetter, NeedsFact = asLetter, ContextHash = EmployerMessageSources.Context(vacancy),
            Contact = "работодатель в чате hh", Situation = asLetter ? "Сопроводительное письмо к уже отправленному отклику." : "Сообщение в текущей переписке.",
            Conversation = RecruiterChatService.ConversationText(application),
        };
    }, ct);

    public Task<ActionResult> RecruiterAsync(long vacancyId, long contactId, string text, bool? attach, bool? fresh,
        long? outreachId, CancellationToken ct, OutreachEditSnapshot? expected = null) => EnqueueAndWaitAsync(vacancyId, async context =>
    {
        var vacancy = await EmployerMessageSources.VacancyAsync(context, vacancyId, ct) ?? throw new InvalidOperationException("вакансия не найдена");
        var contact = await context.CompanyContacts.FromSqlInterpolated($"SELECT * FROM company_contacts WHERE id = {contactId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("контакт не найден");
        if (contact.RetiredAt is not null) throw new InvalidOperationException("контакт исключён из новых откликов");
        if (contact.CompanyKey != VacancyFingerprint.NormalizeCompany(vacancy.CompanyName) || contact.Kind != ContactKind.Telegram)
            throw new InvalidOperationException("контакт не принадлежит вакансии или не является Telegram-контактом");
        var outreach = outreachId is { } oid ? await context.Outreaches.AsNoTracking().SingleOrDefaultAsync(row => row.Id == oid, ct) : null;
        if (outreachId is not null && (outreach is not { Status: OutreachStatus.Draft } || outreach.VacancyId != vacancyId || outreach.ContactId != contactId))
            throw new InvalidOperationException("черновик уже обработан");
        if (expected is not null && (outreach is null || !expected.Matches(outreach, contact)))
            throw new InvalidOperationException("черновик или контакт изменились; используйте новую карточку");
        var live = RecruiterChatService.Live(vacancy);
        var situation = RecruiterChatService.Situation(live);
        var clean = fresh ?? outreach?.Fresh ?? RecruiterChatService.FreshByDefault(situation);
        var first = clean || situation is RecruiterSituation.NotApplied or RecruiterSituation.Waiting or RecruiterSituation.Silent or RecruiterSituation.Rejected;
        var anchor = vacancy.Applications.Where(row => row.Status == ApplicationStatus.Sent).MaxBy(row => row.SentAt ?? row.CreatedAt);
        if (RecruiterChatService.IsClosed(anchor)) anchor = null;
        return new EmployerMessageInput
        {
            VacancyId = vacancyId, Kind = EmployerMessageKind.Recruiter, Channel = ApplicationChannel.TelegramDm,
            ContactId = contactId, OutreachId = outreachId, SourceRevision = outreach?.Revision ?? 0,
            SourceHash = outreach is null ? EmployerMessageSources.ContactHash(contact) : EmployerMessageSources.OutreachHash(outreach, contact),
            Target = contact.Value, Text = HumanText.Clean(text.Trim()), Fresh = clean, First = first,
            NeedsFact = first && (clean || situation != RecruiterSituation.Rejected),
            AttachResume = attach ?? (clean || RecruiterChatService.AttachByDefault(situation)),
            CreateApplication = anchor is null, ApplicationId = anchor?.Id, ContextHash = EmployerMessageSources.Context(vacancy),
            Contact = (contact.PersonName is { Length: > 0 } name ? $"{name} ({contact.Value})" : contact.Value) + (contact.Role is { } role ? $", {role}" : ""),
            Situation = clean ? RecruiterChatService.FreshText(vacancy) : RecruiterChatService.SituationText(situation, live, vacancy, time.GetUtcNow()),
            Conversation = clean ? "(переписки нет: кандидат пишет этому человеку впервые)" : RecruiterChatService.ConversationText(live),
        };
    }, ct);

    public async Task<ActionResult> OutreachAsync(long id, string? text, CancellationToken ct, OutreachEditSnapshot? expected = null)
    {
        await using var lookup = await db.CreateDbContextAsync(ct);
        var known = await lookup.Outreaches.AsNoTracking().Include(row => row.Contact).SingleOrDefaultAsync(row => row.Id == id, ct);
        if (known?.Contact is null) return ActionResult.Fail("черновик не найден");
        if (expected is not null && !expected.Matches(known, known.Contact)) return ActionResult.Fail("черновик или контакт изменились; используйте новую карточку");
        if (known.Contact.Kind == ContactKind.Telegram)
            return await RecruiterAsync(known.VacancyId, known.ContactId, text ?? known.Text, null, known.Fresh, id, ct, expected);
        if (known.Contact.Kind != ContactKind.Email) return ActionResult.Fail("канал требует ручной отправки");
        return await EnqueueAndWaitAsync(known.VacancyId, async context =>
        {
            var source = await context.Outreaches.AsNoTracking().Include(row => row.Contact).SingleAsync(row => row.Id == id, ct);
            if (source.Status != OutreachStatus.Draft || source.Contact is null) throw new InvalidOperationException("черновик уже обработан");
            var contact = await context.CompanyContacts.FromSqlInterpolated($"SELECT * FROM company_contacts WHERE id = {source.ContactId} FOR SHARE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (contact is null || contact.RetiredAt is not null) throw new InvalidOperationException("контакт исключён из новых откликов");
            source.Contact = contact;
            if (expected is not null && !expected.Matches(source, source.Contact)) throw new InvalidOperationException("черновик или контакт изменились; используйте новую карточку");
            var vacancy = await EmployerMessageSources.VacancyAsync(context, source.VacancyId, ct) ?? throw new InvalidOperationException("вакансия не найдена");
            if (source.Contact.CompanyKey != VacancyFingerprint.NormalizeCompany(vacancy.CompanyName)) throw new InvalidOperationException("контакт не принадлежит вакансии");
            var anchor = vacancy.Applications.Where(row => row.Status == ApplicationStatus.Sent).MaxBy(row => row.SentAt ?? row.CreatedAt);
            if (RecruiterChatService.IsClosed(anchor)) anchor = null;
            return new EmployerMessageInput
            {
                VacancyId = source.VacancyId, Kind = EmployerMessageKind.Outreach, Channel = ApplicationChannel.Email,
                ContactId = source.ContactId, OutreachId = id, SourceRevision = source.Revision,
                SourceHash = EmployerMessageSources.OutreachHash(source, source.Contact), ContextHash = EmployerMessageSources.Context(vacancy),
                Target = source.Contact.Value, Text = HumanText.Clean((text ?? source.Text).Trim()), First = true, NeedsFact = true, AttachResume = true,
                CreateApplication = anchor is null, ApplicationId = anchor?.Id, Contact = source.Contact.PersonName ?? source.Contact.Value,
                Subject = $"Отклик на вакансию «{vacancy.Title}»", Situation = "Первое сообщение этому рекрутеру по вакансии.", Conversation = "(переписки нет)",
            };
        }, ct);
    }

    public async Task<ActionResult> ReplyAsync(long eventId, string? text, bool automatic, int version, CancellationToken ct, ReplyEditSnapshot? expected = null)
    {
        await using var lookup = await db.CreateDbContextAsync(ct);
        var vacancyId = await lookup.ApplicationEvents.Where(row => row.Id == eventId).Select(row => (long?)row.Application!.VacancyId).SingleOrDefaultAsync(ct);
        if (vacancyId is null) return ActionResult.Fail("сообщение не найдено");
        return await EnqueueAndWaitAsync(vacancyId.Value, context => CaptureReplyAsync(context, eventId, text, automatic, version, expected, ct), ct);
    }

    private static async Task<EmployerMessageInput> CaptureReplyAsync(JobFinderDbContext context, long eventId, string? text, bool automatic,
        int version, ReplyEditSnapshot? expected, CancellationToken ct)
    {
        await context.Applications.FromSqlInterpolated($"SELECT a.* FROM applications a JOIN application_events e ON e.application_id = a.id WHERE e.id = {eventId} FOR UPDATE OF a")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        await context.ApplicationEvents.FromSqlInterpolated($"SELECT * FROM application_events WHERE id = {eventId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        var vacancyId = await context.ApplicationEvents.Where(row => row.Id == eventId).Select(row => row.Application!.VacancyId).SingleAsync(ct);
        var vacancy = await EmployerMessageSources.VacancyAsync(context, vacancyId, ct) ?? throw new InvalidOperationException("вакансия не найдена");
        var source = vacancy.Applications.SelectMany(row => row.Events).Single(row => row.Id == eventId);
        var app = vacancy.Applications.Single(row => row.Id == source.ApplicationId);
        if (source.DraftStatus != DraftStatus.Pending || app.Status != ApplicationStatus.Sent || vacancy.Status == VacancyStatus.Dismissed
            || source.Kind is not (EventKind.MessageIn or EventKind.FollowUp)) throw new InvalidOperationException("черновик уже обработан");
        if (expected is not null && !expected.Matches(source, app)
            || version != 0 && ReplyEditSnapshot.Capture(source, app).Version != version)
            throw new InvalidOperationException("черновик или получатель изменились: используйте последнюю карточку");
        var channel = source.Channel switch
        {
            EventChannel.Telegram => ApplicationChannel.TelegramDm,
            EventChannel.Email => ApplicationChannel.Email,
            EventChannel.HeadHunter => ApplicationChannel.HeadHunter,
            _ => throw new InvalidOperationException("нет канала для ответа"),
        };
        var input = new EmployerMessageInput
        {
            VacancyId = vacancy.Id, Kind = EmployerMessageKind.Reply, ApplicationId = app.Id, EventId = eventId,
            SourceRevision = source.Revision, SourceHash = EmployerMessageSources.EventHash(source), ContextHash = EmployerMessageSources.Context(vacancy),
            Channel = channel, Target = source.Kind == EventKind.FollowUp ? app.Target ?? "" : source.Author ?? app.Target ?? "",
            Text = HumanText.Clean((text ?? source.Draft ?? "").Trim()), Automatic = automatic,
            Contact = source.Author ?? app.Target ?? "работодатель", Situation = "Ответ на сообщение работодателя в текущей переписке.",
            Conversation = RecruiterChatService.ConversationText(app),
            Subject = source.Kind == EventKind.FollowUp || string.IsNullOrWhiteSpace(source.Text)
                ? $"Re: Отклик на вакансию «{vacancy.Title}»" : "Re: " + source.Text.Split('\n')[0].Trim(),
            InReplyTo = source.Kind == EventKind.FollowUp ? app.EmailMessageId : source.ExternalId,
        };
        if (channel == ApplicationChannel.HeadHunter)
        {
            (input.HhChatId, input.HhVacancyId) = await ReplyService.HhChatIdAsync(context, app, ct);
            if (input.HhChatId is null) throw new InvalidOperationException("чат hh ещё не синхронизирован");
            input.Target = input.HhChatId.Value.ToString(CultureInfo.InvariantCulture);
        }
        return input;
    }

    internal async Task<ActionResult> QueueReplyAsync(JobFinderDbContext context, IDbContextOutbox outbox, long eventId, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("reply handoff requires the source transaction");
        try
        {
            var input = await CaptureReplyAsync(context, eventId, null, automatic: true, version: 0, expected: null, ct);
            if (string.IsNullOrWhiteSpace(input.Text) || string.IsNullOrWhiteSpace(input.Target)) return ActionResult.Fail("нет текста или адресата");
            if (await context.Applications.AnyAsync(row => row.VacancyId == input.VacancyId
                && (row.Status == ApplicationStatus.Pending || row.Status == ApplicationStatus.Sending || row.Status == ApplicationStatus.OutcomeUnknown), ct)
                || await EmployerMessageSources.UnresolvedDeliveryAsync(context, input.VacancyId, ct)) return ActionResult.Fail("предыдущая доставка требует проверки");
            await EmployerMessageSources.WitnessAsync(context, input, ct);
            var key = BusinessKey(input);
            if (!await context.EmployerMessages.AnyAsync(row => row.BusinessKey == key, ct))
            {
                var message = new EmployerMessage
                {
                    Id = Guid.NewGuid(), BusinessKey = key, VacancyId = input.VacancyId, ApplicationId = input.ApplicationId,
                    InputJson = JsonSerializer.Serialize(input, EmployerMessageJsonContext.Default.EmployerMessageInput), CreatedAt = time.GetUtcNow(),
                };
                context.EmployerMessages.Add(message);
                await outbox.PublishAsync(new PrepareEmployerMessage { MessageId = message.Id });
            }
            return ActionResult.Done("автоответ в очереди; доставка ещё не подтверждена");
        }
        catch (InvalidOperationException error) { return ActionResult.Fail(error.Message); }
    }

    private static string BusinessKey(EmployerMessageInput input) => DeliveryLedger.Hash(JsonSerializer.Serialize(new
        { input.Kind, input.VacancyId, input.ContactId, input.EventId, input.OutreachId, input.SourceRevision, input.Text, input.Target, input.Fresh, input.AttachResume }));

    private async Task<ActionResult> EnqueueAndWaitAsync(long vacancyId, Func<JobFinderDbContext, Task<EmployerMessageInput>> snapshot, CancellationToken ct)
    {
        Guid id;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            await using var context = await db.CreateDbContextAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            outbox.Enroll(context);
            await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
            if (await context.Applications.AnyAsync(row => row.VacancyId == vacancyId
                && (row.Status == ApplicationStatus.Pending || row.Status == ApplicationStatus.Sending || row.Status == ApplicationStatus.OutcomeUnknown), ct))
                return ActionResult.Fail("предыдущий отклик в очереди, отправляется или требует сверки; новое сообщение не поставлено в очередь");
            if (await EmployerMessageSources.UnresolvedDeliveryAsync(context, vacancyId, ct))
                return ActionResult.Fail("предыдущая доставка отправляется или требует сверки; новое сообщение не поставлено в очередь");
            var input = await snapshot(context);
            if (string.IsNullOrWhiteSpace(input.Text) || string.IsNullOrWhiteSpace(input.Target)) return ActionResult.Fail("нет текста или адресата");
            await EmployerMessageSources.WitnessAsync(context, input, ct);
            var key = BusinessKey(input);
            var message = await context.EmployerMessages.SingleOrDefaultAsync(row => row.BusinessKey == key, ct);
            if (message is null)
            {
                message = new EmployerMessage
                {
                    Id = Guid.NewGuid(), BusinessKey = key, VacancyId = vacancyId, ApplicationId = input.ApplicationId,
                    InputJson = JsonSerializer.Serialize(input, EmployerMessageJsonContext.Default.EmployerMessageInput), CreatedAt = time.GetUtcNow(),
                };
                context.EmployerMessages.Add(message);
                await outbox.PublishAsync(new PrepareEmployerMessage { MessageId = message.Id });
                await outbox.SaveChangesAndFlushMessagesAsync(ct);
            }
            id = message.Id;
        }
        catch (InvalidOperationException error) { return ActionResult.Fail(error.Message); }
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var result = await context.EmployerMessages.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            if (result.Status == EmployerMessageStatus.Sent) return ActionResult.Done("сообщение отправлено");
            if (result.Status is EmployerMessageStatus.Cancelled or EmployerMessageStatus.NeedsManual or EmployerMessageStatus.OutcomeUnknown)
                return ActionResult.Fail(result.Error ?? "отправка остановлена");
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, ct);
        }
    }
}
