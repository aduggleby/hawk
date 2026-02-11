// <file>
// <summary>
// Hangfire-executed monitor runner. Thin wrapper around <see cref="IMonitorExecutor"/>.
// </summary>
// </file>

using Hangfire;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Executes monitors and stores their run history.
/// </summary>
public sealed class MonitorRunner(IMonitorExecutor executor) : IMonitorRunner
{
    /// <inheritdoc />
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid monitorId, string? reason, CancellationToken cancellationToken)
    {
        await executor.ExecuteAsync(monitorId, reason, cancellationToken);
    }
}
