using System.Text.Json.Nodes;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Tasks;

namespace JobFinder.Tests.Llm;

public sealed class ProviderSchemaProjectionTests
{
    [Theory]
    [InlineData("json_schema")]
    [InlineData("json_object")]
    [InlineData("prompt")]
    public void Provider_output_modes_never_relax_the_local_canonical_contract(string mode)
    {
        var schema = LlmTaskCatalog.Score.Schema;
        var original = schema.Schema;
        var projection = ProviderSchemaProjection.Create(schema, mode);
        Assert.Equal("canonical-v1", ProviderSchemaProjection.Version);
        Assert.Equal(mode == "json_schema", projection.RequireParameters);
        if (mode == "json_schema")
        {
            Assert.Equal("", projection.SystemSuffix);
            Assert.True(projection.ResponseFormat!["json_schema"]!["strict"]!.GetValue<bool>());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), projection.ResponseFormat["json_schema"]!["schema"]));
            projection.ResponseFormat["json_schema"]!["schema"]!["required"] = new JsonArray();
        }
        else
        {
            Assert.Contains(original, projection.SystemSuffix);
            Assert.Equal(mode == "json_object" ? "json_object" : null, projection.ResponseFormat?["type"]?.GetValue<string>());
        }
        Assert.Equal(original, schema.Schema);
        Assert.Throws<LlmCallException>(() => StructuredOutputValidator.Validate("{}", schema));
    }

    [Theory]
    [InlineData("json_schema")]
    [InlineData("json_object")]
    [InlineData("prompt")]
    public void Plain_text_has_no_provider_schema_or_json_instructions(string mode)
    {
        var projection = ProviderSchemaProjection.Create(null, mode);
        Assert.Equal("", projection.SystemSuffix);
        Assert.Null(projection.ResponseFormat);
        Assert.False(projection.RequireParameters);
    }

    [Fact]
    public void Unknown_projection_modes_fail_instead_of_silently_losing_strict_output()
    {
        Assert.Throws<ArgumentException>(() => ProviderSchemaProjection.Create(LlmTaskCatalog.Score.Schema, "json-scheam"));
    }
}
