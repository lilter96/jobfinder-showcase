using JobFinder.Integrations.Threads;

namespace JobFinder.Pipeline.Modules.Engagement;

public interface ICommentDeliveryTransport
{
    ThreadsPublicationRoute CaptureRoute(ThreadsPost post);
    Task<ThreadsPreparedPublication> PrepareAsync(CommentDeliveryInput input, ThreadsPreparedPublication? saved, CancellationToken ct);
    Task<ThreadsPublicationReceipt> PublishAsync(CommentDeliveryInput input, ThreadsPreparedPublication prepared,
        Func<CancellationToken, Task<bool>> mayPublish, CancellationToken ct);
}

public sealed class CommentDeliveryTransport(ThreadsApiClient api, ThreadsBrowserClient browser, ThreadsGateway gateway, ThreadsOptions options) : ICommentDeliveryTransport
{
    public ThreadsPublicationRoute CaptureRoute(ThreadsPost post) => gateway.UsesApi && (options.Mode.Equals("Api", StringComparison.OrdinalIgnoreCase)
        || post.Id is { Length: > 0 } && post.Id.All(char.IsAsciiDigit))
        ? ThreadsPublicationRoute.Api(options.ApiBase)
        : new() { Mode = ThreadsPublicationMode.Browser, Endpoint = "https://www.threads.com" };

    public Task<ThreadsPreparedPublication> PrepareAsync(CommentDeliveryInput input, ThreadsPreparedPublication? saved, CancellationToken ct)
    {
        if (!Compatible(input)) return Task.FromResult((saved ?? new()) with
        {
            State = ThreadsPreparationState.Waiting, Error = "captured Threads route changed; no fallback is authorized", RetrySeconds = 300,
        });
        return input.Route.Mode == ThreadsPublicationMode.Api ? api.PreparePublicationAsync(input.Source.Post(), input.Source.Text, input.Route, saved, ct)
            : browser.PreparePublicationAsync(input.Source.Post(), input.Source.Text, saved, ct);
    }

    public Task<ThreadsPublicationReceipt> PublishAsync(CommentDeliveryInput input, ThreadsPreparedPublication prepared,
        Func<CancellationToken, Task<bool>> mayPublish, CancellationToken ct)
    {
        if (!Compatible(input)) return Task.FromResult(new ThreadsPublicationReceipt
        {
            Status = Core.Domain.DeliveryAttemptStatus.KnownNotSent, Error = "captured Threads route changed; no fallback is authorized",
        });
        return input.Route.Mode == ThreadsPublicationMode.Api ? api.PublishPreparedAsync(input.Route, prepared, mayPublish, ct)
            : browser.PublishPreparedAsync(input.Source.Post(), input.Source.Text, prepared, mayPublish, ct);
    }

    private bool Compatible(CommentDeliveryInput input) => CaptureRoute(input.Source.Post()) == input.Route;
}
