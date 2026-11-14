// <file>
// <summary>
// Ties Hawk's request log to the edge proxy's access log: adopts the proxy's X-Request-Id as the request's
// TraceIdentifier (also shown on the error page) and adds request id, client IP and user id to the request log line.
// </summary>
// </file>

using System.Security.Claims;
using System.Text.RegularExpressions;
using Serilog;

namespace Hawk.Web.Infrastructure;

public static partial class RequestCorrelation
{
    public const string HeaderName = "X-Request-Id";

    /// <summary>Request-log line; the extra properties come from <see cref="Enrich"/>.</summary>
    public const string MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms (req {RequestId}, ip {ClientIp}, user {UserId})";

    // Caddy's {http.request.uuid} is a UUID; accept any short token so another proxy's ids work too.
    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidId();

    /// <summary>True when <paramref name="value"/> is a safe request id (short, no spaces or control characters).</summary>
    public static bool IsValid(string? value) => value is not null && ValidId().IsMatch(value);

    /// <summary>
    /// Uses a valid incoming <c>X-Request-Id</c> as <see cref="HttpContext.TraceIdentifier"/>. Only the edge proxy
    /// can reach the app port, and it always overwrites the header, so clients cannot choose the id in production.
    /// </summary>
    public static IApplicationBuilder UseHawkRequestCorrelation(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var id = context.Request.Headers[HeaderName].ToString();
            if (IsValid(id))
                context.TraceIdentifier = id;
            return next();
        });

    /// <summary>Adds request id, client IP (after forwarded headers) and user id to the request log line.</summary>
    public static void Enrich(IDiagnosticContext diagnostic, HttpContext context)
    {
        diagnostic.Set("RequestId", context.TraceIdentifier);
        diagnostic.Set("ClientIp", context.Connection.RemoteIpAddress?.ToString() ?? "-");
        diagnostic.Set("UserId", context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "-");
    }
}
