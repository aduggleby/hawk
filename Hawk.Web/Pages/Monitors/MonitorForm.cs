// <file>
// <summary>
// Shared form model for creating and editing monitors.
// Keeping this centralized ensures Create/Edit have identical validation and binding behavior.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Bound form model for monitor create/edit pages.
/// </summary>
public sealed class MonitorForm
{
    /// <summary>Header rows the Create/Edit forms offer; the API refuses more so nothing is silently lost on the next Edit save.</summary>
    public const int MaxHeaders = 5;

    /// <summary>Match-rule rows the Create/Edit forms offer.</summary>
    public const int MaxMatchRules = 5;

    /// <summary>
    /// Optimistic concurrency token (the monitor's <c>ConcurrencyStamp</c> Guid as loaded by the Edit page).
    /// Used by the Edit page to detect conflicting user edits.
    /// </summary>
    public string? ConcurrencyStamp { get; set; }

    /// <summary>
    /// Display name.
    /// </summary>
    [Display(Name = "Name")]
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional group (trimmed; blank means none). Takes the spelling of an existing group that matches ignoring case.
    /// </summary>
    [Display(Name = "Group")]
    [MaxLength(100)]
    public string? Group { get; set; }

    /// <summary>
    /// Monitor type. Create binds it; Edit overwrites it with the persisted type (the type is immutable).
    /// </summary>
    [Display(Name = "Monitor type")]
    public MonitorType Type { get; set; } = MonitorType.Http;

    /// <summary>
    /// Absolute URL to request. Required for HTTP monitors only (see <see cref="Validate"/>).
    /// </summary>
    [Display(Name = "URL")]
    [MaxLength(2048)]
    public string? Url { get; set; } = string.Empty;

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
    /// Fixed interval in seconds between runs (for heartbeats: the expected period).
    /// </summary>
    [Display(Name = "Interval")]
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Heartbeat only: grace time in seconds after the period.
    /// </summary>
    [Display(Name = "Grace")]
    public int GraceSeconds { get; set; } = 300;

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
    /// Optional additional HTTP status codes that count as success.
    /// </summary>
    [Display(Name = "Allowed HTTP status codes (additional OK)")]
    [MaxLength(256)]
    public string? AllowedStatusCodes { get; set; }

    /// <summary>
    /// Follow HTTP redirects (default). When off, the 3xx response itself is checked.
    /// </summary>
    [Display(Name = "Follow redirects")]
    public bool FollowRedirects { get; set; } = true;

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
    public string? ContentType { get; set; } = "application/json";

    /// <summary>
    /// POST body.
    /// </summary>
    [Display(Name = "Body")]
    public string? Body { get; set; }

    /// <summary>
    /// Header names (parallel arrays for simple Razor binding).
    /// </summary>
    public string[] HeaderNames { get; set; } = new string[MaxHeaders];

    /// <summary>
    /// Header values (parallel arrays for simple Razor binding).
    /// </summary>
    public string[] HeaderValues { get; set; } = new string[MaxHeaders];

    /// <summary>
    /// Match rule modes.
    /// </summary>
    public ContentMatchMode[] MatchModes { get; set; } = new ContentMatchMode[MaxMatchRules];

    /// <summary>
    /// Match rule patterns.
    /// </summary>
    public string[] MatchPatterns { get; set; } = new string[MaxMatchRules];

    /// <summary>
    /// Validates the form for its <see cref="Type"/>: URL/method/match rules for HTTP monitors, period and grace for heartbeats.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(IHostEnvironment env)
    {
        // Undefined numeric values (e.g. from a bulk import) would persist a monitor the scheduler cannot advance.
        if (!Enum.IsDefined(Type))
        {
            yield return new ValidationResult("Monitor type is not valid.", [nameof(Type)]);
            yield break;
        }

        var heartbeat = Type == MonitorType.Heartbeat;

        if (MonitorGroups.Validate(Group) is { } groupError)
            yield return new ValidationResult(groupError, [nameof(Group)]);

        if (heartbeat)
        {
            if (!MonitorIntervals.AllowedHeartbeatPeriods(env).Contains(IntervalSeconds))
                yield return new ValidationResult("Expected period is not allowed in this environment.", [nameof(IntervalSeconds)]);
            if (!MonitorIntervals.AllowedHeartbeatGraces(env).Contains(GraceSeconds))
                yield return new ValidationResult("Grace is not allowed in this environment.", [nameof(GraceSeconds)]);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Url))
                yield return new ValidationResult("URL is required.", [nameof(Url)]);
            else if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                yield return new ValidationResult("URL must be an absolute http/https URL.", [nameof(Url)]);

            if (!MonitorIntervals.AllowedSeconds(env).Contains(IntervalSeconds))
                yield return new ValidationResult("Interval is not allowed in this environment.", [nameof(IntervalSeconds)]);
        }

        if (AlertSettingsValidator.ValidateThreshold(AlertAfterConsecutiveFailures) is { } thresholdError)
            yield return new ValidationResult(thresholdError, [nameof(AlertAfterConsecutiveFailures)]);

        if (AlertSettingsValidator.ValidateEmail(AlertEmailOverride) is { } emailError)
            yield return new ValidationResult(emailError, [nameof(AlertEmailOverride)]);

        if (RunRetentionDays is < 1 or > 3650)
            yield return new ValidationResult("Run retention override must be between 1 and 3650 days.", [nameof(RunRetentionDays)]);

        // PostgreSQL text columns reject NUL characters (SQL Server accepts them); refuse them on every provider.
        // The alert email is checked by AlertSettingsValidator above.
        string?[] texts = heartbeat
            ? [Name]
            : [Name, Url, Method, ContentType, Body, AllowedStatusCodes, .. HeaderNames, .. HeaderValues, .. MatchPatterns];
        if (texts.Any(t => t is not null && t.Contains('\0')))
            yield return new ValidationResult("Fields must not contain NUL characters.", [string.Empty]);

        // Everything below configures the HTTP request; heartbeats have none.
        if (heartbeat)
            yield break;

        if (!AllowedStatusCodesParser.TryParse(AllowedStatusCodes, out _, out var statusCodeError))
            yield return new ValidationResult(statusCodeError ?? "Allowed HTTP status codes are invalid.", [nameof(AllowedStatusCodes)]);

        for (var i = 0; i < Math.Min(MatchModes.Length, MatchPatterns.Length); i++)
        {
            var pattern = (MatchPatterns[i] ?? string.Empty).Trim();
            if (MatchModes[i] == ContentMatchMode.Regex && pattern.Length > 0 && !IsValidRegex(pattern, out var regexError))
                yield return new ValidationResult($"Match rule {i + 1}: invalid regular expression ({regexError}).", [$"{nameof(MatchPatterns)}[{i}]"]);
        }

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

    /// <summary>
    /// Checks that a pattern compiles with the options the URL checker uses.
    /// </summary>
    public static bool IsValidRegex(string pattern, out string? error)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline,
                TimeSpan.FromSeconds(2));
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
