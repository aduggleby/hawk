// <file>
// <summary>
// Lists configured monitors and their last/next run timestamps.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Monitors list page model.
/// </summary>
public class IndexModel(ApplicationDbContext db) : PageModel
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
        Monitors = await db.Monitors
            .OrderBy(m => !m.Enabled ? 2 : m.IsPaused ? 1 : 0)
            .ThenBy(m => m.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        LastRunSuccessByMonitor = await db.MonitorRuns
            .AsNoTracking()
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
        var updated = await db.Monitors
            .Where(m => m.Enabled && !m.IsPaused)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsPaused, true), cancellationToken);

        TempData["FlashInfo"] = $"Paused {updated} monitor(s).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResumeAllAsync(CancellationToken cancellationToken)
    {
        var updated = await db.Monitors
            .Where(m => m.Enabled && m.IsPaused)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsPaused, false), cancellationToken);

        TempData["FlashInfo"] = $"Resumed {updated} monitor(s).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBatchAsync(CancellationToken cancellationToken)
    {
        if (SelectedMonitorIds.Count == 0)
        {
            TempData["FlashError"] = "No monitors selected.";
            return RedirectToPage();
        }

        var action = (BatchAction ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("pause" or "resume"))
        {
            TempData["FlashError"] = "Invalid batch action.";
            return RedirectToPage();
        }

        var query = db.Monitors.Where(m => SelectedMonitorIds.Contains(m.Id));
        var updated = action == "pause"
            ? await query
                .Where(m => m.Enabled && !m.IsPaused)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsPaused, true), cancellationToken)
            : await query
                .Where(m => m.Enabled && m.IsPaused)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsPaused, false), cancellationToken);

        TempData["FlashInfo"] = $"{(action == "pause" ? "Paused" : "Resumed")} {updated} monitor(s).";
        return RedirectToPage();
    }
}
