// <file>
// <summary>
// Tests for the monitors overview: headline wording (pure) and MonitorBoardService (EF InMemory) for the failing list,
// the headline count, group counts and the 24 h strip cells (AE6).
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="BoardHeadline"/> and <see cref="MonitorBoardService"/>.
/// </summary>
public sealed class MonitorBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 7, 0, TimeSpan.Zero);

    [Fact]
    public void Headline_covers_none_one_several_down_all_paused_and_no_monitors()
    {
        Assert.Equal("No monitors yet.", BoardHeadline.For(0, 0, 0, null, null));
        Assert.Equal("All 31 monitors are up.", BoardHeadline.For(31, 0, 0, null, null));
        Assert.Equal("The monitor is up.", BoardHeadline.For(1, 0, 0, null, null));
        Assert.Equal("1 of 31 monitors is down. Longest outage: API health, 12 min.",
            BoardHeadline.For(31, 1, 0, "API health", TimeSpan.FromMinutes(12)));
        Assert.Equal("2 of 31 monitors are down. Longest outage: API health, 3 h 12 min.",
            BoardHeadline.For(31, 2, 4, "API health", TimeSpan.FromMinutes(192)));
        Assert.Equal("2 of 3 monitors are down.", BoardHeadline.For(3, 2, 0, null, null));
        Assert.Equal("All 4 monitors are paused or disabled.", BoardHeadline.For(4, 0, 4, null, null));
        Assert.Equal("All 3 active monitors are up. 1 paused or disabled.", BoardHeadline.For(4, 0, 1, null, null));
        Assert.Equal("1 monitor is up. 2 paused or disabled.", BoardHeadline.For(3, 0, 2, null, null));
        Assert.Equal("5 of 7 monitors are up. 1 overdue, 1 waiting for a first check.", BoardHeadline.For(7, 0, 0, null, null, 1, 1));
        Assert.Equal("1 of 3 monitors is up. 2 overdue.", BoardHeadline.For(3, 0, 0, null, null, 2, 0));
        Assert.Equal("1 of 2 active monitors is up. 1 waiting for a first check. 1 paused or disabled.", BoardHeadline.For(3, 0, 1, null, null, 0, 1));
    }

    [Theory]
    [InlineData(20, "under 1 min")]
    [InlineData(5 * 60, "5 min")]
    [InlineData(3 * 3600 + 12 * 60, "3 h 12 min")]
    [InlineData(2 * 86400 + 4 * 3600, "2 d 4 h")]
    public void Duration_is_compact(int seconds, string expected) =>
        Assert.Equal(expected, BoardHeadline.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task Failing_monitor_is_in_attention_and_headline_and_its_strip_has_a_red_cell()
    {
        await using var db = NewDb();
        var failing = Add(db, "API health", "Core");
        var healthy = Add(db, "Web", "Core");
        var paused = Add(db, "Batch", null, paused: true);
        var started = Now.AddHours(-3);
        db.MonitorRuns.Add(new MonitorRun { MonitorId = failing.Id, StartedAt = Now.AddMinutes(-1), FinishedAt = Now, Success = false, StatusCode = 500, ErrorMessage = "Unexpected status 500" });
        db.MonitorRuns.Add(new MonitorRun { MonitorId = healthy.Id, StartedAt = Now.AddMinutes(-1), FinishedAt = Now, Success = true, StatusCode = 200 });
        db.MonitorAlertStates.Add(new MonitorAlertState { MonitorId = failing.Id, FailureIncidentOpenedAt = started });
        var failBucket = RunBucketRollup.BucketStartOf(Now.AddMinutes(-1));
        db.MonitorRunBuckets.Add(new MonitorRunBucket { MonitorId = failing.Id, BucketStart = failBucket, RunCount = 2, FailureCount = 1, TotalDurationMs = 200 });
        db.MonitorRunBuckets.Add(new MonitorRunBucket { MonitorId = healthy.Id, BucketStart = failBucket, RunCount = 1, FailureCount = 0, TotalDurationMs = 50 });
        await db.SaveChangesAsync();

        var board = await new MonitorBoardService(db, NewStatus(db)).BuildAsync(Now, default);

        var card = Assert.Single(board.Attention);
        Assert.Equal(failing.Id, card.Monitor.Id);
        Assert.Equal("Unexpected status 500", card.Reason);
        Assert.Equal(started, card.Since);
        Assert.Equal(500, card.StatusCode);
        Assert.Equal("1 of 3 monitors is down. Longest outage: API health, 3 h 0 min.", board.Headline);

        Assert.Equal(["Core", null], board.Groups.Select(g => g.Name));
        var core = board.Groups[0];
        Assert.Equal(1, core.DownCount);
        Assert.Equal(2, core.Rows.Count);

        var row = core.Rows.Single(r => r.Monitor.Id == failing.Id);
        Assert.True(row.Failing);
        Assert.Equal("Down", row.StatusLabel);
        Assert.Equal(96, row.Strip.Cells.Count);
        Assert.Equal(1, row.Strip.FailedCount);
        var red = Assert.Single(row.Strip.Cells, c => c.State == StripCellState.Fail);
        Assert.Equal(100, red.HeightPercent);
        Assert.Equal(failBucket, red.Start);
        Assert.Equal(95, row.Strip.Cells.Count(c => c.State == StripCellState.Empty));
        Assert.Equal(100, row.LastMs);

        var ok = core.Rows.Single(r => r.Monitor.Id == healthy.Id);
        Assert.False(ok.Failing);
        Assert.Equal("Up", ok.StatusLabel);
        Assert.Equal("Paused", board.Groups[1].Rows.Single().StatusLabel);
        Assert.Equal(paused.Id, board.Groups[1].Rows.Single().Monitor.Id);
    }

    [Fact]
    public async Task An_overdue_heartbeat_is_not_up()
    {
        await using var db = NewDb();
        var beat = Add(db, "Beat", null);
        beat.Type = MonitorType.Heartbeat;
        beat.NextRunAt = Now.AddMinutes(-5);
        var ok = Add(db, "Web", null);
        db.MonitorRuns.Add(new MonitorRun { MonitorId = beat.Id, StartedAt = Now.AddHours(-2), FinishedAt = Now.AddHours(-2), Success = true });
        db.MonitorRuns.Add(new MonitorRun { MonitorId = ok.Id, StartedAt = Now.AddMinutes(-1), FinishedAt = Now, Success = true });
        await db.SaveChangesAsync();

        var board = await new MonitorBoardService(db, NewStatus(db)).BuildAsync(Now, default);

        var rows = board.Groups.Single().Rows;
        Assert.Equal("Overdue", rows.Single(r => r.Monitor.Id == beat.Id).StatusLabel);
        Assert.Equal("Up", rows.Single(r => r.Monitor.Id == ok.Id).StatusLabel);
        Assert.Equal("1 of 2 monitors is up. 1 overdue.", board.Headline);
    }

    [Fact]
    public async Task Empty_database_gives_the_empty_board()
    {
        await using var db = NewDb();
        var board = await new MonitorBoardService(db, NewStatus(db)).BuildAsync(Now, default);
        Assert.Equal("No monitors yet.", board.Headline);
        Assert.Empty(board.Groups);
        Assert.Empty(board.Attention);
    }

    [Fact]
    public async Task Groups_merge_case_insensitively_with_ungrouped_last_and_new_monitors_are_pending()
    {
        await using var db = NewDb();
        Add(db, "b", "shop");
        Add(db, "a", "SHOP");
        Add(db, "c", null);
        Add(db, "d", "alpha");
        await db.SaveChangesAsync();

        var board = await new MonitorBoardService(db, NewStatus(db)).BuildAsync(Now, default);

        Assert.Equal(["alpha", "SHOP", null], board.Groups.Select(g => g.Name));
        Assert.Equal(["a", "b"], board.Groups[1].Rows.Select(r => r.Monitor.Name));
        Assert.All(board.Groups.SelectMany(g => g.Rows), r => Assert.Equal("Pending", r.StatusLabel));
        Assert.Equal("0 of 4 monitors are up. 4 waiting for a first check.", board.Headline);
    }

    private static MonitorEntity Add(ApplicationDbContext db, string name, string? group, bool paused = false)
    {
        var m = new MonitorEntity
        {
            Name = name,
            Url = "https://example.com",
            Method = "GET",
            GroupName = group,
            IsPaused = paused,
            LastRunAt = Now.AddMinutes(-1),
        };
        db.Monitors.Add(m);
        return m;
    }

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options);

    private static MonitorStatusService NewStatus(ApplicationDbContext db) =>
        new(db, new ConfigurationBuilder().Build(), new FakeEnv());

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
