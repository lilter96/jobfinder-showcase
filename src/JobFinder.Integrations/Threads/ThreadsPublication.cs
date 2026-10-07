using JobFinder.Core.Domain;

namespace JobFinder.Integrations.Threads;

public enum ThreadsPublicationMode { Api, Browser }
public enum ThreadsPreparationState { Ready, Waiting, NotSent, Unknown }

public sealed record ThreadsPublicationRoute
{
    public ThreadsPublicationMode Mode { get; set; }
    public string Endpoint { get; set; } = "";

    public static ThreadsPublicationRoute Api(string endpoint) => new()
    {
        Mode = ThreadsPublicationMode.Api,
        Endpoint = IsApiEndpoint(endpoint) ? endpoint.TrimEnd('/') : "",
    };

    internal static bool IsApiEndpoint(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host is "graph.threads.net" or "graph.threads.com"
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

public sealed record ThreadsPreparedPublication
{
    public ThreadsPreparationState State { get; set; }
    public string? ContainerId { get; set; }
    public string AccountId { get; set; } = "";
    public string Username { get; set; } = "";
    public int RetrySeconds { get; set; } = 30;
    public string? Error { get; set; }
}

public sealed record ThreadsPublicationReceipt
{
    public DeliveryAttemptStatus Status { get; set; }
    public string? ExternalId { get; set; }
    public string? Error { get; set; }
    public bool Retryable { get; set; }
}
