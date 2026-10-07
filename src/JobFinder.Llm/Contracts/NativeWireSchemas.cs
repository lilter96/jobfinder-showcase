using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts.Wire;

namespace JobFinder.Llm.Contracts;

public static class NativeWireSchemas
{
    public static readonly JsonSchemaSpec VacancyScoring = WireSchema.Create("vacancy_scoring", GeneratedWireJsonContext.Default.VacancyScoringWire);

    public static readonly JsonSchemaSpec CommentPlan = WireSchema.Create("comment_plan", GeneratedWireJsonContext.Default.CommentPlanWire);

    public static readonly JsonSchemaSpec ResumeDraft = WireSchema.Create("tailored_resume", GeneratedWireJsonContext.Default.ResumeDraftWire);

    public static readonly JsonSchemaSpec ResumeReview = WireSchema.Create("resume_review", GeneratedWireJsonContext.Default.ResumeReviewWire);

    public static readonly JsonSchemaSpec CandidateProfile = WireSchema.Create("candidate_profile", GeneratedWireJsonContext.Default.CandidateProfileWire);

    public static readonly JsonSchemaSpec StoryDraft = WireSchema.Create("story", GeneratedWireJsonContext.Default.StoryDraftWire);

    public static readonly JsonSchemaSpec MockQuestions = WireSchema.Create("mock_questions", GeneratedWireJsonContext.Default.MockQuestionsWire);

    public static readonly JsonSchemaSpec MockReview = WireSchema.Create("mock_review", GeneratedWireJsonContext.Default.MockReviewWire);

    public static readonly JsonSchemaSpec MockSummary = WireSchema.Create("mock_summary", GeneratedWireJsonContext.Default.MockSummaryWire);

    public static readonly JsonSchemaSpec DebriefResult = WireSchema.Create("debrief", GeneratedWireJsonContext.Default.DebriefResultWire);

    public static readonly JsonSchemaSpec ReplyAnalysis = WireSchema.Create("reply_analysis", GeneratedWireJsonContext.Default.ReplyAnalysisWire);

    public static readonly JsonSchemaSpec OfferAnalysis = WireSchema.Create("offer_analysis", GeneratedWireJsonContext.Default.OfferAnalysisWire);

    public static readonly JsonSchemaSpec LinkedInPeople = WireSchema.Create("linkedin_people", GeneratedWireJsonContext.Default.LinkedInPeopleWire);

    public static readonly JsonSchemaSpec RecruiterMessage = WireSchema.Create("recruiter_message", GeneratedWireJsonContext.Default.RecruiterMessageWire);

    public static readonly JsonSchemaSpec MessageCheck = WireSchema.Create("recruiter_check", GeneratedWireJsonContext.Default.MessageCheckWire);

    public static readonly JsonSchemaSpec RejectionReview = WireSchema.Create("rejection_review", GeneratedWireJsonContext.Default.RejectionReviewWire);

    public static readonly JsonSchemaSpec HhResumeAdvice = WireSchema.Create("hh_resume_advice", GeneratedWireJsonContext.Default.HhResumeAdviceWire);

    public static readonly JsonSchemaSpec InterviewPlan = WireSchema.Create("interview_plan", GeneratedWireJsonContext.Default.InterviewPlanWire);

    public static readonly JsonSchemaSpec SpokenAnswer = WireSchema.Create("interview_answer", GeneratedWireJsonContext.Default.SpokenAnswerWire);
}
