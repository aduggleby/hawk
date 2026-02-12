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
    /// Loads request metadata for rendering on GET.
    /// </summary>
    public void OnGet() => Load();

    /// <summary>
    /// Loads request metadata for rendering on POST.
    /// UseExceptionHandler re-executes the pipeline with the original method.
    /// </summary>
    public void OnPost() => Load();

    private void Load()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier ?? "(unknown)";

        var pathFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        var exceptionFeature = HttpContext.Features.Get<IExceptionHandlerFeature>();
        var ex = pathFeature?.Error
                 ?? exceptionFeature?.Error
                 ?? HttpContext.Items["Hawk.Exception"] as Exception;

        Path = pathFeature?.Path ?? HttpContext.Request.Path.Value;
        if (ex is null)
        {
            ExceptionType = "Unknown";
            ErrorMessage = "No exception details were captured for this request.";
            ExceptionDetails = $"""
                No exception object was available in the current request context.
                Path: {Path}
                Request ID: {RequestId}
                """;
            return;
        }

        ExceptionType = ex.GetType().FullName;
        ErrorMessage = ex.Message;
        ExceptionDetails = ex.ToString();
    }
}
