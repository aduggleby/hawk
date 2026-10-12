// <file>
// <summary>
// Integration tests against a real PostgreSQL 17 (Testcontainers, needs Docker; skipped when Docker is unavailable).
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
using Hawk.Web.Data;
using Hawk.Web.Infrastructure;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services;
using Hawk.Web.Services.Email;
using Hawk.Web.Services.Import;
using Hawk.Web.Services.Monitoring;
using Microsoft.AspNetCore.Http;
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
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

/// <summary>
/// Starts one PostgreSQL container for all tests in <see cref="PostgresIntegrationTests"/>.
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
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
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
public sealed class PostgresIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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
            var index = new IndexModel(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), new RecordingJobClient())
            {
                SelectedMonitorIds = [id],
                BatchAction = "pause",
            };
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

    // ---- helpers ----

    private sealed class TestApp(ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private async Task<TestApp> CreateAppAsync()
    {
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
        services.AddSingleton<JobStorage>(_ => DatabaseSetup.CreatePostgreSqlStorage(connectionString));
        var sp = services.BuildServiceProvider();

        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();

        return new TestApp(sp);
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

    private static async Task<MonitorForm> LoadEditFormAsync(TestApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var page = new EditModel(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), Production) { Id = id };
        InitPage(page);
        Assert.IsType<PageResult>(await page.OnGetAsync(CancellationToken.None));
        return page.Form;
    }

    private static async Task<(IActionResult Result, string? FlashError)> PostEditAsync(TestApp app, Guid id, MonitorForm form)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var page = new EditModel(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), Production) { Id = id, Form = form };
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

    private static async Task<MonitorExecutionResult?> ExecuteAsync(TestApp app, Guid id, HttpStatusCode status)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var executor = new MonitorExecutor(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            new FixedUrlChecker(status),
            new NoopEmailSender(),
            app.Services.GetRequiredService<JobStorage>(),
            scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(),
            NullLogger<MonitorExecutor>.Instance,
            app.Services.GetRequiredService<IConfiguration>());
        return await executor.ExecuteAsync(id, "schedule", CancellationToken.None);
    }

    private static async Task<Guid> AlertStampAsync(TestApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorAlertStates
            .Where(s => s.MonitorId == id)
            .Select(s => s.ConcurrencyStamp)
            .SingleAsync();
    }

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

    private sealed class FixedUrlChecker(HttpStatusCode status) : IUrlChecker
    {
        public Task<UrlCheckResult> CheckAsync(UrlCheckRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new UrlCheckResult(
                (int)status < 400, status, TimeSpan.FromMilliseconds(5), null, [], "ok",
                new Dictionary<string, string>(), "text/plain", 2));
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
