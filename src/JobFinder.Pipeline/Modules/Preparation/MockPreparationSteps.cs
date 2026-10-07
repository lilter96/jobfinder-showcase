using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Llm;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;

namespace JobFinder.Pipeline.Modules.Preparation;

public interface IMockPreparationSteps
{
    void EnsureCompatible(MockPreparationInput input);
    Task<MockPreparationResult> PrepareAsync(MockPreparationInput input, CancellationToken ct);
}

public sealed class MockPreparationSteps(JobFinderLlm llm) : IMockPreparationSteps
{
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, MockPreparationInput input) => operation.Recipe(
        [(Task(input.Kind), LlmPurpose.Coach)],
        new Dictionary<string, string?>
        {
            ["source"] = JsonSerializer.Serialize(input.Source, MockPreparationJsonContext.Default.MockSessionSnapshot),
            ["waiting"] = ConversationStore.Capture(input.Waiting), ["start"] = input.Start is null ? null : JsonSerializer.Serialize(input.Start, MockPreparationJsonContext.Default.MockStartSnapshot),
            ["answer"] = input.Answer, ["learning"] = input.Learning is null ? null : JsonSerializer.Serialize(input.Learning, LearningProgressJsonContext.Default.LearningItemSnapshot),
            ["prefix"] = input.Prefix,
        }, new Dictionary<string, string> { ["mock"] = "native-v1", ["presentation"] = MockPresentation.Version });

    public void EnsureCompatible(MockPreparationInput input)
    {
        if (Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash) throw new MockPreparationSupersededException();
    }

    private static ILlmTaskDefinition Task(MockPreparationKind kind) => kind switch
    {
        MockPreparationKind.Questions => LlmTaskCatalog.MockQuestions, MockPreparationKind.Review => LlmTaskCatalog.MockReview,
        MockPreparationKind.Summary => LlmTaskCatalog.MockSummary, _ => throw new UnsupportedMockPreparationException(),
    };

    public async Task<MockPreparationResult> PrepareAsync(MockPreparationInput input, CancellationToken ct)
    {
        EnsureCompatible(input);
        var operation = llm.RestoreOperation(input.Operation);
        if (input.Kind == MockPreparationKind.Questions)
        {
            var start = input.Start!;
            var count = start.Kind switch { JobFinder.Core.Domain.InterviewKind.SystemDesign => 3, JobFinder.Core.Domain.InterviewKind.Coding => 2, _ => 6 };
            var questions = (await operation.MockQuestionsAsync(start.Profile, MockInterviews.KindArgument(start.Kind), count, start.Vacancy?.ToVacancy(), start.Stories, ct)).Value;
            return new() { Questions = questions.Questions.Select(question => new MockTurn { Question = question.Question, Focus = question.Focus, Ideal = question.Ideal }).ToList() };
        }
        if (input.Kind == MockPreparationKind.Review)
        {
            var turn = ConversationStore.Turns(input.Source.TurnsJson)[input.Source.Current];
            return new() { Review = (await operation.MockReviewAsync(turn.Question, turn.Ideal, input.Answer!, ct)).Value };
        }
        return new() { Summary = (await operation.MockSummaryAsync(input.Transcript(), ct)).Value };
    }
}
