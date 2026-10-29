// <file>
// <summary>
// The 24 h status strip (96 cells of 15 minutes) built from MonitorRunBuckets. Shared by the monitors overview and
// the details pages; rendered by Pages/Shared/_StatusStrip.cshtml.
// </summary>
// </file>

using Hawk.Web.Data.Monitoring;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// State of one strip cell.
/// </summary>
public enum StripCellState
{
    /// <summary>No completed runs in the interval.</summary>
    Empty,

    /// <summary>All runs passed.</summary>
    Ok,

    /// <summary>At least one run failed.</summary>
    Fail,
}

/// <summary>
/// One 15-minute cell of the strip.
/// </summary>
/// <param name="Start">Interval start (UTC).</param>
/// <param name="State">Cell state.</param>
/// <param name="Runs">Completed runs in the interval.</param>
/// <param name="Failures">Failed runs in the interval.</param>
/// <param name="AverageMs">Average run duration, null without runs.</param>
/// <param name="HeightPercent">Bar height (25-100; failures and heartbeats 100, empty cells 0).</param>
public sealed record StripCell(DateTimeOffset Start, StripCellState State, int Runs, int Failures, int? AverageMs, int HeightPercent);

/// <summary>
/// A built strip: 96 cells, oldest first, plus its text alternative.
/// </summary>
/// <param name="Cells">The cells, oldest first.</param>
/// <param name="FailedCount">Cells with at least one failure.</param>
/// <param name="Heartbeat">True for heartbeat monitors (cells are pings, full height).</param>
public sealed record StatusStripModel(IReadOnlyList<StripCell> Cells, int FailedCount, bool Heartbeat)
{
    /// <summary>
    /// Text alternative for assistive technology.
    /// </summary>
    public string AriaLabel => $"Last 24 hours: {FailedCount} of {StatusStrip.CellCount} intervals failed";
}

/// <summary>
/// Builds <see cref="StatusStripModel"/> values.
/// </summary>
public static class StatusStrip
{
    /// <summary>Number of cells (24 h of 15-minute intervals).</summary>
    public const int CellCount = 96;

    private const int MinHeightPercent = 25;

    /// <summary>
    /// Start of the oldest cell for a strip ending with the interval that contains <paramref name="now"/>.
    /// Load buckets with <c>BucketStart &gt;= WindowStart(now)</c>.
    /// </summary>
    public static DateTimeOffset WindowStart(DateTimeOffset now) =>
        RunBucketRollup.BucketStartOf(now) - RunBucketRollup.BucketLength * (CellCount - 1);

    /// <summary>
    /// Builds the strip of one monitor from its buckets (buckets outside the window, including future ones, are ignored).
    /// </summary>
    public static StatusStripModel Build(IEnumerable<MonitorRunBucket> buckets, DateTimeOffset now, bool heartbeat)
    {
        var start = WindowStart(now);
        var current = RunBucketRollup.BucketStartOf(now);
        var byStart = buckets
            .Where(b => b.BucketStart >= start && b.BucketStart <= current && b.RunCount > 0)
            .GroupBy(b => b.BucketStart.UtcTicks)
            .ToDictionary(g => g.Key, g => g.First());

        // Heights are relative to the slowest bucket average of this monitor in the window.
        var maxAverage = byStart.Values.Select(b => b.TotalDurationMs / (double)b.RunCount).DefaultIfEmpty(0).Max();

        var cells = new List<StripCell>(CellCount);
        for (var i = 0; i < CellCount; i++)
        {
            var cellStart = start + RunBucketRollup.BucketLength * i;
            if (!byStart.TryGetValue(cellStart.UtcTicks, out var b))
            {
                cells.Add(new StripCell(cellStart, StripCellState.Empty, 0, 0, null, 0));
                continue;
            }

            var average = b.TotalDurationMs / (double)b.RunCount;
            var failed = b.FailureCount > 0;
            var height = failed || heartbeat || maxAverage <= 0
                ? 100
                : Math.Max(MinHeightPercent, (int)Math.Round(average / maxAverage * 100));
            cells.Add(new StripCell(cellStart, failed ? StripCellState.Fail : StripCellState.Ok, b.RunCount, b.FailureCount,
                (int)Math.Round(average), height));
        }

        return new StatusStripModel(cells, cells.Count(c => c.State == StripCellState.Fail), heartbeat);
    }
}
