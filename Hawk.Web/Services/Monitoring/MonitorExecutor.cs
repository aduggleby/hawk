// <file>
// <summary>
// Shared monitor execution implementation used by both Hangfire jobs and interactive UI testing.
// Produces both the raw <see cref="UrlCheckResult"/> (for diagnostics) and a persisted <see cref="MonitorRun"/>.
// </summary>
// </file>

using System.Net;
using System.Text.Json;
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

public interface IMonitorExecutor
{
    Task<MonitorExecutionResult?> ExecuteAsync(Guid monitorId, string? reason, CancellationToken cancellationToken);
}

public sealed class MonitorExecutor(
    ApplicationDbContext db,
    IUrlChecker urlChecker,
    IEmailSender emailSender,
    UserManager<IdentityUser> userManager,
    ILogger<MonitorExecutor> logger,
    IConfiguration config) : IMonitorExecutor
{
    public async Task<MonitorExecutionResult?> ExecuteAsync(Guid monitorId, string? reason, CancellationToken cancellationToken)
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

        if ((!monitor.Enabled || monitor.IsPaused) && !string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase))
        {
            // Branch: disabled/paused monitors should not run on schedule.
            return null;
        }

        if (!Uri.TryCreate(monitor.Url, UriKind.Absolute, out var uri))
        {
            var invalidReq = new UrlCheckRequest(uri ?? new Uri("http://invalid.local/"), HttpMethod.Get, new Dictionary<string, string>(), null, null, TimeSpan.Zero, []);
            var invalidRun = await PersistInvalidConfigAsync(monitor, "Invalid URL", reason, invalidReq, cancellationToken);
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
            MatchRules: matchRules
        );

        var started = DateTimeOffset.UtcNow;
        var res = await urlChecker.CheckAsync(req, cancellationToken);
        var finished = started.Add(res.Duration);
        var statusIsSuccess = AllowedStatusCodesParser.IsSuccessStatusCode(res.StatusCode, monitor.AllowedStatusCodes);
        var matchesPassed = res.MatchResults.All(m => m.Matched);
        var success = statusIsSuccess && matchesPassed;

        var run = new MonitorRun
        {
            MonitorId = monitor.Id,
            StartedAt = started,
            FinishedAt = finished,
            Reason = NormalizeReason(reason),
            RequestUrl = req.Url.ToString(),
            RequestMethod = req.Method.Method,
            RequestContentType = req.ContentType,
            RequestTimeoutMs = (int)Math.Clamp(req.Timeout.TotalMilliseconds, 0, int.MaxValue),
            RequestHeadersJson = SerializeHeaders(req.Headers),
            RequestBodySnippet = TrimOrNull(req.Body, 4000),
            DurationMs = (int)Math.Clamp(res.Duration.TotalMilliseconds, 0, int.MaxValue),
            StatusCode = res.StatusCode is null ? null : (int)res.StatusCode.Value,
            Success = success,
            ErrorMessage = success ? null : BuildFailureMessage(res.StatusCode, statusIsSuccess, res.MatchResults),
            ResponseSnippet = res.ResponseBodySnippet,
            ResponseHeadersJson = SerializeHeaders(res.ResponseHeaders),
            ResponseContentType = res.ResponseContentType,
            ResponseContentLength = res.ResponseContentLength,
            MatchResultsJson = JsonSerializer.Serialize(res.MatchResults.Select(m => new
            {
                Mode = m.Rule.Mode.ToString(),
                m.Rule.Pattern,
                m.Matched,
                m.Details
            })),
        };

        var threshold = Math.Clamp(monitor.AlertAfterConsecutiveFailures, 1, 20);
        var repeatHours = config.GetValue("Hawk:Alerting:RepeatFailureAlertEveryHours", 24);
        repeatHours = Math.Clamp(repeatHours, 1, 24 * 30);
        var repeatEvery = TimeSpan.FromHours(repeatHours);

        var state = await GetOrCreateAlertStateAsync(monitor.Id, cancellationToken);

        // Capture prior state for composing emails (duration, etc.).
        var incidentOpenedAt = state.FailureIncidentOpenedAt;
        var lastFailureAlertSentAt = state.LastFailureAlertSentAt;

        MonitorAlertDecision decision;
        if (!run.Success)
        {
            decision = MonitorAlertingDecider.OnFailure(state, threshold, run.StartedAt, repeatEvery);

            if (decision.Kind is MonitorAlertKind.Failure or MonitorAlertKind.FailureReminder)
            {
                var sent = await TrySendFailureAlertAsync(
                    monitor,
                    run,
                    decision.Kind,
                    incidentOpenedAt,
                    lastFailureAlertSentAt,
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

        monitor.LastRunAt = started;
        db.MonitorRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        await PruneRunHistoryAsync(monitor, cancellationToken);

        return new MonitorExecutionResult(monitor, req, res, run);
    }

    private async Task<MonitorAlertState> GetOrCreateAlertStateAsync(Guid monitorId, CancellationToken cancellationToken)
    {
        var state = await db.MonitorAlertStates.FirstOrDefaultAsync(x => x.MonitorId == monitorId, cancellationToken);
        if (state is not null)
            return state;

        // Initialize best-effort from recent run history so upgrades don't change alert timing too much.
        var recent = await db.MonitorRuns
            .Where(r => r.MonitorId == monitorId)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .Select(r => new { r.StartedAt, r.Success, r.AlertSent })
            .ToListAsync(cancellationToken);

        var failures = 0;
        DateTimeOffset? openedAt = null;
        foreach (var r in recent)
        {
            if (r.Success)
                break;
            failures++;
            openedAt = r.StartedAt;
        }

        // last alert during the current failure streak (if any).
        DateTimeOffset? lastAlertSentAt = null;
        if (failures > 0)
        {
            lastAlertSentAt = recent
                .Where(r => !r.Success && r.AlertSent)
                .Select(r => (DateTimeOffset?)r.StartedAt)
                .FirstOrDefault();
        }

        state = new MonitorAlertState
        {
            MonitorId = monitorId,
            ConsecutiveFailures = failures,
            FailureIncidentOpenedAt = failures > 0 ? openedAt : null,
            LastFailureAlertSentAt = lastAlertSentAt,
            PendingRecoveryAlert = false,
        };

        db.MonitorAlertStates.Add(state);
        return state;
    }

    private async Task<MonitorRun> PersistInvalidConfigAsync(
        MonitorEntity monitor,
        string error,
        string? reason,
        UrlCheckRequest req,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        monitor.LastRunAt = now;
        var run = new MonitorRun
        {
            MonitorId = monitor.Id,
            StartedAt = now,
            FinishedAt = now,
            Reason = NormalizeReason(reason),
            RequestUrl = req.Url.ToString(),
            RequestMethod = req.Method.Method,
            RequestContentType = req.ContentType,
            RequestTimeoutMs = (int)Math.Clamp(req.Timeout.TotalMilliseconds, 0, int.MaxValue),
            RequestHeadersJson = SerializeHeaders(req.Headers),
            RequestBodySnippet = TrimOrNull(req.Body, 4000),
            DurationMs = 0,
            StatusCode = null,
            Success = false,
            ErrorMessage = error,
            ResponseSnippet = null,
            ResponseHeadersJson = "{}",
            ResponseContentType = null,
            ResponseContentLength = null,
            MatchResultsJson = "[]",
        };
        db.MonitorRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task<bool> TrySendFailureAlertAsync(
        MonitorEntity monitor,
        MonitorRun run,
        MonitorAlertKind kind,
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

        var status = run.StatusCode is null ? "NO_RESPONSE" : run.StatusCode.ToString()!;
        var subjectPrefix = kind == MonitorAlertKind.FailureReminder ? "[ALERT FAIL REMINDER]" : "[ALERT FAIL]";
        var subject = $"{subjectPrefix} {monitor.Name} ({status})";
        var incidentLine = incidentOpenedAt is null ? "" : $"<p><b>Incident opened:</b> {incidentOpenedAt:O} UTC</p>";
        var lastAlertLine = lastFailureAlertSentAt is null ? "" : $"<p><b>Last alert sent:</b> {lastFailureAlertSentAt:O} UTC</p>";
        var html = $"""
            <h2>Monitor failed</h2>
            <p><b>Name:</b> {WebUtility.HtmlEncode(monitor.Name)}</p>
            <p><b>URL:</b> {WebUtility.HtmlEncode(monitor.Url)}</p>
            <p><b>Method:</b> {WebUtility.HtmlEncode(monitor.Method)}</p>
            <p><b>Status:</b> {WebUtility.HtmlEncode(status)}</p>
            <p><b>Error:</b> {WebUtility.HtmlEncode(run.ErrorMessage ?? "(none)")}</p>
            <p><b>When:</b> {run.StartedAt:O} UTC</p>
            {incidentLine}
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
            logger.LogWarning(ex, "Failed to send alert for monitor {MonitorId}", monitor.Id);
            run.AlertSent = false;
            run.AlertError = ex.Message;
            return false;
        }
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

        var status = run.StatusCode is null ? "NO_RESPONSE" : run.StatusCode.ToString()!;
        var subject = $"[ALERT RECOVERED] {monitor.Name} ({status})";

        var incidentOpenedLine = incidentOpenedAt is null ? "" : $"<p><b>Incident opened:</b> {incidentOpenedAt:O} UTC</p>";
        var incidentDurationLine = incidentOpenedAt is null ? "" : $"<p><b>Incident duration:</b> {(run.StartedAt - incidentOpenedAt.Value).TotalMinutes:F1} minutes</p>";
        var lastAlertLine = lastFailureAlertSentAt is null ? "" : $"<p><b>Last failure alert sent:</b> {lastFailureAlertSentAt:O} UTC</p>";

        var html = $"""
            <h2>Monitor recovered</h2>
            <p><b>Name:</b> {WebUtility.HtmlEncode(monitor.Name)}</p>
            <p><b>URL:</b> {WebUtility.HtmlEncode(monitor.Url)}</p>
            <p><b>Method:</b> {WebUtility.HtmlEncode(monitor.Method)}</p>
            <p><b>Status:</b> {WebUtility.HtmlEncode(status)}</p>
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

    private async Task<string[]> ResolveRecipientsAsync(MonitorEntity monitor, CancellationToken cancellationToken)
    {
        // 1) Per-monitor override.
        if (!string.IsNullOrWhiteSpace(monitor.AlertEmailOverride))
            return [monitor.AlertEmailOverride.Trim()];

        // 2) Account-wide override for the monitor owner.
        if (!string.IsNullOrWhiteSpace(monitor.CreatedByUserId))
        {
            var accountOverride = await db.UserAlertSettings
                .Where(x => x.UserId == monitor.CreatedByUserId)
                .Select(x => x.AlertEmail)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(accountOverride))
                return [accountOverride.Trim()];

            // 3) Owner's Identity email.
            var owner = await userManager.FindByIdAsync(monitor.CreatedByUserId);
            if (!string.IsNullOrWhiteSpace(owner?.Email))
                return [owner!.Email!];
        }

        // Fallback: previous v1 behavior, email all Admin users.
        var admins = await userManager.GetUsersInRoleAsync("Admin");
        return admins
            .Select(a => a.Email)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(e => e!)
            .ToArray();
    }

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
