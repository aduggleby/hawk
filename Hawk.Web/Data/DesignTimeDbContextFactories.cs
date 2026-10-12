// <file>
// <summary>
// Design-time factories used by `dotnet ef` so each provider's context can be created without starting the app
// and independent of Hawk:Database:Provider. Commands (run from the repository root):
//   dotnet ef migrations add <Name> --project Hawk.Web --context ApplicationDbContext --output-dir Data/Migrations
//   dotnet ef migrations add <Name> --project Hawk.Web --context PostgresApplicationDbContext --output-dir Data/Migrations/Postgres
// `migrations add` never connects. For commands that do (e.g. `database update`), set
// ConnectionStrings__DefaultConnection to a connection string for the matching provider.
// Both factories attach the app's Identity registration so the scaffolded model matches the runtime model.
// </summary>
// </file>

using Hawk.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hawk.Web.Data;

/// <summary>
/// Design-time factory for the SQL Server context (<see cref="ApplicationDbContext"/>).
/// </summary>
public sealed class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    /// <inheritdoc />
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=localhost;Database=Hawk;Trusted_Connection=True;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .UseApplicationServiceProvider(DesignTimeServices.Create())
            .Options;
        return new ApplicationDbContext(options);
    }
}

/// <summary>
/// Design-time factory for the PostgreSQL context (<see cref="PostgresApplicationDbContext"/>).
/// </summary>
public sealed class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresApplicationDbContext>
{
    /// <inheritdoc />
    public PostgresApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=hawk;Username=postgres";
        var options = new DbContextOptionsBuilder<PostgresApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseApplicationServiceProvider(DesignTimeServices.Create())
            .Options;
        return new PostgresApplicationDbContext(options);
    }
}

/// <summary>
/// Minimal application services needed to build the EF model at design time.
/// </summary>
internal static class DesignTimeServices
{
    /// <summary>
    /// Creates a service provider with the app's Identity options (they influence the Identity part of the model).
    /// </summary>
    public static IServiceProvider Create()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHawkIdentity();
        return services.BuildServiceProvider();
    }
}
