// <file>
// <summary>
// JSON error contract for /api/*: problem+json writer and the middleware that keeps HTML error pages and empty error bodies out of API responses.
// </summary>
// </file>

namespace Hawk.Web.Infrastructure;

public static class ApiErrors
{
    public const string PathPrefix = "/api";

    /// <summary>True for <c>/api</c> and everything below it.</summary>
    public static bool IsApiPath(PathString path) =>
        path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes an RFC 9457 problem document with a stable <c>code</c> extension. Does nothing if the response has started.
    /// </summary>
    public static async Task WriteProblemAsync(HttpContext context, int status, string code, string title, string? detail = null)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
        {
            ["type"] = "about:blank",
            ["title"] = title,
            ["status"] = status,
            ["detail"] = detail,
            ["code"] = code,
        }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }

    /// <summary>
    /// For <c>/api/*</c>: unhandled exceptions become a 500 <c>internal_error</c> problem, and empty-body 400/404/405/415 responses get a problem document.
    /// Place after the exception handler and before routing.
    /// </summary>
    public static IApplicationBuilder UseHawkApiErrors(this IApplicationBuilder app) =>
        app.UseWhen(context => IsApiPath(context.Request.Path), branch => branch.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hawk.Api")
                    .LogError(ex, "Unhandled exception in API request {Method} {Path}", context.Request.Method, context.Request.Path.Value);
                await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "Internal server error");
                return;
            }

            var response = context.Response;
            if (response.HasStarted || response.ContentType is not null || (response.ContentLength ?? 0) != 0) return;

            switch (response.StatusCode)
            {
                case StatusCodes.Status400BadRequest or StatusCodes.Status415UnsupportedMediaType:
                    await WriteProblemAsync(context, response.StatusCode, "validation_failed", "The request is not valid");
                    break;
                case StatusCodes.Status404NotFound:
                    await WriteProblemAsync(context, response.StatusCode, "not_found", "Not found");
                    break;
                case StatusCodes.Status405MethodNotAllowed:
                    await WriteProblemAsync(context, response.StatusCode, "method_not_allowed", "Method not allowed");
                    break;
            }
        }));
}
