using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using JobFinder.Llm.Client;

namespace JobFinder.Llm.Contracts;

public static class StructuredOutputValidator
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    public static void Validate(string json, JsonSchemaSpec specification)
    {
        JsonDocument instance;
        try
        {
            instance = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new LlmCallException(LlmFailureKind.BadResponse, "response is not valid JSON", inner: error);
        }
        using (instance)
        {
            var schema = Schemas.GetOrAdd(specification.Schema, text => JsonSchema.FromText(text));
            var result = schema.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
            if (!result.IsValid)
                throw new LlmCallException(LlmFailureKind.BadResponse, $"response violates the canonical schema '{specification.Name}'");
        }
    }
}
