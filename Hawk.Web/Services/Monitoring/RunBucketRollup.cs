// <file>
// <summary>
// Single writer of the MonitorRunBuckets table (15-minute rollup of completed runs).
//
// Why a rollup: run history is pruned (default 90 days, at least 1 day), but the 24 h strip and the 90-day uptime must
// survive pruning. Every write path runs under the Hangfire distributed lock "hawk:run-buckets", so two workers never
// insert the same (MonitorId, BucketStart) key; a busy lock skips the call because the next minute repairs any gap.
// The write rule (see Merge) lets late or backfilled history fill gaps without ever shrinking stored history.
// </summary>
// </file>

using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// One completed run reduced to what the rollup needs.
/// </summary>
/// <param name="MonitorId">Monitor id.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="Success">Run result.</param>
/// <param name="DurationMs">Run duration in milliseconds.</param>
public sealed record RunSample(Guid MonitorId, DateTimeOffset StartedAt, bool Success, int DurationMs);

/// <summary>
/// Outcome of <see cref="RunBucketRollup.Merge"/>.
/// </summary>
public enum RunBucketWrite
{
    /// <summary>Keep the stored row as it is.</summary>
    None,

    /// <summary>Insert the computed row.</summary>
    Insert,

    /// <summary>Overwrite the stored row with the computed values.</summary>
    Overwrite,
}

/// <summary>
/// Fills and maintains <c>MonitorRunBuckets</c>.
/// </summary>
public sealed class RunBucketRollup(
    ApplicationDbContext db,
    IConfiguration config,
    ILogger<RunBucketRollup> logger,
    JobStorage? jobStorage = null) : IRunBucketRollup
{
    /// <summary>Recurring job id of <see cref="RefreshAsync"/>.</summary>
    public const string RecurringJobId = "hawk-run-buckets";

    private const string LockKey = "hawk:run-buckets";
    private const int BucketMinutes = 15;
    private static readonly TimeSpan BucketLength = TimeSpan.FromMinutes(BucketMinutes);
    private static readonly TimeSpan OverwriteWindow = TimeSpan.FromHours(23);
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxRefreshLookBack = TimeSpan.FromDays(7);
    private static readonly TimeSpan BackfillSpan = TimeSpan.FromDays(90);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(95);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Start of the 15-minute UTC bucket containing <paramref name="time"/>.
    /// </summary>
    public static DateTimeOffset BucketStartOf(DateTimeOffset time)
    {
        var ticks = time.UtcTicks;
        return new DateTimeOffset(ticks - ticks % BucketLength.Ticks, TimeSpan.Zero);
    }

    /// <summary>
    /// Pure aggregation of runs into buckets (one per monitor and 15-minute interval).
    /// </summary>
    public static List<MonitorRunBucket> Bucket(IEnumerable<RunSample> runs) =>
        runs.GroupBy(r => (r.MonitorId, Start: BucketStartOf(r.StartedAt)))
            .Select(g => new MonitorRunBucket
            {
                MonitorId = g.Key.MonitorId,
                BucketStart = g.Key.Start,
                RunCount = g.Count(),
                FailureCount = g.Count(r => !r.Success),
                TotalDurationMs = g.Sum(r => (long)r.DurationMs),
            })
            .OrderBy(b => b.MonitorId)
            .ThenBy(b => b.BucketStart)
            .ToList();

    /// <summary>
    /// Pure write rule: buckets that started less than 23 hours ago are overwritten (their runs are complete because
    /// retention is at least one day); older buckets are only written when missing or when the computed run count is
    /// higher, because pruning only lowers counts and must never shrink history.
    /// </summary>
    public static RunBucketWrite Merge(MonitorRunBucket? existing, MonitorRunBucket computed, DateTimeOffset now)
    {
        if (existing is null)
            return RunBucketWrite.Insert;
        if (computed.BucketStart > now - OverwriteWindow)
            return RunBucketWrite.Overwrite;
        return computed.RunCount > existing.RunCount ? RunBucketWrite.Overwrite : RunBucketWrite.None;
    }

    /// <summary>
    /// Registers (or removes) the recurring refresh job and enqueues the startup backfill.
    /// </summary>
    /// <param name="schedulerEnabled">Value of <c>Hawk:Scheduler:Enabled</c>.</param>
    public static void EnsureStarted(IRecurringJobManager recurring, IBackgroundJobClient jobs, bool schedulerEnabled)
    {
        if (!schedulerEnabled)
        {
            recurring.RemoveIfExists(RecurringJobId);
            return;
        }

        recurring.AddOrUpdate<IRunBucketRollup>(RecurringJobId, r => r.RefreshAsync(CancellationToken.None), Cron.Minutely());
        jobs.Enqueue<IRunBucketRollup>(r => r.BackfillAsync(CancellationToken.None));
    }

    /// <inheritdoc />
    [AutomaticRetry(Attempts = 0)]
    public Task RecomputeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        RunLockedAsync(() => RecomputeCoreAsync(from, to, cancellationToken));

    /// <inheritdoc />
    [AutomaticRetry(Attempts = 0)]
    public Task RefreshAsync(CancellationToken cancellationToken) =>
        RunLockedAsync(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var newest = await db.MonitorRunBuckets
                .OrderByDescending(b => b.BucketStart)
                .Select(b => (DateTimeOffset?)b.BucketStart)
                .FirstOrDefaultAsync(cancellationToken);

            var start = now - RefreshWindow;
            if (newest is not null && newest < start)
                start = newest.Value;
            if (start < now - MaxRefreshLookBack)
                start = now - MaxRefreshLookBack;

            await RecomputeCoreAsync(start, now, cancellationToken);
            await DeleteOldAsync(now - Retention, cancellationToken);
        });

    /// <inheritdoc />
    [AutomaticRetry(Attempts = 0)]
    public Task BackfillAsync(CancellationToken cancellationToken) =>
        RunLockedAsync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            return RecomputeCoreAsync(now - BackfillSpan, now, cancellationToken);
        });

    private async Task RunLockedAsync(Func<Task> work)
    {
        // Branch: scheduling off (as MonitorScheduler.TickAsync), nothing runs on this instance.
        if (!config.GetValue("Hawk:Scheduler:Enabled", true))
            return;

        // Branch: no job storage (EF InMemory unit tests) runs unlocked.
        if (jobStorage is null)
        {
            await work();
            return;
        }

        IStorageConnection? conn = null;
        IDisposable? distLock = null;
        try
        {
            conn = jobStorage.GetConnection();
            distLock = conn.AcquireDistributedLock(LockKey, LockTimeout);
            await work();
        }
        catch (DistributedLockTimeoutException)
        {
            logger.LogDebug("Run bucket rollup skipped (lock busy).");
        }
        finally
        {
            distLock?.Dispose();
            conn?.Dispose();
        }
    }

    private async Task RecomputeCoreAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cursor = BucketStartOf(from);
        var end = to.ToUniversalTime();

        while (cursor < end)
        {
            var dayEnd = new DateTimeOffset(cursor.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            var windowEnd = dayEnd < end ? dayEnd : end;

            var samples = await db.MonitorRuns
                .Where(r => r.State == "completed" && r.StartedAt >= cursor && r.StartedAt < windowEnd)
                .Select(r => new { r.MonitorId, r.StartedAt, r.Success, r.DurationMs })
                .ToListAsync(cancellationToken);
            var computed = Bucket(samples.Select(r => new RunSample(r.MonitorId, r.StartedAt, r.Success, r.DurationMs)));

            if (computed.Count != 0)
            {
                var existing = (await db.MonitorRunBuckets
                        .Where(b => b.BucketStart >= cursor && b.BucketStart < windowEnd)
                        .ToListAsync(cancellationToken))
                    .ToDictionary(b => (b.MonitorId, b.BucketStart.UtcTicks));

                foreach (var bucket in computed)
                {
                    existing.TryGetValue((bucket.MonitorId, bucket.BucketStart.UtcTicks), out var stored);
                    switch (Merge(stored, bucket, now))
                    {
                        case RunBucketWrite.Insert:
                            db.MonitorRunBuckets.Add(bucket);
                            break;
                        case RunBucketWrite.Overwrite:
                            stored!.RunCount = bucket.RunCount;
                            stored.FailureCount = bucket.FailureCount;
                            stored.TotalDurationMs = bucket.TotalDurationMs;
                            break;
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            cursor = windowEnd;
        }
    }

    private async Task DeleteOldAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            await db.MonitorRunBuckets.Where(b => b.BucketStart < cutoff).ExecuteDeleteAsync(cancellationToken);
            return;
        }

        // Branch: EF InMemory (unit tests) has no ExecuteDelete.
        db.MonitorRunBuckets.RemoveRange(await db.MonitorRunBuckets.Where(b => b.BucketStart < cutoff).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }
}
