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

        if (!monitor.Enabled && !string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase))
        {
            // Branch: disabled monitors should not run on schedule.
            return null;
        }

        if (!Uri.TryCreate(monitor.Url, UriKind.Absolute, out var uri))
        {
            var invalidRun = await PersistInvalidConfigAsync(monitor, "Invalid URL", cancellationToken);
            var invalidReq = new UrlCheckRequest(uri ?? new Uri("http://invalid.local/"), HttpMethod.Get, new Dictionary<string, string>(), null, null, TimeSpan.Zero, []);
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

        var run = new MonitorRun
        {
            MonitorId = monitor.Id,
            StartedAt = started,
            FinishedAt = finished,
            DurationMs = (int)Math.Clamp(res.Duration.TotalMilliseconds, 0, int.MaxValue),
            StatusCode = res.StatusCode is null ? null : (int)res.StatusCode.Value,
            Success = res.Success,
            ErrorMessage = res.ErrorMessage,
            ResponseSnippet = res.ResponseBodySnippet,
            MatchResultsJson = JsonSerializer.Serialize(res.MatchResults.Select(m => new
            {
                Mode = m.Rule.Mode.ToString(),
                m.Rule.Pattern,
                m.Matched,
                m.Details
            })),
        };

        if (!run.Success)
        {
            var priorFailures = await CountPriorConsecutiveFailuresAsync(monitor.Id, cancellationToken);
            var threshold = Math.Clamp(monitor.AlertAfterConsecutiveFailures, 1, 20);
            if (AlertPolicy.ShouldAlertOnFailure(threshold, priorFailures))
            {
                await TrySendAlertAsync(monitor, run, cancellationToken);
            }
            else
            {
                run.AlertSent = false;
                run.AlertError = $"Not alerted (needs {threshold} consecutive failures; prior={priorFailures}).";
            }
        }

        monitor.LastRunAt = started;
        db.MonitorRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        return new MonitorExecutionResult(monitor, req, res, run);
    }

    private async Task<MonitorRun> PersistInvalidConfigAsync(MonitorEntity monitor, string error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        monitor.LastRunAt = now;
        var run = new MonitorRun
        {
            MonitorId = monitor.Id,
            StartedAt = now,
            FinishedAt = now,
            DurationMs = 0,
            StatusCode = null,
            Success = false,
            ErrorMessage = error,
            ResponseSnippet = null,
            MatchResultsJson = "[]",
        };
        db.MonitorRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task TrySendAlertAsync(MonitorEntity monitor, MonitorRun run, CancellationToken cancellationToken)
    {
        var emailEnabled = config.GetValue("Hawk:Email:Enabled", true);
        if (!emailEnabled)
            return;

        var from = config["Hawk:Email:From"] ?? config["Hawk:Resend:From"];
        if (string.IsNullOrWhiteSpace(from))
        {
            run.AlertSent = false;
            run.AlertError = "Email from address is not configured (Hawk:Email:From).";
            return;
        }

        var to = await ResolveRecipientsAsync(monitor, cancellationToken);
        if (to.Length == 0)
        {
            run.AlertSent = false;
            run.AlertError = "No alert recipients resolved.";
            return;
        }

        var status = run.StatusCode is null ? "NO_RESPONSE" : run.StatusCode.ToString()!;
        var subject = $"[Hawk] FAIL {monitor.Name} ({status})";
        var html = $"""
            <h2>Monitor failed</h2>
            <p><b>Name:</b> {WebUtility.HtmlEncode(monitor.Name)}</p>
            <p><b>URL:</b> {WebUtility.HtmlEncode(monitor.Url)}</p>
            <p><b>Method:</b> {WebUtility.HtmlEncode(monitor.Method)}</p>
            <p><b>Status:</b> {WebUtility.HtmlEncode(status)}</p>
            <p><b>Error:</b> {WebUtility.HtmlEncode(run.ErrorMessage ?? "(none)")}</p>
            <p><b>When:</b> {run.StartedAt:O} UTC</p>
            """;

        try
        {
            await emailSender.SendAsync(from, to, subject, html, cancellationToken);
            run.AlertSent = true;
            run.AlertError = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send alert for monitor {MonitorId}", monitor.Id);
            run.AlertSent = false;
            run.AlertError = ex.Message;
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

    private async Task<int> CountPriorConsecutiveFailuresAsync(Guid monitorId, CancellationToken cancellationToken)
    {
        var recent = await db.MonitorRuns
            .Where(r => r.MonitorId == monitorId)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .Select(r => new { r.Success })
            .ToListAsync(cancellationToken);

        var count = 0;
        foreach (var r in recent)
        {
            if (r.Success)
                break;
            count++;
        }
        return count;
    }
}
