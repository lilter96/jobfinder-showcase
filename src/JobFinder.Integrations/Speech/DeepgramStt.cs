using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace JobFinder.Integrations.Speech;

public sealed class SttOptions
{
    public const string Section = "Stt";

    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "nova-3";

    public string Language { get; set; } = "ru";

    public string Endpoint { get; set; } = "https://api.deepgram.com/v1/listen";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Voice messages to text (Deepgram prerecorded API): answers in the practice interview, the story interview and debriefs.</summary>
public sealed class DeepgramStt(HttpClient http, SttOptions options)
{
    public bool IsConfigured => options.IsConfigured;

    public async Task<string> TranscribeAsync(Stream audio, string contentType, CancellationToken ct)
    {
        if (!options.IsConfigured) throw new InvalidOperationException("распознавание речи не настроено (Stt:ApiKey)");
        var url = $"{options.Endpoint}?model={Uri.EscapeDataString(options.Model)}&language={Uri.EscapeDataString(options.Language)}" +
                  "&smart_format=true&punctuate=true";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", options.ApiKey);
        request.Content = new StreamContent(audio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Deepgram {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        var transcript = JsonNode.Parse(body)?["results"]?["channels"]?[0]?["alternatives"]?[0]?["transcript"]?.ToString();
        // Silence or noise is an empty transcript, not an error: the caller asks to repeat.
        return transcript?.Trim() ?? "";
    }
}
