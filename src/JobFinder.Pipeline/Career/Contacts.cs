using System.Text.RegularExpressions;
using JobFinder.Core.Contacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.LinkedIn;
using JobFinder.Integrations.Telegram;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

/// <param name="Vacancy">"title — company" of the vacancy it was linked through.</param>
public sealed record LinkedContact(long ContactId, ContactKind Kind, string Value, string Vacancy);

/// <summary>
/// People and addresses per company: from vacancy posts, vacancy descriptions (a recruiter's @username), hh contact persons, Telegram
/// search, LinkedIn search, and the user (a recruiter found by hand, linked to a vacancy). A company's Telegram contact that writes to the
/// account is taken as a message about the application at that company (<see cref="ReplyService.HandleTelegramAsync"/>).
/// </summary>
public sealed partial class ContactRegistry(IDbContextFactory<JobFinderDbContext> db, TelegramUserClient telegram, Ingest.TelegramContactVerifier verifier,
    ContactSources sources, ILogger<ContactRegistry> log)
{
    public const string ManualSource = ContactSources.ManualSource;
    public const string DescriptionSource = "описание";

    /// <summary>A contact the user found, for the vacancy's company ("@hr_anna", "t.me/hr_anna", an e-mail, a LinkedIn URL).</summary>
    /// <returns>The contact and the vacancy ("title — company"), or null when the vacancy or the contact is not recognized.</returns>
    public async Task<LinkedContact?> LinkAsync(long vacancyId, string raw, string? person, CancellationToken ct)
    {
        // A person only: a form or a bot link is not someone to write to.
        if (ContactExtractor.Classify(raw) is not { Kind: not ContactKind.Link } parsed) return null;
        await using var ctx = await db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vacancyId, ct);
        if (v is null || string.IsNullOrWhiteSpace(v.CompanyName)) return null;
        var name = person;
        if (parsed.Kind == ContactKind.Telegram && name is null && telegram.IsReady)
        {
            try
            {
                name = (await telegram.ResolvePersonAsync(parsed.Value))?.Name;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.Debug($"[contacts] resolve {parsed.Value}: {e.Message}");
            }
        }

        var id = await UpsertAsync(v.CompanyName, v.CompanyId, parsed.Kind, parsed.Value, name, "рекрутер (вы нашли)", ManualSource, v.Url, ct);
        return id is null ? null : new LinkedContact(id.Value, parsed.Kind, parsed.Value, $"{v.Title} — {v.CompanyName}");
    }

    /// <summary>A wrong or stale contact goes; one that already has a conversation sent from here stays (its replies are matched by it).</summary>
    public Task<ActionResult> RemoveAsync(ContactEditSnapshot snapshot, CancellationToken ct) => sources.RetireAsync(snapshot, ct);

    /// <summary>
    /// A recruiter's Telegram in the vacancy text ("пишите в Telegram @hr_anna", "t.me/hr_anna"): kept only when Telegram says it is a person
    /// (a company channel or a bot is not someone to write to); unchecked while Telegram is not connected.
    /// </summary>
    public async Task<int> FromDescriptionAsync(Vacancy v, CancellationToken ct)
    {
        var found = 0;
        foreach (var handle in TelegramHandles(v.Description).Take(3))
        {
            // A channel or a group named in the text is not someone to write to; a handle that cannot be checked now is kept, marked.
            var known = await verifier.WhatIsAsync(handle, ct);
            if (known is { Kind: not TelegramPeerKind.Person }) continue;
            var role = known is null ? "контакт из описания вакансии (не проверен)" : "контакт из описания вакансии";
            if (await UpsertAsync(v.CompanyName, v.CompanyId, ContactKind.Telegram, handle, known?.Name, role, DescriptionSource, v.Url, ct) is not null)
                found++;
        }

        return found;
    }

    /// <summary>Every Telegram username in a text (@name or t.me/name), without bots, invite links and repeats.</summary>
    public static IReadOnlyList<string> TelegramHandles(string? text) =>
        ContactExtractor.FromText(text).Where(c => c.Kind == ContactKind.Telegram).Select(c => c.Value).ToList();

    public async Task<List<CompanyContact>> ForCompanyAsync(string? companyName, CancellationToken ct)
    {
        var key = VacancyFingerprint.NormalizeCompany(companyName);
        if (key.Length == 0) return [];
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.CompanyContacts.AsNoTracking().Where(c => c.CompanyKey == key && c.RetiredAt == null)
            .OrderBy(c => c.Kind).ThenByDescending(c => c.LastSeenAt).ToListAsync(ct);
    }

    /// <returns>The contact's id (null when there is no company or value).</returns>
    public async Task<long?> UpsertAsync(string? companyName, long? companyId, ContactKind kind, string value, string? person, string? role, string source,
        string? sourceRef, CancellationToken ct)
    {
        return await sources.UpsertAsync(companyName, companyId, kind, value, person, role, source, sourceRef, ct);
    }

    /// <summary>The contacts a post lists ("@hr_anna", "hr@acme.io", "+375 29 …", a LinkedIn URL) under its company.</summary>
    /// <summary>The people of a vacancy post (and its pages): a way to reach that company later. Forms and bots are not people.</summary>
    public async Task FromPostAsync(string? company, long? companyId, IEnumerable<VacancyContact> contacts, string sourceRef, CancellationToken ct)
    {
        foreach (var c in contacts.Where(c => c.Kind is ContactKind.Telegram or ContactKind.Email or ContactKind.Phone or ContactKind.LinkedIn
                                             && !c.Value.StartsWith("tg://", StringComparison.Ordinal)))
            await UpsertAsync(company, companyId, c.Kind, c.Value, c.Label is "автор поста" ? null : c.Label, "рекрутер (из поста)", "пост",
                c.Source ?? sourceRef, ct);
    }

    internal Task FromPostAsync(JobFinderDbContext context, string? company, long? companyId, IEnumerable<VacancyContact> contacts, string sourceRef, CancellationToken ct) =>
        sources.PublishPostAsync(context, company, companyId, contacts, sourceRef, ct);

}

/// <summary>
/// "Who to write at the company" for strong applications: the best known contact (recruiter from posts, the hh contact person, a Telegram
/// search hit, a LinkedIn search), a short note, a card. Nothing is sent without the button; LinkedIn is never sent at all (link + text).
/// </summary>
public sealed partial class OutreachService(IDbContextFactory<JobFinderDbContext> db, ContactRegistry registry, JobFinderLlm llm, ProfileProvider profiles,
    TelegramUserClient telegram, LinkedInClient linkedIn, LinkedInPeopleReader linkedInPeople, LinkedInOptions linkedInOptions,
    EmployerMessages messages, RecruiterChatService chats, LetterWriter writer,
    TimeProvider time, ILogger<OutreachService> log,
    OutreachSources sources, FactsProvider facts)
{
    /// <summary>
    /// A draft to one particular contact (the recruiter the user linked or picked), written for where the response stands, as a bot card
    /// with send / own text / skip. The user asked for it: no once-a-week rule.
    /// </summary>
    /// <param name="fresh">From scratch (the earlier response not mentioned); null = as the situation suggests.</param>
    public async Task<ActionResult> DraftForContactAsync(long vacancyId, long contactId, bool? fresh, CancellationToken ct)
    {
        await facts.GetAsync(ct);
        var captured = await sources.CaptureAsync(vacancyId, contactId, automatic: false, ct);
        if (captured is null) return ActionResult.Fail("вакансия или контакт изменились, либо требуется сверка отправки");
        if (captured.Contact.Kind == ContactKind.LinkedIn)
        {
            try
            {
                var note = await PrepareLinkedInAsync(captured, ct);
                if (!ReadyLinkedIn(note)) return ActionResult.Fail("заметка LinkedIn не прошла проверку качества или длины");
                var checkedNote = new RecruiterDraft(note!.Text, false, RecruiterSituation.NotApplied, true, note.Evidence, note.Problems);
                return await sources.PublishAsync(captured.Snapshot, note.Text, checkedNote.Fresh, checkedNote, ct, note) is null
                    ? ActionResult.Fail("вакансия, контакт или данные кандидата изменились; черновик не опубликован")
                    : ActionResult.Done("черновик в боте");
            }
            catch (LetterPreparationSupersededException)
            {
                return ActionResult.Fail("данные изменились во время подготовки; черновик не опубликован");
            }
        }
        var (draft, error) = await chats.DraftAsync(captured.Vacancy, captured.Contact, fresh, ct);
        if (draft is null) return ActionResult.Fail(error ?? "черновик не получился");
        if (await sources.PublishAsync(captured.Snapshot, draft.Text, draft.Fresh, draft, ct) is null)
            return ActionResult.Fail("вакансия, контакт или данные кандидата изменились; черновик не опубликован");
        return ActionResult.Done("черновик в боте");
    }

    /// <summary>Who works at the company (Telegram search, then LinkedIn within its quota): into the contacts, no draft.</summary>
    public async Task<ActionResult> FindPeopleAsync(long vacancyId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vacancyId, ct);
        var key = VacancyFingerprint.NormalizeCompany(v?.CompanyName);
        if (v is null || key.Length == 0) return ActionResult.Fail("у вакансии нет компании");
        var before = await ctx.CompanyContacts.CountAsync(c => c.CompanyKey == key, ct);
        await DiscoverAsync(v, key, [], ct);
        await using var again = await db.CreateDbContextAsync(ct);
        var found = await again.CompanyContacts.CountAsync(c => c.CompanyKey == key, ct) - before;
        return found > 0 ? ActionResult.Done($"найдено контактов: {found}") : ActionResult.Fail("новых контактов не нашлось (Telegram и LinkedIn)");
    }

    private static readonly string[] RecruiterWords = ["hr", "recruit", "рекрут", "talent", "подбор", "hiring", "career", "карьер", "job", "ваканс"];

    /// <summary>Finds a contact for the vacancy's company and prepares an outreach draft (null when nobody suitable is known).</summary>
    public async Task<Outreach?> PrepareAsync(long vacancyId, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        if (profile is null) return null;
        await using var ctx = await db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.AsNoTracking().Include(x => x.Company).Include(x => x.Sources).FirstAsync(x => x.Id == vacancyId, ct);
        var key = VacancyFingerprint.NormalizeCompany(v.CompanyName);
        if (key.Length == 0) return null;

        // Once a week per company: outreach should read as a person's note, not a campaign.
        var weekAgo = time.GetUtcNow().AddDays(-7);
        if (await ctx.Outreaches.AnyAsync(o => o.VacancyId == vacancyId || (o.Contact!.CompanyKey == key && o.CreatedAt >= weekAgo), ct)) return null;

        // Whoever already got the application is not "someone else to write to".
        var targets = (await ctx.Applications.Where(a => a.VacancyId == vacancyId && a.Target != null).Select(a => a.Target!).ToListAsync(ct))
            .Select(t => t.Trim().TrimStart('@').ToLowerInvariant()).ToHashSet();
        var contact = await BestContactAsync(ctx, key, targets, ct) ?? await DiscoverAsync(v, key, targets, ct);
        if (contact is null) return null;
        await facts.GetAsync(ct);
        var captured = await sources.CaptureAsync(vacancyId, contact.Id, automatic: true, ct);
        if (captured is null) return null;
        v = captured.Vacancy;
        contact = captured.Contact;
        string text;
        bool? fresh = null;
        LetterDraft? reviewed = null;
        try
        {
            if (contact.Kind == ContactKind.Telegram)
            {
                var draft = (await chats.DraftAsync(v, contact, null, ct)).Draft;
                if (draft is null || draft.Problems.Count > 0) return null;
                text = draft.Text;
                fresh = draft.Fresh;
            }
            else if (contact.Kind == ContactKind.Email)
            {
                var draft = await writer.ResponseAsync(v, ApplicationChannel.Email, contact.Value, ct);
                if (draft is null || draft.Problems.Count > 0) return null;
                text = draft.Text;
            }
            else
            {
                reviewed = await PrepareLinkedInAsync(captured, ct);
                if (!ReadyLinkedIn(reviewed)) return null;
                text = reviewed!.Text;
            }
        }
        catch (LlmUnavailableException e)
        {
            log.Warn($"[outreach] #{vacancyId}: {e.Message}");
            return null;
        }
        catch (LetterPreparationSupersededException)
        {
            log.Warn($"[outreach] #{vacancyId}: captured preparation inputs changed");
            return null;
        }

        var outreach = await sources.PublishAsync(captured.Snapshot, text, fresh, null, ct, reviewed);
        if (outreach is null) return null;
        log.Info($"[outreach] #{vacancyId} ({v.CompanyName}): {contact.Kind} {contact.Value}");
        return outreach;
    }

    private async Task<LetterDraft?> PrepareLinkedInAsync(CapturedOutreachDraft captured, CancellationToken ct)
    {
        var contact = captured.Contact;
        var brief = new LetterBrief(captured.Vacancy, LetterChannel.LinkedIn, contact.PersonName ?? contact.Value,
            "Первое знакомство с сотрудником компании по конкретной вакансии; роль адресата: " + (contact.Role ?? "неизвестна"),
            false, "", First: true, NeedsFact: true, Deep: true);
        var operation = await llm.ForOperationAsync(ct);
        var draft = await writer.WriteAsync(brief, captured.Profile, captured.Facts, operation, ct);
        return draft is not null && LetterWriter.Matches(draft.Review, draft.Text,
            LetterWriter.ContextRecipe(operation, captured.Profile, captured.Facts, brief).Hash) ? draft : null;
    }

    private static bool ReadyLinkedIn(LetterDraft? draft) => draft is { Problems.Count: 0, Review: { } review }
        && draft.Text.Length <= LetterLayout.LinkedInLimit && LetterWriter.Matches(review, draft.Text, review.ContextHash);

    /// <param name="overrideText">The user's own text instead of the draft (the bot's "edit" reply).</param>
    public async Task<ActionResult> SendAsync(long outreachId, CancellationToken ct, string? overrideText = null, OutreachEditSnapshot? snapshot = null)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var source = await context.Outreaches.Include(row => row.Contact).SingleOrDefaultAsync(row => row.Id == outreachId, ct);
        if (source?.Contact is null || source.Status != OutreachStatus.Draft) return ActionResult.Fail("черновик уже обработан или не найден");
        var expected = snapshot ?? OutreachEditSnapshot.Capture(source, source.Contact);
        if (expected.Id != outreachId || !expected.Matches(source, source.Contact)) return ActionResult.Fail("черновик или контакт изменились; используйте новую карточку");
        if (source.Contact.Kind != ContactKind.LinkedIn) return await messages.OutreachAsync(outreachId, overrideText, ct, expected);
        return await sources.DecideAsync(expected, manualSent: true, overrideText, ct);
    }

    public async Task<ActionResult> DismissAsync(long outreachId, CancellationToken ct, OutreachEditSnapshot? snapshot = null)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var source = await ctx.Outreaches.AsNoTracking().Include(row => row.Contact).SingleOrDefaultAsync(row => row.Id == outreachId, ct);
        if (source?.Contact is null || source.Status != OutreachStatus.Draft) return ActionResult.Fail("черновик уже обработан или не найден");
        var expected = snapshot ?? OutreachEditSnapshot.Capture(source, source.Contact);
        if (expected.Id != outreachId) return ActionResult.Fail("неверный snapshot черновика");
        return await sources.DecideAsync(expected, manualSent: false, null, ct);
    }

    private static async Task<CompanyContact?> BestContactAsync(JobFinderDbContext ctx, string key, HashSet<string> exclude, CancellationToken ct)
    {
        var contacts = await ctx.CompanyContacts.Where(c => c.CompanyKey == key && c.Kind != ContactKind.Phone && c.RetiredAt == null).ToListAsync(ct);
        return contacts
            .Where(c => !exclude.Contains(c.Value.TrimStart('@').ToLowerInvariant()))
            .OrderBy(c => c.Kind switch { ContactKind.Telegram => 0, ContactKind.Email => 1, _ => 2 })
            // The one the user found beats every guess; then people named in the vacancy itself.
            .ThenByDescending(c => c.Source == ContactRegistry.ManualSource)
            .ThenByDescending(c => c.Source is "пост" or "hh" or ContactRegistry.DescriptionSource)
            .ThenByDescending(c => c.LastSeenAt)
            .FirstOrDefault();
    }

    /// <summary>Nobody known: Telegram search first (free, no limits worth caring about), LinkedIn second (daily quota).</summary>
    private async Task<CompanyContact?> DiscoverAsync(Vacancy v, string key, HashSet<string> exclude, CancellationToken ct)
    {
        var company = v.CompanyName ?? "";
        if (telegram.IsReady)
        {
            try
            {
                foreach (var hit in await telegram.SearchAsync(company, 20))
                {
                    var text = TextUtil.Normalize($"{hit.Name} {hit.Username}");
                    if (hit.IsChannel || hit.Username is null || !RecruiterWords.Any(text.Contains)) continue;
                    if (!TelegramAccountOf(company, text)) continue;
                    await registry.UpsertAsync(company, v.CompanyId, ContactKind.Telegram, hit.Username, hit.Name, "HR (по имени аккаунта)", "tg-search", null, ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Debug($"[outreach] telegram search {company}: {e.Message}");
            }
        }

        await using (var ctx = await db.CreateDbContextAsync(ct))
        {
            if (await BestContactAsync(ctx, key, exclude, ct) is { } found) return found;
        }

        if (!linkedInOptions.Enabled || !await linkedIn.HasSessionAsync(ct)) return null;
        try
        {
            // LinkedIn answers "{company} recruiter" with recruiters in general: the reader keeps people who work at this company now,
            // and outreach goes to the ones who hire.
            foreach (var p in (await linkedInPeople.FindAtCompanyAsync(company, ct)).Where(p => p.IsRecruiter).Take(3))
                await registry.UpsertAsync(company, v.CompanyId, ContactKind.LinkedIn, p.ProfileUrl, p.Name, p.Role ?? p.Headline, "linkedin", null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn($"[outreach] linkedin {company}: {e.Message}");
        }

        await using var again = await db.CreateDbContextAsync(ct);
        return await BestContactAsync(again, key, exclude, ct);
    }

    /// <summary>
    /// The company appears in the person's text (headline, "Текущая: … в Company") as whole words: its distinctive words, not the legal
    /// form or "group", "systems" — "EPAM Systems" is "epam", "ООО «Рога и Копыта»" is "рога" + "копыта".
    /// </summary>
    public static bool WorksAt(string company, string text)
    {
        var words = CompanyWords(company);
        if (words.Count == 0) return false;
        // The past does not count: the card's "Прежняя: … – Wargaming" line and an "ex-Wargaming" headline.
        var current = string.Join(" | ", text.Split('|').Select(part => part.Trim()).Where(part => !PastEmployer().IsMatch(part)));
        var norm = " " + string.Join(' ', Words().Split(TextUtil.Normalize(current)).Where(w => w.Length > 0)) + " ";
        return words.Take(2).All(w => norm.Contains(" " + w + " ", StringComparison.Ordinal) && !norm.Contains(" ex " + w + " ", StringComparison.Ordinal)
                                      && !norm.Contains(" бывший " + w + " ", StringComparison.Ordinal));
    }

    /// <summary>A Telegram account named after the company: usernames glue words ("@epam_hr", "wargaminghr"), so a substring counts.</summary>
    public static bool TelegramAccountOf(string company, string nameAndUsername)
    {
        var first = CompanyWords(company).FirstOrDefault();
        return first is not null && TextUtil.Normalize(nameAndUsername).Replace("_", "", StringComparison.Ordinal).Contains(first, StringComparison.Ordinal);
    }

    public static List<string> CompanyWords(string company) =>
        Words().Split(TextUtil.Normalize(company)).Where(w => w.Length >= 3 && !GenericCompanyWords.Contains(w)).ToList();

    private static readonly HashSet<string> GenericCompanyWords =
    [
        "ооо", "оао", "зао", "пао", "одо", "чуп", "llc", "ltd", "inc", "gmbh", "corp", "group", "systems", "system", "software",
        "solutions", "technologies", "technology", "tech", "labs", "company", "global", "international", "digital", "consulting",
        "services", "development", "компания", "группа", "компаний", "технологии", "системы", "софт", "солюшнс", "the",
    ];

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"^(прежн\w*|past|previous|former)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PastEmployer();

}
