// <file>
// <summary>
// Monitor execution history. Each row represents a single scheduled or manual run.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.Monitoring;

/// <summary>
/// Persisted result of running a <see cref="Monitor"/>.
/// </summary>
public sealed class MonitorRun
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to the monitor that was executed.
    /// </summary>
    public Guid MonitorId { get; set; }

    /// <summary>
    /// When the run started (UTC).
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// When the run finished (UTC).
    /// </summary>
    public DateTimeOffset FinishedAt { get; set; }

    /// <summary>
    /// Total duration in milliseconds.
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// HTTP status code (if a response was received).
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// True if the request succeeded and all match rules passed.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Failure description (HTTP failure, timeout, match failure, network error, etc.).
    /// </summary>
    [MaxLength(4000)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Short response snippet (if captured).
    /// </summary>
    [MaxLength(2000)]
    public string? ResponseSnippet { get; set; }

    /// <summary>
    /// JSON-serialized match results for display/debugging.
    /// </summary>
    public string MatchResultsJson { get; set; } = "[]";

    /// <summary>
    /// True if an alert was sent for this run (best-effort).
    /// </summary>
    public bool AlertSent { get; set; }

    /// <summary>
    /// Optional error message from alert sending.
    /// </summary>
    [MaxLength(2000)]
    public string? AlertError { get; set; }

    /// <summary>
    /// Navigation to the monitor.
    /// </summary>
    public Monitor? Monitor { get; set; }
}

