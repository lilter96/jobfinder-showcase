using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record HhChatRead(long ApplicationId, long ChatId, long VacancyId);

public sealed record HhNegotiationPublication(int Imported, int Dequeued, int Changed, IReadOnlyList<HhChatRead> Chats,
    IReadOnlyList<string> UnknownStates);

public sealed class HhNegotiationSources(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    InterviewTransitions transitions, Notifier notifier, HhOptions options, TimeProvider time)
{
    internal const string UnreadPrefix = "hh.chat.unread.";

    public async Task<HhNegotiationPublication> PublishAsync(IReadOnlyList<HhNegotiation> observations, DateTimeOffset observedAt, CancellationToken ct)
    {
        if (observedAt == default || observations.Any(row => row.VacancyId <= 0 || string.IsNullOrWhiteSpace(row.State)))
            throw new ArgumentException("hh observations require a vacancy and state", nameof(observations));
        observedAt = new DateTimeOffset(observedAt.UtcTicks - observedAt.UtcTicks % 10, TimeSpan.Zero);
        using var stage = Telemetry.StartStage("publish-responses", "hh-negotiations").Tag("response.count", observations.Count);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"hh.negotiations.import"}, 0))", ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var now = time.GetUtcNow();
        var list = observations.GroupBy(row => row.VacancyId)
            .Select(group => group.OrderByDescending(row => row.UpdatedAt).First()).ToArray();
        var ids = list.Select(row => row.VacancyId).ToArray();
        var known = await context.HhResponses.Where(row => ids.Contains(row.HhVacancyId)).ToDictionaryAsync(row => row.HhVacancyId, ct);
        var fresh = new List<HhNegotiation>();
        foreach (var observation in list)
        {
            if (known.TryGetValue(observation.VacancyId, out var response))
            {
                if (response.SyncedAt >= observedAt || observation.UpdatedAt is { } updated && response.UpdatedAt > updated) continue;
            }
            else
            {
                response = new HhResponse { HhVacancyId = observation.VacancyId, State = observation.State };
                context.HhResponses.Add(response);
                known.Add(observation.VacancyId, response);
            }
            response.State = observation.State;
            response.VacancyName = observation.VacancyName ?? response.VacancyName;
            response.CompanyName = observation.CompanyName ?? response.CompanyName;
            if (observation.VacancyName is not null)
                response.Fingerprint = VacancyFingerprint.Compute(observation.CompanyName, observation.VacancyName, $"hh:{observation.VacancyId}");
            response.ViewedByEmployer = observation.ViewedByEmployer;
            response.HasUnread = observation.HasUnread;
            response.ChatId = observation.ChatId ?? response.ChatId;
            response.RespondedAt = observation.CreatedAt ?? response.RespondedAt;
            response.UpdatedAt = observation.UpdatedAt ?? response.UpdatedAt;
            response.SyncedAt = observedAt;
            fresh.Add(observation);
        }
        var external = fresh.Select(row => row.VacancyId.ToString(CultureInfo.InvariantCulture)).ToArray();
        var vacancyIds = await context.Vacancies.Where(vacancy => vacancy.Sources.Any(source => source.Source == SourceKind.HeadHunter
            && external.Contains(source.ExternalId))).Select(vacancy => vacancy.Id).OrderBy(id => id).ToListAsync(ct);
        foreach (var vacancyId in vacancyIds) await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
        var vacancies = await context.Vacancies.Include(vacancy => vacancy.Sources).Include(vacancy => vacancy.Applications)
            .Where(vacancy => vacancyIds.Contains(vacancy.Id)).ToListAsync(ct);
        var byHhId = vacancies.Select(vacancy => (Id: ApplyRoutes.HhId(vacancy), Vacancy: vacancy)).Where(item => item.Id is not null)
            .GroupBy(item => item.Id!.Value).ToDictionary(group => group.Key, group => group.First().Vacancy);
        var unresolved = await context.EmployerMessages.Where(message => vacancyIds.Contains(message.VacancyId)
                && (message.Status == EmployerMessageStatus.Sending || message.Status == EmployerMessageStatus.OutcomeUnknown))
            .Select(message => message.VacancyId).Distinct().ToListAsync(ct);
        var cursorKeys = fresh.Select(row => UnreadPrefix + row.VacancyId.ToString(CultureInfo.InvariantCulture)).ToArray();
        var cursors = await context.Settings.Where(setting => cursorKeys.Contains(setting.Key)).ToDictionaryAsync(setting => setting.Key, ct);
        int imported = 0, dequeued = 0, changed = 0, announced = 0;
        var chats = new List<HhChatRead>();
        var unknownStates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in fresh)
        {
            var cursorKey = UnreadPrefix + observation.VacancyId.ToString(CultureInfo.InvariantCulture);
            if (!observation.HasUnread && cursors.Remove(cursorKey, out var cursor)) context.Settings.Remove(cursor);
            if (!byHhId.TryGetValue(observation.VacancyId, out var vacancy)) continue;
            var targetStage = HhHistory.StageOf(observation.State, observation.ViewedByEmployer);
            if (targetStage is null)
            {
                unknownStates.Add(observation.State);
                continue;
            }
            if (unresolved.Contains(vacancy.Id) || vacancy.Applications.Any(application => application.Status is ApplicationStatus.Sending or ApplicationStatus.OutcomeUnknown))
                continue;
            var application = vacancy.Applications.Where(row => row.Status == ApplicationStatus.Sent).OrderByDescending(row => row.SentAt).FirstOrDefault();
            if (application is null)
            {
                if (vacancy.Status == VacancyStatus.Scored && vacancy.Decision is Decision.Approval or Decision.AutoApply) dequeued++;
                application = HhHistory.Link(context, vacancy, known[observation.VacancyId], targetStage.Value, now);
                imported++;
                if (FunnelService.IsInterview(targetStage.Value) && observation.UpdatedAt > now.AddDays(-14))
                    await transitions.AdvanceAsync(context, outbox, application, targetStage.Value, EventChannel.HeadHunter,
                        $"статус hh: {observation.State}", manual: true, ct);
                continue;
            }
            var before = application.Stage;
            if (await transitions.AdvanceAsync(context, outbox, application, targetStage.Value, EventChannel.HeadHunter,
                $"статус hh: {observation.State}", manual: false, ct) && application.Stage != before)
            {
                changed++;
                if (targetStage != ApplicationStage.Viewed)
                    notifier.Add(context, NotificationKind.Info, StatusCard(application, targetStage.Value), [CareerCards.StageRow(application.Id)], application.Id);
            }
            if (observation.HasUnread && observation.UpdatedAt > now.AddDays(-3))
            {
                if (observation.ChatId is { } chatId && chats.Count < 5)
                    chats.Add(new(application.Id, chatId, observation.VacancyId));
                else if (announced < 5 && !cursors.ContainsKey(cursorKey))
                {
                    announced++;
                    var marker = new AppSetting { Key = cursorKey, Value = "announced", UpdatedAt = now };
                    context.Settings.Add(marker);
                    cursors.Add(cursorKey, marker);
                    notifier.Add(context, NotificationKind.Info,
                        $"💬 <b>hh: новое сообщение</b> · {TextUtil.EscapeHtml(vacancy.CompanyName ?? "?")} — {TextUtil.EscapeHtml(vacancy.Title)}\n" +
                        $"{Cards.Link($"https://{options.ApplyHost}/applicant/negotiations", "Открыть чаты hh")} · #{vacancy.Id}\n" +
                        "Если пригласили — отметьте стадию, подготовлю пакет к интервью.", [CareerCards.StageRow(application.Id)], application.Id);
                }
            }
        }
        if (imported > 0)
        {
            var states = list.GroupBy(row => HhNegotiations.Direction(row.State) ?? row.State).ToDictionary(group => group.Key, group => group.Count());
            notifier.Add(context, NotificationKind.Info,
                $"📥 <b>История откликов с {options.ApplyHost}</b>: {list.Length} " +
                $"(ждут ответа {states.GetValueOrDefault("sent")}, приглашения {states.GetValueOrDefault("invitation")}, отказы {states.GetValueOrDefault("rejected")}).\n" +
                $"{imported} из них — по вакансиям, которые собрала система: добавлены в воронку" +
                (dequeued > 0 ? $", {dequeued} убраны из очереди на одобрение (вы на них уже откликались)." : ".") +
                "\nНа вакансии из этой истории система больше не откликается и не предлагает их.");
        }
        await EmailInboxSources.AcknowledgeHhRefreshJoinedAsync(context, observedAt, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return new(imported, dequeued, changed, chats, unknownStates.ToArray());
    }

    private static string StatusCard(Application application, ApplicationStage stage)
    {
        var vacancy = application.Vacancy!;
        var (icon, text) = stage switch
        {
            ApplicationStage.HrScreen => ("📞", "приглашение на hh"),
            ApplicationStage.Offer => ("🏆", "оффер на hh"),
            ApplicationStage.Rejected => ("❌", "отказ на hh"),
            ApplicationStage.Accepted => ("🎉", "hh: вы приняты"),
            _ => ("💬", FunnelService.Label(stage)),
        };
        var extra = stage == ApplicationStage.HrScreen ? "\nГотовлю пакет к интервью. Когда договоритесь о времени — нажмите «Указать время» в карточке пакета." : "";
        return $"{icon} <b>{text}</b> · {TextUtil.EscapeHtml(vacancy.CompanyName ?? "?")} — {TextUtil.EscapeHtml(vacancy.Title)}\n{Cards.Link(vacancy.Url, "Вакансия")} · #{vacancy.Id}{extra}";
    }
}
