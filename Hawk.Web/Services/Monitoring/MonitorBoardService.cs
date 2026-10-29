// <file>
// <summary>
// Builds the monitors overview (Tower board): groups with rows, the failing (Attention) list, per-monitor 24 h strips
// and the headline sentence. Reuses MonitorStatusService for the failing rule and failure details and MonitorGroups for
// group naming, so the page and any other reader agree.
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

/// <summary>One monitor row of the board.</summary>
/// <param name="Monitor">The monitor.</param>
/// <param name="State">Configuration state (active, paused, disabled).</param>
/// <param name="Failing">True when it is in the failing list (<see cref="MonitorStatusService.IsInFailingGroup"/>).</param>
/// <param name="Pending">True when it has no completed run yet.</param>
/// <param name="Overdue">True when checks or pings stopped arriving (<see cref="MonitorHealth.Overdue"/>).</param>
/// <param name="Strip">The 24 h strip.</param>
/// <param name="LastMs">Average response time of the newest interval with runs (null for none and for heartbeats).</param>
public sealed record BoardRow(
    MonitorEntity Monitor, MonitorState State, bool Failing, bool Pending, bool Overdue, StatusStripModel Strip, int? LastMs)
{
    /// <summary>Status word shown next to the colored mark, so state never relies on color alone.</summary>
    public string StatusLabel => Failing ? "Down" : State switch
    {
        MonitorState.Disabled => "Disabled",
        MonitorState.Paused => "Paused",
        _ => Overdue ? "Overdue" : Pending ? "Pending" : "Up",
    };
}

/// <summary>One Attention card (a failing monitor).</summary>
/// <param name="Monitor">The monitor.</param>
/// <param name="State">Configuration state.</param>
/// <param name="Reason">Error text, or the HTTP status.</param>
/// <param name="Since">Start of the incident, else the last failed run.</param>
/// <param name="StatusCode">HTTP status of the last run, if any.</param>
public sealed record BoardAttention(
    MonitorEntity Monitor, MonitorState State, string Reason, DateTimeOffset Since, int? StatusCode);

/// <summary>One group section; <see cref="Name"/> is null for Ungrouped.</summary>
public sealed record BoardGroup(string? Name, string Anchor, List<BoardRow> Rows, int DownCount);

/// <summary>The whole board.</summary>
public sealed record BoardModel(string Headline, List<BoardAttention> Attention, List<BoardGroup> Groups, int Total)
{
    /// <summary>Empty board.</summary>
    public static BoardModel Empty { get; } = new("No monitors yet.", [], [], 0);
}

/// <summary>Headline wording (pure).</summary>
public static class BoardHeadline
{
    /// <summary>
    /// Sentence for the overview. <paramref name="down"/> counts failing monitors, <paramref name="inactive"/> paused or
    /// disabled ones (a failing monitor may be among both); <paramref name="longest"/> is the longest running outage.
    /// <paramref name="overdue"/> and <paramref name="pending"/> count active, not failing monitors that are overdue or
    /// have no completed run yet; they are not up.
    /// </summary>
    public static string For(
        int total, int down, int inactive, string? longestName, TimeSpan? longest, int overdue = 0, int pending = 0)
    {
        if (total == 0)
            return "No monitors yet.";
        if (down > 0)
        {
            var text = $"{down} of {total} monitors {(down == 1 ? "is" : "are")} down.";
            if (longestName is not null && longest is { } d)
                text += $" Longest outage: {longestName}, {Duration(d)}.";
            return text;
        }
        if (inactive >= total)
            return total == 1 ? "The monitor is paused or disabled." : $"All {total} monitors are paused or disabled.";
        var active = total - inactive;
        if (overdue + pending > 0)
        {
            var ok = active - overdue - pending;
            var scope = inactive == 0
                ? $"{total} {(total == 1 ? "monitor" : "monitors")}"
                : $"{active} active {(active == 1 ? "monitor" : "monitors")}";
            var parts = new List<string>();
            if (overdue > 0)
                parts.Add($"{overdue} overdue");
            if (pending > 0)
                parts.Add($"{pending} waiting for a first check");
            var text = $"{ok} of {scope} {(ok == 1 ? "is" : "are")} up. {string.Join(", ", parts)}.";
            return inactive == 0 ? text : $"{text} {inactive} paused or disabled.";
        }

        var up = active == 1 ? "1 monitor is up." : $"All {active} active monitors are up.";
        if (inactive == 0)
            return total == 1 ? "The monitor is up." : $"All {total} monitors are up.";
        return $"{up} {inactive} paused or disabled.";
    }

    /// <summary>Compact duration such as "3 h 12 min", "2 d 4 h" or "under 1 min".</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.FromMinutes(1))
            return "under 1 min";
        if (d.TotalDays >= 1)
            return $"{(int)d.TotalDays} d {d.Hours} h";
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : $"{(int)d.TotalMinutes} min";
    }
}

/// <summary>Builds the <see cref="BoardModel"/>.</summary>
public sealed class MonitorBoardService(ApplicationDbContext db, MonitorStatusService status)
{
    /// <summary>
    /// Loads all monitors, last completed runs, the 24 h buckets (one query) and builds the board as of <paramref name="now"/>.
    /// </summary>
    public async Task<BoardModel> BuildAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Sort in memory: SQL Server's default collation is case-insensitive, PostgreSQL's is not.
        var monitors = (await db.Monitors.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(m => !m.Enabled ? 2 : m.IsPaused ? 1 : 0)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (monitors.Count == 0)
            return BoardModel.Empty;

        var lastRuns = await status.GetLastCompletedRunsAsync(null, cancellationToken);
        var failing = monitors.Where(m => MonitorStatusService.IsInFailingGroup(m, lastRuns)).ToList();
        var failingIds = failing.Select(m => m.Id).ToHashSet();
        var statuses = failing.Count == 0
            ? []
            : await status.GetStatusesAsync(failing, cancellationToken);

        var windowStart = StatusStrip.WindowStart(now);
        var buckets = (await db.MonitorRunBuckets
                .AsNoTracking()
                .Where(b => b.BucketStart >= windowStart)
                .ToListAsync(cancellationToken))
            .ToLookup(b => b.MonitorId);

        var rows = monitors.ToDictionary(m => m.Id, m =>
        {
            var heartbeat = m.Type == MonitorType.Heartbeat;
            var strip = StatusStrip.Build(buckets[m.Id], now, heartbeat);
            int? lastMs = heartbeat ? null : strip.Cells.LastOrDefault(c => c.State != StripCellState.Empty)?.AverageMs;
            lastRuns.TryGetValue(m.Id, out var last);
            var overdue = status.Health(m, last, now) == MonitorHealth.Overdue;
            return new BoardRow(m, MonitorStatusService.ComputeState(m), failingIds.Contains(m.Id), last is null, overdue, strip, lastMs);
        });

        // Group case-insensitively; the section is named after the canonical (ordinal-first) spelling.
        var groups = monitors
            .GroupBy(m => MonitorGroups.Normalize(m.GroupName), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var name = g.Key is null ? null : MonitorGroups.DisplayName(g.Select(m => m.GroupName!));
                var list = g.Select(m => rows[m.Id]).ToList();
                return new BoardGroup(name, MonitorGroups.Anchor(name), list, list.Count(r => r.Failing));
            })
            .OrderBy(g => g.Name is null ? 1 : 0)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attention = failing
            .Select(m =>
            {
                var f = statuses.TryGetValue(m.Id, out var s) ? s.Failure : null;
                var since = f?.FailingSince ?? f?.LastRunAt ?? lastRuns[m.Id].StartedAt;
                var code = f?.StatusCode;
                var reason = !string.IsNullOrWhiteSpace(f?.Error) ? f!.Error! : "Check failed";
                return new BoardAttention(m, MonitorStatusService.ComputeState(m), reason, since, code);
            })
            .OrderBy(a => a.Since)
            .ToList();

        var inactive = monitors.Count(m => MonitorStatusService.ComputeState(m) != MonitorState.Active);
        var oldest = attention.FirstOrDefault();
        var live = rows.Values.Where(r => r.State == MonitorState.Active && !r.Failing).ToList();
        var overdueCount = live.Count(r => r.Overdue);
        var pendingCount = live.Count(r => !r.Overdue && r.Pending);
        var headline = BoardHeadline.For(monitors.Count, attention.Count, inactive,
            oldest?.Monitor.Name, oldest is null ? null : now - oldest.Since, overdueCount, pendingCount);

        return new BoardModel(headline, attention, groups, monitors.Count);
    }
}
