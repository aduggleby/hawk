// <file>
// <summary>
// Lists configured monitors and their last/next run timestamps.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Monitors list page model.
/// </summary>
public class IndexModel(ApplicationDbContext db, IBackgroundJobClient jobs) : PageModel
{
    /// <summary>
    /// Monitors to display.
    /// </summary>
    public List<MonitorEntity> Monitors { get; private set; } = [];

    /// <summary>
    /// Monitors whose most recent run failed (shown at top).
    /// </summary>
    public List<MonitorEntity> FailingMonitors { get; private set; } = [];

    /// <summary>
    /// Remaining monitors.
    /// </summary>
    public List<MonitorEntity> OtherMonitors { get; private set; } = [];

    /// <summary>
    /// Latest run success state by monitor id.
    /// </summary>
    public Dictionary<Guid, bool> LastRunSuccessByMonitor { get; private set; } = [];

    [BindProperty]
    public List<Guid> SelectedMonitorIds { get; set; } = [];

    [BindProperty]
    public string? BatchAction { get; set; }

    /// <summary>
    /// Loads the monitors list.
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Sort in memory: SQL Server's default collation is case-insensitive, PostgreSQL's is not.
        Monitors = (await db.Monitors
            .AsNoTracking()
            .ToListAsync(cancellationToken))
            .OrderBy(m => !m.Enabled ? 2 : m.IsPaused ? 1 : 0)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LastRunSuccessByMonitor = await db.MonitorRuns
            .AsNoTracking()
            .Where(r => r.State == "completed" || r.State == "")
            .GroupBy(r => r.MonitorId)
            .Select(g => g
                .OrderByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.Id)
                .Select(r => new { r.MonitorId, r.Success })
                .First())
            .ToDictionaryAsync(x => x.MonitorId, x => x.Success, cancellationToken);

        FailingMonitors = Monitors
            .Where(m => m.LastRunAt is not null &&
                        LastRunSuccessByMonitor.TryGetValue(m.Id, out var lastOk) &&
                        !lastOk)
            .ToList();

        var failingIds = FailingMonitors.Select(m => m.Id).ToHashSet();
        OtherMonitors = Monitors.Where(m => !failingIds.Contains(m.Id)).ToList();
    }

    public async Task<IActionResult> OnPostPauseAllAsync(CancellationToken cancellationToken)
    {
        var stamp = Guid.NewGuid();
        var updated = await db.Monitors
            .Where(m => m.Enabled && !m.IsPaused)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsPaused, true)
                .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);

        TempData["FlashInfo"] = $"Paused {updated} monitor(s).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResumeAllAsync(CancellationToken cancellationToken)
    {
        var updated = await ResumeAsync(db.Monitors, cancellationToken);

        TempData["FlashInfo"] = $"Resumed {updated} monitor(s).";
        return RedirectToPage();
    }

    /// <summary>
    /// Resumes paused, enabled monitors. Heartbeats get a fresh deadline (a full period plus grace) in the same
    /// statement that unpauses them, so the scheduler can never see a resumed heartbeat with a stale past deadline.
    /// Heartbeats are updated first, grouped by (period, grace) with a precomputed UTC deadline; the generic update afterwards
    /// only touches HTTP monitors.
    /// </summary>
    private async Task<int> ResumeAsync(IQueryable<MonitorEntity> monitors, CancellationToken cancellationToken)
    {
        var stamp = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var paused = monitors.Where(m => m.Enabled && m.IsPaused);
        var updated = 0;

        var groups = await paused
            .Where(m => m.Type == MonitorType.Heartbeat)
            .Select(m => new { m.IntervalSeconds, m.HeartbeatGraceSeconds })
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var group in groups)
        {
            var period = group.IntervalSeconds;
            var grace = group.HeartbeatGraceSeconds;
            var deadline = HeartbeatTokens.InitialDeadline(now, period, grace);
            updated += await paused
                .Where(m => m.Type == MonitorType.Heartbeat && m.IntervalSeconds == period && m.HeartbeatGraceSeconds == grace)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.IsPaused, false)
                    .SetProperty(m => m.NextRunAt, deadline)
                    .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);
        }

        // HTTP monitors only: a heartbeat paused after the group query stays paused rather than resuming without a deadline.
        updated += await paused
            .Where(m => m.Type == MonitorType.Http)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsPaused, false)
                .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);
        return updated;
    }

    public async Task<IActionResult> OnPostBatchAsync(CancellationToken cancellationToken)
    {
        if (SelectedMonitorIds.Count == 0)
        {
            TempData["FlashError"] = "No monitors selected.";
            return RedirectToPage();
        }

        var action = (BatchAction ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("pause" or "resume" or "run"))
        {
            TempData["FlashError"] = "Invalid batch action.";
            return RedirectToPage();
        }

        var selectedIds = SelectedMonitorIds.Distinct().ToArray();
        if (action == "run")
        {
            // Heartbeats cannot be run: they wait for a ping.
            var httpIds = await db.Monitors
                .Where(m => selectedIds.Contains(m.Id) && m.Type == MonitorType.Http)
                .Select(m => m.Id)
                .ToListAsync(cancellationToken);
            foreach (var id in httpIds)
            {
                var monitorId = id;
                jobs.Enqueue<IMonitorRunner>(r => r.RunAsync(monitorId, "manual", CancellationToken.None));
            }

            var skipped = selectedIds.Length - httpIds.Count;
            TempData["FlashInfo"] = $"Queued {httpIds.Count} monitor run(s)." +
                (skipped > 0 ? $" Skipped {skipped} (heartbeat monitors cannot be run manually)." : "");
            return RedirectToPage();
        }

        // Pause/resume are user changes: renew the concurrency stamp so open Edit forms notice.
        var query = db.Monitors.Where(m => selectedIds.Contains(m.Id));
        int updated;
        if (action == "pause")
        {
            var stamp = Guid.NewGuid();
            updated = await query
                .Where(m => m.Enabled && !m.IsPaused)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.IsPaused, true)
                    .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);
        }
        else
        {
            updated = await ResumeAsync(query, cancellationToken);
        }

        TempData["FlashInfo"] = $"{(action == "pause" ? "Paused" : "Resumed")} {updated} monitor(s).";
        return RedirectToPage();
    }
}
