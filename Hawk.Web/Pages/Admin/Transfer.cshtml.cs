// <file>
// <summary>
// Admin-only bulk export and import of all monitors (with dry-run preview).
// </summary>
// </file>

using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Hawk.Web.Services.Import;

namespace Hawk.Web.Pages.Admin;

/// <summary>
/// Monitor transfer page model.
/// </summary>
public class TransferModel(MonitorTransferService transfer, UserManager<IdentityUser> userManager) : PageModel
{
    /// <summary>
    /// Uploaded JSON export file (preview step).
    /// </summary>
    [BindProperty]
    public IFormFile? Upload { get; set; }

    /// <summary>
    /// Raw JSON carried from preview to confirm. Always re-parsed and re-validated on confirm.
    /// </summary>
    [BindProperty]
    public string? Json { get; set; }

    /// <summary>
    /// Create all imported monitors paused (recommended for migrations).
    /// </summary>
    [BindProperty]
    public bool CreatePaused { get; set; } = true;

    /// <summary>
    /// Dry-run plan shown after uploading a file.
    /// </summary>
    public MonitorImportPlan? Plan { get; private set; }

    /// <summary>
    /// Displays the page.
    /// </summary>
    public void OnGet()
    {
    }

    /// <summary>
    /// Downloads every monitor as JSON.
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken cancellationToken)
    {
        var json = await transfer.ExportAllJsonAsync(cancellationToken);
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"hawk-monitors-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    /// <summary>
    /// Reads the uploaded file and shows a dry-run preview. Writes nothing.
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        if (Upload is null || Upload.Length == 0)
        {
            ModelState.AddModelError(nameof(Upload), "Please choose a JSON file to upload.");
            return Page();
        }

        if (Upload.Length > MonitorTransferService.MaxImportBytes)
        {
            ModelState.AddModelError(nameof(Upload), "Import file is larger than 2 MB.");
            return Page();
        }

        using (var reader = new StreamReader(Upload.OpenReadStream(), Encoding.UTF8))
        {
            Json = await reader.ReadToEndAsync(cancellationToken);
        }

        Plan = await transfer.PlanAsync(Json, userManager.GetUserId(User), CreatePaused, cancellationToken);
        return Page();
    }

    /// <summary>
    /// Re-parses and re-validates the carried JSON, then imports everything or nothing.
    /// </summary>
    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Json))
        {
            TempData["FlashError"] = "Nothing to import. Upload a file first.";
            return RedirectToPage();
        }

        var result = await transfer.ImportAsync(Json, userManager.GetUserId(User), CreatePaused, cancellationToken);
        if (result.Plan.ParseError is not null)
        {
            TempData["FlashError"] = result.Plan.ParseError;
            return RedirectToPage();
        }

        if (!result.Plan.CanImport)
        {
            TempData["FlashError"] = $"Import refused: {result.Plan.InvalidCount} invalid monitor(s). Nothing was imported.";
            return RedirectToPage();
        }

        TempData["FlashInfo"] =
            $"Imported {result.Imported} monitor(s), skipped {result.SkippedDuplicates} duplicate(s). Created paused: {(CreatePaused ? "yes" : "no")}.";
        return RedirectToPage();
    }
}
