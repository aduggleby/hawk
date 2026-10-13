// <file>
// <summary>
// Compatibility route for older monitor test links. GET and HEAD remain passive.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages.Monitors;

public sealed class TestModel : PageModel
{
    public IActionResult OnGet(Guid id) => RedirectToPage("/Monitors/Details", new { id });
}

