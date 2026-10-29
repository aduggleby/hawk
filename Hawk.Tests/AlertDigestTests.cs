using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Email;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawk.Tests;

public sealed class AlertDigestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly AlertDigestOptions Options = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));

    private static PendingAlert Alert(string name, MonitorAlertKind kind, int queuedSecondsAgo, string recipients = "a@example.com", int? occurredSecondsAgo = null) => new()
    {
        MonitorId = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        Kind = kind.ToString(),
        MonitorName = name,
        Status = kind == MonitorAlertKind.Recovered ? "200" : "500",
        Error = kind == MonitorAlertKind.Recovered ? null : "HTTP 500",
        Recipients = recipients,
        Subject = $"[single] {name}",
        Html = $"<p>details {name}</p>",
        OccurredAt = Now.AddSeconds(-(occurredSecondsAgo ?? queuedSecondsAgo)),
        QueuedAt = Now.AddSeconds(-queuedSecondsAgo),
    };

    [Fact]
    public void Group_waits_for_the_quiet_window_after_the_newest_alert()
    {
        var plan = AlertDigestSender.Plan([Alert("A", MonitorAlertKind.Failure, 40), Alert("B", MonitorAlertKind.Failure, 10)], Now, Options);

        Assert.Empty(plan.Due);
        Assert.Equal(Now.AddSeconds(20), plan.NextCheckAt);
    }

    [Fact]
    public void Group_is_due_once_quiet_for_the_window()
    {
        var plan = AlertDigestSender.Plan([Alert("B", MonitorAlertKind.Failure, 31), Alert("A", MonitorAlertKind.Failure, 45)], Now, Options);

        var group = Assert.Single(plan.Due);
        Assert.Equal(["A", "B"], group.Select(a => a.MonitorName));
        Assert.Null(plan.NextCheckAt);
    }

    [Fact]
    public void Max_delay_sends_a_group_that_keeps_receiving_alerts()
    {
        var plan = AlertDigestSender.Plan([Alert("A", MonitorAlertKind.Failure, 120), Alert("B", MonitorAlertKind.Failure, 1)], Now, Options);

        Assert.Equal(2, Assert.Single(plan.Due).Count);
    }

    [Fact]
    public void Recipient_sets_are_separate_groups()
    {
        var plan = AlertDigestSender.Plan(
        [
            Alert("A", MonitorAlertKind.Failure, 40, "a@example.com"),
            Alert("B", MonitorAlertKind.Failure, 40, "a@example.com,b@example.com"),
            Alert("C", MonitorAlertKind.Failure, 5, "c@example.com"),
        ], Now, Options);

        Assert.Equal(2, plan.Due.Count);
        Assert.Equal(Now.AddSeconds(25), plan.NextCheckAt);
    }

    [Fact]
    public void Recipient_key_is_normalized()
    {
        Assert.Equal("a@example.com,b@example.com", AlertDigestSender.RecipientKey([" B@example.com", "a@example.com", "b@EXAMPLE.com", ""]));
    }

    [Fact]
    public void Options_default_and_clamp()
    {
        Assert.Equal(new AlertDigestOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)), AlertDigestOptions.From(Config()));

        var off = AlertDigestOptions.From(Config(("Hawk:Alerting:DigestWindowSeconds", "0")));
        Assert.False(off.Enabled);

        var clamped = AlertDigestOptions.From(Config(("Hawk:Alerting:DigestWindowSeconds", "900"), ("Hawk:Alerting:DigestMaxDelaySeconds", "10")));
        Assert.Equal(TimeSpan.FromSeconds(600), clamped.Window);
        Assert.Equal(TimeSpan.FromSeconds(600), clamped.MaxDelay);
    }

    [Fact]
    public void Single_alert_keeps_its_own_email()
    {
        var alert = Alert("A", MonitorAlertKind.Failure, 40);

        var (subject, html) = AlertDigestSender.Compose([alert]);

        // Same subject and block, wrapped in the email card (no summary table).
        Assert.Equal(alert.Subject, subject);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains(alert.Html, html);
        Assert.DoesNotContain("alert summary", html);
    }

    [Fact]
    public void Summary_of_one_instant_shows_the_time_once()
    {
        var a = Alert("A", MonitorAlertKind.Failure, 40);
        var b = Alert("B", MonitorAlertKind.Failure, 40);
        b.OccurredAt = a.OccurredAt;

        var (_, html) = AlertDigestSender.Compose([a, b]);

        Assert.Contains("At 2026-10-29 11:59:20 UTC", html);
        Assert.DoesNotContain("Between", html);
    }

    [Fact]
    public void Summary_lists_every_event_in_order_including_a_flap()
    {
        var (subject, html) = AlertDigestSender.Compose(
        [
            Alert("Flappy", MonitorAlertKind.Failure, 50),
            Alert("Old <b>", MonitorAlertKind.FailureReminder, 45),
            Alert("Flappy", MonitorAlertKind.Recovered, 35),
        ]);

        Assert.Equal("[ALERT SUMMARY] 1 failed, 1 still failing, 1 recovered", subject);
        var failed = html.IndexOf("FAILED</td>", StringComparison.Ordinal);
        var recovered = html.IndexOf("RECOVERED</td>", StringComparison.Ordinal);
        Assert.True(failed >= 0 && recovered > failed);
        Assert.Contains("Old &lt;b&gt;", html);
        Assert.Contains("<p>details Flappy</p>", html);
        Assert.Contains("Between 2026-10-29 11:59:10 UTC and 2026-10-29 11:59:25 UTC", html);
        Assert.DoesNotContain(".0000000", html);
    }

    [Fact]
    public async Task Failed_send_is_retried_and_given_up_after_24_hours()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options);
        var run = new MonitorRun { MonitorId = Guid.NewGuid(), State = "completed", AlertSent = true };
        db.MonitorRuns.Add(run);
        var fresh = Alert("Fresh", MonitorAlertKind.Failure, 0);
        fresh.QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        fresh.Recipients = "x@example.com";
        var stale = Alert("Stale", MonitorAlertKind.Failure, 0);
        stale.QueuedAt = DateTimeOffset.UtcNow.AddHours(-25);
        stale.RunId = run.Id;
        db.PendingAlerts.AddRange(fresh, stale);
        await db.SaveChangesAsync();

        var sender = new AlertDigestSender(db, new FailingEmailSender(), Config(("Hawk:Email:From", "hawk@example.com")), NullLogger<AlertDigestSender>.Instance);
        await sender.FlushAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.PendingAlerts.ToDictionaryAsync(a => a.MonitorName);
        Assert.Null(rows["Fresh"].SentAt);
        Assert.Null(rows["Fresh"].FailedAt);
        Assert.Equal(1, rows["Fresh"].Attempts);
        Assert.Equal("smtp down", rows["Fresh"].LastError);
        Assert.NotNull(rows["Stale"].FailedAt);
        var failedRun = await db.MonitorRuns.SingleAsync();
        Assert.False(failedRun.AlertSent);
        Assert.Contains("smtp down", failedRun.AlertError);
    }

    [Fact]
    public async Task Send_timeout_on_one_group_does_not_stop_the_others()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options);
        var slow = Alert("Slow", MonitorAlertKind.Failure, 0, "slow@example.com");
        var fine = Alert("Fine", MonitorAlertKind.Failure, 0, "fine@example.com");
        slow.QueuedAt = fine.QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        db.PendingAlerts.AddRange(slow, fine);
        await db.SaveChangesAsync();

        var emails = new SelectiveEmailSender("slow@example.com");
        await new AlertDigestSender(db, emails, Config(("Hawk:Email:From", "hawk@example.com")), NullLogger<AlertDigestSender>.Instance)
            .FlushAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.PendingAlerts.ToDictionaryAsync(a => a.MonitorName);
        Assert.Null(rows["Slow"].SentAt);
        Assert.Equal(1, rows["Slow"].Attempts);
        Assert.NotNull(rows["Slow"].LastError);
        Assert.NotNull(rows["Fine"].SentAt);
        Assert.Equal(["[single] Fine"], emails.Subjects);
    }

    [Fact]
    public async Task Email_disabled_at_flush_time_keeps_alerts_queued()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options);
        var alert = Alert("A", MonitorAlertKind.Failure, 0);
        alert.QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        db.PendingAlerts.Add(alert);
        await db.SaveChangesAsync();

        var emails = new SelectiveEmailSender("none");
        await new AlertDigestSender(db, emails, Config(("Hawk:Email:From", "hawk@example.com"), ("Hawk:Email:Enabled", "false")), NullLogger<AlertDigestSender>.Instance)
            .FlushAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var row = await db.PendingAlerts.SingleAsync();
        Assert.Null(row.SentAt);
        Assert.Contains("Email disabled", row.LastError);
        Assert.Empty(emails.Subjects);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private sealed class SelectiveEmailSender(string timesOutFor) : IEmailSender
    {
        public List<string> Subjects { get; } = [];

        public Task SendAsync(string from, IReadOnlyList<string> to, string subject, string html, CancellationToken cancellationToken)
        {
            // HttpClient reports its own timeout as TaskCanceledException.
            if (to.Contains(timesOutFor))
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing.");
            Subjects.Add(subject);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingEmailSender : IEmailSender
    {
        public Task SendAsync(string from, IReadOnlyList<string> to, string subject, string html, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("smtp down");
    }
}
