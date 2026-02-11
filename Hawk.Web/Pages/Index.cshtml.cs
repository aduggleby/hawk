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
    /// Redirects authenticated users to monitors and anonymous users to login.
    /// </summary>
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Monitors/Index");
        return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl = Url.Page("/Monitors/Index") });
    }
}
