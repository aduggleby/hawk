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
    /// <summary>
    /// A previously completed run, used to seed alert state.
    /// </summary>
    public sealed record HistoricRun(DateTimeOffset StartedAt, bool Success, bool AlertSent);

    /// <summary>
    /// Seeds a new alert state from completed history (newest first). Callers must exclude the run being
    /// evaluated and any non-completed (queued/running) runs, otherwise the in-flight run is double counted.
    /// </summary>
    public static MonitorAlertState SeedFromHistory(Guid monitorId, IReadOnlyList<HistoricRun> recentNewestFirst)
    {
        var failures = 0;
        DateTimeOffset? openedAt = null;
        foreach (var r in recentNewestFirst)
        {
            if (r.Success)
                break;
            failures++;
            openedAt = r.StartedAt;
        }

        DateTimeOffset? lastAlertSentAt = null;
        if (failures > 0)
        {
            lastAlertSentAt = recentNewestFirst
                .Take(failures)
                .Where(r => r.AlertSent)
                .Select(r => (DateTimeOffset?)r.StartedAt)
                .FirstOrDefault();
        }

        return new MonitorAlertState
        {
            MonitorId = monitorId,
            ConsecutiveFailures = failures,
            FailureIncidentOpenedAt = failures > 0 ? openedAt : null,
            LastFailureAlertSentAt = lastAlertSentAt,
            PendingRecoveryAlert = false,
        };
    }

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

        // At/over threshold with no failure alert delivered for this incident (first send failed, or the
        // threshold was lowered while failing): keep retrying the failure alert until a send succeeds.
        // After a successful send LastFailureAlertSentAt is set, so this never repeats; reminders take over.
        if (state.ConsecutiveFailures > threshold && state.LastFailureAlertSentAt is null)
            return new MonitorAlertDecision(MonitorAlertKind.Failure, $"No failure alert delivered yet (failures={state.ConsecutiveFailures}, threshold={threshold}).");

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
        var wasFailing = state.ConsecutiveFailures > 0;

        // LastFailureAlertSentAt is cleared on every success, so it alone says whether this incident was alerted
        // (comparing against the current threshold would suppress recovery after the threshold was raised).
        var wasAlertedIncident = state.LastFailureAlertSentAt is not null;

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

