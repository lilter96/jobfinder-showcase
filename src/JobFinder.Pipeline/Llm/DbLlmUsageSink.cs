using JobFinder.Core.Domain;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Llm.Client;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Llm;

/// <summary>Every LLM attempt as a row of <c>llm_calls</c>; today's spend is summed from them in the user's time zone.</summary>
public sealed class DbLlmUsageSink(IDbContextFactory<JobFinderDbContext> db, LocalClock clock) : ILlmUsageSink
{
    public async Task RecordAsync(LlmAttempt attempt, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        ctx.LlmCalls.Add(new LlmCall
        {
            OperationId = attempt.OperationId,
            CallId = attempt.CallId,
            AttemptNumber = attempt.AttemptNumber,
            RequestedModel = attempt.RequestedModel,
            Recipe = attempt.Recipe,
            Purpose = attempt.Purpose,
            Model = attempt.Model,
            PromptTokens = attempt.PromptTokens,
            CompletionTokens = attempt.CompletionTokens,
            CostUsd = attempt.CostUsd,
            LatencyMs = attempt.LatencyMs,
            Ok = attempt.Ok,
            Error = attempt.Error is { Length: > 500 } e ? e[..500] : attempt.Error,
            At = clock.UtcNow,
        });
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<decimal> SpentTodayUsdAsync(CancellationToken ct)
    {
        var since = clock.StartOfTodayUtc();
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.LlmCalls.Where(c => c.At >= since).SumAsync(c => c.CostUsd, ct);
    }
}
