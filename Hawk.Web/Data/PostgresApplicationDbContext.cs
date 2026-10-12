// <file>
// <summary>
// PostgreSQL flavour of <see cref="ApplicationDbContext"/>.
// Exists only so PostgreSQL gets its own migration set (Data/Migrations/Postgres) and model snapshot:
// EF Core matches migrations to a context by exact type, so the SQL Server migrations (bound to
// ApplicationDbContext) and the PostgreSQL migrations (bound to this type) never mix.
// The model itself is inherited unchanged. Application code keeps depending on ApplicationDbContext.
// </summary>
// </file>

using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Data;

/// <summary>
/// Application DbContext used when <c>Hawk:Database:Provider</c> is <c>Postgres</c>.
/// </summary>
/// <param name="options">DbContext options configured with Npgsql.</param>
public sealed class PostgresApplicationDbContext(DbContextOptions<PostgresApplicationDbContext> options)
    : ApplicationDbContext(options);
