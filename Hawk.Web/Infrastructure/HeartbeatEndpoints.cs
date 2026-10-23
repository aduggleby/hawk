// <file>
// <summary>
// Anonymous heartbeat ping endpoint: GET/HEAD/POST /ping/{token}.
// The token is a bearer secret, so nothing here logs the path (see also LogRedaction and the request-logging setup in Program.cs).
// </summary>
// </file>

using Hawk.Web.Services.Monitoring;

namespace Hawk.Web.Infrastructure;

public static class HeartbeatEndpoints
{
    /// <summary>Route prefix of the ping endpoint.</summary>
    public const string PathPrefix = "/ping/";

    /// <summary>
    /// Maps <c>/ping/{token}</c>. Like <c>/live</c> it is anonymous; it is a minimal-API endpoint, so the Razor Pages
    /// authorization conventions and antiforgery do not apply.
    /// </summary>
    public static IEndpointRouteBuilder MapHawkHeartbeats(this IEndpointRouteBuilder app)
    {
        app.MapMethods(PathPrefix + "{token}", [HttpMethods.Get, HttpMethods.Head, HttpMethods.Post], HandlePingAsync)
            .AllowAnonymous();
        return app;
    }

    /// <summary>
    /// Handles one ping inline. Malformed tokens return 404 before any database access.
    /// </summary>
    public static async Task<IResult> HandlePingAsync(string token, HttpContext http, IMonitorExecutor executor)
    {
        if (!HeartbeatTokens.TryNormalize(token, out var normalized))
            return Results.NotFound();

        var request = http.Request;
        var info = new PingRequestInfo(
            request.Method,
            request.Headers.UserAgent.ToString(),
            request.ContentType,
            http.Connection.RemoteIpAddress?.ToString());

        // A client that gives up (curl -m 10) must not leave a half-applied ping: finish regardless of RequestAborted.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var result = await executor.RecordPingAsync(normalized, info, timeout.Token);
            http.Response.Headers.CacheControl = "no-store";
            if (result == PingResult.Busy)
            {
                // Nothing was written; curl --retry retries 503 (honouring Retry-After).
                http.Response.Headers.RetryAfter = "5";
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            return result switch
            {
                PingResult.NotFound => Results.NotFound(),
                PingResult.Paused => Results.Text("paused"),
                _ => Results.Text("OK"),
            };
        }
        catch (Exception)
        {
            // Already logged by the executor with the monitor id; never log here, the path carries the token.
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Replaces the secret token in request paths so it never reaches the logs.
    /// </summary>
    public static string RedactPath(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) ? PathPrefix + "{redacted}" : path;
}
