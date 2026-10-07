using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Llm.Tasks;

namespace JobFinder.Tests.Core;

/// <summary>
/// Source-generated System.Text.Json sets a missing init-only property to null, ignoring its initializer: an evaluation stored before
/// a field existed (or a hand-written profile without a list) then throws on first use. Everything read back from JSON uses setters.
/// </summary>
public sealed class JsonDefaultsTests
{
    public static TheoryData<Type> Persisted()
    {
        var types = new[] { typeof(VacancyEvaluation), typeof(CandidateProfile), typeof(ResumeDocument), typeof(JobFinder.Pipeline.Career.MockTurn),
                typeof(VacancyContact), typeof(PostLink), typeof(AssessmentVacancy), typeof(AssessmentSource),
                typeof(JobFinder.Pipeline.Career.BotEditPrompt), typeof(JobFinder.Pipeline.Career.BotEditBinding) }
            .Concat(typeof(InterviewCopilot).Assembly.GetTypes().Where(t => t.Namespace == typeof(InterviewCopilot).Namespace && t.IsClass && t.IsPublic
                                                                        && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(CandidateProfile).Assembly.GetTypes().Where(t => t.Namespace == typeof(CandidateProfile).Namespace && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(ResumeDocument).Assembly.GetTypes().Where(t => t.Namespace == typeof(ResumeDocument).Namespace && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(JobFinder.Core.Artifacts.ArtifactRecipe).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Core.Artifacts.ArtifactRecipe).Namespace
                && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(JobFinder.Core.Policies.SearchPolicy).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Core.Policies.SearchPolicy).Namespace
                && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(JobFinder.Pipeline.Modules.Hiring.LetterPreparationInput).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Pipeline.Modules.Hiring.LetterPreparationInput).Namespace
                && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(JobFinder.Pipeline.Modules.Candidate.StoryPreparationInput).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Pipeline.Modules.Candidate.StoryPreparationInput).Namespace
                && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(JobFinder.Pipeline.Modules.Engagement.CommentPreparationInput).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Pipeline.Modules.Engagement.CommentPreparationInput).Namespace
                && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(JobFinder.Pipeline.Modules.Preparation.InterviewPreparationInput).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Pipeline.Modules.Preparation.InterviewPreparationInput).Namespace
                && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(JobFinder.Pipeline.Notifications.NotificationDeliveryInput).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Pipeline.Notifications.NotificationDeliveryInput).Namespace
                && t.GetMethod("<Clone>$") is not null))
            .Concat(typeof(JobFinder.Pipeline.Modules.Discovery.ChannelMembershipInput).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Pipeline.Modules.Discovery.ChannelMembershipInput).Namespace
                && t.GetMethod("<Clone>$") is not null))
            // Positional records get their values from the constructor, not from JSON setters.
            .Concat(typeof(JobFinder.Pipeline.Controls.BotControlInput).Assembly.GetTypes().Where(t => t.Namespace == typeof(JobFinder.Pipeline.Controls.BotControlInput).Namespace
                && t.GetMethod("<Clone>$") is not null))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .Distinct();
        var data = new TheoryData<Type>();
        foreach (var t in types) data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(Persisted))]
    public void Json_records_have_no_init_only_properties(Type type)
    {
        var initOnly = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) == true)
            .Select(p => p.Name).ToList();
        Assert.Empty(initOnly);
    }

    [Fact]
    public void Missing_fields_keep_their_defaults()
    {
        var copilot = JsonSerializer.Deserialize("{}", InterviewJsonContext.Default.InterviewCopilot)!;
        Assert.NotNull(copilot.Preparation);
        Assert.NotNull(copilot.LastMinute.DoNotSay);
        // A pack of the old research format reads as version 0: shown as "rebuild", never a crash.
        var old = JsonSerializer.Deserialize("""{"companyBrief":"x","likelyQuestions":[{"question":"q"}]}""", InterviewJsonContext.Default.InterviewCopilot)!;
        Assert.Equal(0, old.Version);
        Assert.Empty(old.Preparation);
        var oldVacancy = JsonSerializer.Deserialize("{}", JobFinder.Pipeline.Modules.Discovery.VacancyAssessmentJsonContext.Default.AssessmentVacancy)!;
        Assert.Equal(0, oldVacancy.LetterRevision);
        var profile = JsonSerializer.Deserialize("""{"name":"X"}""", ProfileJsonContext.Default.CandidateProfile)!;
        Assert.NotNull(profile.Skills);
        Assert.NotNull(profile.DealBreakers);
        Assert.True(profile.Reviewed);
    }
}

public sealed class StoredPathTests
{
    [Fact]
    public void Paths_are_stored_relative_to_the_home_and_resolved_back()
    {
        var full = Path.Combine(Directory.GetCurrentDirectory(), "data", "resumes", "18", "cv.pdf");
        var stored = JobFinder.Core.Options.StoredPath.ToStored(full);
        Assert.Equal("data/resumes/18/cv.pdf", stored);
        Assert.Equal(Path.GetFullPath(full), JobFinder.Core.Options.StoredPath.Resolve(stored));
    }

    [Theory]
    [InlineData(@"E:\Work\jobfinder\data\resumes\1\cv.pdf")]
    [InlineData("/app/data/resumes/1/cv.pdf")]
    public void Absolute_paths_from_the_other_side_are_re_rooted_at_data(string written) =>
        Assert.Equal(Path.GetFullPath(Path.Combine("data", "resumes", "1", "cv.pdf")), JobFinder.Core.Options.StoredPath.Resolve(written));
}
