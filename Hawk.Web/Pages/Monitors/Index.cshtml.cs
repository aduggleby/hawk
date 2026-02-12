// <file>
// <summary>
// Lists configured monitors and their last/next run timestamps.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Monitors list page model.
/// </summary>
public class IndexModel(ApplicationDbContext db, IHostEnvironment env, UserManager<IdentityUser> userManager) : PageModel
{
    /// <summary>
    /// Monitors to display.
    /// </summary>
    public List<MonitorEntity> Monitors { get; private set; } = [];

    /// <summary>
    /// Latest run success state by monitor id.
    /// </summary>
    public Dictionary<Guid, bool> LastRunSuccessByMonitor { get; private set; } = [];

    [BindProperty]
    public List<Guid> SelectedMonitorIds { get; set; } = [];

    [BindProperty]
    public string? BatchAction { get; set; }

    [BindProperty]
    public IFormFile? ImportFile { get; set; }

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

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        if (ImportFile is null || ImportFile.Length <= 0)
        {
            TempData["FlashError"] = "Select a JSON file to import.";
            return RedirectToPage();
        }

        string json;
        await using (var stream = ImportFile.OpenReadStream())
        using (var reader = new StreamReader(stream))
        {
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        if (!MonitorJsonPort.TryParse(json, out var envelope, out var parseError) || envelope is null)
        {
            TempData["FlashError"] = parseError ?? "Invalid monitor JSON.";
            return RedirectToPage();
        }

        if (envelope.Monitors.Count == 0)
        {
            TempData["FlashError"] = "Import JSON does not contain any monitors.";
            return RedirectToPage();
        }

        var imported = new List<MonitorEntity>(envelope.Monitors.Count);
        var createdByUserId = userManager.GetUserId(User);

        foreach (var model in envelope.Monitors)
        {
            if (!MonitorJsonPort.TryCreateMonitor(model, createdByUserId, env, out var monitor, out var error))
            {
                var name = string.IsNullOrWhiteSpace(model.Name) ? "(unnamed)" : model.Name;
                TempData["FlashError"] = $"Import failed for '{name}': {error}";
                return RedirectToPage();
            }

            imported.Add(monitor);
        }

        db.Monitors.AddRange(imported);
        await db.SaveChangesAsync(cancellationToken);

        TempData["FlashInfo"] = $"Imported {imported.Count} monitor(s).";
        return RedirectToPage();
    }
}
