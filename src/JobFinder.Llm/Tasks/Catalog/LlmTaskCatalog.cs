using JobFinder.Llm.Contracts;
using JobFinder.Llm.Contracts.Wire;

namespace JobFinder.Llm.Tasks;

public static class LlmTaskCatalog
{
    public static readonly LlmTaskDefinition<ScoreInput, VacancyScoringWire> Score =
        new("score_vacancy", "score_vacancy", "v1", GeneratedWireJsonContext.Default.VacancyScoringWire,
            [LlmPurpose.Score], DiscoveryTaskBuilders.Score, essential: false, schema: NativeWireSchemas.VacancyScoring, probeInput: TaskProbeInputs.Score);

    public static readonly LlmTaskDefinition<PlanCommentInput, CommentPlanWire> PlanComment =
        new("threads_comment", "threads_comment", "v1", GeneratedWireJsonContext.Default.CommentPlanWire,
            [LlmPurpose.Comment], DiscoveryTaskBuilders.PlanComment, essential: false, schema: NativeWireSchemas.CommentPlan, probeInput: TaskProbeInputs.PlanComment);

    public static readonly LlmTaskDefinition<ImportResumeInput, CandidateProfileWire> ImportResume =
        new("import_resume", "import_resume", "v1", GeneratedWireJsonContext.Default.CandidateProfileWire,
            [LlmPurpose.Profile], CandidateTaskBuilders.ImportResume, essential: true, schema: NativeWireSchemas.CandidateProfile, probeInput: TaskProbeInputs.ImportResume);

    public static readonly LlmTaskDefinition<TailorResumeInput, ResumeDraftWire> TailorResume =
        new("tailor_resume", "tailor_resume", "v1", GeneratedWireJsonContext.Default.ResumeDraftWire,
            [LlmPurpose.Resume], PreparationTaskBuilders.TailorResume, essential: true, schema: NativeWireSchemas.ResumeDraft, probeInput: TaskProbeInputs.TailorResume);

    public static readonly LlmTaskDefinition<ReviewResumeInput, ResumeReviewWire> ReviewResume =
        new("resume_review", "resume_review", "v1", GeneratedWireJsonContext.Default.ResumeReviewWire,
            [LlmPurpose.Resume], PreparationTaskBuilders.ReviewResume, essential: true, schema: NativeWireSchemas.ResumeReview, probeInput: TaskProbeInputs.ReviewResume);

    public static readonly LlmTaskDefinition<ComposeStoryInput, StoryDraftWire> ComposeStory =
        new("compose_story", "compose_story", "v1", GeneratedWireJsonContext.Default.StoryDraftWire,
            [LlmPurpose.Coach], CandidateTaskBuilders.ComposeStory, essential: true, schema: NativeWireSchemas.StoryDraft, probeInput: TaskProbeInputs.ComposeStory);

    public static readonly LlmTaskDefinition<MockQuestionsInput, MockQuestionsWire> MockQuestions =
        new("mock_questions", "mock_questions", "v2", GeneratedWireJsonContext.Default.MockQuestionsWire,
            [LlmPurpose.Coach], PreparationTaskBuilders.MockQuestions, essential: true, schema: NativeWireSchemas.MockQuestions, probeInput: TaskProbeInputs.MockQuestions);

    public static readonly LlmTaskDefinition<MockReviewInput, MockReviewWire> MockReview =
        new("mock_review", "mock_review", "v1", GeneratedWireJsonContext.Default.MockReviewWire,
            [LlmPurpose.Coach], PreparationTaskBuilders.MockReview, essential: true, schema: NativeWireSchemas.MockReview, probeInput: TaskProbeInputs.MockReview);

    public static readonly LlmTaskDefinition<MockSummaryInput, MockSummaryWire> MockSummary =
        new("mock_summary", "mock_summary", "v1", GeneratedWireJsonContext.Default.MockSummaryWire,
            [LlmPurpose.Coach], PreparationTaskBuilders.MockSummary, essential: true, schema: NativeWireSchemas.MockSummary, probeInput: TaskProbeInputs.MockSummary);

    public static readonly LlmTaskDefinition<DebriefInput, DebriefResultWire> Debrief =
        new("debrief", "debrief", "v1", GeneratedWireJsonContext.Default.DebriefResultWire,
            [LlmPurpose.Coach], PreparationTaskBuilders.Debrief, essential: true, schema: NativeWireSchemas.DebriefResult, probeInput: TaskProbeInputs.Debrief);

    public static readonly LlmTaskDefinition<AnalyzeReplyInput, ReplyAnalysisWire> AnalyzeReply =
        new("reply_analysis", "reply_analysis", "v1", GeneratedWireJsonContext.Default.ReplyAnalysisWire,
            [LlmPurpose.ReplyDeep, LlmPurpose.Reply], HiringTaskBuilders.AnalyzeReply, essential: true, schema: NativeWireSchemas.ReplyAnalysis, probeInput: TaskProbeInputs.AnalyzeReply);

    public static readonly LlmTaskDefinition<ReadLinkedInPeopleInput, LinkedInPeopleWire> ReadLinkedInPeople =
        new("linkedin_people", "linkedin_people", "v1", GeneratedWireJsonContext.Default.LinkedInPeopleWire,
            [LlmPurpose.Extract], DiscoveryTaskBuilders.ReadLinkedInPeople, essential: false, schema: NativeWireSchemas.LinkedInPeople, probeInput: TaskProbeInputs.ReadLinkedInPeople);

    public static readonly LlmTextTaskDefinition<OutreachInput> Outreach =
        new("outreach", "outreach", "v1", [LlmPurpose.Letter], HiringTaskBuilders.Outreach, probeInput: TaskProbeInputs.Outreach);

    public static readonly LlmTaskDefinition<RecruiterMessageInput, RecruiterMessageWire> RecruiterMessage =
        new("recruiter_dm", "recruiter_dm", "v1", GeneratedWireJsonContext.Default.RecruiterMessageWire,
            [LlmPurpose.Application, LlmPurpose.ReplyDeep], HiringTaskBuilders.RecruiterMessage, essential: true, schema: NativeWireSchemas.RecruiterMessage, probeInput: TaskProbeInputs.RecruiterMessage);

    public static readonly LlmTaskDefinition<CheckRecruiterMessageInput, MessageCheckWire> CheckRecruiterMessage =
        new("recruiter_check", "recruiter_check", "v1", GeneratedWireJsonContext.Default.MessageCheckWire,
            [LlmPurpose.Application, LlmPurpose.ReplyDeep, LlmPurpose.Reply], HiringTaskBuilders.CheckRecruiterMessage, essential: true, schema: NativeWireSchemas.MessageCheck, probeInput: TaskProbeInputs.CheckRecruiterMessage);

    public static readonly LlmTaskDefinition<OfferInput, OfferAnalysisWire> Offer =
        new("offer_analysis", "offer_analysis", "v1", GeneratedWireJsonContext.Default.OfferAnalysisWire,
            [LlmPurpose.Resume], PreparationTaskBuilders.Offer, essential: true, schema: NativeWireSchemas.OfferAnalysis, probeInput: TaskProbeInputs.Offer);

    public static readonly LlmTaskDefinition<RejectionReviewInput, RejectionReviewWire> RejectionReview =
        new("rejection_review", "rejection_review", "v1", GeneratedWireJsonContext.Default.RejectionReviewWire,
            [LlmPurpose.Resume], PreparationTaskBuilders.RejectionReview, essential: true, schema: NativeWireSchemas.RejectionReview, probeInput: TaskProbeInputs.RejectionReview);

    public static readonly LlmTaskDefinition<HhAdviceInput, HhResumeAdviceWire> HhAdvice =
        new("hh_resume_advice", "hh_resume_advice", "v1", GeneratedWireJsonContext.Default.HhResumeAdviceWire,
            [LlmPurpose.Resume], PreparationTaskBuilders.HhAdvice, essential: false, schema: NativeWireSchemas.HhResumeAdvice, probeInput: TaskProbeInputs.HhAdvice);

    public static readonly LlmTaskDefinition<InterviewPlanInput, InterviewPlanWire> InterviewPlan =
        new("interview_plan", "interview_plan", "v1", GeneratedWireJsonContext.Default.InterviewPlanWire,
            [LlmPurpose.Interview], PreparationTaskBuilders.InterviewPlan, essential: true, schema: NativeWireSchemas.InterviewPlan, probeInput: TaskProbeInputs.InterviewPlan);

    public static readonly LlmTaskDefinition<HrAnswerInput, SpokenAnswerWire> HrAnswer =
        new("hr_answer", "hr_answer", "v1", GeneratedWireJsonContext.Default.SpokenAnswerWire,
            [LlmPurpose.Interview, LlmPurpose.InterviewHigh, LlmPurpose.InterviewMedium, LlmPurpose.InterviewLow], PreparationTaskBuilders.HrAnswer, essential: true, schema: NativeWireSchemas.SpokenAnswer, probeInput: TaskProbeInputs.HrAnswer);

    public static readonly LlmTaskDefinition<InterviewAnswerInput, SpokenAnswerWire> InterviewAnswer =
        new("interview_answer", "interview_answer", "v1", GeneratedWireJsonContext.Default.SpokenAnswerWire,
            [LlmPurpose.Interview, LlmPurpose.InterviewHigh, LlmPurpose.InterviewMedium, LlmPurpose.InterviewLow], PreparationTaskBuilders.InterviewAnswer, essential: true, schema: NativeWireSchemas.SpokenAnswer, probeInput: TaskProbeInputs.InterviewAnswer);

    public static IReadOnlyList<ILlmTaskDefinition> All { get; } = Array.AsReadOnly<ILlmTaskDefinition>(
    [
        Discovery.ExtractPostTask.Definition,
        Score,
        PlanComment,
        ImportResume,
        TailorResume,
        ReviewResume,
        ComposeStory,
        MockQuestions,
        MockReview,
        MockSummary,
        Debrief,
        AnalyzeReply,
        ReadLinkedInPeople,
        Outreach,
        RecruiterMessage,
        CheckRecruiterMessage,
        Offer,
        RejectionReview,
        HhAdvice,
        InterviewPlan,
        HrAnswer,
        InterviewAnswer,
    ]);
}
