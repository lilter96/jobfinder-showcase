using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Modules.Engagement;

namespace JobFinder.Tests.Pipeline;

internal sealed class DormantCommentTransport : ICommentDeliveryTransport
{
    public ThreadsPublicationRoute CaptureRoute(ThreadsPost post) => new() { Mode = ThreadsPublicationMode.Browser, Endpoint = "https://www.threads.com" };
    public Task<ThreadsPreparedPublication> PrepareAsync(CommentDeliveryInput input, ThreadsPreparedPublication? saved, CancellationToken ct) =>
        Task.FromResult(new ThreadsPreparedPublication { State = ThreadsPreparationState.Waiting, RetrySeconds = 300 });
    public Task<ThreadsPublicationReceipt> PublishAsync(CommentDeliveryInput input, ThreadsPreparedPublication prepared,
        Func<CancellationToken, Task<bool>> mayPublish, CancellationToken ct) => throw new InvalidOperationException("this fixture must never publish comments");
}

internal sealed class NoThreadsNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new InvalidOperationException("synthetic fixture must not make a real Threads request");
}
