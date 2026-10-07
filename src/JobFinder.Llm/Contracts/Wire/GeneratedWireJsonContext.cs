using System.Text.Json.Serialization;

namespace JobFinder.Llm.Contracts.Wire;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(VacancyScoringWire))]
[JsonSerializable(typeof(CommentPlanWire))]
[JsonSerializable(typeof(ResumeDraftWire))]
[JsonSerializable(typeof(ResumeReviewWire))]
[JsonSerializable(typeof(CandidateProfileWire))]
[JsonSerializable(typeof(StoryDraftWire))]
[JsonSerializable(typeof(MockQuestionsWire))]
[JsonSerializable(typeof(MockReviewWire))]
[JsonSerializable(typeof(MockSummaryWire))]
[JsonSerializable(typeof(DebriefResultWire))]
[JsonSerializable(typeof(ReplyAnalysisWire))]
[JsonSerializable(typeof(OfferAnalysisWire))]
[JsonSerializable(typeof(LinkedInPeopleWire))]
[JsonSerializable(typeof(RecruiterMessageWire))]
[JsonSerializable(typeof(MessageCheckWire))]
[JsonSerializable(typeof(RejectionReviewWire))]
[JsonSerializable(typeof(HhResumeAdviceWire))]
[JsonSerializable(typeof(InterviewPlanWire))]
[JsonSerializable(typeof(SpokenAnswerWire))]
public sealed partial class GeneratedWireJsonContext : JsonSerializerContext;
