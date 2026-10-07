using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

internal static class ApplicationDeliverySources
{
    public static Task<Application?> ReadAsync(JobFinderDbContext context, long id, CancellationToken ct) =>
        context.Applications.AsNoTracking().Include(row => row.Vacancy).ThenInclude(row => row!.Company)
            .Include(row => row.Vacancy).ThenInclude(row => row!.Sources).SingleOrDefaultAsync(row => row.Id == id, ct);

    public static LetterBrief Brief(ApplicationDeliveryInput input)
    {
        var vacancy = input.Vacancy.ToVacancy();
        vacancy.Evaluation = input.Evaluation;
        return LetterWriter.ResponseBrief(vacancy, input.Channel, input.Target);
    }

    public static string Serialize(ApplicationDeliveryInput input) => JsonSerializer.Serialize(input, ApplicationDeliveryJsonContext.Default.ApplicationDeliveryInput);

    public static ApplicationDeliveryInput Read(ApplicationDeliveryRequest request)
    {
        try { return ReadCore(request); }
        catch (JsonException) { throw new UnsupportedApplicationDeliveryException(); }
    }

    private static ApplicationDeliveryInput ReadCore(ApplicationDeliveryRequest request)
    {
        var input = JsonSerializer.Deserialize(request.InputJson, ApplicationDeliveryJsonContext.Default.ApplicationDeliveryInput)
            ?? throw new UnsupportedApplicationDeliveryException();
        if (input.Version != 1 || input.ApplicationId <= 0 || input.ApplicationId != request.ApplicationId
            || input.Revision < 0 || input.Revision != request.ApplicationRevision || input.OperationId == Guid.Empty
            || input.Vacancy is null || input.Vacancy.Id <= 0 || input.Profile is not { Reviewed: true }
            || input.Facts?.Facts is null || input.ProfileVersion <= 0 || input.FactsVersion <= 0 || input.AutomationVersion <= 0
            || input.Automation?.SchemaVersion != 1 || input.Automation.Validate() is not null || input.Operation?.SchemaVersion != 1 || input.Operation.OperationId == Guid.Empty
            || input.Recipe is null || input.Recipe.OperationId != input.Operation.OperationId || input.CapturedAt == default
            || !Enum.IsDefined(input.Mode) || input.Mode == ApplicationMode.External || !Enum.IsDefined(input.Channel) || input.Channel == ApplicationChannel.Manual
            || string.IsNullOrWhiteSpace(input.Text) || string.IsNullOrWhiteSpace(input.ReviewContextHash)
            || input.Text != HumanText.Clean(input.OriginalLetter ?? input.Vacancy.CoverLetter ?? "")
            || input.Channel != ApplicationChannel.HeadHunter && string.IsNullOrWhiteSpace(input.Target)
            || CanonicalJson.Hash(request.InputJson) != request.InputHash) throw new UnsupportedApplicationDeliveryException();
        return input;
    }

    public static void EnsureCompatible(ApplicationDeliveryInput input, JobFinderLlm llm)
    {
        var operation = llm.RestoreOperation(input.Operation);
        var brief = Brief(input);
        if (LetterWriter.ReviewRecipe(operation, input.Profile, input.Facts, brief, input.Text).Hash != input.Recipe.Hash
            || LetterWriter.ContextRecipe(operation, input.Profile, input.Facts, brief).Hash != input.ReviewContextHash)
            throw new ApplicationDeliverySupersededException();
    }

    public static async Task<bool> MatchesAsync(JobFinderDbContext context, ApplicationDeliveryInput input, CancellationToken ct)
    {
        var companyId = input.Vacancy.CompanyId;
        if (companyId is { } id) await context.Companies.FromSqlInterpolated($"SELECT * FROM companies WHERE id = {id} FOR SHARE").ToListAsync(ct);
        await context.VacancySources.FromSqlInterpolated($"SELECT * FROM vacancy_sources WHERE vacancy_id = {input.Vacancy.Id} FOR SHARE").ToListAsync(ct);
        var documents = await context.Documents.FromSqlInterpolated($"SELECT * FROM app_documents WHERE key = {ProfileProvider.Key} OR key = {FactsProvider.Key} FOR SHARE").ToListAsync(ct);
        var current = await ReadAsync(context, input.ApplicationId, ct);
        return current is { Status: ApplicationStatus.Pending, Vacancy: not null } && current.Vacancy.Status is not (VacancyStatus.Applied or VacancyStatus.Dismissed)
            && current.OperationId == input.OperationId
            && current.Revision == input.Revision && current.Channel == input.Channel && current.Mode == input.Mode
            && current.Target == input.Target && current.CoverLetter == input.OriginalLetter
            && current.Vacancy.Score == input.Score && current.Vacancy.Decision == input.Decision
            && JsonSerializer.Serialize(current.Vacancy.Evaluation, ApplicationDeliveryJsonContext.Default.VacancyEvaluation)
                == JsonSerializer.Serialize(input.Evaluation, ApplicationDeliveryJsonContext.Default.VacancyEvaluation)
            && Witness(AssessmentVacancy.From(current.Vacancy)) == Witness(input.Vacancy)
            && documents.Any(row => row.Key == ProfileProvider.Key && row.Version == input.ProfileVersion
                && CanonicalJson.Hash(ProfileProvider.ToJson(ProfileProvider.FromJson(row.Content))) == CanonicalJson.Hash(ProfileProvider.ToJson(input.Profile)))
            && documents.Any(row => row.Key == FactsProvider.Key && row.Version == input.FactsVersion
                && JsonSerializer.Deserialize(row.Content, FactsJsonContext.Default.CandidateFacts)!.WithStandardQuestions().ForPrompt() == input.Facts.ForPrompt());
    }

    private static string Witness(AssessmentVacancy vacancy) => CanonicalJson.Hash(JsonSerializer.Serialize(vacancy, LetterPreparationJsonContext.Default.AssessmentVacancy));
}
