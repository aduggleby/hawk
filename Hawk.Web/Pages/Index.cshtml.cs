// <file>
// <summary>
// Home page model. This will evolve into the "dashboard" (current checks + last run status) for Hawk.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages;

/// <summary>
/// Home page model (dashboard placeholder).
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// Redirects authenticated users to monitors; shows public home for anonymous users.
    /// </summary>
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Monitors/Index");
        return Page();
    }
}
