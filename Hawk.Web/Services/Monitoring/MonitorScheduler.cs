// <file>
// <summary>
// Hangfire-based scheduler loop for monitors.
//
// Why a "tick" loop:
// - Requirements allow only fixed intervals (minutes) for users, but E2E needs a special 5s interval in Testing.
// - Hangfire recurring jobs do not support sub-minute Cron schedules in the OSS version.
// - A self-scheduling Hangfire job can tick every N seconds and enqueue due monitors precisely enough for tests.
// </summary>
// </file>

using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Scheduler loop that enqueues due monitors and reschedules itself.
/// </summary>
public sealed class MonitorScheduler(
    ApplicationDbContext db,
    IBackgroundJobClient jobs,
    JobStorage jobStorage,
    ILogger<MonitorScheduler> logger,
    IHostEnvironment env,
    IConfiguration config) : IMonitorScheduler
{
    private const string LockKey = "hawk:scheduler:tick";

    /// <inheritdoc />
    [AutomaticRetry(Attempts = 0)]
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var enabled = config.GetValue("Hawk:Scheduler:Enabled", true);
        if (!enabled)
            return;

        var tickSeconds = config.GetValue("Hawk:Scheduler:TickSeconds", env.IsEnvironment("Testing") ? 5 : 30);
        tickSeconds = Math.Clamp(tickSeconds, 1, 300);

        // Acquire a distributed lock so multiple enqueued Tick jobs can't stampede.
        // Branch: if we cannot acquire the lock, we exit without scheduling the next tick (the lock holder will do it).
        IDisposable? distLock = null;
        IStorageConnection? conn = null;
        try
        {
            conn = jobStorage.GetConnection();
            distLock = conn.AcquireDistributedLock(LockKey, TimeSpan.FromSeconds(Math.Max(10, tickSeconds * 2)));

            var now = DateTimeOffset.UtcNow;

            // Enqueue due monitors in small batches to avoid long-running ticks.
            var due = await db.Monitors
                .Where(m => m.Enabled && (m.NextRunAt == null || m.NextRunAt <= now))
                .OrderBy(m => m.NextRunAt)
                .Take(50)
                .ToListAsync(cancellationToken);

            foreach (var monitor in due)
            {
                // Branch: guard against invalid config (interval <= 0).
                var interval = Math.Clamp(monitor.IntervalSeconds, 5, 24 * 60 * 60);
                monitor.NextRunAt = now.AddSeconds(interval);

                jobs.Enqueue<IMonitorRunner>(r => r.RunAsync(monitor.Id, "schedule", CancellationToken.None));
            }

            if (due.Count != 0)
                await db.SaveChangesAsync(cancellationToken);

            // Self-schedule the next tick.
            jobs.Schedule<IMonitorScheduler>(s => s.TickAsync(CancellationToken.None), TimeSpan.FromSeconds(tickSeconds));
        }
        catch (DistributedLockTimeoutException)
        {
            logger.LogDebug("Scheduler tick skipped (lock busy).");
        }
        finally
        {
            distLock?.Dispose();
            conn?.Dispose();
        }
    }

    /// <inheritdoc />
    public Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        // Best-effort starter: enqueue a tick. The distributed lock prevents stampedes.
        jobs.Enqueue<IMonitorScheduler>(s => s.TickAsync(CancellationToken.None));
        return Task.CompletedTask;
    }
}
