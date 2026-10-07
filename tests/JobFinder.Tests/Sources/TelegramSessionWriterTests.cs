using JobFinder.Integrations.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Sources;

/// <summary>
/// WTelegramClient hands its session over synchronously, inside its locks, on its network threads; the writer stores it in the background.
/// </summary>
public sealed class TelegramSessionWriterTests
{
    private sealed class Store(int failures = 0) : ITelegramSessionStore
    {
        private int _failures = failures;

        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Gate { get; set; }

        public List<byte[]> Saved { get; } = [];

        public Task<byte[]?> LoadAsync(CancellationToken ct) => Task.FromResult(Saved.LastOrDefault());

        public async Task SaveAsync(byte[] session, CancellationToken ct)
        {
            FirstWrite.TrySetResult();
            if (Gate is { } gate) await gate.Task;
            if (Interlocked.Decrement(ref _failures) >= 0) throw new InvalidOperationException("the database is away");
            lock (Saved) Saved.Add(session);
        }
    }

    [Fact]
    public async Task Sessions_handed_over_during_a_write_collapse_into_the_latest()
    {
        var store = new Store { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var writer = new SessionWriter(store, NullLogger.Instance);

        writer.Enqueue([1]);
        await store.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        writer.Enqueue([2]);
        writer.Enqueue([3]); // the client saved twice while the first write was in flight
        store.Gate.SetResult();

        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([[1], [3]], store.Saved);
    }

    [Fact]
    public async Task A_failed_write_is_retried_until_the_session_is_stored()
    {
        var store = new Store(failures: 1);
        var writer = new SessionWriter(store, NullLogger.Instance);

        writer.Enqueue([7, 7]);

        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal([7, 7], Assert.Single(store.Saved));
    }

    [Fact]
    public async Task A_flush_with_nothing_handed_over_returns_at_once()
    {
        var writer = new SessionWriter(new Store(), NullLogger.Instance);

        Assert.True(await writer.FlushAsync(TimeSpan.FromMilliseconds(50)));
    }
}
