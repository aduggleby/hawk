// <file>
// <summary>
// Integration tests against a real PostgreSQL 17 (Testcontainers, needs Docker; skipped when Docker is unavailable,
// failed instead when HAWK_REQUIRE_POSTGRES_TESTS=1).
// Each test gets its own database, migrated with the PostgreSQL migration set exactly as at startup.
// Covers behaviour that the InMemory provider cannot: ExecuteUpdate/ExecuteDelete, transactions, the Edit page
// concurrency flow, NULL ordering in the scheduler, Hangfire.PostgreSql distributed locks, case-insensitive
// lookups and UTC-only timestamptz writes.
// </summary>
// </file>

using System.Net;
using System.Text;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using SettingsModel = Hawk.Web.Areas.Identity.Pages.Account.Manage.SettingsModel;
using Hawk.Web.Data;
using Hawk.Web.Data.ApiKeys;
using Hawk.Web.Infrastructure;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services;
using Hawk.Web.Services.Email;
using Hawk.Web.Services.ApiKeys;
using Hawk.Web.Services.Import;
using Hawk.Web.Services.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Hawk.Web.Data.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

/// <summary>
/// Shares one <see cref="PostgresFixture"/> across every PostgreSQL test class, so the container is started (or found
/// unavailable, which can take about a minute without Docker) once per test run instead of once per class.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

/// <summary>
/// Starts one PostgreSQL container for all PostgreSQL test classes (<see cref="PostgresCollection"/>).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>Why the tests are skipped (Docker unavailable), or null.</summary>
    public string? UnavailableReason { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                // One container serves every PostgreSQL test class; each test opens its own database and pool.
                .WithCommand("-c", "max_connections=1000")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            UnavailableReason = $"PostgreSQL test container unavailable (Docker required): {ex.Message}";
        }
    }

    /// <summary>Connection string for a new, not yet existing database on the shared server.</summary>
    public string NewDatabaseConnectionString() =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            Database = $"hawk_test_{Guid.NewGuid():N}"
        }.ToString();

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>
/// PostgreSQL provider integration tests.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresIntegrationTests(PostgresFixture fixture)
{
    private static readonly IHostEnvironment Production = new FakeEnv("Production");

    [SkippableFact]
    public async Task Migrations_apply_and_seeders_run()
    {
        await using var app = await CreateAppAsync();

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_InitialPostgres", StringComparison.Ordinal));

        // Identity lookups use normalized columns, so they are case-insensitive on PostgreSQL as well.
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.True((await users.CreateAsync(new IdentityUser { UserName = "Mixed@Example.com", Email = "Mixed@Example.com" }, "password123")).Succeeded);
        Assert.NotNull(await users.FindByEmailAsync("mixed@example.COM"));
    }

    [SkippableFact]
    public async Task Edit_does_not_conflict_with_a_run_that_finished_while_the_form_was_open()
    {
        await using var app = await CreateAppAsync();
        var id = await AddMonitorAsync(app);

        var form = await LoadEditFormAsync(app, id);

        // The scheduler tick and a full executor run touch NextRunAt/LastRunAt (set-based) and the alert state.
        await TickSchedulerAsync(app);
        var result = await ExecuteAsync(app, id, HttpStatusCode.OK);
        Assert.NotNull(result);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var m = await db.Monitors.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.NotNull(m.NextRunAt);
            Assert.NotNull(m.LastRunAt);
            Assert.Equal(form.ConcurrencyStamp, m.ConcurrencyStamp.ToString("D"));
            Assert.Equal(1, await db.MonitorRuns.CountAsync(r => r.MonitorId == id && r.State == "completed"));
        }

        form.Name = "Edited after run";
        var (post, flash) = await PostEditAsync(app, id, form);

        var redirect = Assert.IsType<RedirectToPageResult>(post);
        Assert.Equal("/Monitors/Details", redirect.PageName);
        Assert.Null(flash);

        await using var check = app.Services.CreateAsyncScope();
        var saved = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Edited after run", saved.Name);
        Assert.NotEqual(form.ConcurrencyStamp, saved.ConcurrencyStamp.ToString("D"));
    }

    [SkippableFact]
    public async Task Edit_conflicts_with_a_concurrent_edit()
    {
        await using var app = await CreateAppAsync();
        var id = await AddMonitorAsync(app);

        var formA = await LoadEditFormAsync(app, id);
        var formB = await LoadEditFormAsync(app, id);

        formB.Name = "Saved by B";
        var (postB, _) = await PostEditAsync(app, id, formB);
        Assert.Equal("/Monitors/Details", Assert.IsType<RedirectToPageResult>(postB).PageName);

        formA.Name = "Saved by A";
        var (postA, flash) = await PostEditAsync(app, id, formA);
        Assert.Equal("/Monitors/Edit", Assert.IsType<RedirectToPageResult>(postA).PageName);
        Assert.Contains("changed by someone else", flash, StringComparison.Ordinal);

        await using var check = app.Services.CreateAsyncScope();
        var saved = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Saved by B", saved.Name);
    }

    [SkippableFact]
    public async Task Edit_conflicts_with_a_pause_from_the_index_page()
    {
        await using var app = await CreateAppAsync();
        var id = await AddMonitorAsync(app);
        var form = await LoadEditFormAsync(app, id);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var index = NewIndex(scope, new RecordingJobClient());
            index.SelectedMonitorIds = [id];
            index.BatchAction = "pause";
            InitPage(index);
            await index.OnPostBatchAsync(CancellationToken.None);
        }

        form.Name = "Edited after pause";
        var (post, flash) = await PostEditAsync(app, id, form);
        Assert.Equal("/Monitors/Edit", Assert.IsType<RedirectToPageResult>(post).PageName);
        Assert.NotNull(flash);
    }

    [SkippableFact]
    public async Task Scheduler_enqueues_never_scheduled_monitors_before_a_full_batch_of_overdue_ones()
    {
        await using var app = await CreateAppAsync();
        var overdue = DateTimeOffset.UtcNow.AddMinutes(-5);
        Guid newId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < 60; i++)
                db.Monitors.Add(NewMonitor($"Overdue {i}", overdue));
            var fresh = NewMonitor("Never scheduled", null);
            db.Monitors.Add(fresh);
            newId = fresh.Id;
            await db.SaveChangesAsync();
        }

        var jobs = await TickSchedulerAsync(app);

        var enqueued = jobs.EnqueuedMonitorIds();
        Assert.Equal(50, enqueued.Count);
        Assert.Contains(newId, enqueued);
    }

    [SkippableFact]
    public async Task Alert_lock_times_out_while_held_and_alert_state_stamp_changes_only_with_state()
    {
        await using var app = await CreateAppAsync();
        var storage = app.Services.GetRequiredService<JobStorage>();
        var id = await AddMonitorAsync(app);
        var key = $"hawk:monitor-alert:{id}";

        using (var holder = storage.GetConnection())
        using (holder.AcquireDistributedLock(key, TimeSpan.FromSeconds(60)))
        {
            using var other = storage.GetConnection();
            // The executor and scheduler catch DistributedLockTimeoutException; Hangfire.PostgreSql's exception derives from it.
            Assert.ThrowsAny<DistributedLockTimeoutException>(() => other.AcquireDistributedLock(key, TimeSpan.FromSeconds(1)));
        }

        using (var again = storage.GetConnection())
        using (again.AcquireDistributedLock(key, TimeSpan.FromSeconds(5)))
        {
        }

        await ExecuteAsync(app, id, HttpStatusCode.OK);
        var afterFirstSuccess = await AlertStampAsync(app, id);
        await ExecuteAsync(app, id, HttpStatusCode.OK);
        Assert.Equal(afterFirstSuccess, await AlertStampAsync(app, id));

        await ExecuteAsync(app, id, HttpStatusCode.InternalServerError);
        Assert.NotEqual(afterFirstSuccess, await AlertStampAsync(app, id));
    }

    [SkippableFact]
    public async Task StatusCake_import_is_case_insensitive_and_writes_utc_timestamps()
    {
        await using var app = await CreateAppAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var importer = new StatusCakeImporter(db, Production, NullLogger<StatusCakeImporter>.Instance);

        foreach (var name in new[] { "Example Site", "EXAMPLE SITE" })
        {
            await using var ms = new MemoryStream(Encoding.UTF8.GetBytes($$"""
                { "data": [ { "id": "AbC1", "name": "{{name}}", "website_url": "https://example.com", "check_rate": 60 } ] }
                """));
            await importer.ImportTestsAsync(ms, null, CancellationToken.None);
        }
        Assert.Equal(1, await db.Monitors.CountAsync());

        var stampBefore = await db.Monitors.Select(m => m.ConcurrencyStamp).SingleAsync();

        // Non-UTC offset in the source: Npgsql would reject it for timestamptz without normalisation.
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("""
            { "test_id": "abc1", "data": [ { "status": "down", "status_code": 503, "triggered_at": "2026-02-09T02:00:00+02:00" } ] }
            """)))
        {
            var res = await importer.ImportAlertsAsync(ms, CancellationToken.None);
            Assert.Equal(1, res.RunsCreated);
        }

        var run = await db.MonitorRuns.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 2, 9, 0, 0, 0, TimeSpan.Zero), run.StartedAt);

        // LastRunAt is advanced set-based (bookkeeping): the concurrency stamp stays untouched.
        var monitor = await db.Monitors.AsNoTracking().SingleAsync();
        Assert.Equal(run.StartedAt, monitor.LastRunAt);
        Assert.Equal(stampBefore, monitor.ConcurrencyStamp);
    }

    [SkippableFact]
    public async Task Run_with_binary_response_body_is_saved()
    {
        await using var app = await CreateAppAsync();
        var id = await AddMonitorAsync(app);

        // PostgreSQL rejects NUL in text; the executor must sanitise the snippet or the run (and alert state) is lost.
        var result = await ExecuteAsync(app, id, HttpStatusCode.InternalServerError, "PNG\0\0binary");
        Assert.NotNull(result);

        await using var scope = app.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorRuns.AsNoTracking().SingleAsync(r => r.MonitorId == id);
        Assert.Equal("completed", run.State);
        Assert.DoesNotContain('\0', run.ResponseSnippet!);
    }

    [SkippableFact]
    public async Task Monitors_index_loads_sorted_with_failing_group_and_delete_works()
    {
        await using var app = await CreateAppAsync();
        Guid failingId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Monitors.AddRange(NewMonitor("beta", null), NewMonitor("Alpha", null), NewMonitor("charlie", null));
            await db.SaveChangesAsync();
            failingId = await db.Monitors.Where(m => m.Name == "beta").Select(m => m.Id).SingleAsync();
        }
        await ExecuteAsync(app, failingId, HttpStatusCode.OK);
        await ExecuteAsync(app, failingId, HttpStatusCode.InternalServerError);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var index = NewIndex(scope, new RecordingJobClient());
            InitPage(index);
            await index.OnGetAsync(CancellationToken.None);

            Assert.Equal(["Alpha", "beta", "charlie"], index.Monitors.Select(m => m.Name));
            Assert.Equal(failingId, Assert.Single(index.FailingMonitors).Id);
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var delete = new DeleteModel(db, new MonitorEditor(db));
            InitPage(delete);
            Assert.IsType<RedirectToPageResult>(await delete.OnPostAsync(failingId, CancellationToken.None));
            Assert.False(await db.Monitors.AnyAsync(m => m.Id == failingId));
            Assert.False(await db.MonitorRuns.AnyAsync(r => r.MonitorId == failingId));
            Assert.False(await db.MonitorAlertStates.AnyAsync(s => s.MonitorId == failingId));
        }
    }

    [SkippableFact]
    public async Task Ready_check_reports_database_reachability()
    {
        await using var app = await CreateAppAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var check = new DatabaseHealthCheck(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
            Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        }

        var unreachable = new DbContextOptionsBuilder<PostgresApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2")
            .Options;
        await using var badDb = new PostgresApplicationDbContext(unreachable);
        Assert.Equal(HealthStatus.Unhealthy, (await new DatabaseHealthCheck(badDb).CheckHealthAsync(new HealthCheckContext())).Status);
    }


    // ---- heartbeat monitors ----

    [SkippableFact]
    public async Task Heartbeat_ping_miss_ping_records_ok_then_fail_with_alert_then_recovery()
    {
        await using var app = await CreateAppAsync();
        var emails = new RecordingEmailSender();
        var (id, token) = await AddHeartbeatAsync(app, period: 60, grace: 30);

        var before = DateTimeOffset.UtcNow;
        var ping = await PingAsync(app, token, emails);
        Assert.Equal((200, "OK"), ping);

        var m = await GetMonitorAsync(app, id);
        Assert.NotNull(m.LastPingAt);
        Assert.Equal(TimeSpan.Zero, m.LastPingAt!.Value.Offset);
        Assert.InRange(m.NextRunAt!.Value, before.AddSeconds(90), DateTimeOffset.UtcNow.AddSeconds(91));
        var okRun = Assert.Single(await RunsAsync(app, id));
        Assert.True(okRun.Success);
        Assert.Equal("completed", okRun.State);
        Assert.Equal("ping", okRun.Reason);
        Assert.Equal("GET", okRun.RequestMethod);
        Assert.DoesNotContain(token, okRun.RequestUrl);
        Assert.Contains("198.51.100.7", okRun.RequestHeadersJson);
        Assert.Contains("curl/8", okRun.RequestHeadersJson);
        Assert.DoesNotContain(token, okRun.RequestHeadersJson);

        // The deadline passes without a ping.
        await SetNextRunAsync(app, id, DateTimeOffset.UtcNow.AddSeconds(-1));
        await MissAsync(app, id, emails);

        m = await GetMonitorAsync(app, id);
        Assert.InRange(m.NextRunAt!.Value, DateTimeOffset.UtcNow.AddSeconds(55), DateTimeOffset.UtcNow.AddSeconds(61));
        var failRun = (await RunsAsync(app, id)).Single(r => !r.Success);
        Assert.Contains("No ping received since", failRun.ErrorMessage);
        Assert.Equal("schedule", failRun.Reason);
        var failure = Assert.Single(emails.Sent);
        Assert.StartsWith("[ALERT FAIL]", failure.Subject);
        Assert.Contains("Heartbeat missed", failure.Html);
        Assert.DoesNotContain("status=", failure.Html);
        Assert.True(failRun.AlertSent);

        // A duplicate job for the same miss (the scheduler enqueues until the deadline moves) is a no-op.
        await MissAsync(app, id, emails);
        Assert.Single(emails.Sent);
        Assert.Single(await RunsAsync(app, id), r => !r.Success);

        // Raising the threshold during the alerted incident must not suppress the recovery email.
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
                .Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AlertAfterConsecutiveFailures, 3));

        Assert.Equal((200, "OK"), await PingAsync(app, token, emails));
        Assert.Equal(2, emails.Sent.Count);
        Assert.StartsWith("[ALERT RECOVERED]", emails.Sent[1].Subject);
        Assert.Contains("Heartbeat recovered", emails.Sent[1].Html);

        await using var check = app.Services.CreateAsyncScope();
        var state = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorAlertStates.AsNoTracking().SingleAsync(x => x.MonitorId == id);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.False(state.PendingRecoveryAlert);
    }

    [SkippableFact]
    public async Task A_stale_missed_job_after_resume_does_nothing_for_pinged_and_never_pinged_heartbeats()
    {
        await using var app = await CreateAppAsync();
        var emails = new RecordingEmailSender();
        var past = DateTimeOffset.UtcNow.AddHours(-3);
        var (neverPinged, _) = await AddHeartbeatAsync(app, period: 60, grace: 60, nextRunAt: past, paused: true);
        var (pinged, token) = await AddHeartbeatAsync(app, period: 3600, grace: 300, nextRunAt: past, paused: true);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
                .Where(x => x.Id == pinged).ExecuteUpdateAsync(x => x.SetProperty(y => y.LastPingAt, past));
        var http = await AddMonitorAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
                .Where(x => x.Id == http).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsPaused, true));

        // Batch resume of one heartbeat, then Resume all for the rest.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var index = NewIndex(scope, new RecordingJobClient());
            index.SelectedMonitorIds = [neverPinged];
            index.BatchAction = "resume";
            InitPage(index);
            await index.OnPostBatchAsync(CancellationToken.None);
        }
        var first = await GetMonitorAsync(app, neverPinged);
        Assert.False(first.IsPaused);
        Assert.InRange(first.NextRunAt!.Value, DateTimeOffset.UtcNow.AddSeconds(100), DateTimeOffset.UtcNow.AddSeconds(121));
        Assert.True((await GetMonitorAsync(app, pinged)).IsPaused);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var index = NewIndex(scope, new RecordingJobClient());
            InitPage(index);
            await index.OnPostResumeAllAsync(CancellationToken.None);
        }
        var second = await GetMonitorAsync(app, pinged);
        Assert.False(second.IsPaused);
        Assert.InRange(second.NextRunAt!.Value, DateTimeOffset.UtcNow.AddSeconds(3800), DateTimeOffset.UtcNow.AddSeconds(3901));
        var resumedHttp = await GetMonitorAsync(app, http);
        Assert.False(resumedHttp.IsPaused);
        Assert.Null(resumedHttp.NextRunAt);

        // Jobs queued before the pause run now: neither may fail inside the new period + grace.
        await MissAsync(app, neverPinged, emails);
        await MissAsync(app, pinged, emails);
        Assert.Empty(await RunsAsync(app, neverPinged));
        Assert.Empty(await RunsAsync(app, pinged));
        Assert.Empty(emails.Sent);
        Assert.Equal(first.NextRunAt, (await GetMonitorAsync(app, neverPinged)).NextRunAt);
        Assert.NotEmpty(token);
    }

    [SkippableFact]
    public async Task A_ping_between_the_deadline_and_the_missed_job_leaves_no_failure()
    {
        await using var app = await CreateAppAsync();
        var emails = new RecordingEmailSender();
        var (id, token) = await AddHeartbeatAsync(app, period: 60, grace: 60, nextRunAt: DateTimeOffset.UtcNow.AddSeconds(-5));

        Assert.Equal(200, (await PingAsync(app, token, emails)).Status);
        var afterPing = (await GetMonitorAsync(app, id)).NextRunAt;
        await MissAsync(app, id, emails);

        var run = Assert.Single(await RunsAsync(app, id));
        Assert.True(run.Success);
        Assert.Equal(afterPing, (await GetMonitorAsync(app, id)).NextRunAt);
        Assert.Empty(emails.Sent);
    }

    [SkippableFact]
    public async Task Scheduler_enqueues_overdue_heartbeats_but_never_writes_their_deadline()
    {
        await using var app = await CreateAppAsync();
        var overdue = DateTimeOffset.UtcNow.AddMinutes(-5);
        var (id, _) = await AddHeartbeatAsync(app, period: 60, grace: 60, nextRunAt: overdue);
        var http = await AddMonitorAsync(app);
        var deadlineBefore = (await GetMonitorAsync(app, id)).NextRunAt;

        var jobs = await TickSchedulerAsync(app);

        var enqueued = jobs.EnqueuedMonitorIds();
        Assert.Contains(id, enqueued);
        Assert.Contains(http, enqueued);
        Assert.Equal(deadlineBefore, (await GetMonitorAsync(app, id)).NextRunAt);
        Assert.NotNull((await GetMonitorAsync(app, http)).NextRunAt);

        // Still overdue on the next tick: enqueued again (duplicates are harmless), still untouched.
        Assert.Contains(id, (await TickSchedulerAsync(app)).EnqueuedMonitorIds());
        Assert.Equal(deadlineBefore, (await GetMonitorAsync(app, id)).NextRunAt);
    }

    [SkippableFact]
    public async Task A_ping_for_a_paused_or_disabled_heartbeat_returns_200_and_writes_nothing()
    {
        await using var app = await CreateAppAsync();
        var emails = new RecordingEmailSender();
        var (pausedId, pausedToken) = await AddHeartbeatAsync(app, paused: true);
        var (disabledId, disabledToken) = await AddHeartbeatAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
                .Where(x => x.Id == disabledId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));

        foreach (var (id, token) in new[] { (pausedId, pausedToken), (disabledId, disabledToken) })
        {
            var before = await GetMonitorAsync(app, id);
            Assert.Equal((200, "paused"), await PingAsync(app, token, emails));
            var after = await GetMonitorAsync(app, id);
            Assert.Null(after.LastPingAt);
            Assert.Null(after.LastRunAt);
            Assert.Equal(before.NextRunAt, after.NextRunAt);
            Assert.Empty(await RunsAsync(app, id));
        }
    }

    [SkippableFact]
    public async Task Heartbeat_tokens_are_unique_and_http_monitors_may_share_a_null_token()
    {
        await using var app = await CreateAppAsync();
        var (_, token) = await AddHeartbeatAsync(app);
        await AddMonitorAsync(app);
        await AddMonitorAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var duplicate = NewHeartbeat("Duplicate", 60, 60, null, false);
        duplicate.HeartbeatToken = token;
        db.Monitors.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Unknown_tokens_return_404_and_malformed_tokens_return_404_without_reaching_the_executor()
    {
        await using var app = await CreateAppAsync();
        await AddHeartbeatAsync(app);

        Assert.Equal(404, (await PingAsync(app, HeartbeatTokens.Generate(), new RecordingEmailSender())).Status);

        var spy = new SpyExecutor();
        foreach (var malformed in new[] { "short", "../../etc/passwd", new string('g', 32), new string('a', 33), "%00" })
            Assert.Equal(404, (await PingAsync(app, malformed, new RecordingEmailSender(), executor: spy)).Status);
        Assert.Equal(0, spy.Pings);

        // A well-formed unknown token does reach the executor (and finds nothing).
        Assert.Equal(404, (await PingAsync(app, new string('a', 32), new RecordingEmailSender(), executor: spy)).Status);
        Assert.Equal(1, spy.Pings);
    }

    [SkippableFact]
    public async Task Head_and_post_pings_are_recorded_with_their_method_and_uppercase_tokens_resolve()
    {
        await using var app = await CreateAppAsync();
        var (id, token) = await AddHeartbeatAsync(app);

        Assert.Equal(200, (await PingAsync(app, token.ToUpperInvariant(), new RecordingEmailSender(), method: "POST")).Status);
        Assert.Equal(200, (await PingAsync(app, token, new RecordingEmailSender(), method: "HEAD")).Status);

        var runs = await RunsAsync(app, id);
        Assert.Equal(["HEAD", "POST"], runs.Select(r => r.RequestMethod).Order());
    }

    [SkippableFact]
    public async Task A_ping_with_a_busy_alert_lock_returns_503_and_writes_nothing()
    {
        await using var app = await CreateAppAsync();
        var storage = app.Services.GetRequiredService<JobStorage>();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        var (id, token) = await AddHeartbeatAsync(app, nextRunAt: past);

        (int Status, string? Body) ping;
        using (var holder = storage.GetConnection())
        using (holder.AcquireDistributedLock($"hawk:monitor-alert:{id}", TimeSpan.FromSeconds(60)))
            ping = await PingAsync(app, token, new RecordingEmailSender());

        Assert.Equal(503, ping.Status);
        var m = await GetMonitorAsync(app, id);
        Assert.Null(m.LastPingAt);
        Assert.True(Math.Abs((m.NextRunAt!.Value - past).TotalMilliseconds) < 1);
        Assert.Empty(await RunsAsync(app, id));
    }

    [SkippableFact]
    public async Task A_missed_check_whose_monitor_changed_after_the_reload_records_nothing()
    {
        await using var app = await CreateAppAsync();
        var emails = new RecordingEmailSender();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        var (id, _) = await AddHeartbeatAsync(app, nextRunAt: past);

        // An Edit/resume between the executor's reload and its write renews the stamp (and moves the deadline).
        var future = DateTimeOffset.UtcNow.AddHours(1);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var bump = new StampBumpInterceptor(app.ConnectionString, $"UPDATE \"Monitors\" SET \"ConcurrencyStamp\" = '{Guid.NewGuid()}', \"NextRunAt\" = '{future:O}' WHERE \"Id\" = '{id}'");
            await using var db = app.NewInterceptedContext(bump);
            var executor = NewExecutor(app, scope, emails, db);
            await executor.ExecuteAsync(id, "schedule", CancellationToken.None);
            Assert.True(bump.Fired);
        }

        Assert.Empty(await RunsAsync(app, id));
        Assert.Empty(emails.Sent);
        Assert.True(Math.Abs((( await GetMonitorAsync(app, id)).NextRunAt!.Value - future).TotalSeconds) < 1);
    }

    [SkippableFact]
    public async Task A_ping_survives_a_concurrent_change_by_reloading_once_and_honours_pause_and_regenerate()
    {
        await using var app = await CreateAppAsync();
        var emails = new RecordingEmailSender();

        // Concurrent rename-like change (stamp renewed): the ping reloads and is recorded.
        var (renamed, renamedToken) = await AddHeartbeatAsync(app);
        var bumpOnly = new StampBumpInterceptor(app.ConnectionString, $"UPDATE \"Monitors\" SET \"ConcurrencyStamp\" = '{Guid.NewGuid()}' WHERE \"Id\" = '{renamed}'");
        Assert.Equal(200, (await PingWithInterceptorAsync(app, renamedToken, emails, bumpOnly)).Status);
        Assert.True(bumpOnly.Fired);
        Assert.Single(await RunsAsync(app, renamed));

        // Paused in between: nothing is written.
        var (paused, pausedToken) = await AddHeartbeatAsync(app);
        var pause = new StampBumpInterceptor(app.ConnectionString, $"UPDATE \"Monitors\" SET \"ConcurrencyStamp\" = '{Guid.NewGuid()}', \"IsPaused\" = true WHERE \"Id\" = '{paused}'");
        Assert.Equal((200, "paused"), await PingWithInterceptorAsync(app, pausedToken, emails, pause));
        Assert.Empty(await RunsAsync(app, paused));
        Assert.Null((await GetMonitorAsync(app, paused)).LastPingAt);

        // Token regenerated while the old-token request was in flight: 404, nothing recorded.
        var (rotated, oldToken) = await AddHeartbeatAsync(app);
        var rotate = new StampBumpInterceptor(app.ConnectionString, $"UPDATE \"Monitors\" SET \"ConcurrencyStamp\" = '{Guid.NewGuid()}', \"HeartbeatToken\" = '{HeartbeatTokens.Generate()}' WHERE \"Id\" = '{rotated}'");
        Assert.Equal(404, (await PingWithInterceptorAsync(app, oldToken, emails, rotate)).Status);
        Assert.Empty(await RunsAsync(app, rotated));
        Assert.Null((await GetMonitorAsync(app, rotated)).LastPingAt);
    }

    [SkippableFact]
    public async Task A_missed_check_with_a_busy_alert_lock_records_nothing()
    {
        await using var app = await CreateAppAsync();
        var storage = app.Services.GetRequiredService<JobStorage>();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        var (id, _) = await AddHeartbeatAsync(app, nextRunAt: past);

        using (var holder = storage.GetConnection())
        using (holder.AcquireDistributedLock($"hawk:monitor-alert:{id}", TimeSpan.FromSeconds(60)))
            await MissAsync(app, id, new RecordingEmailSender());

        Assert.Empty(await RunsAsync(app, id));
        // timestamptz keeps microseconds only.
        Assert.True(Math.Abs(((await GetMonitorAsync(app, id)).NextRunAt!.Value - past).TotalMilliseconds) < 1);
    }

    [SkippableFact]
    public async Task StatusCake_history_import_ignores_heartbeats()
    {
        await using var app = await CreateAppAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var importer = new StatusCakeImporter(db, Production, NullLogger<StatusCakeImporter>.Instance);

        // A heartbeat that already uses the name the import would create does not block it ...
        db.Monitors.Add(NewHeartbeat("Example Site (sc:ab12)", 60, 60, null, false));
        await db.SaveChangesAsync();
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("""
            { "data": [ { "id": "ab12", "name": "Example Site", "website_url": "https://example.com", "check_rate": 60 } ] }
            """)))
            Assert.Equal(1, (await importer.ImportTestsAsync(ms, null, CancellationToken.None)).MonitorsCreated);

        // ... and a newer heartbeat carrying the marker never receives the imported HTTP history.
        db.Monitors.Add(NewHeartbeat("Newer (sc:ab12)", 60, 60, null, false));
        await db.SaveChangesAsync();
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("""
            { "test_id": "ab12", "data": [ { "status": "down", "status_code": 503, "triggered_at": "2026-02-09T02:00:00Z" } ] }
            """)))
            Assert.Equal(1, (await importer.ImportAlertsAsync(ms, CancellationToken.None)).RunsCreated);

        var run = await db.MonitorRuns.AsNoTracking().SingleAsync();
        var owner = await db.Monitors.AsNoTracking().SingleAsync(m => m.Id == run.MonitorId);
        Assert.Equal(MonitorType.Http, owner.Type);
        Assert.Equal(0, await db.MonitorRuns.CountAsync(r => db.Monitors.Any(m => m.Id == r.MonitorId && m.Type == MonitorType.Heartbeat)));
    }

    [SkippableFact]
    public async Task Batch_run_now_skips_heartbeats_and_reports_it()
    {
        await using var app = await CreateAppAsync();
        var http = await AddMonitorAsync(app);
        var (hb, _) = await AddHeartbeatAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        var jobs = new RecordingJobClient();
        var index = NewIndex(scope, jobs);
        index.SelectedMonitorIds = [http, hb];
        index.BatchAction = "run";
        InitPage(index);
        await index.OnPostBatchAsync(CancellationToken.None);

        Assert.Equal([http], jobs.EnqueuedMonitorIds());
        var flash = (string)index.TempData["FlashInfo"]!;
        Assert.Contains("Queued 1", flash);
        Assert.Contains("Skipped 1", flash);
    }

    [SkippableFact]
    public async Task Run_now_is_rejected_for_heartbeats_and_manual_executor_runs_do_nothing()
    {
        await using var app = await CreateAppAsync();
        var (id, _) = await AddHeartbeatAsync(app, nextRunAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var jobs = new RecordingJobClient();
            var details = new DetailsModel(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), NewCommands(scope, jobs));
            InitPage(details);
            Assert.IsType<RedirectToPageResult>(await details.OnPostRunNowAsync(id, CancellationToken.None));
            Assert.Empty(jobs.EnqueuedMonitorIds());
        }

        await using (var scope = app.Services.CreateAsyncScope())
            Assert.Null(await NewExecutor(app, scope, new RecordingEmailSender()).ExecuteAsync(id, "manual", CancellationToken.None));
        Assert.Empty(await RunsAsync(app, id));
    }

    [SkippableFact]
    public async Task Heartbeat_edit_keeps_its_type_resets_the_deadline_only_on_real_changes_and_can_regenerate_the_token()
    {
        await using var app = await CreateAppAsync();
        var (id, token) = await AddHeartbeatAsync(app, period: 3600, grace: 300);
        var deadline = (await GetMonitorAsync(app, id)).NextRunAt;

        var form = await LoadEditFormAsync(app, id);
        Assert.Equal(MonitorType.Heartbeat, form.Type);
        Assert.Equal(3600, form.IntervalSeconds);
        Assert.Equal(300, form.GraceSeconds);

        // Forged HTTP fields are ignored; renaming keeps the deadline.
        form.Name = "Renamed";
        form.Type = MonitorType.Http;
        form.Url = "https://evil.example";
        var (rename, _) = await PostEditAsync(app, id, form);
        Assert.Equal("/Monitors/Details", Assert.IsType<RedirectToPageResult>(rename).PageName);
        var renamed = await GetMonitorAsync(app, id);
        Assert.Equal(MonitorType.Heartbeat, renamed.Type);
        Assert.Equal(string.Empty, renamed.Url);
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(deadline, renamed.NextRunAt);

        // A new period gives a full period plus grace from now.
        form = await LoadEditFormAsync(app, id);
        form.IntervalSeconds = 86400;
        await PostEditAsync(app, id, form);
        Assert.InRange((await GetMonitorAsync(app, id)).NextRunAt!.Value, DateTimeOffset.UtcNow.AddSeconds(86400), DateTimeOffset.UtcNow.AddSeconds(86701));

        // Re-enabling does the same.
        await SetNextRunAsync(app, id, DateTimeOffset.UtcNow.AddMinutes(-10));
        form = await LoadEditFormAsync(app, id);
        form.Enabled = false;
        await PostEditAsync(app, id, form);
        form = await LoadEditFormAsync(app, id);
        form.Enabled = true;
        await PostEditAsync(app, id, form);
        Assert.True((await GetMonitorAsync(app, id)).NextRunAt > DateTimeOffset.UtcNow.AddSeconds(86000));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var page = new EditModel(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), Production, scope.ServiceProvider.GetRequiredService<MonitorEditor>()) { Id = id };
            InitPage(page);
            Assert.IsType<RedirectToPageResult>(await page.OnPostRegenerateAsync(CancellationToken.None));
        }
        var regenerated = (await GetMonitorAsync(app, id)).HeartbeatToken;
        Assert.NotEqual(token, regenerated);
        Assert.Equal(404, (await PingAsync(app, token, new RecordingEmailSender())).Status);
        Assert.Equal(200, (await PingAsync(app, regenerated!, new RecordingEmailSender())).Status);
    }

    [SkippableFact]
    public async Task Heartbeat_timestamps_are_utc_in_timestamptz()
    {
        await using var app = await CreateAppAsync();
        var (id, token) = await AddHeartbeatAsync(app);
        await PingAsync(app, token, new RecordingEmailSender());

        var m = await GetMonitorAsync(app, id);
        Assert.Equal(TimeSpan.Zero, m.LastPingAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, m.NextRunAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, m.LastRunAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, (await RunsAsync(app, id)).Single().StartedAt.Offset);
    }

    [SkippableFact]
    public async Task Api_keys_are_removed_when_the_user_is_deleted()
    {
        await using var app = await CreateAppAsync();
        var (userId, _) = await AddUserWithKeyAsync(app, "cascade@example.com");
        var (otherId, _) = await AddUserWithKeyAsync(app, "other@example.com");

        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.True((await users.DeleteAsync((await users.FindByIdAsync(userId))!)).Succeeded);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.ApiKeys.Where(k => k.UserId == userId).ToListAsync());
        Assert.Single(await db.ApiKeys.Where(k => k.UserId == otherId).ToListAsync());
    }

    [SkippableFact]
    public async Task Api_key_touch_writes_once_within_five_minutes()
    {
        await using var app = await CreateAppAsync();
        var (_, key) = await AddUserWithKeyAsync(app, "touch@example.com");

        async Task<DateTimeOffset?> LastUsedAsync()
        {
            await using var s = app.Services.CreateAsyncScope();
            return (await s.ServiceProvider.GetRequiredService<ApplicationDbContext>().ApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.Id)).LastUsedAt;
        }

        async Task TouchAsync()
        {
            await using var s = app.Services.CreateAsyncScope();
            await s.ServiceProvider.GetRequiredService<ApiKeyService>().TouchAsync(key.Id);
        }

        Assert.Null(await LastUsedAsync());
        await TouchAsync();
        var first = await LastUsedAsync();
        Assert.NotNull(first);

        await TouchAsync();
        Assert.Equal(first, await LastUsedAsync());

        var old = DateTimeOffset.UtcNow.AddMinutes(-6);
        await using (var s = app.Services.CreateAsyncScope())
            await s.ServiceProvider.GetRequiredService<ApplicationDbContext>().ApiKeys
                .Where(k => k.Id == key.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.LastUsedAt, old));
        await TouchAsync();
        Assert.True(await LastUsedAsync() > first);
    }

    [SkippableFact]
    public async Task Api_key_unique_index_rejects_a_duplicate_hash()
    {
        await using var app = await CreateAppAsync();
        var (userId, key) = await AddUserWithKeyAsync(app, "unique@example.com");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.ApiKeys.Add(new ApiKey { UserId = userId, Name = "dup", DisplayPrefix = "hawk_dup", SecretHash = key.SecretHash });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Api_key_revoke_all_and_revoke_work_set_based()
    {
        await using var app = await CreateAppAsync();
        var (userId, key) = await AddUserWithKeyAsync(app, "revoke@example.com");
        var (otherId, _) = await AddUserWithKeyAsync(app, "revoke2@example.com");

        await using var scope = app.Services.CreateAsyncScope();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        Assert.False(await keys.RevokeAsync(otherId, key.Id));
        Assert.True(await keys.RevokeAsync(userId, key.Id));
        await keys.RevokeAllAsync(userId);
        Assert.Empty(await keys.ListAsync(userId));
        Assert.Single(await keys.ListAsync(otherId));
    }

    [SkippableFact]
    public async Task Admin_password_reset_revokes_the_users_api_keys()
    {
        await using var app = await CreateAppAsync();
        var (userId, _) = await AddUserWithKeyAsync(app, "admin-reset@example.com");
        var (otherId, _) = await AddUserWithKeyAsync(app, "admin-reset-other@example.com");

        await using var scope = app.Services.CreateAsyncScope();
        var page = new Hawk.Web.Pages.Admin.Users.ResetPasswordModel(
            scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(),
            scope.ServiceProvider.GetRequiredService<ApiKeyService>())
        {
            Id = userId,
            Input = new() { NewPassword = "Reset-Password-123!" },
        };
        InitPage(page);
        Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(CancellationToken.None));

        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        Assert.Empty(await keys.ListAsync(userId));
        Assert.Single(await keys.ListAsync(otherId));
    }

    [SkippableFact]
    public async Task Forgot_password_reset_revokes_keys_only_when_the_reset_succeeds()
    {
        await using var app = await CreateAppAsync();
        const string email = "forgot@example.com";
        var (userId, _) = await AddUserWithKeyAsync(app, email);

        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var user = (await users.FindByIdAsync(userId))!;

        Hawk.Web.Areas.Identity.Pages.Account.ResetPasswordModel NewPage(string code, string password)
        {
            var p = new Hawk.Web.Areas.Identity.Pages.Account.ResetPasswordModel(users, keys)
            {
                Input = new() { Email = email, Password = password, ConfirmPassword = password, Code = code },
            };
            InitPage(p);
            return p;
        }

        // Bad token: the reset fails and the keys keep working.
        Assert.IsType<PageResult>(await NewPage("bad-token", "Reset-Password-123!").OnPostAsync());
        Assert.Single(await keys.ListAsync(userId));

        // Valid token: the reset succeeds and the keys are revoked.
        var token = await users.GeneratePasswordResetTokenAsync(user);
        Assert.IsType<RedirectToPageResult>(await NewPage(token, "Reset-Password-123!").OnPostAsync());
        Assert.Empty(await keys.ListAsync(userId));
    }

    [SkippableFact]
    public async Task Index_failing_group_follows_the_latest_completed_run_and_ignores_unfinished_ones()
    {
        await using var app = await CreateAppAsync();
        var failing = await AddMonitorAsync(app);
        var recovered = await AddMonitorAsync(app);
        var neverRan = await AddMonitorAsync(app);
        await ExecuteAsync(app, failing, HttpStatusCode.InternalServerError);
        await ExecuteAsync(app, recovered, HttpStatusCode.InternalServerError);
        await ExecuteAsync(app, recovered, HttpStatusCode.OK);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // A failed run that never completed (queued "Run now") must not count, and a failed run without LastRunAt is not "failing".
            db.MonitorRuns.Add(new MonitorRun { MonitorId = failing, State = "queued", Success = true, StartedAt = DateTimeOffset.UtcNow.AddMinutes(1), FinishedAt = DateTimeOffset.UtcNow });
            db.MonitorRuns.Add(new MonitorRun { MonitorId = neverRan, State = "completed", Success = false, StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var index = NewIndex(scope);
            InitPage(index);
            await index.OnGetAsync(CancellationToken.None);

            Assert.Equal([failing], index.FailingMonitors.Select(m => m.Id));
            var section = Assert.Single(index.GroupSections);
            Assert.Null(section.Name);
            Assert.Equal(3, section.Monitors.Count);
            Assert.Equal(1, section.FailingCount);
        }
    }

    [SkippableFact]
    public async Task Pause_all_and_resume_all_skip_disabled_monitors_and_renew_the_stamp()
    {
        await using var app = await CreateAppAsync();
        var active = await AddMonitorAsync(app);
        var disabled = await AddMonitorAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
                .Where(m => m.Id == disabled).ExecuteUpdateAsync(s => s.SetProperty(m => m.Enabled, false));
        var activeStamp = (await GetMonitorAsync(app, active)).ConcurrencyStamp;
        var disabledStamp = (await GetMonitorAsync(app, disabled)).ConcurrencyStamp;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var index = NewIndex(scope);
            InitPage(index);
            await index.OnPostPauseAllAsync(CancellationToken.None);
            Assert.Equal("Paused 1 monitor(s).", index.TempData["FlashInfo"]);
        }
        var paused = await GetMonitorAsync(app, active);
        Assert.True(paused.IsPaused);
        Assert.NotEqual(activeStamp, paused.ConcurrencyStamp);
        Assert.Equal(disabledStamp, (await GetMonitorAsync(app, disabled)).ConcurrencyStamp);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var index = NewIndex(scope);
            InitPage(index);
            await index.OnPostResumeAllAsync(CancellationToken.None);
            Assert.Equal("Resumed 1 monitor(s).", index.TempData["FlashInfo"]);
        }
        var resumed = await GetMonitorAsync(app, active);
        Assert.False(resumed.IsPaused);
        Assert.NotEqual(paused.ConcurrencyStamp, resumed.ConcurrencyStamp);
        Assert.Equal(disabledStamp, (await GetMonitorAsync(app, disabled)).ConcurrencyStamp);
    }

    [SkippableFact]
    public async Task Pause_one_renews_the_stamp_and_a_repeat_changes_nothing()
    {
        await using var app = await CreateAppAsync();
        var id = await AddMonitorAsync(app);
        var before = (await GetMonitorAsync(app, id)).ConcurrencyStamp;

        await using var scope = app.Services.CreateAsyncScope();
        var commands = NewCommands(scope, new RecordingJobClient());
        Assert.Equal(PauseResumeOutcome.Changed, await commands.PauseMonitorAsync(id, CancellationToken.None));
        var paused = await GetMonitorAsync(app, id);
        Assert.True(paused.IsPaused);
        Assert.NotEqual(before, paused.ConcurrencyStamp);

        Assert.Equal(PauseResumeOutcome.Unchanged, await commands.PauseMonitorAsync(id, CancellationToken.None));
        Assert.Equal(paused.ConcurrencyStamp, (await GetMonitorAsync(app, id)).ConcurrencyStamp);

        Assert.Equal(PauseResumeOutcome.Changed, await commands.ResumeMonitorAsync(id, CancellationToken.None));
        Assert.Equal(PauseResumeOutcome.Unchanged, await commands.ResumeMonitorAsync(id, CancellationToken.None));
        Assert.Equal(PauseResumeOutcome.NotFound, await commands.PauseMonitorAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Resume_one_heartbeat_sets_the_deadline_in_the_same_statement()
    {
        await using var app = await CreateAppAsync();
        var (id, _) = await AddHeartbeatAsync(app, period: 3600, grace: 300, nextRunAt: DateTimeOffset.UtcNow.AddMinutes(-30), paused: true);
        var before = (await GetMonitorAsync(app, id)).ConcurrencyStamp;

        await using var scope = app.Services.CreateAsyncScope();
        Assert.Equal(PauseResumeOutcome.Changed, await NewCommands(scope, new RecordingJobClient()).ResumeMonitorAsync(id, CancellationToken.None));

        var m = await GetMonitorAsync(app, id);
        Assert.False(m.IsPaused);
        Assert.NotEqual(before, m.ConcurrencyStamp);
        Assert.InRange(m.NextRunAt!.Value, DateTimeOffset.UtcNow.AddSeconds(3800), DateTimeOffset.UtcNow.AddSeconds(3901));
    }

    [SkippableFact]
    public async Task Pause_and_resume_one_report_disabled_and_change_nothing()
    {
        await using var app = await CreateAppAsync();
        var disabledActive = await AddMonitorAsync(app);
        var disabledPaused = await AddMonitorAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
                .Where(m => m.Id == disabledActive || m.Id == disabledPaused)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Enabled, false));
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
                .Where(m => m.Id == disabledPaused).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsPaused, true));
        var stampA = (await GetMonitorAsync(app, disabledActive)).ConcurrencyStamp;
        var stampP = (await GetMonitorAsync(app, disabledPaused)).ConcurrencyStamp;

        await using var cmdScope = app.Services.CreateAsyncScope();
        var commands = NewCommands(cmdScope, new RecordingJobClient());
        Assert.Equal(PauseResumeOutcome.Disabled, await commands.PauseMonitorAsync(disabledActive, CancellationToken.None));
        Assert.Equal(PauseResumeOutcome.Disabled, await commands.ResumeMonitorAsync(disabledPaused, CancellationToken.None));

        var a = await GetMonitorAsync(app, disabledActive);
        var p = await GetMonitorAsync(app, disabledPaused);
        Assert.False(a.IsPaused);
        Assert.True(p.IsPaused);
        Assert.Equal(stampA, a.ConcurrencyStamp);
        Assert.Equal(stampP, p.ConcurrencyStamp);
    }

    [SkippableFact]
    public async Task Queue_manual_run_creates_a_queued_run_and_one_job_and_keeps_the_stamp()
    {
        await using var app = await CreateAppAsync();
        var id = await AddMonitorAsync(app);
        var stamp = (await GetMonitorAsync(app, id)).ConcurrencyStamp;

        await using var scope = app.Services.CreateAsyncScope();
        var jobs = new RecordingJobClient();
        var result = await NewCommands(scope, jobs).QueueManualRunAsync(id, reuseInFlight: false, rejectDisabled: false, CancellationToken.None);

        Assert.Equal(ManualRunOutcome.Queued, result.Outcome);
        var run = Assert.Single(await RunsAsync(app, id));
        Assert.Equal(result.RunId, run.Id);
        Assert.Equal("queued", run.State);
        Assert.Equal("manual", run.Reason);
        Assert.Equal([id], jobs.EnqueuedMonitorIds());
        Assert.Equal(stamp, (await GetMonitorAsync(app, id)).ConcurrencyStamp);
    }

    [SkippableFact]
    public async Task Queue_manual_run_reuses_a_recent_in_flight_run_only_when_asked()
    {
        await using var app = await CreateAppAsync();
        var id = await AddMonitorAsync(app); // timeout 15 s, so the reuse window is 75 s

        await using var scope = app.Services.CreateAsyncScope();
        var jobs = new RecordingJobClient();
        var commands = NewCommands(scope, jobs);
        var first = await commands.QueueManualRunAsync(id, reuseInFlight: true, rejectDisabled: true, CancellationToken.None);
        var second = await commands.QueueManualRunAsync(id, reuseInFlight: true, rejectDisabled: true, CancellationToken.None);
        Assert.Equal(ManualRunOutcome.Queued, first.Outcome);
        Assert.Equal(ManualRunOutcome.AlreadyInFlight, second.Outcome);
        Assert.Equal(first.RunId, second.RunId);
        Assert.Single(await RunsAsync(app, id));
        Assert.Single(jobs.EnqueuedMonitorIds());

        // Without reuse (Details page) a second call queues a second run.
        var third = await commands.QueueManualRunAsync(id, reuseInFlight: false, rejectDisabled: false, CancellationToken.None);
        Assert.Equal(ManualRunOutcome.Queued, third.Outcome);
        Assert.NotEqual(first.RunId, third.RunId);
        Assert.Equal(2, (await RunsAsync(app, id)).Count);

        // A running manual run counts as in flight too, but one older than timeout + 60 s is treated as lost.
        await using (var db = app.Services.CreateAsyncScope())
            await db.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorRuns
                .Where(r => r.MonitorId == id).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.State, "running")
                    .SetProperty(r => r.StartedAt, DateTimeOffset.UtcNow.AddSeconds(-74)));
        Assert.Equal(ManualRunOutcome.AlreadyInFlight, (await commands.QueueManualRunAsync(id, true, true, CancellationToken.None)).Outcome);

        await using (var db = app.Services.CreateAsyncScope())
            await db.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorRuns
                .Where(r => r.MonitorId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.StartedAt, DateTimeOffset.UtcNow.AddSeconds(-76)));
        var fresh = await commands.QueueManualRunAsync(id, true, true, CancellationToken.None);
        Assert.Equal(ManualRunOutcome.Queued, fresh.Outcome);
        Assert.DoesNotContain(fresh.RunId, (await RunsAsync(app, id)).Where(r => r.State == "running").Select(r => (Guid?)r.Id));
    }

    [SkippableFact]
    public async Task Queue_manual_run_reports_heartbeat_disabled_and_not_found()
    {
        await using var app = await CreateAppAsync();
        var (hb, _) = await AddHeartbeatAsync(app);
        var disabled = await AddMonitorAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
                .Where(m => m.Id == disabled).ExecuteUpdateAsync(s => s.SetProperty(m => m.Enabled, false));

        await using var cmdScope = app.Services.CreateAsyncScope();
        var jobs = new RecordingJobClient();
        var commands = NewCommands(cmdScope, jobs);
        Assert.Equal(ManualRunOutcome.Heartbeat, (await commands.QueueManualRunAsync(hb, true, true, CancellationToken.None)).Outcome);
        Assert.Equal(ManualRunOutcome.NotFound, (await commands.QueueManualRunAsync(Guid.NewGuid(), true, true, CancellationToken.None)).Outcome);
        Assert.Equal(ManualRunOutcome.Disabled, (await commands.QueueManualRunAsync(disabled, true, true, CancellationToken.None)).Outcome);
        Assert.Empty(jobs.EnqueuedMonitorIds());
        Assert.Empty(await RunsAsync(app, disabled));

        // The Details page still queues disabled monitors.
        Assert.Equal(ManualRunOutcome.Queued, (await commands.QueueManualRunAsync(disabled, false, false, CancellationToken.None)).Outcome);
    }

    [SkippableFact]
    public async Task Update_alerting_changes_only_the_given_fields_renews_the_stamp_and_conflicts_with_an_open_edit_form()
    {
        await using var app = await CreateAppAsync();
        var id = await AddMonitorAsync(app); // threshold 1, override alerts@example.com
        var form = await LoadEditFormAsync(app, id);
        var stamp = (await GetMonitorAsync(app, id)).ConcurrencyStamp;

        await using var scope = app.Services.CreateAsyncScope();
        var commands = NewCommands(scope, new RecordingJobClient());

        Assert.True(await commands.UpdateAlertingAsync(id, 3, null, false, CancellationToken.None));
        var afterThreshold = await GetMonitorAsync(app, id);
        Assert.Equal(3, afterThreshold.AlertAfterConsecutiveFailures);
        Assert.Equal("alerts@example.com", afterThreshold.AlertEmailOverride);
        Assert.NotEqual(stamp, afterThreshold.ConcurrencyStamp);

        Assert.True(await commands.UpdateAlertingAsync(id, null, "  ops@example.com ", false, CancellationToken.None));
        var afterEmail = await GetMonitorAsync(app, id);
        Assert.Equal(3, afterEmail.AlertAfterConsecutiveFailures);
        Assert.Equal("ops@example.com", afterEmail.AlertEmailOverride);
        Assert.NotEqual(afterThreshold.ConcurrencyStamp, afterEmail.ConcurrencyStamp);

        Assert.True(await commands.UpdateAlertingAsync(id, null, null, true, CancellationToken.None));
        var cleared = await GetMonitorAsync(app, id);
        Assert.Null(cleared.AlertEmailOverride);
        Assert.Equal(3, cleared.AlertAfterConsecutiveFailures);
        Assert.NotEqual(afterEmail.ConcurrencyStamp, cleared.ConcurrencyStamp);

        Assert.False(await commands.UpdateAlertingAsync(Guid.NewGuid(), 2, null, false, CancellationToken.None));

        // The Edit form loaded before the change now conflicts.
        form.Name = "Edited after API change";
        var (post, flash) = await PostEditAsync(app, id, form);
        Assert.Equal("/Monitors/Edit", Assert.IsType<RedirectToPageResult>(post).PageName);
        Assert.NotNull(flash);
    }

    [SkippableFact]
    public async Task Status_service_reports_state_health_and_failure_summary_for_many_monitors()
    {
        await using var app = await CreateAppAsync();
        var failing = await AddMonitorAsync(app);
        var healthy = await AddMonitorAsync(app);
        var queuedOnly = await AddMonitorAsync(app);
        var (overdueHeartbeat, _) = await AddHeartbeatAsync(app, nextRunAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await ExecuteAsync(app, failing, HttpStatusCode.OK);
        await ExecuteAsync(app, failing, HttpStatusCode.InternalServerError);
        await ExecuteAsync(app, failing, HttpStatusCode.InternalServerError);
        await ExecuteAsync(app, healthy, HttpStatusCode.OK);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MonitorRuns.Add(new MonitorRun { MonitorId = queuedOnly, State = "queued", StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await db.Monitors.Where(m => m.Id == failing).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsPaused, true));
        }

        await using var statusScope = app.Services.CreateAsyncScope();
        var monitors = await statusScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking().ToListAsync();
        var statuses = await NewStatus(statusScope).GetStatusesAsync(monitors, CancellationToken.None);

        var f = statuses[failing];
        Assert.Equal(MonitorState.Paused, f.State);
        Assert.Equal(MonitorHealth.Failing, f.Health);
        Assert.Equal(2, f.Failure!.ConsecutiveFailures);
        Assert.Equal(500, f.Failure.StatusCode);
        Assert.NotNull(f.Failure.FailingSince);
        Assert.Equal(MonitorHealth.Ok, statuses[healthy].Health);
        Assert.Null(statuses[healthy].Failure);
        Assert.Equal(MonitorHealth.Unknown, statuses[queuedOnly].Health);
        Assert.Equal(MonitorHealth.Overdue, statuses[overdueHeartbeat].Health);
    }

    [SkippableFact]
    public async Task Recipient_resolver_returns_addresses_with_the_rule_that_picked_them()
    {
        await using var app = await CreateAppAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var withAccount = new IdentityUser { UserName = "acct@example.com", Email = "acct@example.com" };
        var plain = new IdentityUser { UserName = "plain@example.com", Email = "plain@example.com" };
        var admin = new IdentityUser { UserName = "admin@example.com", Email = "admin@example.com" };
        foreach (var u in new[] { withAccount, plain, admin })
            Assert.True((await users.CreateAsync(u, "password123")).Succeeded);
        Assert.True((await roles.CreateAsync(new IdentityRole("Admin"))).Succeeded);
        Assert.True((await users.AddToRoleAsync(admin, "Admin")).Succeeded);
        await new AccountAlertSettings(db).SetEmailAsync(withAccount.Id, " account@example.com ", CancellationToken.None);

        var resolver = new AlertRecipientResolver(db, users);
        async Task<AlertRecipients> Resolve(string? overrideEmail, string? owner) =>
            await resolver.ResolveAsync(new MonitorEntity { Name = "m", Url = "u", Method = "GET", AlertEmailOverride = overrideEmail, CreatedByUserId = owner }, CancellationToken.None);

        var monitor = await Resolve(" ops@example.com ", withAccount.Id);
        Assert.Equal(AlertRecipientSource.Monitor, monitor.Source);
        Assert.Equal(["ops@example.com"], monitor.Addresses);

        var account = await Resolve(null, withAccount.Id);
        Assert.Equal(AlertRecipientSource.Account, account.Source);
        Assert.Equal(["account@example.com"], account.Addresses);

        var owner = await Resolve(null, plain.Id);
        Assert.Equal(AlertRecipientSource.Owner, owner.Source);
        Assert.Equal(["plain@example.com"], owner.Addresses);

        // No owner, or an owner without an email, falls back to all admins.
        var noOwner = await Resolve(null, null);
        Assert.Equal(AlertRecipientSource.Admins, noOwner.Source);
        Assert.Equal(["admin@example.com"], noOwner.Addresses);
        plain.Email = null;
        await users.UpdateAsync(plain);
        Assert.Equal(AlertRecipientSource.Admins, (await Resolve(null, plain.Id)).Source);
    }

    [SkippableFact]
    public async Task Account_alert_email_is_saved_trimmed_replaced_and_cleared()
    {
        await using var app = await CreateAppAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = "u@example.com", Email = "u@example.com" };
        Assert.True((await users.CreateAsync(user, "password123")).Succeeded);

        var settings = new AccountAlertSettings(db);
        await settings.SetEmailAsync(user.Id, " first@example.com ", CancellationToken.None);
        Assert.Equal("first@example.com", await settings.GetEmailAsync(user.Id, CancellationToken.None));
        await settings.SetEmailAsync(user.Id, "second@example.com", CancellationToken.None);
        Assert.Equal("second@example.com", await settings.GetEmailAsync(user.Id, CancellationToken.None));
        Assert.Equal(1, await db.UserAlertSettings.CountAsync());
        await settings.SetEmailAsync(user.Id, "  ", CancellationToken.None);
        Assert.Null(await settings.GetEmailAsync(user.Id, CancellationToken.None));
        Assert.Equal(0, await db.UserAlertSettings.CountAsync());
    }

    [SkippableFact]
    public async Task Settings_page_saves_and_clears_the_account_alert_email_through_the_shared_service()
    {
        await using var app = await CreateAppAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = "u@example.com", Email = "u@example.com" };
        Assert.True((await users.CreateAsync(user, "password123")).Succeeded);

        SettingsModel NewPage(string? email)
        {
            var page = new SettingsModel(db, users, new ConfigurationBuilder().Build(), new AccountAlertSettings(db)) { AlertEmail = email };
            InitPage(page);
            page.PageContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id)], "test"));
            return page;
        }

        Assert.IsType<RedirectToPageResult>(await NewPage(" me@example.com ").OnPostAsync(CancellationToken.None));
        Assert.Equal("me@example.com", await db.UserAlertSettings.Where(x => x.UserId == user.Id).Select(x => x.AlertEmail).SingleAsync());

        var get = NewPage(null);
        Assert.IsType<PageResult>(await get.OnGetAsync(CancellationToken.None));
        Assert.Equal("me@example.com", get.AlertEmail);

        Assert.IsType<RedirectToPageResult>(await NewPage("").OnPostAsync(CancellationToken.None));
        Assert.Empty(await db.UserAlertSettings.Where(x => x.UserId == user.Id).ToListAsync());
    }

    // ---- helpers ----

    private sealed class TestApp(ServiceProvider services, string connectionString) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public string ConnectionString { get; } = connectionString;

        /// <summary>A context on the same database whose commands pass through <paramref name="interceptor"/>.</summary>
        public ApplicationDbContext NewInterceptedContext(StampBumpInterceptor interceptor) =>
            new PostgresApplicationDbContext(new DbContextOptionsBuilder<PostgresApplicationDbContext>()
                .UseNpgsql(ConnectionString)
                .AddInterceptors(interceptor)
                .Options);

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private async Task<TestApp> CreateAppAsync()
    {
        // HAWK_REQUIRE_POSTGRES_TESTS=1 (CI/release checks): a missing Docker is a failure, not a skip.
        if (fixture.UnavailableReason is not null && Environment.GetEnvironmentVariable("HAWK_REQUIRE_POSTGRES_TESTS") == "1")
            throw new InvalidOperationException($"HAWK_REQUIRE_POSTGRES_TESTS=1 but {fixture.UnavailableReason}");
        Skip.If(fixture.UnavailableReason is not null, fixture.UnavailableReason);

        var connectionString = fixture.NewDatabaseConnectionString();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hawk:Email:Enabled"] = "true",
                ["Hawk:Email:From"] = "Hawk <hawk@localhost>",
            })
            .Build();

        // Same registrations as Program.cs for the database, Identity and Hangfire storage.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Production);
        services.AddHawkDbContext(DatabaseProvider.Postgres, connectionString);
        services.AddHawkIdentity();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<MonitorEditor>();
        services.AddSingleton<JobStorage>(_ => DatabaseSetup.CreatePostgreSqlStorage(connectionString));
        var sp = services.BuildServiceProvider();

        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();

        return new TestApp(sp, connectionString);
    }

    private static async Task<(string UserId, ApiKey Key)> AddUserWithKeyAsync(TestApp app, string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = email, Email = email };
        Assert.True((await users.CreateAsync(user, "Password-123!")).Succeeded);
        var created = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync(user.Id, "test", ApiKeyScope.Read);
        return (user.Id, created.Key);
    }

    private static MonitorEntity NewMonitor(string name, DateTimeOffset? nextRunAt) => new()
    {
        Name = name,
        Url = "https://example.com/health",
        Method = "GET",
        IntervalSeconds = 60,
        NextRunAt = nextRunAt,
        AlertEmailOverride = "alerts@example.com",
    };

    private static async Task<Guid> AddMonitorAsync(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var m = NewMonitor("Monitor", null);
        db.Monitors.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    private static MonitorEntity NewHeartbeat(string name, int period, int grace, DateTimeOffset? nextRunAt, bool paused) => new()
    {
        Name = name,
        Type = MonitorType.Heartbeat,
        Url = string.Empty,
        Method = "GET",
        IntervalSeconds = period,
        HeartbeatGraceSeconds = grace,
        HeartbeatToken = HeartbeatTokens.Generate(),
        NextRunAt = nextRunAt ?? DateTimeOffset.UtcNow.AddSeconds(period + grace),
        IsPaused = paused,
        AlertEmailOverride = "alerts@example.com",
    };

    private static async Task<(Guid Id, string Token)> AddHeartbeatAsync(TestApp app, int period = 60, int grace = 60, DateTimeOffset? nextRunAt = null, bool paused = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var m = NewHeartbeat("Heartbeat", period, grace, nextRunAt, paused);
        db.Monitors.Add(m);
        await db.SaveChangesAsync();
        return (m.Id, m.HeartbeatToken!);
    }

    private static async Task<MonitorEntity> GetMonitorAsync(TestApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private static async Task SetNextRunAsync(TestApp app, Guid id, DateTimeOffset value)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors
            .Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.NextRunAt, value));
    }

    private static async Task<List<MonitorRun>> RunsAsync(TestApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorRuns
            .AsNoTracking().Where(r => r.MonitorId == id).ToListAsync())
            .OrderBy(r => r.StartedAt).ToList();
    }

    private static async Task<(int Status, string? Body)> PingWithInterceptorAsync(TestApp app, string token, RecordingEmailSender emails, StampBumpInterceptor interceptor)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = app.NewInterceptedContext(interceptor);
        var result = await PingAsync(app, token, emails, executor: NewExecutor(app, scope, emails, db));
        Assert.True(interceptor.Fired);
        return result;
    }

    private static async Task MissAsync(TestApp app, Guid id, RecordingEmailSender emails)
    {
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Null(await NewExecutor(app, scope, emails).ExecuteAsync(id, "schedule", CancellationToken.None));
    }

    /// <summary>Runs the real ping endpoint handler (token already routed) and returns status and body.</summary>
    private static async Task<(int Status, string? Body)> PingAsync(TestApp app, string token, RecordingEmailSender emails, string method = "GET", IMonitorExecutor? executor = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Headers.UserAgent = "curl/8.5.0";
        http.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        var result = await HeartbeatEndpoints.HandlePingAsync(token, http, executor ?? NewExecutor(app, scope, emails));
        return result switch
        {
            ContentHttpResult content => (content.StatusCode ?? 200, content.ResponseContent),
            IStatusCodeHttpResult status => (status.StatusCode ?? 200, null),
            _ => throw new InvalidOperationException($"Unexpected result {result.GetType()}"),
        };
    }

    private static async Task<MonitorForm> LoadEditFormAsync(TestApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var page = new EditModel(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), Production, scope.ServiceProvider.GetRequiredService<MonitorEditor>()) { Id = id };
        InitPage(page);
        Assert.IsType<PageResult>(await page.OnGetAsync(CancellationToken.None));
        return page.Form;
    }

    private static async Task<(IActionResult Result, string? FlashError)> PostEditAsync(TestApp app, Guid id, MonitorForm form)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var page = new EditModel(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), Production, scope.ServiceProvider.GetRequiredService<MonitorEditor>()) { Id = id, Form = form };
        InitPage(page);
        var result = await page.OnPostAsync(CancellationToken.None);
        return (result, page.TempData["FlashError"] as string);
    }

    private static async Task<RecordingJobClient> TickSchedulerAsync(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var jobs = new RecordingJobClient();
        var scheduler = new MonitorScheduler(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            jobs,
            app.Services.GetRequiredService<JobStorage>(),
            NullLogger<MonitorScheduler>.Instance,
            Production,
            app.Services.GetRequiredService<IConfiguration>());
        await scheduler.TickAsync(CancellationToken.None);
        return jobs;
    }

    private static async Task<MonitorExecutionResult?> ExecuteAsync(TestApp app, Guid id, HttpStatusCode status, string snippet = "ok")
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await NewExecutor(app, scope, new NoopEmailSender(), null, status, snippet).ExecuteAsync(id, "schedule", CancellationToken.None);
    }

    private static MonitorExecutor NewExecutor(TestApp app, AsyncServiceScope scope, IEmailSender emails, ApplicationDbContext? db = null, HttpStatusCode status = HttpStatusCode.OK, string snippet = "ok") =>
        new(
            db ?? scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            new FixedUrlChecker(status, snippet),
            emails,
            app.Services.GetRequiredService<JobStorage>(),
            new AlertRecipientResolver(db ?? scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>()),
            NullLogger<MonitorExecutor>.Instance,
            app.Services.GetRequiredService<IConfiguration>());

    private static async Task<Guid> AlertStampAsync(TestApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorAlertStates
            .Where(s => s.MonitorId == id)
            .Select(s => s.ConcurrencyStamp)
            .SingleAsync();
    }

    private static IndexModel NewIndex(AsyncServiceScope scope, IBackgroundJobClient? jobs = null)
    {
        jobs ??= new RecordingJobClient();
        return new(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), jobs, NewStatus(scope), NewCommands(scope, jobs));
    }

    private static MonitorCommands NewCommands(AsyncServiceScope scope, IBackgroundJobClient jobs) =>
        new(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), jobs);

    private static MonitorStatusService NewStatus(AsyncServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), new ConfigurationBuilder().Build(), Production);

    private static void InitPage(PageModel page)
    {
        var http = new DefaultHttpContext();
        page.PageContext = new PageContext { HttpContext = http };
        page.TempData = new TempDataDictionary(http, new NullTempDataProvider());
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        private readonly List<(Job Job, IState State)> _created = [];

        public string Create(Job job, IState state)
        {
            _created.Add((job, state));
            return _created.Count.ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;

        public IReadOnlyList<Guid> EnqueuedMonitorIds() => _created
            .Where(c => c.State is EnqueuedState && c.Job.Method.Name == nameof(IMonitorRunner.RunAsync))
            .Select(c => (Guid)c.Job.Args[0]!)
            .ToList();
    }

    private sealed class FixedUrlChecker(HttpStatusCode status, string snippet) : IUrlChecker
    {
        public Task<UrlCheckResult> CheckAsync(UrlCheckRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new UrlCheckResult(
                (int)status < 400, status, TimeSpan.FromMilliseconds(5), null, [], snippet,
                new Dictionary<string, string>(), "text/plain", 2));
    }

    /// <summary>
    /// Runs a raw SQL statement on a separate connection right before the executor's first UPDATE of Monitors, simulating an
    /// Edit/resume/regenerate that lands between the executor's reload and its write.
    /// </summary>
    public sealed class StampBumpInterceptor(string connectionString, string sql) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("UPDATE \"Monitors\"", StringComparison.Ordinal))
            {
                Fired = true;
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var bump = new NpgsqlCommand(sql, connection);
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<(string Subject, string Html)> Sent { get; } = [];

        public Task SendAsync(string from, IReadOnlyList<string> to, string subject, string html, CancellationToken cancellationToken)
        {
            Sent.Add((subject, html));
            return Task.CompletedTask;
        }
    }

    private sealed class SpyExecutor : IMonitorExecutor
    {
        public int Pings { get; private set; }

        public Task<MonitorExecutionResult?> ExecuteAsync(Guid monitorId, string? reason, CancellationToken cancellationToken, Guid? runId = null) =>
            Task.FromResult<MonitorExecutionResult?>(null);

        public Task<PingResult> RecordPingAsync(string token, PingRequestInfo info, CancellationToken cancellationToken)
        {
            Pings++;
            return Task.FromResult(PingResult.NotFound);
        }
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task SendAsync(string from, IReadOnlyList<string> to, string subject, string html, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
