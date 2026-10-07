using System.Text.Json.Nodes;
using JobFinder.Integrations.Browser;
using Microsoft.Playwright;

namespace JobFinder.Integrations.HeadHunter;

public enum ApplyOutcome
{
    Sent,
    AlreadyApplied,

    /// <summary>The employer wants answers or a test: the user finishes it by hand (the link is in the bot).</summary>
    NeedsManual,

    /// <summary>The saved login is gone: auto-apply pauses until <c>--login hh</c> is run again.</summary>
    SessionExpired,

    /// <summary>Captcha or an anti-bot page: auto-apply pauses.</summary>
    Blocked,
    Closed,

    /// <summary>Dry run: the vacancy page was checked (session, state, apply button) and captured; nothing was pressed.</summary>
    DryRun,
    Failed,
}

/// <param name="Contact">The vacancy's contact person, when the logged-in page shows one (a lead for outreach).</param>
/// <param name="LetterRequired">Dry run: the employer requires a letter, so pressing the button would open a form.</param>
public sealed record ApplyResult(ApplyOutcome Outcome, string Message, string? Screenshot = null, HhContact? Contact = null, bool LetterRequired = false);

/// <summary>
/// Sends an hh.ru / rabota.by response through the real site under the user's saved session. The page's own state JSON tells whether
/// the session is alive and whether the vacancy was already answered; the clicks use configurable selectors. Every outcome other
/// than <see cref="ApplyOutcome.Sent"/> leaves a screenshot.
/// </summary>
public sealed class HhApplier(BrowserSessions browser, HhOptions options, ILogger<HhApplier> log)
{
    public const string Site = "hh";

    public Task<bool> HasSessionAsync(CancellationToken ct) => browser.HasSessionAsync(Site, ct);

    public Task<ApplyResult> ApplyAsync(long vacancyId, string letter, bool dryRun, CancellationToken ct) =>
        browser.RunAsync(Site, page => ApplyOnPageAsync(page, vacancyId, letter, dryRun, ct), ct);

    /// <summary>
    /// Read-only look at a vacancy page under the session: no click, and no visit to the response address (which can respond by itself). Writes the response-related state
    /// and the form controls to <paramref name="dumpDir"/> and takes screenshots, so the apply flow can be checked against the real page.
    /// </summary>
    public Task<string> InspectAsync(long vacancyId, string dumpDir, CancellationToken ct) =>
        browser.RunAsync(Site, async page =>
        {
            Directory.CreateDirectory(dumpDir);
            var report = new System.Text.StringBuilder();
            await page.GotoAsync(HhStateParser.VacancyUrl(options.ApplyHost, vacancyId), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForTimeoutAsync(2500);
            var state = await ReadStateAsync(page);
            var vacancy = state?["vacancyView"]?["vacancyFull"]?["vacancy"];
            report.Append($"вакансия: userType={state?["userType"]}, «{vacancy?["name"]}», закрыта={HhStateParser.Bool(vacancy?["closedForApplicants"])}, ")
                .Append($"уже откликались={(state is not null && AlreadyAnswered(state, vacancyId))}, кнопка «Откликнуться»={await page.Locator(options.Selectors.ApplyButton).CountAsync()}");
            await browser.ScreenshotAsync(page, $"hh-{vacancyId}-inspect-vacancy");

            // Never open /applicant/vacancy_response here: for a "quick response" vacancy (one resume, no letter required, no questions)
            // merely loading that address SENDS the response (seen live 2026-09-27). Only the vacancy page is read.
            var controls = await page.EvaluateAsync<string>("""
                () => JSON.stringify([...document.querySelectorAll('[data-qa], textarea, input, button, [role="button"]')]
                  .filter(e => e.offsetParent !== null || e.tagName === 'INPUT')
                  .slice(0, 150).map(e => ({ tag: e.tagName, qa: e.getAttribute('data-qa'), type: e.getAttribute('type'), name: e.getAttribute('name'),
                    checked: e.checked === true, text: (e.innerText || e.value || '').trim().slice(0, 60) })), null, 1)
                """);
            await File.WriteAllTextAsync(Path.Combine(dumpDir, $"hh-{vacancyId}-response-controls.json"), controls, ct);
            report.Append($"\nскриншоты: {await browser.ScreenshotAsync(page, $"hh-{vacancyId}-inspect-response")}");
            return report.ToString();
        }, ct);

    private readonly System.Threading.RateLimiting.SlidingWindowRateLimiter _accountReads = new(new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
    {
        PermitLimit = Math.Max(1, options.AccountReadsPerDay), Window = TimeSpan.FromDays(1), SegmentsPerWindow = 24, QueueLimit = 0, AutoReplenishment = true,
    });

    /// <summary>
    /// A vacancy page read under the user's session, only for what the user asked for (a link they sent): one page view, no clicks, the
    /// same state JSON the anonymous reader parses. A real logged-in browser passes the site's JavaScript check, so this works while the
    /// anonymous reader is blocked. null = not available now (no session, the day's reads used up, a captcha or an anti-bot page): the
    /// caller falls back to the anonymous reader.
    /// </summary>
    /// <summary>Account reads left in the last day's window.</summary>
    public long AccountReadsLeft => _accountReads.GetStatistics()?.CurrentAvailablePermits ?? 0;

    public async Task<HhVacancyDetail?> ReadVacancyAsync(long vacancyId, CancellationToken ct)
    {
        if (!await HasSessionAsync(ct)) return null;
        using var lease = _accountReads.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            log.Warn($"[hh] account reads: {options.AccountReadsPerDay} a day used up, vacancy {vacancyId} goes the anonymous way");
            return null;
        }

        return await browser.RunAsync(Site, async page =>
        {
            var response = await page.GotoAsync(HhStateParser.VacancyUrl(options.ApplyHost, vacancyId), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await HumanPauseAsync(ct);
            if (response?.Status == 404) throw new HttpRequestException($"404 vacancy {vacancyId}", null, System.Net.HttpStatusCode.NotFound);
            if (page.Url.Contains("captcha", StringComparison.OrdinalIgnoreCase) || await ReadStateAsync(page) is not { } state)
            {
                log.Warn($"[hh] vacancy {vacancyId} under the session: captcha or an anti-bot page, {await browser.ScreenshotAsync(page, $"hh-{vacancyId}-read-blocked")}");
                return null;
            }

            return HhStateParser.ParseVacancy(state, options.ApplyHost);
        }, ct);
    }

    /// <summary>Opens the start page and reads the user type from the state JSON: "applicant" means the session works.</summary>
    public Task<bool> CheckSessionAsync(CancellationToken ct) =>
        browser.RunAsync(Site, async page =>
        {
            await page.GotoAsync($"https://{options.ApplyHost}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            return await UserTypeAsync(page) == "applicant";
        }, ct);

    public static async Task<string?> UserTypeAsync(IPage page)
    {
        var state = await ReadStateAsync(page);
        return state?["userType"]?.ToString();
    }

    private async Task<ApplyResult> ApplyOnPageAsync(IPage page, long vacancyId, string letter, bool dryRun, CancellationToken ct)
    {
        var s = options.Selectors;
        var url = HhStateParser.VacancyUrl(options.ApplyHost, vacancyId);
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await HumanPauseAsync(ct);

        if (page.Url.Contains("captcha", StringComparison.OrdinalIgnoreCase)) return await Fail(ApplyOutcome.Blocked, "captcha on the vacancy page");

        var state = await ReadStateAsync(page);
        if (state is null) return await Fail(ApplyOutcome.Blocked, "no page state (anti-bot page?)");
        if (state["userType"]?.ToString() != "applicant") return await Fail(ApplyOutcome.SessionExpired, $"userType={state["userType"]}");

        var vacancy = state["vacancyView"]?["vacancyFull"]?["vacancy"];
        var contact = ReadContact(vacancy?["contactInfo"]);
        if (HhStateParser.Bool(vacancy?["closedForApplicants"]) || HhStateParser.Bool(vacancy?["status"]?["archived"]))
            return await Fail(ApplyOutcome.Closed, "vacancy is closed or archived");
        if (AlreadyAnswered(state, vacancyId) || await VisibleAsync(page, s.AlreadyApplied))
        {
            // Answered already (by hand, or a quick response that went without a letter): the letter can still go under it.
            if (dryRun || string.IsNullOrWhiteSpace(letter) || !await LetterBoxAsync(page)) return new ApplyResult(ApplyOutcome.AlreadyApplied, "already applied");
            var (added, note, _) = await SendLetterAsync(page, letter, vacancyId, ct);
            return new ApplyResult(ApplyOutcome.AlreadyApplied, added ? "already applied, letter added" : $"already applied; letter: {note}");
        }

        // Questions or a test: the user answers them, the system does not (and does not press anything).
        if (HhStateParser.Bool(vacancy?["userTestPresent"]) || HhStateParser.Bool(vacancy?["test"]?["hasTests"]))
            return await Fail(ApplyOutcome.NeedsManual, "работодатель просит ответить на вопросы или пройти тест");

        var apply = page.Locator(s.ApplyButton).First;
        if (!await apply.IsVisibleAsync()) return await Fail(ApplyOutcome.NeedsManual, "no apply button");

        // Pressing the button (or merely opening /applicant/vacancy_response) RESPONDS at once when the account has one resume and the
        // letter is optional — seen live 2026-09-27. A dry run therefore stops before the button.
        var letterRequired = HhStateParser.Bool(vacancy?["@responseLetterRequired"]) || HhStateParser.Bool(vacancy?["responseLetterRequired"]);
        if (dryRun)
            return new ApplyResult(ApplyOutcome.DryRun,
                "dry run, nothing pressed: " + (letterRequired ? "the letter is required, a form will open" : "likely a quick response, the letter goes under it"),
                await browser.ScreenshotAsync(page, $"hh-{vacancyId}-dryrun"), contact, letterRequired);

        await apply.ClickAsync();
        await HumanPauseAsync(ct);
        for (var step = 0; step < 3 && await VisibleAsync(page, s.RelocationConfirm); step++)
        {
            await page.Locator(s.RelocationConfirm).First.ClickAsync();
            await HumanPauseAsync(ct);
        }

        if (await VisibleAsync(page, s.TestMarker) || page.Url.Contains("/applicant/vacancy_response", StringComparison.Ordinal)
            && !await VisibleAsync(page, s.SubmitButton))
            return await Fail(ApplyOutcome.NeedsManual, "the employer asks questions or a test");

        // The popup (several resumes, or a required letter) takes the letter before the response.
        var letterInForm = false;
        if (await VisibleAsync(page, s.SubmitButton))
        {
            await PickResumeAsync(page);
            letterInForm = await FillLetterAsync(page, letter, ct);
            await page.Locator(s.SubmitButton).First.ClickAsync();
            await HumanPauseAsync(ct);
        }

        try
        {
            await page.Locator(s.Success).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
            return await Fail(ApplyOutcome.Failed, "no success marker after the response");
        }

        log.Info($"[hh-apply] {vacancyId}: sent");
        if (letterInForm || string.IsNullOrWhiteSpace(letter)) return new ApplyResult(ApplyOutcome.Sent, "sent", Contact: contact);

        // A quick response: the letter goes into the field under «Резюме доставлено».
        var (sent, letterNote, shot) = await SendLetterAsync(page, letter, vacancyId, ct);
        return new ApplyResult(ApplyOutcome.Sent, sent ? "sent, letter added" : $"sent; letter: {letterNote}", sent ? null : shot, Contact: contact);

        async Task<ApplyResult> Fail(ApplyOutcome outcome, string message)
        {
            var failShot = await browser.ScreenshotAsync(page, $"hh-{vacancyId}-{outcome}");
            log.Warn($"[hh-apply] {vacancyId}: {outcome} ({message})");
            return new ApplyResult(outcome, message, failShot);
        }
    }

    /// <summary>
    /// Read-only look at the chat of an answered vacancy: opens «Чат», writes what the chat consists of (frames, messages, input, send
    /// control) to <paramref name="dumpPath"/> and takes a screenshot. Nothing is typed.
    /// </summary>
    public Task<string> InspectChatAsync(long vacancyId, string dumpPath, CancellationToken ct, string? probeText = null) =>
        browser.RunAsync(Site, async page =>
        {
            await page.GotoAsync(HhStateParser.VacancyUrl(options.ApplyHost, vacancyId), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await HumanPauseAsync(ct);
            var open = page.Locator("[data-qa='vacancy-response-link-view-topic'], [data-qa='open-vacancy-chat']").First;
            if (!await open.IsVisibleAsync()) return "на странице нет кнопки «Чат»";
            await open.ClickAsync();
            await page.WaitForTimeoutAsync(5000);
            var report = new System.Text.StringBuilder($"url после клика: {page.Url}; фреймов: {page.Frames.Count}");
            var dump = new JsonArray();
            foreach (var frame in page.Frames)
            {
                string json;
                try
                {
                    json = await frame.EvaluateAsync<string>("""
                        () => JSON.stringify({ url: location.href, items: [...document.querySelectorAll('[data-qa], textarea, [contenteditable="true"], button, [role="button"], [role="textbox"]')]
                          .filter(e => e.offsetParent !== null).slice(0, 200)
                          .map(e => ({ tag: e.tagName, qa: e.getAttribute('data-qa'), role: e.getAttribute('role'), aria: e.getAttribute('aria-label'),
                                       editable: e.getAttribute('contenteditable'), text: (e.innerText || e.value || '').trim().slice(0, 120) })) })
                        """);
                }
                catch (PlaywrightException e)
                {
                    json = System.Text.Json.JsonSerializer.Serialize(new { url = frame.Url, error = e.Message });
                }

                dump.Add(JsonNode.Parse(json));
                report.Append($"\n  фрейм {frame.Url}");
            }

            await File.WriteAllTextAsync(dumpPath, dump.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), ct);
            report.Append($"\nскриншот: {await browser.ScreenshotAsync(page, $"hh-{vacancyId}-chat")}");

            // «Добавить сопроводительное» under our response bubble: opened (nothing typed) to see the letter form.
            var chat = page.Frames.FirstOrDefault(f => f.Url.Contains("chatik.", StringComparison.Ordinal));
            var addLetter = chat?.Locator("[data-qa='chatik-chat-message-applicant-action']").First;
            if (addLetter is not null && await addLetter.IsVisibleAsync())
            {
                await addLetter.ClickAsync();
                await page.WaitForTimeoutAsync(3000);
                var after = new JsonArray();
                foreach (var frame in page.Frames)
                {
                    try
                    {
                        after.Add(JsonNode.Parse(await frame.EvaluateAsync<string>("""
                            () => JSON.stringify({ url: location.href, items: [...document.querySelectorAll('textarea, [contenteditable="true"], button, [role="button"], [role="dialog"], [data-qa*="letter"], [data-qa*="modal"]')]
                              .filter(e => e.offsetParent !== null).slice(0, 120)
                              .map(e => ({ tag: e.tagName, qa: e.getAttribute('data-qa'), role: e.getAttribute('role'), aria: e.getAttribute('aria-label'),
                                           text: (e.innerText || e.value || '').trim().slice(0, 120) })) })
                            """)));
                    }
                    catch (PlaywrightException)
                    {
                    }
                }

                await File.WriteAllTextAsync(Path.ChangeExtension(dumpPath, ".letter.json"), after.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), ct);
                report.Append($"\nпосле «Добавить сопроводительное»: {page.Url}; скриншот: {await browser.ScreenshotAsync(page, $"hh-{vacancyId}-chat-letter")}");

                // With text the send control appears: typed, looked at, erased. Nothing is sent and Enter is never pressed.
                if (probeText is not null)
                {
                    var input = chat!.Locator("textarea[data-qa='text-input']").First;
                    await input.ClickAsync();
                    await page.Keyboard.TypeAsync(probeText, new KeyboardTypeOptions { Delay = 15 });
                    await page.WaitForTimeoutAsync(1500);
                    var buttons = await chat.EvaluateAsync<string>("""
                        () => JSON.stringify([...document.querySelectorAll('button, [role="button"]')].filter(e => e.offsetParent !== null)
                          .map(e => ({ qa: e.getAttribute('data-qa'), aria: e.getAttribute('aria-label'), type: e.getAttribute('type'),
                                       text: (e.innerText || '').trim().slice(0, 40), disabled: e.disabled === true })), null, 1)
                        """);
                    await File.WriteAllTextAsync(Path.ChangeExtension(dumpPath, ".send.json"), buttons, ct);
                    report.Append($"\nс текстом: скриншот {await browser.ScreenshotAsync(page, $"hh-{vacancyId}-chat-typed")}");
                    await input.FillAsync("");
                    report.Append($"; поле очищено: {(await input.InputValueAsync()).Length == 0}");
                }

                await page.Keyboard.PressAsync("Escape");
            }

            return report.ToString();
        }, ct);

    /// <summary>The letter for a vacancy the account has already answered (a quick response went without one): nothing else is pressed.</summary>
    public Task<(bool Ok, string Message, string? Screenshot)> AddLetterAsync(long vacancyId, string letter, string? dumpPath, CancellationToken ct) =>
        browser.RunAsync(Site, async page =>
        {
            await page.GotoAsync(HhStateParser.VacancyUrl(options.ApplyHost, vacancyId), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await HumanPauseAsync(ct);
            var state = await ReadStateAsync(page);
            if (state?["userType"]?.ToString() != "applicant") return (false, "сессия hh истекла (--login hh)", null);
            // The response must be confirmed by the page state itself: only then is the response address safe to open (for a
            // vacancy not yet answered, opening it RESPONDS).
            if (!AlreadyAnswered(state, vacancyId)) return (false, "на вакансию ещё нет отклика — письмо некуда добавлять", null);

            // Right after a quick response the field is under «Резюме доставлено»; later only the response page offers it.
            if (!await LetterBoxAsync(page))
            {
                await page.GotoAsync($"https://{options.ApplyHost}/applicant/vacancy_response?vacancyId={vacancyId}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await HumanPauseAsync(ct);
                var popup = (await ReadStateAsync(page))?["vacancyResponsePopup"]?["type"]?.ToString();
                if (popup != "alreadyApplied")
                {
                    if (dumpPath is not null) await File.WriteAllTextAsync(dumpPath, await ControlsAsync(page), ct);
                    return (false, $"страница отклика не подтвердила существующий отклик (type={popup}) — ничего не нажато",
                        await browser.ScreenshotAsync(page, $"hh-{vacancyId}-letter-unexpected"));
                }
            }

            var result = await SendLetterAsync(page, letter, vacancyId, ct);
            if (dumpPath is not null) await File.WriteAllTextAsync(dumpPath, await ControlsAsync(page), ct);
            return result;
        }, ct);

    private async Task<bool> LetterBoxAsync(IPage page) =>
        await VisibleAsync(page, options.Selectors.LetterSubmit)
        || await VisibleAsync(page, options.Selectors.LetterToggle) || await VisibleAsync(page, options.Selectors.LetterInput);

    /// <summary>
    /// Writes the letter into the field under the response and sends it; sent = the field is gone or empty afterwards. Never throws
    /// (but for cancellation): it runs after the response went out, so a failure here is "the letter did not go", told to the user —
    /// not a failed response that the retry then reports as "already applied" (seen live 2026-09-28 on #141, the letter was lost).
    /// </summary>
    private async Task<(bool Ok, string Message, string? Screenshot)> SendLetterAsync(IPage page, string letter, long vacancyId, CancellationToken ct)
    {
        try
        {
            return await SendLetterCoreAsync(page, letter, vacancyId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn($"[hh-apply] {vacancyId}: letter not added: {e.GetType().Name}: {e.Message}");
            return (false, $"ошибка на странице: {e.Message.Split('\n')[0]}", await browser.ScreenshotAsync(page, $"hh-{vacancyId}-letter-error"));
        }
    }

    private async Task<(bool Ok, string Message, string? Screenshot)> SendLetterCoreAsync(IPage page, string letter, long vacancyId, CancellationToken ct)
    {
        var s = options.Selectors;
        if (string.IsNullOrWhiteSpace(letter)) return (false, "нет письма", null);
        if (!await VisibleAsync(page, s.LetterInput) && await VisibleAsync(page, s.LetterToggle))
        {
            await page.Locator(s.LetterToggle).First.ClickAsync();
            await HumanPauseAsync(ct);
        }

        var input = page.Locator(s.LetterInput).First;
        if (!await input.IsVisibleAsync())
            return (false, "нет поля для письма под откликом", await browser.ScreenshotAsync(page, $"hh-{vacancyId}-letter-missing"));
        await input.FillAsync(letter);
        await page.WaitForTimeoutAsync(Random.Shared.Next(800, 1600));

        // The send button shows up only once there is text (seen live): the configured one, or the submit of the letter's own form.
        var submit = page.Locator(s.LetterSubmit).First;
        if (!await submit.IsVisibleAsync())
        {
            var form = input.Locator("xpath=ancestor::form[1]");
            submit = (await form.CountAsync()) > 0
                ? form.Locator("button[type='submit'], button:has-text('Отправить'), [role='button']:has-text('Отправить')").First
                : page.Locator("button:has-text('Отправить')").First;
        }

        if (!await submit.IsVisibleAsync())
        {
            // Nothing is sent: the field is emptied again and the page is kept for a look (every button, visible or not).
            var missing = await browser.ScreenshotAsync(page, $"hh-{vacancyId}-letter-no-button");
            var buttons = await page.EvaluateAsync<string>("""
                () => JSON.stringify([...document.querySelectorAll('button, [role="button"], [type="submit"]')].map(b => ({ tag: b.tagName, qa: b.getAttribute('data-qa'),
                  type: b.getAttribute('type'), text: (b.innerText || '').trim().slice(0, 40), visible: b.offsetParent !== null, disabled: b.disabled === true })), null, 1)
                """);
            await File.WriteAllTextAsync(Path.Combine("data", "debug", $"hh-{vacancyId}-letter-buttons.json"), buttons, ct);
            await input.FillAsync("");
            return (false, "поле для письма есть, а кнопки отправки нет — ничего не отправлено", missing);
        }

        await submit.ClickAsync();
        await page.WaitForTimeoutAsync(3500);
        var shot = await browser.ScreenshotAsync(page, $"hh-{vacancyId}-letter");
        var left = await input.IsVisibleAsync() && (await input.InputValueAsync()).Trim().Length > 0;
        return left ? (false, "письмо осталось в поле после нажатия «Отправить»", shot) : (true, "письмо отправлено", shot);
    }

    private static Task<string> ControlsAsync(IPage page) => page.EvaluateAsync<string>("""
        () => JSON.stringify([...document.querySelectorAll('[data-qa], textarea, button')].filter(e => e.offsetParent !== null)
          .map(e => ({ tag: e.tagName, qa: e.getAttribute('data-qa'), text: (e.innerText || e.value || '').trim().slice(0, 80) }))
          .filter(x => !(x.qa || '').startsWith('footer') && !(x.qa || '').startsWith('mainmenu')), null, 1)
        """);

    private async Task PickResumeAsync(IPage page)
    {
        if (string.IsNullOrWhiteSpace(options.ResumeTitle)) return;
        var resumes = page.Locator(options.Selectors.ResumeSelector);
        var count = await resumes.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var item = resumes.Nth(i);
            if ((await item.InnerTextAsync()).Contains(options.ResumeTitle, StringComparison.OrdinalIgnoreCase))
            {
                await item.ClickAsync();
                return;
            }
        }

        log.Warn($"[hh-apply] resume \"{options.ResumeTitle}\" not found in the form, the default one is used");
    }

    private async Task<bool> FillLetterAsync(IPage page, string letter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(letter)) return false;
        var s = options.Selectors;
        if (!await VisibleAsync(page, s.LetterInput) && await VisibleAsync(page, s.LetterToggle))
        {
            await page.Locator(s.LetterToggle).First.ClickAsync();
            await HumanPauseAsync(ct);
        }

        var input = page.Locator(s.LetterInput).First;
        if (!await input.IsVisibleAsync())
        {
            log.Warn("[hh-apply] no letter field in the form: the letter goes under the response");
            return false;
        }

        await input.FillAsync(letter);
        return true;
    }

    /// <summary>contactInfo: {fio, email, phones: [{country, city, number}] | {phones: [...]}} — every part optional.</summary>
    internal static HhContact? ReadContact(JsonNode? info)
    {
        if (info is not JsonObject) return null;
        var name = HhStateParser.Str(info["fio"]) ?? HhStateParser.Str(info["name"]);
        var email = HhStateParser.Str(info["email"]);
        var phones = info["phones"] is JsonObject wrapped ? wrapped["phones"] : info["phones"];
        var first = (phones as JsonArray)?.FirstOrDefault();
        string? phone = first is null
            ? null
            : $"+{HhStateParser.Str(first["country"])} {HhStateParser.Str(first["city"])} {HhStateParser.Str(first["number"])}".Trim();
        return name is null && email is null && phone is null ? null : new HhContact(name, email, phone);
    }

    private static bool AlreadyAnswered(JsonNode state, long vacancyId)
    {
        // The reliable signal (live, 2026-09-27): the page state lists the account's negotiation for this vacancy.
        var key = vacancyId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (state["applicantVacancyResponseStatuses"]?[key]?["negotiations"]?["topicList"] is JsonArray { Count: > 0 }) return true;
        var labels = state["userLabelsForVacancies"]?[vacancyId.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        var text = labels?.ToJsonString() ?? "";
        return text.Contains("RESPONSE", StringComparison.OrdinalIgnoreCase) || text.Contains("INVITATION", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonNode?> ReadStateAsync(IPage page)
    {
        // textContent of the template's fragment is the decoded JSON text (innerHTML would be re-escaped markup).
        var json = await page.EvaluateAsync<string?>(
            "() => { const t = document.getElementById('HH-Lux-InitialState'); return t ? (t.content ? t.content.textContent : t.textContent) : null; }");
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> VisibleAsync(IPage page, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return false;
        try
        {
            return await page.Locator(selector).First.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static Task HumanPauseAsync(CancellationToken ct) => Task.Delay(Random.Shared.Next(900, 2600), ct);
}
