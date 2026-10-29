// <file>
// <summary>
// The monitors overview (Tower board); data comes from MonitorBoardService.
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
    MonitorBoardService board,
    MonitorCommands commands) : PageModel
{
    /// <summary>
    /// The board: headline, Attention list and group sections with rows.
    /// </summary>
    public BoardModel Board { get; private set; } = BoardModel.Empty;

    [BindProperty]
    public List<Guid> SelectedMonitorIds { get; set; } = [];

    [BindProperty]
    public string? BatchAction { get; set; }

    /// <summary>
    /// Loads the board.
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Board = await board.BuildAsync(DateTimeOffset.UtcNow, cancellationToken);

    public async Task<IActionResult> OnPostGroupPauseAsync(string? group, CancellationToken cancellationToken)
    {
        if (!TryGroup(group, out var name))
            return RedirectToPage();
        var updated = await commands.PauseAsync(MonitorGroups.InGroup(db.Monitors, name), cancellationToken);
        TempData["FlashInfo"] = $"Paused {updated} monitor(s) in {name}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGroupResumeAsync(string? group, CancellationToken cancellationToken)
    {
        if (!TryGroup(group, out var name))
            return RedirectToPage();
        var updated = await commands.ResumeAsync(MonitorGroups.InGroup(db.Monitors, name), cancellationToken);
        TempData["FlashInfo"] = $"Resumed {updated} monitor(s) in {name}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGroupRunAsync(string? group, CancellationToken cancellationToken)
    {
        if (!TryGroup(group, out var name))
            return RedirectToPage();
        var result = await commands.QueueGroupRunAsync(name, cancellationToken);
        TempData["FlashInfo"] = $"Queued {result.Queued} monitor run(s)." +
            (result.AlreadyInFlight > 0 ? $" {result.AlreadyInFlight} already running." : "") +
            (result.Skipped > 0 ? $" Skipped {result.Skipped} (heartbeat or disabled)." : "");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGroupRenameAsync(string? group, string? newGroupName, CancellationToken cancellationToken)
    {
        if (!TryGroup(group, out var name))
            return RedirectToPage();
        var result = await commands.RenameGroupAsync(name, newGroupName ?? string.Empty, cancellationToken);
        if (result.Error is not null)
            TempData["FlashError"] = result.Error;
        else
            TempData["FlashInfo"] = $"Renamed group {name} to {result.NewName} ({result.Renamed} monitor(s)).";
        return RedirectToPage();
    }

    /// <summary>Normalizes and validates the posted group; sets a flash error and returns false when it is blank or invalid (no query runs).</summary>
    private bool TryGroup(string? group, out string name)
    {
        name = MonitorGroups.Normalize(group) ?? string.Empty;
        var error = name.Length == 0 ? "Group is required." : MonitorGroups.Validate(name);
        if (error is not null)
            TempData["FlashError"] = error;
        return error is null;
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
