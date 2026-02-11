using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.Monitoring;

/// <summary>
/// Per-user monitor behavior settings.
/// </summary>
public sealed class UserMonitorSettings
{
    [MaxLength(450)]
    public required string UserId { get; set; }

    /// <summary>
    /// Optional account-wide run history retention in days.
    /// </summary>
    public int? RunRetentionDays { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
