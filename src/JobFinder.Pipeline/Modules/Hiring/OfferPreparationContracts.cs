using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Text;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record OfferMarketEvidence
{
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public decimal P25 { get; set; }
    public decimal P50 { get; set; }
    public decimal P75 { get; set; }
    public decimal P90 { get; set; }
    public List<decimal> Sorted { get; set; } = [];
    public int? PercentileOf(decimal value) => Sorted.Count == 0 ? null : (int)Math.Round(100m * Sorted.Count(amount => amount <= value) / Sorted.Count);
}

public sealed record OfferConversionBasis
{
    public Dictionary<string, decimal> RatesToUsd { get; set; } = [];
    public decimal NetShareOfGross { get; set; } = 0.87m;
    public decimal? Convert(decimal amount, string? currency, bool? gross)
    {
        var normalized = Currency.Normalize(currency) ?? "RUB";
        var rate = normalized == "USD" ? 1 : RatesToUsd.GetValueOrDefault(normalized);
        return rate <= 0 ? null : amount * rate * (gross == true ? NetShareOfGross : 1);
    }
}

public sealed record OfferPreparationInput
{
    public int Version { get; set; } = 1;
    public OfferDraftSnapshot Source { get; set; } = new();
    public string MarketText { get; set; } = "";
    public OfferMarketEvidence? Market { get; set; }
    public string OtherOffersText { get; set; } = "";
    public OfferConversionBasis Conversion { get; set; } = new();
    public DateTimeOffset CapturedAt { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
}

public sealed record OfferPreparationResult
{
    public int Version { get; set; } = 1;
    public OfferAnalysis Analysis { get; set; } = new();
    public decimal? MonthlyNetUsd { get; set; }
}

[MessageIdentity("jobfinder.hiring.analyze-offer.v1")]
public sealed record AnalyzeOffer { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.hiring.publish-offer.v1")]
public sealed record PublishOffer { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }

public sealed class UnsupportedOfferPreparationException() : Exception("unsupported or damaged offer preparation");
public sealed class OfferPreparationSupersededException() : Exception("offer preparation inputs or recipe changed");
public sealed class OfferPreparationCheckpointException(Exception inner) : Exception("offer checkpoint persistence failed", inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(OfferPreparationInput))]
[JsonSerializable(typeof(OfferPreparationResult))]
[JsonSerializable(typeof(AnalyzeOffer))]
[JsonSerializable(typeof(PublishOffer))]
public sealed partial class OfferPreparationJsonContext : JsonSerializerContext;
