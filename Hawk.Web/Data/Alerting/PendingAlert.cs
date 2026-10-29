// <file>
// <summary>
// Outbox row for one alert email (failure, reminder or recovery). Written in the same save as the run and the alert state;
// sent (alone or grouped into a summary per recipient set) only by AlertDigestSender.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.Alerting;

/// <summary>
/// One queued alert email. Carries everything needed to send it, so a monitor deleted in the meantime still gets its alert out.
/// </summary>
public sealed class PendingAlert
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Monitor that alerted (no foreign key: the alert is still sent after the monitor is deleted).
    /// </summary>
    public Guid MonitorId { get; set; }

    /// <summary>
    /// Run that raised the alert (no foreign key: runs are pruned).
    /// </summary>
    public Guid RunId { get; set; }

    /// <summary>
    /// <c>Failure</c>, <c>FailureReminder</c> or <c>Recovered</c> (<see cref="Hawk.Web.Services.Monitoring.MonitorAlertKind"/> names).
    /// </summary>
    [MaxLength(32)]
    public string Kind { get; set; } = "";

    /// <summary>
    /// Monitor name when the alert was raised.
    /// </summary>
    [MaxLength(200)]
    public string MonitorName { get; set; } = "";

    /// <summary>
    /// Short result shown in the summary (status code, NO_RESPONSE, NO_PING, PING_RECEIVED).
    /// </summary>
    [MaxLength(100)]
    public string Status { get; set; } = "";

    /// <summary>
    /// Run error message shown in the summary (failures only).
    /// </summary>
    [MaxLength(2000)]
    public string? Error { get; set; }

    /// <summary>
    /// Normalized recipients: trimmed, lowercased, distinct, ordinal-sorted and comma-joined. Alerts are grouped by this value.
    /// </summary>
    public string Recipients { get; set; } = "";

    /// <summary>
    /// Subject of the single-alert email.
    /// </summary>
    [MaxLength(500)]
    public string Subject { get; set; } = "";

    /// <summary>
    /// HTML body of the single-alert email (also embedded in the summary).
    /// </summary>
    public string Html { get; set; } = "";

    /// <summary>
    /// When the alerting run started (UTC).
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// When the row was queued (UTC); drives the quiet window and the maximum delay.
    /// </summary>
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>
    /// When the email (single or summary) containing this alert was sent. Null while queued.
    /// </summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>
    /// When delivery was given up (24 h of failed attempts). Null otherwise.
    /// </summary>
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>
    /// Number of failed send attempts.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Error of the last failed send attempt.
    /// </summary>
    [MaxLength(2000)]
    public string? LastError { get; set; }

    /// <summary>
    /// Number of alerts in the email this alert was sent in (1 = the single-alert email).
    /// </summary>
    public int? SentInBatchOf { get; set; }
}
