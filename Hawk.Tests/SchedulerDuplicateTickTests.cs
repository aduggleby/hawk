namespace Hawk.Tests;

public class SchedulerDuplicateTickTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, false)]      // first tick ever
    [InlineData(-5.0, true)]       // second chain 5 s later
    [InlineData(-22.4, true)]      // just under 3/4 of 30 s
    [InlineData(-22.5, false)]
    [InlineData(-30.0, false)]     // the chain's own next tick
    [InlineData(-3600.0, false)]   // after downtime
    [InlineData(10.0, false)]      // last tick in the future (clock change): continue
    public void Duplicate_ticks_are_less_than_three_quarters_of_an_interval_apart(double? lastOffsetSeconds, bool expected)
    {
        DateTimeOffset? last = lastOffsetSeconds is { } o ? Now.AddSeconds(o) : null;
        Assert.Equal(expected, Hawk.Web.Services.Monitoring.MonitorScheduler.IsDuplicateTick(last, Now, 30));
    }

    [Theory]
    [InlineData(null, true)]       // never ticked
    [InlineData(-45.0, false)]     // live chain, late poll
    [InlineData(-150.0, false)]    // exactly 3 x 30 s + 60 s
    [InlineData(-151.0, true)]
    [InlineData(5.0, false)]       // slightly in the future (clock skew)
    [InlineData(31.0, true)]       // more than one interval in the future: the clock went back
    public void Chain_is_stale_after_three_intervals_and_a_minute(double? lastOffsetSeconds, bool expected)
    {
        DateTimeOffset? last = lastOffsetSeconds is { } o ? Now.AddSeconds(o) : null;
        Assert.Equal(expected, Hawk.Web.Services.Monitoring.MonitorScheduler.IsChainStale(last, Now, 30));
    }
}
