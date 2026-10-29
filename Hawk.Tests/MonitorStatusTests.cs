// <file>
// <summary>
// Unit tests for monitor state/health computation, the failure summary and the shared alert settings validator.
// </summary>
// </file>

using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services.Monitoring;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="MonitorStatusService"/> and <see cref="AlertSettingsValidator"/>.
/// </summary>
public class MonitorStatusTests
{
    private const int Tick = 30;
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static MonitorEntity Http(DateTimeOffset? lastRunAt = null, int interval = 60, int timeout = 15) => new()
    {
        Name = "m",
        Url = "https://example.com",
        Method = "GET",
        IntervalSeconds = interval,
        TimeoutSeconds = timeout,
        LastRunAt = lastRunAt,
    };

    private static MonitorEntity Heartbeat(DateTimeOffset? deadline) => new()
    {
        Name = "hb",
        Type = MonitorType.Heartbeat,
        Url = "",
        Method = "GET",
        IntervalSeconds = 3600,
        HeartbeatGraceSeconds = 300,
        NextRunAt = deadline,
    };

    private static LastCompletedRun Run(bool success, int? status = null, string? error = null) =>
        new(Now.AddMinutes(-1), success, status, error);

    [Fact]
    public void Health_is_unknown_without_a_completed_run_and_follows_the_last_completed_run()
    {
        // A monitor with only a queued run has no completed run, so the status service passes null.
        Assert.Equal(MonitorHealth.Unknown, MonitorStatusService.ComputeHealth(Http(), null, Now, Tick));
        Assert.Equal(MonitorHealth.Failing, MonitorStatusService.ComputeHealth(Http(Now.AddSeconds(-30)), Run(false), Now, Tick));
        Assert.Equal(MonitorHealth.Ok, MonitorStatusService.ComputeHealth(Http(Now.AddSeconds(-30)), Run(true), Now, Tick));
    }

    [Fact]
    public void An_unrecorded_heartbeat_miss_is_overdue_and_wins_over_a_failed_last_run()
    {
        Assert.Equal(MonitorHealth.Overdue, MonitorStatusService.ComputeHealth(Heartbeat(Now.AddSeconds(-1)), Run(true), Now, Tick));
        Assert.Equal(MonitorHealth.Overdue, MonitorStatusService.ComputeHealth(Heartbeat(Now.AddSeconds(-1)), Run(false), Now, Tick));
        Assert.Equal(MonitorHealth.Ok, MonitorStatusService.ComputeHealth(Heartbeat(Now.AddSeconds(1)), Run(true), Now, Tick));
    }

    [Fact]
    public void An_http_monitor_whose_checks_stopped_is_overdue_only_while_active()
    {
        // Threshold: 2 * 60 + 15 + 30 = 165 seconds since LastRunAt.
        Assert.Equal(MonitorHealth.Ok, MonitorStatusService.ComputeHealth(Http(Now.AddSeconds(-165)), Run(true), Now, Tick));
        var stale = Http(Now.AddSeconds(-166));
        Assert.Equal(MonitorHealth.Overdue, MonitorStatusService.ComputeHealth(stale, Run(true), Now, Tick));

        stale.IsPaused = true;
        Assert.Equal(MonitorHealth.Ok, MonitorStatusService.ComputeHealth(stale, Run(true), Now, Tick));
        stale.IsPaused = false;
        stale.Enabled = false;
        Assert.Equal(MonitorHealth.Ok, MonitorStatusService.ComputeHealth(stale, Run(true), Now, Tick));

        // Never ran yet: not overdue, just unknown.
        Assert.Equal(MonitorHealth.Unknown, MonitorStatusService.ComputeHealth(Http(), null, Now, Tick));
    }

    [Fact]
    public void State_is_disabled_before_paused_before_active()
    {
        var m = Http();
        Assert.Equal(MonitorState.Active, MonitorStatusService.ComputeState(m));
        m.IsPaused = true;
        Assert.Equal(MonitorState.Paused, MonitorStatusService.ComputeState(m));
        m.Enabled = false;
        Assert.Equal(MonitorState.Disabled, MonitorStatusService.ComputeState(m));
    }

    [Fact]
    public void A_paused_monitor_whose_last_run_failed_is_paused_and_failing_with_a_failure_summary()
    {
        var m = Http(Now.AddSeconds(-30));
        m.IsPaused = true;
        var opened = Now.AddMinutes(-10);
        var alertState = new MonitorAlertState { MonitorId = m.Id, ConsecutiveFailures = 4, FailureIncidentOpenedAt = opened };

        var status = MonitorStatusService.Evaluate(m, Run(false, 503, "Unexpected status"), alertState, Now, Tick);

        Assert.Equal(MonitorState.Paused, status.State);
        Assert.Equal(MonitorHealth.Failing, status.Health);
        Assert.Equal(new FailureSummary(Now.AddMinutes(-1), 503, "Unexpected status", 4, opened), status.Failure);
    }

    [Fact]
    public void Failure_summary_is_absent_when_the_last_run_succeeded_and_zero_without_alert_state()
    {
        var ok = MonitorStatusService.Evaluate(Http(Now.AddSeconds(-30)), Run(true), null, Now, Tick);
        Assert.Null(ok.Failure);

        var failing = MonitorStatusService.Evaluate(Http(Now.AddSeconds(-30)), Run(false, null, "timeout"), null, Now, Tick);
        Assert.Equal(new FailureSummary(Now.AddMinutes(-1), null, "timeout", 0, null), failing.Failure);
    }

    [Fact]
    public void Failing_group_needs_a_last_run_time_and_a_failed_completed_run()
    {
        var failing = Http(Now);
        var neverRan = Http();
        var ok = Http(Now);
        var lastRuns = new Dictionary<Guid, LastCompletedRun>
        {
            [failing.Id] = Run(false),
            [neverRan.Id] = Run(false),
            [ok.Id] = Run(true),
        };

        Assert.True(MonitorStatusService.IsInFailingGroup(failing, lastRuns));
        Assert.False(MonitorStatusService.IsInFailingGroup(neverRan, lastRuns));
        Assert.False(MonitorStatusService.IsInFailingGroup(ok, lastRuns));
        Assert.False(MonitorStatusService.IsInFailingGroup(Http(Now), lastRuns));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(0, false)]
    [InlineData(21, false)]
    public void Threshold_is_limited_to_1_through_20(int threshold, bool valid)
    {
        Assert.Equal(valid, AlertSettingsValidator.ValidateThreshold(threshold) is null);

        var form = new MonitorForm { Name = "x", Url = "https://example.com", Method = "GET", IntervalSeconds = 60, AlertAfterConsecutiveFailures = threshold };
        Assert.Equal(valid, !form.Validate(new FakeEnv()).Any(e => e.MemberNames.Contains(nameof(MonitorForm.AlertAfterConsecutiveFailures))));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("ops@example.com", true)]
    [InlineData("not-an-email", false)]
    [InlineData("a\0@example.com", false)]
    public void Email_must_be_valid_or_blank(string? email, bool valid)
    {
        Assert.Equal(valid, AlertSettingsValidator.ValidateEmail(email) is null);

        var form = new MonitorForm { Name = "x", Url = "https://example.com", Method = "GET", IntervalSeconds = 60, AlertEmailOverride = email };
        Assert.Equal(valid, !form.Validate(new FakeEnv()).Any(e => e.MemberNames.Contains(nameof(MonitorForm.AlertEmailOverride))));
    }

    [Fact]
    public void Email_longer_than_320_characters_is_rejected_by_the_validator()
    {
        var tooLong = $"{new string('a', 64)}@{new string('b', 260)}.com";
        Assert.True(tooLong.Length > 320);
        Assert.NotNull(AlertSettingsValidator.ValidateEmail(tooLong));
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
