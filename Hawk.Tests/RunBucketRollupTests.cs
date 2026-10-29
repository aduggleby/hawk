// <file>
// <summary>
// Tests for the run-bucket rollup: pure bucketing and merge rule, the InMemory recompute/refresh/backfill logic,
// the StatusCake import recompute, and (PostgreSQL) cascade delete, the lock, concurrent writers and job registration.
// </summary>
// </file>

using System.Text;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Infrastructure;
using Hawk.Web.Services.Import;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="RunBucketRollup"/> that need no database server.
/// </summary>
public sealed class RunBucketRollupTests
{
    private static readonly Guid MonitorA = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private static MonitorRunBucket Bucket(DateTimeOffset start, int runs, int failures = 0, long durationMs = 0) =>
        new() { MonitorId = MonitorA, BucketStart = start, RunCount = runs, FailureCount = failures, TotalDurationMs = durationMs };

    // ---- pure functions ----

    [Fact]
    public void Bucket_splits_at_15_minute_boundaries_and_aggregates()
    {
        var b = RunBucketRollup.Bucket(
        [
            new RunSample(MonitorA, Utc(2026, 3, 1, 10, 14, 59), true, 100),
            new RunSample(MonitorA, Utc(2026, 3, 1, 10, 0, 0), false, 300),
            new RunSample(MonitorA, Utc(2026, 3, 1, 10, 15, 0), true, 50),
        ]);

        Assert.Equal(2, b.Count);
        Assert.Equal(Utc(2026, 3, 1, 10, 0, 0), b[0].BucketStart);
        Assert.Equal((2, 1, 400L), (b[0].RunCount, b[0].FailureCount, b[0].TotalDurationMs));
        Assert.Equal(Utc(2026, 3, 1, 10, 15, 0), b[1].BucketStart);
        Assert.Equal((1, 0, 50L), (b[1].RunCount, b[1].FailureCount, b[1].TotalDurationMs));
    }

    [Fact]
    public void Bucket_normalises_offsets_to_utc()
    {
        var b = RunBucketRollup.Bucket([new RunSample(MonitorA, new DateTimeOffset(2026, 3, 1, 12, 20, 0, TimeSpan.FromHours(2)), true, 1)]);
        Assert.Equal(TimeSpan.Zero, b[0].BucketStart.Offset);
        Assert.Equal(Utc(2026, 3, 1, 10, 15, 0), b[0].BucketStart);
    }

    [Fact]
    public void Merge_inserts_when_missing_and_overwrites_recent_buckets_with_lower_counts()
    {
        var now = Utc(2026, 3, 2, 12, 0, 0);
        var recent = Utc(2026, 3, 2, 10, 0, 0);

        Assert.Equal(RunBucketWrite.Insert, RunBucketRollup.Merge(null, Bucket(Utc(2026, 1, 1, 0, 0, 0), 1), now));
        Assert.Equal(RunBucketWrite.Overwrite, RunBucketRollup.Merge(Bucket(recent, 5), Bucket(recent, 3), now));
    }

    [Fact]
    public void Merge_keeps_old_buckets_unless_the_computed_count_is_higher()
    {
        var now = Utc(2026, 3, 2, 12, 0, 0);
        var old = now.AddHours(-23).AddMinutes(-1);

        Assert.Equal(RunBucketWrite.None, RunBucketRollup.Merge(Bucket(old, 5), Bucket(old, 3), now));
        Assert.Equal(RunBucketWrite.None, RunBucketRollup.Merge(Bucket(old, 5), Bucket(old, 5), now));
        Assert.Equal(RunBucketWrite.Overwrite, RunBucketRollup.Merge(Bucket(old, 3), Bucket(old, 5), now));
    }

    // ---- InMemory logic ----

    [Fact]
    public async Task Recompute_counts_only_completed_runs_and_is_idempotent()
    {
        var (db, rollup, id) = await CreateAsync();
        var t = RunBucketRollup.BucketStartOf(DateTimeOffset.UtcNow).AddHours(-3);
        AddRun(db, id, t.AddSeconds(5), true, 100);
        AddRun(db, id, t.AddMinutes(14).AddSeconds(59), false, 200);
        AddRun(db, id, t.AddMinutes(15), true, 50);
        AddRun(db, id, t.AddMinutes(16), true, 50, state: "running");
        AddRun(db, id, t.AddMinutes(17), true, 50, state: "queued");
        await db.SaveChangesAsync();

        for (var i = 0; i < 2; i++)
        {
            await rollup.RecomputeAsync(t, t.AddHours(1), CancellationToken.None);
            var rows = await db.MonitorRunBuckets.AsNoTracking().OrderBy(b => b.BucketStart).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal((t, 2, 1, 300L), (rows[0].BucketStart, rows[0].RunCount, rows[0].FailureCount, rows[0].TotalDurationMs));
            Assert.Equal((t.AddMinutes(15), 1, 0, 50L), (rows[1].BucketStart, rows[1].RunCount, rows[1].FailureCount, rows[1].TotalDurationMs));
        }
    }

    [Fact]
    public async Task Refresh_counts_a_run_that_completes_after_a_previous_refresh()
    {
        var (db, rollup, id) = await CreateAsync();
        var started = DateTimeOffset.UtcNow.AddMinutes(-6);
        var run = AddRun(db, id, started, true, 0, state: "running");
        await db.SaveChangesAsync();

        await rollup.RefreshAsync(CancellationToken.None);
        Assert.Empty(await db.MonitorRunBuckets.ToListAsync());

        // The run finishes 4 minutes after it started; the next refresh still covers its start time.
        run.State = "completed";
        run.DurationMs = 240_000;
        await db.SaveChangesAsync();
        await rollup.RefreshAsync(CancellationToken.None);

        var bucket = Assert.Single(await db.MonitorRunBuckets.ToListAsync());
        Assert.Equal((1, 240_000L), (bucket.RunCount, bucket.TotalDurationMs));
    }

    [Fact]
    public async Task Recompute_counts_legacy_runs_with_an_empty_state_and_covers_every_monitor()
    {
        var (db, rollup, id) = await CreateAsync();
        var other = new MonitorEntity { Name = "O", Url = "https://example.com", Method = "GET", IntervalSeconds = 60 };
        db.Monitors.Add(other);
        var t = RunBucketRollup.BucketStartOf(DateTimeOffset.UtcNow).AddHours(-3);
        AddRun(db, id, t.AddSeconds(5), true, 10, state: "");
        AddRun(db, other.Id, t.AddHours(1), false, 20);
        await db.SaveChangesAsync();

        await rollup.RecomputeAsync(t, t.AddDays(3), CancellationToken.None);

        var rows = await db.MonitorRunBuckets.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Single(b => b.MonitorId == id).RunCount);
        Assert.Equal(1, rows.Single(b => b.MonitorId == other.Id).FailureCount);
    }

    [Fact]
    public async Task Refresh_clamps_its_look_back_to_7_days()
    {
        var (db, rollup, id) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var newest = RunBucketRollup.BucketStartOf(now.AddDays(-10));
        db.MonitorRunBuckets.Add(new MonitorRunBucket { MonitorId = id, BucketStart = newest, RunCount = 1 });
        AddRun(db, id, now.AddDays(-9), true, 1);
        AddRun(db, id, now.AddDays(-6), true, 1);
        await db.SaveChangesAsync();

        await rollup.RefreshAsync(CancellationToken.None);

        var starts = (await db.MonitorRunBuckets.AsNoTracking().Select(b => b.BucketStart).ToListAsync()).OrderBy(x => x).ToList();
        Assert.Equal([newest, RunBucketRollup.BucketStartOf(now.AddDays(-6))], starts);
    }

    [Fact]
    public async Task Refresh_overwrites_recent_buckets_with_lower_counts()
    {
        var (db, rollup, id) = await CreateAsync();
        var start = RunBucketRollup.BucketStartOf(DateTimeOffset.UtcNow).AddMinutes(-15);
        db.MonitorRunBuckets.Add(new MonitorRunBucket { MonitorId = id, BucketStart = start, RunCount = 9, FailureCount = 9, TotalDurationMs = 9 });
        AddRun(db, id, start.AddMinutes(1), true, 10);
        await db.SaveChangesAsync();

        await rollup.RefreshAsync(CancellationToken.None);

        var row = Assert.Single(await db.MonitorRunBuckets.AsNoTracking().ToListAsync());
        Assert.Equal((1, 0, 10L), (row.RunCount, row.FailureCount, row.TotalDurationMs));
    }

    [Fact]
    public async Task Backfill_keeps_old_buckets_after_their_runs_were_pruned()
    {
        var (db, rollup, id) = await CreateAsync();
        var t = RunBucketRollup.BucketStartOf(DateTimeOffset.UtcNow).AddDays(-2);
        foreach (var i in Enumerable.Range(0, 4))
            AddRun(db, id, t.AddMinutes(i), i != 0, 10);
        await db.SaveChangesAsync();
        await rollup.BackfillAsync(CancellationToken.None);

        // Pruning removes all but one run, then the next startup backfills again.
        db.MonitorRuns.RemoveRange(await db.MonitorRuns.Skip(1).ToListAsync());
        await db.SaveChangesAsync();
        await rollup.BackfillAsync(CancellationToken.None);

        var row = Assert.Single(await db.MonitorRunBuckets.AsNoTracking().ToListAsync());
        Assert.Equal((4, 1, 40L), (row.RunCount, row.FailureCount, row.TotalDurationMs));
    }

    [Fact]
    public async Task Refresh_repairs_a_gap_since_the_newest_bucket()
    {
        var (db, rollup, id) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var newest = RunBucketRollup.BucketStartOf(now.AddHours(-2));
        db.MonitorRunBuckets.Add(new MonitorRunBucket { MonitorId = id, BucketStart = newest, RunCount = 1 });
        // One run per 15-minute interval from the newest bucket until now.
        var expected = new List<DateTimeOffset>();
        for (var s = newest; s < now; s = s.AddMinutes(15))
        {
            AddRun(db, id, s, true, 1); // at s: s.AddMinutes(1) can lie in the future
            expected.Add(s);
        }
        await db.SaveChangesAsync();

        await rollup.RefreshAsync(CancellationToken.None);

        var starts = (await db.MonitorRunBuckets.AsNoTracking().Select(b => b.BucketStart).ToListAsync()).OrderBy(x => x).ToList();
        Assert.Equal(expected, starts);
    }

    [Fact]
    public async Task Backfill_over_three_days_equals_incremental_recomputes()
    {
        var (dbA, rollupA, idA) = await CreateAsync();
        var (dbB, rollupB, idB) = await CreateAsync();
        var t0 = RunBucketRollup.BucketStartOf(DateTimeOffset.UtcNow).AddDays(-3);
        var rnd = new Random(7);
        for (var i = 0; i < 400; i++)
        {
            var at = t0.AddMinutes(rnd.Next(0, 3 * 24 * 60 - 20)).AddSeconds(rnd.Next(60));
            var ok = rnd.Next(5) != 0;
            var ms = rnd.Next(10, 900);
            AddRun(dbA, idA, at, ok, ms);
            AddRun(dbB, idB, at, ok, ms);
        }
        await dbA.SaveChangesAsync();
        await dbB.SaveChangesAsync();

        await rollupA.BackfillAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        for (var s = t0; s < now; s = s.AddHours(5))
            await rollupB.RecomputeAsync(s, s.AddHours(5) < now ? s.AddHours(5) : now, CancellationToken.None);

        static List<(long, int, int, long)> Rows(ApplicationDbContext db) =>
            [.. db.MonitorRunBuckets.AsNoTracking().AsEnumerable()
                .OrderBy(b => b.BucketStart)
                .Select(b => (b.BucketStart.UtcTicks, b.RunCount, b.FailureCount, b.TotalDurationMs))];
        Assert.NotEmpty(Rows(dbA));
        Assert.Equal(Rows(dbA), Rows(dbB));
    }

    [Fact]
    public async Task Refresh_deletes_buckets_older_than_95_days()
    {
        var (db, rollup, id) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        db.MonitorRunBuckets.AddRange(
            new MonitorRunBucket { MonitorId = id, BucketStart = RunBucketRollup.BucketStartOf(now.AddDays(-96)), RunCount = 1 },
            new MonitorRunBucket { MonitorId = id, BucketStart = RunBucketRollup.BucketStartOf(now.AddDays(-94)), RunCount = 1 });
        await db.SaveChangesAsync();

        await rollup.RefreshAsync(CancellationToken.None);

        var row = Assert.Single(await db.MonitorRunBuckets.AsNoTracking().ToListAsync());
        Assert.Equal(RunBucketRollup.BucketStartOf(now.AddDays(-94)), row.BucketStart);
    }

    [Fact]
    public async Task Jobs_do_nothing_when_the_scheduler_is_disabled()
    {
        var (db, rollup, id) = await CreateAsync(schedulerEnabled: false);
        var t = RunBucketRollup.BucketStartOf(DateTimeOffset.UtcNow).AddMinutes(-10);
        AddRun(db, id, t, true, 1);
        await db.SaveChangesAsync();

        await rollup.RefreshAsync(CancellationToken.None);
        await rollup.BackfillAsync(CancellationToken.None);
        await rollup.RecomputeAsync(t, t.AddHours(1), CancellationToken.None);

        Assert.Empty(await db.MonitorRunBuckets.ToListAsync());
    }

    [Fact]
    public async Task StatusCake_import_enqueues_a_recompute_that_fills_buckets_for_old_runs()
    {
        var (db, rollup, _) = await CreateAsync();
        var jobs = new RecordingJobClient();
        var importer = new StatusCakeImporter(db, new FakeEnv(), NullLogger<StatusCakeImporter>.Instance, jobs);
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("""
            { "data": [ { "id": "321", "name": "Imported", "website_url": "https://example.com", "check_rate": 60 } ] }
            """)))
            await importer.ImportTestsAsync(ms, "user-1", CancellationToken.None);

        var down = DateTimeOffset.UtcNow.AddDays(-5);
        var json = $$"""
            [ { "test_id": "321", "data": [
              { "status": "down", "status_code": 500, "triggered_at": "{{down:O}}" },
              { "status": "up", "status_code": 200, "triggered_at": "{{down.AddHours(1):O}}" }
            ] } ]
            """;
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            Assert.Equal(2, (await importer.ImportAlertsAsync(ms, CancellationToken.None)).RunsCreated);

        Assert.Empty(await db.MonitorRunBuckets.ToListAsync());
        var (from, to) = jobs.RecomputeRanges.Single();
        Assert.True(from <= down && to >= down.AddHours(1));
        await rollup.RecomputeAsync(from, to, CancellationToken.None);

        var rows = await db.MonitorRunBuckets.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Sum(r => r.FailureCount));
    }

    // ---- helpers ----

    private static async Task<(ApplicationDbContext Db, RunBucketRollup Rollup, Guid MonitorId)> CreateAsync(bool schedulerEnabled = true)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options);
        var monitor = new MonitorEntity { Name = "M", Url = "https://example.com", Method = "GET", IntervalSeconds = 60 };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Hawk:Scheduler:Enabled"] = schedulerEnabled ? "true" : "false" })
            .Build();
        return (db, new RunBucketRollup(db, config, NullLogger<RunBucketRollup>.Instance), monitor.Id);
    }

    private static MonitorRun AddRun(ApplicationDbContext db, Guid monitorId, DateTimeOffset startedAt, bool success, int durationMs, string state = "completed")
    {
        var run = new MonitorRun
        {
            MonitorId = monitorId,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddMilliseconds(durationMs),
            State = state,
            Success = success,
            DurationMs = durationMs,
            Reason = "schedule",
        };
        db.MonitorRuns.Add(run);
        return run;
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<(DateTimeOffset From, DateTimeOffset To)> RecomputeRanges { get; } = [];

        public string Create(Job job, IState state)
        {
            if (job.Method.Name == nameof(IRunBucketRollup.RecomputeAsync))
                RecomputeRanges.Add(((DateTimeOffset)job.Args[0]!, (DateTimeOffset)job.Args[1]!));
            return RecomputeRanges.Count.ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}

/// <summary>
/// PostgreSQL tests for <see cref="RunBucketRollup"/> (real cascade, Hangfire.PostgreSql locks and recurring jobs).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunBucketRollupPostgresTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Deleting_a_monitor_set_based_cascades_its_buckets()
    {
        await using var app = await CreateAppAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (keep, drop) = (await AddMonitorAsync(db), await AddMonitorAsync(db));
        var start = RunBucketRollup.BucketStartOf(DateTimeOffset.UtcNow);
        db.MonitorRunBuckets.AddRange(
            new MonitorRunBucket { MonitorId = keep, BucketStart = start, RunCount = 1 },
            new MonitorRunBucket { MonitorId = drop, BucketStart = start, RunCount = 1 });
        await db.SaveChangesAsync();

        await db.Monitors.Where(m => m.Id == drop).ExecuteDeleteAsync();

        Assert.Equal(keep, Assert.Single(await db.MonitorRunBuckets.ToListAsync()).MonitorId);
    }

    [SkippableFact]
    public async Task Concurrent_backfill_and_refresh_both_complete_with_correct_rows()
    {
        await using var app = await CreateAppAsync();
        Guid id;
        var t = RunBucketRollup.BucketStartOf(DateTimeOffset.UtcNow).AddHours(-3);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            id = await AddMonitorAsync(db);
            for (var i = 0; i < 12; i++)
                db.MonitorRuns.Add(new MonitorRun { MonitorId = id, StartedAt = t.AddMinutes(i * 5), FinishedAt = t.AddMinutes(i * 5), State = "completed", Success = i % 4 != 0, DurationMs = 100, Reason = "schedule" });
            await db.SaveChangesAsync();
        }

        async Task Run(Func<RunBucketRollup, Task> job)
        {
            await using var s = app.Services.CreateAsyncScope();
            await job(app.NewRollup(s));
        }

        await Task.WhenAll(
            Run(r => r.BackfillAsync(CancellationToken.None)),
            Run(r => r.RefreshAsync(CancellationToken.None)),
            Run(r => r.RefreshAsync(CancellationToken.None)));

        // Whichever jobs skipped on the busy lock, one more refresh leaves exactly the correct rows.
        await Run(r => r.BackfillAsync(CancellationToken.None));
        await using var check = app.Services.CreateAsyncScope();
        var rows = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorRunBuckets.OrderBy(b => b.BucketStart).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, b => Assert.Equal(3, b.RunCount));
        Assert.Equal(3, rows.Sum(b => b.FailureCount));
        Assert.Equal(t, rows[0].BucketStart);
    }

    [SkippableFact]
    public async Task A_busy_lock_skips_the_job()
    {
        await using var app = await CreateAppAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var id = await AddMonitorAsync(db);
        var t = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.MonitorRuns.Add(new MonitorRun { MonitorId = id, StartedAt = t, FinishedAt = t, State = "completed", Success = true, Reason = "schedule" });
        await db.SaveChangesAsync();

        var storage = app.Services.GetRequiredService<JobStorage>();
        using (var holder = storage.GetConnection())
        using (holder.AcquireDistributedLock("hawk:run-buckets", TimeSpan.FromSeconds(60)))
            await app.NewRollup(scope).RefreshAsync(CancellationToken.None);
        Assert.Empty(await db.MonitorRunBuckets.ToListAsync());

        // The one-shot jobs wait for the lock and then fail, so Hangfire retries them.
        using (var holder = storage.GetConnection())
        using (holder.AcquireDistributedLock("hawk:run-buckets", TimeSpan.FromSeconds(60)))
        {
            var oneShot = app.NewRollup(scope);
            oneShot.OneShotLockTimeout = TimeSpan.FromSeconds(1);
            await Assert.ThrowsAnyAsync<DistributedLockTimeoutException>(() => oneShot.RecomputeAsync(t.AddHours(-1), t.AddHours(1), CancellationToken.None));
            await Assert.ThrowsAnyAsync<DistributedLockTimeoutException>(() => oneShot.BackfillAsync(CancellationToken.None));
        }
        Assert.Empty(await db.MonitorRunBuckets.ToListAsync());

        await app.NewRollup(scope).RefreshAsync(CancellationToken.None);
        Assert.Single(await db.MonitorRunBuckets.ToListAsync());
    }

    [SkippableFact]
    public async Task Startup_registers_the_recurring_job_or_removes_it_when_scheduling_is_off()
    {
        await using var app = await CreateAppAsync();
        var storage = app.Services.GetRequiredService<JobStorage>();
        var recurring = new RecurringJobManager(storage);
        var client = new BackgroundJobClient(storage);

        bool Exists()
        {
            using var conn = storage.GetConnection();
            return conn.GetRecurringJobs().Any(j => j.Id == RunBucketRollup.RecurringJobId);
        }

        RunBucketRollup.EnsureStarted(recurring, client, schedulerEnabled: true);
        Assert.True(Exists());

        RunBucketRollup.EnsureStarted(recurring, client, schedulerEnabled: false);
        Assert.False(Exists());
    }

    // ---- helpers ----

    private sealed class TestApp(ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public RunBucketRollup NewRollup(AsyncServiceScope scope) => new(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            Services.GetRequiredService<IConfiguration>(),
            NullLogger<RunBucketRollup>.Instance,
            Services.GetRequiredService<JobStorage>());

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private async Task<TestApp> CreateAppAsync()
    {
        if (fixture.UnavailableReason is not null && Environment.GetEnvironmentVariable("HAWK_REQUIRE_POSTGRES_TESTS") == "1")
            throw new InvalidOperationException($"HAWK_REQUIRE_POSTGRES_TESTS=1 but {fixture.UnavailableReason}");
        Skip.If(fixture.UnavailableReason is not null, fixture.UnavailableReason);

        var connectionString = fixture.NewDatabaseConnectionString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddHawkDbContext(DatabaseProvider.Postgres, connectionString);
        services.AddHawkIdentity();
        services.AddSingleton<JobStorage>(_ => DatabaseSetup.CreatePostgreSqlStorage(connectionString));
        var sp = services.BuildServiceProvider();

        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
        return new TestApp(sp);
    }

    private static async Task<Guid> AddMonitorAsync(ApplicationDbContext db)
    {
        var m = new MonitorEntity { Name = "M", Url = "https://example.com", Method = "GET", IntervalSeconds = 60 };
        db.Monitors.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }
}
