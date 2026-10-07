using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JobFinder.MessagingProbe;

public sealed class CommentDeliveryProbeTransport(IDbContextFactory<JobFinderDbContext> db, Guid? id = null, string? boundary = null) : ICommentDeliveryTransport
{
    public static string CounterKey(Guid id) => "probe.comment.delivery." + id.ToString("N");
    public ThreadsPublicationRoute CaptureRoute(ThreadsPost post) => new() { Mode = ThreadsPublicationMode.Browser, Endpoint = "https://www.threads.com" };
    public Task<ThreadsPreparedPublication> PrepareAsync(CommentDeliveryInput input, ThreadsPreparedPublication? saved, CancellationToken ct) =>
        Task.FromResult(id is null ? new ThreadsPreparedPublication { State = ThreadsPreparationState.Waiting, RetrySeconds = 300 }
            : new ThreadsPreparedPublication { State = ThreadsPreparationState.Ready, Username = "candidate", AccountId = "candidate" });

    public async Task<ThreadsPublicationReceipt> PublishAsync(CommentDeliveryInput input, ThreadsPreparedPublication prepared, Func<CancellationToken, Task<bool>> mayPublish, CancellationToken ct)
    {
        if (id is not { } request || input.Source.Id <= 0) throw new InvalidOperationException("process probe refuses an unexpected publication");
        if (boundary == "before-effect") await PauseAsync(ct);
        if (!await mayPublish(ct)) return new() { Status = DeliveryAttemptStatus.KnownNotSent, Retryable = true };
        await using var context = await db.CreateDbContextAsync(ct);
        var key = CounterKey(request);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO settings (key, value, updated_at) VALUES ({key}, '1', now())
            ON CONFLICT (key) DO UPDATE SET value = (settings.value::int + 1)::text, updated_at = now()
            """, ct);
        if (boundary == "after-effect") await PauseAsync(ct);
        return new() { Status = DeliveryAttemptStatus.Confirmed, ExternalId = "https://www.threads.com/@candidate/post/receipt" };
    }

    internal static async Task PauseAsync(CancellationToken ct)
    {
        Console.WriteLine("READY");
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}

public sealed class CommentDeliveryProbeCheckpoint(Guid id, CommentDeliveryStage phase, string boundary) : DbTransactionInterceptor
{
    private int _armed = 1;
    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
    {
        if (boundary == "before-checkpoint") await PauseAsync(eventData.Context!, ct);
        return result;
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) =>
        boundary == "after-checkpoint" ? PauseAsync(eventData.Context!, ct) : Task.CompletedTask;

    private Task PauseAsync(DbContext context, CancellationToken ct) =>
        context.ChangeTracker.Entries<CommentDeliveryRequest>().Any(entry => entry.Entity.Id == id && entry.Entity.Stage == phase)
            && Interlocked.Exchange(ref _armed, 0) == 1 ? CommentDeliveryProbeTransport.PauseAsync(ct) : Task.CompletedTask;
}
