// <file>
// <summary>
// Monitoring configuration entities (what to check and how often).
// Stored in SQL Server via EF Core and executed by Hangfire jobs.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.Monitoring;

/// <summary>
/// A configured uptime/content check.
/// </summary>
public sealed class Monitor
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name for this monitor.
    /// </summary>
    [MaxLength(200)]
    public required string Name { get; set; }

    /// <summary>
    /// Absolute URL to request.
    /// </summary>
    [MaxLength(2048)]
    public required string Url { get; set; }

    /// <summary>
    /// HTTP method string (e.g., GET, POST).
    /// </summary>
    [MaxLength(16)]
    public required string Method { get; set; }

    /// <summary>
    /// True if the scheduler should run this monitor.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Fixed interval in seconds between executions.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// When the scheduler should run this monitor next.
    /// </summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>
    /// When this monitor last started.
    /// </summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>
    /// Optional request content-type (used for POST/PUT/PATCH).
    /// </summary>
    [MaxLength(200)]
    public string? ContentType { get; set; }

    /// <summary>
    /// Optional request body (used for POST/PUT/PATCH).
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    /// Created timestamp.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Foreign key to the Identity user that created the monitor (optional).
    /// </summary>
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// Optional navigation to headers.
    /// </summary>
    public List<MonitorHeader> Headers { get; set; } = [];

    /// <summary>
    /// Optional navigation to match rules.
    /// </summary>
    public List<MonitorMatchRule> MatchRules { get; set; } = [];
}

