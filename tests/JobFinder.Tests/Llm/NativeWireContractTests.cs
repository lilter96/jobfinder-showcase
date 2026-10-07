using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Llm.Tasks;

namespace JobFinder.Tests.Llm;

public sealed class NativeWireContractTests
{
    public static TheoryData<string> Contracts => new(typeof(NativeWireSchemas).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(member => member.Name));

    private static JsonSchemaSpec Contract(string name) => (JsonSchemaSpec)typeof(NativeWireSchemas).GetField(name)!.GetValue(null)!;

    private static JsonNode Sample(JsonNode schema, JsonNode root)
    {
        if (schema["$ref"] is { } reference)
        {
            var target = root;
            foreach (var part in reference.GetValue<string>()[2..].Split('/')) target = target[part.Replace("~1", "/").Replace("~0", "~")]!;
            return Sample(target, root);
        }
        if (schema["enum"] is JsonArray choices) return choices.First(choice => choice is not null)!.DeepClone();
        if (schema["anyOf"] is JsonArray alternatives) return Sample(alternatives.First(alternative => alternative?["type"]?.ToString() != "null")!, root);
        var kind = schema["type"] is JsonArray types ? types.First(type => type?.ToString() != "null")!.ToString() : schema["type"]!.ToString();
        return kind switch
        {
            "object" => new JsonObject(schema["properties"]!.AsObject().Select(property =>
                new KeyValuePair<string, JsonNode?>(property.Key, Sample(property.Value!, root)))),
            "array" => new JsonArray(Sample(schema["items"]!, root)),
            "string" => JsonValue.Create("sample")!,
            "integer" or "number" => schema["minimum"]?.DeepClone() ?? JsonValue.Create(1)!,
            "boolean" => JsonValue.Create(false)!,
            _ => throw new InvalidOperationException(kind),
        };
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Every_wire_contract_has_closed_required_fields_and_rejects_missing_extra_or_null_collections(string name)
    {
        var specification = Contract(name);
        var schema = JsonNode.Parse(specification.Schema)!;
        var properties = schema["properties"]!.AsObject();
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(properties.Select(property => property.Key).Order(), schema["required"]!.AsArray().Select(value => value!.GetValue<string>()).Order());
        var sample = Sample(schema, schema).AsObject();
        StructuredOutputValidator.Validate(sample.ToJsonString(), specification);
        foreach (var property in properties)
        {
            var missing = sample.DeepClone().AsObject();
            missing.Remove(property.Key);
            Assert.Throws<LlmCallException>(() => StructuredOutputValidator.Validate(missing.ToJsonString(), specification));
            if (property.Value?["type"]?.ToString() == "array")
            {
                var invalid = sample.DeepClone().AsObject();
                invalid[property.Key] = null;
                Assert.Throws<LlmCallException>(() => StructuredOutputValidator.Validate(invalid.ToJsonString(), specification));
            }
        }
        sample["unexpected"] = true;
        Assert.Throws<LlmCallException>(() => StructuredOutputValidator.Validate(sample.ToJsonString(), specification));
    }

    [Theory]
    [InlineData("score", "101")]
    [InlineData("stackMatch", "-1")]
    [InlineData("language", "\"fr\"")]
    [InlineData("legitimacy", "\"unverified\"")]
    [InlineData("englishRequired", "\"C2\"")]
    public void Attribute_constraints_are_exported_in_the_canonical_schema(string property, string invalidValue)
    {
        var specification = NativeWireSchemas.VacancyScoring;
        var schema = JsonNode.Parse(specification.Schema)!;
        var sample = Sample(schema, schema).AsObject();
        sample[property] = JsonNode.Parse(invalidValue);
        Assert.Throws<LlmCallException>(() => StructuredOutputValidator.Validate(sample.ToJsonString(), specification));
    }

    [Fact]
    public void Wire_does_not_expose_profile_approval_or_resume_pipeline_metadata()
    {
        var profileSchema = JsonNode.Parse(NativeWireSchemas.CandidateProfile.Schema)!;
        Assert.False(profileSchema["properties"]!.AsObject().ContainsKey("reviewed"));
        Assert.False(profileSchema["properties"]!.AsObject().ContainsKey("nameLatin"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{}", GeneratedWireJsonContext.Default.CandidateProfileWire));
        var legacy = JsonSerializer.Deserialize("{}", LlmJsonContext.Default.CandidateProfile)!;
        Assert.True(legacy.Reviewed);
        Assert.Empty(legacy.Skills);
    }

    [Fact]
    public void Wire_mapping_preserves_generated_values_and_legacy_domain_defaults()
    {
        var schema = JsonNode.Parse(NativeWireSchemas.CandidateProfile.Schema)!;
        var json = Sample(schema, schema).ToJsonString();
        var wire = JsonSerializer.Deserialize(json, GeneratedWireJsonContext.Default.CandidateProfileWire)!;
        var domain = wire.ToDomain();
        Assert.Equal(wire.Name, domain.Name);
        Assert.Equal(wire.Skills[0].Name, domain.Skills[0].Name);
        Assert.True(domain.Reviewed);
        Assert.Null(domain.NameLatin);
    }
}
