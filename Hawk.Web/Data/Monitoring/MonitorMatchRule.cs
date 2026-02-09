// <file>
// <summary>
// Per-monitor content verification rules (contains/regex) evaluated against response bodies.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Hawk.Web.Services;

namespace Hawk.Web.Data.Monitoring;

/// <summary>
/// A content verification rule attached to a <see cref="Monitor"/>.
/// </summary>
public sealed class MonitorMatchRule
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
    /// How the pattern should be applied.
    /// </summary>
    public ContentMatchMode Mode { get; set; } = ContentMatchMode.Contains;

    /// <summary>
    /// String or regex pattern to match.
    /// </summary>
    [MaxLength(2000)]
    public required string Pattern { get; set; }

    /// <summary>
    /// Navigation to the owning monitor.
    /// </summary>
    public Monitor? Monitor { get; set; }
}

