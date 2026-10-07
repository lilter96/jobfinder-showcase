using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Engagement;

namespace JobFinder.Pipeline.Workers;

/// <summary>
/// Threads comment drafts: the model writes three variants, <see cref="CommentStyle"/> checks them (length, lecturing, stack forced into a
/// non-technical thread, a question to the author), and a set that fails is rewritten once with the exact remarks. What still fails is
/// dropped; a post with no clean variant is not offered at all.
/// </summary>
public sealed partial class CommentPlanner(CommentPreparationRequests requests)
{
    public static HashSet<string> ExamplesOf(string prompt)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in GoodExample().Matches(prompt)) set.UnionWith(CommentStyle.Shingles(m.Groups[1].Value));
        return set;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Хорошо:\s*«(.+)»\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex GoodExample();

    /// <summary>A post is worth a model call: Russian, and the post itself rather than a reply inside a thread.</summary>
    public static bool IsCandidate(string postText) => CommentStyle.IsRussian(postText) && !CommentStyle.IsReply(postText);

    /// <returns>The plan with only clean variants, and the remarks the first draft got (empty when it passed as is).</returns>
    public async Task<(CommentPlan Plan, List<string> Remarks)> PlanAsync(CandidateProfile profile, string author, string postText, CancellationToken ct, bool force = false)
    {
        var id = await requests.RequestAsync(profile, author, postText, ct, force);
        var result = await requests.WaitAsync(id, ct);
        return (result.Plan, result.Remarks);
    }

    public static (List<string> Good, List<string> Remarks) Review(CommentPlan plan, IReadOnlySet<string>? exampleShingles = null)
    {
        var technical = plan.Audience == "tech";
        var variants = plan.Variants.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Take(3).ToList();
        var good = new List<string>();
        var remarks = new List<string>();
        for (var i = 0; i < variants.Count; i++)
        {
            var problems = CommentStyle.Problems(variants[i], technical);
            if (exampleShingles is not null && CommentStyle.CopiesExample(variants[i], exampleShingles)) problems.Add("повторяет образец из промпта — нужна своя фраза");
            if (problems.Count == 0) good.Add(variants[i]);
            else remarks.Add($"вариант {i + 1}: {string.Join(", ", problems)}");
        }

        remarks.AddRange(CommentStyle.SetProblems(variants));
        return (good, remarks);
    }
}
