using System.Globalization;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Http;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Market;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public interface IInterviewPreparationCapture
{
    Task<InterviewPreparationInput> CaptureAsync(InterviewPreparationSeed seed, CancellationToken ct);
    Task EnsureCurrentAsync(JobFinderDbContext context, InterviewPreparationSeed seed, InterviewPreparationInput? input, CancellationToken ct);
}

public sealed class InterviewPreparationCapture(IDbContextFactory<JobFinderDbContext> db, JobFinderLlm llm, MarketService market,
    PoliteHttp http, ILogger<InterviewPreparationCapture> log) : IInterviewPreparationCapture
{
    public async Task<InterviewPreparationInput> CaptureAsync(InterviewPreparationSeed seed, CancellationToken ct)
    {
        CandidateProfile profile;
        CandidateFacts facts;
        long profileVersion, factsVersion;
        InterviewPreparationSupport support;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await LockAsync(context, seed.Source.Id, ct);
            await EnsureSourceAsync(context, seed, ct);
            (profile, facts, profileVersion, factsVersion) = await CandidateAsync(context, ct);
            support = await SupportAsync(context, seed, profile, ct);
            await transaction.CommitAsync(ct);
        }
        var research = new StringBuilder(support.Research);
        if (support.HabrUrl is { } habr) research.Append(await ReadPageAsync(habr, "Хабр Карьера", ct));
        if (support.Site is { } site) research.Append(await ReadPageAsync(site, "Сайт компании", ct));
        var vacancy = support.Vacancy.ToVacancy();
        if (vacancy.Company is { } company) company.Description = support.CompanyDescription;
        var view = await market.DescribeAsync(vacancy, ct);
        var marketText = $"{view.Note ?? "данных мало"}\nОжидания кандидата: {profile.DesiredSalary?.Amount} {profile.DesiredSalary?.Currency} net";
        var operation = llm.RestoreOperation(seed.Operation);
        var input = InterviewPreparationSteps.Capture(seed, operation, profile, facts, profileVersion, factsVersion, vacancy, support.Catalog,
            support.Conversation, support.Notes, support.Hash(), research.ToString(), marketText);
        input.AnswerSource.CompanyDescription = support.CompanyDescription;
        input.Recipe = InterviewPreparationSteps.InputRecipe(operation, seed, input);
        return input;
    }

    public async Task EnsureCurrentAsync(JobFinderDbContext context, InterviewPreparationSeed seed, InterviewPreparationInput? input, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("interview witness requires a source transaction");
        await LockAsync(context, seed.Source.Id, ct);
        await EnsureSourceAsync(context, seed, ct);
        if (input is null) return;
        InterviewPreparationSteps.Restore(llm, seed, input);
        try
        {
            var (profile, facts, profileVersion, factsVersion) = await CandidateAsync(context, ct);
            if (input.ProfileVersion != profileVersion || input.FactsVersion != factsVersion
                || CanonicalJson.Hash(ProfileProvider.ToJson(profile)) != CanonicalJson.Hash(ProfileProvider.ToJson(input.AnswerSource.Profile))
                || CanonicalJson.Hash(JsonSerializer.Serialize(facts, FactsJsonContext.Default.CandidateFacts))
                    != CanonicalJson.Hash(JsonSerializer.Serialize(input.AnswerSource.Facts, FactsJsonContext.Default.CandidateFacts))
                || (await SupportAsync(context, seed, profile, ct)).Hash() != input.SupportHash)
                throw new InterviewPreparationSupersededException();
        }
        catch (InterviewPreparationPrerequisiteException) { throw new InterviewPreparationSupersededException(); }
    }

    internal static async Task LockAsync(JobFinderDbContext context, long id, CancellationToken ct)
    {
        await LockCandidateAsync(context, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM interviews WHERE id = {id} FOR UPDATE", ct);
    }

    internal static async Task LockCandidateAsync(JobFinderDbContext context, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("interview locks require a transaction");
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ProfileProvider.Key}, 0))", ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({FactsProvider.Key}, 0))", ct);
    }

    private static async Task EnsureSourceAsync(JobFinderDbContext context, InterviewPreparationSeed seed, CancellationToken ct)
    {
        var source = await context.Interviews.AsNoTracking().SingleOrDefaultAsync(row => row.Id == seed.Source.Id, ct);
        if (source is null || !seed.Source.Matches(source, seed.Operation.OperationId)) throw new InterviewPreparationSupersededException();
    }

    private static async Task<(CandidateProfile Profile, CandidateFacts Facts, long ProfileVersion, long FactsVersion)> CandidateAsync(JobFinderDbContext context, CancellationToken ct)
    {
        var profileRow = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        var factsRow = await context.Documents.AsNoTracking().SingleOrDefaultAsync(row => row.Key == FactsProvider.Key, ct);
        if (profileRow is null || factsRow is null) throw new InterviewPreparationPrerequisiteException("нет опубликованного профиля или фактов кандидата");
        var profile = ProfileProvider.FromJson(profileRow.Content);
        if (!profile.Reviewed) throw new InterviewPreparationPrerequisiteException("профиль кандидата не проверен");
        var facts = JsonSerializer.Deserialize(factsRow.Content, FactsJsonContext.Default.CandidateFacts)?.WithStandardQuestions()
            ?? throw new InterviewPreparationPrerequisiteException("нет фактов кандидата");
        return (profile, facts, profileRow.Version, factsRow.Version);
    }

    private static async Task<InterviewPreparationSupport> SupportAsync(JobFinderDbContext context, InterviewPreparationSeed seed, CandidateProfile profile, CancellationToken ct)
    {
        var interview = await context.Interviews.AsNoTracking().Include(row => row.Application).ThenInclude(row => row!.Vacancy).ThenInclude(row => row!.Company)
            .SingleAsync(row => row.Id == seed.Source.Id, ct);
        var vacancy = interview.Application?.Vacancy ?? throw new InterviewPreparationSupersededException();
        var stories = await context.Stories.AsNoTracking().Where(row => row.Confirmed).OrderBy(row => row.Id).ToListAsync(ct);
        var events = await context.ApplicationEvents.AsNoTracking().Where(row => row.ApplicationId == interview.ApplicationId
                && (row.Kind == EventKind.MessageIn || row.Kind == EventKind.MessageOut) && row.Text != null)
            .OrderByDescending(row => row.At).ThenByDescending(row => row.Id).Take(14).ToListAsync(ct);
        var notes = await context.Interviews.AsNoTracking().Where(row => row.ApplicationId == interview.ApplicationId && row.Debrief != null && row.Id != interview.Id)
            .OrderBy(row => row.Id).Select(row => row.Debrief!).ToListAsync(ct);
        var company = vacancy.Company;
        var research = new StringBuilder();
        if (company is not null)
        {
            research.Append("hh: ").Append(company.Name).Append("; отрасли: ").Append(string.Join(", ", company.Industries));
            if (company.SizeCategory is { } size) research.Append("; размер: ").Append(size);
            if (company.Rating is { } rating) research.Append("; рейтинг работодателя: ").Append(rating.ToString("0.0", CultureInfo.InvariantCulture));
            if (company.ItAccredited) research.Append("; IT-аккредитация");
            if (company.OpenVacancies is { } open) research.Append("; открытых вакансий: ").Append(open);
            research.Append('\n').Append(TextUtil.Truncate(company.Description, 3000)).Append('\n');
        }
        var cutoff = seed.RenderedAt.AddDays(-90);
        var others = await context.Vacancies.AsNoTracking().Where(row => row.Id != vacancy.Id && row.FirstSeenAt >= cutoff
                && (company != null ? row.CompanyId == company.Id : row.CompanyName == vacancy.CompanyName))
            .OrderByDescending(row => row.FirstSeenAt).ThenByDescending(row => row.Id).Take(12).Select(row => new { row.Title, row.Skills }).ToListAsync(ct);
        foreach (var other in others) research.Append("- ").Append(other.Title).Append(" [").Append(string.Join(", ", other.Skills.Take(8))).Append("]\n");
        if (research.Length == 0) research.Append("(данных о компании нет, кроме вакансии; ключ: ").Append(VacancyFingerprint.NormalizeCompany(vacancy.CompanyName)).Append(')');
        return new()
        {
            Vacancy = ResumeVacancyInput.From(vacancy), CompanyDescription = company?.Description, Catalog = CopilotRules.Catalog(profile, stories),
            Conversation = events.Count == 0 ? "(переписки нет)" : string.Join("\n", events.OrderBy(row => row.At).ThenBy(row => row.Id)
                .Select(row => $"[{(row.Kind == EventKind.MessageIn ? "работодатель" : "кандидат")}] {TextUtil.Truncate(row.Text, 700)}")),
            Notes = string.Join("\n", notes), Research = research.ToString(),
            HabrUrl = company?.HabrAlias is { } alias ? "https://career.habr.com/companies/" + Uri.EscapeDataString(alias) : null, Site = company?.Site,
        };
    }

    private async Task<string> ReadPageAsync(string url, string label, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var html = await http.GetStringAsync(uri, deadline.Token);
            using var document = new HtmlParser().ParseDocument(html);
            foreach (var node in document.QuerySelectorAll("script,style,noscript,nav,header,footer")) node.Remove();
            return $"\n{label} ({uri}):\n{TextUtil.Truncate(document.Body?.TextContent.Trim(), 3000)}\n";
        }
        catch (Exception error) when (error is HttpRequestException or SourceBlockedException || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.Debug($"[interview] {label}: {error.Message}");
            return "";
        }
    }
}
