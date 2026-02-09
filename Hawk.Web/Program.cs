// <file>
// <summary>
// Application entry point and dependency injection configuration.
// Notes:
// - Identity is configured for username/password logins.
// - Startup seeding applies EF migrations and ensures a seed admin user exists.
// - HTTPS redirection can be disabled (for E2E) via Hawk__DisableHttpsRedirection=true.
// </summary>
// </file>

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Seeding;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<IdentityUser>(options =>
    {
        // For an internal uptime tool, email confirmation is usually friction with no upside.
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddRazorPages();

var app = builder.Build();

// Important branch: boot-time seeding.
// - Applies EF migrations (required for container deployments).
// - Ensures an Admin role and a seed admin user exists.
await app.Services.SeedIdentityAsync();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!app.Configuration.GetValue("Hawk:DisableHttpsRedirection", false))
{
    // Branch: production/default behavior.
    app.UseHttpsRedirection();
}
// Else branch: E2E/Testing can force plain HTTP to keep Playwright stable.

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
