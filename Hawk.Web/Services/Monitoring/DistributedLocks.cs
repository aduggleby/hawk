// <file>
// <summary>
// Acquires Hangfire distributed locks without blocking a thread-pool thread while the lock is busy.
// </summary>
// </file>

using System.Diagnostics;
using Hangfire.Storage;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Hangfire's <c>AcquireDistributedLock(resource, timeout)</c> waits for a busy lock by polling with
/// <c>Thread.Sleep</c> (Hangfire.PostgreSql) or a blocking wait (Hangfire.SqlServer) on the calling thread, which in
/// Hawk's async code is a thread-pool thread. Many waiters starve the pool (2026-11-14: every page, query and check
/// stalled 1-4 s). <see cref="AcquireAsync"/> instead makes zero-wait attempts (one database round trip each) and
/// waits between them with <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, so a waiter holds no thread.
/// Each attempt is still a short synchronous database round trip (Hangfire has no async lock API): milliseconds,
/// and at most a few per second per waiter.
/// </summary>
public static class DistributedLocks
{
    /// <summary>First wait between attempts; doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public static readonly TimeSpan MinRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Longest wait between attempts.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Acquires <paramref name="resource"/> on <paramref name="connection"/>, retrying until <paramref name="timeout"/>
    /// has passed (<see cref="TimeSpan.Zero"/> = one attempt). Throws <see cref="DistributedLockTimeoutException"/>
    /// (or the provider's subclass) when the lock stays busy, like Hangfire's own method, so callers keep their
    /// busy handling. The lock belongs to the connection: keep the connection open until the lock is disposed.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(
        IStorageConnection connection, string resource, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var delay = MinRetryDelay;
        while (true)
        {
            TimeSpan remaining;
            try
            {
                return connection.AcquireDistributedLock(resource, TimeSpan.Zero);
            }
            catch (DistributedLockTimeoutException)
                when ((remaining = timeout - Stopwatch.GetElapsedTime(started)) > TimeSpan.Zero)
            {
                // Busy (or a transient error the provider reports as busy): wait without holding a thread.
            }

            await Task.Delay(delay < remaining ? delay : remaining, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }
}
