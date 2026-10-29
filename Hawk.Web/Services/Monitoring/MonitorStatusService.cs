// <file>
// <summary>
// Computes monitor state, health and the failure summary from existing data (monitor, last completed run, alert state).
// Shared by the monitors list page and any other reader that needs the same answer.
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.Monitoring;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

/// <summary>Configuration state: disabled wins over paused.</summary>
public enum MonitorState { Active, Paused, Disabled }

/// <summary>Outcome of the checks: see <see cref="MonitorStatusService.ComputeHealth"/> for precedence.</summary>
public enum MonitorHealth { Ok, Failing, Overdue, Unknown }

/// <summary>The most recent completed run of a monitor.</summary>
public sealed record LastCompletedRun(DateTimeOffset StartedAt, bool Success, int? StatusCode, string? ErrorMessage);

/// <summary>
/// Why a monitor is failing: last completed run, consecutive failures and when the current incident started.
/// </summary>
public sealed record FailureSummary(
    DateTimeOffset LastRunAt,
    int? StatusCode,
    string? Error,
    int ConsecutiveFailures,
    DateTimeOffset? FailingSince);

/// <summary>State, health and (when the last completed run failed) the failure summary of one monitor.</summary>
public sealed record MonitorStatus(Guid MonitorId, MonitorState State, MonitorHealth Health, FailureSummary? Failure);

/// <summary>
/// Status computation for monitors.
/// </summary>
public sealed class MonitorStatusService(ApplicationDbContext db, IConfiguration config, IHostEnvironment env)
{
    /// <summary>
    /// Last completed run per monitor (legacy runs with an empty state count as completed), optionally limited to some monitors.
    /// </summary>
    public async Task<Dictionary<Guid, LastCompletedRun>> GetLastCompletedRunsAsync(
        IReadOnlyCollection<Guid>? monitorIds, CancellationToken cancellationToken)
    {
        var runs = db.MonitorRuns
            .AsNoTracking()
            .WhereCompleted();
        if (monitorIds is not null)
            runs = runs.Where(r => monitorIds.Contains(r.MonitorId));

        return await runs
            .GroupBy(r => r.MonitorId)
            .Select(g => g
                .OrderByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.Id)
                .Select(r => new { r.MonitorId, r.StartedAt, r.Success, r.StatusCode, r.ErrorMessage })
                .First())
            .ToDictionaryAsync(
                x => x.MonitorId,
                x => new LastCompletedRun(x.StartedAt, x.Success, x.StatusCode, x.ErrorMessage),
                cancellationToken);
    }

    /// <summary>
    /// The monitors list's "Failing" rule: the monitor has run and its last completed run failed.
    /// </summary>
    public static bool IsInFailingGroup(MonitorEntity monitor, IReadOnlyDictionary<Guid, LastCompletedRun> lastRuns) =>
        monitor.LastRunAt is not null &&
        lastRuns.TryGetValue(monitor.Id, out var last) &&
        !last.Success;

    /// <summary>
    /// Computes status for the given monitors with two queries (last completed runs, alert states), however many monitors there are.
    /// </summary>
    public async Task<Dictionary<Guid, MonitorStatus>> GetStatusesAsync(
        IReadOnlyCollection<MonitorEntity> monitors, CancellationToken cancellationToken)
    {
        var ids = monitors.Select(m => m.Id).ToArray();
        var lastRuns = await GetLastCompletedRunsAsync(ids, cancellationToken);
        var alertStates = await db.MonitorAlertStates
            .AsNoTracking()
            .Where(s => ids.Contains(s.MonitorId))
            .ToDictionaryAsync(s => s.MonitorId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var tickSeconds = MonitorScheduler.ResolveTickSeconds(config, env);
        return monitors.ToDictionary(
            m => m.Id,
            m =>
            {
                lastRuns.TryGetValue(m.Id, out var last);
                alertStates.TryGetValue(m.Id, out var alertState);
                return Evaluate(m, last, alertState, now, tickSeconds);
            });
    }

    /// <summary>
    /// Health of one monitor as of <paramref name="now"/> (resolves the scheduler tick from configuration).
    /// </summary>
    public MonitorHealth Health(MonitorEntity monitor, LastCompletedRun? last, DateTimeOffset now) =>
        ComputeHealth(monitor, last, now, MonitorScheduler.ResolveTickSeconds(config, env));

    /// <summary>
    /// Pure status computation.
    /// </summary>
    public static MonitorStatus Evaluate(
        MonitorEntity monitor, LastCompletedRun? last, MonitorAlertState? alertState, DateTimeOffset now, int tickSeconds)
    {
        var health = ComputeHealth(monitor, last, now, tickSeconds);

        FailureSummary? failure = null;
        if (last is { Success: false })
        {
            failure = new FailureSummary(
                last.StartedAt,
                last.StatusCode,
                last.ErrorMessage,
                alertState?.ConsecutiveFailures ?? 0,
                alertState?.FailureIncidentOpenedAt);
        }

        return new MonitorStatus(monitor.Id, ComputeState(monitor), health, failure);
    }

    /// <summary>
    /// <c>disabled</c> when not enabled, otherwise <c>paused</c> when paused, otherwise <c>active</c>.
    /// </summary>
    public static MonitorState ComputeState(MonitorEntity monitor) =>
        !monitor.Enabled ? MonitorState.Disabled : monitor.IsPaused ? MonitorState.Paused : MonitorState.Active;

    /// <summary>
    /// First rule that applies wins:
    /// <list type="number">
    /// <item><description><c>Overdue</c>, only for an enabled, unpaused monitor: a heartbeat whose deadline passed, or an HTTP monitor
    /// whose last run is older than twice its interval plus its timeout plus one scheduler tick (checks stopped running).</description></item>
    /// <item><description><c>Failing</c>: the last completed run failed.</description></item>
    /// <item><description><c>Ok</c>: the last completed run succeeded.</description></item>
    /// <item><description><c>Unknown</c>: no completed run.</description></item>
    /// </list>
    /// </summary>
    public static MonitorHealth ComputeHealth(MonitorEntity monitor, LastCompletedRun? last, DateTimeOffset now, int tickSeconds)
    {
        if (monitor is { Enabled: true, IsPaused: false })
        {
            var overdue = monitor.Type == MonitorType.Heartbeat
                ? monitor.NextRunAt is { } deadline && deadline < now
                : monitor.LastRunAt is { } lastRunAt &&
                  now - lastRunAt > TimeSpan.FromSeconds(2L * monitor.IntervalSeconds + monitor.TimeoutSeconds + tickSeconds);
            if (overdue)
                return MonitorHealth.Overdue;
        }

        return last is null ? MonitorHealth.Unknown : last.Success ? MonitorHealth.Ok : MonitorHealth.Failing;
    }
}
