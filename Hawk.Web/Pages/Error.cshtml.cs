// <file>
// <summary>
// Default Razor Pages error model. Displays request correlation data useful for debugging.
// </summary>
// </file>

using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
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
    /// Request path that failed.
    /// </summary>
    public string? Path { get; private set; }

    /// <summary>
    /// Exception type name.
    /// </summary>
    public string? ExceptionType { get; private set; }

    /// <summary>
    /// Top-level exception message.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Full exception details (including inner exceptions and stack trace).
    /// </summary>
    public string? ExceptionDetails { get; private set; }

    /// <summary>
    /// True if diagnostics are available for rendering.
    /// </summary>
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(ExceptionDetails);

    /// <summary>
    /// Loads request metadata for rendering.
    /// </summary>
    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (feature?.Error is null)
            return;

        Path = feature.Path;
        ExceptionType = feature.Error.GetType().FullName;
        ErrorMessage = feature.Error.Message;
        ExceptionDetails = feature.Error.ToString();
    }
}
