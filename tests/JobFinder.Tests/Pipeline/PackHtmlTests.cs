using JobFinder.Core.Domain;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Documents;

namespace JobFinder.Tests.Pipeline;

public sealed class PackHtmlTests
{
    [Fact]
    public void Salary_without_a_confirmed_position_keeps_the_question_and_options_in_the_printed_pack()
    {
        var pack = new InterviewCopilot
        {
            Salary = new()
            {
                Q = new() { Id = "salary", Question = "Какие у вас зарплатные ожидания?", Category = "negotiation" },
                A = new() { EvidenceLevel = "personal_unknown", PositionOptions = ["Уточнить вилку", "Обсудить формат"] },
            },
        };
        var html = PackHtml.Render(pack, new Vacancy { Title = "Backend", CompanyName = "Synthetic", Url = "https://vacancy.invalid/1", Fingerprint = "synthetic" }, "Technical", null, "");
        Assert.Contains("Какие у вас зарплатные ожидания?", html);
        Assert.Contains("Нужна ваша позиция (дашборд)", html);
        Assert.Contains("Уточнить вилку / Обсудить формат", html);
        Assert.DoesNotContain("<div class=\"start\"></div>", html);
    }

    [Fact]
    public void Confirmed_salary_keeps_speech_and_check_reasons_instead_of_only_a_short_excerpt()
    {
        var pack = new InterviewCopilot
        {
            Salary = new()
            {
                Q = new() { Id = "salary", Question = "Зарплатные ожидания", Category = "negotiation" },
                A = new() { Start = "Мои ожидания обсуждаемы.", Core = "Важны задачи и формат.", Extended = "Уточним детали на встрече.", EvidenceLevel = "verified" },
                RequiresApproval = true, ApprovalReasons = ["Проверьте актуальность позиции"],
            },
        };
        var html = PackHtml.Render(pack, new Vacancy { Title = "Backend", Url = "https://vacancy.invalid/1", Fingerprint = "synthetic" }, "Technical", null, "");
        Assert.Contains("Мои ожидания обсуждаемы.", html);
        Assert.Contains("Важны задачи и формат.", html);
        Assert.Contains("Уточним детали на встрече.", html);
        Assert.Contains("Проверьте актуальность позиции", html);
    }
}
