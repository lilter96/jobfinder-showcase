using System.Globalization;
using System.Text.Json;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Telegram;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Controls;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace JobFinder.Bot;

public sealed class BotOptions
{
    public const string Section = "Bot";

    public bool Enabled { get; set; } = true;

    public string Token { get; set; } = "";

    /// <summary>The only Telegram user the bot talks to (also the chat id notifications go to).</summary>
    public long OwnerUserId { get; set; }
}

/// <summary>
/// The control panel in Telegram: long polling, owner-only. Buttons and commands call the same <see cref="ApprovalService"/> actions as the
/// dashboard; free text and voice go to the active career dialogue (<see cref="CareerBot"/>) or answer a "send me the text" prompt.
/// </summary>
public sealed partial class BotService(BotOptions options, TelegramBotClientHolder holder, ApprovalService approvals, RuntimeSettings settings,
    JobFinder.Integrations.Email.EmailSender email, JobFinder.Pipeline.Profile.ProfileProvider profiles,
    JobFinder.Pipeline.Documents.ResumeTailor tailor, CareerBot career, JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db, HhApplier applier,
    TelegramUserClient telegram, ILlmUsageSink usage, JobFinder.Pipeline.Ingest.HhLinkQueue links, JobFinder.Pipeline.Career.ContactRegistry contacts,
    JobFinder.Pipeline.Ingest.PostReparser reparser, LocalClock clock, ILogger<BotService> log, JobFinder.Pipeline.Career.BotEdits edits, BotControls controls,
    OwnerBotMessages messages, JobFinder.Pipeline.Modules.Hiring.ApplicationBlocks blocks,
    JobFinder.Pipeline.Modules.Hiring.ApplicationAutomationControls automationControls, JobFinder.Pipeline.Modules.Discovery.ChannelDecisions channelDecisions)
    : BackgroundService, IBotControlExecutor
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (holder.Client is not { } bot)
        {
            log.Warn("[bot] Bot:Token is empty, the Telegram bot is off");
            return;
        }

        try
        {
            await bot.DeleteWebhook(cancellationToken: stoppingToken);
            var me = await bot.GetMe(stoppingToken);
            log.Info($"[bot] @{me.Username} is polling (owner {options.OwnerUserId})");
            await bot.SetMyCommands(
            [
                new BotCommand { Command = "status", Description = "Состояние системы" },
                new BotCommand { Command = "digest", Description = "Дайджест сейчас" },
                new BotCommand { Command = "top", Description = "Ждут решения" },
                new BotCommand { Command = "pause", Description = "Пауза автооткликов" },
                new BotCommand { Command = "resume", Description = "Снять паузу" },
                ..CareerBot.Commands,
                new BotCommand { Command = "help", Description = "Все команды" },
            ], cancellationToken: stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error($"[bot] start failed: {e.Message}");
            return;
        }

        var offset = await controls.OffsetAsync(bot.BotId, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                updates = await bot.GetUpdates(offset, 50, 20, [UpdateType.Message, UpdateType.CallbackQuery], stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                var root = e;
                while (root.InnerException is not null) root = root.InnerException;
                log.Warn($"[bot] polling failed: {e.Message}{(root == e ? "" : $" ({root.GetType().Name}: {root.Message})")}");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                continue;
            }

            try
            {
                offset = await controls.AdmitAsync(bot.BotId, options.OwnerUserId, offset, updates.Select(update => new BotPollUpdate
                {
                    UpdateId = update.Id, PayloadJson = IsOwner(update.Message?.From ?? update.CallbackQuery?.From) ? JsonSerializer.Serialize(update) : null,
                }).ToArray(), stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.Error($"[bot] durable update admission failed: {error.GetType().Name}; polling offset not acknowledged", error);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    public bool CanExecute(BotControlInput input)
    {
        if (!options.Enabled || holder.Client is not { } bot || bot.BotId != input.BotId || options.OwnerUserId != input.OwnerId) return false;
        Decode(input);
        return true;
    }

    public async Task ExecuteAsync(BotControlInput input, CancellationToken ct)
    {
        if (!CanExecute(input) || holder.ControlClient is not { } bot) throw new InvalidOperationException("captured bot account/owner unavailable");
        var update = Decode(input);
        if (update.CallbackQuery is { } callback) await OnCallbackAsync(bot, callback, ct);
        else if (update.Message is { } message) await OnMessageAsync(bot, message, ct);
    }

    private Update Decode(BotControlInput input)
    {
        try
        {
            var update = JsonSerializer.Deserialize<Update>(input.PayloadJson);
            if (update is null || update.Id != input.UpdateId || !IsOwner(update.Message?.From ?? update.CallbackQuery?.From)
                || update.Message is null && update.CallbackQuery is null) throw new UnsupportedBotControlException();
            return update;
        }
        catch (JsonException) { throw new UnsupportedBotControlException(); }
    }

    private bool IsOwner(User? user) => options.OwnerUserId != 0 && user?.Id == options.OwnerUserId;

    private async Task OnMessageAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        if (!IsOwner(msg.From) || msg.Chat.Id != options.OwnerUserId) return;

        if (await career.TryEditAsync(bot, msg, ct)) return;
        if (msg.Chat.Id == options.OwnerUserId && msg.ReplyToMessage is { } replied && !string.IsNullOrWhiteSpace(msg.Text))
        {
            try
            {
                var edit = await edits.FindAsync(bot.BotId, options.OwnerUserId, replied.MessageId, replied.Text, replied.From?.Id, ct);
                if (edit is not null)
                {
                    await OnEditReplyAsync(bot, msg, edit, ct);
                    return;
                }
            }
            catch (JobFinder.Pipeline.Career.BotEditPromptException error)
            {
                await messages.PublishAsync(bot, options.OwnerUserId, "⚠️ " + TextUtil.EscapeHtml(error.Message), ct);
                return;
            }
        }

        if (msg.Voice is not null || msg.Audio is not null || msg.VideoNote is not null)
        {
            await career.VoiceAsync(bot, msg, ct);
            return;
        }

        var text = msg.Text?.Trim() ?? msg.Caption?.Trim() ?? "";
        if (text.Length > 0 && !text.StartsWith('/') && HhClient.ParseVacancyIds(text).Count > 0)
        {
            // A vacancy shared from the hh app (or pasted), with the recruiter's @username if given: into the usual pipeline, whatever
            // dialogue is open.
            await messages.PublishAsync(bot, msg.Chat.Id, await AddLinksAsync(text, ct), ct);
            return;
        }

        if (text.Length > 0 && !text.StartsWith('/') && VacancyRef().Match(text) is { Success: true } vref &&
            JobFinder.Pipeline.Career.ContactRegistry.TelegramHandles(text) is { Count: > 0 } handles)
        {
            // "#123 @hr_anna": a recruiter found by hand, for a vacancy the system knows (the # of its cards and pages).
            var (linkedText, linkedButtons) = await LinkRecruitersAsync(long.Parse(vref.Groups[1].Value, CultureInfo.InvariantCulture), handles, ct);
            await messages.PublishAsync(bot, msg.Chat.Id, linkedText, ct, linkedButtons);
            return;
        }

        if (text.Length > 0 && !text.StartsWith('/'))
        {
            // Free text is an answer in the active dialogue (story, practice interview, offer, debrief, interview time).
            if (!await career.TryTextAsync(bot, text, ct))
                await messages.PublishAsync(bot, msg.Chat.Id, "Сейчас нет активного диалога. /help — список команд.", ct);
            return;
        }

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.Length > 0 ? parts[0].Split('@')[0].ToLowerInvariant() : "";
        var arg = parts.Length > 1 ? parts[1] : "";
        if (await career.TryCommandAsync(bot, command, arg, ct)) return;
        var reply = command switch
        {
            "/start" or "/help" => Help(),
            "/status" => await StatusAsync(ct),
            "/digest" => Run(JobNames.Digest),
            "/top" => await TopAsync(ct),
            "/pause" => await SetAsync(RuntimeSettings.AutoApplyPaused, true, "⏸ Автоотклики на паузе (карточки на одобрение продолжат приходить)", ct),
            "/resume" => await ResumeAsync(ct),
            "/dryrun" => await DryRunAsync(arg is "on" or "1" or "да", ct),
            "/limit" => await SetIntAsync(RuntimeSettings.DailyAutoApplyLimit, arg, 0, 200, "Лимит автооткликов в день", ct),
            "/threshold" => await SetIntAsync(RuntimeSettings.AutoApplyThreshold, arg, 0, 101, "Порог автоотклика", ct),
            "/approval" => await SetIntAsync(RuntimeSettings.ApprovalThreshold, arg, 0, 100, "Порог карточки на одобрение", ct),
            "/comments" => await SetAsync(RuntimeSettings.CommentsPaused, arg is "off" or "0" or "нет",
                arg is "off" or "0" or "нет" ? "Черновики комментариев выключены" : "Черновики комментариев включены", ct),
            "/run" => Run(arg),
            "/reparse" => Reparse(bot, msg.Chat.Id, arg, ct),
            "/channels" => await ChannelsAsync(ct),
            "/testsend" => arg.Length > 0 ? TestSendVacancy(bot, msg.Chat.Id, arg, ct) : await TestSendAsync(ct),
            _ => "Не понял. /help — список команд.",
        };
        await messages.PublishAsync(bot, msg.Chat.Id, reply, ct);
    }

    private async Task OnCallbackAsync(ITelegramBotClient bot, CallbackQuery cb, CancellationToken ct)
    {
        if (!IsOwner(cb.From))
        {
            await bot.AnswerCallbackQuery(cb.Id, "Нет доступа", cancellationToken: ct);
            return;
        }

        var parts = (cb.Data ?? "").Split(':');
        if (parts.Length < 3 || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
            return;
        }

        var action = parts[0] + ":" + parts[1];
        var arg = parts.Length > 3 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0;
        var commentRevision = parts.Length > 4 && long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) ? revision : 0;
        if (action.StartsWith("c:", StringComparison.Ordinal) && (commentRevision < 0 || parts.Length > 4 && !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            await bot.AnswerCallbackQuery(cb.Id, "Некорректная версия карточки", showAlert: true, cancellationToken: ct);
            return;
        }
        var comment = new JobFinder.Pipeline.Modules.Engagement.CommentDecisionSnapshot { Id = id, Revision = commentRevision };
        if (await career.TryCallbackAsync(bot, cb, action, id, arg, ct)) return;
        ActionResult result;
        switch (action)
        {
            case Callback.ApplyVacancy:
                result = await approvals.ApplyAsync(id, ct);
                break;
            case Callback.SkipVacancy:
                result = await approvals.DismissAsync(id, ct);
                break;
            case Callback.ManualDone:
                result = await approvals.MarkManualAsync(id, ct);
                break;
            case Callback.RegenLetter:
                await bot.AnswerCallbackQuery(cb.Id, "Пишу новое письмо…", cancellationToken: ct);
                var (regen, problems) = await approvals.RegenerateLetterAsync(id, ct);
                if (regen.Ok) await SendVacancyCardAsync(bot, id, ct, problems);
                else await messages.PublishAsync(bot, options.OwnerUserId, "⚠️ " + TextUtil.EscapeHtml(regen.Message), ct);
                return;
            case Callback.ResumePdf:
                await bot.AnswerCallbackQuery(cb.Id, "Готовлю резюме под вакансию…", cancellationToken: ct);
                await SendTailoredResumeAsync(bot, id, ct);
                return;
            case Callback.EditLetter:
                await AskForTextAsync(bot, cb, "letter", id, $"✏️ Пришлите текст письма для вакансии #{id} ответом на это сообщение.", ct);
                return;
            case Callback.PickComment:
                result = await approvals.PickCommentAsync(comment, arg, ct);
                break;
            case Callback.EditComment:
                await AskForTextAsync(bot, cb, "comment", id, $"✏️ Пришлите текст комментария для #c{id} ответом на это сообщение.", ct, comment);
                return;
            case Callback.SkipComment:
                result = await approvals.RejectCommentAsync(comment, ct);
                break;
            case Callback.CancelComment:
                result = await approvals.CancelCommentAsync(comment with { Status = CommentStatus.Approved }, ct);
                break;
            case Callback.ApproveChannels:
                result = await approvals.ApproveChannelBatchAsync(id, ct);
                break;
            default:
                await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                return;
        }

        await bot.AnswerCallbackQuery(cb.Id, TextUtil.Truncate(result.Message, 190), showAlert: !result.Ok, cancellationToken: ct);
        if (result.Ok && cb.Message is { } card)
        {
            if (await messages.EditKeyboardAsync(bot, card.Chat.Id, card.MessageId,
                    [["✔️ " + TextUtil.Truncate(result.Message, 50) + "|noop:0:0"]], ct) is null)
                log.Debug("[bot] keyboard has no verified native card receipt; not queued");
        }
    }

    private async Task OnEditReplyAsync(ITelegramBotClient bot, Message msg, JobFinder.Pipeline.Career.CapturedBotEdit captured, CancellationToken ct)
    {
        if (captured.Value.Completed)
        {
            await messages.PublishAsync(bot, options.OwnerUserId, "Этот запрос уже обработан. Откройте новый запрос редактирования.", ct);
            return;
        }
        var submitted = await edits.RecordReplyAsync(captured, msg.MessageId, msg.Text!, ct);
        if (submitted is null)
        {
            await messages.PublishAsync(bot, options.OwnerUserId, "⚠️ Запрос изменился; сохранённый текст не перезаписан.", ct);
            return;
        }
        var edit = submitted.Value;
        var text = edit.Draft!.Trim();
        if (edit.Kind == "comment")
        {
            var result = edit.Comment is { } snapshot ? await approvals.SetCommentAsync(snapshot, text, ct) : ActionResult.Fail("нет версии карточки: откройте редактирование заново");
            if (result.Ok) await edits.CompleteAsync(submitted, ct);
            await messages.PublishAsync(bot, msg.Chat.Id, (result.Ok ? "✅ " : "⚠️ ") + TextUtil.EscapeHtml(result.Message), ct);
            return;
        }

        var saved = await approvals.SetLetterAsync(edit.SourceId, text, edit.Letter, ct);
        if (!saved.Ok)
        {
            await messages.PublishAsync(bot, msg.Chat.Id, "⚠️ " + TextUtil.EscapeHtml(saved.Message), ct);
            return;
        }

        await edits.CompleteAsync(submitted, ct);
        await SendVacancyCardAsync(bot, edit.SourceId, ct);
    }

    private async Task AskForTextAsync(ITelegramBotClient bot, CallbackQuery cb, string kind, long id, string prompt, CancellationToken ct,
        JobFinder.Pipeline.Modules.Engagement.CommentDecisionSnapshot? comment = null)
    {
        var letter = kind == "letter" ? await approvals.LetterSnapshotAsync(id, ct) : null;
        if (kind == "letter" && letter is null)
        {
            await bot.AnswerCallbackQuery(cb.Id, "Вакансия не найдена", showAlert: true, cancellationToken: ct);
            return;
        }
        await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
        await edits.BeginQuestionAsync(bot.BotId, options.OwnerUserId,
            new JobFinder.Pipeline.Career.BotEditPrompt { Kind = kind, SourceId = id, Letter = letter, Comment = comment }, prompt, ct);
    }

    private async Task SendTailoredResumeAsync(ITelegramBotClient bot, long vacancyId, CancellationToken ct)
    {
        try
        {
            var resume = await tailor.GetOrCreateAsync(vacancyId, ct);
            var total = resume.KeywordsCovered.Count + resume.KeywordsMissing.Count;
            var caption = new System.Text.StringBuilder($"📄 Резюме под вакансию #{vacancyId} ({resume.Language})");
            if (total > 0) caption.Append($"\nATS-ключи: {resume.KeywordsCovered.Count}/{total}");
            if (resume.KeywordsMissing.Count > 0)
                caption.Append(" · нет в опыте: ").Append(TextUtil.EscapeHtml(TextUtil.Truncate(string.Join(", ", resume.KeywordsMissing), 300)));
            if (resume.Corrections.Count > 0) caption.Append($"\nПроверка фактов исправила {resume.Corrections.Count} мест(а) — подробности в дашборде");
            await messages.PublishAsync(bot, options.OwnerUserId, caption.ToString(), ct, attachmentPath: resume.PdfPath, attachmentName: resume.FileName);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn($"[bot] resume for #{vacancyId}: {e.Message}");
            await messages.PublishAsync(bot, options.OwnerUserId, "⚠️ Резюме не собрано: " + TextUtil.EscapeHtml(e.Message), ct);
        }
    }

    /// <summary>A fresh approval card (after the letter changed), with the buttons of the current state.</summary>
    /// <param name="letterProblems">What the checks still found in the letter: the card says it next to the letter.</param>
    private async Task SendVacancyCardAsync(ITelegramBotClient bot, long id, CancellationToken ct, IReadOnlyList<string>? letterProblems = null)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.Include(x => x.Sources).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return;
        var (text, buttons) = Cards.VacancyApproval(v, ApplyRoutes.Resolve(v, email.IsConfigured), letterProblems: letterProblems);
        await messages.PublishAsync(bot, options.OwnerUserId, text, ct, buttons, entityId: id);
    }

    /// <summary>Off also says what happens to the responses dry-run held: they queue again (the apply worker releases them).</summary>
    private async Task<string> DryRunAsync(bool on, CancellationToken ct)
    {
        await settings.SetAsync(RuntimeSettings.ApplyDryRun, on, ct);
        if (on) return $"🧪 Dry-run: вакансия открывается и проверяется, кнопка отклика не нажимается. Проверенные отклики ждут /dryrun off (до {ApplyWorker.DryRunHoldDays} дн.)";
        await using var ctx = await db.CreateDbContextAsync(ct);
        var cutoff = clock.UtcNow.AddDays(-ApplyWorker.DryRunHoldDays);
        var held = await ctx.Applications.CountAsync(a => a.Status == ApplicationStatus.DryRun && a.CreatedAt >= cutoff, ct);
        if (held > 0) triggers.Trigger(JobNames.Apply);
        return held > 0 ? $"Dry-run выключен, в очередь уходят проверенные отклики: {held}" : "Dry-run выключен";
    }

    private async Task<string> StatusAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var decision = await settings.GetDecisionAsync(ct);
        var since = clock.StartOfTodayUtc();
        var autoToday = await ctx.Applications.CountAsync(ApplyWorker.CountsTowardAutoLimit(since), ct);
        var newVacancies = await ctx.Vacancies.CountAsync(v => v.Status == VacancyStatus.New, ct);
        var newPosts = await ctx.RawPosts.CountAsync(p => p.Status == RawPostStatus.New, ct);
        var pendingApps = await ctx.Applications.CountAsync(a => a.Status == ApplicationStatus.Pending, ct);
        var waiting = await ctx.Vacancies.CountAsync(v => v.Status == VacancyStatus.Scored && v.Decision == Decision.Approval, ct);
        var channels = await ctx.TelegramChannels.GroupBy(c => c.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var blocked = await settings.GetInstantAsync(RuntimeSettings.ApplyBlockedUntil, ct);
        var dryRun = await settings.GetBoolAsync(RuntimeSettings.ApplyDryRun, false, ct);
        var spent = await usage.SpentTodayUsdAsync(ct);
        var lastRuns = await ctx.JobRuns.GroupBy(r => r.Job)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First()).ToListAsync(ct);

        var sb = new StringBuilder("📊 <b>Состояние</b>\n");
        sb.Append("Автоотклики: ").Append(decision.AutoApplyPaused ? "⏸ пауза" : "▶️ включены")
            .Append(dryRun ? " (dry-run)" : "").Append(", сегодня ").Append(autoToday).Append('/').Append(decision.DailyAutoApplyLimit)
            .Append(", порог ").Append(decision.AutoApplyThreshold).Append(", одобрение от ").Append(decision.ApprovalThreshold).Append('\n');
        sb.Append("Сессия hh: ").Append(await applier.HasSessionAsync(ct) ? "есть" : "❌ нет (--login hh)");
        if (blocked > clock.UtcNow) sb.Append(", пауза до ").Append(TimeZoneInfo.ConvertTime(blocked.Value, clock.Zone).ToString("HH:mm", CultureInfo.InvariantCulture));
        sb.Append('\n');
        sb.Append("Telegram MTProto: ").Append(telegram.IsReady ? "подключён" : "❌ не подключён").Append('\n');
        sb.Append("Каналы: ").Append(string.Join(", ", channels.Select(c => $"{c.Key} {c.Count}"))).Append('\n');
        sb.Append("Очереди: постов ").Append(newPosts).Append(", вакансий ").Append(newVacancies).Append(", откликов ").Append(pendingApps)
            .Append(", ждут решения ").Append(waiting).Append('\n');
        sb.Append("LLM сегодня: $").Append(spent.ToString("0.###", CultureInfo.InvariantCulture)).Append("\n\n");
        foreach (var run in lastRuns.OrderBy(r => r.Job))
        {
            var at = TimeZoneInfo.ConvertTime(run.StartedAt, clock.Zone).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
            sb.Append(run.Status == JobRunStatus.Ok ? "✅ " : run.Status == JobRunStatus.Running ? "⏳ " : "❌ ").Append(run.Job).Append(' ').Append(at)
                .Append(" — ").Append(run.Items).Append(run.Message is null ? "" : " · " + TextUtil.EscapeHtml(TextUtil.Truncate(run.Message, 80))).Append('\n');
        }

        return sb.ToString();
    }

    private async Task<string> TopAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var list = await ctx.Vacancies.Where(v => v.Status == VacancyStatus.Scored && v.Decision == Decision.Approval)
            .OrderByDescending(v => v.Score).Take(15).ToListAsync(ct);
        if (list.Count == 0) return "Нет вакансий, ждущих решения.";
        var sb = new StringBuilder("⏳ <b>Ждут решения</b>\n");
        foreach (var v in list)
            sb.Append(Cards.ScoreBadge(v.Score)).Append(' ').Append(Cards.Link(v.Url, TextUtil.Truncate(v.Title, 60))).Append(" — ")
                .Append(TextUtil.EscapeHtml(TextUtil.Truncate(v.CompanyName ?? "?", 40))).Append(" · #").Append(v.Id).Append('\n');
        return sb.ToString();
    }

    private async Task<string> ChannelsAsync(CancellationToken ct)
    {
        return (await channelDecisions.PublishCurrentCardAsync(ct)).Message;
    }

    /// <summary>
    /// What a recruiter receives, sent to the user instead: Saved Messages for the DM, the own mailbox for the e-mail. Uses the server's
    /// own MTProto client (a second client on the same session would risk the auth key).
    /// </summary>
    /// <summary>
    /// «/testsend 371»: the message and the tailored PDF exactly as the vacancy's recruiter would get them, into «Избранное». In the
    /// background: a fresh letter and a resume take minutes.
    /// </summary>
    private string TestSendVacancy(ITelegramBotClient bot, long chatId, string arg, CancellationToken ct)
    {
        if (!long.TryParse(arg.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return "Формат: /testsend 371";
        _ = Task.Run(async () =>
        {
            string report;
            try
            {
                var preview = await approvals.ResponsePreviewAsync(id, ct) ?? throw new InvalidOperationException("нет вакансии или профиля");
                var resume = await tailor.AttachmentAsync(id, ct) ?? throw new InvalidOperationException("нет файла резюме");
                await telegram.SendToSavedMessagesAsync(JobFinder.Core.Text.HumanText.Clean(preview.Text), resume.Path, resume.FileName);
                report = $"✅ #{id}: сообщение ({preview.Text.Length} символов) и {TextUtil.EscapeHtml(resume.FileName)} — в «Избранном», как их увидит рекрутер" +
                         (preview.Problems.Count > 0 ? $"\n⚠️ Проверка письма: {TextUtil.EscapeHtml(string.Join("; ", preview.Problems.Take(3)))}" : "");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Warn($"[bot] /testsend {id}: {e.Message}");
                report = $"⚠️ /testsend {id}: " + TextUtil.EscapeHtml(e.Message);
            }

            await messages.PublishAsync(bot, chatId, report, ct);
        }, ct);
        return $"🧪 Готовлю отклик по #{id}: письмо и резюме под вакансию придут в «Избранное».";
    }

    private async Task<string> TestSendAsync(CancellationToken ct)
    {
        var resume = ApplyRoutes.Resume(profiles, await profiles.GetAsync(ct));
        if (resume is null) return "❌ Нет profile/resume.pdf — ЛС и письма уходят только с резюме.";
        const string sample = "Тест JobFinder: так рекрутер увидит отклик — текст письма и PDF-резюме в одном сообщении.";
        var lines = new List<string> { $"📎 Вложение: {TextUtil.EscapeHtml(resume.FileName)}" };
        if (telegram.IsReady)
        {
            try
            {
                await telegram.SendToSavedMessagesAsync(sample, resume.Path, resume.FileName);
                lines.Add("✅ Telegram: отправлено в «Избранное»");
            }
            catch (Exception e)
            {
                lines.Add("❌ Telegram: " + TextUtil.EscapeHtml(e.Message));
            }
        }
        else
        {
            lines.Add("❌ Telegram: MTProto не подключён");
        }

        if (email.IsConfigured)
        {
            try
            {
                await email.SendToSelfAsync("Тест JobFinder: отклик по почте", sample, resume, ct);
                lines.Add($"✅ Почта: письмо отправлено на {TextUtil.EscapeHtml(email.SelfAddress)}");
            }
            catch (Exception e)
            {
                lines.Add("❌ Почта: " + TextUtil.EscapeHtml(e.Message));
            }
        }
        else
        {
            lines.Add("⚪ Почта не настроена (Email:Password — пароль приложения Gmail)");
        }

        return string.Join('\n', lines);
    }

    private async Task<string> ResumeAsync(CancellationToken ct)
    {
        var policy = await settings.Automation.ReadAsync(ct);
        var block = await blocks.ReadAsync(ct);
        try { await automationControls.SetPauseAsync(policy, false, block, "bot:/resume", ct); }
        catch (Exception error) when (error is PolicyConflictException or JobFinder.Pipeline.Modules.Hiring.ApplicationBlockConflictException)
        {
            return "⚠️ Состояние изменилось во время команды; новая пауза не снята. Проверьте состояние и повторите /resume.";
        }
        triggers.Trigger(JobNames.Apply);
        return "▶️ Автоотклики включены";
    }

    private async Task<string> SetAsync(string key, bool value, string reply, CancellationToken ct)
    {
        await settings.SetAsync(key, value, ct);
        return reply;
    }

    private async Task<string> SetIntAsync(string key, string arg, int min, int max, string label, CancellationToken ct)
    {
        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            return $"Нужно число {min}–{max}";
        await settings.SetAsync(key, value, ct);
        return $"{label}: {value}";
    }

    private string Run(string job) =>
        string.IsNullOrWhiteSpace(job) ? $"Джобы: {string.Join(", ", triggers.Names.Order())}"
        : triggers.Trigger(job) ? $"▶️ {TextUtil.EscapeHtml(job)} запущен" : $"Нет джоба {TextUtil.EscapeHtml(job)}";

    /// <summary>
    /// Re-reads vacancy posts in the background (pages and the model take minutes) and sends the report when done; the bot keeps answering.
    /// </summary>
    private string Reparse(ITelegramBotClient bot, long chatId, string arg, CancellationToken ct)
    {
        var ids = arg.Split([' ', ',', '#'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToList();
        _ = Task.Run(async () =>
        {
            string report;
            try
            {
                report = JobFinder.Pipeline.Ingest.PostReparser.Report(await reparser.ReparseAsync(ids, ids.Count > 0 ? ids.Count * 3 : 25, ct));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Error($"[bot] /reparse failed: {e.Message}", e);
                report = "⚠️ /reparse: " + TextUtil.EscapeHtml(e.Message);
            }

            await messages.PublishAsync(bot, chatId, report, ct);
        }, ct);
        return ids.Count > 0
            ? $"🔁 Перечитываю посты вакансий {string.Join(", ", ids.Select(i => "#" + i))}: ссылки, страницы, контакты. Отчёт пришлю."
            : "🔁 Перечитываю посты вакансий из Telegram без контактов (до 25). Отчёт пришлю.";
    }

    private static string Help() => HelpText + CareerBot.Help;

    private async Task<string> AddLinksAsync(string text, CancellationToken ct)
    {
        var added = await links.AddAsync(text, ct);
        var ids = added.Ids;
        var what = (ids.Count == 1 ? $"вакансию {ids[0]}" : $"{ids.Count} вакансии: {string.Join(", ", ids)}") +
                   (added.Contacts.Count == 0 ? "" : $" и рекрутера {string.Join(", ", added.Contacts)} (привяжу к её компании)");
        const string flow = "дальше как обычно (фильтр, оценка, отклик или карточка). Итог придёт сюда.";
        if (added.ViaAccount) return $"➕ Принял {what}: читаю под вашим аккаунтом, {flow}";
        return added.PausedUntil is { } until
            ? $"➕ Принял {what}. Через аккаунт сейчас нельзя (нет сессии hh или лимит чтений на сегодня), а анонимный доступ на паузе после блокировки до " +
              $"{TimeZoneInfo.ConvertTime(until, clock.Zone):HH:mm}: прочитаю сразу после неё, {flow}"
            : $"➕ Принял {what}: читаю страницу, {flow}";
    }

    /// <summary>"#123 @hr_anna": the recruiter joins the vacancy's company; the reply offers to write to them and to pull the existing chat in.</summary>
    private async Task<(string Text, List<List<string>> Buttons)> LinkRecruitersAsync(long vacancyId, IReadOnlyList<string> handles, CancellationToken ct)
    {
        var linked = new List<JobFinder.Pipeline.Career.LinkedContact>();
        foreach (var handle in handles)
            if (await contacts.LinkAsync(vacancyId, handle, null, ct) is { } c) linked.Add(c);
        if (linked.Count == 0) return ($"Не нашёл вакансию #{vacancyId} (или у неё нет компании). Номер — тот, что в карточках и на дашборде.", []);
        var buttons = linked.Where(c => c.Kind == ContactKind.Telegram)
            .Select(c => new List<string> { Callback.Button($"✈️ Написать {c.Value}", CareerCallback.RecruiterDraft, vacancyId, (int)c.ContactId) }).ToList();
        buttons.Add([Callback.Button("🔄 Подтянуть переписку", CareerCallback.RecruiterSync, vacancyId)]);
        return ($"🔗 {TextUtil.EscapeHtml(string.Join(", ", linked.Select(c => c.Value)))} → {TextUtil.EscapeHtml(linked[0].Vacancy)} (#{vacancyId}).\n" +
                "Его сообщения в Telegram попадут в отклик на эту компанию. «✈️ Написать» — черновик под ситуацию (отклика нет, ждёте, отказ, " +
                "интервью) с резюме под вакансию; отправка кнопкой. «🔄» — перенести уже существующую переписку с ним в историю отклика.", buttons);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:^|\s)#(\d{1,7})\b")]
    private static partial System.Text.RegularExpressions.Regex VacancyRef();

    private const string HelpText = """
        🤖 <b>JobFinder</b>
        Карточки вакансий приходят сами: 🚀 откликнуться, ✏️ своё письмо, 🔄 другое письмо, 🙈 пропустить.
        Ссылка на вакансию hh / rabota.by (или «Поделиться» из приложения hh) добавляет её в обычный поток; с @username рядом — и рекрутера.
        #123 @username — привязать найденного вами рекрутера к вакансии #123.

        /status — состояние, очереди, последние запуски
        /top — вакансии, ждущие решения
        /digest — дайджест сейчас
        /pause, /resume — пауза автооткликов
        /limit 25 — автооткликов в день
        /threshold 80 — порог автоотклика; /approval 60 — порог карточки
        /dryrun on|off — проверять отклики hh без нажатия (ждут /dryrun off)
        /comments on|off — черновики комментариев Threads
        /channels — каналы, ждущие одобрения
        /run hh-search — запустить джоб (без аргумента — список)
        /reparse 341 333 — перечитать посты вакансий: скрытые ссылки, страницы по ссылкам, контакты (без номеров — все из Telegram без контактов)
        /testsend — пробный отклик себе: ЛС в «Избранное» и письмо на свою почту, с PDF; /testsend 371 — настоящий отклик по вакансии (письмо и резюме под неё) себе в «Избранное»
        """;
}
