using System.Text.Json;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Text;
using JobFinder.Llm;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Profile;

namespace JobFinder.Pipeline.Modules.Hiring;

public interface IOfferPreparationSteps
{
    void EnsureCompatible(OfferPreparationInput input);
    Task<OfferPreparationResult> AnalyzeAsync(OfferPreparationInput input, CancellationToken ct);
}

public sealed class OfferPreparationSteps(JobFinderLlm llm) : IOfferPreparationSteps
{
    internal static ArtifactRecipe Recipe(JobFinderLlm operation, OfferPreparationInput input) => operation.Recipe(
        [(LlmTaskCatalog.Offer, LlmPurpose.Resume)], new Dictionary<string, string?>
        {
            ["profile"] = ProfileProvider.ToJson(input.Source.Profile), ["offer"] = input.Source.Text,
            ["source"] = JsonSerializer.Serialize(input.Source, OfferSourceJsonContext.Default.OfferDraftSnapshot),
            ["market"] = input.MarketText, ["other-offers"] = input.OtherOffersText,
            ["market-evidence"] = JsonSerializer.Serialize(input.Market, OfferPreparationJsonContext.Default.OfferMarketEvidence),
            ["conversion"] = JsonSerializer.Serialize(input.Conversion, OfferPreparationJsonContext.Default.OfferConversionBasis),
        }, new Dictionary<string, string> { ["offer"] = "native-v1", ["presentation"] = OfferPresentation.Version });

    public void EnsureCompatible(OfferPreparationInput input)
    {
        if (Recipe(llm.RestoreOperation(input.Operation), input).Hash != input.Recipe.Hash) throw new OfferPreparationSupersededException();
    }

    public async Task<OfferPreparationResult> AnalyzeAsync(OfferPreparationInput input, CancellationToken ct)
    {
        EnsureCompatible(input);
        var guessed = OfferSources.Guess(input.Source);
        var analysis = (await llm.RestoreOperation(input.Operation).OfferAsync(input.Source.Profile, input.Source.Text,
            guessed?.Vacancy.ToVacancy(), input.MarketText, input.OtherOffersText, ct)).Value;
        analysis.CounterScript = HumanText.Clean(analysis.CounterScript);
        return new OfferPreparationResult
        {
            Analysis = analysis,
            MonthlyNetUsd = analysis.MonthlyAmount is { } amount ? input.Conversion.Convert(amount, analysis.Currency, analysis.Gross) : null,
        };
    }
}
