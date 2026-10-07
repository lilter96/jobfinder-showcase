using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Profile;

namespace JobFinder.Pipeline.Career;

/// <summary>Where the words go: it decides the greeting, the attachment, the length and who reads it.</summary>
public enum LetterChannel
{
    /// <summary>The cover letter of a response on hh.ru / rabota.by: read in the response card, the resume is already there.</summary>
    Hh,

    /// <summary>A letter to the address in the vacancy, the resume attached.</summary>
    Email,

    /// <summary>A personal Telegram message: to the recruiter of the company or to the author of a vacancy post.</summary>
    Telegram,
    LinkedIn,
}

/// <param name="Contact">Who reads it ("Юлия Ботвинина (@…), hr"; "рекрутер, имя неизвестно").</param>
/// <param name="Situation">Context for the model, not phrases for the text.</param>
/// <param name="First">A first word to this person (stricter limits, a fact from the experience required when <paramref name="NeedsFact"/>).</param>
/// <param name="Deep">The user asked for it now: the check runs on the high-effort route; letters written for every scored vacancy use the regular one.</param>
public sealed record LetterBrief(Vacancy Vacancy, LetterChannel Channel, string Contact, string Situation, bool AttachesResume, string Conversation,
    bool First, bool NeedsFact, bool Deep);

/// <param name="Evidence">What in the profile the text rests on.</param>
/// <param name="Problems">What the checks still found in the best of the attempts; empty = passed.</param>
public sealed record LetterDraft(string Text, IReadOnlyList<RecruiterEvidence> Evidence, IReadOnlyList<string> Problems,
    ApplicationLetterReview? Review = null);

/// <summary>
/// Everything the candidate writes to an employer about a vacancy: the response letter (hh, e-mail), a Telegram message to a recruiter or
/// a post's author. One way for all of them: the vacancy's main need → the case of the experience that closes it (plus at most a short
/// support), each quoted from the profile → the text in parts (greeting, short paragraphs, a closing with the next step). The code puts
/// the line naming the vacancy first (title, company, link: a recruiter with twenty roles knows which one) and lays the parts out with
/// empty lines. Then code checks (a letter is not a compressed resume: stack garlands, metric chains, AI phrases, template openings and
/// closings, a wall of text) and an independent reading: every claim against the profile, and a recruiter's verdict on a first message —
/// under <see cref="MinVerdict"/> of 10 it is rewritten with their fix. The native preparation workflow owns drafting, review and
/// rewrites; what still fails is returned with its problems, never silently.
/// </summary>
public sealed class LetterWriter(JobFinderLlm llm, ProfileProvider profiles, FactsProvider facts, TimeProvider time, ILogger<LetterWriter> log,
    LetterPreparationRequests? preparations = null)
{
    /// <summary>A first message a recruiter scores below this (out of 10) is not ready to send: it is rewritten with their fix.</summary>
    public const int MinVerdict = 9;
    internal const string QualityVersion = "application-quality-v3";

    public async Task<LetterDraft?> WriteAsync(LetterBrief brief, CancellationToken ct, bool force = false)
    {
        if ((await profiles.GetSnapshotAsync(ct))?.Value is not { } profile) return null;
        var operation = await llm.ForOperationAsync(ct);
        var sheet = (await facts.GetSnapshotAsync(ct)).Value;
        return await WriteAsync(brief, profile, sheet, operation, ct, force);
    }

    public Task<LetterDraft?> WriteAsync(LetterBrief brief, CandidateProfile profile, CandidateFacts sheet,
        JobFinderLlm operation, CancellationToken ct, bool force = false, AssessmentVacancy? anchor = null)
    {
        return (preparations ?? throw new InvalidOperationException("native letter preparation is required"))
            .WriteAsync(brief, profile, sheet, operation, ct, force, anchor);
    }

    internal static Core.Artifacts.ArtifactRecipe ContextRecipe(JobFinderLlm operation, CandidateProfile profile, CandidateFacts sheet, LetterBrief brief) => operation.Recipe(
        [(LlmTaskCatalog.RecruiterMessage, brief.First ? JobFinder.Llm.LlmPurpose.Application : JobFinder.Llm.LlmPurpose.ReplyDeep),
         (LlmTaskCatalog.CheckRecruiterMessage, brief.First ? JobFinder.Llm.LlmPurpose.Application
             : brief.Deep ? JobFinder.Llm.LlmPurpose.ReplyDeep : JobFinder.Llm.LlmPurpose.Reply)],
        ContextInputs(profile, sheet, brief), new Dictionary<string, string> { ["quality"] = QualityVersion });

    internal static Core.Artifacts.ArtifactRecipe ReviewRecipe(JobFinderLlm operation, CandidateProfile profile, CandidateFacts sheet, LetterBrief brief, string text)
    {
        var inputs = ContextInputs(profile, sheet, brief);
        inputs["text"] = text;
        return operation.Recipe([(LlmTaskCatalog.CheckRecruiterMessage, brief.First ? JobFinder.Llm.LlmPurpose.Application
            : brief.Deep ? JobFinder.Llm.LlmPurpose.ReplyDeep : JobFinder.Llm.LlmPurpose.Reply)], inputs,
            new Dictionary<string, string> { ["quality"] = QualityVersion, ["exact-review"] = "v1" });
    }

    private static Dictionary<string, string?> ContextInputs(CandidateProfile profile, CandidateFacts sheet, LetterBrief brief) => new()
        {
            ["profile"] = ProfileProvider.ToJson(profile), ["facts"] = sheet.ForPrompt(),
            ["vacancy"] = JobFinderLlm.VacancyText(brief.Vacancy, withEvaluation: true), ["url"] = brief.Vacancy.Url,
            ["channel"] = brief.Channel.ToString(), ["contact"] = brief.Contact, ["situation"] = brief.Situation,
            ["first"] = brief.First.ToString(), ["needsFact"] = brief.NeedsFact.ToString(), ["attachment"] = brief.AttachesResume.ToString(),
            ["conversation"] = brief.Conversation,
        };

    public static bool Matches(ApplicationLetterReview? report, string text, string contextHash) => report is { Score: >= MinVerdict and <= 10, Problems.Count: 0 }
        && report.TextHash == Fingerprint.Of(text) && report.ContextHash == contextHash;

    public async Task<ApplicationLetterReview> ReviewResponseAsync(Vacancy vacancy, ApplicationChannel channel, string? target, string text,
        CancellationToken ct)
        => await ReviewMessageAsync(ResponseBrief(vacancy, channel, target), text, ct);

    public async Task<ApplicationLetterReview> ReviewMessageAsync(LetterBrief brief, string text, CancellationToken ct)
    {
        var ticket = await RequestReviewAsync(brief, text, ct);
        if (ticket.Review is not null) return ticket.Review;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await GetReviewAsync(ticket.RequestId ?? throw new InvalidOperationException("missing review dependency"), ct) is { } review) return review;
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    public async Task<LetterReviewTicket> RequestReviewAsync(LetterBrief brief, string text, CancellationToken ct)
    {
        var operation = await llm.ForOperationAsync(ct);
        var profile = (await profiles.GetSnapshotAsync(ct))?.Value ?? throw new InvalidOperationException("нет профиля кандидата");
        if (!profile.Reviewed) throw new LetterPreparationSupersededException("reviewed candidate profile is required");
        var sheet = (await facts.GetSnapshotAsync(ct)).Value;
        return await RequestReviewAsync(brief, text, profile, sheet, operation, ct);
    }

    internal async Task<LetterReviewTicket> RequestReviewAsync(LetterBrief brief, string text, CandidateProfile profile, CandidateFacts sheet,
        JobFinderLlm operation, CancellationToken ct)
    {
        if (!profile.Reviewed) throw new LetterPreparationSupersededException("reviewed candidate profile is required");
        if (brief.Vacancy.LetterReview is { Score: >= 0 and <= 10 } cached
            && cached.TextHash == Fingerprint.Of(text) && cached.ContextHash == ContextRecipe(operation, profile, sheet, brief).Hash)
            return new(null, brief.Vacancy.LetterReview);
        var ticket = await (preparations ?? throw new InvalidOperationException("native letter preparation is required"))
            .RequestReviewAsync(brief, text, profile, sheet, operation, ct);
        log.Debug($"[letter] #{brief.Vacancy.Id}: exact-text review request {ticket.RequestId}");
        return new(ticket.RequestId, ticket.Result is null ? null : await GetReviewAsync(ticket.RequestId, brief, text, ct));
    }

    public async Task<ApplicationLetterReview?> GetReviewAsync(Guid id, CancellationToken ct)
    {
        return await (preparations ?? throw new InvalidOperationException("native letter preparation is required")).GetReviewAsync(id, null, null, ct);
    }

    public Task<ApplicationLetterReview?> GetReviewAsync(Guid id, LetterBrief brief, string text, CancellationToken ct) =>
        (preparations ?? throw new InvalidOperationException("native letter preparation is required")).GetReviewAsync(id, brief, text, ct);

    /// <summary>
    /// The first word of a response (hh form, e-mail, a message to the author of a vacancy post): no conversation, no earlier response.
    /// </summary>
    public Task<LetterDraft?> ResponseAsync(Vacancy v, ApplicationChannel channel, string? target, CancellationToken ct, bool force = false)
    {
        return WriteAsync(ResponseBrief(v, channel, target), ct, force);
    }

    public Task<LetterDraft?> ResponseAsync(Vacancy v, ApplicationChannel channel, string? target, CandidateProfile profile,
        CandidateFacts sheet, JobFinderLlm operation, CancellationToken ct, bool force = false, AssessmentVacancy? anchor = null)
    {
        return WriteAsync(ResponseBrief(v, channel, target), profile, sheet, operation, ct, force, anchor);
    }

    public Task<LetterPreparationTicket> RequestResponseAsync(Vacancy vacancy, ApplicationChannel channel, string? target, CandidateProfile profile,
        CandidateFacts sheet, JobFinderLlm operation, CancellationToken ct, bool force = false, AssessmentVacancy? anchor = null)
    {
        return (preparations ?? throw new InvalidOperationException("native letter preparation is required"))
            .RequestAsync(ResponseBrief(vacancy, channel, target), profile, sheet, operation, ct, force, anchor);
    }

    internal static LetterBrief ResponseBrief(Vacancy v, ApplicationChannel channel, string? target)
    {
        var letterChannel = channel switch
        {
            ApplicationChannel.Email => LetterChannel.Email,
            ApplicationChannel.TelegramDm => LetterChannel.Telegram,
            _ => LetterChannel.Hh,
        };
        // The post names its author more often than not ("Valentina (@CodeVal)"): a greeting by name reads as written to a person.
        var label = target is null ? null
            : v.Contacts.FirstOrDefault(c => string.Equals(c.Value, target, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Label))?.Label;
        var contact = letterChannel switch
        {
            LetterChannel.Telegram => label is null ? $"автор поста с вакансией ({target}), имя неизвестно" : $"{label}, автор поста с вакансией",
            LetterChannel.Email => label is null ? $"рекрутер по адресу {target}, имя неизвестно" : $"{label}, рекрутер ({target})",
            _ => "рекрутер или HR компании, имя неизвестно",
        };
        return new LetterBrief(v, letterChannel, contact,
            "Отклика ещё нет: это первое слово кандидата по этой вакансии и сам отклик.", letterChannel != LetterChannel.Hh, "(переписки нет)",
            First: true, NeedsFact: true, Deep: false);
    }

    /// <summary>"Гацуков Терентий Алексеевич" → "Терентий": a letter is signed with the first name (the profile keeps "surname name patronymic").</summary>
    public static string FirstName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : fullName.Trim();
    }
}
