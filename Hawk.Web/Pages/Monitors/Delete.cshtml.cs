// <file>
// <summary>
// Deletes a monitor.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Delete monitor page model.
/// </summary>
public class DeleteModel(ApplicationDbContext db) : PageModel
{
    /// <summary>
    /// Loaded monitor.
    /// </summary>
    public MonitorEntity? Monitor { get; private set; }

    /// <summary>
    /// Loads the monitor.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Monitor = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (Monitor is null)
            return NotFound();
        return Page();
    }

    /// <summary>
    /// Deletes the monitor and all dependent rows (headers, match rules, runs).
    /// </summary>
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var m = await db.Monitors.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (m is null)
            return NotFound();

        db.Monitors.Remove(m);
        await db.SaveChangesAsync(cancellationToken);

        return RedirectToPage("/Monitors/Index");
    }
}
