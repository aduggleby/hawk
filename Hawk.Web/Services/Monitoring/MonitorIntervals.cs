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
}

