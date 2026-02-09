// <file>
// <summary>
// Monitor execution contract, used by Hangfire jobs and UI "run now" actions.
// </summary>
// </file>

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Executes a configured monitor and persists the result.
/// </summary>
public interface IMonitorRunner
{
    /// <summary>
    /// Runs a monitor immediately.
    /// </summary>
    /// <param name="monitorId">Monitor id.</param>
    /// <param name="reason">Optional reason (schedule/manual).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunAsync(Guid monitorId, string? reason, CancellationToken cancellationToken);
}

