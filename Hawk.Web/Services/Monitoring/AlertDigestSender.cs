// <file>
// <summary>
// Sends queued alert emails (PendingAlerts), grouped per recipient set.
//
// Why: a shared dependency failing makes many monitors alert in the same scheduler tick, one email each. Alerts are queued
// by MonitorExecutor and sent here once no new alert for the same recipients arrived for the quiet window (default 30 s),
// at the latest after the maximum delay (default 120 s). One alert is sent as its normal email, two or more as a summary.
// Every flush runs under the Hangfire lock "hawk:alert-digest" so two workers never send the same group.
// Each queued alert schedules a flush after the window; the minutely recurring job "hawk-alert-digest" (always registered,
// alerts are also raised by pings and manual runs) retries failed sends and picks up anything a skipped or lost flush missed.
// </summary>
// </file>

using System.Net;
using System.Text;
using Hangfire;
using Hangfire.Storage;
using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Quiet window and maximum delay for grouped alert emails (<c>Hawk:Alerting:DigestWindowSeconds</c>, <c>DigestMaxDelaySeconds</c>).
/// </summary>
/// <param name="Window">Quiet time after the newest queued alert before a group is sent. Zero = no grouping (send inline).</param>
/// <param name="MaxDelay">Longest time the oldest queued alert of a group waits.</param>
public sealed record AlertDigestOptions(TimeSpan Window, TimeSpan MaxDelay)
{
    /// <summary>Whether alerts are queued (window above zero).</summary>
    public bool Enabled => Window > TimeSpan.Zero;

    /// <summary>
    /// Reads the options: window 0-600 s (default 30), maximum delay from the window up to 3600 s (default 120).
    /// </summary>
    public static AlertDigestOptions From(IConfiguration config)
    {
        var window = Math.Clamp(config.GetValue("Hawk:Alerting:DigestWindowSeconds", 30), 0, 600);
        var maxDelay = Math.Clamp(config.GetValue("Hawk:Alerting:DigestMaxDelaySeconds", 120), window, 3600);
        return new AlertDigestOptions(TimeSpan.FromSeconds(window), TimeSpan.FromSeconds(maxDelay));
    }
}

/// <summary>
/// Result of <see cref="AlertDigestSender.Plan"/>.
/// </summary>
/// <param name="Due">Groups to send now, each ordered by occurrence.</param>
/// <param name="NextCheckAt">When the earliest waiting group becomes due; null when none is waiting.</param>
public sealed record AlertDigestPlan(IReadOnlyList<IReadOnlyList<PendingAlert>> Due, DateTimeOffset? NextCheckAt);

/// <summary>
/// Hangfire contract of the alert digest flush.
/// </summary>
public interface IAlertDigestSender
{
    /// <summary>
    /// Sends every due group of queued alerts. Skips when another flush holds the lock.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AutomaticRetry(Attempts = 0)]
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sends queued alert emails, one email per due recipient group.
/// </summary>
public sealed class AlertDigestSender(
    ApplicationDbContext db,
    IEmailSender emailSender,
    IConfiguration config,
    ILogger<AlertDigestSender> logger,
    JobStorage? jobStorage = null,
    IBackgroundJobClient? jobs = null) : IAlertDigestSender
{
    /// <summary>Recurring job id of <see cref="FlushAsync"/>.</summary>
    public const string RecurringJobId = "hawk-alert-digest";

    /// <summary>A queued alert whose sends keep failing is given up after this long.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    private const string LockKey = "hawk:alert-digest";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeepSentFor = TimeSpan.FromDays(7);

    /// <summary>
    /// Registers the minutely recurring flush. Also with <c>Hawk:Scheduler:Enabled=false</c>: pings and manual runs still queue alerts.
    /// </summary>
    public static void EnsureStarted(IRecurringJobManager recurring) =>
        recurring.AddOrUpdate<IAlertDigestSender>(RecurringJobId, s => s.FlushAsync(CancellationToken.None), Cron.Minutely());

    /// <summary>
    /// Schedules a flush after <paramref name="delay"/>.
    /// </summary>
    public static void ScheduleFlush(IBackgroundJobClient jobs, TimeSpan delay) =>
        jobs.Schedule<IAlertDigestSender>(s => s.FlushAsync(CancellationToken.None), delay);

    /// <summary>
    /// Normalizes recipients into the grouping key: trimmed, lowercased, distinct, ordinal-sorted, comma-joined.
    /// </summary>
    public static string RecipientKey(IEnumerable<string> addresses) =>
        string.Join(",", addresses
            .Select(a => a.Trim().ToLowerInvariant())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    /// <summary>
    /// Splits queued alerts into groups by recipients and picks the due ones: no alert queued within the window,
    /// or the oldest queued at least the maximum delay ago. Pure; used by <see cref="FlushAsync"/>.
    /// </summary>
    public static AlertDigestPlan Plan(IEnumerable<PendingAlert> queued, DateTimeOffset now, AlertDigestOptions options)
    {
        var due = new List<IReadOnlyList<PendingAlert>>();
        DateTimeOffset? next = null;

        foreach (var group in queued.GroupBy(a => a.Recipients, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var oldest = group.Min(a => a.QueuedAt);
            var newest = group.Max(a => a.QueuedAt);
            var dueAt = Min(newest + options.Window, oldest + options.MaxDelay);
            if (dueAt <= now)
            {
                due.Add(group.OrderBy(a => a.OccurredAt).ThenBy(a => a.QueuedAt).ToList());
                continue;
            }

            next = next is null ? dueAt : Min(next.Value, dueAt);
        }

        return new AlertDigestPlan(due, next);
    }

    /// <summary>
    /// The email for a group: the stored email for one alert, a summary for more.
    /// </summary>
    public static (string Subject, string Html) Compose(IReadOnlyList<PendingAlert> alerts)
    {
        if (alerts.Count == 1)
            return (alerts[0].Subject, alerts[0].Html);

        var failed = alerts.Count(a => a.Kind == nameof(MonitorAlertKind.Failure));
        var stillFailing = alerts.Count(a => a.Kind == nameof(MonitorAlertKind.FailureReminder));
        var recovered = alerts.Count(a => a.Kind == nameof(MonitorAlertKind.Recovered));
        var parts = new List<string>();
        if (failed > 0) parts.Add($"{failed} failed");
        if (stillFailing > 0) parts.Add($"{stillFailing} still failing");
        if (recovered > 0) parts.Add($"{recovered} recovered");
        var subject = $"[ALERT SUMMARY] {string.Join(", ", parts)}";

        var first = alerts.Min(a => a.OccurredAt);
        var last = alerts.Max(a => a.OccurredAt);
        var sb = new StringBuilder();
        sb.AppendLine($"<h2>{alerts.Count} alerts</h2>");
        sb.AppendLine($"<p>Between {first:O} UTC and {last:O} UTC.</p>");
        sb.AppendLine("""<table cellpadding="4" style="border-collapse: collapse;">""");
        sb.AppendLine("<tr><th align=\"left\">When (UTC)</th><th align=\"left\">Event</th><th align=\"left\">Monitor</th><th align=\"left\">Status</th><th align=\"left\">Error</th></tr>");
        foreach (var alert in alerts)
        {
            sb.AppendLine(
                $"<tr><td>{alert.OccurredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}</td>" +
                $"<td><b>{EventLabel(alert.Kind)}</b></td>" +
                $"<td>{WebUtility.HtmlEncode(alert.MonitorName)}</td>" +
                $"<td>{WebUtility.HtmlEncode(alert.Status)}</td>" +
                $"<td>{WebUtility.HtmlEncode(alert.Error ?? "")}</td></tr>");
        }
        sb.AppendLine("</table>");

        foreach (var alert in alerts)
        {
            sb.AppendLine("<hr>");
            sb.AppendLine(alert.Html);
        }

        return (subject, sb.ToString());
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (jobStorage is null)
        {
            await FlushCoreAsync(cancellationToken);
            return;
        }

        using var connection = jobStorage.GetConnection();
        IDisposable flushLock;
        try
        {
            flushLock = connection.AcquireDistributedLock(LockKey, LockTimeout);
        }
        catch (DistributedLockTimeoutException)
        {
            // Another flush is sending and may not see groups that became due meanwhile: try again shortly.
            logger.LogDebug("Alert digest flush skipped (lock busy).");
            if (jobs is not null)
                ScheduleFlush(jobs, BusyRetryDelay);
            return;
        }

        using (flushLock)
            await FlushCoreAsync(cancellationToken);
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        var options = AlertDigestOptions.From(config);
        var now = DateTimeOffset.UtcNow;
        var queued = await db.PendingAlerts
            .Where(a => a.SentAt == null && a.FailedAt == null)
            .ToListAsync(cancellationToken);

        var plan = Plan(queued, now, options);
        foreach (var group in plan.Due)
            await SendGroupAsync(group, now, cancellationToken);

        await DeleteOldAsync(now, cancellationToken);

        if (plan.NextCheckAt is { } next && jobs is not null)
            ScheduleFlush(jobs, next - now);
    }

    private async Task SendGroupAsync(IReadOnlyList<PendingAlert> group, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (subject, html) = Compose(group);
        try
        {
            // Settings may have changed since the alerts were queued; a failure here is retried like a failed send.
            if (!config.GetValue("Hawk:Email:Enabled", true))
                throw new InvalidOperationException("Email disabled (Hawk:Email:Enabled=false).");
            var from = config["Hawk:Email:From"] ?? config["Hawk:Resend:From"];
            if (string.IsNullOrWhiteSpace(from))
                throw new InvalidOperationException("Email from address is not configured (Hawk:Email:From).");

            await emailSender.SendAsync(from, group[0].Recipients.Split(','), subject, html, cancellationToken);
            foreach (var alert in group)
            {
                alert.SentAt = now;
                alert.SentInBatchOf = group.Count;
                alert.LastError = null;
            }

            logger.LogInformation("Sent {Count} alert(s) in one email ({Subject}).", group.Count, subject);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Includes the email client's own timeout (TaskCanceledException); only cancelling this job propagates.
            logger.LogWarning(ex, "Sending {Count} queued alert(s) failed.", group.Count);
            var error = Truncate(ex.Message, 2000);
            var givenUp = new List<Guid>();
            foreach (var alert in group)
            {
                alert.Attempts++;
                alert.LastError = error;
                if (now - alert.QueuedAt >= GiveUpAfter)
                {
                    alert.FailedAt = now;
                    givenUp.Add(alert.RunId);
                }
            }

            // The run was marked as alerted when the alert was queued; record that it never went out.
            if (givenUp.Count > 0)
            {
                var runs = await db.MonitorRuns.Where(r => givenUp.Contains(r.Id)).ToListAsync(cancellationToken);
                foreach (var run in runs)
                {
                    run.AlertSent = false;
                    run.AlertError = Truncate($"Alert email failed for 24 h, given up: {error}", 2000);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task DeleteOldAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - KeepSentFor;
        var old = db.PendingAlerts.Where(a => a.QueuedAt < cutoff && (a.SentAt != null || a.FailedAt != null));
        if (db.Database.IsRelational())
        {
            await old.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        db.PendingAlerts.RemoveRange(await old.ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string EventLabel(string kind) => kind switch
    {
        nameof(MonitorAlertKind.Failure) => "FAILED",
        nameof(MonitorAlertKind.FailureReminder) => "STILL FAILING",
        nameof(MonitorAlertKind.Recovered) => "RECOVERED",
        _ => WebUtility.HtmlEncode(kind),
    };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
