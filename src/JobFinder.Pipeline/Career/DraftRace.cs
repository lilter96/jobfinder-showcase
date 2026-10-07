using System.Runtime.ExceptionServices;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// Several drafts of the same text written at once, each judged as soon as it is done. A perfect one ends the round on the spot (the rest
/// are cancelled); a good one gives the others a short grace to beat it. A round then lasts as long as its fastest good draft, not the
/// slowest model call, and a call that hangs until its timeout no longer holds the text back.
/// </summary>
public static class DraftRace
{
    /// <param name="draft">Writes and judges draft number i (0-based); the token is cancelled when the round is decided.</param>
    /// <returns>The drafts that finished, in the order they finished. A draft that failed is left out; when every one failed, the last failure is thrown.</returns>
    public static async Task<List<T>> RunAsync<T>(int count, Func<int, CancellationToken, Task<T>> draft, Func<T, bool> perfect, Func<T, bool> good,
        TimeSpan grace, TimeProvider time, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ct.ThrowIfCancellationRequested();
        using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task<T> StartAsync(int draftIndex) => await draft(draftIndex, round.Token);
        var pending = Enumerable.Range(0, count).Select(StartAsync).ToList();
        var finished = new List<T>();
        Exception? failure = null;
        Task? deadline = null;
        try
        {
            while (pending.Count > 0)
            {
                var next = deadline is null ? await Task.WhenAny(pending) : await Task.WhenAny([.. pending, deadline]);
                if (next == deadline) break;
                var task = (Task<T>)next;
                pending.Remove(task);
                try
                {
                    finished.Add(await task);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    failure = e;
                    continue;
                }

                if (perfect(finished[^1])) break;
                if (good(finished[^1])) deadline ??= Task.Delay(grace, time, round.Token);
            }
        }
        finally
        {
            await round.CancelAsync();
            // The losers end at once (their HTTP requests are aborted); waiting for them leaves no exception unobserved.
            foreach (var loser in pending)
            {
                try
                {
                    await loser;
                }
                catch (Exception e) when (e is OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Cancelled because the round was decided, or failed after it: either way not part of the result.
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        if (finished.Count == 0 && failure is not null) ExceptionDispatchInfo.Throw(failure);
        return finished;
    }
}
