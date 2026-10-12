// <file>
// <summary>
// Readiness check for GET /ready: healthy when the application database accepts connections.
// GET /live runs no checks (process up only).
// </summary>
// </file>

using Hawk.Web.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hawk.Web.Infrastructure;

/// <summary>
/// Reports whether the application database is reachable.
/// </summary>
public sealed class DatabaseHealthCheck(ApplicationDbContext db) : IHealthCheck
{
    /// <summary>Tag used to select this check for /ready.</summary>
    public const string ReadyTag = "ready";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database check failed.", ex);
        }
    }
}
