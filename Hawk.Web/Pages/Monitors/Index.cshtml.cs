// <file>
// <summary>
// Lists configured monitors and their last/next run timestamps.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc.RazorPages;
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
    /// Loads the monitors list.
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Monitors = await db.Monitors
            .OrderByDescending(m => m.Enabled)
            .ThenBy(m => m.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
