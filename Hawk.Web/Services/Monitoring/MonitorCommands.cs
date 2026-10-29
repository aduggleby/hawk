// <file>
// <summary>
// User-level changes to monitors that are shared by the pages (and other callers): pause, resume, run now, alert settings.
// Pause, resume and alert updates are set-based and renew the concurrency stamp so an open Edit form notices them.
// </summary>
// </file>

using Hangfire;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

/// <summary>Result of pausing or resuming one monitor.</summary>
public enum PauseResumeOutcome { Changed, Unchanged, Disabled, NotFound }

/// <summary>Result of queueing a manual run.</summary>
public enum ManualRunOutcome { Queued, AlreadyInFlight, NotFound, Heartbeat, Disabled }

/// <summary>Outcome of <see cref="MonitorCommands.QueueManualRunAsync"/>; <see cref="RunId"/> is set for Queued and AlreadyInFlight.</summary>
public sealed record ManualRunResult(ManualRunOutcome Outcome, Guid? RunId = null);

/// <summary>Result of <see cref="MonitorCommands.RenameGroupAsync"/>: the new spelling and member count, or a validation <see cref="Error"/> (nothing changed).</summary>
public sealed record GroupRenameResult(int Renamed, string? NewName = null, string? Error = null);

/// <summary>Result of <see cref="MonitorCommands.QueueGroupRunAsync"/>; skipped counts heartbeats and disabled monitors.</summary>
public sealed record GroupRunResult(int Queued, int AlreadyInFlight, int Skipped);

/// <summary>
/// Shared monitor commands.
/// </summary>
public sealed class MonitorCommands(ApplicationDbContext db, IBackgroundJobClient jobs, ILogger<MonitorCommands>? logger = null)
{
    /// <summary>Slack added to the monitor timeout before an unfinished manual run counts as lost.</summary>
    private static readonly TimeSpan InFlightSlack = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Pauses the enabled, unpaused monitors in <paramref name="monitors"/>. Returns how many changed.
    /// </summary>
    public async Task<int> PauseAsync(IQueryable<MonitorEntity> monitors, CancellationToken cancellationToken)
    {
        var stamp = Guid.NewGuid();
        return await monitors
            .Where(m => m.Enabled && !m.IsPaused)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsPaused, true)
                .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);
    }

    /// <summary>
    /// Resumes paused, enabled monitors. Heartbeats get a fresh deadline (a full period plus grace) in the same
    /// statement that unpauses them, so the scheduler can never see a resumed heartbeat with a stale past deadline.
    /// Heartbeats are updated first, grouped by (period, grace) with a precomputed UTC deadline; the generic update afterwards
    /// only touches HTTP monitors. Returns how many changed.
    /// </summary>
    public async Task<int> ResumeAsync(IQueryable<MonitorEntity> monitors, CancellationToken cancellationToken)
    {
        var stamp = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var paused = monitors.Where(m => m.Enabled && m.IsPaused);
        var updated = 0;

        var groups = await paused
            .Where(m => m.Type == MonitorType.Heartbeat)
            .Select(m => new { m.IntervalSeconds, m.HeartbeatGraceSeconds })
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var group in groups)
        {
            var period = group.IntervalSeconds;
            var grace = group.HeartbeatGraceSeconds;
            var deadline = HeartbeatTokens.InitialDeadline(now, period, grace);
            updated += await paused
                .Where(m => m.Type == MonitorType.Heartbeat && m.IntervalSeconds == period && m.HeartbeatGraceSeconds == grace)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.IsPaused, false)
                    .SetProperty(m => m.NextRunAt, deadline)
                    .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);
        }

        // HTTP monitors only: a heartbeat paused after the group query stays paused rather than resuming without a deadline.
        updated += await paused
            .Where(m => m.Type == MonitorType.Http)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsPaused, false)
                .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);
        return updated;
    }

    /// <summary>
    /// Renames group <paramref name="from"/> to <paramref name="to"/> for all its members in one set-based update that renews their
    /// stamps. The new name is validated (blank is an error) and takes the spelling of another existing group that matches ignoring
    /// case (a merge); a case-only rename of the same group stores the new spelling.
    /// </summary>
    public async Task<GroupRenameResult> RenameGroupAsync(string from, string to, CancellationToken cancellationToken)
    {
        var source = MonitorGroups.Normalize(from);
        if (source is null)
            return new GroupRenameResult(0, Error: "Group is required.");
        var target = MonitorGroups.Normalize(to);
        if (target is null)
            return new GroupRenameResult(0, Error: "New group name is required.");
        if (MonitorGroups.Validate(target) is { } error)
            return new GroupRenameResult(0, Error: error);

        target = await MonitorGroups.CanonicalizeAsync(db, target, excludeGroup: source, cancellationToken: cancellationToken) ?? target;
        var stamp = Guid.NewGuid();
        var renamed = await MonitorGroups.InGroup(db.Monitors, source)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.GroupName, target)
                .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);
        return new GroupRenameResult(renamed, target);
    }

    /// <summary>
    /// Queues a manual run for every HTTP monitor in <paramref name="group"/> (reusing a run already in flight). Heartbeats and
    /// disabled monitors are counted as skipped.
    /// </summary>
    public async Task<GroupRunResult> QueueGroupRunAsync(string group, CancellationToken cancellationToken)
    {
        var ids = await MonitorGroups.InGroup(db.Monitors, group).Select(m => m.Id).ToListAsync(cancellationToken);
        int queued = 0, inFlight = 0, skipped = 0;
        foreach (var id in ids)
        {
            var result = await QueueManualRunAsync(id, reuseInFlight: true, rejectDisabled: true, cancellationToken);
            switch (result.Outcome)
            {
                case ManualRunOutcome.Queued: queued++; break;
                case ManualRunOutcome.AlreadyInFlight: inFlight++; break;
                default: skipped++; break;
            }
        }

        return new GroupRunResult(queued, inFlight, skipped);
    }

    /// <summary>
    /// Pauses one monitor and reports whether it changed.
    /// </summary>
    public async Task<PauseResumeOutcome> PauseMonitorAsync(Guid id, CancellationToken cancellationToken) =>
        await PauseAsync(db.Monitors.Where(m => m.Id == id), cancellationToken) > 0
            ? PauseResumeOutcome.Changed
            : await ExplainNoChangeAsync(id, cancellationToken);

    /// <summary>
    /// Resumes one monitor and reports whether it changed.
    /// </summary>
    public async Task<PauseResumeOutcome> ResumeMonitorAsync(Guid id, CancellationToken cancellationToken) =>
        await ResumeAsync(db.Monitors.Where(m => m.Id == id), cancellationToken) > 0
            ? PauseResumeOutcome.Changed
            : await ExplainNoChangeAsync(id, cancellationToken);

    private async Task<PauseResumeOutcome> ExplainNoChangeAsync(Guid id, CancellationToken cancellationToken)
    {
        var enabled = await db.Monitors
            .Where(m => m.Id == id)
            .Select(m => (bool?)m.Enabled)
            .FirstOrDefaultAsync(cancellationToken);
        return enabled switch
        {
            null => PauseResumeOutcome.NotFound,
            false => PauseResumeOutcome.Disabled,
            _ => PauseResumeOutcome.Unchanged,
        };
    }

    /// <summary>
    /// Queues a manual run: creates a <c>queued</c> run up front and enqueues the Hangfire job (the stamp is left alone).
    /// With <paramref name="reuseInFlight"/>, a manual run that started within the monitor timeout plus 60 seconds and is still
    /// queued or running is returned instead of queueing another; an older unfinished run is treated as lost.
    /// With <paramref name="rejectDisabled"/>, a disabled monitor returns <see cref="ManualRunOutcome.Disabled"/> (the Details page queues them).
    /// </summary>
    public async Task<ManualRunResult> QueueManualRunAsync(
        Guid id, bool reuseInFlight, bool rejectDisabled, CancellationToken cancellationToken)
    {
        var monitor = await db.Monitors
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (monitor is null)
            return new ManualRunResult(ManualRunOutcome.NotFound);

        // Heartbeats have nothing to run: they wait for the client to call the ping URL.
        if (monitor.Type == MonitorType.Heartbeat)
            return new ManualRunResult(ManualRunOutcome.Heartbeat);
        if (rejectDisabled && !monitor.Enabled)
            return new ManualRunResult(ManualRunOutcome.Disabled);

        var now = DateTimeOffset.UtcNow;
        if (reuseInFlight)
        {
            var cutoff = now - TimeSpan.FromSeconds(monitor.TimeoutSeconds) - InFlightSlack;
            var inFlight = await db.MonitorRuns
                .Where(r => r.MonitorId == id && r.Reason == "manual" && (r.State == "queued" || r.State == "running") && r.StartedAt >= cutoff)
                .OrderByDescending(r => r.StartedAt)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (inFlight is { } existing)
                return new ManualRunResult(ManualRunOutcome.AlreadyInFlight, existing);
        }

        // Create a run record up front so callers can point at its diagnostics immediately.
        var run = new MonitorRun
        {
            Id = Guid.NewGuid(),
            MonitorId = id,
            State = "queued",
            StartedAt = now,
            FinishedAt = now,
            DurationMs = 0,
            StatusCode = null,
            Success = false,
            ErrorMessage = "Queued",
            Reason = "manual",
            RequestUrl = monitor.Url,
            RequestMethod = monitor.Method,
            RequestContentType = monitor.ContentType,
            RequestTimeoutMs = (int)Math.Clamp(monitor.TimeoutSeconds * 1000, 0, int.MaxValue),
            MatchResultsJson = "[]",
            RequestHeadersJson = "{}",
            ResponseHeadersJson = "{}",
        };

        db.MonitorRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            jobs.Enqueue<IMonitorRunner>(r => r.RunAsync(id, "manual", CancellationToken.None, run.Id));
        }
        catch
        {
            // No job will ever run this record; remove it so it is not mistaken for an in-flight run.
            try
            {
                db.MonitorRuns.Remove(run);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                logger?.LogError(cleanupError, "Could not remove queued run {RunId} after a failed enqueue", run.Id);
            }
            throw;
        }

        return new ManualRunResult(ManualRunOutcome.Queued, run.Id);
    }

    /// <summary>
    /// Changes the alert threshold and/or the alert email override in one statement and renews the concurrency stamp.
    /// A null <paramref name="threshold"/> or <paramref name="email"/> leaves that field alone; <paramref name="clearEmail"/> removes the email.
    /// The caller validates values with <see cref="AlertSettingsValidator"/>. Returns false when the monitor does not exist.
    /// </summary>
    public async Task<bool> UpdateAlertingAsync(
        Guid id, int? threshold, string? email, bool clearEmail, CancellationToken cancellationToken)
    {
        var stamp = Guid.NewGuid();
        var newEmail = clearEmail ? null : string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        var setEmail = clearEmail || newEmail is not null;

        var updated = await db.Monitors
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(m => m.ConcurrencyStamp, stamp);
                if (threshold is { } value)
                    s.SetProperty(m => m.AlertAfterConsecutiveFailures, value);
                if (setEmail)
                    s.SetProperty(m => m.AlertEmailOverride, newEmail);
            }, cancellationToken);
        return updated > 0;
    }
}
