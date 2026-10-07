using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;

namespace JobFinder.Pipeline.Modules.Hiring;

public interface ILetterPreparationSteps
{
    void EnsureCompatible(LetterPreparationInput input);
    Task<LetterCandidate> DraftAsync(LetterPreparationInput input, LetterCandidate? best, CancellationToken ct);
    Task<LetterCandidate> ReviewAsync(LetterPreparationInput input, LetterCandidate draft, CancellationToken ct);
}

public sealed class LetterPreparationSteps(JobFinderLlm llm, TimeProvider time, ILogger<LetterPreparationSteps> log) : ILetterPreparationSteps
{
    private readonly LetterContent _content = new(time, log);

    public void EnsureCompatible(LetterPreparationInput input)
    {
        var operation = llm.RestoreOperation(input.Operation);
        var brief = input.Brief.ToBrief();
        var recipe = input.ExactText is null ? LetterWriter.ContextRecipe(operation, input.Profile, input.Facts, brief)
            : LetterWriter.ReviewRecipe(operation, input.Profile, input.Facts, brief, input.ExactText);
        if (recipe.Hash != input.Recipe.Hash || input.ExactText is not null
            && LetterWriter.ContextRecipe(operation, input.Profile, input.Facts, brief).Hash != input.ReviewContextHash)
            throw new LetterPreparationSupersededException();
    }

    public Task<LetterCandidate> DraftAsync(LetterPreparationInput input, LetterCandidate? best, CancellationToken ct)
    {
        EnsureCompatible(input);
        if (input.ExactText is not null) throw new UnsupportedLetterPreparationException();
        var remarks = best is null ? null : $"Текст:\n{best.Written}\n\nЧто исправить:\n- " + string.Join("\n- ", best.Problems);
        return _content.DraftAsync(llm.RestoreOperation(input.Operation), input.Profile, input.Facts, input.Brief.ToBrief(), input.CapturedAt, remarks, ct);
    }

    public async Task<LetterCandidate> ReviewAsync(LetterPreparationInput input, LetterCandidate draft, CancellationToken ct)
    {
        EnsureCompatible(input);
        if (input.ExactText is null)
            return await _content.ReviewAsync(llm.RestoreOperation(input.Operation), input.Profile, input.Facts, input.Brief.ToBrief(), draft, ct);
        if (draft.Text != input.ExactText) throw new UnsupportedLetterPreparationException();
        var review = await _content.ReviewTextAsync(llm.RestoreOperation(input.Operation), input.Profile, input.Facts, input.Brief.ToBrief(), input.ExactText, ct);
        return new LetterCandidate { Text = input.ExactText, Written = input.ExactText, Review = review, Score = review.Score, Problems = review.Problems.ToList() };
    }
}
