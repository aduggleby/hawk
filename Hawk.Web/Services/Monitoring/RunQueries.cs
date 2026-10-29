// <file>
// <summary>
// Shared query predicates over MonitorRuns, so every reader agrees on what a "completed" run is.
// </summary>
// </file>

using Hawk.Web.Data.Monitoring;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Query helpers for <see cref="MonitorRun"/>.
/// </summary>
public static class RunQueries
{
    /// <summary>
    /// Completed runs. Runs saved before the <c>State</c> column existed have an empty state and count as completed.
    /// </summary>
    public static IQueryable<MonitorRun> WhereCompleted(this IQueryable<MonitorRun> runs) =>
        runs.Where(r => r.State == "completed" || r.State == "");
}
