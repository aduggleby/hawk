// <file>
// <summary>
// Details page data from the run-bucket rollup (U8): day cells and thresholds, uptime figures, "UP since", the status lead,
// the response chart, and the Details page for HTTP and heartbeat monitors (AE5: 90 day cells survive 1-day run retention).
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MonitorHistoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 7, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.NewGuid();

    private static MonitorEntity Http(DateTimeOffset? created = null) => new()
    {
        Id = Id, Name = "web", Url = "https://example.com", Method = "GET", IntervalSeconds = 60,
        CreatedAt = created ?? Now.AddDays(-200),
    };

    private static MonitorEntity Heartbeat() => new()
    {
        Name = "hb", Type = MonitorType.Heartbeat, Url = "", Method = "GET", IntervalSeconds = 3600, HeartbeatGraceSeconds = 300,
        HeartbeatToken = "0123456789abcdef0123456789abcdef", CreatedAt = Now.AddDays(-200),
    };

    private static MonitorRunBucket B(DateTimeOffset start, int runs, int failures, long totalMs = 100, Guid? monitorId = null) =>
        new() { MonitorId = monitorId ?? Id, BucketStart = start, RunCount = runs, FailureCount = failures, TotalDurationMs = totalMs };

    private static DateTimeOffset Day(int daysAgo, int hour = 3) =>
        new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-daysAgo).AddHours(hour);

    private static MonitorStatus Status(MonitorState state, MonitorHealth health, FailureSummary? failure = null) =>
        new(Id, state, health, failure);

    // ---- days and uptime ----

    [Fact]
    public void A_day_with_three_of_four_runs_passed_is_75_percent_and_a_day_without_runs_is_null()
    {
        var days = MonitorHistoryService.Days([B(Day(2), 3, 1), B(Day(2, 4), 1, 0)], Now);
        var withRuns = days.Single(d => d.Day == Day(2, 0));
        Assert.Equal(75, withRuns.UptimePercent);
        Assert.Equal(4, withRuns.Runs);
        Assert.Equal(DayState.Down, withRuns.State);

        var empty = days.Single(d => d.Day == Day(3, 0));
        Assert.Null(empty.UptimePercent);
        Assert.Equal(DayState.None, empty.State);
    }

    [Fact]
    public void Future_buckets_are_ignored_by_uptime_days_and_the_strip()
    {
        var future = RunRollupStart(Now).AddHours(2);
        var buckets = new List<MonitorRunBucket> { B(RunRollupStart(Now), 4, 0), B(future, 10, 10) };

        Assert.Equal(new UptimeFigures(100, 100, 100), MonitorHistoryService.Uptime(buckets, Now));
        Assert.Equal(4, MonitorHistoryService.Days(buckets, Now).Sum(d => d.Runs));
        Assert.Equal(0, StatusStrip.Build(buckets, Now, heartbeat: false).FailedCount);
        Assert.Null(MonitorHistoryService.Build(Http(), buckets, Now).NewestFailingBucket);
    }

    private static DateTimeOffset RunRollupStart(DateTimeOffset t) => RunBucketRollup.BucketStartOf(t);

    [Fact]
    public void There_are_always_90_day_cells_oldest_first_ending_today()
    {
        foreach (var buckets in new[] { new List<MonitorRunBucket>(), [B(Day(0), 1, 0)] })
        {
            var days = MonitorHistoryService.Days(buckets, Now);
            Assert.Equal(90, days.Count);
            Assert.Equal(Day(89, 0), days[0].Day);
            Assert.Equal(Day(0, 0), days[^1].Day);
            Assert.Equal(days.Select(d => d.Day).OrderBy(d => d), days.Select(d => d.Day));
        }
    }

    [Fact]
    public void Day_colors_follow_the_four_thresholds()
    {
        Assert.Equal(DayState.Up, MonitorHistoryService.DayStateOf(100));
        Assert.Equal(DayState.UpDim, MonitorHistoryService.DayStateOf(99.5));
        Assert.Equal(DayState.DownTint, MonitorHistoryService.DayStateOf(97));
        Assert.Equal(DayState.Down, MonitorHistoryService.DayStateOf(90));
        Assert.Equal(DayState.UpDim, MonitorHistoryService.DayStateOf(99));
        Assert.Equal(DayState.DownTint, MonitorHistoryService.DayStateOf(95));
        Assert.Equal(DayState.Down, MonitorHistoryService.DayStateOf(94.99));
        Assert.Equal(DayState.None, MonitorHistoryService.DayStateOf(null));

        // 1 failure in 1000 runs must not read as 100.
        Assert.Equal(99.9, MonitorHistoryService.Percent(1000, 1));
        Assert.Equal(99.99, MonitorHistoryService.Percent(100000, 10));
        Assert.Null(MonitorHistoryService.Percent(0, 0));
        Assert.Equal("99.99%", MonitorHistoryService.FormatPercent(99.9999));
        Assert.Equal("100%", MonitorHistoryService.FormatPercent(100));
        Assert.Equal("—", MonitorHistoryService.FormatPercent(null));
    }

    [Fact]
    public void Uptime_figures_cover_24_hours_7_days_and_30_days_and_are_null_without_runs()
    {
        Assert.Equal(new UptimeFigures(null, null, null), MonitorHistoryService.Uptime([], Now));

        var figures = MonitorHistoryService.Uptime(
        [
            B(Now.AddHours(-1), 4, 1),       // in all windows
            B(Now.AddDays(-3), 4, 0),        // 7 d and 30 d
            B(Now.AddDays(-20), 8, 4),       // 30 d only
            B(Now.AddDays(-40), 100, 100),   // outside
        ], Now);
        Assert.Equal(75, figures.Last24h);
        Assert.Equal(87.5, figures.Last7d);
        Assert.Equal(68.75, figures.Last30d);
    }

    // ---- lead ----

    [Fact]
    public void The_lead_reads_down_up_paused_disabled_pending_and_waiting()
    {
        var failingSince = Now.AddHours(-3);
        var monitor = Http();
        var history = MonitorHistoryService.Build(monitor, [], Now);

        var down = MonitorHistoryService.Lead(monitor,
            Status(MonitorState.Active, MonitorHealth.Failing, new FailureSummary(Now.AddMinutes(-1), 500, "x", 3, failingSince)), history);
        Assert.Equal((LeadKind.Down, "DOWN", "since", (DateTimeOffset?)failingSince), (down.Kind, down.Word, down.Caption, down.Time));

        var up = MonitorHistoryService.Lead(monitor, Status(MonitorState.Active, MonitorHealth.Ok), history with { UpSince = Now.AddDays(-2) });
        Assert.Equal((LeadKind.Up, "UP", "since", (DateTimeOffset?)Now.AddDays(-2)), (up.Kind, up.Word, up.Caption, up.Time));

        var old = MonitorHistoryService.Lead(monitor, Status(MonitorState.Active, MonitorHealth.Ok), history);
        Assert.True(history.UpForMoreThanHistory);
        Assert.Equal("for more than 90 days", old.Caption);
        Assert.Null(old.Time);

        Assert.Equal("PAUSED", MonitorHistoryService.Lead(monitor, Status(MonitorState.Paused, MonitorHealth.Failing), history).Word);
        Assert.Equal("DISABLED", MonitorHistoryService.Lead(monitor, Status(MonitorState.Disabled, MonitorHealth.Ok), history).Word);

        monitor.NextRunAt = Now.AddMinutes(1);
        var pending = MonitorHistoryService.Lead(monitor, Status(MonitorState.Active, MonitorHealth.Unknown), history);
        Assert.Equal((LeadKind.Pending, "PENDING", "first check at", (DateTimeOffset?)Now.AddMinutes(1)), (pending.Kind, pending.Word, pending.Caption, pending.Time));

        var hb = Heartbeat();
        hb.NextRunAt = Now.AddHours(1);
        var waiting = MonitorHistoryService.Lead(hb, Status(MonitorState.Active, MonitorHealth.Unknown), MonitorHistoryService.Build(hb, [], Now));
        Assert.Equal((LeadKind.Waiting, "WAITING", "first ping due by", (DateTimeOffset?)Now.AddHours(1)), (waiting.Kind, waiting.Word, waiting.Caption, waiting.Time));

        // An unrecorded heartbeat miss is down since its deadline.
        hb.NextRunAt = Now.AddMinutes(-5);
        var missed = MonitorHistoryService.Lead(hb, Status(MonitorState.Active, MonitorHealth.Overdue), MonitorHistoryService.Build(hb, [], Now));
        Assert.Equal((LeadKind.Down, (DateTimeOffset?)Now.AddMinutes(-5)), (missed.Kind, missed.Time));
    }

    // ---- up since ----

    [Fact]
    public void Up_since_reads_the_failing_bucket_end_without_a_failure_inside_history_only_the_creation_time_or_more_than_90_days()
    {
        var failing = Day(10, 5);
        var fromBucket = MonitorHistoryService.Build(Http(), [B(failing, 4, 2), B(Day(12, 1), 3, 3), B(Day(5), 10, 0)], Now);
        Assert.Equal(failing, fromBucket.NewestFailingBucket);
        Assert.Equal(failing.AddMinutes(15), fromBucket.UpSince);
        Assert.False(fromBucket.UpForMoreThanHistory);

        var young = Http(created: Now.AddDays(-5));
        var fromCreated = MonitorHistoryService.Build(young, [B(Day(2), 10, 0)], Now);
        Assert.Null(fromCreated.NewestFailingBucket);
        Assert.Equal(young.CreatedAt, fromCreated.UpSince);

        var old = MonitorHistoryService.Build(Http(), [B(Day(2), 10, 0)], Now);
        Assert.Null(old.UpSince);
        Assert.True(old.UpForMoreThanHistory);
    }

    [Fact]
    public async Task Up_since_with_pruned_runs_reads_from_the_bucket_and_with_retained_runs_from_the_first_success_after_the_last_failure()
    {
        using var db = NewDb();
        await AssertUpSinceAsync(db);
    }

    [SkippableFact]
    public async Task Up_since_refinement_runs_on_postgres()
    {
        if (fixture.UnavailableReason is not null && Environment.GetEnvironmentVariable("HAWK_REQUIRE_POSTGRES_TESTS") == "1")
            throw new InvalidOperationException($"HAWK_REQUIRE_POSTGRES_TESTS=1 but {fixture.UnavailableReason}");
        Skip.If(fixture.UnavailableReason is not null, fixture.UnavailableReason);
        await using var app = await SecurityTestApplication.CreateAsync(fixture.NewDatabaseConnectionString());
        await using var scope = app.App.Services.CreateAsyncScope();
        await AssertUpSinceAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static async Task AssertUpSinceAsync(ApplicationDbContext db)
    {
        // Retention of 1 day: the failing bucket 10 days ago has no runs left.
        var pruned = new MonitorEntity { Name = "pruned", Url = "https://a", Method = "GET", CreatedAt = Now.AddDays(-200), RunRetentionDays = 1 };
        var kept = new MonitorEntity { Name = "kept", Url = "https://b", Method = "GET", CreatedAt = Now.AddDays(-200) };
        db.Monitors.AddRange(pruned, kept);
        var failBucket = Day(10, 5);
        foreach (var m in new[] { pruned, kept })
            db.MonitorRunBuckets.Add(B(failBucket, 4, 2, monitorId: m.Id));

        MonitorRun Run(Guid id, DateTimeOffset at, bool ok, string state = "completed") => new()
        {
            MonitorId = id, StartedAt = at, FinishedAt = at, Success = ok, State = state, Reason = "schedule", RequestMethod = "GET", RequestUrl = "https://x",
        };
        db.MonitorRuns.AddRange(
            Run(kept.Id, failBucket.AddMinutes(1), false),
            Run(kept.Id, failBucket.AddMinutes(9), false),
            Run(kept.Id, failBucket.AddMinutes(10), true),   // first success after the last failed run
            Run(kept.Id, failBucket.AddMinutes(2), true),    // passed between failures, must be ignored
            Run(kept.Id, failBucket.AddMinutes(12), true, state: "queued"),
            Run(kept.Id, Day(3), true));
        await db.SaveChangesAsync();

        var service = new MonitorHistoryService(db);
        var fromBucket = await service.GetAsync(pruned, Now, refineUpSince: true, CancellationToken.None);
        Assert.Equal(failBucket.AddMinutes(15), fromBucket.UpSince);

        var refined = await service.GetAsync(kept, Now, refineUpSince: true, CancellationToken.None);
        Assert.Equal(failBucket.AddMinutes(10), refined.UpSince);

        var unrefined = await service.GetAsync(kept, Now, refineUpSince: false, CancellationToken.None);
        Assert.Equal(failBucket.AddMinutes(15), unrefined.UpSince);

        // Uptime for the API comes from the same buckets.
        var figures = await service.GetUptimeAsync(kept.Id, Now, CancellationToken.None);
        Assert.Equal(new UptimeFigures(null, null, 50), figures);
        Assert.Equal(figures, refined.Uptime);
    }

    // ---- chart ----

    [Fact]
    public void The_chart_is_null_without_data_and_breaks_the_line_at_gaps()
    {
        Assert.Null(MonitorHistoryService.Chart(StatusStrip.Build([], Now, heartbeat: false)));

        var last = StatusStrip.WindowStart(Now) + TimeSpan.FromMinutes(15 * 95);
        var chart = MonitorHistoryService.Chart(StatusStrip.Build(
        [
            B(last, 2, 1, 400), B(last.AddMinutes(-15), 1, 0, 200), B(last.AddMinutes(-45), 1, 0, 100), B(last.AddMinutes(-90), 1, 0, 100),
        ], Now, heartbeat: false))!;
        Assert.Equal(4, chart.Points.Count);
        Assert.Single(chart.Paths);
        Assert.StartsWith("M", chart.Paths[0]);
        Assert.Equal(1, chart.FailedCount);
        Assert.Equal(2, chart.Points.Count(p => p.Isolated));
        Assert.Equal(3, chart.Grid.Count);
        Assert.True(chart.Points[^1].Y < chart.Points[0].Y);
    }

    // ---- Details page ----

    [Fact]
    public async Task Details_shows_90_day_cells_after_one_day_retention_and_no_ping_url_for_http()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        var m = new MonitorEntity
        {
            Name = "Retained", Url = "https://example.com", Method = "GET", IntervalSeconds = 60, RunRetentionDays = 1,
            CreatedAt = Now.AddDays(-200), LastRunAt = DateTimeOffset.UtcNow, Enabled = true,
        };
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Monitors.Add(m);
            db.MonitorRunBuckets.AddRange(
                B(today.AddDays(-60).AddHours(2), 10, 0, monitorId: m.Id),
                B(today.AddDays(-30).AddHours(2), 10, 3, monitorId: m.Id));
            await db.SaveChangesAsync();
        }

        var html = await app.Client.GetStringAsync($"/Monitors/Details/{m.Id}");
        Assert.Equal(90, System.Text.RegularExpressions.Regex.Matches(html, "data-day=\"").Count);
        Assert.Contains($"title=\"{today.AddDays(-60):yyyy-MM-dd}: 100% (0 of 10 runs failed)\"", html);
        Assert.Contains("70% (3 of 10 runs failed)", html);
        Assert.Contains("data-testid=\"response-chart\"", html);
        Assert.DoesNotContain("ping-timeline", html);
        Assert.DoesNotContain("/ping/", html);
    }

    [Fact]
    public async Task Details_shows_empty_states_for_a_new_monitor_and_ping_url_and_timeline_for_a_heartbeat()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        var http = new MonitorEntity { Name = "Fresh", Url = "https://example.com", Method = "GET", IntervalSeconds = 60, NextRunAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        var hb = new MonitorEntity
        {
            Name = "Nightly", Type = MonitorType.Heartbeat, Url = "", Method = "GET", IntervalSeconds = 3600, HeartbeatGraceSeconds = 300,
            HeartbeatToken = "fedcba9876543210fedcba9876543210", NextRunAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Monitors.AddRange(http, hb);
            await db.SaveChangesAsync();
        }

        var httpHtml = await app.Client.GetStringAsync($"/Monitors/Details/{http.Id}");
        Assert.Contains("PENDING", httpHtml);
        Assert.Contains("first check at", httpHtml);
        Assert.Contains("No data yet", httpHtml);
        Assert.Matches("uptime-24h\">(\u2014|&#x2014;)<", httpHtml);
        Assert.DoesNotContain("status-strip", httpHtml);

        var hbHtml = await app.Client.GetStringAsync($"/Monitors/Details/{hb.Id}");
        Assert.Contains("WAITING", hbHtml);
        Assert.Contains("first ping due by", hbHtml);
        Assert.Contains("/ping/fedcba9876543210fedcba9876543210", hbHtml);
        Assert.Contains("data-testid=\"ping-timeline\"", hbHtml);
        Assert.DoesNotContain("response-chart", hbHtml);
        Assert.DoesNotContain("Run now", hbHtml);
    }

    [Fact]
    public async Task Details_shows_down_with_since_and_failing_cells_in_the_strip()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        var now = DateTimeOffset.UtcNow;
        var m = new MonitorEntity { Name = "Broken", Url = "https://example.com", Method = "GET", IntervalSeconds = 60, LastRunAt = now, CreatedAt = now.AddDays(-3) };
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Monitors.Add(m);
            db.MonitorRuns.Add(new MonitorRun
            {
                MonitorId = m.Id, StartedAt = now, FinishedAt = now, Success = false, State = "completed", Reason = "schedule",
                RequestMethod = "GET", RequestUrl = "https://example.com", ErrorMessage = "boom",
            });
            db.MonitorRunBuckets.Add(B(RunBucketRollup.BucketStartOf(now), 1, 1, monitorId: m.Id));
            await db.SaveChangesAsync();
        }

        var html = await app.Client.GetStringAsync($"/Monitors/Details/{m.Id}");
        Assert.Contains("data-testid=\"status-word\"", html);
        Assert.Matches("status-word[^>]*>DOWN<", html);
        Assert.Contains("data-testid=\"status-since\"", html);
        Assert.Contains("data-testid=\"uptime-24h\">0%<", html);
        Assert.Contains("<circle class=\"pt fail\"", html);
    }

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options);
}
