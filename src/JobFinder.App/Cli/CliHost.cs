using System.Globalization;
using System.Text;
using System.Text.Json;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Browser;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.LinkedIn;
using JobFinder.Integrations.Telegram;
using JobFinder.Integrations.Threads;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Market;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Telegram.Bot;

namespace JobFinder.App.Cli;

public static class CliCommands
{
    /// <summary>Flag → description. The first known flag in the arguments wins.</summary>
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["--login-telegram"] = "первый вход MTProto-аккаунта (код из Telegram, из консоли или файла Telegram:CodeFile)",
        ["--login"] = "вход в браузере: --login hh | --login threads | --login linkedin (на хосте, окно браузера)",
        ["--install-browsers"] = "скачать Chromium для Playwright (--with-deps в контейнере)",
        ["--import-resume"] = "резюме из profile/resume.(pdf|docx|md|txt) → профиль кандидата в БД через LLM (правка — дашборд → Настройки)",
        ["--doctor"] = "проверить БД, LLM, hh, Хабр, Telegram, бота, сессии",
        ["--test-llm"] = "прогнать модели без ротации (кто реально отдаёт JSON): --test-llm [Application | Resume | другой маршрут]",
        ["--score"] = "оценить одну вакансию hh по ссылке: --score https://hh.ru/vacancy/123",
        ["--score-preview"] = "продолжить сохранённое read-only превью без повторного чтения hh: --score-preview <guid>",
        ["--add-vacancy"] = "сохранить ссылки hh в устойчивую очередь сервера (паузы сайта сохраняются): --add-vacancy <ссылка> [<ссылка>…]",
        ["--tailor"] = "резюме под вакансию: --tailor <id вакансии> [--force] — PDF, покрытие ATS-ключей, исправления проверки фактов",
        ["--interview"] = "подготовить пакет интервью через работающий сервер: --interview <id интервью> [--force] (без отправки)",
        ["--linkedin-search"] = "кто работает в компании (поиск LinkedIn + разбор моделью, как в аутриче): --linkedin-search \"EPAM Systems\"",
        ["--linkedin-read"] = "повторить разбор сохранённой страницы без поиска: --linkedin-read data/debug/linkedin-search.cards.json \"EPAM Systems\"",
        ["--read-link"] = "прочитать ссылку так, как её читают из поста вакансии (страницы, контакты, прокси; ничего не сохраняется): --read-link <url>",
        ["--threads-search"] = "один поиск в Threads под сохранённой сессией (проверка разбора): --threads-search \"ищем .NET разработчика\"",
        ["--comment-test"] = "заново написать комментарии к постам сохранённых черновиков Threads (без записи и публикации): --comment-test 3 5",
        ["--threads-reply-verify"] = "есть ли опубликованный комментарий черновика на посте (только чтение): --threads-reply-verify <id черновика>",
        ["--threads-reply-check"] = "проверить публикацию ответа на живом посте без публикации: --threads-reply-check <ссылка на пост>",
        ["--comment-refresh"] = "переписать ожидающие черновики Threads по текущим правилам: новая карточка в бот, у старой гаснут кнопки: --comment-refresh 3 5",
        ["--post-process"] = "сохранить/дождаться обработки raw post в работающем сервере: --post-process <id> [--refresh]",
        ["--comment-recard"] = "перерисовать карточки ожидающих черновиков Threads в боте по текущему тексту (кнопки те же): --comment-recard 66 67",
        ["--hh-negotiations"] = "прочитать страницу «Отклики» rabota.by под сессией (только чтение): статус по каждой вакансии",
        ["--email-reprocess"] = "заново разобрать входящее письмо события (после исправления разбора): --email-reprocess <id события>",
        ["--hh-chat-read"] = "прочитать чат hh так, как его читает история откликов (только чтение): --hh-chat-read <id чата> <номер вакансии hh>",
        ["--hh-chat-check"] = "открыть чат hh по отклику и снять его структуру (только чтение): --hh-chat-check <номер вакансии hh>",
        ["--hh-chat-send"] = "отправить в чат hh отклика: --hh-chat-send <номер вакансии hh> --letter (сопроводительное из вакансии) | --text \"…\"",
        ["--reply-redraft"] = "переписать черновик ответа на входящее сообщение заново (новая карточка в боте, отправка кнопкой): --reply-redraft <id события> [--preview — только показать]",
        ["--reply-process"] = "статус native-обработки ответа; --retry повторяет только остановленный входящий анализ: --reply-process <guid> [--retry]",
        ["--letter"] = "сопроводительное к отклику на вакансию (как для автоотклика), только показать: --letter <id вакансии>",
        ["--application-check"] = "проверить письмо и PDF без отправки: --application-check <id вакансии> [--fresh] (новый текст, не меняя историю)",
        ["--letters-refresh"] = "переписать письма вакансий, ждущих решения, текущим писателем; их карточки в боте обновляются на месте (новых карточек нет): --letters-refresh [<id>…] (без id — за 7 дней)",
        ["--recruiter-draft"] = "черновик сообщения рекрутеру компании вакансии в Telegram, только показать (без отправки): --recruiter-draft <id вакансии> [--fresh | --context]",
        ["--reply-card"] = "свой черновик ответа на входящее сообщение и новая карточка в боте (отправка — кнопкой): --reply-card <id события> --draft \"…\"",
        ["--hh-add-letter"] = "добавить сопроводительное письмо к отправленному отклику через синхронизированный чат hh: --hh-add-letter <id вакансии в JobFinder>",
        ["--message-reconcile"] = "записать ручную сверку неопределённой доставки (не отправляет): --message-reconcile <guid> --sent|--not-sent --evidence \"…\"",
        ["--application-reconcile"] = "сверить неопределённую доставку первого отклика (не отправляет): --application-reconcile <guid> --sent|--not-sent --evidence \"…\"",
        ["--comment-reconcile"] = "сверить неопределённую публикацию Threads (не отправляет): --comment-reconcile <guid> --sent|--not-sent --evidence \"…\"",
        ["--channel-membership-reconcile"] = "сверить Telegram-операцию без вызова MTProto: --channel-membership-reconcile <guid> --attempt <guid> --applied|--not-applied --evidence \"…\"",
        ["--notification-reconcile"] = "сверить неопределённое уведомление (не отправляет): --notification-reconcile <guid> --sent|--not-sent --evidence \"…\"",
        ["--bot-update-reconcile"] = "разблокировать сохранённую команду после ручной проверки (не повторяет): --bot-update-reconcile <guid> --evidence \"…\"",
        ["--hh-apply-check"] = "посмотреть вакансию и полную страницу отклика под сессией hh, ничего не нажимая: --hh-apply-check 135602805",
        ["--backfill-eval"] = "дополнить оценки старых вакансий: ATS-ключи, язык, легитимность, рынок (решения и уведомления не меняются)",
        ["--migrate"] = "применить миграции БД, перенести в БД состояние из файлов старой раскладки (профиль, факты, сессии) и выйти",
        ["--help"] = "эта справка",
    };

    public static bool TryGet(string[] args, out string command)
    {
        command = args.FirstOrDefault(a => All.ContainsKey(a)) ?? "";
        return command.Length > 0;
    }
}

/// <summary>One-shot commands: the same service graph as the server, no background loops, no web host.</summary>
public static class CliHost
{
    public static async Task<int> RunAsync(string command, string[] args, string home)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = home });
        AppHome.AddConfiguration(builder.Configuration);
        builder.Logging.ClearProviders().AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning).AddFilter("System", LogLevel.Warning);
        // One-off commands trace too (a redraft, a chat read): the spans reach the Aspire dashboard when OTLP is configured.
        JobFinder.ServiceDefaults.ServiceDefaultsExtensions.AddServiceDefaults(builder, web: false);
        builder.Services.AddJobFinderCore(builder.Configuration);
        builder.Services.AddJobFinderBot(builder.Configuration, withLoops: false);
        var messaging = command is not ("--migrate" or "--help" or "--install-browsers");
        builder.AddJobFinderMessaging(consumer: false);
        using var host = builder.Build();
        var sp = host.Services;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var ct = cts.Token;
        var argument = Argument(args, command);
        try
        {
            if (command is not ("--help" or "--install-browsers"))
                JobFinder.Pipeline.Configuration.DeploymentConfiguration.Validate(sp, JobFinder.Pipeline.DatabaseOptions.Section,
                    JobFinder.Core.Options.PathsOptions.Section);
            if (messaging) await host.StartAsync(ct);
            return command switch
            {
                "--login-telegram" => await LoginTelegramAsync(sp, ct),
                "--login" => await LoginBrowserAsync(sp, argument, ct),
                "--install-browsers" => BrowserSessions.InstallBrowsers(args.Contains("--with-deps")),
                "--import-resume" => await ImportResumeAsync(sp, argument, ct),
                "--doctor" => await Doctor.RunAsync(sp, ct),
                "--test-llm" => await TestLlmAsync(sp, argument, ct),
                "--score" => await ScoreAsync(sp, argument, ct),
                "--score-preview" => Guid.TryParse(argument, out var previewId) ? await ScorePreviewResultAsync(sp, previewId, ct) : 2,
                "--add-vacancy" => await AddVacancyAsync(sp, args, ct),
                "--migrate" => await MigrateAsync(sp, ct),
                "--tailor" => await TailorAsync(sp, argument, args.Contains("--force"), ct),
                "--interview" => await InterviewAsync(sp, argument, args.Contains("--force"), ct),
                "--backfill-eval" => await BackfillEvaluationAsync(sp, ct),
                "--linkedin-search" => await LinkedInSearchAsync(sp, argument, ct),
                "--linkedin-read" => await LinkedInReadAsync(sp, args, ct),
                "--read-link" => await ReadLinkAsync(sp, argument, ct),
                "--threads-search" => await ThreadsSearchAsync(sp, argument, ct),
                "--comment-test" => await CommentTestAsync(sp, args, ct),
                "--hh-apply-check" => await HhApplyCheckAsync(sp, argument, ct),
                "--hh-add-letter" => await HhAddLetterAsync(sp, argument, ct),
                "--message-reconcile" => await MessageReconcileAsync(sp, argument, args, ct),
                "--application-reconcile" => await ApplicationReconcileAsync(sp, argument, args, ct),
                "--comment-reconcile" => await CommentReconcileAsync(sp, argument, args, ct),
                "--channel-membership-reconcile" => await ChannelMembershipReconcileAsync(sp, argument, args, ct),
                "--notification-reconcile" => await NotificationReconcileAsync(sp, argument, args, ct),
                "--bot-update-reconcile" => await BotUpdateReconcileAsync(sp, argument, args, ct),
                "--reply-card" => await ReplyCardAsync(sp, argument, args, ct),
                "--letter" => await LetterAsync(sp, argument, ct),
                "--application-check" => await ApplicationCheckAsync(sp, argument, args.Contains("--fresh"), ct),
                "--letters-refresh" => await LettersRefreshAsync(sp, args, ct),
                "--recruiter-draft" => await RecruiterDraftAsync(sp, argument, args.Contains("--fresh") ? true : args.Contains("--context") ? false : null, ct),
                "--reply-redraft" => await ReplyRedraftAsync(sp, argument, args.Contains("--preview"), ct),
                "--reply-process" => await ReplyProcessAsync(sp, argument, args.Contains("--retry"), ct),
                "--hh-chat-send" => await HhChatSendAsync(sp, argument, args, ct),
                "--hh-chat-check" => await HhChatCheckAsync(sp, argument, args, ct),
                "--hh-chat-read" => await HhChatReadAsync(sp, argument, args, ct),
                "--email-reprocess" => await EmailReprocessAsync(sp, argument, ct),
                "--hh-negotiations" => await HhNegotiationsAsync(sp, ct),
                "--comment-refresh" => await CommentRefreshAsync(sp, args, ct),
                "--post-process" => await PostProcessAsync(sp, argument, args.Contains("--refresh"), ct),
                "--comment-recard" => await CommentRecardAsync(sp, args, ct),
                "--threads-reply-check" => await ThreadsReplyCheckAsync(sp, argument, ct),
                "--threads-reply-verify" => await ThreadsReplyVerifyAsync(sp, argument, ct),
                _ => Help(),
            };
        }
        catch (Microsoft.Extensions.Options.OptionsValidationException error)
        {
            Console.Error.WriteLine("Configuration is invalid: " + string.Join("; ", error.Failures));
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        finally
        {
            if (messaging) await host.StopAsync(CancellationToken.None);
            // A command that connected to Telegram leaves its session in the database before the host (and its database access) goes away.
            if (command == "--login-telegram") await sp.GetRequiredService<TelegramUserClient>().ShutdownAsync();
        }
    }

    private static string ReadHidden()
    {
        var chars = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Length > 0) chars.Length--;
                continue;
            }

            chars.Append(key.KeyChar);
        }

        Console.WriteLine();
        return chars.ToString();
    }

    private static string? Argument(string[] args, string command)
    {
        var i = Array.IndexOf(args, command);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
    }

    private static int Help()
    {
        Console.WriteLine("JobFinder. Без флагов запускается сервер (дашборд + конвейер + бот). Команды:");
        foreach (var (flag, description) in CliCommands.All) Console.WriteLine($"  {flag,-20} {description}");
        return 0;
    }

    private static async Task<int> TestLlmAsync(IServiceProvider sp, string? onlyPurpose, CancellationToken ct)
    {
        var llm = await sp.GetRequiredService<JobFinderLlm>().ForOperationAsync(ct);
        if (onlyPurpose is not null && !llm.ConfiguredPurposes.Any(purpose => purpose.Equals(onlyPurpose, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Неизвестный маршрут LLM: " + onlyPurpose);
            return 2;
        }
        var failures = 0;
        await foreach (var (purpose, model, ok, detail, ms) in llm.ProbeRoutesAsync(
                           sp.GetRequiredService<JobFinder.Llm.Client.OpenRouterTransport>(), ct, onlyPurpose))
        {
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "✓" : "✗")} {purpose,-8} {model,-42} {ms,6} ms  {detail}");
        }

        return failures == 0 ? 0 : 1;
    }

    internal static async Task<int> InterviewAsync(IServiceProvider sp, string? argument, bool force, CancellationToken ct)
    {
        if (!long.TryParse(argument, out var id) || id <= 0)
        {
            Console.Error.WriteLine("Нужен id интервью: --interview 123");
            return 2;
        }
        var requests = sp.GetRequiredService<JobFinder.Pipeline.Modules.Preparation.InterviewPreparationRequests>();
        var requestId = await requests.RequestAsync(id, ct, force);
        if (requestId is not { } request)
        {
            Console.Error.WriteLine("Native-пакет недоступен: проверьте интервью; для legacy-пакета используйте --force.");
            return 1;
        }
        await requests.WaitAsync(request, ct);
        await using var context = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        var source = await context.Interviews.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (source?.PackPdfPath is null)
        {
            Console.Error.WriteLine("Пакет не готов; нужен действующий Scheduled interview и работающий native consumer.");
            return 1;
        }
        Console.WriteLine("PDF: " + JobFinder.Core.Options.StoredPath.Resolve(source.PackPdfPath));
        return 0;
    }

    private static async Task<int> TailorAsync(IServiceProvider sp, string? argument, bool force, CancellationToken ct)
    {
        if (!long.TryParse(argument, out var id))
        {
            Console.Error.WriteLine("Нужен id вакансии: --tailor 123");
            return 2;
        }

        var tailor = sp.GetRequiredService<JobFinder.Pipeline.Documents.ResumeTailor>();
        var resume = await tailor.GetOrCreateAsync(id, ct, force);
        Console.WriteLine($"PDF: {JobFinder.Core.Options.StoredPath.Resolve(resume.PdfPath)}");
        Console.WriteLine($"Файл для получателя: {resume.FileName}; язык {resume.Language}; модель {resume.Model}; ${resume.CostUsd:0.####}");
        Console.WriteLine($"ATS-ключи покрыты ({resume.KeywordsCovered.Count}): {string.Join(", ", resume.KeywordsCovered)}");
        Console.WriteLine($"Нет в опыте ({resume.KeywordsMissing.Count}): {string.Join(", ", resume.KeywordsMissing)}");
        Console.WriteLine($"Исправления проверки фактов ({resume.Corrections.Count}):");
        foreach (var c in resume.Corrections) Console.WriteLine("  - " + c);
        if (resume.ReportJson is { } json &&
            System.Text.Json.JsonSerializer.Deserialize(json, JobFinder.Core.Resume.ResumeJsonContext.Default.ResumeReport) is { } report)
        {
            Console.WriteLine($"Страниц: {report.Pages}; вёрстка: {(report.Layout.Count == 0 ? "разрывов нет" : string.Join("; ", report.Layout))}");
            if (report.Unshown.Count > 0) Console.WriteLine($"Есть в опыте, но не словами вакансии: {string.Join(", ", report.Unshown)}");
            if (report.Review is { } review)
            {
                Console.WriteLine($"Рецензия рекрутера: {review.Score}/10 — {review.Verdict}");
                foreach (var c in review.Checks) Console.WriteLine($"  {(c.Passed ? "✓" : "✗")} {c.Id}{(c.Note.Length > 0 && !c.Passed ? ": " + c.Note : "")}");
                foreach (var i in review.Issues) Console.WriteLine($"  - {i.Where}: {i.Problem} → {i.Fix}");
            }

            foreach (var r in report.Requirements)
                Console.WriteLine($"  {(r.MustHave ? "❗" : " ")} {r.Requirement} — {(string.IsNullOrWhiteSpace(r.Evidence) ? "нет в опыте" : r.Placement)}");
        }

        // The HTML next to the PDF, for looking at the layout in a browser.
        var doc = System.Text.Json.JsonSerializer.Deserialize(resume.ContentJson, JobFinder.Core.Resume.ResumeJsonContext.Default.ResumeDocument)!;
        var html = JobFinder.Pipeline.Documents.ResumeHtml.Render(doc, (await sp.GetRequiredService<ProfileProvider>().GetAsync(ct))!,
            sp.GetRequiredService<JobFinder.Pipeline.Documents.DocumentAssets>().FontCss);
        await File.WriteAllTextAsync(Path.ChangeExtension(JobFinder.Core.Options.StoredPath.Resolve(resume.PdfPath), ".html"), html, ct);
        return 0;
    }

    private static async Task<int> ApplicationCheckAsync(IServiceProvider sp, string? argument, bool fresh, CancellationToken ct)
    {
        if (!long.TryParse(argument, out var id)) return 2;
        using var stage = JobFinder.Core.Telemetry.Telemetry.StartStage("prepare", "application").Tag("vacancy.id", id).Tag("fresh", fresh);
        await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        var vacancy = await ctx.Vacancies.AsNoTracking().Include(v => v.Company).Include(v => v.Sources).FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vacancy is null || !fresh && string.IsNullOrWhiteSpace(vacancy.CoverLetter)) return 2;
        var route = JobFinder.Pipeline.Workers.ApplyRoutes.Resolve(vacancy, sp.GetRequiredService<JobFinder.Integrations.Email.EmailSender>().IsConfigured);
        if (route.Channel is not { } channel) return 2;
        var writer = sp.GetRequiredService<JobFinder.Pipeline.Career.LetterWriter>();
        JobFinder.Integrations.Email.EmailAttachment? preparedResume = null;
        if (fresh)
        {
            var draftTask = writer.ResponseAsync(vacancy, channel, route.Target, ct, force: true);
            var resumeTask = route.AttachesResume
                ? sp.GetRequiredService<JobFinder.Pipeline.Documents.ResumeTailor>().AttachmentAsync(id, ct)
                : Task.FromResult<JobFinder.Integrations.Email.EmailAttachment?>(null);
            await Task.WhenAll(draftTask, resumeTask);
            var draft = await draftTask;
            preparedResume = await resumeTask;
            if (draft is null) return 2;
            vacancy.CoverLetter = draft.Text;
            vacancy.LetterReview = draft.Review;
        }
        var review = await writer.ReviewResponseAsync(vacancy, channel, route.Target, HumanText.Clean(vacancy.CoverLetter), ct);
        stage.Tag("review", review.Score).Tag("problems", review.Problems.Count);
        Console.WriteLine($"#{id}: письмо {review.Score}/10, замечаний {review.Problems.Count}; отправка не выполняется");
        Console.WriteLine(vacancy.CoverLetter);
        foreach (var problem in review.Problems) Console.WriteLine("  ✗ " + problem);
        if (review.Problems.Count > 0 || review.Score < JobFinder.Pipeline.Career.LetterWriter.MinVerdict) return 1;
        if (route.AttachesResume)
        {
            var attachment = preparedResume ?? await sp.GetRequiredService<JobFinder.Pipeline.Documents.ResumeTailor>().AttachmentAsync(id, ct);
            Console.WriteLine("  PDF проверен перед вложением: " + attachment?.FileName);
        }
        return 0;
    }

    private static async Task<int> MigrateAsync(IServiceProvider sp, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        await using var guard = ActivatorUtilities.CreateInstance<SingleInstanceGuard>(sp);
        var publisher = sp.GetRequiredService<JobFinder.Pipeline.Messaging.NativeBootstrapPublisher>();
        await guard.StartAsync(stopping.Token);
        try
        {
            var ownership = sp.GetRequiredService<ServerOwnership>();
            await DatabaseInitializer.MigrateAsync(sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("migrate"), ownership, stopping.Token);
            ownership.EnsureOwned();
            await publisher.StartAsync(stopping.Token);
            var imported = await sp.GetRequiredService<JobFinder.Pipeline.Storage.FileStateImport>().ImportAsync(stopping.Token);
            ownership.EnsureOwned();
            foreach (var file in imported) Console.WriteLine($"{file.File} → {file.Target}: {file.Outcome}");
            if (imported.Count == 0) Console.WriteLine("Файлов старой раскладки нет: всё состояние уже в БД.");
            return 0;
        }
        finally
        {
            try { await publisher.StopAsync(CancellationToken.None); }
            finally { await guard.StopAsync(CancellationToken.None); }
        }
    }

    /// <summary>
    /// Asks for the code on the console AND watches <see cref="TelegramOptions.CodeFile"/>: whichever arrives first. The file lets the
    /// code be supplied by a tool that reads it from the Telegram service chat.
    /// </summary>
    private static async Task<int> LoginTelegramAsync(IServiceProvider sp, CancellationToken ct)
    {
        var options = sp.GetRequiredService<TelegramOptions>();
        var client = sp.GetRequiredService<TelegramUserClient>();
        if (options.ApiId == 0 || string.IsNullOrWhiteSpace(options.ApiHash))
        {
            Console.Error.WriteLine("Telegram:ApiId / Telegram:ApiHash не заданы (config/appsettings.Development.json)");
            return 2;
        }

        if (File.Exists(options.CodeFile)) File.Delete(options.CodeFile);
        var who = await client.LoginInteractiveAsync(async what =>
        {
            if (what == "phone_number")
            {
                Console.Write("Телефон (+375…): ");
                return Console.ReadLine();
            }

            if (what == "password")
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("Аккаунт защищён облачным паролем: запустите команду в терминале или впишите его в Telegram:Password.");
                    return null;
                }

                Console.Write("Облачный пароль Telegram (2FA, ввод скрыт): ");
                return ReadHidden();
            }

            Console.WriteLine($"Нужен {what}. Введите его здесь или запишите в файл {Path.GetFullPath(options.CodeFile)}");
            var console = Task.Run(Console.ReadLine, ct);
            while (!ct.IsCancellationRequested)
            {
                if (console.IsCompleted && !string.IsNullOrWhiteSpace(console.Result)) return console.Result.Trim();
                if (File.Exists(options.CodeFile))
                {
                    var code = (await File.ReadAllTextAsync(options.CodeFile, ct)).Trim();
                    File.Delete(options.CodeFile);
                    if (code.Length > 0) return code;
                }

                await Task.Delay(1000, ct);
            }

            return null;
        }, ct);
        Console.WriteLine($"Готово: вошли как {who}. Сессия сохранена в БД (telegram_sessions).");
        await client.ShutdownAsync();
        return 0;
    }

    private static async Task<int> LoginBrowserAsync(IServiceProvider sp, string? site, CancellationToken ct)
    {
        var browser = sp.GetRequiredService<BrowserSessions>();
        var hh = sp.GetRequiredService<HhOptions>();
        bool ok;
        switch (site)
        {
            case "hh":
                var block = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.ApplicationBlocks>().ReadAsync(ct);
                Console.WriteLine($"Откроется браузер: войдите в аккаунт соискателя на {hh.ApplyHost}. Сессия сохранится сама (или нажмите Enter).");
                ok = await browser.LoginInteractiveAsync(HhApplier.Site, $"https://{hh.ApplyHost}/account/login",
                    async page => await HhApplier.UserTypeAsync(page) == "applicant", TimeSpan.FromMinutes(10), ct);
                if (ok) await TryClearApplyBlockAsync(sp, block, ct);
                break;
            case "threads":
                Console.WriteLine("Откроется браузер: войдите в Threads (через Instagram). Сессия сохранится сама (или нажмите Enter).");
                ok = await browser.LoginInteractiveAsync(ThreadsBrowserClient.Site, "https://www.threads.com/login",
                    // Threads (on the Instagram login) keeps the session in sessionid / ds_user_id; the old threads.net domain may still set them.
                    async page => (await page.Context.CookiesAsync(["https://www.threads.com", "https://www.threads.net"]))
                        .Any(c => c.Name is "sessionid" or "ds_user_id" && !string.IsNullOrEmpty(c.Value)),
                    TimeSpan.FromMinutes(10), ct);
                break;
            case "linkedin":
                Console.WriteLine("Откроется браузер: войдите в LinkedIn. Используется только поиск людей (≤ LinkedIn:MaxSearchesPerDay в день), сообщения не отправляются.");
                ok = await browser.LoginInteractiveAsync(LinkedInClient.Site, "https://www.linkedin.com/login",
                    async page => (await page.Context.CookiesAsync(["https://www.linkedin.com"])).Any(c => c.Name == "li_at"),
                    TimeSpan.FromMinutes(10), ct);
                break;
            default:
                Console.Error.WriteLine("Укажите сайт: --login hh | --login threads | --login linkedin");
                return 2;
        }

        var siteKey = site switch { "hh" => HhApplier.Site, "linkedin" => LinkedInClient.Site, _ => ThreadsBrowserClient.Site };
        Console.WriteLine(ok ? $"Сессия сохранена в БД (документ {BrowserSessions.StateKey(siteKey)})" : "Вход не подтверждён.");
        return ok ? 0 : 1;
    }

    private static async Task TryClearApplyBlockAsync(IServiceProvider sp, JobFinder.Pipeline.Modules.Hiring.ApplicationBlockSnapshot block, CancellationToken ct)
    {
        try
        {
            await sp.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.ApplicationBlocks>().ReleaseAsync(block, "cli:hh-login", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.WriteLine($"(пауза автооткликов не снята: {e.Message}; проверьте состояние и используйте /resume в боте)");
        }
    }

    private static async Task<int> ImportResumeAsync(IServiceProvider sp, string? path, CancellationToken ct)
    {
        var profiles = sp.GetRequiredService<ProfileProvider>();
        var llm = sp.GetRequiredService<JobFinderLlm>();
        path ??= profiles.FindResume();
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("Положите резюме в profile/resume.pdf (или .docx/.md/.txt) либо укажите путь: --import-resume <файл>");
            return 2;
        }

        var text = ProfileProvider.ReadResumeText(path);
        var snapshot = await profiles.GetSnapshotAsync(ct);
        Console.WriteLine($"Резюме: {path} ({text.Length} символов). Извлекаю профиль…");
        var result = await llm.ImportResumeAsync(text, ct);
        var profile = result.Value;
        var existing = snapshot?.Value;
        if (existing is not null)
        {
            // What the user wrote by hand and the resume cannot know survives a re-import.
            profile = profile with
            {
                DesiredSalary = existing.DesiredSalary ?? profile.DesiredSalary,
                DealBreakers = existing.DealBreakers.Count > 0 ? existing.DealBreakers : profile.DealBreakers,
                Preferences = existing.Preferences.Count > 0 ? existing.Preferences : profile.Preferences,
            };
        }

        // Not scored against until the user checks it: salary, formats and deal-breakers are rarely in a resume.
        profile = profile with { Reviewed = false };
        try
        {
            await profiles.SaveAsync(profile, snapshot?.Version ?? 0, "resume-import", ct, keepPrevious: true);
        }
        catch (JobFinder.Pipeline.Modules.Candidate.CandidateConflictException)
        {
            Console.Error.WriteLine("Профиль изменён во время импорта. Ничего не перезаписано; проверьте актуальную версию перед повторным импортом.");
            return 3;
        }
        Console.WriteLine($"Готово ({result.Model}, ${result.CostUsd:0.####}): профиль в БД ({ProfileProvider.Key}{(existing is null ? "" : $", прежний — {ProfileProvider.PreviousKey}")})");
        Console.WriteLine($"{profile.Name} — {profile.Title}, {profile.YearsOfExperience} лет, навыков: {profile.Skills.Count}");
        Console.WriteLine("Проверьте и дополните: desiredSalary, dealBreakers, preferences, workFormats.");
        Console.WriteLine("Правка и подтверждение — дашборд → Настройки: оценка вакансий начнётся после кнопки «Профиль проверен».");
        return 0;
    }

    /// <summary>
    /// Vacancies scored before the career features have no ATS keywords, language, legitimacy or market note: one more scoring call fills
    /// them. Score, decision, status and notifications stay as they were (a scam verdict only marks the vacancy, it does not reject it).
    /// </summary>
    private static async Task<int> BackfillEvaluationAsync(IServiceProvider sp, CancellationToken ct)
    {
        var profile = await sp.GetRequiredService<ProfileProvider>().GetAsync(ct);
        if (profile is not { Reviewed: true })
        {
            Console.Error.WriteLine("Нужен проверенный профиль кандидата.");
            return 2;
        }

        var db = sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>();
        var assessments = sp.GetRequiredService<VacancyAssessments>();
        List<long> ids;
        await using (var ctx = await db.CreateDbContextAsync(ct))
        {
            var candidates = await ctx.Vacancies.AsNoTracking()
                .Where(v => (v.Status == VacancyStatus.Scored || v.Status == VacancyStatus.Applied) && v.Evaluation != null)
                .Select(v => new { v.Id, v.Evaluation }).ToListAsync(ct);
            ids = candidates.Where(c => c.Evaluation!.Keywords.Count == 0).Select(c => c.Id).ToList();
        }

        Console.WriteLine($"Без ATS-ключей: {ids.Count} вакансий");
        decimal cost = 0;
        var done = 0;
        foreach (var id in ids)
        {
            var requestId = await assessments.RequestAnnotationsAsync(id, ct);
            if (requestId is null) { Console.WriteLine($"#{id}: источник изменился или подготовка пока недоступна; данные сохранены."); continue; }
            Console.WriteLine($"#{id}: запрос {requestId}; нужен работающий сервер с текущими миграциями.");
            var request = await assessments.WaitAnnotationsAsync(requestId.Value, ct);
            await using var ctx = await db.CreateDbContextAsync(ct);
            cost += await ctx.LlmCalls.Where(call => call.OperationId == request.Id).SumAsync(call => call.CostUsd, ct);
            if (request.Stage != VacancyAssessmentStage.Completed)
            {
                Console.WriteLine($"#{id}: {request.Stage}, {request.Error}; запрос сохранён, история не изменена.");
                if (request.Stage is VacancyAssessmentStage.Scoring or VacancyAssessmentStage.Finishing) break;
                continue;
            }
            var v = await ctx.Vacancies.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            done++;
            Console.WriteLine($"#{id} {TextUtil.Truncate(v.Title, 50)}: {v.Evaluation!.Keywords.Count} ключей, {v.Evaluation.Language}, {v.Legitimacy}" +
                              (v.Evaluation.MarketNote is null ? "" : $", {v.Evaluation.MarketNote}"));
        }

        Console.WriteLine($"Готово: {done}/{ids.Count}, ${cost:0.####}");
        return 0;
    }

    /// <summary>
    /// Who works at a company, the way outreach looks for it: one "{company} recruiter" search, the model's reading of the page, the
    /// grounding check. The page's link structure and the blocks the model saw are saved for --linkedin-read.
    /// </summary>
    private static async Task<int> LinkedInSearchAsync(IServiceProvider sp, string? company, CancellationToken ct)
    {
        if (!await sp.GetRequiredService<LinkedInClient>().HasSessionAsync(ct))
        {
            Console.Error.WriteLine("Нет сессии LinkedIn — --login linkedin");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(company))
        {
            Console.Error.WriteLine("Укажите компанию: --linkedin-search \"EPAM Systems\"");
            return 2;
        }

        const string diagnose = "data/debug/linkedin-search.json";
        var people = await sp.GetRequiredService<JobFinder.Pipeline.Career.LinkedInPeopleReader>().FindAtCompanyAsync(company, ct, diagnose);
        Console.WriteLine($"Блоки страницы: {Path.ChangeExtension(diagnose, ".cards.json")} (повторить разбор без поиска: --linkedin-read)");
        return PrintPeople(company, people);
    }

    /// <summary>The model's reading of a saved page (no search): to tune the prompt on a real page without touching LinkedIn.</summary>
    private static async Task<int> LinkedInReadAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var i = Array.IndexOf(args, "--linkedin-read");
        var file = i + 1 < args.Length ? args[i + 1] : "data/debug/linkedin-search.cards.json";
        var company = i + 2 < args.Length ? args[i + 2] : "";
        if (!File.Exists(file) || string.IsNullOrWhiteSpace(company))
        {
            Console.Error.WriteLine("Формат: --linkedin-read <файл .cards.json> \"Компания\"");
            return 2;
        }

        var cards = LinkedInClient.ParseCards(await File.ReadAllTextAsync(file, ct));
        Console.WriteLine($"Блоков: {cards.Count}");
        var (people, reading) = await sp.GetRequiredService<JobFinder.Pipeline.Career.LinkedInPeopleReader>()
            .ReadWithReasonsAsync(company, new LinkedInSearchPage(cards, []), ct);
        foreach (var r in reading?.People.Where(r => r.IsResult) ?? [])
            Console.WriteLine($"  [{r.Index}] {r.Name}: сейчас «{r.CurrentCompany ?? "—"}», {r.CurrentRole ?? "—"}; найм={r.IsRecruiter}, в компании={r.WorksAtTarget}, " +
                              $"не работает={r.OnLeave}");
        return PrintPeople(company, people);
    }

    private static int PrintPeople(string company, List<JobFinder.Pipeline.Career.CompanyPerson> people)
    {
        Console.WriteLine($"Работают в «{company}» сейчас: {people.Count}");
        foreach (var p in people)
            Console.WriteLine($"  {(p.IsRecruiter ? "✓ найм" : "· не найм")}  {p.Name} — {TextUtil.Truncate(p.Headline, 80)}  [{p.ReadBy}]");
        Console.WriteLine("✓ найм = попадёт в аутрич (не больше трёх на компанию)");
        return 0;
    }

    /// <summary>The current prompt and style gate on real posts from stored drafts, side by side with what was generated before.</summary>
    private static async Task<int> CommentTestAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var profile = await sp.GetRequiredService<ProfileProvider>().GetAsync(ct) ?? throw new InvalidOperationException("нет профиля");
        var ids = args.SkipWhile(a => a != "--comment-test").Skip(1).Select(a => long.TryParse(a, out var id) ? id : 0).Where(id => id > 0).ToList();
        await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        var drafts = await ctx.CommentDrafts.AsNoTracking().Where(d => ids.Contains(d.Id)).OrderBy(d => d.Id).ToListAsync(ct);
        var planner = sp.GetRequiredService<JobFinder.Pipeline.Workers.CommentPlanner>();
        foreach (var d in drafts)
        {
            Console.WriteLine($"\n=== #{d.Id} @{d.Author}: {TextUtil.Truncate(d.PostText.Replace('\n', ' '), 220)}");
            Console.WriteLine("  было:");
            foreach (var v in d.Variants) Console.WriteLine($"    - {v}");
            if (!JobFinder.Pipeline.Workers.CommentPlanner.IsCandidate(d.PostText))
            {
                Console.WriteLine("  стало: пост отсеян до модели (не русский или ответ в чужой ветке)");
                continue;
            }

            CommentPlan plan;
            List<string> remarks;
            try
            {
                (plan, remarks) = await planner.PlanAsync(profile, d.Author, d.PostText, ct, force: true);
            }
            catch (JobFinder.Llm.Client.LlmUnavailableException e)
            {
                Console.WriteLine($"  модели не ответили: {e.Message}");
                continue;
            }

            if (remarks.Count > 0) Console.WriteLine($"  первая попытка не прошла проверку: {string.Join("; ", remarks)}");
            Console.WriteLine(plan.WorthCommenting ? $"  стало ({plan.Audience}):" : $"  стало: не комментировать — {plan.Reason}");
            foreach (var v in plan.Variants) Console.WriteLine($"    - {v}  [{v.Length}]");
        }

        return 0;
    }

    /// <summary>
    /// Pending drafts written before a prompt change get new variants. The old row is expired (its buttons answer "already Expired") and
    /// renamed so the post id is free for the new row; the old Telegram card loses its keyboard. Nothing is published.
    /// </summary>
    private static async Task<int> PostProcessAsync(IServiceProvider sp, string? argument, bool refresh, CancellationToken ct)
    {
        if (!long.TryParse(argument, out var id) || id <= 0) { Console.Error.WriteLine("--post-process <raw-post-id> [--refresh]"); return 1; }
        var requests = sp.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.PostProcessingRequests>();
        var request = await requests.RequestAsync(id, ct, refresh);
        if (request is null) { Console.Error.WriteLine("post is missing or not eligible for this operation"); return 1; }
        Console.WriteLine("saved post request: " + request);
        var result = await requests.WaitAsync(request.Value, ct);
        Console.WriteLine("post processed: vacancy=" + result.VacancyId + ", pages=" + result.Pages + ", contacts=" + result.After);
        return 0;
    }

    private static async Task<int> CommentRefreshAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var ids = args.SkipWhile(a => a != "--comment-refresh").Skip(1).Select(a => long.TryParse(a, out var id) ? id : 0).Where(id => id > 0).ToList();
        var db = sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>();
        var sources = sp.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.CommentSources>();
        foreach (var id in ids)
        {
            await using var ctx = await db.CreateDbContextAsync(ct);
            var old = await ctx.CommentDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (old is null || old.Status != CommentStatus.Pending)
            {
                Console.WriteLine($"#{id}: пропущен ({(old is null ? "нет такого" : old.Status.ToString())})");
                continue;
            }

            try
            {
                var request = await sources.RefreshAsync(JobFinder.Pipeline.Modules.Engagement.CommentSources.Snapshot(old), ct);
                if (request is null) { Console.WriteLine($"#{id}: пропущен (версия, профиль или пауза изменились)"); continue; }
                var fresh = await sources.WaitAsync(request.Value, ct);
                if (fresh is { Status: CommentStatus.Pending })
                {
                    Console.WriteLine($"#{id} → #{fresh.Id} @{old.Author}: {fresh.Variants.Count} варианта, карточка в очереди бота");
                    foreach (var variant in fresh.Variants) Console.WriteLine($"    - {variant}");
                }
                else Console.WriteLine($"#{id} @{old.Author}: больше не предлагается — {fresh?.Reason}");
            }
            catch (JobFinder.Llm.Client.LlmUnavailableException e)
            {
                Console.WriteLine($"#{id}: модели не ответили, черновик не тронут ({e.Message})");
                continue;
            }
        }

        return 0;
    }

    /// <summary>The bot card of a pending draft shows its current variants (after they were cleaned in the database).</summary>
    private static async Task<int> CommentRecardAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var ids = args.SkipWhile(a => a != "--comment-recard").Skip(1).Select(a => long.TryParse(a, out var id) ? id : 0).Where(id => id > 0).ToList();
        var bot = sp.GetRequiredService<TelegramBotClientHolder>().Client ?? throw new InvalidOperationException("Bot:Token пуст");
        var owner = sp.GetRequiredService<BotOptions>().OwnerUserId;
        await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        foreach (var id in ids)
        {
            var d = await ctx.CommentDrafts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Status == CommentStatus.Pending, ct);
            var messageId = await ctx.Notifications.Where(n => n.Kind == NotificationKind.CommentDraft && n.EntityId == id && n.TelegramMessageId != null)
                .OrderByDescending(n => n.Id).Select(n => n.TelegramMessageId).FirstOrDefaultAsync(ct);
            if (d is null || messageId is null)
            {
                Console.WriteLine($"#{id}: пропущен (нет ожидающего черновика или его карточки)");
                continue;
            }

            var (text, buttons) = JobFinder.Pipeline.Notifications.Cards.CommentDraft(d);
            await bot.EditMessageText(owner, messageId.Value, text, Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: NotificationDispatcher.Keyboard(buttons),
                linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct);
            Console.WriteLine($"#{id}: карточка {messageId} обновлена");
        }

        return 0;
    }

    private static async Task<int> HhNegotiationsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var list = await sp.GetRequiredService<HhNegotiations>().ReadAsync("data/debug", ct);
        Console.WriteLine($"Откликов на странице: {list.Count} (состояние страницы: data/debug/hh-negotiations-state.json)");
        foreach (var n in list.Take(40))
            Console.WriteLine($"  {n.VacancyId} {n.State,-12} {(n.HasUnread ? "✉" : " ")} {TextUtil.Truncate(n.CompanyName, 25)} — {TextUtil.Truncate(n.VacancyName, 50)}");
        return 0;
    }

    /// <summary>An inbound e-mail event read with the old parser: the message is fetched again and analysed anew (new card in the bot).</summary>
    private static async Task<int> EmailReprocessAsync(IServiceProvider sp, string? id, CancellationToken ct)
    {
        if (!long.TryParse(id, out var eventId))
        {
            Console.Error.WriteLine("Нужен id события: --email-reprocess 123");
            return 2;
        }

        var captured = await sp.GetRequiredService<ReplyDraftSources>().CaptureAsync(eventId, ct);
        var ev = captured?.Event;
        if (ev?.ExternalId is null || ev.Channel != EventChannel.Email)
        {
            Console.Error.WriteLine("Нет доступного входящего e-mail с Message-ID, либо исходный ответ уже отправляется/отправлен");
            return 2;
        }

        var message = await sp.GetRequiredService<JobFinder.Integrations.Email.EmailSender>().FindAsync(ev.ExternalId, ct);
        if (message is null)
        {
            Console.Error.WriteLine("Письмо не найдено в ящике");
            return 1;
        }

        Console.WriteLine($"Текст письма теперь ({message.Text.Length} симв.):\n{TextUtil.Truncate(message.Text, 1500)}\n");
        var requests = sp.GetRequiredService<ReplyProcessingRequests>();
        try
        {
            var ticket = await requests.ReprocessAsync(captured!, new InboundReply
            {
                ApplicationId = ev.ApplicationId, Channel = ev.Channel, Author = message.FromAddress,
                Text = TextUtil.Truncate(message.Text, 6000), ExternalId = message.MessageId, At = message.Date, Subject = message.Subject,
            }, ct);
            Console.WriteLine($"Повторный анализ #{ticket.RequestId} сохранён; исходное событие #{eventId} не удалено.");
            await requests.WaitAsync(ticket.RequestId, ct);
            Console.WriteLine("Разобрано заново, новая карточка — в очереди бота.");
        }
        catch (ReplyProcessingSupersededException)
        {
            Console.Error.WriteLine("Письмо, диалог или данные кандидата изменились. История сохранена; запустите команду повторно.");
            return 1;
        }
        return 0;
    }

    private static async Task<int> HhChatReadAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        var vacancy = args.SkipWhile(a => a != "--hh-chat-read").Skip(2).FirstOrDefault();
        if (!long.TryParse(id, out var chatId) || !long.TryParse(vacancy, out var hhId))
        {
            Console.Error.WriteLine("Формат: --hh-chat-read <id чата> <номер вакансии hh>");
            return 2;
        }

        var messages = await sp.GetRequiredService<HhChat>().ReadAsync(chatId, hhId, ct);
        foreach (var m in messages)
            Console.WriteLine($"[{(m.Mine ? "я" : m.Author ?? "работодатель")}{(m.Time is null ? "" : " " + m.Time)}] {TextUtil.Truncate(m.Text, 300)}");
        Console.WriteLine($"{messages.Count} сообщений. Ничего не отправлено.");
        return 0;
    }

    private static async Task<int> HhChatCheckAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        if (!long.TryParse(id, out var hhId))
        {
            Console.Error.WriteLine("Нужен номер вакансии hh: --hh-chat-check 137710779");
            return 2;
        }

        var probe = args.SkipWhile(a => a != "--probe-text").Skip(1).FirstOrDefault();
        Console.WriteLine(await sp.GetRequiredService<HhApplier>().InspectChatAsync(hhId, $"data/debug/hh-{hhId}-chat.json", ct, probe));
        Console.WriteLine("Ничего не набрано и не отправлено.");
        return 0;
    }

    /// <summary>One message into the hh chat of a response: the vacancy's cover letter (as the response's letter) or a given text.</summary>
    private static async Task<int> HhChatSendAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        if (!long.TryParse(id, out var hhId))
        {
            Console.Error.WriteLine("Нужен номер вакансии hh");
            return 2;
        }

        await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        var chatId = await ctx.HhResponses.Where(r => r.HhVacancyId == hhId).Select(r => r.ChatId).FirstOrDefaultAsync(ct);
        if (chatId is null)
        {
            Console.Error.WriteLine("Нет чата: отклика нет в синхронизированной истории");
            return 2;
        }

        var asLetter = args.Contains("--letter");
        string? text = args.SkipWhile(a => a != "--text").Skip(1).FirstOrDefault();
        if (asLetter)
        {
            var external = hhId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            text = await ctx.Vacancies.Where(v => v.Sources.Any(s => s.Source == SourceKind.HeadHunter && s.ExternalId == external))
                .Select(v => v.CoverLetter).FirstOrDefaultAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine("Нет текста: --letter (письмо вакансии) или --text \"…\"");
            return 2;
        }

        var sourceId = hhId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var vacancyId = await ctx.VacancySources.Where(row => row.Source == SourceKind.HeadHunter && row.ExternalId == sourceId)
            .Select(row => (long?)row.VacancyId).SingleOrDefaultAsync(ct);
        if (vacancyId is null) { Console.Error.WriteLine("Вакансия не найдена"); return 2; }
        var result = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.EmployerMessages>().HhAsync(vacancyId.Value, text, asLetter, ct);
        Console.WriteLine($"{(result.Ok ? "✓" : "✗")} {result.Message}");
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> ReplyCardAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        var draft = args.SkipWhile(a => a != "--draft").Skip(1).FirstOrDefault();
        if (!long.TryParse(id, out var eventId) || string.IsNullOrWhiteSpace(draft))
        {
            Console.Error.WriteLine("Формат: --reply-card <id события> --draft \"текст\"");
            return 2;
        }

        var sources = sp.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.ReplyDraftSources>();
        var captured = await sources.CaptureAsync(eventId, ct);
        if (captured is null)
        {
            Console.Error.WriteLine("Нет редактируемого события или требуется сверка отправки");
            return 3;
        }
        var analysis = new JobFinder.Llm.Tasks.ReplyAnalysis
        {
            Classification = "question", Summary = "черновик ответа переписан", Draft = HumanText.Clean(draft.Trim()),
        };
        var result = await sources.PublishAsync(captured.Snapshot, analysis,
            JobFinder.Core.Filtering.ChatStyle.Problems(analysis.Draft, considered: true), ct);
        if (!result.Ok) { Console.Error.WriteLine(result.Message); return 3; }
        Console.WriteLine($"Черновик сохранён, карточка — в очереди бота:\n{analysis.Draft}");
        return 0;
    }

    private static async Task<int> ReplyRedraftAsync(IServiceProvider sp, string? id, bool preview, CancellationToken ct)
    {
        if (!long.TryParse(id, out var eventId))
        {
            Console.Error.WriteLine("Формат: --reply-redraft <id события>");
            return 2;
        }

        var result = await sp.GetRequiredService<JobFinder.Pipeline.Career.ReplyService>().RedraftAsync(eventId, ct, preview);
        if (preview)
        {
            Console.WriteLine(result.Message);
            return result.Ok ? 0 : 1;
        }

        await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        var draft = await ctx.ApplicationEvents.Where(e => e.Id == eventId).Select(e => e.Draft).FirstOrDefaultAsync(ct);
        Console.WriteLine($"{result.Message}\n\n{draft}");
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> MessageReconcileAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        var evidence = args.SkipWhile(argument => argument != "--evidence").Skip(1).FirstOrDefault();
        var sent = args.Contains("--sent");
        if (!Guid.TryParse(id, out var messageId) || sent == args.Contains("--not-sent") || string.IsNullOrWhiteSpace(evidence))
        {
            Console.Error.WriteLine("Нужны GUID операции, один из --sent/--not-sent и --evidence с результатом сверки");
            return 2;
        }
        var recorded = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.DeliveryLedger>().ReconcileMessageAsync(messageId, sent, evidence, ct);
        Console.WriteLine(recorded ? "Сверка записана. Ничего не отправлялось." : "Операция не ждёт сверки или уже сверена.");
        return recorded ? 0 : 3;
    }

    private static async Task<int> ApplicationReconcileAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        var evidence = args.SkipWhile(argument => argument != "--evidence").Skip(1).FirstOrDefault();
        var sent = args.Contains("--sent");
        if (!Guid.TryParse(id, out var requestId) || sent == args.Contains("--not-sent") || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000)
        {
            Console.Error.WriteLine("Нужны GUID запроса, один из --sent/--not-sent и --evidence с результатом сверки (до 2000 символов)");
            return 2;
        }
        var recorded = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.DeliveryLedger>().ReconcileApplicationAsync(requestId, sent, evidence, ct);
        Console.WriteLine(recorded ? "Сверка записана. Ничего не отправлялось; исходное письмо/PDF не сертифицированы как доставленные."
            : "Запрос не ждёт сверки, уже сверён или его исходная попытка изменилась.");
        return recorded ? 0 : 3;
    }

    private static async Task<int> ReplyProcessAsync(IServiceProvider sp, string? id, bool retry, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var requestId) || requestId == Guid.Empty)
        {
            Console.Error.WriteLine("--reply-process <guid> [--retry]");
            return 2;
        }
        if (retry)
        {
            var ticket = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.ReplyProcessingRequests>().RetryAsync(requestId, ct);
            Console.WriteLine(ticket.AlreadyPublished ? "Входящее уже опубликовано; повтор не поставлен."
                : $"Анализ в очереди: {ticket.RequestId}. Дальнейшие действия определяет политика; CLI не отправляет сообщения.");
            return 0;
        }
        await using var context = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        var request = await context.ReplyProcessingRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == requestId, ct);
        if (request is null) return 3;
        Console.WriteLine($"{request.Id}: {request.Mode}/{request.Stage}; application={request.ApplicationId}; event={request.EventId}; deep={request.DeepAttempt}; retry={request.RetryAt:O}; {request.Error}");
        return 0;
    }

    private static async Task<int> BotUpdateReconcileAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        var evidence = args.SkipWhile(argument => argument != "--evidence").Skip(1).FirstOrDefault();
        if (!Guid.TryParse(id, out var requestId) || requestId == Guid.Empty || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000)
        {
            Console.Error.WriteLine("Нужны GUID сохранённой команды и --evidence с проверенными сведениями (до 2000 символов)");
            return 2;
        }
        var recorded = await sp.GetRequiredService<JobFinder.Pipeline.Controls.BotControls>().ReconcileAsync(requestId, evidence, ct);
        Console.WriteLine(recorded ? "Проверка записана без повторной обработки команды или подтверждения доставки." : "Команда не требует сверки; изменений нет.");
        return recorded ? 0 : 3;
    }

    private static async Task<int> NotificationReconcileAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        var evidence = args.SkipWhile(argument => argument != "--evidence").Skip(1).FirstOrDefault();
        var sent = args.Contains("--sent");
        if (!Guid.TryParse(id, out var requestId) || requestId == Guid.Empty || sent == args.Contains("--not-sent")
            || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000)
        {
            Console.Error.WriteLine("Нужны GUID уведомления, один из --sent/--not-sent и --evidence с результатом сверки (до 2000 символов)");
            return 2;
        }
        await sp.GetRequiredService<JobFinder.Pipeline.Notifications.NotificationDeliveryLedger>().ReconcileAsync(requestId, sent, evidence, ct);
        Console.WriteLine("Сверка уведомления записана; отправка не выполнялась и доставка PDF не подтверждена.");
        return 0;
    }

    private static async Task<int> ChannelMembershipReconcileAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        var evidence = args.SkipWhile(argument => argument != "--evidence").Skip(1).FirstOrDefault();
        var attempt = args.SkipWhile(argument => argument != "--attempt").Skip(1).FirstOrDefault();
        var applied = args.Contains("--applied");
        if (!Guid.TryParse(id, out var requestId) || requestId == Guid.Empty || !Guid.TryParse(attempt, out var attemptId) || attemptId == Guid.Empty
            || applied == args.Contains("--not-applied") || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000)
        {
            Console.Error.WriteLine("Нужны точные GUID операции и --attempt, один из --applied/--not-applied и проверенное --evidence (до 2000 символов)");
            return 2;
        }
        var result = await sp.GetRequiredService<ChannelMembershipLedger>().ReconcileAsync(requestId, attemptId, applied, evidence, ct);
        Console.WriteLine(result.Message);
        return result.Ok ? 0 : 3;
    }

    private static async Task<int> CommentReconcileAsync(IServiceProvider sp, string? id, string[] args, CancellationToken ct)
    {
        var evidence = args.SkipWhile(argument => argument != "--evidence").Skip(1).FirstOrDefault();
        var sent = args.Contains("--sent");
        if (!Guid.TryParse(id, out var requestId) || sent == args.Contains("--not-sent") || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000)
        {
            Console.Error.WriteLine("Нужны GUID запроса, один из --sent/--not-sent и --evidence с результатом сверки (до 2000 символов)");
            return 2;
        }
        var result = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.CommentDeliveryLedger>().ReconcileAsync(requestId, sent, evidence, ct);
        Console.WriteLine(result.Message);
        return result.Ok ? 0 : 3;
    }

    private static async Task<int> HhAddLetterAsync(IServiceProvider sp, string? id, CancellationToken ct)
    {
        if (!long.TryParse(id, out var vacancyId))
        {
            Console.Error.WriteLine("Нужен id вакансии в JobFinder: --hh-add-letter 50");
            return 2;
        }
        var result = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Hiring.EmployerMessages>().HhAsync(vacancyId, null, true, ct);
        Console.WriteLine($"{(result.Ok ? "✓" : "✗")} {result.Message}");
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> HhApplyCheckAsync(IServiceProvider sp, string? id, CancellationToken ct)
    {
        if (!long.TryParse(id, out var vacancyId))
        {
            Console.Error.WriteLine("Нужен номер вакансии hh: --hh-apply-check 135602805");
            return 2;
        }

        var applier = sp.GetRequiredService<HhApplier>();
        if (!await applier.HasSessionAsync(ct))
        {
            Console.Error.WriteLine("Нет сессии hh — --login hh");
            return 2;
        }

        Console.WriteLine(await applier.InspectAsync(vacancyId, "data/debug", ct));
        Console.WriteLine("Ничего не нажато и не отправлено.");
        return 0;
    }

    private static async Task<int> ThreadsReplyVerifyAsync(IServiceProvider sp, string? id, CancellationToken ct)
    {
        await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        var draft = long.TryParse(id, out var draftId) ? await ctx.CommentDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct) : null;
        if (draft?.ChosenText is null)
        {
            Console.Error.WriteLine("Нужен черновик с выбранным текстом: --threads-reply-verify 79");
            return 2;
        }

        var text = HumanText.Clean(draft.ChosenText);
        var found = await sp.GetRequiredService<ThreadsBrowserClient>().HasReplyAsync(draft.PostUrl, text, ct);
        Console.WriteLine($"#c{draft.Id} ({draft.Status}) {draft.PostUrl}: {(found ? "✓ собственный полный комментарий под точным постом" : "✗ публикация не подтверждена")} — «{text}»");
        return found ? 0 : 1;
    }

    private static async Task<int> ThreadsReplyCheckAsync(IServiceProvider sp, string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("Нужна ссылка на пост: --threads-reply-check https://www.threads.com/@user/post/CODE");
            return 2;
        }

        const string dump = "data/debug/threads-post-controls.json";
        Console.WriteLine(await sp.GetRequiredService<ThreadsBrowserClient>().CheckReplyAsync(url, dump, ct));
        Console.WriteLine($"Элементы страницы: {dump}; скриншот — data/screenshots. Ничего не опубликовано.");
        return 0;
    }

    /// <summary>One link read as a post's link is: pages (depth, rendered, route), what they say, the contacts found. Nothing is stored.</summary>
    private static async Task<int> ReadLinkAsync(IServiceProvider sp, string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("Нужна ссылка: --read-link https://telegra.ph/...");
            return 2;
        }

        var post = new RawPost { Source = SourceKind.Telegram, ChatRef = "cli", ExternalId = "cli", Text = url, Links = [new PostLink { Url = url }] };
        var enricher = sp.GetRequiredService<JobFinder.Pipeline.Ingest.PostEnricher>();
        var enriched = await enricher.EnrichAsync(post, JobFinder.Pipeline.Ingest.PostEnricher.LinksOf(post), ct);
        foreach (var p in enriched.Pages)
        {
            Console.WriteLine($"[{p.Depth}] {p.Page.Url}{(p.Rendered ? " · браузер" : "")}{(p.ViaProxy ? " · через прокси" : "")}{(p.Via is null ? "" : $" · по ссылке «{p.Via}»")}");
            Console.WriteLine($"    {p.Page.Title} — {p.Page.Text.Length} симв.: {TextUtil.Truncate(p.Page.Text.Replace('\n', ' '), 200)}");
            if (p.Page.JobPosting is { } j)
                Console.WriteLine($"    JobPosting: {j.Title} · {j.Company} · {j.SalaryFrom}–{j.SalaryTo} {j.Currency}{(j.Remote ? " · удалённо" : "")}");
        }

        Console.WriteLine(enriched.Contacts.Count == 0 ? "Контактов нет" : "Контакты:");
        foreach (var c in enriched.Contacts) Console.WriteLine($"    {c.Kind}: {c}{(c.Source is null ? "" : " — " + c.Source)}");
        foreach (var f in enriched.Failures) Console.WriteLine($"✗ {f}");
        return enriched.Pages.Count > 0 ? 0 : 1;
    }

    private static async Task<int> ThreadsSearchAsync(IServiceProvider sp, string? query, CancellationToken ct)
    {
        var web = sp.GetRequiredService<ThreadsBrowserClient>();
        if (!await web.HasSessionAsync(ct))
        {
            Console.Error.WriteLine("Нет сессии Threads — --login threads");
            return 2;
        }

        const string diagnose = "data/debug/threads-search.json";
        var posts = await web.SearchRecentAsync(string.IsNullOrWhiteSpace(query) ? "ищем .NET разработчика" : query, 25, ct, diagnose);
        Console.WriteLine($"Постов: {posts.Count} · структура страницы: {diagnose}");
        foreach (var p in posts)
            Console.WriteLine($"  @{p.Username} {p.PublishedAt:dd.MM HH:mm} {p.Url}\n      {TextUtil.Truncate(p.Text.Replace('\n', ' '), 160)}");
        return posts.Count > 0 ? 0 : 1;
    }

    /// <summary>The response letter the vacancy would get (its route: hh, e-mail, a Telegram message), printed with its evidence and problems.</summary>
    private static async Task<int> LettersRefreshAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var ids = args.SkipWhile(a => a != "--letters-refresh").Skip(1)
            .Select(a => long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToList();
        var bot = sp.GetRequiredService<TelegramBotClientHolder>().Client ?? throw new InvalidOperationException("Bot:Token пуст");
        var owner = sp.GetRequiredService<BotOptions>().OwnerUserId;
        var db = sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>();
        var emailReady = sp.GetRequiredService<JobFinder.Integrations.Email.EmailSender>().IsConfigured;
        // The card the user already has shows the new letter: the same message edited, no new one in the chat.
        async Task RecardAsync(long id, IReadOnlyList<string> problems)
        {
            await using var ctx = await db.CreateDbContextAsync(ct);
            var v = await ctx.Vacancies.AsNoTracking().Include(x => x.Sources).FirstAsync(x => x.Id == id, ct);
            var (text, buttons) = JobFinder.Pipeline.Notifications.Cards.VacancyApproval(v, JobFinder.Pipeline.Workers.ApplyRoutes.Resolve(v, emailReady),
                letterProblems: problems);
            var messageId = await ctx.Notifications.Where(n => n.Kind == NotificationKind.VacancyApproval && n.EntityId == id && n.TelegramMessageId != null)
                .OrderByDescending(n => n.Id).Select(n => n.TelegramMessageId).FirstOrDefaultAsync(ct);
            // A vacancy without a card (queued past the hourly card limit) gets none now either: dozens of cards at once is a flood. Its
            // new letter is on the dashboard and goes out on approval.
            if (messageId is null)
            {
                Console.WriteLine($"#{id}: карточки нет — новое письмо на дашборде");
                return;
            }

            try
            {
                await bot.EditMessageText(owner, messageId.Value, text, Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: NotificationDispatcher.Keyboard(buttons),
                    linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct);
                Console.WriteLine($"#{id}: карточка {messageId} обновлена");
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException e)
            {
                Console.WriteLine($"#{id}: карточку {messageId} не обновить ({e.Message}) — новое письмо на дашборде");
            }
        }

        var done = await sp.GetRequiredService<JobFinder.Pipeline.Actions.ApprovalService>().RefreshLettersAsync(ids, days: 7, ct, RecardAsync);
        foreach (var (id, result) in done) Console.WriteLine($"#{id}: {result}");
        Console.WriteLine(done.Count == 0 ? "Ждущих решения вакансий с письмом нет." : $"Переписано писем: {done.Count}");
        return 0;
    }

    private static async Task<int> LetterAsync(IServiceProvider sp, string? argument, CancellationToken ct)
    {
        if (!long.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vacancyId))
        {
            Console.Error.WriteLine("Нужен id вакансии: --letter 76");
            return 2;
        }

        await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.AsNoTracking().Include(x => x.Sources).Include(x => x.Company).FirstOrDefaultAsync(x => x.Id == vacancyId, ct);
        if (v is null)
        {
            Console.Error.WriteLine($"Вакансии #{vacancyId} нет");
            return 1;
        }

        var route = JobFinder.Pipeline.Workers.ApplyRoutes.Resolve(v, sp.GetRequiredService<JobFinder.Integrations.Email.EmailSender>().IsConfigured);
        var letter = await sp.GetRequiredService<LetterWriter>().ResponseAsync(v, route.Channel ?? ApplicationChannel.HeadHunter, route.Target, ct);
        if (letter is null)
        {
            Console.Error.WriteLine("Нет профиля");
            return 1;
        }

        Console.WriteLine($"#{v.Id} {v.Title} — {v.CompanyName} · {route.Channel ?? ApplicationChannel.HeadHunter} · {letter.Text.Length} символов\n{letter.Text}");
        foreach (var e in letter.Evidence) Console.WriteLine($"{(e.Found ? "  ✔" : "  ⚠ нет в профиле:")} «{e.Quote}»");
        if (letter.Problems.Count > 0) Console.WriteLine("Не прошло проверку: " + string.Join("; ", letter.Problems));
        return 0;
    }

    /// <summary>What the dashboard's «✨ Черновик» would write, printed: Telegram is not touched (only the server owns the session).</summary>
    private static async Task<int> RecruiterDraftAsync(IServiceProvider sp, string? argument, bool? fresh, CancellationToken ct)
    {
        if (!long.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vacancyId))
        {
            Console.Error.WriteLine("Нужен id вакансии: --recruiter-draft 14");
            return 2;
        }

        var db = sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>();
        await using var ctx = await db.CreateDbContextAsync(ct);
        var company = await ctx.Vacancies.Where(v => v.Id == vacancyId).Select(v => v.CompanyName).FirstOrDefaultAsync(ct);
        var contact = (await sp.GetRequiredService<ContactRegistry>().ForCompanyAsync(company, ct)).FirstOrDefault(c => c.Kind == ContactKind.Telegram);
        if (contact is null)
        {
            Console.Error.WriteLine($"У компании вакансии #{vacancyId} нет Telegram-контакта");
            return 1;
        }

        var (draft, error) = await sp.GetRequiredService<RecruiterChatService>().DraftAsync(vacancyId, contact.Id, fresh, ct);
        if (draft is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        Console.WriteLine($"Кому: {contact.Value} {contact.PersonName} · " +
                          (draft.Fresh ? RecruiterChatService.FreshLabel : "ситуация: " + RecruiterChatService.Label(draft.Situation)) +
                          $" · резюме: {(draft.AttachResume ? "да" : "нет")}");
        Console.WriteLine($"({draft.Text.Length} символов)\n{draft.Text}");
        foreach (var e in draft.Evidence) Console.WriteLine($"{(e.Found ? "  ✔" : "  ⚠ нет в профиле:")} «{e.Quote}»");
        if (draft.Problems.Count > 0) Console.WriteLine("Не прошёл проверку: " + string.Join("; ", draft.Problems));
        return 0;
    }

    private static async Task<int> AddVacancyAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var queue = sp.GetRequiredService<JobFinder.Pipeline.Ingest.HhLinkQueue>();
        var added = await queue.AddAsync(string.Join(' ', args), ct);
        if (added.Ids.Count == 0)
        {
            Console.Error.WriteLine("Нужны ссылки вида https://hh.ru/vacancy/123456");
            return 2;
        }

        Console.WriteLine($"В очереди: {string.Join(", ", (await queue.PendingAsync(ct)).Select(link => link.Id))}. " + (added.PausedUntil is { } until
            ? $"Сессии hh нет, анонимный доступ на паузе до {TimeZoneInfo.ConvertTime(until, sp.GetRequiredService<JobFinder.Core.Time.LocalClock>().Zone):HH:mm}: сервер прочитает их после неё."
            : "Сервер обработает сохранённую очередь (под аккаунтом, если есть сессия); итог придёт в бот."));
        return 0;
    }

    private static async Task<int> ScoreAsync(IServiceProvider sp, string? url, CancellationToken ct)
    {
        if (!HhClient.TryParseVacancyUrl(url, out var host, out var id))
        {
            Console.Error.WriteLine("Нужна ссылка вида https://hh.ru/vacancy/123456");
            return 2;
        }

        var profile = await sp.GetRequiredService<ProfileProvider>().GetAsync(ct);
        if (profile is null)
        {
            Console.Error.WriteLine("Нет профиля кандидата — сначала --import-resume");
            return 2;
        }

        var detail = await sp.GetRequiredService<HhClient>().GetVacancyAsync(host, id, ct);
        var v = new Vacancy
        {
            Title = detail.Name,
            CompanyName = detail.CompanyName,
            Description = detail.Description,
            SalaryFrom = detail.SalaryFrom,
            SalaryTo = detail.SalaryTo,
            Currency = detail.Currency,
            SalaryGross = detail.SalaryGross,
            City = detail.AreaName,
            CountryCode = detail.CountryCode,
            WorkFormats = detail.Formats,
            EnglishRequired = detail.English,
            Skills = detail.KeySkills,
            Experience = detail.Experience,
            Url = detail.Url,
            Fingerprint = "",
        };
        var assessments = sp.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.VacancyAssessments>();
        var requestId = await assessments.RequestPreviewAsync(AssessmentVacancy.From(v), ct);
        Console.WriteLine($"Превью сохранено: {requestId}; продолжить без чтения hh: --score-preview {requestId} (Ctrl+C прекращает ожидание, не запрос).");
        return await ScorePreviewResultAsync(sp, requestId, ct);
    }

    private static async Task<int> ScorePreviewResultAsync(IServiceProvider sp, Guid requestId, CancellationToken ct)
    {
        var assessments = sp.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.VacancyAssessments>();
        var (captured, score) = await assessments.WaitPreviewAsync(requestId, ct);
        var v = captured.Vacancy.ToVacancy();
        var filter = new JobFinder.Pipeline.Modules.Discovery.SearchPolicySnapshot(captured.Search, captured.SearchVersion).Filter().Check(v, alreadyApplied: false);
        Console.WriteLine($"{v.Title} — {v.CompanyName} ({v.City}, {v.CountryCode}, {v.WorkFormats}, англ. {v.EnglishRequired})");
        Console.WriteLine(filter.Passed ? "Жёсткий фильтр: пройден" : $"Жёсткий фильтр: ОТКАЗ — {filter.Reason}");
        var s = score.Evaluation!;
        Console.WriteLine($"LLM ({s.Model}, ${score.CostUsd:0.####}): {s.Score} (стек {s.StackMatch}, грейд {s.SeniorityMatch}, домен {s.DomainMatch}, формат {s.FormatMatch}), англ. {s.EnglishRequired}");
        Console.WriteLine($"Зарплата: {captured.SalaryNote}");
        Console.WriteLine($"Итог: {score.Score}");
        Console.WriteLine($"Резюме: {s.Summary}");
        Console.WriteLine($"+ {string.Join("; ", s.Strengths)}");
        Console.WriteLine($"- {string.Join("; ", s.Gaps)}");
        if (s.RedFlags.Count > 0) Console.WriteLine($"! {string.Join("; ", s.RedFlags)}");
        v.Evaluation = s;
        if (!captured.Profile.Reviewed || captured.FactsVersion == 0)
        {
            Console.WriteLine("Письмо не подготовлено: нужны проверенный профиль и сохранённые факты. Оценка показана для захваченного исходного профиля; отправки нет.");
            return 0;
        }
        var operation = sp.GetRequiredService<JobFinderLlm>().RestoreOperation(captured.Operation);
        var letter = await sp.GetRequiredService<LetterWriter>().ResponseAsync(v, ApplicationChannel.HeadHunter, null, captured.Profile, captured.Facts, operation, ct);
        Console.WriteLine($"\nПисьмо ({letter?.Text.Length} символов):\n{letter?.Text}");
        foreach (var e in letter?.Evidence ?? []) Console.WriteLine($"{(e.Found ? "  ✔" : "  ⚠ нет в профиле:")} «{e.Quote}»");
        if (letter?.Problems is { Count: > 0 } problems) Console.WriteLine("Не прошло проверку: " + string.Join("; ", problems));
        return 0;
    }
}
