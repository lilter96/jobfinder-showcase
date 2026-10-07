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

public sealed record ImportResumeInput(string ResumeText);

public sealed record ComposeStoryInput(string Achievement, string Answers, string ResumeText);

internal static class CandidateTaskBuilders
{
    public static LlmTaskPayload ImportResume(ImportResumeInput input)
    {
        var resumeText = input.ResumeText;
        var bindings = new Dictionary<string, string>();
        var user = $"<resume>\n{TextUtil.Truncate(resumeText, 30000)}\n</resume>";
        return new LlmTaskPayload(LlmPurpose.Profile, bindings, user);
    }

    public static LlmTaskPayload ComposeStory(ComposeStoryInput input)
    {
        var achievement = input.Achievement;
        var answers = input.Answers;
        var resumeText = input.ResumeText;
        var bindings = new Dictionary<string, string>();
        var user = $"<achievement>\n{achievement}\n</achievement>\n\n<answers>\n{TextUtil.Truncate(answers, 8000)}\n</answers>\n\n" +
                   $"<resume>\n{TextUtil.Truncate(resumeText, 8000)}\n</resume>";
        return new LlmTaskPayload(LlmPurpose.Coach, bindings, user);
    }
}
