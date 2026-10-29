// <file>
// <summary>
// Shared checks for the alert settings a user can change: failure threshold and alert email.
// Used by the monitor form and by every other writer of these fields so they cannot drift apart.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Validation rules for alert threshold and alert email values.
/// </summary>
public static class AlertSettingsValidator
{
    public const int MinThreshold = 1;
    public const int MaxThreshold = 20;
    public const int MaxEmailLength = 320;

    /// <summary>
    /// Returns an error message, or null when the threshold is valid.
    /// </summary>
    public static string? ValidateThreshold(int threshold) =>
        threshold is < MinThreshold or > MaxThreshold
            ? $"Alert threshold must be between {MinThreshold} and {MaxThreshold}."
            : null;

    /// <summary>
    /// Returns an error message, or null when the email is valid. Null or blank means "no email" and is valid.
    /// </summary>
    public static string? ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        // PostgreSQL text columns reject NUL characters (SQL Server accepts them); refuse them on every provider.
        if (email.Contains('\0'))
            return "Fields must not contain NUL characters.";
        if (email.Length > MaxEmailLength)
            return $"Alert email must be at most {MaxEmailLength} characters.";
        if (!new EmailAddressAttribute().IsValid(email))
            return "Alert email override must be a valid email address.";
        return null;
    }
}
