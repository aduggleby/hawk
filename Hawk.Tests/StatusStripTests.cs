using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Monitoring;

namespace Hawk.Tests;

public sealed class StatusStripTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 7, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.NewGuid();

    private static MonitorRunBucket B(DateTimeOffset start, int runs, int failures, long totalMs) =>
        new() { MonitorId = Id, BucketStart = start, RunCount = runs, FailureCount = failures, TotalDurationMs = totalMs };

    [Fact]
    public void Window_ends_with_the_current_interval()
    {
        var start = StatusStrip.WindowStart(Now);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 12, 15, 0, TimeSpan.Zero), start);
        var strip = StatusStrip.Build([], Now, heartbeat: false);
        Assert.Equal(96, strip.Cells.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), strip.Cells[^1].Start);
        Assert.All(strip.Cells, c => Assert.Equal(StripCellState.Empty, c.State));
        Assert.Equal("Last 24 hours: 0 of 96 intervals failed", strip.AriaLabel);
    }

    [Fact]
    public void Failures_are_full_height_and_others_scale_with_a_minimum()
    {
        var last = StatusStrip.WindowStart(Now) + TimeSpan.FromMinutes(15 * 95);
        var strip = StatusStrip.Build(
        [
            B(last, 2, 1, 100),
            B(last - TimeSpan.FromMinutes(15), 1, 0, 1000),
            B(last - TimeSpan.FromMinutes(30), 1, 0, 100),
            B(last - TimeSpan.FromMinutes(45), 1, 0, 500),
            B(StatusStrip.WindowStart(Now) - TimeSpan.FromMinutes(15), 1, 1, 10),
        ], Now, heartbeat: false);

        Assert.Equal(StripCellState.Fail, strip.Cells[95].State);
        Assert.Equal(100, strip.Cells[95].HeightPercent);
        Assert.Equal(100, strip.Cells[94].HeightPercent);
        Assert.Equal(25, strip.Cells[93].HeightPercent);
        Assert.Equal(50, strip.Cells[92].HeightPercent);
        Assert.Equal(500, strip.Cells[92].AverageMs);
        Assert.Equal(1, strip.FailedCount);
        Assert.Equal(StripCellState.Empty, strip.Cells[0].State);
    }

    [Fact]
    public void Heartbeat_cells_are_full_height()
    {
        var last = StatusStrip.WindowStart(Now) + TimeSpan.FromMinutes(15 * 95);
        var strip = StatusStrip.Build([B(last, 1, 0, 1), B(last - TimeSpan.FromMinutes(15), 1, 0, 1000)], Now, heartbeat: true);
        Assert.Equal(100, strip.Cells[95].HeightPercent);
        Assert.True(strip.Heartbeat);
    }
}
