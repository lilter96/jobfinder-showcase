using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using JobFinder.Core.Text;
using JobFinder.Llm.Client;

namespace JobFinder.Llm.Contracts;

[AttributeUsage(AttributeTargets.Property)]
public sealed class WireChoicesAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
    public bool AllowNull { get; set; }
}

public static class WireSchema
{
    public const string VocabularyVersion = "native-wire-v1";

    public static JsonSchemaSpec Create<T>(string name, JsonTypeInfo<T> type)
    {
        var generated = JsonSchemaExporter.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = ApplyConstraints,
        });
        return new JsonSchemaSpec(name, CanonicalJson.Normalize(generated.ToJsonString()));
    }

    private static JsonNode ApplyConstraints(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (context.PropertyInfo is not { } property || schema is not JsonObject node) return schema;
        var member = property.AttributeProvider ?? property.DeclaringType?.GetProperties().FirstOrDefault(candidate =>
            (context.TypeInfo.Options.PropertyNamingPolicy?.ConvertName(candidate.Name) ?? candidate.Name) == property.Name);
        if (member?.GetCustomAttributes(typeof(WireChoicesAttribute), inherit: true).OfType<WireChoicesAttribute>().SingleOrDefault() is { } choices)
        {
            var values = new JsonArray(choices.Values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            if (choices.AllowNull) values.Add((JsonNode?)null);
            node["enum"] = values;
        }
        if (member?.GetCustomAttributes(typeof(RangeAttribute), inherit: true).OfType<RangeAttribute>().SingleOrDefault() is { } range)
        {
            node["minimum"] = JsonSerializer.SerializeToNode(range.Minimum);
            node["maximum"] = JsonSerializer.SerializeToNode(range.Maximum);
        }
        return schema;
    }
}

public static class WireMapping
{
    public static TDomain Map<TWire, TDomain>(TWire wire, JsonTypeInfo<TWire> input, JsonTypeInfo<TDomain> output) where TDomain : class =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(wire, input), output)
        ?? throw new LlmCallException(LlmFailureKind.BadResponse, "wire result could not be mapped");

    public static string? Validate<TDomain>(TDomain domain) => domain is IValidatable validatable ? validatable.Validate() : null;

    public static async Task<LlmResult<TDomain>> MapAsync<TWire, TDomain>(this Task<LlmResult<TWire>> task, Func<TWire, TDomain> map)
    {
        var result = await task;
        return new LlmResult<TDomain>(map(result.Value), result.Model, result.CostUsd);
    }
}
