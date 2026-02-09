// <file>
// <summary>
// Admin-only import endpoint for StatusCake exports.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using Hawk.Web.Services.Import;

namespace Hawk.Web.Pages.Admin.Import;

/// <summary>
/// StatusCake import page model.
/// </summary>
public class StatusCakeModel(StatusCakeImporter importer, UserManager<IdentityUser> userManager) : PageModel
{
    /// <summary>
    /// Import type selector.
    /// </summary>
    [BindProperty]
    public string ImportType { get; set; } = "tests";

    /// <summary>
    /// Uploaded JSON export file.
    /// </summary>
    [BindProperty]
    public IFormFile? Upload { get; set; }

    /// <summary>
    /// Latest import result.
    /// </summary>
    public StatusCakeImportResult? Result { get; private set; }

    /// <summary>
    /// Displays the import form.
    /// </summary>
    public void OnGet()
    {
    }

    /// <summary>
    /// Runs an import based on the uploaded file.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Upload is null || Upload.Length == 0)
        {
            ModelState.AddModelError(nameof(Upload), "Please choose a JSON file to upload.");
            return Page();
        }

        if (!string.Equals(ImportType, "tests", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ImportType, "alerts", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(ImportType), "Unknown import type.");
            return Page();
        }

        await using var s = Upload.OpenReadStream();
        if (string.Equals(ImportType, "tests", StringComparison.OrdinalIgnoreCase))
        {
            var userId = userManager.GetUserId(User);
            Result = await importer.ImportTestsAsync(s, userId, cancellationToken);
        }
        else
        {
            Result = await importer.ImportAlertsAsync(s, cancellationToken);
        }

        return Page();
    }
}

