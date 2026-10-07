using JobFinder.Core.Artifacts;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Workers;

namespace JobFinder.Pipeline.Modules.Engagement;

public interface ICommentPreparationSteps
{
    Task<CommentPlan> DraftAsync(CommentPreparationInput input, string? remarks, CancellationToken ct);
    CommentPreparationRound Check(CommentPreparationInput input, CommentPlan plan);
}

public sealed class CommentPreparationSteps(JobFinderLlm llm) : ICommentPreparationSteps
{
    public static ArtifactRecipe Recipe(JobFinderLlm operation, CommentPreparationInput input) => operation.Recipe(
        [(LlmTaskCatalog.PlanComment, LlmPurpose.Comment)],
        new Dictionary<string, string?> { ["profile"] = ProfileProvider.ToJson(input.Profile), ["author"] = input.Author, ["post"] = input.PostText },
        new Dictionary<string, string> { ["style"] = "comment-style-v2" });

    private JobFinderLlm Operation(CommentPreparationInput input)
    {
        var operation = llm.RestoreOperation(input.Operation);
        if (input.Version != 1 || input.Recipe.OperationId != input.Operation.OperationId || input.Recipe.PolicyRevision != input.Operation.PolicyRevision
            || Recipe(operation, input).Hash != input.Recipe.Hash) throw new CommentPreparationSupersededException();
        return operation;
    }

    public async Task<CommentPlan> DraftAsync(CommentPreparationInput input, string? remarks, CancellationToken ct)
    {
        var plan = (await Operation(input).PlanCommentAsync(input.Profile, input.Author, input.PostText, ct, remarks)).Value;
        return plan with { Variants = plan.Variants.Select(HumanText.Clean).ToList() };
    }

    public CommentPreparationRound Check(CommentPreparationInput input, CommentPlan plan)
    {
        _ = Operation(input);
        if (!plan.WorthCommenting) return new() { Plan = plan };
        var (good, remarks) = CommentPlanner.Review(plan, input.ExampleShingles.ToHashSet(StringComparer.Ordinal));
        foreach (var problem in CommentStyle.SetProblems(good))
            if (!remarks.Contains(problem, StringComparer.Ordinal)) remarks.Add(problem);
        return new() { Plan = plan, Good = good, Remarks = remarks };
    }

    internal static CommentPreparationResult Finish(CommentPreparationRound current, CommentPreparationRound? first = null)
    {
        if (!current.Plan.WorthCommenting) return new() { Plan = current.Plan, Remarks = first?.Remarks ?? current.Remarks };
        var chosen = first is not null && Usable(first) && (!Usable(current) || first.Good.Count > current.Good.Count) ? first : current;
        var plan = Usable(chosen) ? chosen.Plan with { Variants = [.. chosen.Good] }
            : current.Plan with { WorthCommenting = false, Variants = [], Reason = "варианты не прошли проверку стиля: " + string.Join("; ", current.Remarks) };
        return new() { Plan = plan, Remarks = [.. first?.Remarks ?? current.Remarks] };
    }

    internal static bool Usable(CommentPreparationRound round) => round.Good.Count > 0 && CommentStyle.SetProblems(round.Good).Count == 0;
}
