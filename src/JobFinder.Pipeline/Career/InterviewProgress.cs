using System.Globalization;
using JobFinder.Pipeline.Modules.Preparation;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// What the candidate has learned of a preparation: per question "known" or "again", and which flagged answers they checked. A scope is
/// one interview (<c>interview.{id}</c>) or the HR document (<c>hr</c>); the data lives in <c>settings</c> as <c>{scope}.progress</c> and
/// <c>{scope}.approved</c>. The dashboard marks by hand; a rehearsal in the bot marks by the review score.
/// </summary>
public sealed class InterviewProgress(LearningProgressSources sources)
{
    public const string Known = "known";
    public const string Again = "again";
    public const string HrScope = "hr";

    /// <summary>A rehearsal answer scored this or higher (of 5) counts as learned.</summary>
    public const int KnownScore = 4;

    public static string Scope(long interviewId) => $"interview.{interviewId.ToString(CultureInfo.InvariantCulture)}";

    public Task<Dictionary<string, string>> GetAsync(long interviewId, CancellationToken ct) => GetAsync(Scope(interviewId), ct);
    public Task<LearningProgressSnapshot> CaptureAsync(string scope, CancellationToken ct) => sources.ReadAsync(scope, ct);
    public Task<LearningPublication> MarkAsync(LearningItemSnapshot expected, string state, CancellationToken ct) => sources.MarkAsync(expected, state, ct);
    public Task<LearningPublication> ApproveAsync(LearningItemSnapshot expected, bool approved, CancellationToken ct) => sources.ApproveAsync(expected, approved, ct);
    public Task<LearningPublication> ResetAsync(LearningProgressSnapshot expected, CancellationToken ct) => sources.ResetAsync(expected, ct);

    public async Task<Dictionary<string, string>> GetAsync(string scope, CancellationToken ct)
    {
        return (await CaptureAsync(scope, ct)).Learned;
    }

    /// <summary>Answers the user checked (the ⚠️ of the guardrail cleared by hand).</summary>
    public async Task<HashSet<string>> ApprovedAsync(string scope, CancellationToken ct)
    {
        return (await CaptureAsync(scope, ct)).Approved;
    }

    /// <summary>The rehearsal turn's link back to its question: "{scope}#{questionId}".</summary>
    public static string ItemKey(string scope, string questionId) => $"{scope}#{questionId}";

    public static bool TryParse(string? key, out string scope, out string questionId)
    {
        scope = "";
        questionId = "";
        var hash = key?.IndexOf('#') ?? -1;
        if (hash <= 0 || hash == key!.Length - 1) return false;
        (scope, questionId) = (key[..hash], key[(hash + 1)..]);
        return true;
    }
}
