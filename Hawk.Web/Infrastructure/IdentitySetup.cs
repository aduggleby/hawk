// <file>
// <summary>
// ASP.NET Core Identity registration shared by the app (Program.cs) and the EF design-time factories.
// Identity shapes part of the EF model from IdentityOptions (e.g. AddDefaultIdentity sets Stores.MaxLengthForKeys=128),
// so `dotnet ef` must see exactly the same registration or scaffolded migrations would drift from the runtime model.
// </summary>
// </file>

using Hawk.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace Hawk.Web.Infrastructure;

/// <summary>
/// Identity registration helpers.
/// </summary>
public static class IdentitySetup
{
    /// <summary>
    /// Registers Identity (default UI, roles, EF stores on <see cref="ApplicationDbContext"/>).
    /// </summary>
    public static IdentityBuilder AddHawkIdentity(this IServiceCollection services)
    {
        return services.AddDefaultIdentity<IdentityUser>(options =>
            {
                // For an internal uptime tool, email confirmation is usually friction with no upside.
                options.SignIn.RequireConfirmedAccount = false;
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 0;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
    }
}
