using System.Collections.Concurrent;

namespace JobFinder.Llm.Client;

public enum LlmFailureKind
{
    /// <summary>429 or a quota message: the model (or its free pool) is out of budget for a while.</summary>
    RateLimited,

    /// <summary>5xx, timeout, connection failure: the upstream is sick.</summary>
    Unavailable,

    /// <summary>200 with no usable answer: empty, truncated, not JSON, or JSON that fails validation.</summary>
    BadResponse,

    /// <summary>400/403/404 about the model itself (removed, not allowed, no provider supports the parameters).</summary>
    Rejected,

    /// <summary>401 or 402: the key is invalid or out of credits — no model can work, the whole client stops.</summary>
    Unauthorized,
}

/// <summary>
/// One circuit breaker per model. A failure that says "this model, for a while" parks only that model; the next call skips it and the
/// route moves on. Cooldowns escalate while the failures repeat and reset on the first success.
/// </summary>
public sealed class ModelHealth(TimeProvider time)
{
    private sealed class State
    {
        public int ConsecutiveFailures;
        public int Strikes;
        public DateTimeOffset BlockedUntil;
        public LlmFailureKind? LastKind;
        public string? LastError;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public bool IsAvailable(string model) => !_states.TryGetValue(model, out var s) || s.BlockedUntil <= time.GetUtcNow();

    public void ReportSuccess(string model)
    {
        var s = _states.GetOrAdd(model, _ => new State());
        lock (s)
        {
            s.ConsecutiveFailures = 0;
            s.Strikes = 0;
            s.BlockedUntil = default;
            s.LastKind = null;
            s.LastError = null;
        }
    }

    /// <returns>The cooldown applied (zero when the failure is tolerated once).</returns>
    public TimeSpan ReportFailure(string model, LlmFailureKind kind, TimeSpan? retryAfter, string error)
    {
        var s = _states.GetOrAdd(model, _ => new State());
        lock (s)
        {
            s.ConsecutiveFailures++;
            s.LastKind = kind;
            s.LastError = error;
            var cooldown = kind switch
            {
                LlmFailureKind.RateLimited => retryAfter ?? Escalate(s, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15)),
                LlmFailureKind.Rejected => TimeSpan.FromMinutes(30),
                // One timeout or one bad answer can be noise: park only when it repeats.
                LlmFailureKind.Unavailable or LlmFailureKind.BadResponse when s.ConsecutiveFailures >= 2 =>
                    Escalate(s, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)),
                _ => TimeSpan.Zero,
            };
            if (cooldown > TimeSpan.Zero) s.BlockedUntil = time.GetUtcNow() + cooldown;
            return cooldown;
        }
    }

    public IReadOnlyList<ModelHealthSnapshot> Snapshot()
    {
        var now = time.GetUtcNow();
        return _states.Select(kv =>
        {
            lock (kv.Value)
            {
                var left = kv.Value.BlockedUntil - now;
                return new ModelHealthSnapshot(kv.Key, left > TimeSpan.Zero ? left : null, kv.Value.ConsecutiveFailures, kv.Value.LastKind, kv.Value.LastError);
            }
        }).OrderBy(s => s.Model, StringComparer.Ordinal).ToList();
    }

    private static TimeSpan Escalate(State s, TimeSpan first, TimeSpan max)
    {
        var factor = Math.Pow(2, Math.Min(s.Strikes++, 10));
        var next = TimeSpan.FromTicks((long)(first.Ticks * factor));
        return next < max ? next : max;
    }
}

public sealed record ModelHealthSnapshot(string Model, TimeSpan? BlockedFor, int ConsecutiveFailures, LlmFailureKind? LastKind, string? LastError);
