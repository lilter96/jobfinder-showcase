using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Workers;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class ApplicationDeliveryPublication(FunnelService funnel, Notifier notifier, TimeProvider time)
{
    public void Record(JobFinderDbContext context, Application application, DeliveryReceipt receipt)
    {
        var vacancy = application.Vacancy!;
        if (receipt.LetterRequired)
            foreach (var source in vacancy.Sources.Where(row => row.Source == SourceKind.HeadHunter)) source.LetterRequired = true;
        switch (receipt.Status)
        {
            case ApplicationStatus.Sent:
                vacancy.Status = VacancyStatus.Applied;
                application.Error = application.Channel == ApplicationChannel.HeadHunter ? ApplyWorker.Note(receipt.Error) : receipt.Error;
                funnel.Entered(context, application, FollowUpJob.ChannelOf(application.Channel));
                var text = application.Mode == ApplicationMode.Auto ? Cards.AutoApplied(vacancy)
                    : $"✅ Отклик отправлен: {TextUtil.EscapeHtml(vacancy.Title)} · #{vacancy.Id}";
                if (application.Error is { } note) text += "\n⚠️ " + TextUtil.EscapeHtml(note);
                notifier.Add(context, NotificationKind.AutoApplied, text, [CareerCards.StageRow(application.Id)], vacancy.Id);
                break;
            case ApplicationStatus.DryRun:
                application.Error = "dry-run: " + receipt.Error;
                var (heldText, buttons) = Cards.DryRunHeld(vacancy, receipt.LetterRequired, ApplyWorker.DryRunHoldDays);
                notifier.Add(context, NotificationKind.ApplyDryRun, heldText, buttons, vacancy.Id,
                    receipt.Screenshot is { } screenshot ? Core.Options.StoredPath.ToStored(screenshot) : null);
                break;
            case ApplicationStatus.Cancelled:
                vacancy.Status = VacancyStatus.Rejected;
                vacancy.RejectReason = receipt.Error;
                notifier.Add(context, NotificationKind.ApplyFailed, Cards.ApplyProblem(vacancy, "закрыта", ApplyWorker.UserText(receipt.Error ?? "")), entityId: vacancy.Id);
                break;
            case ApplicationStatus.Pending:
                notifier.Add(context, NotificationKind.ApplyFailed,
                    $"⏸ Отклики hh приостановлены; подтверждено, что отклик #{vacancy.Id} не отправлен. {TextUtil.EscapeHtml(receipt.Error ?? "проверьте сессию hh")}", entityId: vacancy.Id);
                break;
            case ApplicationStatus.NeedsManual:
            case ApplicationStatus.OutcomeUnknown:
                var reason = receipt.Status == ApplicationStatus.OutcomeUnknown ? "доставку нужно сверить; повтор остановлен" : "нужно вручную";
                var problem = Cards.ApplyProblem(vacancy, reason, ApplyWorker.UserText(receipt.Error ?? ""));
                if (receipt.Status == ApplicationStatus.OutcomeUnknown && application.DeliveryRequestId is { } requestId)
                    problem += $"\nID операции: <code>{requestId}</code>\nРезультат записывается через --application-reconcile; это не повтор отправки.";
                notifier.Add(context, NotificationKind.ApplyFailed, problem,
                    Cards.ManualRow(vacancy.Id), vacancy.Id);
                break;
        }
        vacancy.UpdatedAt = time.GetUtcNow();
    }
}
