// <file>
// <summary>
// Shared monitor execution implementation used by both Hangfire jobs and interactive UI testing.
// Produces both the raw <see cref="UrlCheckResult"/> (for diagnostics) and a persisted <see cref="MonitorRun"/>.
// </summary>
// </file>

using System.Net;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Data.UrlChecks;
using Hawk.Web.Services.Email;
using Hawk.Web.Services.UrlChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

public sealed record MonitorExecutionResult(
    MonitorEntity Monitor,
    UrlCheckRequest Request,
    UrlCheckResult Result,
    MonitorRun Run
);

/// <summary>
/// Facts about an incoming heartbeat ping that are kept in the run diagnostics (never the secret token).
/// </summary>
public sealed record PingRequestInfo(string Method, string? UserAgent, string? ContentType, string? ClientIp);

/// <summary>
/// Outcome of <see cref="IMonitorExecutor.RecordPingAsync"/>.
/// </summary>
public enum PingResult
{
    /// <summary>The ping was recorded.</summary>
    Recorded = 0,

    /// <summary>The monitor is paused or disabled; nothing was written.</summary>
    Paused = 1,

    /// <summary>No heartbeat monitor has this token (any more).</summary>
    NotFound = 2,

    /// <summary>The alert lock was busy or the monitor kept changing; nothing was written and the client should retry (503).</summary>
    Busy = 3,
}

public interface IMonitorExecutor
{
    /// <summary>
    /// Runs a monitor. For heartbeats this is the missed-ping check (reason <c>schedule</c>) and always returns null.
    /// </summary>
    Task<MonitorExecutionResult?> ExecuteAsync(Guid monitorId, string? reason, CancellationToken cancellationToken, Guid? runId = null);

    /// <summary>
    /// Records a heartbeat ping for an already normalized token (see <see cref="HeartbeatTokens.TryNormalize"/>).
    /// Handled inline: takes the per-monitor alert lock, reloads, then writes the run, the new deadline and any recovery alert in one transaction.
    /// </summary>
    Task<PingResult> RecordPingAsync(string token, PingRequestInfo info, CancellationToken cancellationToken);
}

public sealed class MonitorExecutor(
    ApplicationDbContext db,
    IUrlChecker urlChecker,
    IEmailSender emailSender,
    JobStorage jobStorage,
    AlertRecipientResolver recipientResolver,
    ILogger<MonitorExecutor> logger,
    IConfiguration config) : IMonitorExecutor
{
    private const int AlertLockTimeoutSeconds = 60;
    private const int HeartbeatMissLockTimeoutSeconds = 10;
    private const int PingLockTimeoutSeconds = 5;
    private const string AlertLockBusyMessage = "Alert state lock busy; skipped alert evaluation to avoid duplicate alert.";
    private const int FailureLogEntryLimit = 20;

    private sealed record FailureLogEntry(DateTimeOffset StartedAt, int? StatusCode);

    public async Task<MonitorExecutionResult?> ExecuteAsync(Guid monitorId, string? reason, CancellationToken cancellationToken, Guid? runId = null)
    {
        // Branch: monitor not found or deleted.
        var monitor = await db.Monitors
            .Include(x => x.Headers)
            .Include(x => x.MatchRules)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == monitorId, cancellationToken);
        if (monitor is null)
        {
            logger.LogWarning("Monitor {MonitorId} not found (reason={Reason})", monitorId, reason);
            return null;
        }

        // Branch: heartbeats have no request to run; "schedule" jobs are missed-ping checks.
        if (monitor.Type == MonitorType.Heartbeat)
        {
            if (!string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase))
                await CheckHeartbeatMissAsync(monitorId, reason, cancellationToken);
            return null;
        }

        if ((!monitor.Enabled || monitor.IsPaused) && !string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase))
        {
            // Branch: disabled/paused monitors should not run on schedule.
            return null;
        }

        if (!Uri.TryCreate(monitor.Url, UriKind.Absolute, out var uri))
        {
            var invalidReq = new UrlCheckRequest(uri ?? new Uri("http://invalid.local/"), HttpMethod.Get, new Dictionary<string, string>(), null, null, TimeSpan.Zero, []);
            var invalidRun = await PersistInvalidConfigAsync(monitor, "Invalid URL", reason, invalidReq, cancellationToken, runId);
            await PruneRunHistoryAsync(monitor, cancellationToken);
            var invalidRes = new UrlCheckResult(false, null, TimeSpan.Zero, "Invalid URL", [], null, new Dictionary<string, string>(), null, null);
            return new MonitorExecutionResult(monitor, invalidReq, invalidRes, invalidRun);
        }

        var headers = monitor.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .GroupBy(h => h.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        // Apply account-wide User-Agent override only if the monitor didn't set it explicitly.
        if (!headers.ContainsKey("User-Agent") && !string.IsNullOrWhiteSpace(monitor.CreatedByUserId))
        {
            var uaRaw = await db.UserUrlCheckSettings
                .Where(x => x.UserId == monitor.CreatedByUserId)
                .Select(x => x.UserAgent)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(uaRaw))
                headers["User-Agent"] = UserAgentResolver.Resolve(config, uaRaw);
        }

        var matchRules = monitor.MatchRules
            .Where(r => r.Mode != ContentMatchMode.None && !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new ContentMatchRule(r.Mode, r.Pattern))
            .ToArray();

        var req = new UrlCheckRequest(
            Url: uri,
            Method: new HttpMethod(monitor.Method),
            Headers: headers,
            ContentType: monitor.ContentType,
            Body: monitor.Body,
            Timeout: TimeSpan.FromSeconds(Math.Clamp(monitor.TimeoutSeconds, 1, 300)),
            MatchRules: matchRules,
            FollowRedirects: monitor.FollowRedirects
        );

        var started = DateTimeOffset.UtcNow;

        // If the UI created a run record up front, fill it in; otherwise create a new one.
        var existingRun = runId is null
            ? null
            : await db.MonitorRuns.FirstOrDefaultAsync(r => r.Id == runId.Value && r.MonitorId == monitor.Id, cancellationToken);

        MonitorRun run;
        if (existingRun is not null)
        {
            run = existingRun;
            run.State = "running";
            run.StartedAt = started;
            run.FinishedAt = started;
            run.Reason = NormalizeReason(reason);
            run.RequestUrl = req.Url.ToString();
            run.RequestMethod = req.Method.Method;
            run.RequestContentType = req.ContentType;
            run.RequestTimeoutMs = (int)Math.Clamp(req.Timeout.TotalMilliseconds, 0, int.MaxValue);
            run.RequestHeadersJson = SerializeHeaders(req.Headers);
            run.RequestBodySnippet = TrimOrNull(req.Body, 4000);

            // Persist early so the run diagnostics page can show RUNNING quickly.
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // Scheduled runs don't need an early write; we save once at the end.
            run = new MonitorRun { MonitorId = monitor.Id };
            run.State = "running";
            run.StartedAt = started;
            run.FinishedAt = started;
            run.Reason = NormalizeReason(reason);
            run.RequestUrl = req.Url.ToString();
            run.RequestMethod = req.Method.Method;
            run.RequestContentType = req.ContentType;
            run.RequestTimeoutMs = (int)Math.Clamp(req.Timeout.TotalMilliseconds, 0, int.MaxValue);
            run.RequestHeadersJson = SerializeHeaders(req.Headers);
            run.RequestBodySnippet = TrimOrNull(req.Body, 4000);
        }

        var res = await urlChecker.CheckAsync(req, cancellationToken);
        var finished = started.Add(res.Duration);
        var statusIsSuccess = AllowedStatusCodesParser.IsSuccessStatusCode(res.StatusCode, monitor.AllowedStatusCodes);
        var matchesPassed = res.MatchResults.All(m => m.Matched);
        var success = statusIsSuccess && matchesPassed;

        run.State = "completed";
        run.FinishedAt = finished;
        run.DurationMs = (int)Math.Clamp(res.Duration.TotalMilliseconds, 0, int.MaxValue);
        run.StatusCode = res.StatusCode is null ? null : (int)res.StatusCode.Value;
        run.Success = success;
        run.ErrorMessage = success ? null : BuildFailureMessage(res.StatusCode, statusIsSuccess, res.MatchResults);
        // PostgreSQL text columns reject NUL characters (binary bodies); SQL Server accepts them.
        run.ResponseSnippet = res.ResponseBodySnippet?.Replace('\0', '\uFFFD');
        run.ResponseHeadersJson = SerializeHeaders(res.ResponseHeaders);
        run.ResponseContentType = res.ResponseContentType;
        run.ResponseContentLength = res.ResponseContentLength;
        run.MatchResultsJson = JsonSerializer.Serialize(res.MatchResults.Select(m => new
        {
            Mode = m.Rule.Mode.ToString(),
            m.Rule.Pattern,
            m.Matched,
            m.Details
        }));

        var saved = false;
        try
        {
            using var lockConnection = jobStorage.GetConnection();
            using var alertLock = lockConnection.AcquireDistributedLock(
                $"hawk:monitor-alert:{monitor.Id}",
                TimeSpan.FromSeconds(AlertLockTimeoutSeconds));

            await EvaluateAlertsAsync(monitor, run, cancellationToken);

            await SetLastRunAtAsync(monitor, started, cancellationToken);
            if (existingRun is null)
                db.MonitorRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            saved = true;
        }
        catch (DistributedLockTimeoutException ex)
        {
            logger.LogWarning(ex, "Skipped alert evaluation for monitor {MonitorId} because alert state lock was busy.", monitor.Id);
            run.AlertSent = false;
            run.AlertError = AlertLockBusyMessage;
        }

        if (!saved)
        {
            await SetLastRunAtAsync(monitor, started, cancellationToken);
            if (existingRun is null)
                db.MonitorRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
        }

        await PruneRunHistoryAsync(monitor, cancellationToken);

        return new MonitorExecutionResult(monitor, req, res, run);
    }

    /// <summary>
    /// Decides and sends the alert for <paramref name="run"/> and updates the (tracked) alert state.
    /// The caller must hold the <c>hawk:monitor-alert:{id}</c> lock and saves afterwards. Shared by HTTP checks,
    /// heartbeat misses and heartbeat pings.
    /// </summary>
    private async Task EvaluateAlertsAsync(MonitorEntity monitor, MonitorRun run, CancellationToken cancellationToken)
    {
        var threshold = Math.Clamp(monitor.AlertAfterConsecutiveFailures, 1, 20);
        var repeatHours = config.GetValue("Hawk:Alerting:RepeatFailureAlertEveryHours", 24);
        repeatHours = Math.Clamp(repeatHours, 1, 24 * 30);
        var repeatEvery = TimeSpan.FromHours(repeatHours);

        var state = await GetOrCreateAlertStateAsync(monitor.Id, run.Id, cancellationToken);

        // Capture prior state for composing emails (duration, etc.).
        var incidentOpenedAt = state.FailureIncidentOpenedAt;
        var lastFailureAlertSentAt = state.LastFailureAlertSentAt;

        MonitorAlertDecision decision;
        if (!run.Success)
        {
            decision = MonitorAlertingDecider.OnFailure(state, threshold, run.StartedAt, repeatEvery);

            if (decision.Kind is MonitorAlertKind.Failure or MonitorAlertKind.FailureReminder)
            {
                var failureLog = await GetFailureLogEntriesAsync(
                    monitor.Id,
                    run,
                    state.FailureIncidentOpenedAt,
                    state.ConsecutiveFailures,
                    cancellationToken);

                var sent = await TrySendFailureAlertAsync(
                    monitor,
                    run,
                    decision.Kind,
                    state.ConsecutiveFailures,
                    threshold,
                    state.FailureIncidentOpenedAt,
                    lastFailureAlertSentAt,
                    failureLog,
                    cancellationToken);

                if (sent)
                {
                    state.LastFailureAlertSentAt = run.StartedAt;
                    state.LastFailureAlertError = null;
                }
                else
                {
                    state.LastFailureAlertError = run.AlertError;
                }
            }
            else
            {
                run.AlertSent = false;
                run.AlertError = decision.Reason;
            }
        }
        else
        {
            decision = MonitorAlertingDecider.OnSuccess(state, threshold, run.StartedAt);

            if (decision.Kind == MonitorAlertKind.Recovered)
            {
                var sent = await TrySendRecoveredAlertAsync(
                    monitor,
                    run,
                    incidentOpenedAt,
                    lastFailureAlertSentAt,
                    cancellationToken);

                if (sent)
                {
                    state.PendingRecoveryAlert = false;
                    state.LastRecoveryAlertSentAt = run.StartedAt;
                    state.LastRecoveryAlertError = null;
                }
                else
                {
                    state.LastRecoveryAlertError = run.AlertError;
                }
            }
        }

        // Renew the alert-state concurrency stamp only when this run actually changed the state,
        // so unchanged states don't cause an extra UPDATE on every run.
        db.ChangeTracker.DetectChanges();
        if (db.Entry(state).State == EntityState.Modified)
            state.ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>
    /// Missed-ping check, run by the scheduler job. Everything happens under the alert lock after reloading:
    /// a ping, resume or edit that moved the deadline since the job was queued makes this a no-op.
    /// </summary>
    private async Task CheckHeartbeatMissAsync(Guid monitorId, string? reason, CancellationToken cancellationToken)
    {
        using var lockConnection = jobStorage.GetConnection();
        IDisposable alertLock;
        try
        {
            alertLock = lockConnection.AcquireDistributedLock($"hawk:monitor-alert:{monitorId}", TimeSpan.FromSeconds(HeartbeatMissLockTimeoutSeconds));
        }
        catch (DistributedLockTimeoutException)
        {
            // Record nothing: the deadline stays in the past, so the next scheduler tick retries.
            logger.LogDebug("Heartbeat miss check for monitor {MonitorId} skipped (lock busy).", monitorId);
            return;
        }

        using (alertLock)
        {
            db.ChangeTracker.Clear();
            var monitor = await db.Monitors.FirstOrDefaultAsync(x => x.Id == monitorId, cancellationToken);
            if (monitor is null || monitor.Type != MonitorType.Heartbeat || !monitor.Enabled || monitor.IsPaused)
                return;

            var now = DateTimeOffset.UtcNow;
            if (monitor.NextRunAt is not { } deadline)
            {
                // No deadline (should not happen): give a full period plus grace instead of failing immediately.
                var initial = HeartbeatTokens.InitialDeadline(now, monitor.IntervalSeconds, monitor.HeartbeatGraceSeconds);
                await db.Monitors
                    .Where(m => m.Id == monitorId && m.NextRunAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.NextRunAt, initial), cancellationToken);
                return;
            }

            if (deadline > now)
                return;

            var since = monitor.LastPingAt is { } lastPing ? $"since {lastPing.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC" : "since monitoring started";
            var run = new MonitorRun
            {
                MonitorId = monitor.Id,
                State = "completed",
                StartedAt = now,
                FinishedAt = now,
                Reason = NormalizeReason(reason),
                Success = false,
                ErrorMessage = $"No ping received {since} ({DescribeHeartbeat(monitor)}).",
            };

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            // Claim the miss first: only if the monitor is still in the generation observed above (an Edit, pause/resume or
            // token change renews the stamp; a ping moves the deadline). Edit and resume do not take the alert lock.
            var observedStamp = monitor.ConcurrencyStamp;
            var next = now.AddSeconds(monitor.IntervalSeconds);
            var claimed = await db.Monitors
                .Where(m => m.Id == monitor.Id && m.ConcurrencyStamp == observedStamp && m.NextRunAt == deadline)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(m => m.NextRunAt, next)
                    .SetProperty(m => m.LastRunAt, now), cancellationToken);
            if (claimed == 0)
                return;

            await EvaluateAlertsAsync(monitor, run, cancellationToken);
            db.MonitorRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            await PruneRunHistoryAsync(monitor, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<PingResult> RecordPingAsync(string token, PingRequestInfo info, CancellationToken cancellationToken)
    {
        var monitorId = await db.Monitors
            .AsNoTracking()
            .Where(m => m.HeartbeatToken == token && m.Type == MonitorType.Heartbeat)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (monitorId is null)
            return PingResult.NotFound;

        try
        {
            return await RecordPingCoreAsync(monitorId.Value, token, info, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log the monitor id only: the request path carries the secret token.
            logger.LogError(ex, "Recording a heartbeat ping failed for monitor {MonitorId}", monitorId);
            throw;
        }
    }

    private async Task<PingResult> RecordPingCoreAsync(Guid monitorId, string token, PingRequestInfo info, CancellationToken cancellationToken)
    {
        using var lockConnection = jobStorage.GetConnection();
        IDisposable alertLock;
        try
        {
            alertLock = lockConnection.AcquireDistributedLock($"hawk:monitor-alert:{monitorId}", TimeSpan.FromSeconds(PingLockTimeoutSeconds));
        }
        catch (DistributedLockTimeoutException ex)
        {
            // Never accept a ping outside the lock (a concurrent missed check could overwrite it); the client retries on 503.
            logger.LogWarning(ex, "Heartbeat ping for monitor {MonitorId} rejected: alert lock busy.", monitorId);
            return PingResult.Busy;
        }

        using (alertLock)
        {
            // Edit/pause/resume/regenerate do not take the lock; they renew the concurrency stamp, which the write is conditioned on.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                db.ChangeTracker.Clear();
                var monitor = await db.Monitors.FirstOrDefaultAsync(x => x.Id == monitorId, cancellationToken);
                if (monitor is null || monitor.Type != MonitorType.Heartbeat || !string.Equals(monitor.HeartbeatToken, token, StringComparison.Ordinal))
                    return PingResult.NotFound;
                if (!monitor.Enabled || monitor.IsPaused)
                    return PingResult.Paused;

                var now = DateTimeOffset.UtcNow;
                var clientHeaders = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(info.UserAgent))
                    clientHeaders["User-Agent"] = info.UserAgent.Replace('\0', '\uFFFD')[..Math.Min(info.UserAgent.Length, 500)];
                if (!string.IsNullOrWhiteSpace(info.ClientIp))
                    clientHeaders["Client-IP"] = info.ClientIp;

                var run = new MonitorRun
                {
                    MonitorId = monitor.Id,
                    State = "completed",
                    StartedAt = now,
                    FinishedAt = now,
                    Reason = "ping",
                    Success = true,
                    StatusCode = 200,
                    // The path without the secret token.
                    RequestUrl = "/ping/{token}",
                    RequestMethod = info.Method.Length <= 16 ? info.Method : info.Method[..16],
                    RequestContentType = TrimOrNull(info.ContentType, 200),
                    RequestHeadersJson = SerializeHeaders(clientHeaders),
                };

                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

                // Claim before any recovery email is sent.
                var observedStamp = monitor.ConcurrencyStamp;
                var deadline = HeartbeatTokens.InitialDeadline(now, monitor.IntervalSeconds, monitor.HeartbeatGraceSeconds);
                var claimed = await db.Monitors
                    .Where(m => m.Id == monitorId && m.HeartbeatToken == token && m.ConcurrencyStamp == observedStamp)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(m => m.LastRunAt, now)
                        .SetProperty(m => m.NextRunAt, deadline)
                        .SetProperty(m => m.LastPingAt, now), cancellationToken);
                if (claimed == 0)
                    continue; // changed since the reload: reload once and re-check paused/token

                await EvaluateAlertsAsync(monitor, run, cancellationToken);
                db.MonitorRuns.Add(run);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                await PruneRunHistoryAsync(monitor, cancellationToken);
                return PingResult.Recorded;
            }

            db.ChangeTracker.Clear();
            var current = await db.Monitors.AsNoTracking().Where(x => x.Id == monitorId).Select(x => x.HeartbeatToken).FirstOrDefaultAsync(cancellationToken);
            return string.Equals(current, token, StringComparison.Ordinal) ? PingResult.Busy : PingResult.NotFound;
        }
    }

    private static string DescribeHeartbeat(MonitorEntity monitor) =>
        $"expected every {MonitorIntervals.FormatDuration(monitor.IntervalSeconds)} + {MonitorIntervals.FormatDuration(monitor.HeartbeatGraceSeconds)} grace";

    /// <summary>
    /// Writes <c>LastRunAt</c> set-based so it can never conflict with the monitor's concurrency stamp
    /// (Edit page or other user changes) and never renews it. A conflict here would fail the final save after an alert was sent,
    /// losing the run record and the alert-state update (jobs are not retried: <c>[AutomaticRetry(Attempts = 0)]</c>).
    /// </summary>
    private async Task SetLastRunAtAsync(MonitorEntity monitor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await db.Monitors
            .Where(m => m.Id == monitor.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.LastRunAt, at), cancellationToken);

        // Keep the in-memory entity in sync without marking it modified.
        var entry = db.Entry(monitor);
        var property = entry.Property(m => m.LastRunAt);
        property.CurrentValue = at;
        property.OriginalValue = at;
        property.IsModified = false;
    }

    private async Task<MonitorAlertState> GetOrCreateAlertStateAsync(Guid monitorId, Guid currentRunId, CancellationToken cancellationToken)
    {
        var state = await db.MonitorAlertStates.FirstOrDefaultAsync(x => x.MonitorId == monitorId, cancellationToken);
        if (state is not null)
            return state;

        // Initialize best-effort from recent completed runs (excluding the run being evaluated).
        var recent = await db.MonitorRuns
            .Where(r => r.MonitorId == monitorId && r.Id != currentRunId && r.State == "completed")
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .Select(r => new MonitorAlertingDecider.HistoricRun(r.StartedAt, r.Success, r.AlertSent))
            .ToListAsync(cancellationToken);

        state = MonitorAlertingDecider.SeedFromHistory(monitorId, recent);
        db.MonitorAlertStates.Add(state);
        return state;
    }

    private async Task<IReadOnlyList<FailureLogEntry>> GetFailureLogEntriesAsync(
        Guid monitorId,
        MonitorRun currentRun,
        DateTimeOffset? incidentOpenedAt,
        int consecutiveFailures,
        CancellationToken cancellationToken)
    {
        var priorLimit = Math.Max(0, Math.Min(FailureLogEntryLimit - 1, consecutiveFailures - 1));
        var entries = new List<FailureLogEntry>(priorLimit + 1);

        if (priorLimit > 0)
        {
            var query = db.MonitorRuns
                .AsNoTracking()
                .Where(r =>
                    r.MonitorId == monitorId &&
                    r.Id != currentRun.Id &&
                    r.State == "completed" &&
                    !r.Success);

            if (incidentOpenedAt is not null)
                query = query.Where(r => r.StartedAt >= incidentOpenedAt.Value);

            var prior = await query
                .OrderByDescending(r => r.StartedAt)
                .Take(priorLimit)
                .Select(r => new FailureLogEntry(r.StartedAt, r.StatusCode))
                .ToListAsync(cancellationToken);

            prior.Reverse();
            entries.AddRange(prior);
        }

        entries.Add(new FailureLogEntry(currentRun.StartedAt, currentRun.StatusCode));
        return entries;
    }

    private async Task<MonitorRun> PersistInvalidConfigAsync(
        MonitorEntity monitor,
        string error,
        string? reason,
        UrlCheckRequest req,
        CancellationToken cancellationToken,
        Guid? runId)
    {
        var now = DateTimeOffset.UtcNow;
        await SetLastRunAtAsync(monitor, now, cancellationToken);

        var existingRun = runId is null
            ? null
            : await db.MonitorRuns.FirstOrDefaultAsync(r => r.Id == runId.Value && r.MonitorId == monitor.Id, cancellationToken);

        var run = existingRun ?? new MonitorRun { MonitorId = monitor.Id };
        run.State = "completed";
        run.StartedAt = now;
        run.FinishedAt = now;
        run.Reason = NormalizeReason(reason);
        run.RequestUrl = req.Url.ToString();
        run.RequestMethod = req.Method.Method;
        run.RequestContentType = req.ContentType;
        run.RequestTimeoutMs = (int)Math.Clamp(req.Timeout.TotalMilliseconds, 0, int.MaxValue);
        run.RequestHeadersJson = SerializeHeaders(req.Headers);
        run.RequestBodySnippet = TrimOrNull(req.Body, 4000);
        run.DurationMs = 0;
        run.StatusCode = null;
        run.Success = false;
        run.ErrorMessage = error;
        run.ResponseSnippet = null;
        run.ResponseHeadersJson = "{}";
        run.ResponseContentType = null;
        run.ResponseContentLength = null;
        run.MatchResultsJson = "[]";

        if (existingRun is null)
            db.MonitorRuns.Add(run);

        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task<bool> TrySendFailureAlertAsync(
        MonitorEntity monitor,
        MonitorRun run,
        MonitorAlertKind kind,
        int consecutiveFailures,
        int threshold,
        DateTimeOffset? incidentOpenedAt,
        DateTimeOffset? lastFailureAlertSentAt,
        IReadOnlyList<FailureLogEntry> failureLog,
        CancellationToken cancellationToken)
    {
        var emailEnabled = config.GetValue("Hawk:Email:Enabled", true);
        if (!emailEnabled)
        {
            run.AlertSent = false;
            run.AlertError = "Email disabled (Hawk:Email:Enabled=false).";
            return false;
        }

        var from = config["Hawk:Email:From"] ?? config["Hawk:Resend:From"];
        if (string.IsNullOrWhiteSpace(from))
        {
            run.AlertSent = false;
            run.AlertError = "Email from address is not configured (Hawk:Email:From).";
            return false;
        }

        var to = await ResolveRecipientsAsync(monitor, cancellationToken);
        if (to.Length == 0)
        {
            run.AlertSent = false;
            run.AlertError = "No alert recipients resolved.";
            return false;
        }

        var heartbeat = monitor.Type == MonitorType.Heartbeat;
        var status = run.StatusCode is null ? "NO_RESPONSE" : run.StatusCode.ToString()!;
        var subjectPrefix = kind == MonitorAlertKind.FailureReminder ? "[ALERT FAIL REMINDER]" : "[ALERT FAIL]";
        var subject = $"{subjectPrefix} {monitor.Name} ({(heartbeat ? "NO_PING" : status)})";
        var failureWord = consecutiveFailures == 1 ? "failure" : "failures";
        var failuresLine = $"<p><b>Failures:</b> {consecutiveFailures} consecutive {failureWord} (alert threshold: {threshold})</p>";
        var incidentLine = incidentOpenedAt is null ? "" : $"<p><b>Incident opened:</b> {incidentOpenedAt:O} UTC</p>";
        var lastAlertLine = lastFailureAlertSentAt is null ? "" : $"<p><b>Last alert sent:</b> {lastFailureAlertSentAt:O} UTC</p>";
        var failureLogHtml = BuildFailureLogHtml(failureLog, consecutiveFailures, heartbeat);
        var targetLines = heartbeat
            ? $"""
              <p><b>Type:</b> Heartbeat ({WebUtility.HtmlEncode(DescribeHeartbeat(monitor))})</p>
              """
            : $"""
              <p><b>URL:</b> {WebUtility.HtmlEncode(monitor.Url)}</p>
              <p><b>Method:</b> {WebUtility.HtmlEncode(monitor.Method)}</p>
              <p><b>Status:</b> {WebUtility.HtmlEncode(status)}</p>
              """;
        var html = $"""
            <h2>{(heartbeat ? "Heartbeat missed" : "Monitor failed")}</h2>
            <p><b>Name:</b> {WebUtility.HtmlEncode(monitor.Name)}</p>
            {targetLines}
            <p><b>Error:</b> {WebUtility.HtmlEncode(run.ErrorMessage ?? "(none)")}</p>
            {failuresLine}
            <p><b>When:</b> {run.StartedAt:O} UTC</p>
            {incidentLine}
            {lastAlertLine}
            {failureLogHtml}
            """;

        try
        {
            await emailSender.SendAsync(from, to, subject, html, cancellationToken);
            run.AlertSent = true;
            run.AlertError = null;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send alert for monitor {MonitorId}", monitor.Id);
            run.AlertSent = false;
            run.AlertError = ex.Message;
            return false;
        }
    }

    private static string BuildFailureLogHtml(IReadOnlyList<FailureLogEntry> entries, int consecutiveFailures, bool timesOnly)
    {
        if (entries.Count == 0)
            return "";

        var sb = new StringBuilder();
        if (consecutiveFailures > entries.Count)
            sb.AppendLine($"Showing latest {entries.Count} of {consecutiveFailures} consecutive failures.");

        foreach (var entry in entries)
        {
            if (timesOnly)
            {
                sb.AppendLine($"{entry.StartedAt:O} UTC");
                continue;
            }

            var status = entry.StatusCode is null ? "NO_RESPONSE" : entry.StatusCode.Value.ToString();
            sb.AppendLine($"{entry.StartedAt:O} UTC status={status}");
        }

        return $"""
            <h3>Failure log</h3>
            <pre style="font-family: monospace; white-space: pre-wrap;">{WebUtility.HtmlEncode(sb.ToString().TrimEnd())}</pre>
            """;
    }

    private async Task<bool> TrySendRecoveredAlertAsync(
        MonitorEntity monitor,
        MonitorRun run,
        DateTimeOffset? incidentOpenedAt,
        DateTimeOffset? lastFailureAlertSentAt,
        CancellationToken cancellationToken)
    {
        var emailEnabled = config.GetValue("Hawk:Email:Enabled", true);
        if (!emailEnabled)
        {
            run.AlertSent = false;
            run.AlertError = "Email disabled (Hawk:Email:Enabled=false).";
            return false;
        }

        var from = config["Hawk:Email:From"] ?? config["Hawk:Resend:From"];
        if (string.IsNullOrWhiteSpace(from))
        {
            run.AlertSent = false;
            run.AlertError = "Email from address is not configured (Hawk:Email:From).";
            return false;
        }

        var to = await ResolveRecipientsAsync(monitor, cancellationToken);
        if (to.Length == 0)
        {
            run.AlertSent = false;
            run.AlertError = "No alert recipients resolved.";
            return false;
        }

        var heartbeat = monitor.Type == MonitorType.Heartbeat;
        var status = run.StatusCode is null ? "NO_RESPONSE" : run.StatusCode.ToString()!;
        var subject = $"[ALERT RECOVERED] {monitor.Name} ({(heartbeat ? "PING_RECEIVED" : status)})";

        var incidentOpenedLine = incidentOpenedAt is null ? "" : $"<p><b>Incident opened:</b> {incidentOpenedAt:O} UTC</p>";
        var incidentDurationLine = incidentOpenedAt is null ? "" : $"<p><b>Incident duration:</b> {(run.StartedAt - incidentOpenedAt.Value).TotalMinutes:F1} minutes</p>";
        var lastAlertLine = lastFailureAlertSentAt is null ? "" : $"<p><b>Last failure alert sent:</b> {lastFailureAlertSentAt:O} UTC</p>";
        var targetLines = heartbeat
            ? $"""
              <p><b>Type:</b> Heartbeat ({WebUtility.HtmlEncode(DescribeHeartbeat(monitor))})</p>
              <p><b>Status:</b> Ping received</p>
              """
            : $"""
              <p><b>URL:</b> {WebUtility.HtmlEncode(monitor.Url)}</p>
              <p><b>Method:</b> {WebUtility.HtmlEncode(monitor.Method)}</p>
              <p><b>Status:</b> {WebUtility.HtmlEncode(status)}</p>
              """;

        var html = $"""
            <h2>{(heartbeat ? "Heartbeat recovered" : "Monitor recovered")}</h2>
            <p><b>Name:</b> {WebUtility.HtmlEncode(monitor.Name)}</p>
            {targetLines}
            <p><b>When:</b> {run.StartedAt:O} UTC</p>
            {incidentOpenedLine}
            {incidentDurationLine}
            {lastAlertLine}
            """;

        try
        {
            await emailSender.SendAsync(from, to, subject, html, cancellationToken);
            run.AlertSent = true;
            run.AlertError = null;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send recovery alert for monitor {MonitorId}", monitor.Id);
            run.AlertSent = false;
            run.AlertError = ex.Message;
            return false;
        }
    }

    private async Task<string[]> ResolveRecipientsAsync(MonitorEntity monitor, CancellationToken cancellationToken) =>
        (await recipientResolver.ResolveAsync(monitor, cancellationToken)).Addresses;

    private static string SerializeHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return "{}";
        return JsonSerializer.Serialize(headers);
    }

    private static string? TrimOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        // PostgreSQL text columns reject NUL characters.
        value = value.Replace('\0', '\uFFFD');
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unknown";
        return reason.Trim().ToLowerInvariant();
    }

    private async Task PruneRunHistoryAsync(MonitorEntity monitor, CancellationToken cancellationToken)
    {
        var retentionDays = await ResolveRunRetentionDaysAsync(monitor, cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        await db.MonitorRuns
            .Where(r => r.MonitorId == monitor.Id && r.StartedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<int> ResolveRunRetentionDaysAsync(MonitorEntity monitor, CancellationToken cancellationToken)
    {
        if (monitor.RunRetentionDays is > 0)
            return Math.Clamp(monitor.RunRetentionDays.Value, 1, 3650);

        if (!string.IsNullOrWhiteSpace(monitor.CreatedByUserId))
        {
            var accountRetention = await db.UserMonitorSettings
                .Where(x => x.UserId == monitor.CreatedByUserId)
                .Select(x => x.RunRetentionDays)
                .FirstOrDefaultAsync(cancellationToken);

            if (accountRetention is > 0)
                return Math.Clamp(accountRetention.Value, 1, 3650);
        }

        var defaultDays = config.GetValue("Hawk:Monitoring:RunRetentionDaysDefault", 90);
        return Math.Clamp(defaultDays, 1, 3650);
    }

    private static string BuildFailureMessage(HttpStatusCode? statusCode, bool statusIsSuccess, IReadOnlyList<UrlCheckMatchResult> matchResults)
    {
        var parts = new List<string>();

        if (!statusIsSuccess)
        {
            if (statusCode is null)
            {
                parts.Add("No HTTP response");
            }
            else
            {
                parts.Add($"HTTP {(int)statusCode.Value} ({statusCode.Value})");
            }
        }

        var failed = matchResults.Count(m => !m.Matched);
        if (failed > 0)
            parts.Add($"{failed} match rule(s) failed");

        return string.Join("; ", parts);
    }
}
