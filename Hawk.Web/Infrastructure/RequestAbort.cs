// <file>
// <summary>
// Treats a request the client hung up on (an abandoned page load, the edge proxy reloading during a probe) as a
// client abort (499), not a server error, and picks the request-log level (aborts Information, slow requests Warning).
// </summary>
// </file>

using Serilog.Events;

namespace Hawk.Web.Infrastructure;

public static class RequestAbort
{
    /// <summary>Non-standard (nginx, Caddy) status for "client closed request".</summary>
    public const int ClientClosedRequest = 499;

    /// <summary>Requests slower than this log at Warning (<c>Hawk:Logging:SlowRequestMs</c>).</summary>
    public const int DefaultSlowRequestMs = 1000;

    public static bool IsHealthPath(PathString path) =>
        path.Equals("/live", StringComparison.OrdinalIgnoreCase) || path.Equals("/ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Request-log level: server failures Error; health probes Verbose; client aborts (499) Information;
    /// other requests Warning when slower than <paramref name="slowRequestMs"/> (0 = off), else Information.
    /// </summary>
    public static LogEventLevel GetLevel(HttpContext httpContext, double elapsedMs, Exception? ex, int slowRequestMs = DefaultSlowRequestMs)
    {
        var status = httpContext.Response.StatusCode;
        if (ex is not null || status >= 500)
            return LogEventLevel.Error;
        if (IsHealthPath(httpContext.Request.Path))
            return LogEventLevel.Verbose;
        if (status == ClientClosedRequest)
            return LogEventLevel.Information;
        return slowRequestMs > 0 && elapsedMs > slowRequestMs ? LogEventLevel.Warning : LogEventLevel.Information;
    }

    /// <summary>
    /// Swallows the cancellation a client abort causes and answers 499. Only applies while
    /// <see cref="HttpContext.RequestAborted"/> is canceled; real failures still surface as 500/503.
    /// </summary>
    public static IApplicationBuilder UseHawkRequestAbort(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
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
