// <file>
// <summary>
// Identity/roles bootstrapper. Applies EF migrations on startup and ensures there is an Admin role.
// In Development/Testing, also ensures a seed admin user exists for convenience and E2E automation.
// This keeps container deployments simple (no separate migration job required).
// </summary>
// </file>

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Hawk.Web.Data.Seeding;

/// <summary>
/// Startup seeding helpers for ASP.NET Core Identity.
/// </summary>
public static class IdentitySeeder
{
    /// <summary>
    /// Applies outstanding EF migrations, creates the <c>Admin</c> role, and (optionally) ensures a seed admin user exists.
    /// </summary>
    /// <param name="services">Application service provider.</param>
    /// <exception cref="InvalidOperationException">Thrown when role or user creation fails.</exception>
    public static async Task SeedIdentityAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<ApplicationDbContext>();
        // Important: this is what allows "apply migrations on startup" in Docker deployments.
        // Branch: when the SQL Server container is still starting up, migrations can fail. Retry for a short window.
        await MigrateWithRetryAsync(db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbMigrate"), CancellationToken.None);

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var config = sp.GetRequiredService<IConfiguration>();
        var env = sp.GetRequiredService<IHostEnvironment>();

        const string adminRole = "Admin";
        if (!await roleManager.RoleExistsAsync(adminRole))
        {
            var roleRes = await roleManager.CreateAsync(new IdentityRole(adminRole));
            if (!roleRes.Succeeded)
                throw new InvalidOperationException($"Failed to create role '{adminRole}': {string.Join(", ", roleRes.Errors.Select(e => e.Description))}");
        }

        // In production-like environments, do NOT seed an admin user.
        // The first registered user will be promoted to Admin during registration.
        if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            return;

        // Configurable via environment variables for Docker/CI.
        var seedEmail = config["Hawk:SeedAdmin:Email"] ?? "ad@dualconsult.com";
        var seedPassword = config["Hawk:SeedAdmin:Password"] ?? "Hawk!2026-Admin#1";

        var seedUser = await userManager.FindByEmailAsync(seedEmail);
        if (seedUser is null)
        {
            // Branch: first boot, no seed user exists yet.
            seedUser = new IdentityUser
            {
                UserName = seedEmail,
                Email = seedEmail,
                EmailConfirmed = true
            };

            var createRes = await userManager.CreateAsync(seedUser, seedPassword);
            if (!createRes.Succeeded)
                throw new InvalidOperationException($"Failed to create seed admin user '{seedEmail}': {string.Join(", ", createRes.Errors.Select(e => e.Description))}");
        }

        if (!await userManager.IsInRoleAsync(seedUser, adminRole))
        {
            // Branch: ensure the seed user is always an admin even if roles were reset.
            var addRoleRes = await userManager.AddToRoleAsync(seedUser, adminRole);
            if (!addRoleRes.Succeeded)
                throw new InvalidOperationException($"Failed to add seed admin user '{seedEmail}' to '{adminRole}': {string.Join(", ", addRoleRes.Errors.Select(e => e.Description))}");
        }
    }

    private static async Task MigrateWithRetryAsync(ApplicationDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 20)
            {
                logger.LogWarning(ex, "Database migration failed (attempt {Attempt}/20). Retrying in {Delay}s.", attempt, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
            }
        }

        // Final attempt: throw for visibility.
        await db.Database.MigrateAsync(cancellationToken);
    }
}
