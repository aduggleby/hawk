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

using System.Globalization;
using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;

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
    private const string StateKey = "hawk:scheduler";
    private const string LastTickField = "lastTickAt";

    /// <summary>
    /// True when a tick at <paramref name="now"/> comes less than 3/4 of a tick interval after the last chain-continuing
    /// tick. One chain's ticks are always at least one interval apart (the next tick is scheduled after a tick ends),
    /// so it is never a duplicate; of two chains, one is a duplicate within one interval and ends.
    /// </summary>
    public static bool IsDuplicateTick(DateTimeOffset? lastTickAt, DateTimeOffset now, int tickSeconds) =>
        lastTickAt is { } last && now >= last && now - last < TimeSpan.FromSeconds(tickSeconds * 0.75);

    private static DateTimeOffset? ReadLastTickAt(IStorageConnection conn)
    {
        var entries = conn.GetAllEntriesFromHash(StateKey);
        return entries is not null
               && entries.TryGetValue(LastTickField, out var value)
               && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Seconds between scheduler ticks (default 30, or 5 in Testing), clamped to 1..300.
    /// </summary>
    public static int ResolveTickSeconds(IConfiguration config, IHostEnvironment env) =>
        Math.Clamp(config.GetValue("Hawk:Scheduler:TickSeconds", env.IsEnvironment("Testing") ? 5 : 30), 1, 300);

    private sealed record DueMonitor(Guid Id, int IntervalSeconds, MonitorType Type);

    /// <inheritdoc />
    [AutomaticRetry(Attempts = 0)]
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var enabled = config.GetValue("Hawk:Scheduler:Enabled", true);
        if (!enabled)
            return;

        var tickSeconds = ResolveTickSeconds(config, env);

        // One tick at a time. Never wait for the lock: Hangfire.PostgreSql waits by polling with Thread.Sleep on a
        // thread-pool thread, and many waiting ticks starved the pool (1-4 s stalls for every page and check).
        // Busy = exit without scheduling; the holder schedules the next tick.
        IDisposable? distLock = null;
        IStorageConnection? conn = null;
        try
        {
            conn = jobStorage.GetConnection();
            distLock = conn.AcquireDistributedLock(LockKey, TimeSpan.Zero);

            var now = DateTimeOffset.UtcNow;

            // Collapse duplicate tick chains (every app start enqueues a tick while the previous run's scheduled tick
            // survives). A tick that comes too soon after the last chain-continuing tick exits at once: it does no
            // work (the surviving chain does it within one interval), does not record itself (that would let two
            // chains end each other) and schedules no successor, so its chain ends. Exiting at once also keeps it
            // from holding the lock when the surviving chain's next tick arrives.
            var lastTickAt = ReadLastTickAt(conn);
            if (IsDuplicateTick(lastTickAt, now, tickSeconds))
            {
                logger.LogInformation("Scheduler tick is a duplicate chain (last tick {LastTickAt:O}); ending it.", lastTickAt);
                return;
            }
            conn.SetRangeInHash(StateKey, [new KeyValuePair<string, string>(LastTickField, now.ToString("O", CultureInfo.InvariantCulture))]);

            // Enqueue due monitors in small batches to avoid long-running ticks.
            var due = await db.Monitors
                .Where(m => m.Enabled && !m.IsPaused && (m.NextRunAt == null || m.NextRunAt <= now))
                // NULL ordering differs (SQL Server: first, PostgreSQL: last). Put never-scheduled monitors first
                // explicitly so they cannot be starved by more than one batch of overdue monitors.
                .OrderBy(m => m.NextRunAt == null ? 0 : 1)
                .ThenBy(m => m.NextRunAt)
                .Take(50)
                .Select(m => new DueMonitor(m.Id, m.IntervalSeconds, m.Type))
                .ToListAsync(cancellationToken);

            foreach (var monitor in due)
            {
                // Branch: guard against invalid config (interval <= 0).
                var interval = Math.Clamp(monitor.IntervalSeconds, 5, 24 * 60 * 60);
                jobs.Enqueue<IMonitorRunner>(r => r.RunAsync(monitor.Id, "schedule", CancellationToken.None));
            }

            if (due.Count != 0)
            {
                // Update NextRunAt with direct set-based writes so scheduler ticks never conflict with concurrent
                // worker updates. Bookkeeping deliberately leaves Monitor.ConcurrencyStamp unchanged.
                // Heartbeats are excluded: their NextRunAt is the ping deadline, written only by pings, recorded
                // misses and create/resume/edit/import (a bump here could overwrite a ping that just arrived).
                foreach (var batch in due.Where(m => m.Type == MonitorType.Http).GroupBy(m => Math.Clamp(m.IntervalSeconds, 5, 24 * 60 * 60)))
                {
                    var ids = batch.Select(m => m.Id).ToArray();
                    var nextRunAt = now.AddSeconds(batch.Key);

                    await db.Monitors
                        .Where(m => ids.Contains(m.Id))
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(m => m.NextRunAt, nextRunAt),
                            cancellationToken);
                }
            }

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

    /// <summary>Recurring job id of <see cref="WatchdogAsync"/>.</summary>
    public const string WatchdogJobId = "hawk-scheduler-watchdog";

    /// <summary>
    /// True when the tick chain looks dead: no recorded tick, the last one is older than three intervals plus a minute
    /// (delayed jobs fire up to ~15 s late, a tick can be slow), or it lies more than one interval in the future (the
    /// clock went back). A live chain rarely gets there (a very slow tick, a backed-up queue); an extra tick is harmless.
    /// </summary>
    public static bool IsChainStale(DateTimeOffset? lastTickAt, DateTimeOffset now, int tickSeconds) =>
        lastTickAt is not { } last
        || now - last > TimeSpan.FromSeconds(tickSeconds * 3 + 60)
        || last - now > TimeSpan.FromSeconds(tickSeconds);

    /// <summary>
    /// Minutely safety net: restarts the tick chain when it died (a tick threw after recording itself, a transient
    /// lock error counted as busy, a lock orphaned by a crash). Only app start restarted it before. An extra tick is
    /// harmless: if a chain is alive after all, one of the two ends as a duplicate.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public Task WatchdogAsync(CancellationToken cancellationToken)
    {
        if (!config.GetValue("Hawk:Scheduler:Enabled", true))
            return Task.CompletedTask;

        var tickSeconds = ResolveTickSeconds(config, env);
        DateTimeOffset? lastTickAt;
        using (var conn = jobStorage.GetConnection())
            lastTickAt = ReadLastTickAt(conn);

        if (IsChainStale(lastTickAt, DateTimeOffset.UtcNow, tickSeconds))
        {
            logger.LogWarning("Scheduler tick chain is stale (last tick {LastTickAt}); enqueuing a tick.", lastTickAt?.ToString("O") ?? "never");
            jobs.Enqueue<IMonitorScheduler>(s => s.TickAsync(CancellationToken.None));
        }
        return Task.CompletedTask;
    }

    /// <summary>Registers the minutely watchdog, or removes it when scheduling is off.</summary>
    public static void EnsureWatchdog(IRecurringJobManager recurring, bool schedulerEnabled)
    {
        if (schedulerEnabled)
            recurring.AddOrUpdate<IMonitorScheduler>(WatchdogJobId, s => s.WatchdogAsync(CancellationToken.None), Cron.Minutely());
        else
            recurring.RemoveIfExists(WatchdogJobId);
    }

    /// <inheritdoc />
    public Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        // Best-effort starter: enqueue a tick. If the previous run's chain is still scheduled, one of the two chains
        // ends as a duplicate within one interval (see IsDuplicateTick).
        jobs.Enqueue<IMonitorScheduler>(s => s.TickAsync(CancellationToken.None));
        return Task.CompletedTask;
    }
}
