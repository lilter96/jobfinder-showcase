using JobFinder.Core.Profile;

namespace JobFinder.Tests.Core;

public sealed class ProfileEvidenceTests
{
    private static readonly CandidateProfile Profile = new()
    {
        Title = "Senior .NET Backend Developer",
        Location = "Минск",
        Experience =
        [
            new ProfileJob
            {
                Company = "Uni Systems", Role = ".NET Developer", Period = "2022-2023",
                Highlights = "Спроектировал и разрабатывал систему «Умная парковка» для аэропорта Домодедово: бронирование, интеграцию с ALPR-камерами. Обеспечил идемпотентность платежей и интеграционное тестирование.",
            },
        ],
    };

    private static readonly string Evidence = ProfileEvidence.Text(Profile,
        new CandidateFacts { Facts = [new CandidateFact { Key = "relocation", Question = "Формат работы", Answer = "удалённо из Минска", Confirmed = true }] });

    [Theory]
    [InlineData("Обеспечил идемпотентность платежей и интеграционное тестирование")]
    [InlineData("обеспечил  ИДЕМПОТЕНТНОСТЬ платежей,")]
    [InlineData("систему «Умная парковка» для аэропорта Домодедово")]
    [InlineData("Формат работы: удалённо из Минска")]
    public void A_real_fragment_is_found_case_and_punctuation_aside(string quote) => Assert.True(ProfileEvidence.Contains(Evidence, quote));

    [Fact]
    public void Logistics_may_rest_on_a_single_value_experience_may_not()
    {
        Assert.True(ProfileEvidence.Contains(Evidence, "Минск", minWords: 1));
        Assert.False(ProfileEvidence.Contains(Evidence, "Минск"));
        // A whole word, not a piece of one: "парк" is not in "парковка".
        Assert.False(ProfileEvidence.Contains(Evidence, "парк", minWords: 1));
    }

    [Theory]
    [InlineData("Разрабатывал платёжный шлюз для банка")] // invented
    [InlineData("работал с платежами и fintech")] // inferred from a word
    [InlineData("C#")] // too short to prove anything
    [InlineData("")]
    public void A_paraphrase_an_invention_or_a_single_word_is_not_support(string quote) => Assert.False(ProfileEvidence.Contains(Evidence, quote));
}
