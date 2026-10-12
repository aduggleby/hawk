// <file>
// <summary>
// Unit tests for the app-managed optimistic concurrency token (Monitor/MonitorAlertState.ConcurrencyStamp).
// Uses the EF InMemory provider, which enforces concurrency tokens, to check the provider-agnostic contract:
// bookkeeping writes (NextRunAt/LastRunAt) never conflict with an open Edit form; real user edits do.
// The Edit page itself is exercised against PostgreSQL in PostgresIntegrationTests.
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

/// <summary>
/// Tests for the concurrency token configuration and semantics.
/// </summary>
public sealed class ConcurrencyStampTests
{
    [Fact]
    public void ConcurrencyStamp_is_the_only_concurrency_token_and_is_not_store_generated()
    {
        using var db = CreateDb(Guid.NewGuid().ToString("n"));

        foreach (var clr in new[] { typeof(MonitorEntity), typeof(MonitorAlertState) })
        {
            var entity = db.Model.FindEntityType(clr)!;
            var tokens = entity.GetProperties().Where(p => p.IsConcurrencyToken).ToList();

            var token = Assert.Single(tokens);
            Assert.Equal("ConcurrencyStamp", token.Name);
            Assert.Equal(typeof(Guid), token.ClrType);
            Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, token.ValueGenerated);
            Assert.Null(entity.FindProperty("RowVersion"));
        }
    }

    [Fact]
    public void New_entities_get_distinct_stamps()
    {
        var a = NewMonitor();
        var b = NewMonitor();
        Assert.NotEqual(Guid.Empty, a.ConcurrencyStamp);
        Assert.NotEqual(a.ConcurrencyStamp, b.ConcurrencyStamp);
        Assert.NotEqual(Guid.Empty, new MonitorAlertState().ConcurrencyStamp);
    }

    [Fact]
    public async Task Edit_succeeds_after_scheduler_and_executor_bookkeeping()
    {
        var dbName = Guid.NewGuid().ToString("n");
        var id = await SeedMonitorAsync(dbName);
        var formStamp = await ReadStampAsync(dbName, id);

        // A run finishes while the Edit form is open: NextRunAt/LastRunAt change, the stamp does not.
        await using (var worker = CreateDb(dbName))
        {
            var m = await worker.Monitors.SingleAsync(x => x.Id == id);
            m.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(60);
            m.LastRunAt = DateTimeOffset.UtcNow;
            await worker.SaveChangesAsync();
        }

        await SaveEditAsync(dbName, id, formStamp, "Edited name");

        await using var check = CreateDb(dbName);
        var saved = await check.Monitors.SingleAsync(x => x.Id == id);
        Assert.Equal("Edited name", saved.Name);
        Assert.NotEqual(formStamp, saved.ConcurrencyStamp);
    }

    [Fact]
    public async Task Edit_conflicts_with_a_concurrent_user_edit()
    {
        var dbName = Guid.NewGuid().ToString("n");
        var id = await SeedMonitorAsync(dbName);
        var formStampA = await ReadStampAsync(dbName, id);
        var formStampB = await ReadStampAsync(dbName, id);

        // User B saves first and renews the stamp.
        await SaveEditAsync(dbName, id, formStampB, "B wins");

        // User A still holds the old stamp.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => SaveEditAsync(dbName, id, formStampA, "A loses"));

        await using var check = CreateDb(dbName);
        Assert.Equal("B wins", (await check.Monitors.SingleAsync(x => x.Id == id)).Name);
    }

    /// <summary>
    /// Mirrors the Edit page: the stamp from the form is the original value and a fresh stamp is written.
    /// </summary>
    private static async Task SaveEditAsync(string dbName, Guid id, Guid formStamp, string newName)
    {
        await using var db = CreateDb(dbName);
        var m = await db.Monitors.SingleAsync(x => x.Id == id);
        db.Entry(m).Property(x => x.ConcurrencyStamp).OriginalValue = formStamp;
        m.ConcurrencyStamp = Guid.NewGuid();
        m.Name = newName;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedMonitorAsync(string dbName)
    {
        await using var db = CreateDb(dbName);
        var m = NewMonitor();
        db.Monitors.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    private static async Task<Guid> ReadStampAsync(string dbName, Guid id)
    {
        await using var db = CreateDb(dbName);
        return await db.Monitors.Where(x => x.Id == id).Select(x => x.ConcurrencyStamp).SingleAsync();
    }

    private static MonitorEntity NewMonitor() => new()
    {
        Name = "Example",
        Url = "https://example.com",
        Method = "GET",
    };

    private static ApplicationDbContext CreateDb(string dbName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options);
}
