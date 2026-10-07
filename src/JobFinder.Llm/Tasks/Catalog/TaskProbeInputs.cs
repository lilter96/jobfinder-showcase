using JobFinder.Core.Domain;
using JobFinder.Core.Profile;

namespace JobFinder.Llm.Tasks;

internal static class TaskProbeInputs
{
    private const string ResumeText = "Иван Примеров. Senior .NET Backend Developer. Минск, BY. Acme, ноябрь 2023 – июль 2026: " +
        "разрабатывал API на C# и ASP.NET Core, PostgreSQL, Kafka; сократил задержку API с 200 до 100 мс. Английский B1.";
    private const string FactsText = "Локация: Минск. Формат: гибрид или удалённо из Беларуси. Английский B1. Выход через две недели.";
    private const string Message = "Здравствуйте! В Acme ускорил API с 200 до 100 мс, разобрав запросы к PostgreSQL. Готов обсудить вашу задачу.";
    private static DateTimeOffset Today => new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static CandidateProfile Profile => new()
    {
        Name = "Иван Примеров", Title = "Senior .NET Backend Developer", Summary = ResumeText, Location = "Минск, BY",
        YearsOfExperience = 5, EnglishLevel = "b1", WorkFormats = ["hybrid", "remote"],
        Skills = [new() { Name = "C#", Level = "expert" }, new() { Name = "PostgreSQL", Level = "good" }],
        Experience = [new() { Company = "Acme", Role = "Backend Developer", Period = "ноябрь 2023 – июль 2026", Highlights = ResumeText }],
    };
    private static Vacancy Vacancy => new()
    {
        Title = "Senior .NET Backend Developer", CompanyName = "Example", Url = "https://example.com/jobs/backend", Fingerprint = "probe",
        Description = "Развитие API на C#, ASP.NET Core и PostgreSQL. Оптимизация задержки. Минск, гибрид. Английский не нужен.",
        Skills = ["C#", "ASP.NET Core", "PostgreSQL"], City = "Минск", CountryCode = "BY", WorkFormats = WorkFormat.Hybrid,
    };
    private static HrQuestion HrQuestion => new() { Id = "about", Question = "Расскажите о своём опыте", Category = "О себе", Why = "релевантность опыта" };
    private static PlannedQuestion Question => new() { Id = "api", Question = "Как вы ускоряли API?", Category = "technical", Tests = "личный вклад" };

    public static Discovery.ExtractPostInput ExtractPost(string purpose) => new(
        "Ищем Senior .NET разработчика в Example: C#, ASP.NET Core, PostgreSQL. Минск, гибрид. ЗП 4000–5000 USD net.", "Test source", "2026-01-01", null, null);
    public static ScoreInput Score(string purpose) => new(Profile, Vacancy, "вилка не указана");
    public static PlanCommentInput PlanComment(string purpose) => new(Profile, "example_author", "Как вы находите медленные запросы PostgreSQL в API?", null);
    public static ReadLinkedInPeopleInput ReadLinkedInPeople(string purpose) => new("Example", "1. Анна Примерова — Tech Recruiter at Example");
    public static ImportResumeInput ImportResume(string purpose) => new(ResumeText);
    public static ComposeStoryInput ComposeStory(string purpose) => new("Сократил задержку API с 200 до 100 мс", "Оптимизировал запросы PostgreSQL в Acme", ResumeText);
    public static TailorResumeInput TailorResume(string purpose) => new(ResumeText, "", FactsText, "", Vacancy, ["C#", "PostgreSQL"], "ru", null, null);
    public static ReviewResumeInput ReviewResume(string purpose) => new(Vacancy, ResumeText, ResumeText, "", FactsText, "");
    public static MockQuestionsInput MockQuestions(string purpose) => new(Profile, "technical", 2, Vacancy, "");
    public static MockReviewInput MockReview(string purpose) => new("Как ускорить запрос?", "Сначала план выполнения и измерение", "Сначала замерю задержку и посмотрю план запроса");
    public static MockSummaryInput MockSummary(string purpose) => new("Вопрос: как ускорить запрос? Ответ: измерить, проверить план выполнения, повторить замер.");
    public static DebriefInput Debrief(string purpose) => new("Technical interview, Example", "Обсудили PostgreSQL, пригласили на финальный этап, дату пока не назначили.");
    public static OfferInput Offer(string purpose) => new(Profile, "Example предлагает 4500 USD net, гибрид в Минске", Vacancy, "нет данных", "других офферов нет");
    public static RejectionReviewInput RejectionReview(string purpose) => new(Profile, ResumeText, "Один отказ без причины, два отклика без ответа", Today);
    public static HhAdviceInput HhAdvice(string purpose) => new(ResumeText, "C#, PostgreSQL", "10 просмотров, 2 приглашения");
    public static InterviewPlanInput InterviewPlan(string purpose) => new(Profile, "technical", Vacancy, "Example создаёт API", ResumeText, FactsText, "", "нет данных", "", Today);
    public static HrAnswerInput HrAnswer(string purpose) => new(Profile, HrQuestion, ResumeText, FactsText, "других офферов нет", null, Today, purpose);
    public static InterviewAnswerInput InterviewAnswer(string purpose) => new(Profile, "technical", Vacancy, Question, ResumeText, FactsText, "", null, Today, purpose);
    public static AnalyzeReplyInput AnalyzeReply(string purpose) => new(Profile, new CandidateFacts(), Vacancy, "", "Можете завтра обсудить вакансию?", "", Today, "Europe/Minsk", null,
        purpose.Equals(LlmPurpose.ReplyDeep, StringComparison.OrdinalIgnoreCase));
    public static OutreachInput Outreach(string purpose) => new(Profile, Vacancy, "Анна", "Tech Recruiter", "Telegram", false);
    public static RecruiterMessageInput RecruiterMessage(string purpose) => new(Profile, Vacancy, FactsText, "Анна", "Первый отклик", "Telegram", false, "", Today,
        purpose.Equals(LlmPurpose.Application, StringComparison.OrdinalIgnoreCase), null);
    public static CheckRecruiterMessageInput CheckRecruiterMessage(string purpose) => new(Profile, FactsText, Vacancy, Message,
        purpose.Equals(LlmPurpose.ReplyDeep, StringComparison.OrdinalIgnoreCase), purpose.Equals(LlmPurpose.Application, StringComparison.OrdinalIgnoreCase));
}
