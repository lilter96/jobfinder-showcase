using System.Text.Json.Nodes;
using JobFinder.Llm.Client;

namespace JobFinder.Llm.Contracts;

public sealed record ProviderSchemaProjection(string SystemSuffix, JsonObject? ResponseFormat, bool RequireParameters)
{
    public const string Version = "canonical-v1";

    public static ProviderSchemaProjection Create(JsonSchemaSpec? canonical, string mode)
    {
        if (mode is not ("json_schema" or "json_object" or "prompt"))
            throw new ArgumentException("unknown structured output mode", nameof(mode));
        if (canonical is null) return new("", null, false);
        if (mode == "json_schema")
            return new("", new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = canonical.Name, ["strict"] = true, ["schema"] = JsonNode.Parse(canonical.Schema),
                },
            }, true);
        return new("\n\nОтвет — ТОЛЬКО JSON-объект без markdown, строго по этой JSON Schema:\n" + canonical.Schema,
            mode == "json_object" ? new JsonObject { ["type"] = "json_object" } : null, false);
    }
}
