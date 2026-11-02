// <file>
// <summary>
// Treats a /live or /ready probe that the client hung up on (for example the edge proxy reloading) as a
// client abort (499), not a server error, so it neither logs at Error nor alerts.
// </summary>
// </file>

using Serilog.Events;

namespace Hawk.Web.Infrastructure;

public static class HealthRequestAbort
{
    /// <summary>Non-standard (nginx) status for "client closed request".</summary>
    public const int ClientClosedRequest = 499;

    public static bool IsHealthPath(PathString path) =>
        path.Equals("/live", StringComparison.OrdinalIgnoreCase) || path.Equals("/ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>Request-log level: health probes stay at Verbose unless they failed on the server.</summary>
    public static LogEventLevel GetLevel(HttpContext httpContext, Exception? ex) =>
        ex is null && httpContext.Response.StatusCode < 500 && IsHealthPath(httpContext.Request.Path)
            ? LogEventLevel.Verbose
            : ex is not null || httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : LogEventLevel.Information;

    /// <summary>
    /// Swallows the cancellation a hung-up probe causes and answers 499. Only applies while
    /// <see cref="HttpContext.RequestAborted"/> is canceled; real failures still surface as 500/503.
    /// </summary>
    public static IApplicationBuilder UseHawkHealthRequestAbort(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsHealthPath(context.Request.Path))
            {
                await next();
                return;
            }

            try
            {
                await next();
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }

            if (context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequest;
            }
        });
}
