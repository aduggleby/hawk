// <file>
// <summary>
// Shared form model for creating and editing monitors.
// Keeping this centralized ensures Create/Edit have identical validation and binding behavior.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Bound form model for monitor create/edit pages.
/// </summary>
public sealed class MonitorForm
{
    /// <summary>
    /// Display name.
    /// </summary>
    [Display(Name = "Name")]
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Absolute URL to request.
    /// </summary>
    [Display(Name = "URL")]
    [Required, MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// HTTP method: GET or POST.
    /// </summary>
    [Display(Name = "Method")]
    [Required, MaxLength(16)]
    public string Method { get; set; } = "GET";

    /// <summary>
    /// Enabled flag.
    /// </summary>
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Timeout in seconds.
    /// </summary>
    [Display(Name = "Timeout (s)")]
    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Fixed interval in seconds between runs.
    /// </summary>
    [Display(Name = "Interval")]
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Number of consecutive failed runs required before an alert email is sent.
    /// </summary>
    [Display(Name = "Alert after consecutive failures")]
    [Range(1, 20)]
    public int AlertAfterConsecutiveFailures { get; set; } = 1;

    /// <summary>
    /// Optional per-monitor override for alert recipient email.
    /// </summary>
    [Display(Name = "Alert email override")]
    [EmailAddress]
    [MaxLength(320)]
    public string? AlertEmailOverride { get; set; }

    /// <summary>
    /// Optional run history retention override in days.
    /// </summary>
    [Display(Name = "Run retention (days) override")]
    [Range(1, 3650)]
    public int? RunRetentionDays { get; set; }

    /// <summary>
    /// POST content-type.
    /// </summary>
    [Display(Name = "Content-Type")]
    [MaxLength(200)]
    public string? ContentType { get; set; }

    /// <summary>
    /// POST body.
    /// </summary>
    [Display(Name = "Body")]
    public string? Body { get; set; }

    /// <summary>
    /// Header names (parallel arrays for simple Razor binding).
    /// </summary>
    public string[] HeaderNames { get; set; } = new string[5];

    /// <summary>
    /// Header values (parallel arrays for simple Razor binding).
    /// </summary>
    public string[] HeaderValues { get; set; } = new string[5];

    /// <summary>
    /// Match rule modes.
    /// </summary>
    public ContentMatchMode[] MatchModes { get; set; } = new ContentMatchMode[5];

    /// <summary>
    /// Match rule patterns.
    /// </summary>
    public string[] MatchPatterns { get; set; } = new string[5];

    /// <summary>
    /// Validates URL scheme and allowed intervals.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(IHostEnvironment env)
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            yield return new ValidationResult("URL must be an absolute http/https URL.", [nameof(Url)]);

        if (!MonitorIntervals.AllowedSeconds(env).Contains(IntervalSeconds))
            yield return new ValidationResult("Interval is not allowed in this environment.", [nameof(IntervalSeconds)]);

        if (AlertAfterConsecutiveFailures < 1 || AlertAfterConsecutiveFailures > 20)
            yield return new ValidationResult("Alert threshold must be between 1 and 20.", [nameof(AlertAfterConsecutiveFailures)]);

        if (!string.IsNullOrWhiteSpace(AlertEmailOverride))
        {
            var emailAttr = new EmailAddressAttribute();
            if (!emailAttr.IsValid(AlertEmailOverride))
                yield return new ValidationResult("Alert email override must be a valid email address.", [nameof(AlertEmailOverride)]);
        }

        if (RunRetentionDays is < 1 or > 3650)
            yield return new ValidationResult("Run retention override must be between 1 and 3650 days.", [nameof(RunRetentionDays)]);

        var method = (Method ?? string.Empty).Trim().ToUpperInvariant();
        if (method is not ("GET" or "POST"))
            yield return new ValidationResult("Method must be GET or POST.", [nameof(Method)]);

        // Branch: require content-type and body only for POST in the UI model.
        if (method == "POST")
        {
            if (string.IsNullOrWhiteSpace(ContentType))
                yield return new ValidationResult("Content-Type is required for POST.", [nameof(ContentType)]);
            if (Body is null)
                yield return new ValidationResult("Body is required for POST (can be empty string).", [nameof(Body)]);
        }
    }
}
