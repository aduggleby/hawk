// <file>
// <summary>
// Unit tests for monitor alert state machine decisions (reminders + recovery).
// </summary>
// </file>

using Hawk.Web.Data.Alerting;
using Hawk.Web.Services.Monitoring;

namespace Hawk.Tests;

public sealed class MonitorAlertingDeciderTests
{
    [Fact]
    public void OnFailure_threshold2_second_failure_triggers_initial_failure_alert()
    {
        var state = new MonitorAlertState { MonitorId = Guid.NewGuid() };
        var now = new DateTimeOffset(2026, 02, 12, 12, 00, 00, TimeSpan.Zero);

        var d1 = MonitorAlertingDecider.OnFailure(state, threshold: 2, nowUtc: now, repeatFailureAlertEvery: TimeSpan.FromHours(24));
        Assert.Equal(MonitorAlertKind.None, d1.Kind);
        Assert.Equal(1, state.ConsecutiveFailures);

        var d2 = MonitorAlertingDecider.OnFailure(state, threshold: 2, nowUtc: now.AddMinutes(5), repeatFailureAlertEvery: TimeSpan.FromHours(24));
        Assert.Equal(MonitorAlertKind.Failure, d2.Kind);
        Assert.Equal(2, state.ConsecutiveFailures);
    }

    [Fact]
    public void OnFailure_threshold3_alerts_at_boundary_then_waits_for_reminder_window()
    {
        var state = new MonitorAlertState { MonitorId = Guid.NewGuid() };
        var now = new DateTimeOffset(2026, 02, 12, 12, 00, 00, TimeSpan.Zero);
        var repeatEvery = TimeSpan.FromHours(24);

        var d1 = MonitorAlertingDecider.OnFailure(state, threshold: 3, nowUtc: now, repeatFailureAlertEvery: repeatEvery);
        var d2 = MonitorAlertingDecider.OnFailure(state, threshold: 3, nowUtc: now.AddMinutes(5), repeatFailureAlertEvery: repeatEvery);
        var d3 = MonitorAlertingDecider.OnFailure(state, threshold: 3, nowUtc: now.AddMinutes(10), repeatFailureAlertEvery: repeatEvery);

        Assert.Equal(MonitorAlertKind.None, d1.Kind);
        Assert.Equal(MonitorAlertKind.None, d2.Kind);
        Assert.Equal(MonitorAlertKind.Failure, d3.Kind);
        Assert.Equal(3, state.ConsecutiveFailures);

        state.LastFailureAlertSentAt = now.AddMinutes(10);

        var d4 = MonitorAlertingDecider.OnFailure(state, threshold: 3, nowUtc: now.AddHours(23), repeatFailureAlertEvery: repeatEvery);

        Assert.Equal(MonitorAlertKind.None, d4.Kind);
        Assert.Equal(4, state.ConsecutiveFailures);
    }

    [Fact]
    public void OnFailure_after_threshold_reminds_only_if_last_alert_is_old_enough()
    {
        var state = new MonitorAlertState
        {
            MonitorId = Guid.NewGuid(),
            ConsecutiveFailures = 2,
            FailureIncidentOpenedAt = new DateTimeOffset(2026, 02, 11, 10, 00, 00, TimeSpan.Zero),
            LastFailureAlertSentAt = new DateTimeOffset(2026, 02, 11, 10, 05, 00, TimeSpan.Zero),
        };

        var now = new DateTimeOffset(2026, 02, 12, 09, 59, 00, TimeSpan.Zero);
        var d1 = MonitorAlertingDecider.OnFailure(state, threshold: 2, nowUtc: now, repeatFailureAlertEvery: TimeSpan.FromHours(24));
        Assert.Equal(MonitorAlertKind.None, d1.Kind);

        var d2 = MonitorAlertingDecider.OnFailure(state, threshold: 2, nowUtc: now.AddMinutes(10), repeatFailureAlertEvery: TimeSpan.FromHours(24));
        Assert.Equal(MonitorAlertKind.FailureReminder, d2.Kind);
    }

    [Fact]
    public void OnSuccess_from_alerted_incident_requests_recovery_and_resets_failure_streak()
    {
        var state = new MonitorAlertState
        {
            MonitorId = Guid.NewGuid(),
            ConsecutiveFailures = 3,
            FailureIncidentOpenedAt = new DateTimeOffset(2026, 02, 12, 11, 00, 00, TimeSpan.Zero),
            LastFailureAlertSentAt = new DateTimeOffset(2026, 02, 12, 11, 01, 00, TimeSpan.Zero),
            PendingRecoveryAlert = false,
        };

        var now = new DateTimeOffset(2026, 02, 12, 12, 00, 00, TimeSpan.Zero);
        var d = MonitorAlertingDecider.OnSuccess(state, threshold: 2, nowUtc: now);

        Assert.Equal(MonitorAlertKind.Recovered, d.Kind);
        Assert.True(state.PendingRecoveryAlert);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Null(state.FailureIncidentOpenedAt);
        Assert.Null(state.LastFailureAlertSentAt);
    }
}
