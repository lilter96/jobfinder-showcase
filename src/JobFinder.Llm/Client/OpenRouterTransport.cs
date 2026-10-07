using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JobFinder.Llm.Client;

public sealed class LlmCallException(LlmFailureKind kind, string message, TimeSpan? retryAfter = null, Exception? inner = null,
    LlmRawResult? raw = null)
    : Exception(message, inner)
{
    public LlmFailureKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public LlmRawResult? Raw { get; } = raw;
}

public sealed record LlmRawResult(string Content, string Model, int PromptTokens, int CompletionTokens, decimal CostUsd);

/// <summary>A JSON schema for structured output: <paramref name="Name"/> is the OpenAI-style schema name.</summary>
public sealed record JsonSchemaSpec(string Name, string Schema);

/// <summary>
/// One chat completion against one model, no retries: the classification of the failure is the whole point, the route
/// (<see cref="LlmClient"/>) decides what to do with it. One exception: when the direct route does not connect at all (TLS, DNS,
/// refused — the Docker network here drops TLS to OpenRouter for minutes at a time while the host gets through) and
/// <see cref="LlmOptions.Proxy"/> is set, the same request goes once through the proxy route, as scraping does. An HTTP answer never
/// switches the route.
/// </summary>
public sealed class OpenRouterTransport(IHttpClientFactory httpFactory, LlmOptions options)
{
    public const string DirectClient = "llm-direct";
    public const string ProxyClient = "llm-proxy";

    public async Task<LlmRawResult> CompleteAsync(LlmModelOptions model, string system, string user, JsonSchemaSpec? schema, CancellationToken ct)
    {
        var body = BuildBody(model, system, user, schema).ToJsonString();
        var limit = model.TimeoutSec ?? options.TimeoutSec;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(limit));
        var proxied = !string.IsNullOrWhiteSpace(options.Proxy);
        var viaProxy = model.UseProxy && proxied;

        HttpResponseMessage response;
        string text;
        try
        {
            try
            {
                (response, text) = await SendAsync(viaProxy ? ProxyClient : DirectClient, body, timeout.Token);
            }
            catch (HttpRequestException e) when (!viaProxy && proxied && IsNetworkFailure(e))
            {
                (response, text) = await SendAsync(ProxyClient, body, timeout.Token);
            }
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new LlmCallException(LlmFailureKind.Unavailable, $"timeout after {limit}s", inner: e);
        }
        catch (HttpRequestException e)
        {
            throw new LlmCallException(LlmFailureKind.Unavailable, $"network: {e.Message}", inner: e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw Classify((int)response.StatusCode, ErrorMessage(text), RetryAfter(response));
            return Parse(text, model.Name);
        }
    }

    /// <summary>A connection that never got an HTTP answer: what the proxy route can fix.</summary>
    public static bool IsNetworkFailure(HttpRequestException e) =>
        e.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError;

    private async Task<(HttpResponseMessage Response, string Text)> SendAsync(string client, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", options.Referer);
        request.Headers.TryAddWithoutValidation("X-Title", options.AppTitle);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await httpFactory.CreateClient(client).SendAsync(request, ct);
        return (response, await response.Content.ReadAsStringAsync(ct));
    }

    internal JsonObject BuildBody(LlmModelOptions model, string system, string user, JsonSchemaSpec? schema)
    {
        try
        {
            model = Configuration.LlmPolicyCompiler.Model(model, options.TimeoutSec);
        }
        catch (Configuration.LlmPolicyException error)
        {
            throw new LlmCallException(LlmFailureKind.Rejected, error.Message, inner: error);
        }
        var projection = Contracts.ProviderSchemaProjection.Create(schema, model.Structured);
        var systemText = system + projection.SystemSuffix;

        var body = new JsonObject
        {
            ["model"] = model.Name,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemText },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
            ["max_tokens"] = model.MaxTokens,
            ["usage"] = new JsonObject { ["include"] = true },
        };

        if (model.Temperature is { } temperature) body["temperature"] = temperature;
        if (projection.ResponseFormat is { } responseFormat) body["response_format"] = responseFormat;
        if (projection.RequireParameters) body["provider"] = new JsonObject { ["require_parameters"] = true };

        if (model.Reasoning is { } reasoning)
            body["reasoning"] = JsonSerializer.SerializeToNode(reasoning, Configuration.LlmPolicyJsonContext.Default.ReasoningSettings);
        if (model.Provider is { } preferences)
        {
            var provider = body["provider"] as JsonObject ?? new JsonObject();
            var typed = JsonSerializer.SerializeToNode(preferences, Configuration.LlmPolicyJsonContext.Default.ProviderSettings)!.AsObject();
            foreach (var property in typed.Where(property => property.Value is not JsonArray { Count: 0 }))
                provider[property.Key] = property.Value?.DeepClone();
            body["provider"] = provider;
        }
        return body;
    }

    internal static LlmRawResult Parse(string text, string requestedModel)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new LlmCallException(LlmFailureKind.BadResponse, $"non-JSON body: {Short(text)}", inner: e);
        }

        if (root is not JsonObject envelope)
            throw new LlmCallException(LlmFailureKind.BadResponse, "response envelope must be an object");
        var choice = envelope["choices"] is JsonArray { Count: > 0 } choices ? choices[0] as JsonObject : null;
        var message = choice?["message"] as JsonObject;
        var content = message?["content"]?.GetValueKind() == JsonValueKind.String ? message["content"]!.GetValue<string>() : null;
        var finish = choice?["finish_reason"]?.ToString();
        var usage = envelope["usage"] as JsonObject;
        var result = new LlmRawResult(content ?? "", envelope["model"]?.ToString() ?? requestedModel,
            Int(usage?["prompt_tokens"]), Int(usage?["completion_tokens"]), Dec(usage?["cost"]));
        // OpenRouter reports some upstream failures as 200 + {"error": {...}}.
        if (envelope["error"] is JsonObject error)
        {
            var code = Int(error["code"]);
            throw Classify(code > 0 ? code : 502, error["message"]?.ToString() ?? Short(text), null, result);
        }

        if (string.IsNullOrWhiteSpace(content))
            throw new LlmCallException(LlmFailureKind.BadResponse, $"empty content (finish_reason={finish ?? "?"})", raw: result);
        if (finish == "length") throw new LlmCallException(LlmFailureKind.BadResponse, "truncated by max_tokens", raw: result);

        return result;
    }

    internal static LlmCallException Classify(int status, string message, TimeSpan? retryAfter, LlmRawResult? raw = null)
    {
        var text = $"HTTP {status}: {message}";
        return status switch
        {
            401 or 402 => new LlmCallException(LlmFailureKind.Unauthorized, text, raw: raw),
            429 => new LlmCallException(LlmFailureKind.RateLimited, text, retryAfter, raw: raw),
            400 or 403 or 404 or 422 => new LlmCallException(LlmFailureKind.Rejected, text, raw: raw),
            _ => new LlmCallException(LlmFailureKind.Unavailable, text, retryAfter, raw: raw),
        };
    }

    private static void Merge(JsonObject target, JsonObject extra)
    {
        foreach (var (key, value) in extra)
        {
            if (value is JsonObject obj && target[key] is JsonObject existing)
            {
                Merge(existing, obj);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.RetryAfter?.Date is { } date) return date - DateTimeOffset.UtcNow;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetMs))
        {
            var left = DateTimeOffset.FromUnixTimeMilliseconds(resetMs) - DateTimeOffset.UtcNow;
            if (left > TimeSpan.Zero && left < TimeSpan.FromHours(24)) return left;
        }

        return null;
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.ToString();
            var raw = node?["error"]?["metadata"]?["raw"]?.ToString();
            if (message is not null) return raw is null ? message : $"{message} ({Short(raw)})";
        }
        catch (JsonException)
        {
            // Not JSON (a proxy error page): the body itself is the message.
        }

        return Short(body);
    }

    private static int Int(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number ? (int)node.GetValue<double>() : 0;

    private static decimal Dec(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number ? (decimal)node.GetValue<double>() : 0m;

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

/// <summary>Proxy-aware handler for the <see cref="OpenRouterTransport.ProxyClient"/> named client.</summary>
public static class LlmHttp
{
    public static HttpMessageHandler CreateHandler(string? proxy)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return handler;
    }
}
