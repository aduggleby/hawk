// <file>
// <summary>
// Monitor interval definitions.
// Only fixed intervals are allowed by requirement; a special 5s interval is enabled only in Testing environment.
// </summary>
// </file>

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Fixed monitor intervals.
/// </summary>
public static class MonitorIntervals
{
    /// <summary>
    /// Default fixed intervals (seconds).
    /// </summary>
    public static readonly int[] DefaultSeconds = [60, 300, 900, 3600];

    /// <summary>
    /// Testing-only interval (seconds).
    /// </summary>
    public const int TestingFiveSeconds = 5;

    /// <summary>
    /// Computes allowed intervals for the current environment.
    /// </summary>
    public static IReadOnlyList<int> AllowedSeconds(IHostEnvironment env)
    {
        if (env.IsEnvironment("Testing"))
            return [TestingFiveSeconds, .. DefaultSeconds];
        return DefaultSeconds;
    }

    /// <summary>
    /// Heartbeat periods (seconds): 1 min, 5 min, 15 min, 1 h, 6 h, 12 h, 1 day, 7 days.
    /// </summary>
    public static readonly int[] HeartbeatPeriodSeconds = [60, 300, 900, 3600, 21600, 43200, 86400, 604800];

    /// <summary>
    /// Heartbeat grace times (seconds): 1 min, 5 min, 15 min, 1 h, 6 h, 1 day.
    /// </summary>
    public static readonly int[] HeartbeatGraceSeconds = [60, 300, 900, 3600, 21600, 86400];

    /// <summary>
    /// Allowed heartbeat periods for the environment (adds 5s in Testing).
    /// </summary>
    public static IReadOnlyList<int> AllowedHeartbeatPeriods(IHostEnvironment env) =>
        env.IsEnvironment("Testing") ? [TestingFiveSeconds, .. HeartbeatPeriodSeconds] : HeartbeatPeriodSeconds;

    /// <summary>
    /// Allowed heartbeat grace times for the environment (adds 0s and 5s in Testing).
    /// </summary>
    public static IReadOnlyList<int> AllowedHeartbeatGraces(IHostEnvironment env) =>
        env.IsEnvironment("Testing") ? [0, TestingFiveSeconds, .. HeartbeatGraceSeconds] : HeartbeatGraceSeconds;

    /// <summary>
    /// Formats a duration for display ("5s", "5 min", "1 h", "7 days").
    /// </summary>
    public static string FormatDuration(int seconds) => seconds switch
    {
        < 60 => $"{seconds}s",
        < 3600 => $"{seconds / 60} min",
        < 86400 => $"{seconds / 3600} h",
        _ => seconds / 86400 == 1 ? "1 day" : $"{seconds / 86400} days",
    };
}

