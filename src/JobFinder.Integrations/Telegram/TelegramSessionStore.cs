namespace JobFinder.Integrations.Telegram;

/// <summary>Where the MTProto session of the user account lives (the database; WTelegramClient's own session file is not used).</summary>
public interface ITelegramSessionStore
{
    /// <summary>The session as WTelegramClient last wrote it; null = never logged in.</summary>
    Task<byte[]?> LoadAsync(CancellationToken ct);

    Task SaveAsync(byte[] session, CancellationToken ct);
}

/// <summary>
/// Takes the session WTelegramClient hands over — synchronously, inside its own locks, on its network threads — and writes it to the store
/// in the background: the latest bytes win, a failed write is retried, and <see cref="FlushAsync"/> waits for the last one (the client
/// being disposed). The session changes rarely (a login, a data center, new salts), so a write rarely waits behind another.
/// </summary>
internal sealed class SessionWriter(ITelegramSessionStore store, ILogger log)
{
    private readonly Lock _lock = new();
    private byte[]? _pending;
    private bool _running;
    private Task _writing = Task.CompletedTask;

    /// <summary>WTelegramClient's save callback: never blocks, never throws.</summary>
    public void Enqueue(byte[] session)
    {
        lock (_lock)
        {
            _pending = session;
            if (_running) return;
            _running = true;
            _writing = Task.Run(WriteAsync);
        }
    }

    /// <summary>Waits until the last session handed over is stored (or <paramref name="timeout"/> passes: the store is down).</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        Task writing;
        lock (_lock) writing = _writing;
        try
        {
            await writing.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            log.Error($"[telegram] the session was not stored within {timeout.TotalSeconds:0} s: the next start may need --login-telegram");
            return false;
        }
    }

    private async Task WriteAsync()
    {
        for (var failures = 0; ;)
        {
            byte[] session;
            lock (_lock)
            {
                if (_pending is null)
                {
                    _running = false;
                    return;
                }

                session = _pending;
                _pending = null;
            }

            try
            {
                await store.SaveAsync(session, CancellationToken.None);
                if (failures > 0) log.Info($"[telegram] session stored after {failures} failed attempts");
                failures = 0;
            }
            catch (Exception e)
            {
                // Kept for the next attempt unless a newer session arrived meanwhile.
                lock (_lock) _pending ??= session;
                if (failures++ % 10 == 0) log.Warn($"[telegram] session not stored (attempt {failures}), retrying: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, failures * 2)));
            }
        }
    }
}
