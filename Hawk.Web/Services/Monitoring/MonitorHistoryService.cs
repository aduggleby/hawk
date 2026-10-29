// <file>
// <summary>
// Details page data of one monitor, built from the MonitorRunBuckets rollup (which survives run pruning): the 24 h strip,
// the 24 h response-time chart, 90 UTC day cells, uptime figures and the "UP since" value. The status lead is a pure function.
// </summary>
// </file>

using System.Globalization;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

/// <summary>Color state of one day cell (R12).</summary>
public enum DayState
{
    /// <summary>No runs that day.</summary>
    None,

    /// <summary>100% passed.</summary>
    Up,

    /// <summary>99% to under 100%.</summary>
    UpDim,

    /// <summary>95% to under 99%.</summary>
    DownTint,

    /// <summary>Under 95%.</summary>
    Down,
}

/// <summary>One UTC day of the 90-day row.</summary>
/// <param name="Day">UTC midnight of the day.</param>
/// <param name="Runs">Completed runs.</param>
/// <param name="Failures">Failed runs.</param>
/// <param name="UptimePercent">Percent of passed runs, null without runs.</param>
/// <param name="State">Color state.</param>
public sealed record DayCell(DateTimeOffset Day, int Runs, int Failures, double? UptimePercent, DayState State);

/// <summary>Uptime (percent of passed completed runs) over the last 24 h, 7 d and 30 d; null without runs.</summary>
public sealed record UptimeFigures(double? Last24h, double? Last7d, double? Last30d);

/// <summary>One data point of the response-time chart.</summary>
/// <param name="X">X in chart units.</param>
/// <param name="Y">Y in chart units.</param>
/// <param name="Failed">The interval had failures.</param>
/// <param name="Isolated">No neighbour with data, so no line reaches the point.</param>
/// <param name="Cell">The strip cell.</param>
public sealed record ChartPoint(double X, double Y, bool Failed, bool Isolated, StripCell Cell);

/// <summary>A horizontal grid line.</summary>
public sealed record ChartGridLine(double Y, int Ms);

/// <summary>Server-rendered SVG data of the 24 h response-time chart.</summary>
public sealed record ResponseChart(
    int Width, int Height, double CellWidth, IReadOnlyList<string> Paths, IReadOnlyList<ChartPoint> Points,
    IReadOnlyList<ChartGridLine> Grid, int FailedCount)
{
    /// <summary>Text alternative.</summary>
    public string AriaLabel => $"Response time, last 24 hours: {Points.Count} intervals with data, {FailedCount} with failures";
}

/// <summary>Everything the details page and the API need from the rollup.</summary>
/// <param name="Strip">24 h strip (96 cells).</param>
/// <param name="Chart">Response-time chart, null without any data in the last 24 h.</param>
/// <param name="Days">90 day cells, oldest first.</param>
/// <param name="HasDayData">At least one day has runs.</param>
/// <param name="Uptime">24 h / 7 d / 30 d figures.</param>
/// <param name="UpSince">Start of the current up period when known.</param>
/// <param name="UpForMoreThanHistory">No failure within the 90 days and the monitor is older than that.</param>
/// <param name="NewestFailingBucket">Start of the newest bucket with failures, null if none.</param>
public sealed record MonitorHistory(
    StatusStripModel Strip, ResponseChart? Chart, IReadOnlyList<DayCell> Days, bool HasDayData, UptimeFigures Uptime,
    DateTimeOffset? UpSince, bool UpForMoreThanHistory, DateTimeOffset? NewestFailingBucket);

/// <summary>Kind of the details lead.</summary>
public enum LeadKind { Down, Up, Paused, Disabled, Pending, Waiting }

/// <summary>
/// Big status word plus its caption ("since", "first check at"); <see cref="Time"/> follows the caption when set.
/// </summary>
public sealed record StatusLead(LeadKind Kind, string Word, string Caption, DateTimeOffset? Time);

/// <summary>
/// Builds <see cref="MonitorHistory"/> for one monitor.
/// </summary>
public sealed class MonitorHistoryService(ApplicationDbContext db)
{
    /// <summary>Days in the day row, and the rollup history.</summary>
    public const int HistoryDays = 90;

    private static readonly TimeSpan CellLength = TimeSpan.FromMinutes(15);
    private const int ChartWidth = 720;
    private const int ChartHeight = 180;
    private const int PadLeft = 8;
    private const int PadRight = 8;
    private const int PadTop = 10;
    private const int PadBottom = 8;

    /// <summary>
    /// Start of the first day cell (UTC midnight, 89 days before today).
    /// </summary>
    public static DateTimeOffset HistoryStart(DateTimeOffset now) =>
        new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-(HistoryDays - 1));

    /// <summary>
    /// Loads the buckets of the last 90 days and builds the history. <paramref name="refineUpSince"/> reads the retained runs
    /// around the newest failing bucket (two bounded queries) to sharpen "UP since"; pass false when it is not shown.
    /// </summary>
    public async Task<MonitorHistory> GetAsync(MonitorEntity monitor, DateTimeOffset now, bool refineUpSince, CancellationToken cancellationToken)
    {
        var from = HistoryStart(now);
        var buckets = await db.MonitorRunBuckets
            .AsNoTracking()
            .Where(b => b.MonitorId == monitor.Id && b.BucketStart >= from)
            .ToListAsync(cancellationToken);

        var history = Build(monitor, buckets, now);
        if (!refineUpSince || history.NewestFailingBucket is not { } bucketStart)
            return history;

        return history with { UpSince = await RefineUpSinceAsync(monitor.Id, bucketStart, history.UpSince, cancellationToken) };
    }

    /// <summary>
    /// Uptime figures only (API detail): loads 30 days of buckets.
    /// </summary>
    public async Task<UptimeFigures> GetUptimeAsync(Guid monitorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var from = RunBucketRollup.BucketStartOf(now - TimeSpan.FromDays(30));
        var buckets = await db.MonitorRunBuckets
            .AsNoTracking()
            .Where(b => b.MonitorId == monitorId && b.BucketStart >= from)
            .ToListAsync(cancellationToken);
        return Uptime(buckets, now);
    }

    /// <summary>
    /// The first successful completed run after the last failed run inside the newest failing bucket, when those runs are
    /// still retained; otherwise the bucket's end (<paramref name="fallback"/>).
    /// </summary>
    private async Task<DateTimeOffset?> RefineUpSinceAsync(
        Guid monitorId, DateTimeOffset bucketStart, DateTimeOffset? fallback, CancellationToken cancellationToken)
    {
        var bucketEnd = bucketStart + CellLength;
        var lastFailed = await db.MonitorRuns
            .AsNoTracking()
            .Where(r => r.MonitorId == monitorId && (r.State == "completed" || r.State == "") && !r.Success
                        && r.StartedAt >= bucketStart && r.StartedAt < bucketEnd)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastFailed is not { } failedAt)
            return fallback;

        var firstOk = await db.MonitorRuns
            .AsNoTracking()
            .Where(r => r.MonitorId == monitorId && (r.State == "completed" || r.State == "") && r.Success && r.StartedAt > failedAt)
            .OrderBy(r => r.StartedAt)
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return firstOk ?? fallback;
    }

    /// <summary>
    /// Pure construction from the monitor's buckets (UP since is the failing bucket's end, not yet refined).
    /// </summary>
    public static MonitorHistory Build(MonitorEntity monitor, IReadOnlyCollection<MonitorRunBucket> buckets, DateTimeOffset now)
    {
        var heartbeat = monitor.Type == MonitorType.Heartbeat;
        var strip = StatusStrip.Build(buckets, now, heartbeat);
        var days = Days(buckets, now);

        var newestFailing = buckets
            .Where(b => b.FailureCount > 0 && b.RunCount > 0)
            .Select(b => (DateTimeOffset?)b.BucketStart)
            .OrderByDescending(b => b)
            .FirstOrDefault();

        DateTimeOffset? upSince = null;
        var moreThanHistory = false;
        if (newestFailing is { } failing)
            upSince = failing + CellLength;
        else if (monitor.CreatedAt > now - TimeSpan.FromDays(HistoryDays))
            upSince = monitor.CreatedAt;
        else
            moreThanHistory = true;

        return new MonitorHistory(strip, Chart(strip), days, days.Any(d => d.Runs > 0), Uptime(buckets, now),
            upSince, moreThanHistory, newestFailing);
    }

    /// <summary>
    /// Color of a day: 100% up; 99% to under 100% dimmed up; 95% to under 99% down tint; under 95% down; null grey.
    /// </summary>
    public static DayState DayStateOf(double? uptimePercent) => uptimePercent switch
    {
        null => DayState.None,
        >= 100 => DayState.Up,
        >= 99 => DayState.UpDim,
        >= 95 => DayState.DownTint,
        _ => DayState.Down,
    };

    /// <summary>
    /// Percent of passed runs, floored to three decimals so a failure never reads as 100; null without runs.
    /// </summary>
    public static double? Percent(long runs, long failures)
    {
        if (runs <= 0)
            return null;
        if (failures <= 0)
            return 100;
        return Math.Floor((runs - failures) * 100000d / runs) / 1000d;
    }

    /// <summary>
    /// The 90 day cells ending with today (UTC), oldest first.
    /// </summary>
    public static List<DayCell> Days(IEnumerable<MonitorRunBucket> buckets, DateTimeOffset now)
    {
        var first = HistoryStart(now);
        var perDay = buckets
            .Where(b => b.BucketStart >= first)
            .GroupBy(b => b.BucketStart.UtcDateTime.Date)
            .ToDictionary(g => g.Key, g => (Runs: g.Sum(b => (long)b.RunCount), Failures: g.Sum(b => (long)b.FailureCount)));

        var cells = new List<DayCell>(HistoryDays);
        for (var i = 0; i < HistoryDays; i++)
        {
            var day = first.AddDays(i);
            perDay.TryGetValue(day.UtcDateTime.Date, out var d);
            var percent = Percent(d.Runs, d.Failures);
            cells.Add(new DayCell(day, (int)d.Runs, (int)d.Failures, percent, DayStateOf(percent)));
        }

        return cells;
    }

    /// <summary>
    /// Uptime over the last 24 h (the strip window), 7 d and 30 d from the buckets.
    /// </summary>
    public static UptimeFigures Uptime(IEnumerable<MonitorRunBucket> buckets, DateTimeOffset now)
    {
        var list = buckets.Where(b => b.RunCount > 0).ToList();
        double? Window(DateTimeOffset from)
        {
            var inWindow = list.Where(b => b.BucketStart >= from).ToList();
            return Percent(inWindow.Sum(b => (long)b.RunCount), inWindow.Sum(b => (long)b.FailureCount));
        }

        return new UptimeFigures(
            Window(StatusStrip.WindowStart(now)),
            Window(RunBucketRollup.BucketStartOf(now - TimeSpan.FromDays(7))),
            Window(RunBucketRollup.BucketStartOf(now - TimeSpan.FromDays(30))));
    }

    /// <summary>
    /// Percent text for display ("99.95%"), truncated (never rounded up to 100) and an em dash for null.
    /// </summary>
    public static string FormatPercent(double? percent) =>
        percent is { } p ? (Math.Floor(p * 100) / 100).ToString("0.##", CultureInfo.InvariantCulture) + "%" : "\u2014";

    /// <summary>
    /// Response-time chart of the strip's cell averages; null when no cell has data.
    /// </summary>
    public static ResponseChart? Chart(StatusStripModel strip)
    {
        var withData = strip.Cells.Where(c => c.State != StripCellState.Empty && c.AverageMs is not null).ToList();
        if (withData.Count == 0)
            return null;

        var plotWidth = ChartWidth - PadLeft - PadRight;
        var plotHeight = ChartHeight - PadTop - PadBottom;
        var cellWidth = (double)plotWidth / strip.Cells.Count;
        var yMax = NiceMax(Math.Max(1, withData.Max(c => c.AverageMs!.Value)));
        double X(int i) => Math.Round(PadLeft + (i + 0.5) * cellWidth, 1);
        double Y(int ms) => Math.Round(PadTop + (1 - ms / (double)yMax) * plotHeight, 1);

        var points = new List<ChartPoint>();
        var paths = new List<string>();
        var segment = new List<string>();
        void Flush()
        {
            if (segment.Count > 1)
                paths.Add(string.Join(' ', segment));
            segment.Clear();
        }

        for (var i = 0; i < strip.Cells.Count; i++)
        {
            var cell = strip.Cells[i];
            if (cell.State == StripCellState.Empty || cell.AverageMs is not { } ms)
            {
                Flush();
                continue;
            }

            var hasPrev = i > 0 && strip.Cells[i - 1].State != StripCellState.Empty;
            var hasNext = i < strip.Cells.Count - 1 && strip.Cells[i + 1].State != StripCellState.Empty;
            var x = X(i);
            var y = Y(ms);
            segment.Add(string.Create(CultureInfo.InvariantCulture, $"{(segment.Count == 0 ? 'M' : 'L')}{x} {y}"));
            points.Add(new ChartPoint(x, y, cell.State == StripCellState.Fail, !hasPrev && !hasNext, cell));
        }

        Flush();
        var grid = new[] { 0, yMax / 2, yMax }.Select(ms => new ChartGridLine(Y(ms), ms)).ToList();
        return new ResponseChart(ChartWidth, ChartHeight, Math.Round(cellWidth, 2), paths, points, grid, points.Count(p => p.Failed));
    }

    private static int NiceMax(int value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var n = value / magnitude;
        var nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return (int)(nice * magnitude);
    }

    /// <summary>
    /// The lead of the details page. State and failing-since come from <see cref="MonitorStatus"/>; up-since from the history.
    /// </summary>
    public static StatusLead Lead(MonitorEntity monitor, MonitorStatus status, MonitorHistory history)
    {
        var heartbeat = monitor.Type == MonitorType.Heartbeat;
        if (status.State == MonitorState.Disabled)
            return new StatusLead(LeadKind.Disabled, "DISABLED", heartbeat ? "no pings are expected" : "no checks are scheduled", null);
        if (status.State == MonitorState.Paused)
            return new StatusLead(LeadKind.Paused, "PAUSED", heartbeat ? "pings are not expected" : "checks are suspended", null);

        switch (status.Health)
        {
            case MonitorHealth.Failing or MonitorHealth.Overdue:
                var overdueSince = heartbeat ? monitor.NextRunAt : monitor.LastRunAt;
                var since = status.Failure?.FailingSince
                            ?? (status.Health == MonitorHealth.Overdue ? overdueSince : null)
                            ?? status.Failure?.LastRunAt;
                return new StatusLead(LeadKind.Down, "DOWN", since is null ? "" : "since", since);
            case MonitorHealth.Unknown:
                return heartbeat
                    ? new StatusLead(LeadKind.Waiting, "WAITING", monitor.NextRunAt is null ? "for the first ping" : "first ping due by", monitor.NextRunAt)
                    : new StatusLead(LeadKind.Pending, "PENDING", monitor.NextRunAt is null ? "waiting for the first check" : "first check at", monitor.NextRunAt);
            default:
                return history.UpSince is { } upSince
                    ? new StatusLead(LeadKind.Up, "UP", "since", upSince)
                    : new StatusLead(LeadKind.Up, "UP", $"for more than {HistoryDays} days", null);
        }
    }
}
