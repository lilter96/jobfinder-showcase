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

public sealed record TailorResumeInput(string ResumeText, string StoriesText, string Facts, string Implied, Vacancy Vacancy, IReadOnlyCollection<string> Keywords, string Language, string? Feedback, Core.Resume.ResumeDocument? Current);

public sealed record ReviewResumeInput(Vacancy Vacancy, string PdfText, string ResumeText, string StoriesText, string Facts, string Implied);

public sealed record MockQuestionsInput(CandidateProfile Profile, string Kind, int Count, Vacancy? Vacancy, string StoriesText);

public sealed record MockReviewInput(string Question, string Ideal, string Answer);

public sealed record MockSummaryInput(string Session);

public sealed record DebriefInput(string Interview, string Story);

public sealed record OfferInput(CandidateProfile Profile, string Offer, Vacancy? Vacancy, string Market, string OtherOffers);

public sealed record RejectionReviewInput(CandidateProfile Profile, string ResumeText, string Facts, DateTimeOffset Now);

public sealed record HhAdviceInput(string ResumeText, string Demand, string Funnel);

public sealed record InterviewPlanInput(CandidateProfile Profile, string Kind, Vacancy Vacancy, string Company, string Evidence, string Facts, string Conversation, string Market, string Notes, DateTimeOffset Now);

public sealed record HrAnswerInput(CandidateProfile Profile, HrQuestion Question, string Evidence, string Facts, string Funnel, string? Remarks, DateTimeOffset Now, string Purpose);

public sealed record InterviewAnswerInput(CandidateProfile Profile, string Kind, Vacancy Vacancy, PlannedQuestion Question, string Evidence, string Facts, string Conversation, string? Remarks, DateTimeOffset Now, string Purpose);

internal static class PreparationTaskBuilders
{
    public static LlmTaskPayload TailorResume(TailorResumeInput input)
    {
        var resumeText = input.ResumeText;
        var storiesText = input.StoriesText;
        var facts = input.Facts;
        var implied = input.Implied;
        var vacancy = input.Vacancy;
        var keywords = input.Keywords;
        var language = input.Language;
        var feedback = input.Feedback;
        var current = input.Current;
        var bindings = new Dictionary<string, string>
        {
            ["language"] = language,
            ["implied"] = string.IsNullOrWhiteSpace(implied) ? "(нет)" : implied,
            ["keywords"] = keywords.Count > 0 ? string.Join(", ", keywords) : "(не выделены — возьми из текста вакансии)",
        };
        var user = new StringBuilder()
            .Append("<resume>\n").Append(TextUtil.Truncate(resumeText, 20000)).Append("\n</resume>\n\n")
            .Append("<stories>\n").Append(string.IsNullOrWhiteSpace(storiesText) ? "(нет подтверждённых историй)" : TextUtil.Truncate(storiesText, 12000))
            .Append("\n</stories>\n\n")
            .Append("<facts>\n").Append(string.IsNullOrWhiteSpace(facts) ? "(нет подтверждённых фактов)" : facts).Append("\n</facts>\n\n")
            .Append(VacancyText(vacancy));
        if (current is not null)
            user.Append("\n\n<current>\n").Append(JsonSerializer.Serialize(current, Core.Resume.ResumeJsonContext.Default.ResumeDocument)).Append("\n</current>\n")
                .Append("Это текущая версия. Улучши её: исправь всё, что названо в замечаниях ниже, а то, что в них не названо и уже хорошо, оставь как есть. ")
                .Append("Факты — только из <resume>, <stories> и <facts>. Замечание, которое противоречит тексту или фактам, не выполняй.");
        if (!string.IsNullOrWhiteSpace(feedback))
            user.Append(current is null
                    ? "\n\nПРЕДЫДУЩАЯ ВЕРСИЯ НЕ ПРОШЛА ПРОВЕРКУ. Напиши заново, исправив всё это (факты — только из <resume> и <stories>):\n"
                    : "\n\nЗАМЕЧАНИЯ:\n")
                .Append(feedback);
        return new LlmTaskPayload(LlmPurpose.Resume, bindings, user.ToString());
    }

    public static LlmTaskPayload ReviewResume(ReviewResumeInput input)
    {
        var vacancy = input.Vacancy;
        var pdfText = input.PdfText;
        var resumeText = input.ResumeText;
        var storiesText = input.StoriesText;
        var facts = input.Facts;
        var implied = input.Implied;
        var bindings = new Dictionary<string, string>();
        var user = $"{VacancyText(vacancy)}\n\n<resume_pdf>\n{TextUtil.Truncate(pdfText, 16000)}\n</resume_pdf>\n\n" +
                   $"<facts>\n{facts}\n</facts>\n\n<source>\n{TextUtil.Truncate(resumeText, 30000)}\n\n{TextUtil.Truncate(storiesText, 12000)}\n</source>\n\n" +
                   $"<implied>\n{implied}\n</implied>";
        return new LlmTaskPayload(LlmPurpose.Resume, bindings, user);
    }

    public static LlmTaskPayload MockQuestions(MockQuestionsInput input)
    {
        var profile = input.Profile;
        var kind = input.Kind;
        var count = input.Count;
        var vacancy = input.Vacancy;
        var storiesText = input.StoriesText;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
            ["kind"] = kind,
            ["count"] = count.ToString(CultureInfo.InvariantCulture),
        };
        var user = (vacancy is null ? "<vacancy>(общая подготовка Senior .NET Backend)</vacancy>" : VacancyText(vacancy)) +
                   $"\n\n<stories>\n{TextUtil.Truncate(storiesText, 6000)}\n</stories>";
        return new LlmTaskPayload(LlmPurpose.Coach, bindings, user);
    }

    public static LlmTaskPayload MockReview(MockReviewInput input)
    {
        var question = input.Question;
        var ideal = input.Ideal;
        var answer = input.Answer;
        var bindings = new Dictionary<string, string>();
        var user = $"<question>\n{question}\n</question>\n<ideal>\n{ideal}\n</ideal>\n<answer>\n{TextUtil.Truncate(answer, 6000)}\n</answer>";
        return new LlmTaskPayload(LlmPurpose.Coach, bindings, user);
    }

    public static LlmTaskPayload MockSummary(MockSummaryInput input)
    {
        var session = input.Session;
        var bindings = new Dictionary<string, string>();
        return new LlmTaskPayload(LlmPurpose.Coach, bindings, $"<session>\n{TextUtil.Truncate(session, 16000)}\n</session>");
    }

    public static LlmTaskPayload Debrief(DebriefInput input)
    {
        var interview = input.Interview;
        var story = input.Story;
        var bindings = new Dictionary<string, string>();
        var user = $"<interview>\n{interview}\n</interview>\n\n<story>\n{TextUtil.Truncate(story, 8000)}\n</story>";
        return new LlmTaskPayload(LlmPurpose.Coach, bindings, user);
    }

    public static LlmTaskPayload Offer(OfferInput input)
    {
        var profile = input.Profile;
        var offer = input.Offer;
        var vacancy = input.Vacancy;
        var market = input.Market;
        var otherOffers = input.OtherOffers;
        var bindings = new Dictionary<string, string> { ["profile"] = ProfileText(profile) };
        var user = $"<offer>\n{TextUtil.Truncate(offer, 8000)}\n</offer>\n\n" +
                   (vacancy is null ? "<vacancy>(не привязан)</vacancy>" : VacancyText(vacancy)) +
                   $"\n\n<market>\n{market}\n</market>\n\n<other_offers>\n{otherOffers}\n</other_offers>";
        return new LlmTaskPayload(LlmPurpose.Resume, bindings, user);
    }

    public static LlmTaskPayload RejectionReview(RejectionReviewInput input)
    {
        var profile = input.Profile;
        var resumeText = input.ResumeText;
        var facts = input.Facts;
        var now = input.Now;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
            ["today"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var user = $"<resume>\n{TextUtil.Truncate(resumeText, 12000)}\n</resume>\n\n<responses>\n{TextUtil.Truncate(facts, 30000)}\n</responses>";
        return new LlmTaskPayload(LlmPurpose.Resume, bindings, user);
    }

    public static LlmTaskPayload HhAdvice(HhAdviceInput input)
    {
        var resumeText = input.ResumeText;
        var demand = input.Demand;
        var funnel = input.Funnel;
        var bindings = new Dictionary<string, string>();
        var user = $"<resume>\n{TextUtil.Truncate(resumeText, 16000)}\n</resume>\n\n<demand>\n{demand}\n</demand>\n\n<funnel>\n{funnel}\n</funnel>";
        return new LlmTaskPayload(LlmPurpose.Resume, bindings, user);
    }

    public static LlmTaskPayload InterviewPlan(InterviewPlanInput input)
    {
        var profile = input.Profile;
        var kind = input.Kind;
        var vacancy = input.Vacancy;
        var company = input.Company;
        var evidence = input.Evidence;
        var facts = input.Facts;
        var conversation = input.Conversation;
        var market = input.Market;
        var notes = input.Notes;
        var now = input.Now;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
            ["kind"] = kind,
            ["today"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var user = $"{VacancyText(vacancy, withEvaluation: true)}\n\n<company>\n{TextUtil.Truncate(company, 10000)}\n</company>\n\n" +
                   $"<evidence>\n{TextUtil.Truncate(evidence, 14000)}\n</evidence>\n\n<facts>\n{facts}\n</facts>\n\n" +
                   $"<conversation>\n{TextUtil.Truncate(conversation, 6000)}\n</conversation>\n\n<market>\n{market}\n</market>\n\n<notes>\n{notes}\n</notes>";
        return new LlmTaskPayload(LlmPurpose.Interview, bindings, user);
    }

    public static LlmTaskPayload HrAnswer(HrAnswerInput input)
    {
        var profile = input.Profile;
        var question = input.Question;
        var evidence = input.Evidence;
        var facts = input.Facts;
        var funnel = input.Funnel;
        var remarks = input.Remarks;
        var now = input.Now;
        var purpose = input.Purpose;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
            ["today"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var user = $"<question>\n{JsonSerializer.Serialize(question, InterviewJsonContext.Default.HrQuestion)}\n</question>\n\n" +
                   $"<evidence>\n{TextUtil.Truncate(evidence, 12000)}\n</evidence>\n\n<facts>\n{facts}\n</facts>\n\n<funnel>\n{funnel}\n</funnel>";
        if (remarks is not null) user += $"\n\n<remarks>\n{remarks}\n</remarks>";
        return new LlmTaskPayload(purpose, bindings, user);
    }

    public static LlmTaskPayload InterviewAnswer(InterviewAnswerInput input)
    {
        var profile = input.Profile;
        var kind = input.Kind;
        var vacancy = input.Vacancy;
        var question = input.Question;
        var evidence = input.Evidence;
        var facts = input.Facts;
        var conversation = input.Conversation;
        var remarks = input.Remarks;
        var now = input.Now;
        var purpose = input.Purpose;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
            ["kind"] = kind,
            ["today"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var brief = $"<vacancy>\nНазвание: {vacancy.Title}\nКомпания: {vacancy.CompanyName}\n{TextUtil.Truncate(vacancy.Description, 5000)}\n</vacancy>";
        var user = $"{brief}\n\n<question>\n{JsonSerializer.Serialize(question, InterviewJsonContext.Default.PlannedQuestion)}\n</question>\n\n" +
                   $"<evidence>\n{TextUtil.Truncate(evidence, 12000)}\n</evidence>\n\n<facts>\n{facts}\n</facts>\n\n" +
                   $"<conversation>\n{TextUtil.Truncate(conversation, 4000)}\n</conversation>";
        if (remarks is not null) user += $"\n\n<remarks>\n{remarks}\n</remarks>";
        return new LlmTaskPayload(purpose, bindings, user);
    }
}
