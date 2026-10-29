// <file>
// <summary>
// Monitor detail page: shows configuration and recent run history, and provides a "Run now" action.
// </summary>
// </file>

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Monitor details page model.
/// </summary>
public class DetailsModel(ApplicationDbContext db, MonitorCommands commands) : PageModel
{
    /// <summary>
    /// Loaded monitor.
    /// </summary>
    public MonitorEntity? Monitor { get; private set; }

    /// <summary>
    /// Recent runs.
    /// </summary>
    public List<MonitorRun> Runs { get; private set; } = [];

    /// <summary>
    /// Full ping URL of a heartbeat (scheme and host as seen by the client, via forwarded headers), or null.
    /// </summary>
    public string? PingUrl => Monitor?.HeartbeatToken is { } token
        ? $"{Request.Scheme}://{Request.Host}/ping/{token}"
        : null;

    /// <summary>
    /// Loads monitor and recent run history.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Monitor = await db.Monitors
            .Include(m => m.Headers)
            .Include(m => m.MatchRules)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (Monitor is null)
            return NotFound();

        Runs = await db.MonitorRuns
            .Where(r => r.MonitorId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(25)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return Page();
    }

    /// <summary>
    /// Enqueues an immediate run.
    /// </summary>
    public async Task<IActionResult> OnPostRunNowAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await commands.QueueManualRunAsync(id, reuseInFlight: false, rejectDisabled: false, cancellationToken);
        switch (result.Outcome)
        {
            case ManualRunOutcome.NotFound:
                return NotFound();
            case ManualRunOutcome.Heartbeat:
                TempData["FlashError"] = "Heartbeat monitors cannot be run manually. They wait for a ping.";
                return RedirectToPage(new { id });
            default:
                return RedirectToPage("/Monitors/Runs/Details", new { monitorId = id, runId = result.RunId });
        }
    }

    /// <summary>
    /// Exports this monitor configuration as JSON.
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync(Guid id, CancellationToken cancellationToken)
    {
        var monitor = await db.Monitors
            .Include(m => m.Headers)
            .Include(m => m.MatchRules)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (monitor is null)
            return NotFound();

        var payload = MonitorJsonPort.ToEnvelope(monitor);
        var json = JsonSerializer.Serialize(payload, MonitorJsonPort.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var safeName = string.Concat(monitor.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = monitor.Id.ToString("N");

        return File(bytes, "application/json", $"{safeName}.monitor.json");
    }
}
