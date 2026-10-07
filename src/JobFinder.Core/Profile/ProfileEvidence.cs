using System.Text;
using System.Text.RegularExpressions;
using JobFinder.Core.Text;

namespace JobFinder.Core.Profile;

/// <summary>
/// What the candidate's words may rest on: the profile's own statements (summary, jobs, achievements, skills, domains, location, formats)
/// and the confirmed facts. A quote a model gives as support must be a real part of it, checked by code, not by trust.
/// </summary>
public static partial class ProfileEvidence
{
    public static string Text(CandidateProfile profile, CandidateFacts? facts = null)
    {
        var sb = new StringBuilder();
        void Add(string? s)
        {
            if (!string.IsNullOrWhiteSpace(s)) sb.Append(s).Append('\n');
        }

        Add(profile.Title);
        Add(profile.Summary);
        foreach (var a in profile.Achievements) Add(a);
        foreach (var j in profile.Experience) Add($"{j.Company}, {j.Role}, {j.Period}: {j.Highlights}");
        Add(string.Join(", ", profile.Skills.Select(s => s.Name)));
        Add(string.Join(", ", profile.Domains));
        Add(profile.Location);
        Add(string.Join(", ", profile.WorkFormats));
        foreach (var f in facts?.Facts.Where(f => f.Confirmed && !string.IsNullOrWhiteSpace(f.Answer)) ?? []) Add($"{f.Question}: {f.Answer}");
        return sb.ToString();
    }

    /// <summary>
    /// The quote is a real fragment of the evidence: the same words in the same order, case, punctuation and quotes aside. Too short a
    /// quote ("C#", "Redis") proves nothing and does not count.
    /// </summary>
    /// <param name="minWords">3 for experience (one word proves nothing); 1 for logistics, where the value is the proof ("Минск").</param>
    public static bool Contains(string evidence, string? quote, int minWords = 3)
    {
        var q = Plain(quote);
        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= minWords && $" {Plain(evidence)} ".Contains($" {q} ", StringComparison.Ordinal);
    }

    private static string Plain(string? text) => Spaces().Replace(NotWord().Replace(TextUtil.Normalize(text), " "), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
