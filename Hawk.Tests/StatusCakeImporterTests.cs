// <file>
// <summary>
// Unit tests for importing StatusCake exports into Hawk monitors and run history.
// </summary>
// </file>

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Hawk.Web.Data;
using Hawk.Web.Services.Import;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="StatusCakeImporter"/>.
/// </summary>
public sealed class StatusCakeImporterTests
{
    /// <summary>
    /// Imports a minimal StatusCake uptime tests JSON export and creates monitors.
    /// </summary>
    [Fact]
    public async Task ImportTests_creates_monitors_and_maps_interval()
    {
        var (db, importer) = Create();
        var json = """
            {
              "data": [
                { "id": "123", "name": "Example OK", "website_url": "https://example.com", "check_rate": 60, "paused": false },
                { "id": "124", "name": "Example Slow", "website_url": "https://example.com", "check_rate": 240, "paused": true }
              ]
            }
            """;

        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var res = await importer.ImportTestsAsync(ms, "user-1", CancellationToken.None);

        Assert.Equal(2, res.MonitorsCreated);
        Assert.Empty(res.Warnings.Where(w => w.Contains("Skipped", StringComparison.OrdinalIgnoreCase)));

        var monitors = await db.Monitors.OrderBy(m => m.Name).ToListAsync();
        Assert.Contains(monitors, m => m.Name.Contains("(sc:123)", StringComparison.Ordinal));
        Assert.Contains(monitors, m => m.Name.Contains("(sc:124)", StringComparison.Ordinal));

        // 240s is not an allowed interval in Production, so it should map up to 300s.
        var m124 = monitors.Single(m => m.Name.Contains("(sc:124)", StringComparison.Ordinal));
        Assert.Equal(300, m124.IntervalSeconds);
        Assert.False(m124.Enabled);
    }

    /// <summary>
    /// Imports alert history into run records for an already-imported monitor.
    /// </summary>
    [Fact]
    public async Task ImportAlerts_creates_runs_and_sets_last_run()
    {
        var (db, importer) = Create();
        var testsJson = """
            { "data": [ { "id": "999", "name": "Example", "website_url": "https://example.com", "check_rate": 60 } ] }
            """;
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(testsJson)))
        {
            await importer.ImportTestsAsync(ms, "user-1", CancellationToken.None);
        }

        var alertsJson = """
            [
              {
                "test_id": "999",
                "data": [
                  { "status": "down", "status_code": 503, "triggered_at": "2026-02-09T00:00:00Z" }
                ]
              }
            ]
            """;

        await using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(alertsJson));
        var res = await importer.ImportAlertsAsync(ms2, CancellationToken.None);
        Assert.Equal(1, res.RunsCreated);

        var monitor = await db.Monitors.SingleAsync(m => m.Name.Contains("(sc:999)", StringComparison.Ordinal));
        Assert.Equal(DateTimeOffset.Parse("2026-02-09T00:00:00Z"), monitor.LastRunAt);

        var run = await db.MonitorRuns.SingleAsync(r => r.MonitorId == monitor.Id);
        Assert.False(run.Success);
        Assert.Equal(503, run.StatusCode);
        Assert.StartsWith("Imported StatusCake alert", run.ErrorMessage);
    }

    private static (ApplicationDbContext Db, StatusCakeImporter Importer) Create()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;

        var db = new ApplicationDbContext(opts);
        var env = new FakeEnv("Production");
        var importer = new StatusCakeImporter(db, env, NullLogger<StatusCakeImporter>.Instance);
        return (db, importer);
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
