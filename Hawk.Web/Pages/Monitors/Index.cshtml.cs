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
public class IndexModel(
    ApplicationDbContext db,
    IBackgroundJobClient jobs,
    MonitorStatusService status,
    MonitorCommands commands) : PageModel
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
    /// Latest completed run by monitor id.
    /// </summary>
    public Dictionary<Guid, LastCompletedRun> LastRunByMonitor { get; private set; } = [];

    /// <summary>
    /// All monitors by group (case-insensitive), sorted by name with Ungrouped last. Failing monitors are included.
    /// </summary>
    public List<GroupSection> GroupSections { get; private set; } = [];

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

        LastRunByMonitor = await status.GetLastCompletedRunsAsync(null, cancellationToken);

        FailingMonitors = Monitors
            .Where(m => MonitorStatusService.IsInFailingGroup(m, LastRunByMonitor))
            .ToList();

        var failingIds = FailingMonitors.Select(m => m.Id).ToHashSet();
        OtherMonitors = Monitors.Where(m => !failingIds.Contains(m.Id)).ToList();

        // Group case-insensitively; the section is named after the ordinal-first spelling (the canonical one, see MonitorGroups).
        GroupSections = Monitors
            .GroupBy(m => MonitorGroups.Normalize(m.GroupName), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var name = g.Key is null ? null : g.Select(m => m.GroupName!).Order(StringComparer.Ordinal).First();
                return new GroupSection(name, MonitorGroups.Anchor(name), g.ToList(), g.Count(m => failingIds.Contains(m.Id)));
            })
            .OrderBy(s => s.Name is null ? 1 : 0)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One group section of the index; <see cref="Name"/> is null for Ungrouped.</summary>
    public sealed record GroupSection(string? Name, string Anchor, List<MonitorEntity> Monitors, int FailingCount);

    public async Task<IActionResult> OnPostGroupPauseAsync(string? group, CancellationToken cancellationToken)
    {
        if (MonitorGroups.Normalize(group) is not { } name)
            return GroupMissing();
        var updated = await commands.PauseAsync(MonitorGroups.InGroup(db.Monitors, name), cancellationToken);
        TempData["FlashInfo"] = $"Paused {updated} monitor(s) in {name}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGroupResumeAsync(string? group, CancellationToken cancellationToken)
    {
        if (MonitorGroups.Normalize(group) is not { } name)
            return GroupMissing();
        var updated = await commands.ResumeAsync(MonitorGroups.InGroup(db.Monitors, name), cancellationToken);
        TempData["FlashInfo"] = $"Resumed {updated} monitor(s) in {name}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGroupRunAsync(string? group, CancellationToken cancellationToken)
    {
        if (MonitorGroups.Normalize(group) is not { } name)
            return GroupMissing();
        var result = await commands.QueueGroupRunAsync(name, cancellationToken);
        TempData["FlashInfo"] = $"Queued {result.Queued} monitor run(s)." +
            (result.AlreadyInFlight > 0 ? $" {result.AlreadyInFlight} already running." : "") +
            (result.Skipped > 0 ? $" Skipped {result.Skipped} (heartbeat or disabled)." : "");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGroupRenameAsync(string? group, string? newGroupName, CancellationToken cancellationToken)
    {
        if (MonitorGroups.Normalize(group) is not { } name)
            return GroupMissing();
        var result = await commands.RenameGroupAsync(name, newGroupName ?? string.Empty, cancellationToken);
        if (result.Error is not null)
            TempData["FlashError"] = result.Error;
        else
            TempData["FlashInfo"] = $"Renamed group {name} to {result.NewName} ({result.Renamed} monitor(s)).";
        return RedirectToPage();
    }

    private IActionResult GroupMissing()
    {
        TempData["FlashError"] = "Group is required.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPauseAllAsync(CancellationToken cancellationToken)
    {
        var updated = await commands.PauseAsync(db.Monitors, cancellationToken);

        TempData["FlashInfo"] = $"Paused {updated} monitor(s).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResumeAllAsync(CancellationToken cancellationToken)
    {
        var updated = await commands.ResumeAsync(db.Monitors, cancellationToken);

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
            updated = await commands.PauseAsync(query, cancellationToken);
        }
        else
        {
            updated = await commands.ResumeAsync(query, cancellationToken);
        }

        TempData["FlashInfo"] = $"{(action == "pause" ? "Paused" : "Resumed")} {updated} monitor(s).";
        return RedirectToPage();
    }
}
