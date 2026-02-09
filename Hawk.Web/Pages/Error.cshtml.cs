// <file>
// <summary>
// Default Razor Pages error model. Displays request correlation data useful for debugging.
// </summary>
// </file>

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages;

/// <summary>
/// Error page model.
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    /// <summary>
    /// Current request id (activity id or trace identifier).
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// True if <see cref="RequestId"/> is present and should be rendered.
    /// </summary>
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>
    /// Loads request metadata for rendering.
    /// </summary>
    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}
