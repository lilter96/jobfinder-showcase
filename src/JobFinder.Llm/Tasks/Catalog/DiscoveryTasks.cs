using System.Globalization;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Contracts.Wire;
using static JobFinder.Llm.Tasks.JobFinderLlm;

namespace JobFinder.Llm.Tasks;

public sealed record ScoreInput(CandidateProfile Profile, Vacancy Vacancy, string SalaryNote);

public sealed record PlanCommentInput(CandidateProfile Profile, string Author, string PostText, string? Remarks);

public sealed record ReadLinkedInPeopleInput(string Company, string Blocks);

internal static class DiscoveryTaskBuilders
{
    public static LlmTaskPayload Score(ScoreInput input)
    {
        var profile = input.Profile;
        var vacancy = input.Vacancy;
        var salaryNote = input.SalaryNote;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
        };
        var user = $"{VacancyText(vacancy)}\n\nЗарплата относительно ожиданий (посчитано кодом, не пересчитывай): {salaryNote}";
        return new LlmTaskPayload(LlmPurpose.Score, bindings, user);
    }

    public static LlmTaskPayload PlanComment(PlanCommentInput input)
    {
        var profile = input.Profile;
        var author = input.Author;
        var postText = input.PostText;
        var remarks = input.Remarks;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
        };
        var user = $"Автор: @{author}\n<post>\n{TextUtil.Truncate(postText, MaxPostChars)}\n</post>";
        if (remarks is not null)
            user += $"\n\nПрошлые варианты не прошли проверку:\n{remarks}\nПерепиши все три заново по правилам: коротко, живо, без лекций и без стека, один вариант — вопрос автору.";
        return new LlmTaskPayload(LlmPurpose.Comment, bindings, user);
    }

    public static LlmTaskPayload ReadLinkedInPeople(ReadLinkedInPeopleInput input)
    {
        var company = input.Company;
        var blocks = input.Blocks;
        var bindings = new Dictionary<string, string> { ["company"] = company };
        return new LlmTaskPayload(LlmPurpose.Extract, bindings, $"<blocks>\n{TextUtil.Truncate(blocks, 16000)}\n</blocks>");
    }
}
