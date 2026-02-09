// <file>
// <summary>
// Per-monitor request headers (name/value pairs).
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.Monitoring;

/// <summary>
/// A single custom request header attached to a <see cref="Monitor"/>.
/// </summary>
public sealed class MonitorHeader
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to the owning monitor.
    /// </summary>
    public Guid MonitorId { get; set; }

    /// <summary>
    /// Header name.
    /// </summary>
    [MaxLength(200)]
    public required string Name { get; set; }

    /// <summary>
    /// Header value.
    /// </summary>
    [MaxLength(2000)]
    public required string Value { get; set; }

    /// <summary>
    /// Navigation to the owning monitor.
    /// </summary>
    public Monitor? Monitor { get; set; }
}

