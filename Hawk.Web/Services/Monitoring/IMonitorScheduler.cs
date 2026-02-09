// <file>
// <summary>
// Scheduler contract for dispatching due monitors. Implemented using Hangfire jobs.
// </summary>
// </file>

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Dispatches monitors on schedule by enqueuing <see cref="IMonitorRunner"/> jobs.
/// </summary>
public interface IMonitorScheduler
{
    /// <summary>
    /// Runs one scheduler tick: finds due monitors, enqueues runs, and schedules the next tick.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TickAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ensures the scheduler loop is started.
    /// </summary>
    Task EnsureStartedAsync(CancellationToken cancellationToken);
}

