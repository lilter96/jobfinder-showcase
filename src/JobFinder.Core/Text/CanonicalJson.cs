using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace JobFinder.Core.Text;

public static class CanonicalJson
{
    public static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, document.RootElement);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(json))));

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.TryGetDecimal(out var number) ? number.ToString("G29", CultureInfo.InvariantCulture)
                    : value.GetDouble().ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
