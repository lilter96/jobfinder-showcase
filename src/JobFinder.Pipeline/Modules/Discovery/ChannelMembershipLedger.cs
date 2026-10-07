using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class ChannelMembershipLedger(IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time)
{
    public async Task<ActionResult> ReconcileAsync(Guid requestId, Guid attemptId, bool checkedApplied, string evidence, CancellationToken ct)
    {
        if (requestId == Guid.Empty || attemptId == Guid.Empty || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000)
            return ActionResult.Fail("нужны исходная операция, попытка и проверенное свидетельство");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var root = await context.ChannelMembershipRequests.SingleOrDefaultAsync(row => row.Id == requestId, ct);
        if (root is not { Stage: ChannelMembershipStage.OutcomeUnknown } || root.AttemptId != attemptId) return ActionResult.Fail("неизвестная попытка изменилась; повторная сверка не записана");
        ChannelMembershipInput input;
        ChannelMembershipChat chat;
        try { input = ChannelMembershipInputs.Input(root); chat = ChannelMembershipInputs.Chat(root); }
        catch (UnsupportedChannelMembershipException) { return ActionResult.Fail("повреждённые исходные данные не позволяют подтвердить адрес операции"); }
        var attempt = await context.ChannelMembershipAttempts.SingleOrDefaultAsync(row => row.Id == attemptId, ct);
        try
        {
            if (attempt is null) return ActionResult.Fail("исходная попытка отсутствует");
            ChannelMembershipInputs.Attempt(root, attempt);
        }
        catch (UnsupportedChannelMembershipException) { return ActionResult.Fail("повреждённое свидетельство попытки не позволяет записать сверку"); }
        if (attempt is not { Outcome: ChannelMembershipOutcome.OutcomeUnknown } || !ChannelMembershipInputs.Effect(attempt.Stage)
            || attempt.RequestId != root.Id || attempt.EnvelopeId != root.ClaimEnvelopeId || attempt.AccountId != root.AccountId
            || attempt.PeerId != chat.Id || attempt.InputHash != root.InputHash || attempt.ChatHash != root.ChatHash)
            return ActionResult.Fail("попытка не совпадает с исходной операцией");
        var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == root.ChannelId, ct);
        if (source is null || source.MembershipRequestId != root.Id || source.AccessHash != input.AccessHash || !input.Source.Matches(source)) return ActionResult.Fail("исходный канал изменился; новая версия не перезаписана");
        attempt.Outcome = ChannelMembershipOutcome.Reconciled;
        attempt.CheckedApplied = checkedApplied;
        attempt.Evidence = evidence.Trim();
        attempt.CompletedAt = time.GetUtcNow();
        root.Stage = ChannelMembershipStage.Reconciled;
        root.CompletedAt = time.GetUtcNow();
        root.RetryAt = null;
        root.Error = "исход проверен вручную; для новой подготовки подписки требуется отдельное одобрение";
        source.Status = ChannelStatus.Failed;
        source.Error = root.Error;
        source.PeerId = chat.Id;
        source.AccessHash = chat.AccessHash;
        source.Title = chat.Title;
        source.Kind = chat.Kind;
        notifier.Add(context, NotificationKind.Info, "🔎 Telegram-операция " + root.Id + ": проверенный исход записан ("
            + (checkedApplied ? "действие выполнено" : "действие не выполнено") + "). Это не новый вызов Telegram и не подтверждение всей настройки канала.");
        await notifier.SaveAndFlushAsync(context, ct);
        return ActionResult.Done("сверка сохранена; автоматического повторения нет");
    }

    public static async Task<int> RecoverAsync(JobFinderDbContext context, Notifier notifier, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await ChannelMembershipRequests.LockAsync(context, ct);
        var roots = await context.ChannelMembershipRequests.Where(row => row.AttemptId != null && row.Stage != ChannelMembershipStage.Completed
            && row.Stage != ChannelMembershipStage.Cancelled && row.Stage != ChannelMembershipStage.Failed && row.Stage != ChannelMembershipStage.OutcomeUnknown
            && row.Stage != ChannelMembershipStage.Reconciled).ToListAsync(ct);
        foreach (var root in roots)
        {
            if (await context.ChannelMembershipAttempts.SingleOrDefaultAsync(row => row.Id == root.AttemptId, ct)
                is { Outcome: ChannelMembershipOutcome.Sending } attempt && attempt.RequestId == root.Id && attempt.Stage == root.Stage
                && attempt.EnvelopeId == root.ClaimEnvelopeId && attempt.AccountId == root.AccountId
                && attempt.InputHash == root.InputHash && attempt.ChatHash == root.ChatHash)
            {
                attempt.Outcome = ChannelMembershipOutcome.OutcomeUnknown;
                attempt.CompletedAt = now;
                attempt.Evidence = "process stopped with a saved membership effect claim; no receipt proves its outcome";
            }
            root.Stage = ChannelMembershipStage.OutcomeUnknown;
            root.RetryAt = null;
            root.Error = "прерванный Telegram-вызов требует сверки; автоматический повтор остановлен";
            var source = await context.TelegramChannels.SingleOrDefaultAsync(row => row.Id == root.ChannelId && row.MembershipRequestId == root.Id, ct);
            if (source is not null) source.Error = root.Error;
            notifier.Add(context, NotificationKind.Alert, "⚠️ Telegram-операция " + root.Id + ": " + TextUtil.EscapeHtml(root.Error));
        }
        await notifier.SaveAndFlushAsync(context, ct);
        return roots.Count;
    }
}
