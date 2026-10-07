using JobFinder.Integrations.Browser;
using Npgsql;

namespace JobFinder.Pipeline;

/// <summary>
/// <see cref="IBrowserSiteLock"/> over a Postgres advisory lock on a dedicated connection: the server and a one-off CLI container wait
/// for each other instead of driving the same site login from two browsers at once.
/// </summary>
public sealed class BrowserSiteLock(DatabaseOptions database, ILogger<BrowserSiteLock> log) : IBrowserSiteLock
{
    public async Task<IAsyncDisposable> AcquireAsync(string site, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(ct);
        var key = $"jobfinder:browser:{site}";
        await using (var probe = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@k))", connection))
        {
            probe.Parameters.AddWithValue("k", key);
            if (await probe.ExecuteScalarAsync(ct) is not true)
            {
                log.Info($"[browser] {site}: another process is using this login, waiting");
                await using var wait = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext(@k))", connection) { CommandTimeout = 600 };
                wait.Parameters.AddWithValue("k", key);
                await wait.ExecuteNonQueryAsync(ct);
            }
        }

        return new Release(connection);
    }

    private sealed class Release(NpgsqlConnection connection) : IAsyncDisposable
    {
        // Closing the unpooled connection ends the session and with it the lock.
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
