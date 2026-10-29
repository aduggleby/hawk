// <file>
// <summary>
// Monitoring configuration entities (what to check and how often).
// Stored via EF Core (SQL Server or PostgreSQL) and executed by Hangfire jobs.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.Monitoring;

/// <summary>
/// How a monitor is checked.
/// </summary>
public enum MonitorType
{
    /// <summary>Hawk requests a URL on a schedule.</summary>
    Http = 0,

    /// <summary>The client calls Hawk (<c>/ping/{token}</c>); Hawk fails the monitor when no ping arrives in time.</summary>
    Heartbeat = 1,
}

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
    /// Optional group name (free text, trimmed, case-insensitively unique by spelling; see <c>MonitorGroups</c>). Null means ungrouped.
    /// Named GroupName because GROUP is a reserved SQL word. Not indexed: group filters use ToLower() and cannot seek.
    /// </summary>
    [MaxLength(100)]
    public string? GroupName { get; set; }

    /// <summary>
    /// Check type. Immutable after creation.
    /// </summary>
    public MonitorType Type { get; set; } = MonitorType.Http;

    /// <summary>
    /// Absolute URL to request (empty for heartbeats).
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
    /// True if this monitor is temporarily paused while still enabled.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Fixed interval in seconds between executions (for heartbeats: the expected period between pings).
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Heartbeat only: extra seconds after the period before a missing ping counts as a failure.
    /// </summary>
    public int HeartbeatGraceSeconds { get; set; }

    /// <summary>
    /// Heartbeat only: secret URL token (32 lowercase hex characters, unique). Null for HTTP monitors.
    /// </summary>
    [MaxLength(32)]
    public string? HeartbeatToken { get; set; }

    /// <summary>
    /// Heartbeat only: when the last accepted ping arrived (UTC). Written set-based.
    /// </summary>
    public DateTimeOffset? LastPingAt { get; set; }

    /// <summary>
    /// When the scheduler should run this monitor next. For heartbeats this is the ping deadline, which the
    /// scheduler never writes (only create/import/resume/edit, pings and recorded misses do).
    /// </summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>
    /// When this monitor last started.
    /// </summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>
    /// Number of consecutive failed runs required before an alert email is sent.
    /// Alerting triggers only when the failure streak reaches this threshold (once per incident).
    /// </summary>
    public int AlertAfterConsecutiveFailures { get; set; } = 1;

    /// <summary>
    /// Optional override for alert recipient email address for this monitor.
    /// If not set, falls back to the owner's account-wide override, then the owner's Identity email.
    /// </summary>
    [MaxLength(320)]
    public string? AlertEmailOverride { get; set; }

    /// <summary>
    /// Optional additional HTTP status codes that should count as success.
    /// Comma-separated numeric list (for example: 404,429).
    /// 2xx status codes are always treated as success.
    /// </summary>
    [MaxLength(256)]
    public string? AllowedStatusCodes { get; set; }

    /// <summary>
    /// When false the check does not follow redirects and evaluates the 3xx response itself (it succeeds only if its code is allowed).
    /// HTTP monitors only; heartbeats store true.
    /// </summary>
    public bool FollowRedirects { get; set; } = true;

    /// <summary>
    /// Optional run history retention override in days for this monitor.
    /// If not set, falls back to the owner's account-wide setting, then the server default.
    /// </summary>
    public int? RunRetentionDays { get; set; }

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
    /// App-managed optimistic concurrency token (provider-agnostic).
    /// Set to a new value only on intentional user changes (create, edit, import, pause/resume).
    /// Scheduler/executor bookkeeping (<see cref="NextRunAt"/>, <see cref="LastRunAt"/>) must not change it,
    /// so the Edit page only conflicts with real concurrent edits.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

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
