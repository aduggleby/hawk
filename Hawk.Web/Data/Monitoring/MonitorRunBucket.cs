// <file>
// <summary>
// 15-minute rollup of completed monitor runs. Survives run pruning and feeds the 24 h strips and uptime figures.
// Written only by RunBucketRollup.
// </summary>
// </file>

namespace Hawk.Web.Data.Monitoring;

/// <summary>
/// Aggregated completed runs of one monitor in one UTC 15-minute interval.
/// </summary>
public sealed class MonitorRunBucket
{
    /// <summary>
    /// Foreign key to the monitor (part of the primary key; rows cascade with the monitor).
    /// </summary>
    public Guid MonitorId { get; set; }

    /// <summary>
    /// Start of the interval (UTC, aligned to 15 minutes; part of the primary key).
    /// </summary>
    public DateTimeOffset BucketStart { get; set; }

    /// <summary>
    /// Number of completed runs that started in the interval.
    /// </summary>
    public int RunCount { get; set; }

    /// <summary>
    /// Number of those runs that failed.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Sum of the run durations in milliseconds.
    /// </summary>
    public long TotalDurationMs { get; set; }
}
