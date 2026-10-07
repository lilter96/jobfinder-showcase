using JobFinder.Pipeline.Career;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

/// <summary>The race behind the letter writer: a perfect draft ends the round on the spot, failures never sink it.</summary>
public sealed class DraftRaceTests
{
    [Fact]
    public async Task A_synchronous_failure_does_not_prevent_other_drafts()
    {
        var finished = await DraftRace.RunAsync(2,
            (draftIndex, token) => draftIndex == 0 ? throw new InvalidOperationException("failed") : Task.FromResult("good"),
            _ => true, _ => true, TimeSpan.Zero, TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Equal("good", Assert.Single(finished));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_success()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DraftRace.RunAsync(2,
            (draftIndex, token) => Task.FromResult("good"), _ => true, _ => true,
            TimeSpan.Zero, TimeProvider.System, cancellation.Token));
    }

    [Fact]
    public async Task A_good_draft_survives_the_grace_deadline()
    {
        async Task<string> Draft(int draftIndex, CancellationToken token)
        {
            if (draftIndex == 0) return "good";
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "slow";
        }

        var finished = await DraftRace.RunAsync(2, Draft, _ => false, value => value == "good",
            TimeSpan.Zero, TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Equal("good", Assert.Single(finished));
    }

    [Fact]
    public async Task A_perfect_draft_ends_the_round_and_cancels_the_slow_one()
    {
        var time = new FakeTimeProvider();
        var slowCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Draft(int i, CancellationToken ct)
        {
            if (i == 0)
            {
                await Task.Delay(10, ct);
                return "good";
            }

            try
            {
                await Task.Delay(30000, ct);
                return "slow";
            }
            catch (OperationCanceledException)
            {
                slowCancelled.SetResult(true);
                throw;
            }
        }

        var ct = TestContext.Current.CancellationToken;
        var finished = await DraftRace.RunAsync(2, Draft, perfect: s => s == "good", good: _ => false,
            TimeSpan.FromSeconds(20), time, ct);

        Assert.Single(finished);
        Assert.Equal("good", finished[0]);
        Assert.True(await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    [Fact]
    public async Task A_failed_draft_is_left_out_when_another_succeeds()
    {
        var time = new FakeTimeProvider();
        var ct = TestContext.Current.CancellationToken;
        async Task<string> Draft(int i, CancellationToken ct)
        {
            await Task.Delay(10, ct);
            return i == 0 ? throw new InvalidOperationException("boom") : "good";
        }

        var finished = await DraftRace.RunAsync(2, Draft, perfect: _ => true, good: _ => false,
            TimeSpan.FromSeconds(20), time, ct);

        Assert.Single(finished);
        Assert.Equal("good", finished[0]);
    }

    [Fact]
    public async Task When_every_draft_fails_the_last_failure_is_thrown()
    {
        var time = new FakeTimeProvider();
        var ct = TestContext.Current.CancellationToken;
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Draft(int draftIndex, CancellationToken token) => draftIndex == 0
            ? Task.FromException<string>(new InvalidOperationException("first")) : second.Task.WaitAsync(token);

        var running = DraftRace.RunAsync(2, Draft, perfect: _ => true, good: _ => false, TimeSpan.FromSeconds(20), time, ct);
        Assert.False(running.IsCompleted);
        second.SetException(new InvalidOperationException("second"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5), ct));

        Assert.Equal("second", error.Message);
    }
}
