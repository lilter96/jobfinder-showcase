using System.Text.Json;
using JobFinder.Core.Text;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Tasks;
using JobFinder.Llm.Tasks.Discovery;

namespace JobFinder.Tests.Llm;

public sealed class GeneratedContractTests
{
    private const string NonVacancy = """
        {"isVacancy":false,"isDotnet":false,"title":null,"company":null,"city":null,"country":null,"workFormats":[],
         "salaryFrom":null,"salaryTo":null,"currency":null,"salaryGross":null,"englishLevel":"unknown","seniority":"unknown",
         "skills":[],"contacts":[],"applyUrl":null}
        """;

    [Fact]
    public void Exported_contract_requires_every_field_and_closes_the_wire_object()
    {
        var task = ExtractPostTask.Definition;
        using var schema = JsonDocument.Parse(task.Schema.Schema);
        var root = schema.RootElement;
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var properties = root.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToArray();
        var required = root.GetProperty("required").EnumerateArray().Select(property => property.GetString()).Order().ToArray();
        Assert.Equal(properties, required);
        Assert.Equal(16, properties.Length);
        Assert.Equal("extract_post", task.Prompt);
        Assert.Equal(Schemas.PostExtraction, task.Schema);
    }

    [Fact]
    public void Explicit_nulls_are_accepted_for_nullable_fields_but_omissions_are_rejected()
    {
        StructuredOutputValidator.Validate(NonVacancy, ExtractPostTask.Definition.Schema);
        var wire = JsonSerializer.Deserialize(NonVacancy, DiscoveryWireJsonContext.Default.PostExtractionWire)!;
        Assert.Null(wire.Validate());
        Assert.False(wire.ToDomain().IsVacancy);
        Assert.Equal("unknown", wire.ToDomain().EnglishLevel);
        Assert.Throws<LlmCallException>(() => StructuredOutputValidator.Validate("{}", ExtractPostTask.Definition.Schema));
    }

    [Theory]
    [InlineData("\"englishLevel\":\"unknown\"", "\"englishLevel\":0")]
    [InlineData("\"seniority\":\"unknown\"", "\"seniority\":999")]
    [InlineData("\"workFormats\":[]", "\"workFormats\":[0]")]
    [InlineData("\"skills\":[]", "\"skills\":null")]
    [InlineData("\"contacts\":[]", "\"contacts\":[null]")]
    [InlineData("\"englishLevel\":\"unknown\"", "\"englishLevel\":\"UNKNOWN\"")]
    public void Generated_schema_retains_strict_enum_and_collection_constraints(string original, string invalid)
    {
        Assert.Throws<LlmCallException>(() => StructuredOutputValidator.Validate(NonVacancy.Replace(original, invalid), ExtractPostTask.Definition.Schema));
    }

    [Fact]
    public void Semantic_invariants_still_reject_a_vacancy_without_a_title()
    {
        var vacancy = NonVacancy.Replace("\"isVacancy\":false", "\"isVacancy\":true");
        StructuredOutputValidator.Validate(vacancy, ExtractPostTask.Definition.Schema);
        Assert.NotNull(JsonSerializer.Deserialize(vacancy, DiscoveryWireJsonContext.Default.PostExtractionWire)!.Validate());
    }

    [Fact]
    public void Wire_strictness_does_not_change_legacy_domain_json_defaults()
    {
        var legacy = JsonSerializer.Deserialize("{}", LlmJsonContext.Default.PostExtraction)!;
        Assert.Empty(legacy.Skills);
        Assert.Equal("unknown", legacy.EnglishLevel);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{}", DiscoveryWireJsonContext.Default.PostExtractionWire));
    }

    [Theory]
    [InlineData("{\"a\":1,\"b\":2}", " { \"b\":2.0, \"a\":1.00 } ")]
    [InlineData("{\"x\":{\"a\":1,\"b\":2}}", "{\"x\":{\"b\":2,\"a\":1}}")]
    public void Contract_identity_ignores_formatting_property_order_and_number_scale(string first, string second) =>
        Assert.Equal(CanonicalJson.Hash(first), CanonicalJson.Hash(second));

    [Fact]
    public void Contract_identity_preserves_array_order_and_meaningful_values()
    {
        Assert.NotEqual(CanonicalJson.Hash("[1,2]"), CanonicalJson.Hash("[2,1]"));
        Assert.NotEqual(CanonicalJson.Hash("{\"effort\":\"xhigh\"}"), CanonicalJson.Hash("{\"effort\":\"none\"}"));
    }
}
