using Hawk.Web.Data.Alerting;

namespace Hawk.Web.Services.Monitoring;

public enum MonitorAlertKind
{
    None = 0,
    Failure = 1,
    FailureReminder = 2,
    Recovered = 3,
}

public sealed record MonitorAlertDecision(
    MonitorAlertKind Kind,
    string Reason
);

/// <summary>
/// Deterministic alert decision logic driven by a persisted <see cref="MonitorAlertState"/>.
/// </summary>
public static class MonitorAlertingDecider
{
    public static MonitorAlertDecision OnFailure(
        MonitorAlertState state,
        int threshold,
        DateTimeOffset nowUtc,
        TimeSpan repeatFailureAlertEvery)
    {
        threshold = Math.Clamp(threshold, 1, 20);
        if (repeatFailureAlertEvery < TimeSpan.FromHours(1))
            repeatFailureAlertEvery = TimeSpan.FromHours(1);

        // New incident.
        if (state.ConsecutiveFailures <= 0)
        {
            state.ConsecutiveFailures = 0;
            state.FailureIncidentOpenedAt = nowUtc;
            state.LastFailureAlertSentAt = null;
            state.LastFailureAlertError = null;

            // We are not recovered anymore.
            state.PendingRecoveryAlert = false;
            state.LastRecoveryAlertError = null;
        }

        state.ConsecutiveFailures++;

        if (state.ConsecutiveFailures == threshold)
            return new MonitorAlertDecision(MonitorAlertKind.Failure, $"Reached threshold {threshold}.");

        if (state.ConsecutiveFailures > threshold &&
            state.LastFailureAlertSentAt is not null &&
            nowUtc - state.LastFailureAlertSentAt.Value >= repeatFailureAlertEvery)
        {
            return new MonitorAlertDecision(MonitorAlertKind.FailureReminder, $"Reminder due (last={state.LastFailureAlertSentAt:O}).");
        }

        return new MonitorAlertDecision(MonitorAlertKind.None, $"Not alerted (failures={state.ConsecutiveFailures}, threshold={threshold}).");
    }

    public static MonitorAlertDecision OnSuccess(
        MonitorAlertState state,
        int threshold,
        DateTimeOffset nowUtc)
    {
        threshold = Math.Clamp(threshold, 1, 20);

        var wasFailing = state.ConsecutiveFailures > 0;
        var wasAlertedIncident = state.ConsecutiveFailures >= threshold && state.LastFailureAlertSentAt is not null;

        // Transition to success always closes the incident.
        state.ConsecutiveFailures = 0;
        state.FailureIncidentOpenedAt = null;
        state.LastFailureAlertSentAt = null;
        state.LastFailureAlertError = null;

        if (wasFailing && wasAlertedIncident)
        {
            state.PendingRecoveryAlert = true;
            return new MonitorAlertDecision(MonitorAlertKind.Recovered, "Recovered from an alerted incident.");
        }

        // If a recovery is pending (previous send failure), keep retrying on future successes.
        if (state.PendingRecoveryAlert)
            return new MonitorAlertDecision(MonitorAlertKind.Recovered, "Recovery email pending.");

        return new MonitorAlertDecision(MonitorAlertKind.None, "No recovery alert needed.");
    }
}

