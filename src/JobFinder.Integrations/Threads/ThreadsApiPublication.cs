using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;

namespace JobFinder.Integrations.Threads;

public sealed partial class ThreadsApiClient
{
    public async Task<ThreadsPreparedPublication> PreparePublicationAsync(ThreadsPost target, string text, ThreadsPublicationRoute route,
        ThreadsPreparedPublication? saved, CancellationToken ct)
    {
        if (!PublicationConfigured(route)) return Waiting("API configuration changed or its token is missing");
        if (!Numeric(target.Id) || text.Length is 0 or > 500) return NotSent("invalid API target or comment");
        try
        {
            var account = await PublicationRequestAsync(HttpMethod.Get, "me?fields=id,username", null, ct);
            var accountId = String(account, "id");
            var username = String(account, "username");
            if (!Numeric(accountId) || string.IsNullOrWhiteSpace(username)) return Waiting("API account could not be identified");
            if (saved is { AccountId.Length: > 0 } && (saved.AccountId != accountId || saved.Username != username)) return NotSent("API account changed");
            var post = await PublicationRequestAsync(HttpMethod.Get, target.Id + "?fields=id,username,permalink,text", null, ct);
            var permalink = String(post, "permalink");
            if (String(post, "id") != target.Id || !String(post, "username").Equals(target.Username.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                || !SamePost(target.Url, permalink) || String(post, "text") != target.Text)
                return NotSent("API post identity or captured content changed");
            if (saved?.ContainerId is not { } containerId)
            {
                var container = await PublicationRequestAsync(HttpMethod.Post, "me/threads", new()
                {
                    ["media_type"] = "TEXT", ["text"] = text, ["reply_to_id"] = target.Id, ["auto_publish_text"] = "false",
                }, ct);
                containerId = String(container, "id");
                if (!Numeric(containerId)) return Waiting("API did not return a valid unpublished container");
                return new() { State = ThreadsPreparationState.Waiting, ContainerId = containerId, AccountId = accountId, Username = username, RetrySeconds = 5 };
            }
            if (!Numeric(containerId)) return NotSent("invalid saved container");
            var status = await PublicationRequestAsync(HttpMethod.Get, containerId + "?fields=status,error_message", null, ct);
            return new()
            {
                State = String(status, "status") switch
                {
                    "FINISHED" => ThreadsPreparationState.Ready,
                    "ERROR" or "EXPIRED" => ThreadsPreparationState.NotSent,
                    "PUBLISHED" => ThreadsPreparationState.Unknown,
                    _ => ThreadsPreparationState.Waiting,
                },
                ContainerId = containerId, AccountId = accountId, Username = username, RetrySeconds = 5,
            };
        }
        catch (Exception error) when ((error is HttpRequestException or JsonException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return saved is null ? Waiting("API preparation unavailable") : saved with { State = ThreadsPreparationState.Waiting, Error = "API preparation unavailable" };
        }
    }

    public async Task<ThreadsPublicationReceipt> PublishPreparedAsync(ThreadsPublicationRoute route, ThreadsPreparedPublication prepared,
        Func<CancellationToken, Task<bool>> mayPublish, CancellationToken ct)
    {
        var effect = false;
        try
        {
            if (!PublicationConfigured(route) || prepared.State != ThreadsPreparationState.Ready || !Numeric(prepared.ContainerId)
                || !Numeric(prepared.AccountId) || string.IsNullOrWhiteSpace(prepared.Username))
                return Unsent("API preparation or configuration changed");
            var account = await PublicationRequestAsync(HttpMethod.Get, "me?fields=id,username", null, ct);
            if (String(account, "id") != prepared.AccountId || String(account, "username") != prepared.Username) return Unsent("API account changed");
            ct.ThrowIfCancellationRequested();
            if (!await mayPublish(ct)) return Unsent("publication permission was revoked", retryable: true);
            ct.ThrowIfCancellationRequested();
            effect = true;
            var response = await PublicationRequestAsync(HttpMethod.Post, "me/threads_publish", new() { ["creation_id"] = prepared.ContainerId! }, ct);
            var id = String(response, "id");
            return Numeric(id) ? new() { Status = DeliveryAttemptStatus.Confirmed, ExternalId = id }
                : new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "publication response has no valid thread id" };
        }
        catch (Exception)
        {
            return effect ? new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "API publication outcome is unknown; do not retry or fall back" }
                : Unsent("API pre-publication check unavailable", retryable: true);
        }
    }

    private bool PublicationConfigured(ThreadsPublicationRoute route) => route.Mode == ThreadsPublicationMode.Api
        && route.Endpoint == options.ApiBase.TrimEnd('/') && !string.IsNullOrWhiteSpace(options.AccessToken)
        && ThreadsPublicationRoute.IsApiEndpoint(route.Endpoint);

    private async Task<JsonNode> PublicationRequestAsync(HttpMethod method, string path, Dictionary<string, string>? form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, options.ApiBase.TrimEnd('/') + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Threads API request failed", null, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        if (node is not JsonObject || node["error"] is not null) throw new HttpRequestException("Threads API rejected the request");
        return node;
    }

    private static string String(JsonNode node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static bool Numeric(string? value) => value is { Length: > 0 and <= 40 } && value.All(char.IsAsciiDigit);
    private static bool SamePost(string expected, string actual)
    {
        if (!ThreadsBrowserClient.PostUrl(expected) || !ThreadsBrowserClient.PostUrl(actual)) return false;
        var expectedParts = new Uri(expected).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var actualParts = new Uri(actual).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return expectedParts[0].Equals(actualParts[0], StringComparison.OrdinalIgnoreCase) && expectedParts[2] == actualParts[2];
    }
    private static ThreadsPreparedPublication Waiting(string error) => new() { State = ThreadsPreparationState.Waiting, Error = error };
    private static ThreadsPreparedPublication NotSent(string error) => new() { State = ThreadsPreparationState.NotSent, Error = error };
    private static ThreadsPublicationReceipt Unsent(string error, bool retryable = false) => new() { Status = DeliveryAttemptStatus.KnownNotSent, Error = error, Retryable = retryable };
}
