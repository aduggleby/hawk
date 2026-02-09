// <file>
// <summary>
// Hangfire-executed monitor runner. Builds a <see cref="Hawk.Web.Services.UrlCheckRequest"/> from stored configuration,
// runs the check, persists results, and sends failure alerts via a Resend-compatible email service.
// </summary>
// </file>

using System.Net;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Email;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Executes monitors and stores their run history.
/// </summary>
public sealed class MonitorRunner(
    ApplicationDbContext db,
    IUrlChecker urlChecker,
    IEmailSender emailSender,
    UserManager<IdentityUser> userManager,
    ILogger<MonitorRunner> logger,
    IConfiguration config) : IMonitorRunner
{
    /// <inheritdoc />
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid monitorId, string? reason, CancellationToken cancellationToken)
    {
        // Branch: monitor not found or deleted.
        var monitor = await db.Monitors
            .Include(x => x.Headers)
            .Include(x => x.MatchRules)
            // Important: multiple collection includes can cause cartesian explosion; split queries keep it predictable.
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == monitorId, cancellationToken);
        if (monitor is null)
        {
            logger.LogWarning("Monitor {MonitorId} not found (reason={Reason})", monitorId, reason);
            return;
        }

        if (!monitor.Enabled && !string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase))
        {
            // Branch: disabled monitors should not run on schedule.
            return;
        }

        if (!Uri.TryCreate(monitor.Url, UriKind.Absolute, out var uri))
        {
            await PersistInvalidConfigAsync(monitor, "Invalid URL", cancellationToken);
            return;
        }

        var headers = monitor.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .GroupBy(h => h.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

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

        // Best-effort: keep a rolling last-run timestamp on the monitor.
        monitor.LastRunAt = started;
        db.MonitorRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistInvalidConfigAsync(MonitorEntity monitor, string error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        monitor.LastRunAt = now;
        db.MonitorRuns.Add(new MonitorRun
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
        });
        await db.SaveChangesAsync(cancellationToken);
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

        // Recipients: all Admin users (simple v1 behavior).
        var admins = await userManager.GetUsersInRoleAsync("Admin");
        var to = admins
            .Select(a => a.Email)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(e => e!)
            .ToArray();

        if (to.Length == 0)
        {
            run.AlertSent = false;
            run.AlertError = "No admin emails found for alert recipients.";
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

    private async Task<int> CountPriorConsecutiveFailuresAsync(Guid monitorId, CancellationToken cancellationToken)
    {
        // Load a small window of recent runs and count failures until the first success.
        // Branch: limited to 50 to keep this O(1) for normal usage.
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
