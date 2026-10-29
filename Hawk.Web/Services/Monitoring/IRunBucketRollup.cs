// <file>
// <summary>
// Contract of the run-bucket rollup jobs. Implemented by RunBucketRollup and executed as Hangfire jobs.
// </summary>
// </file>

using Hangfire;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Maintains the 15-minute <c>MonitorRunBuckets</c> table from completed runs.
/// </summary>
public interface IRunBucketRollup
{
    /// <summary>
    /// Recomputes the buckets of all monitors that started in <paramref name="from"/> (rounded down to 15 minutes)
    /// up to <paramref name="to"/>, one range query per monitor. Waits for the lock and fails (Hangfire retries) when it stays busy.
    /// </summary>
    /// <param name="from">Start of the range (UTC).</param>
    /// <param name="to">End of the range (UTC, exclusive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AutomaticRetry(Attempts = 5)]
    Task RecomputeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>
    /// Recurring job: recomputes the recent window (repairing gaps up to 7 days) and deletes buckets older than 95 days.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AutomaticRetry(Attempts = 0)]
    Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Startup job: recomputes the last 90 days (idempotent, never shrinks history). Waits for the lock and fails (Hangfire retries) when it stays busy.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AutomaticRetry(Attempts = 5)]
    Task BackfillAsync(CancellationToken cancellationToken);
}
