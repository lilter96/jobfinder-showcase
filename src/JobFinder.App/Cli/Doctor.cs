using JobFinder.Bot;
using JobFinder.Data;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.LinkedIn;
using JobFinder.Integrations.Speech;
using JobFinder.Integrations.Telegram;
using JobFinder.Integrations.Threads;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;

namespace JobFinder.App.Cli;

/// <summary>Every external dependency, one line each: what works, what does not, and the command that fixes it.</summary>
public static class Doctor
{
    public static async Task<int> RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var failures = 0;

        await Check("prompts", () =>
        {
            sp.GetRequiredService<PromptCatalog>();
            return Task.FromResult($"{PromptCatalog.Required.Count} files");
        });

        await Check("profile", async () =>
        {
            var p = await sp.GetRequiredService<ProfileProvider>().GetAsync(ct)
                    ?? throw new InvalidOperationException("нет профиля кандидата в БД — положите резюме в profile/ и выполните --import-resume");
            return $"{p.Name}, {p.Title}, {p.Skills.Count} навыков, ЗП {p.DesiredSalary?.Amount} {p.DesiredSalary?.Currency}{(p.Reviewed ? "" : " — НЕ ПРОВЕРЕН (дашборд → Настройки)")}";
        });

        // Everything durable is in the database; a file of the old layout still on disk means the import has not run (the server does it at start).
        await Check("state (database)", async () =>
        {
            await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
            var documents = await ctx.Documents.AsNoTracking().OrderBy(d => d.Key).Select(d => new { d.Key, d.Version, d.UpdatedAt }).ToListAsync(ct);
            var session = await ctx.TelegramSessions.AsNoTracking().Where(s => s.Name == JobFinder.Pipeline.Storage.PgTelegramSessionStore.Name)
                .Select(s => (DateTimeOffset?)s.UpdatedAt).FirstOrDefaultAsync(ct);
            var keys = await ctx.DataProtectionKeys.CountAsync(ct);
            var home = Directory.GetCurrentDirectory();
            string[] legacy = ["profile/profile.json", "profile/facts.json", "data/telegram.session", "data/telegram.updates.json"];
            var left = legacy.Where(f => File.Exists(Path.Combine(home, f)))
                .Concat(Directory.Exists("data/browser") ? Directory.EnumerateFiles("data/browser", "*.json").Select(f => f.Replace('\\', '/')) : [])
                .ToList();
            if (left.Count > 0) throw new InvalidOperationException($"файлы старой раскладки не перенесены: {string.Join(", ", left)} — --migrate или старт сервера");
            return string.Join(", ", documents.Select(d => $"{d.Key} v{d.Version}")) + $"; сессия Telegram {(session is null ? "нет" : "есть")}; ключей дашборда {keys}";
        });

        await Check("database", async () =>
        {
            await using var ctx = await sp.GetRequiredService<IDbContextFactory<JobFinderDbContext>>().CreateDbContextAsync(ct);
            var pending = (await ctx.Database.GetPendingMigrationsAsync(ct)).Count();
            var vacancies = await ctx.Vacancies.CountAsync(ct);
            return $"ok, вакансий {vacancies}, неприменённых миграций {pending}";
        });

        await Check("llm", async () =>
        {
            var result = await sp.GetRequiredService<JobFinderLlm>().ExtractPostAsync(
                "Ищем Senior .NET разработчика в продуктовую команду, Минск, гибрид. C#, ASP.NET Core, PostgreSQL, Kafka. ЗП 4000–5000$ net. Писать @hr_anna",
                "doctor", ct);
            var x = result.Value;
            return $"{result.Model}: isVacancy={x.IsVacancy}, title=\"{x.Title}\", {x.SalaryFrom}-{x.SalaryTo} {x.Currency}, ${result.CostUsd:0.#####}";
        });

        await Check("hh.ru", async () =>
        {
            var policy = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.SearchPolicyStore>().ReadAsync(ct);
            var query = policy.Value.HeadHunter.Searches.FirstOrDefault() ?? throw new InvalidOperationException("policy.search.headHunter.searches пуст");
            var search = JobFinder.Pipeline.Modules.Discovery.SearchPolicyCompiler.ToRequest(query);
            var (items, total) = await sp.GetRequiredService<HhClient>().SearchAsync(search, 0, ct);
            return $"\"{search.Name}\" на {search.Host}: {items.Count} на странице, всего {total}";
        });

        await Check("habr", async () =>
        {
            var policy = await sp.GetRequiredService<JobFinder.Pipeline.Modules.Discovery.SearchPolicyStore>().ReadAsync(ct);
            var query = policy.Value.Habr.Queries.FirstOrDefault() ?? throw new InvalidOperationException("policy.search.habr.queries пуст");
            var (items, pages) = await sp.GetRequiredService<HabrClient>().SearchAsync(query, policy.Value.Habr.Filters.FirstOrDefault() ?? "", 1, ct);
            return $"{items.Count} на странице, страниц {pages}";
        });

        // Presence only: connecting a second client on the same auth key while the server runs risks AUTH_KEY_DUPLICATED,
        // which kills the session. The server logs "[telegram] connected as …" (and the bot's /status shows it).
        await Check("telegram (MTProto)", async () =>
        {
            var options = sp.GetRequiredService<TelegramOptions>();
            if (options.ApiId == 0) throw new InvalidOperationException("Telegram:ApiId не задан");
            if (!await sp.GetRequiredService<TelegramUserClient>().HasSessionAsync(ct)) throw new InvalidOperationException("нет сессии — выполните --login-telegram");
            return "сессия есть (в БД); подключение — в логе сервера и в /status";
        });

        await Check("bot", async () =>
        {
            var bot = sp.GetRequiredService<TelegramBotClientHolder>().Client ?? throw new InvalidOperationException("Bot:Token пуст");
            var me = await bot.GetMe(ct);
            var owner = sp.GetRequiredService<BotOptions>().OwnerUserId;
            return $"@{me.Username}, владелец {(owner == 0 ? "НЕ ЗАДАН (Bot:OwnerUserId)" : owner.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
        });

        await Check("email (SMTP)", async () =>
        {
            var email = sp.GetRequiredService<JobFinder.Integrations.Email.EmailSender>();
            if (!email.IsConfigured) throw new InvalidOperationException("не настроена: Email:Username + Email:Password (пароль приложения Gmail)");
            await email.CheckAsync(ct);
            return "вход на SMTP и IMAP успешен (письмо не отправлялось)";
        });

        await Check("resume file", () =>
        {
            var path = sp.GetRequiredService<ProfileProvider>().FindResume()
                       ?? throw new InvalidOperationException("нет profile/resume.pdf — ЛС и письма уходят только с ним");
            return Task.FromResult($"{Path.GetFileName(path)}, {new FileInfo(path).Length / 1024} КБ — прикладывается к ЛС и письмам");
        });

        await Check("hh session (browser)", async () =>
        {
            var applier = sp.GetRequiredService<HhApplier>();
            if (!await applier.HasSessionAsync(ct)) throw new InvalidOperationException("нет сохранённой сессии — выполните --login hh");
            return await applier.CheckSessionAsync(ct) ? "залогинен как соискатель" : throw new InvalidOperationException("сессия протухла — --login hh");
        });

        await Check("threads", async () =>
        {
            var gateway = sp.GetRequiredService<ThreadsGateway>();
            if (!await gateway.IsConfiguredAsync(ct)) throw new InvalidOperationException("нет Threads:AccessToken и нет браузерной сессии — --login threads");
            return gateway.UsesApi ? "официальный API" : "браузерная сессия";
        });

        await Check("speech (Deepgram)", async () =>
        {
            var stt = sp.GetRequiredService<DeepgramStt>();
            if (!stt.IsConfigured) throw new InvalidOperationException("нет Stt:ApiKey — голосовые в боте не распознаются");
            using var silence = new MemoryStream(SilentWav(TimeSpan.FromMilliseconds(500)));
            var text = await stt.TranscribeAsync(silence, "audio/wav", ct);
            return $"ключ принят (тишина → {(text.Length == 0 ? "пустая расшифровка, как и должно быть" : "«" + text + "»")})";
        });

        await Check("linkedin", async () =>
        {
            var options = sp.GetRequiredService<LinkedInOptions>();
            if (!options.Enabled) return "выключен (LinkedIn:Enabled)";
            if (!await sp.GetRequiredService<LinkedInClient>().HasSessionAsync(ct)) throw new InvalidOperationException("нет сессии — --login linkedin (поиск контактов для аутрича)");
            return $"сессия есть, поиск ≤ {options.MaxSearchesPerDay}/день, сообщения не отправляются";
        });

        Console.WriteLine(failures == 0 ? "\nВсё в порядке." : $"\nПроблем: {failures}.");
        return failures == 0 ? 0 : 1;

        static byte[] SilentWav(TimeSpan length)
        {
            const int rate = 16000;
            var samples = (int)(rate * length.TotalSeconds);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8);
            w.Write(36 + samples * 2);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(samples * 2);
            w.Write(new byte[samples * 2]);
            w.Flush();
            return ms.ToArray();
        }

        async Task Check(string name, Func<Task<string>> probe)
        {
            try
            {
                var result = await probe();
                Console.WriteLine($"  ✓ {name,-22} {result}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures++;
                Console.WriteLine($"  ✗ {name,-22} {e.Message}");
            }
        }
    }
}
