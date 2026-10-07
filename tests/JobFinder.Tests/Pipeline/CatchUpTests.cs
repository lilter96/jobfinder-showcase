using JobFinder.Pipeline.Jobs;

namespace JobFinder.Tests.Pipeline;

/// <summary>A time-of-day job whose slot passed while the app was down runs on start (the 19:00 digest of 2026-09-28 was lost).</summary>
public sealed class CatchUpTests
{
    // 2026-09-28 22:15 Minsk = 19:15 UTC; the missed slot 19:00 Minsk = 16:00 UTC; the next one 09:00 Minsk tomorrow.
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 19, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Slot = new(2026, 9, 28, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Next = new(2026, 9, 29, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(12);

    [Fact]
    public void The_missed_evening_digest_runs_on_start() =>
        Assert.True(ScheduledJob.ShouldCatchUp(Now, Slot, Next, lastOkStart: Slot.AddHours(-10), Window));

    [Fact]
    public void A_job_that_never_ran_catches_up_too() =>
        Assert.True(ScheduledJob.ShouldCatchUp(Now, Slot, Next, lastOkStart: null, Window));

    [Fact]
    public void A_slot_that_did_run_is_not_repeated()
    {
        Assert.False(ScheduledJob.ShouldCatchUp(Now, Slot, Next, lastOkStart: Slot.AddSeconds(-1), Window)); // started a hair early
        Assert.False(ScheduledJob.ShouldCatchUp(Now, Slot, Next, lastOkStart: Slot.AddHours(1), Window)); // run by hand after it
    }

    [Fact]
    public void A_stale_slot_is_dropped() =>
        Assert.False(ScheduledJob.ShouldCatchUp(Slot.AddHours(13), Slot, Slot.AddHours(14), null, Window));

    [Fact]
    public void When_the_next_slot_is_near_it_is_waited_for() =>
        Assert.False(ScheduledJob.ShouldCatchUp(Now, Slot, Now.AddMinutes(40), null, Window));

    [Fact]
    public void Interval_jobs_have_no_slot() =>
        Assert.False(ScheduledJob.ShouldCatchUp(Now, null, Next, null, Window));
}
