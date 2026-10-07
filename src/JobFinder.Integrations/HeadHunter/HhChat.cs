using System.Text.Json.Nodes;
using JobFinder.Integrations.Browser;
using Microsoft.Playwright;

namespace JobFinder.Integrations.HeadHunter;

/// <param name="Mine">Written by the applicant (bubbles without an author name are ours).</param>
public sealed record HhChatMessage(string Id, string? Author, string Text, string? Time, bool Mine);

public sealed record HhChatSendResult(bool Ok, string Message, string? Screenshot, bool OutcomeUnknown = false);

/// <summary>
/// The hh chat of a response (chatik.{site}/chat/{chatId}, seen live 2026-09-27): messages are <c>chatik-chat-message-{id}</c> bubbles,
/// the input is <c>textarea[data-qa=text-input]</c>, the send button <c>chatik-do-send-message</c> appears once there is text, and
/// «Добавить сопроводительное» under our response bubble turns the same input into the response's cover letter. Enter sends, so text is
/// filled, never typed key by key (a line break would send half a message).
/// </summary>
public sealed class HhChat(BrowserSessions browser, HhOptions options, ILogger<HhChat> log)
{
    private const string Input = "textarea[data-qa='text-input']";
    private const string Bubble = "[data-qa^='chatik-chat-message-']";
    private const string Send = "[data-qa='chatik-do-send-message']";
    private const string AddLetter = "[data-qa='chatik-chat-message-applicant-action']";

    private const string MessagesScript = """
        () => JSON.stringify([...document.querySelectorAll('[data-qa^="chatik-chat-message-"]')]
          .filter(e => /^chatik-chat-message-\d+$/.test(e.getAttribute('data-qa')))
          .map(e => {
            const author = e.querySelector('[data-qa="chat-bubble-author-name"]');
            const title = e.querySelector('[data-qa="chat-bubble-title"]');
            const body = [...e.querySelectorAll('[data-qa="chat-bubble-text"]')].map(t => t.innerText.trim()).join('\n');
            const time = e.querySelector('[data-qa="chat-buble-display-time"]');
            return { id: e.getAttribute('data-qa').replace('chatik-chat-message-', ''), author: author ? author.innerText.trim() : null,
                     text: ((title ? title.innerText.trim() + '\n' : '') + body).trim(), time: time ? time.innerText.trim() : null };
          }))
        """;

    public Task<List<HhChatMessage>> ReadAsync(long chatId, long? vacancyId, CancellationToken ct) =>
        browser.RunAsync(HhApplier.Site, async page =>
        {
            var chat = await OpenAsync(page, chatId, vacancyId, ct, forReading: true);
            return await MessagesAsync(chat);
        }, ct);

    /// <param name="asCoverLetter">Sent through «Добавить сопроводительное» (the response's letter) instead of as a chat message.</param>
    public Task<HhChatSendResult> SendAsync(long chatId, long? vacancyId, string text, bool asCoverLetter, CancellationToken ct) =>
        browser.RunAsync(HhApplier.Site, async page =>
        {
            try
            {
                return await SendOnPageAsync(page, chatId, vacancyId, text, asCoverLetter, ct);
            }
            catch (Exception e) when (e is TimeoutException or PlaywrightException)
            {
                // Whatever broke, the page is kept for a look; the caller learns nothing was confirmed.
                return new HhChatSendResult(false, $"сбой в чате ({e.Message.Split('\n')[0]}) — проверьте чат", await browser.ScreenshotAsync(page, $"hh-chat-{chatId}-error"), OutcomeUnknown: true);
            }
        }, ct);

    private async Task<HhChatSendResult> SendOnPageAsync(IPage page, long chatId, long? vacancyId, string text, bool asCoverLetter, CancellationToken ct)
    {
            var chat = await OpenAsync(page, chatId, vacancyId, ct);
            var before = await MessagesAsync(chat);
            if (asCoverLetter)
            {
                var add = chat.Locator(AddLetter).First;
                if (!await add.IsVisibleAsync())
                    return new HhChatSendResult(false, "в чате нет «Добавить сопроводительное» (письмо уже добавлено?)", await browser.ScreenshotAsync(page, $"hh-chat-{chatId}-no-letter"));
                await add.ClickAsync();
                await page.WaitForTimeoutAsync(1500);
            }

            var input = chat.Locator(Input).First;
            if (!await input.IsVisibleAsync())
                return new HhChatSendResult(false, "нет поля ввода в чате", await browser.ScreenshotAsync(page, $"hh-chat-{chatId}-no-input"));
            await input.ClickAsync();
            await input.FillAsync(text);
            await page.WaitForTimeoutAsync(Random.Shared.Next(900, 1600));
            var send = chat.Locator(Send).First;
            if (!await send.IsVisibleAsync())
            {
                await input.FillAsync("");
                return new HhChatSendResult(false, "кнопка отправки не появилась — ничего не отправлено", await browser.ScreenshotAsync(page, $"hh-chat-{chatId}-no-send"));
            }

            await send.ClickAsync();
            await page.WaitForTimeoutAsync(3000);
            var after = await MessagesAsync(chat);
            var shot = await browser.ScreenshotAsync(page, $"hh-chat-{chatId}-sent");
            var delivered = (await input.InputValueAsync()).Trim().Length == 0
                            && IsConfirmed(before, after, text, asCoverLetter);
            log.Info($"[hh-chat] {chatId}: {(asCoverLetter ? "letter" : "message")} {(delivered ? "sent" : "not confirmed")}");
            return delivered
                ? new HhChatSendResult(true, asCoverLetter ? "сопроводительное добавлено" : "сообщение отправлено", shot)
                : new HhChatSendResult(false, "отправка не подтвердилась — проверьте чат", shot, OutcomeUnknown: true);
    }

    /// <summary>
    /// The chat as the site shows it: the vacancy page's «Чат» opens it in a frame (the path checked live, including the cover-letter
    /// mode); the chat's own address only when the vacancy is unknown.
    /// </summary>
    /// <param name="forReading">A chat the employer closed (a rejection) has messages but no input: enough to read it.</param>
    private async Task<IFrame> OpenAsync(IPage page, long chatId, long? vacancyId, CancellationToken ct, bool forReading = false)
    {
        if (vacancyId is { } id)
        {
            await page.GotoAsync(HhStateParser.VacancyUrl(options.ApplyHost, id), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForTimeoutAsync(Random.Shared.Next(1500, 2500));
            var open = page.Locator("[data-qa='vacancy-response-link-view-topic'], [data-qa='open-vacancy-chat']").First;
            if (!await open.IsVisibleAsync()) throw new InvalidOperationException($"на странице вакансии {id} нет «Чат» (отклика нет?)");
            await open.ClickAsync();
            for (var i = 0; i < 20; i++)
            {
                var frame = page.Frames.FirstOrDefault(f => f.Url.Contains("chatik.", StringComparison.Ordinal) && f.Url.Contains($"/chat/{chatId}", StringComparison.Ordinal))
                            ?? page.Frames.FirstOrDefault(f => f.Url.Contains("chatik.", StringComparison.Ordinal) && f.Url.Contains("/chat/", StringComparison.Ordinal));
                if (frame is not null && (await frame.Locator(Input).CountAsync() > 0 || (forReading && await frame.Locator(Bubble).CountAsync() > 0)))
                {
                    await page.WaitForTimeoutAsync(Random.Shared.Next(800, 1500));
                    return frame;
                }

                await page.WaitForTimeoutAsync(500);
            }

            throw new InvalidOperationException($"чат вакансии {id} не открылся");
        }

        var host = options.ApplyHost.StartsWith("www.", StringComparison.Ordinal) ? options.ApplyHost[4..] : options.ApplyHost;
        await page.GotoAsync($"https://chatik.{host}/chat/{chatId}?without_list=1", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        try
        {
            await page.Locator(forReading ? $"{Input}, {Bubble}" : Input).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"чат {chatId} не открылся (сессия hh? --login hh)");
        }

        await page.WaitForTimeoutAsync(Random.Shared.Next(800, 1500));
        return page.MainFrame;
    }

    private static async Task<List<HhChatMessage>> MessagesAsync(IFrame chat)
    {
        var json = await chat.EvaluateAsync<string>(MessagesScript);
        return (JsonNode.Parse(json)?.AsArray() ?? [])
            .Select(n => new HhChatMessage(
                HhStateParser.Str(n?["id"]) ?? "",
                HhStateParser.Str(n?["author"]),
                HhStateParser.Str(n?["text"]) ?? "",
                HhStateParser.Str(n?["time"]),
                HhStateParser.Str(n?["author"]) is null))
            .Where(m => m.Id.Length > 0)
            .ToList();
    }

    public static bool IsConfirmed(IReadOnlyList<HhChatMessage> before, IReadOnlyList<HhChatMessage> after, string text, bool asCoverLetter)
    {
        var expected = Normalize(text);
        if (expected.Length == 0) return false;
        return after.Any(message => message.Mine && Normalize(message.Text).Contains(expected, StringComparison.Ordinal)
            && !before.Any(old => old.Id == message.Id && (!asCoverLetter || Normalize(old.Text).Contains(expected, StringComparison.Ordinal))));
    }

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
