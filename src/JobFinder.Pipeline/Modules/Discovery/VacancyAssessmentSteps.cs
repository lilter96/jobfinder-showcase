using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Workers;

namespace JobFinder.Pipeline.Modules.Discovery;

public interface IVacancyAssessmentSteps
{
    void EnsureCompatible(VacancyAssessmentInput input);
    Task<AssessmentScore> ScoreAsync(VacancyAssessmentInput input, CancellationToken ct);
    Task<AssessmentLetter> LetterAsync(VacancyAssessmentInput input, AssessmentScore score, CancellationToken ct);
}

public sealed class VacancyAssessmentSteps(JobFinderLlm llm, LetterWriter writer) : IVacancyAssessmentSteps
{
    public void EnsureCompatible(VacancyAssessmentInput input) => Operation(input);
    public static ArtifactRecipe Recipe(JobFinderLlm operation, VacancyAssessmentInput input) => input.Annotation is not null ? operation.Recipe(
        [(LlmTaskCatalog.Score, LlmPurpose.Score)],
        new Dictionary<string, string?>
        {
            ["profile"] = ProfileProvider.ToJson(input.Profile), ["facts"] = input.Facts.ForPrompt(),
            ["vacancy"] = JsonSerializer.Serialize(input.Vacancy, VacancyAssessmentJsonContext.Default.AssessmentVacancy),
            ["salary"] = input.SalaryNote, ["previous"] = VacancyAssessments.AnnotationHash(input.Annotation),
        }, new Dictionary<string, string> { ["quality"] = "evaluation-annotations-v1" }) : input.Preview is not null ? operation.Recipe(
        [(LlmTaskCatalog.Score, LlmPurpose.Score)],
        new Dictionary<string, string?>
        {
            ["profile"] = ProfileProvider.ToJson(input.Profile), ["facts"] = input.Facts.ForPrompt(),
            ["vacancy"] = JsonSerializer.Serialize(input.Vacancy, VacancyAssessmentJsonContext.Default.AssessmentVacancy),
            ["salary"] = input.SalaryNote,
        }, new Dictionary<string, string> { ["quality"] = "vacancy-preview-v1" }) : operation.Recipe(
        [(LlmTaskCatalog.Score, LlmPurpose.Score), (LlmTaskCatalog.RecruiterMessage, LlmPurpose.Application),
            (LlmTaskCatalog.CheckRecruiterMessage, LlmPurpose.Application)],
        new Dictionary<string, string?>
        {
            ["profile"] = ProfileProvider.ToJson(input.Profile), ["facts"] = input.Facts.ForPrompt(),
            ["vacancy"] = JsonSerializer.Serialize(input.Vacancy, VacancyAssessmentJsonContext.Default.AssessmentVacancy),
            ["salary"] = input.SalaryNote,
        }, new Dictionary<string, string> { ["quality"] = "vacancy-assessment-v1", ["letter"] = LetterWriter.QualityVersion });

    private JobFinderLlm Operation(VacancyAssessmentInput input)
    {
        if (!VacancyAssessments.Supported(input) || input.Preview is null && (input.Vacancy.Id <= 0 || !input.Profile.Reviewed)
            || input.Operation.OperationId == Guid.Empty)
            throw new UnsupportedVacancyAssessmentException();
        var operation = llm.RestoreOperation(input.Operation);
        if (input.Recipe.OperationId != input.Operation.OperationId || input.Recipe.PolicyRevision != input.Operation.PolicyRevision
            || Recipe(operation, input).Hash != input.Recipe.Hash) throw new VacancyAssessmentSupersededException();
        return operation;
    }

    public async Task<AssessmentScore> ScoreAsync(VacancyAssessmentInput input, CancellationToken ct)
    {
        var operation = Operation(input);
        var vacancy = input.Vacancy.ToVacancy();
        if (input.Annotation is { } annotation)
        {
            var scored = (await operation.ScoreAsync(input.Profile, vacancy, input.SalaryNote, ct)).Value;
            var annotationLegitimacy = LegitimacyRules.Evaluate(new LegitimacyInput(vacancy.Title + "\n" + vacancy.Description,
                !string.IsNullOrWhiteSpace(vacancy.CompanyName), input.Reposts,
                vacancy.OriginalPostedAt is { } posted ? (int)(input.CapturedAt - posted).TotalDays : null,
                vacancy.ResponsesCount, input.RatioToP90, scored.Legitimacy, scored.LegitimacyReasons));
            return new AssessmentScore
            {
                Score = annotation.Score, Status = input.Vacancy.Status, Decision = annotation.Decision,
                EnglishRequired = input.Vacancy.EnglishRequired, Legitimacy = annotationLegitimacy.Level, RejectReason = annotation.RejectReason,
                Evaluation = annotation.Evaluation with
                {
                    Keywords = scored.Keywords, Language = scored.Language, LegitimacyReasons = annotationLegitimacy.Reasons, MarketNote = input.MarketNote,
                },
            };
        }
        var filter = new SearchPolicySnapshot(input.Search, input.SearchVersion).Filter();
        var before = filter.Check(vacancy, alreadyApplied: false);
        if (input.Preview is null && input.Vacancy.Blacklisted) return Rejected(vacancy, $"компания в чёрном списке (дашборд): {vacancy.CompanyName}");
        if (input.Preview is null && !before.Passed && !input.Responded) return Rejected(vacancy, before.Reason!);

        var result = await operation.ScoreAsync(input.Profile, vacancy, input.SalaryNote, ct);
        var scoring = result.Value;
        if (vacancy.EnglishRequired == EnglishLevel.Unknown) vacancy.EnglishRequired = EnglishLevels.Parse(scoring.EnglishRequired);
        var after = filter.Check(vacancy, alreadyApplied: false);
        var legitimacy = LegitimacyRules.Evaluate(new LegitimacyInput(vacancy.Title + "\n" + vacancy.Description,
            !string.IsNullOrWhiteSpace(vacancy.CompanyName), input.Reposts,
            vacancy.OriginalPostedAt is { } created ? (int)(input.CapturedAt - created).TotalDays : null,
            vacancy.ResponsesCount, input.RatioToP90, scoring.Legitimacy, scoring.LegitimacyReasons));
        var score = new AssessmentScore
        {
            CostUsd = input.Preview is null ? null : result.CostUsd,
            Score = Math.Clamp(scoring.Score + input.SalaryAdjustment, 0, 100), EnglishRequired = vacancy.EnglishRequired,
            Legitimacy = legitimacy.Level, Status = input.Responded ? VacancyStatus.Applied : VacancyStatus.Scored,
            Evaluation = new VacancyEvaluation
            {
                Score = scoring.Score, StackMatch = scoring.StackMatch, SeniorityMatch = scoring.SeniorityMatch,
                DomainMatch = scoring.DomainMatch, FormatMatch = scoring.FormatMatch, EnglishRequired = scoring.EnglishRequired,
                Strengths = scoring.Strengths, Gaps = scoring.Gaps, RedFlags = scoring.RedFlags, Summary = scoring.Summary,
                Model = result.Model, SalaryNote = input.SalaryNote, Keywords = scoring.Keywords, Language = scoring.Language,
                LegitimacyReasons = legitimacy.Reasons, MarketNote = input.MarketNote,
            },
        };
        if (input.Preview is not null) return score;
        if (input.Responded)
        {
            if ((before.Passed ? after : before) is { Passed: false } would)
                score.Evaluation.RedFlags.Add("фильтр системы отсеял бы: " + would.Reason);
            return score;
        }
        if (!after.Passed)
        {
            score.Status = VacancyStatus.Rejected;
            score.RejectReason = after.Reason + " (по тексту вакансии)";
        }
        else if (legitimacy.Level == Legitimacy.Scam)
        {
            score.Status = VacancyStatus.Rejected;
            score.RejectReason = "похоже на скам: " + string.Join("; ", legitimacy.Reasons);
        }
        else
        {
            var policy = input.Automation;
            score.Decision = DecisionPolicy.Decide(new DecisionInput(score.Score.Value,
                input.CanAutoApply && legitimacy.Level != Legitimacy.Suspicious, input.AutoToday),
                new DecisionSettings(policy.AutoApplyThreshold, policy.ApprovalThreshold, policy.DailyAutoApplyLimit, policy.AutoApplyPaused));
        }
        return score;
    }

    public async Task<AssessmentLetter> LetterAsync(VacancyAssessmentInput input, AssessmentScore score, CancellationToken ct)
    {
        var operation = Operation(input);
        if (input.Annotation is not null || input.Preview is not null) throw new UnsupportedVacancyAssessmentException();
        var vacancy = input.Vacancy.ToVacancy();
        vacancy.Evaluation = score.Evaluation;
        vacancy.EnglishRequired = score.EnglishRequired;
        var route = ApplyRoutes.Resolve(vacancy, input.EmailReady);
        var ticket = await writer.RequestResponseAsync(vacancy, route.Channel ?? ApplicationChannel.HeadHunter, route.Target,
            input.Profile, input.Facts, operation, ct, anchor: input.Vacancy);
        return new AssessmentLetter { PreparationId = ticket.RequestId, Text = ticket.Result?.Text ?? "", Review = ticket.Result?.Review,
            Problems = ticket.Result?.Problems.ToList() ?? [] };
    }

    private static AssessmentScore Rejected(Vacancy vacancy, string reason) => new()
    {
        Status = VacancyStatus.Rejected, RejectReason = reason, EnglishRequired = vacancy.EnglishRequired,
    };
}
