using System.ComponentModel.DataAnnotations;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Data.Alerting;

/// <summary>
/// Persisted per-monitor alert state used to avoid spamming and to support
/// reminder + recovery notifications.
/// </summary>
public sealed class MonitorAlertState
{
    /// <summary>
    /// Primary key and foreign key to the monitored entity.
    /// </summary>
    public Guid MonitorId { get; set; }

    /// <summary>
    /// Number of consecutive failed runs as of the most recent run.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// When the current failure incident started (first failure in the streak).
    /// Null when not currently in an incident.
    /// </summary>
    public DateTimeOffset? FailureIncidentOpenedAt { get; set; }

    /// <summary>
    /// When the last failure alert email was successfully sent for the current incident
    /// (initial alert or reminder). Null if no alert has been successfully sent yet.
    /// </summary>
    public DateTimeOffset? LastFailureAlertSentAt { get; set; }

    /// <summary>
    /// Most recent failure alert error (if any).
    /// </summary>
    [MaxLength(2000)]
    public string? LastFailureAlertError { get; set; }

    /// <summary>
    /// True if the monitor has recovered from an alerted incident but we have not yet
    /// successfully sent the recovery email.
    /// </summary>
    public bool PendingRecoveryAlert { get; set; }

    /// <summary>
    /// When the last recovery email was successfully sent.
    /// </summary>
    public DateTimeOffset? LastRecoveryAlertSentAt { get; set; }

    /// <summary>
    /// Most recent recovery email error (if any).
    /// </summary>
    [MaxLength(2000)]
    public string? LastRecoveryAlertError { get; set; }

    /// <summary>
    /// Optimistic concurrency token (SQL Server rowversion).
    /// </summary>
    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public MonitorEntity? Monitor { get; set; }
}
