// <file>
// <summary>
// Alert policy helpers.
//
// Hawk v1 supports a simple "consecutive failures threshold" rule:
// - Do not email on every failing run.
// - Email only when the failure streak reaches the configured threshold.
// - After a success, the failure streak resets and the next incident can alert again.
// </summary>
// </file>

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Helper for deciding whether a failing run should trigger an alert.
/// </summary>
public static class AlertPolicy
{
    /// <summary>
    /// Returns true if an alert should be sent for the current failed attempt.
    /// </summary>
    /// <param name="threshold">Consecutive failures required before alerting.</param>
    /// <param name="priorConsecutiveFailures">Number of consecutive failures immediately before the current attempt.</param>
    /// <returns>True if the current failure reaches the threshold boundary.</returns>
    public static bool ShouldAlertOnFailure(int threshold, int priorConsecutiveFailures)
    {
        // Branch: sanitize input; treat anything <= 1 as "alert on first failure of an incident".
        threshold = Math.Clamp(threshold, 1, 20);
        priorConsecutiveFailures = Math.Max(0, priorConsecutiveFailures);

        // Alert only when we reach the threshold boundary (once per incident).
        return priorConsecutiveFailures + 1 == threshold;
    }
}

