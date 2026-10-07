using System.Globalization;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm.Client;

namespace JobFinder.Llm.Tasks;

/// <summary>The career-stage operations: stories, practice interviews, interview packs, replies, follow-ups, outreach, offers, hh advice.</summary>
public sealed partial class JobFinderLlm
{
    public Task<LlmResult<StoryDraft>> ComposeStoryAsync(string achievement, string answers, string resumeText, CancellationToken ct)
    {
        return LlmTaskCatalog.ComposeStory.ExecuteAsync(client, prompts,
            new ComposeStoryInput(achievement, answers, resumeText), ct).MapAsync(static wire => wire.ToDomain());
    }

    public Task<LlmResult<MockQuestions>> MockQuestionsAsync(CandidateProfile profile, string kind, int count, Vacancy? vacancy, string storiesText,
        CancellationToken ct)
    {
        return LlmTaskCatalog.MockQuestions.ExecuteAsync(client, prompts,
            new MockQuestionsInput(profile, kind, count, vacancy, storiesText), ct).MapAsync(static wire => wire.ToDomain());
    }

    public Task<LlmResult<MockReview>> MockReviewAsync(string question, string ideal, string answer, CancellationToken ct)
    {
        return LlmTaskCatalog.MockReview.ExecuteAsync(client, prompts,
            new MockReviewInput(question, ideal, answer), ct).MapAsync(static wire => wire.ToDomain());
    }

    public Task<LlmResult<MockSummary>> MockSummaryAsync(string session, CancellationToken ct)
    {
        return LlmTaskCatalog.MockSummary.ExecuteAsync(client, prompts,
            new MockSummaryInput(session), ct).MapAsync(static wire => wire.ToDomain());
    }

    public Task<LlmResult<DebriefResult>> DebriefAsync(string interview, string story, CancellationToken ct)
    {
        return LlmTaskCatalog.Debrief.ExecuteAsync(client, prompts,
            new DebriefInput(interview, story), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <param name="stories">The confirmed story bank (STAR), the first source for technical and open questions.</param>
    /// <param name="remarks">Why the previous draft was rejected by the style check, with that draft: the model rewrites it.</param>
    /// <param name="deep">The high-effort route (<see cref="LlmPurpose.ReplyDeep"/>) for an answer that needs thought.</param>
    public Task<LlmResult<ReplyAnalysis>> AnalyzeReplyAsync(CandidateProfile profile, CandidateFacts facts, Vacancy? vacancy, string conversation,
        string message, string stories, DateTimeOffset now, string timeZone, CancellationToken ct, string? remarks = null, bool deep = false)
    {
        return LlmTaskCatalog.AnalyzeReply.ExecuteAsync(client, prompts,
            new AnalyzeReplyInput(profile, facts, vacancy, conversation, message, stories, now, timeZone, remarks, deep), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <param name="blocks">"[N]" + the text around the N-th profile link of a LinkedIn people-search page.</param>
    public Task<LlmResult<LinkedInPeople>> ReadLinkedInPeopleAsync(string company, string blocks, CancellationToken ct)
    {
        return LlmTaskCatalog.ReadLinkedInPeople.ExecuteAsync(client, prompts,
            new ReadLinkedInPeopleInput(company, blocks), ct).MapAsync(static wire => wire.ToDomain());
    }

    public Task<LlmResult<string>> OutreachAsync(CandidateProfile profile, Vacancy vacancy, string contact, string role, string channel,
        CancellationToken ct, bool essential = false)
    {
        return LlmTaskCatalog.Outreach.ExecuteAsync(client, prompts,
            new OutreachInput(profile, vacancy, contact, role, channel, essential), ct);
    }

    /// <summary>
    /// A message to the company's recruiter in Telegram for the situation the code describes (no response yet, waiting, rejected, an
    /// interview, a conversation going on), with the whole conversation of the response as context.
    /// </summary>
    /// <param name="remarks">What the style check found in the previous variant.</param>
    /// <param name="channel">Where the text goes and what that implies (a Telegram message, a response letter on hh, an e-mail).</param>
    /// <param name="first">The first word about this vacancy (the response itself): the maximum-effort route.</param>
    public Task<LlmResult<RecruiterMessage>> RecruiterMessageAsync(CandidateProfile profile, Vacancy vacancy, string facts, string contact, string situation,
        string channel, bool attachesResume, string conversation, DateTimeOffset now, bool first, CancellationToken ct, string? remarks = null)
    {
        return LlmTaskCatalog.RecruiterMessage.ExecuteAsync(client, prompts,
            new RecruiterMessageInput(profile, vacancy, facts, contact, situation, channel, attachesResume, conversation, now, first, remarks), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <summary>
    /// An independent reading of a finished message to a recruiter: every claim about the candidate against the profile (with the quote
    /// the code then looks up), the number of project cases, and the "without the title and the stack" naturalness test.
    /// </summary>
    /// <param name="deep">A message the user asked for (the high-effort route); letters written for every scored vacancy use the regular one.</param>
    public Task<LlmResult<MessageCheck>> CheckRecruiterMessageAsync(CandidateProfile profile, string facts, Vacancy vacancy, string message, bool deep,
        CancellationToken ct, bool first = false)
    {
        return LlmTaskCatalog.CheckRecruiterMessage.ExecuteAsync(client, prompts,
            new CheckRecruiterMessageInput(profile, facts, vacancy, message, deep, first), ct).MapAsync(static wire => wire.ToDomain());
    }

    public Task<LlmResult<OfferAnalysis>> OfferAsync(CandidateProfile profile, string offer, Vacancy? vacancy, string market, string otherOffers,
        CancellationToken ct)
    {
        return LlmTaskCatalog.Offer.ExecuteAsync(client, prompts,
            new OfferInput(profile, offer, vacancy, market, otherOffers), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <param name="facts">The computed groups, keyword gaps, rejection speed and one line per response (<c>RejectionReviewService</c>).</param>
    public Task<LlmResult<RejectionReview>> RejectionReviewAsync(CandidateProfile profile, string resumeText, string facts, DateTimeOffset now,
        CancellationToken ct)
    {
        return LlmTaskCatalog.RejectionReview.ExecuteAsync(client, prompts,
            new RejectionReviewInput(profile, resumeText, facts, now), ct).MapAsync(static wire => wire.ToDomain());
    }

    public Task<LlmResult<HhResumeAdvice>> HhAdviceAsync(string resumeText, string demand, string funnel, CancellationToken ct)
    {
        return LlmTaskCatalog.HhAdvice.ExecuteAsync(client, prompts,
            new HhAdviceInput(resumeText, demand, funnel), ct).MapAsync(static wire => wire.ToDomain());
    }
}
