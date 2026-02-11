using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.Alerting;

/// <summary>
/// Per-user alerting settings (separate from Identity email).
/// </summary>
public sealed class UserAlertSettings
{
    [MaxLength(450)]
    public required string UserId { get; set; }

    [MaxLength(320)]
    public required string AlertEmail { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

