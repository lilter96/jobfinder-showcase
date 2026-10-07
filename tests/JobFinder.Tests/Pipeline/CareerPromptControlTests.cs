using JobFinder.Core.Domain;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;

namespace JobFinder.Tests.Pipeline;

public sealed class CareerPromptControlTests
{
    [Fact]
    public void Pack_buttons_use_the_explicit_vacancy_and_saved_source_without_navigation_properties()
    {
        var source = new Interview { Id = 37, ApplicationId = 15, Kind = InterviewKind.Technical, Status = InterviewStatus.Scheduled,
            CreatedAt = DateTimeOffset.UtcNow, PreparationRequestId = Guid.NewGuid(), PreparationRevision = 2 };
        var vacancy = new Vacancy { Id = 12, Title = "Backend", CompanyName = "Synthetic employer", Url = "https://vacancy.invalid/12", Fingerprint = "synthetic" };
        var version = CareerPromptSources.ControlVersion(source, vacancy);
        var (_, buttons) = CareerCards.Pack(source, vacancy, new InterviewCopilot(), null);
        Assert.Contains(Callback.Button("🗓 Указать время", CareerCallback.InterviewTime, source.Id, version), buttons.SelectMany(row => row));
        Assert.Contains(Callback.Button("📝 Дебриф", CareerCallback.InterviewDebrief, source.Id, version), buttons.SelectMany(row => row));
        Assert.NotEqual(version, CareerPromptSources.ControlVersion(source, new Vacancy { Title = "Another role", CompanyName = vacancy.CompanyName,
            Url = vacancy.Url, Fingerprint = vacancy.Fingerprint }));
        source.PreparationRequestId = Guid.NewGuid();
        Assert.NotEqual(version, CareerPromptSources.ControlVersion(source, vacancy));
        Assert.Null(source.Application);
    }
}
