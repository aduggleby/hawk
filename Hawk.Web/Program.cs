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
using Hangfire;
using Hangfire.SqlServer;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, cfg) =>
{
    // Rolling file logs for container-friendly diagnostics.
    // retainedFileCountLimit=30 approximates "delete logs older than 30 days" when rolling daily.
    cfg.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            path: Path.Combine("logs", "hawk-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(2));
});

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddHangfire(cfg =>
{
    // Hangfire storage is co-located with the app DB for simple Docker Compose deployment.
    cfg.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.FromSeconds(5),
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
        });
});
builder.Services.AddHangfireServer();

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

// Hangfire dashboard should be restricted later to Admin users.
app.UseHangfireDashboard("/hangfire");

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
