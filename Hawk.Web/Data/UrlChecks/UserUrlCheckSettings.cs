using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.UrlChecks;

/// <summary>
/// Per-user URL check HTTP settings.
/// </summary>
public sealed class UserUrlCheckSettings
{
    [MaxLength(450)]
    public required string UserId { get; set; }

    /// <summary>
    /// Either a preset key (e.g., "firefox") or a full User-Agent string.
    /// </summary>
    [MaxLength(512)]
    public required string UserAgent { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

