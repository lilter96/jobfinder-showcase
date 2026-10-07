using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Ingest;

/// <summary>
/// Keeps only people among a vacancy's Telegram contacts. A post's links often name channels — its own network's footer ("База знаний",
/// "Задачи", "Собеседования" → @csharpproglib…, seen on every post of a channel) or a company channel — and a DM to a channel fails.
/// Telegram decides what a @username is; the answer is kept in <c>telegram_usernames</c> (Telegram rate-limits resolving hard, and a footer
/// repeats the same handles), resolving has its own hourly budget, and a flood wait stops it for as long as Telegram asks. A handle that
/// cannot be checked now stays: an unverified recruiter is worth more than a lost one.
/// </summary>
public sealed class TelegramContactVerifier(IDbContextFactory<JobFinderDbContext> db, ITelegramUsernames telegram, TimeProvider time,
    ILogger<TelegramContactVerifier> log, int resolvesPerHour = 30)
{
    /// <summary>A person or a channel keeps its username for long; a free one may be taken tomorrow.</summary>
    private static readonly TimeSpan KnownFor = TimeSpan.FromDays(30);

    private static readonly TimeSpan MissingFor = TimeSpan.FromDays(3);

    private readonly ConcurrentDictionary<string, TelegramUsername> _memory = new(StringComparer.OrdinalIgnoreCase);

    private readonly TokenBucketRateLimiter _budget = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = resolvesPerHour, TokensPerPeriod = resolvesPerHour, ReplenishmentPeriod = TimeSpan.FromHours(1), AutoReplenishment = true, QueueLimit = 0,
    });

    private DateTimeOffset _floodUntil;

    /// <summary>The contacts without Telegram channels, groups and missing handles; a person gets their name as the label when it had none.</summary>
    public async Task<List<VacancyContact>> VerifyAsync(IEnumerable<VacancyContact> contacts, CancellationToken ct)
    {
        var kept = new List<VacancyContact>();
        foreach (var c in contacts)
        {
            if (c.Kind != ContactKind.Telegram || !c.Value.StartsWith('@'))
            {
                kept.Add(c);
                continue;
            }

            switch (await WhatIsAsync(c.Value, ct))
            {
                case null:
                    kept.Add(c);
                    break;
                case { Kind: TelegramPeerKind.Person } person:
                    c.Label ??= person.Name;
                    kept.Add(c);
                    break;
                case var other:
                    log.Debug($"[contacts] {c.Value} is a {other.Kind} ({other.Name}), not a contact");
                    break;
            }
        }

        return kept;
    }

    /// <summary>What the username is, from memory, the table or Telegram; null when it cannot be told now (not connected, budget, flood).</summary>
    public async Task<TelegramUsername?> WhatIsAsync(string username, CancellationToken ct)
    {
        var key = username.Trim().TrimStart('@').ToLowerInvariant();
        var now = time.GetUtcNow();
        if (_memory.TryGetValue(key, out var remembered) && Fresh(remembered, now)) return remembered;

        TelegramUsername? stored;
        await using (var ctx = await db.CreateDbContextAsync(ct))
        {
            stored = await ctx.TelegramUsernames.AsNoTracking().FirstOrDefaultAsync(u => u.Username == key, ct);
        }

        if (stored is not null && Fresh(stored, now)) return _memory[key] = stored;
        // A stale answer is still better than none when Telegram cannot be asked.
        if (!telegram.IsReady || now < _floodUntil) return stored;
        using var lease = _budget.AttemptAcquire();
        if (!lease.IsAcquired) return stored;

        (TelegramPeerKind Kind, string? Name) resolved;
        try
        {
            resolved = await telegram.ResolveKindAsync(key);
        }
        catch (TelegramFloodException e)
        {
            _floodUntil = now + e.RetryAfter;
            log.Warn($"[contacts] resolving paused for {e.RetryAfter.TotalMinutes:0} min: {e.Message}");
            return stored;
        }

        var row = new TelegramUsername { Username = key, Kind = resolved.Kind, Name = resolved.Name, ResolvedAt = now };
        await using (var ctx = await db.CreateDbContextAsync(ct))
        {
            if (await ctx.TelegramUsernames.AnyAsync(u => u.Username == key, ct)) ctx.TelegramUsernames.Update(row);
            else ctx.TelegramUsernames.Add(row);
            await ctx.SaveChangesAsync(ct);
        }

        return _memory[key] = row;
    }

    private static bool Fresh(TelegramUsername u, DateTimeOffset now) => now - u.ResolvedAt < (u.Kind == TelegramPeerKind.Missing ? MissingFor : KnownFor);
}
