using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Data.Seeding;

public static class IdentitySeeder
{
    public static async Task SeedIdentityAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var config = sp.GetRequiredService<IConfiguration>();

        const string adminRole = "Admin";
        if (!await roleManager.RoleExistsAsync(adminRole))
        {
            var roleRes = await roleManager.CreateAsync(new IdentityRole(adminRole));
            if (!roleRes.Succeeded)
                throw new InvalidOperationException($"Failed to create role '{adminRole}': {string.Join(", ", roleRes.Errors.Select(e => e.Description))}");
        }

        var seedEmail = config["Hawk:SeedAdmin:Email"] ?? "ad@dualconsult.com";
        var seedPassword = config["Hawk:SeedAdmin:Password"] ?? "Hawk!2026-Admin#1";

        var seedUser = await userManager.FindByEmailAsync(seedEmail);
        if (seedUser is null)
        {
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
            var addRoleRes = await userManager.AddToRoleAsync(seedUser, adminRole);
            if (!addRoleRes.Succeeded)
                throw new InvalidOperationException($"Failed to add seed admin user '{seedEmail}' to '{adminRole}': {string.Join(", ", addRoleRes.Errors.Select(e => e.Description))}");
        }
    }
}

