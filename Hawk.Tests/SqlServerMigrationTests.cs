// <file>
// <summary>
// Migration tests against a real SQL Server 2022 (Testcontainers, needs Docker; skipped when Docker is unavailable,
// failed instead when HAWK_REQUIRE_SQLSERVER_TESTS=1). Covers the SQL Server migration set, which the PostgreSQL
// integration tests do not apply.
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

/// <summary>
/// Starts one SQL Server container for <see cref="SqlServerMigrationTests"/>.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    /// <summary>Why the tests are skipped (Docker unavailable), or null.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Connection string for the server's master database.</summary>
    public string ConnectionString => _container!.GetConnectionString();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        try
        {
            // Same image as docker-compose.yml.
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            UnavailableReason = $"SQL Server test container unavailable (Docker required): {ex.Message}";
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>
/// SQL Server migration tests.
/// </summary>
public sealed class SqlServerMigrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [SkippableFact]
    public async Task Alerts_enabled_migration_sets_existing_monitors_to_true()
    {
        if (fixture.UnavailableReason is not null && Environment.GetEnvironmentVariable("HAWK_REQUIRE_SQLSERVER_TESTS") == "1")
            throw new InvalidOperationException($"HAWK_REQUIRE_SQLSERVER_TESTS=1 but {fixture.UnavailableReason}");
        Skip.If(fixture.UnavailableReason is not null, fixture.UnavailableReason);

        var connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"hawk_test_{Guid.NewGuid():N}",
        }.ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddHawkDbContext(DatabaseProvider.SqlServer, connectionString);
        services.AddHawkIdentity();
        await using var sp = services.BuildServiceProvider();

        Guid id;
        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.MigrateAsync();
            var m = new MonitorEntity { Name = "Existing", Url = "https://example.com/", Method = "GET", AlertsEnabled = false };
            db.Monitors.Add(m);
            await db.SaveChangesAsync();
            id = m.Id;

            // Back to the schema before the column existed and forward again: the row now gets the column default.
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261029121806_AddPendingAlerts");
            await migrator.MigrateAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True((await db.Monitors.AsNoTracking().SingleAsync(m => m.Id == id)).AlertsEnabled);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
    }
}
