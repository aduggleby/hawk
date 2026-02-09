// <file>
// <summary>
// Monitor detail page: shows configuration and recent run history, and provides a "Run now" action.
// </summary>
// </file>

using Hangfire;
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
public class DetailsModel(ApplicationDbContext db, IBackgroundJobClient jobs) : PageModel
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
        var exists = await db.Monitors.AnyAsync(m => m.Id == id, cancellationToken);
        if (!exists)
            return NotFound();

        jobs.Enqueue<IMonitorRunner>(r => r.RunAsync(id, "manual", CancellationToken.None));
        return RedirectToPage("/Monitors/Details", new { id });
    }
}
