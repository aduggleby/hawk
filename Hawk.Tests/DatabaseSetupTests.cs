// <file>
// <summary>
// Unit tests for database provider selection (Hawk:Database:Provider) and the per-provider migration sets.
// None of these tests connect to a database.
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="DatabaseSetup"/>.
/// </summary>
public sealed class DatabaseSetupTests
{
    private const string SqlServerConnection = "Server=localhost;Database=HawkUnit;User Id=sa;Password=x;TrustServerCertificate=true";
    private const string PostgresConnection = "Host=localhost;Database=hawk_unit;Username=hawk;Password=x";

    [Theory]
    [InlineData(null, DatabaseProvider.SqlServer)]
    [InlineData("", DatabaseProvider.SqlServer)]
    [InlineData("  ", DatabaseProvider.SqlServer)]
    [InlineData("SqlServer", DatabaseProvider.SqlServer)]
    [InlineData("sqlserver", DatabaseProvider.SqlServer)]
    [InlineData("Postgres", DatabaseProvider.Postgres)]
    [InlineData(" postgres ", DatabaseProvider.Postgres)]
    public void GetProvider_accepts_supported_values_and_defaults_to_SqlServer(string? value, DatabaseProvider expected)
    {
        Assert.Equal(expected, DatabaseSetup.GetProvider(Config(value)));
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("Npgsql")]
    [InlineData("Sqlite")]
    [InlineData("mssql")]
    public void GetProvider_rejects_unknown_values(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSetup.GetProvider(Config(value)));
        Assert.Contains(value, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Hawk:Database:Provider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_registers_ApplicationDbContext_with_SqlServer_migrations_only()
    {
        using var sp = BuildServices(DatabaseProvider.SqlServer, SqlServerConnection);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Equal(typeof(ApplicationDbContext), db.GetType());
        Assert.True(db.Database.IsSqlServer());

        var migrations = db.Database.GetMigrations().ToList();
        Assert.Contains("20260209064151_InitialSqlServer", migrations);
        Assert.Contains(migrations, m => m.EndsWith("_ReplaceRowVersionWithConcurrencyStamp", StringComparison.Ordinal));
        Assert.DoesNotContain(migrations, m => m.EndsWith("_InitialPostgres", StringComparison.Ordinal));
    }

    [Fact]
    public void Postgres_registers_PostgresApplicationDbContext_with_Postgres_migrations_only()
    {
        using var sp = BuildServices(DatabaseProvider.Postgres, PostgresConnection);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.IsType<PostgresApplicationDbContext>(db);
        Assert.True(db.Database.IsNpgsql());

        var migrations = db.Database.GetMigrations().ToList();
        Assert.NotEmpty(migrations);
        Assert.Contains(migrations, m => m.EndsWith("_InitialPostgres", StringComparison.Ordinal));
        Assert.DoesNotContain("20260209064151_InitialSqlServer", migrations);
    }

    /// <summary>
    /// Guards against forgetting to add a migration for one of the providers after a model change
    /// (MigrateAsync would otherwise fail at startup with PendingModelChangesWarning).
    /// </summary>
    [Theory]
    [InlineData(DatabaseProvider.SqlServer, SqlServerConnection)]
    [InlineData(DatabaseProvider.Postgres, PostgresConnection)]
    public void Migrations_are_up_to_date_with_the_model(DatabaseProvider provider, string connectionString)
    {
        using var sp = BuildServices(provider, connectionString);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.False(db.Database.HasPendingModelChanges(), $"{provider} migrations are missing model changes. Run `dotnet ef migrations add` for both providers (see AGENTS.md).");
    }

    [Fact]
    public void Design_time_factories_build_the_same_model_as_the_app()
    {
        using var sqlServer = new SqlServerDesignTimeDbContextFactory().CreateDbContext([]);
        using var postgres = new PostgresDesignTimeDbContextFactory().CreateDbContext([]);

        Assert.False(sqlServer.Database.HasPendingModelChanges());
        Assert.False(postgres.Database.HasPendingModelChanges());
    }

    private static ServiceProvider BuildServices(DatabaseProvider provider, string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHawkDbContext(provider, connectionString);
        services.AddHawkIdentity();
        return services.BuildServiceProvider();
    }

    private static IConfiguration Config(string? provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DatabaseSetup.ProviderKey] = provider })
            .Build();
}
