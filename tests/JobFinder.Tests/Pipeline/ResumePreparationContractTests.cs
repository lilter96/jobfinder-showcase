using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Resume;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Modules.Preparation;

namespace JobFinder.Tests.Pipeline;

public sealed class ResumePreparationContractTests
{
    [Fact]
    public void An_incomplete_checklist_does_not_pass_even_when_the_score_is_nine()
    {
        var review = new ResumeReview { Checks = ResumeReview.Criteria.Keys.Where(id => id != "readability")
            .Select(id => new ReviewCheck { Id = id, Passed = true }).ToList() };
        Assert.Equal(9, review.Scored().Score);
        Assert.False(review.HasCompleteChecklist());
        Assert.False(ResumeTailor.Ready(new ResumeReport { Ready = true, Review = review, Pages = 1 }));
        Assert.False(new ResumeRound { Review = review, Pages = 1 }.Ready);
        review.Checks.Add(new() { Id = "readability", Passed = false });
        Assert.True(new ResumeRound { Review = review, Pages = 1 }.Ready);
        review.Checks.Add(new() { Id = "readability", Passed = true });
        Assert.False(new ResumeRound { Review = review, Pages = 1 }.Ready);
    }

    [Fact]
    public void The_narrow_projection_preserves_exact_vacancy_inputs_without_serializing_EF_relationships()
    {
        var vacancy = new Vacancy { Id = 1, Title = "Senior .NET", CompanyName = null, Company = new Company { Name = "Company", Industries = ["Fintech"] },
            Description = "Details", WorkFormats = WorkFormat.Remote, SalaryFrom = 1000, Currency = "USD", EnglishRequired = EnglishLevel.B1,
            City = "Minsk", Skills = [".NET"], Experience = "3 years", Fingerprint = "synthetic", Url = "https://example.invalid/1" };
        var snapshot = ResumeVacancyInput.From(vacancy);
        var reloaded = JsonSerializer.Deserialize(JsonSerializer.Serialize(new ResumePreparationInput { Vacancy = snapshot },
            ResumePreparationJsonContext.Default.ResumePreparationInput), ResumePreparationJsonContext.Default.ResumePreparationInput)!;
        Assert.Equal(JobFinderLlm.VacancyText(vacancy), JobFinderLlm.VacancyText(reloaded.Vacancy.ToVacancy()));
        vacancy.Skills.Add("changed later");
        vacancy.Company.Industries.Add("changed later");
        Assert.Equal([".NET"], snapshot.Skills);
        Assert.Equal(["Fintech"], snapshot.Industries);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("print")]
    [InlineData("review")]
    [InlineData("finish")]
    public void Legacy_message_defaults_survive_source_generated_deserialization(string kind)
    {
        var id = Guid.NewGuid();
        var json = "{\"requestId\":\"" + id + "\"}";
        var values = kind switch
        {
            "draft" => JsonSerializer.Deserialize(json, ResumePreparationJsonContext.Default.DraftResume) is { } draft ? (draft.RequestId, draft.Round, draft.Version) : default,
            "print" => JsonSerializer.Deserialize(json, ResumePreparationJsonContext.Default.PrintResume) is { } print ? (print.RequestId, print.Round, print.Version) : default,
            "review" => JsonSerializer.Deserialize(json, ResumePreparationJsonContext.Default.ReviewResume) is { } review ? (review.RequestId, review.Round, review.Version) : default,
            "finish" => JsonSerializer.Deserialize(json, ResumePreparationJsonContext.Default.FinishResume) is { } finish ? (finish.RequestId, finish.Round, finish.Version) : default,
            _ => throw new ArgumentException(kind),
        };
        Assert.Equal((id, 1, 1), values);
    }
}
