// <file>
// <summary>
// Database provider selection (Hawk:Database:Provider) for EF Core, Identity and Hangfire.
// - SqlServer (default; existing installs are unchanged): ApplicationDbContext + Hangfire.SqlServer.
// - Postgres: PostgresApplicationDbContext (registered as ApplicationDbContext) + Hangfire.PostgreSql.
// One connection string (ConnectionStrings:DefaultConnection) is used for everything.
// Any other provider value is a startup error.
// </summary>
// </file>

using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.SqlServer;
using Hawk.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Infrastructure;

/// <summary>
/// Supported database providers.
/// </summary>
public enum DatabaseProvider
{
    /// <summary>Microsoft SQL Server (default).</summary>
    SqlServer,

    /// <summary>PostgreSQL (Npgsql).</summary>
    Postgres,
}

/// <summary>
/// Registers the database provider for EF Core and Hangfire.
/// </summary>
public static class DatabaseSetup
{
    /// <summary>Configuration key for the provider switch.</summary>
    public const string ProviderKey = "Hawk:Database:Provider";

    /// <summary>
    /// Reads <c>Hawk:Database:Provider</c>. Missing/empty means <see cref="DatabaseProvider.SqlServer"/>;
    /// <c>SqlServer</c> and <c>Postgres</c> are accepted case-insensitively.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown for any other value.</exception>
    public static DatabaseProvider GetProvider(IConfiguration configuration)
    {
        var raw = configuration[ProviderKey]?.Trim();
        if (string.IsNullOrEmpty(raw) || string.Equals(raw, "SqlServer", StringComparison.OrdinalIgnoreCase))
            return DatabaseProvider.SqlServer;
        if (string.Equals(raw, "Postgres", StringComparison.OrdinalIgnoreCase))
            return DatabaseProvider.Postgres;

        throw new InvalidOperationException(
            $"Unsupported database provider '{raw}' in {ProviderKey} (env Hawk__Database__Provider). Use 'SqlServer' (default) or 'Postgres'.");
    }

    /// <summary>
    /// Registers <see cref="ApplicationDbContext"/> for the configured provider.
    /// For Postgres the registered implementation is <see cref="PostgresApplicationDbContext"/>, so the
    /// Postgres migration set is used while application code keeps depending on <see cref="ApplicationDbContext"/>.
    /// </summary>
    public static IServiceCollection AddHawkDbContext(this IServiceCollection services, DatabaseProvider provider, string connectionString)
    {
        switch (provider)
        {
            case DatabaseProvider.SqlServer:
                services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
                break;
            case DatabaseProvider.Postgres:
                services.AddDbContext<ApplicationDbContext, PostgresApplicationDbContext>(options => options.UseNpgsql(connectionString));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported database provider.");
        }

        return services;
    }

    /// <summary>
    /// Configures Hangfire job storage for the configured provider (co-located with the app database).
    /// </summary>
    public static IGlobalConfiguration UseHawkStorage(this IGlobalConfiguration configuration, DatabaseProvider provider, string connectionString)
    {
        return provider switch
        {
            DatabaseProvider.SqlServer => configuration.UseSqlServerStorage(connectionString, new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.FromSeconds(5),
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            }),
            DatabaseProvider.Postgres => configuration.UseStorage(CreatePostgreSqlStorage(connectionString)),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported database provider.")
        };
    }

    /// <summary>
    /// Creates Hangfire PostgreSQL storage (schema <c>hangfire</c>, created/upgraded on startup).
    /// Mirrors the SQL Server options where an equivalent exists; CommandBatchMaxTimeout,
    /// UseRecommendedIsolationLevel and DisableGlobalLocks have no Hangfire.PostgreSql counterpart.
    /// </summary>
    public static PostgreSqlStorage CreatePostgreSqlStorage(string connectionString)
    {
        var options = new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            InvisibilityTimeout = TimeSpan.FromMinutes(5),
            UseSlidingInvisibilityTimeout = true,
            QueuePollInterval = TimeSpan.FromSeconds(5),
        };
        return new PostgreSqlStorage(new NpgsqlConnectionFactory(connectionString, options), options);
    }
}
