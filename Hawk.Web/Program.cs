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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Seeding;
using Hangfire;
using Hangfire.SqlServer;
using Serilog;
using Hawk.Web.Services.Email;
using Hawk.Web.Services.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Import;
using Hawk.Web.Infrastructure;
using Hawk.Web.Services.UrlChecks;

StartupBanner.Write();

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

builder.Services.AddHttpClient("urlchecks", c =>
{
    // We implement timeouts via CancellationTokenSource to capture them as "timeout" results.
    c.Timeout = Timeout.InfiniteTimeSpan;

    // Default crawler UA. Can be either a preset key (e.g., "firefox") or a full UA string.
    var ua = UserAgentResolver.Resolve(builder.Configuration, builder.Configuration["Hawk:UrlChecks:UserAgent"]);

    // Use TryAddWithoutValidation so odd-but-real UA strings don't throw.
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
});
builder.Services.AddScoped<IUrlChecker>(sp =>
    new UrlChecker(sp.GetRequiredService<IHttpClientFactory>().CreateClient("urlchecks")));

builder.Services.Configure<ResendCompatibleEmailOptions>(builder.Configuration.GetSection("Hawk:Resend"));
builder.Services.AddHttpClient<ResendCompatibleEmailSender>();
builder.Services.AddScoped<IEmailSender, ResendCompatibleEmailSender>();
builder.Services.AddScoped<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender, IdentityUiEmailSender>();

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

// Persist DataProtection keys in containers so Identity cookies remain valid across restarts.
// Branch: only enabled automatically when running in a container unless explicitly configured.
var dpKeysPath = builder.Configuration["Hawk:DataProtection:KeysPath"];
var runningInContainer = string.Equals(
    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
    "true",
    StringComparison.OrdinalIgnoreCase);
if (!string.IsNullOrWhiteSpace(dpKeysPath) || runningInContainer)
{
    dpKeysPath = string.IsNullOrWhiteSpace(dpKeysPath) ? "/var/lib/hawk/dpkeys" : dpKeysPath;
    Directory.CreateDirectory(dpKeysPath);
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
        .SetApplicationName("Hawk");
}

builder.Services.AddDefaultIdentity<IdentityUser>(options =>
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

builder.Services.ConfigureApplicationCookie(options =>
{
    var httpsDisabled = builder.Configuration.GetValue("Hawk:DisableHttpsRedirection", false);

    options.Cookie.Name = "Hawk.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = httpsDisabled
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
    options.Events = new CookieAuthenticationEvents
    {
        OnRedirectToLogin = context =>
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Hawk.Auth");
            logger.LogWarning(
                "Auth challenge for {Path}. Cookie header present: {HasCookieHeader}. Auth cookie present: {HasAuthCookie}. User authenticated: {IsAuthenticated}.",
                context.Request.Path,
                context.Request.Headers.Cookie.Count > 0,
                context.Request.Cookies.ContainsKey("Hawk.Auth"),
                context.HttpContext.User.Identity?.IsAuthenticated == true);
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        },
        OnValidatePrincipal = context =>
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Hawk.Auth");
            logger.LogDebug(
                "Validated Hawk auth cookie for {Name}.",
                context.Principal?.Identity?.Name ?? "(anonymous)");
            return Task.CompletedTask;
        }
    };
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;

    // Container and appliance deployments often sit behind an internal reverse proxy
    // whose address is not stable from the app's point of view.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
});

builder.Services.AddRouting(options =>
{
    options.LowercaseUrls = true;
    options.LowercaseQueryStrings = true;
});

builder.Services.AddRazorPages(options =>
{
    // Default: require auth for app pages. Identity UI remains accessible for login.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
});

builder.Services.AddScoped<IMonitorRunner, MonitorRunner>();
builder.Services.AddScoped<IMonitorExecutor, MonitorExecutor>();
builder.Services.AddScoped<IMonitorScheduler, MonitorScheduler>();
builder.Services.AddScoped<StatusCakeImporter>();

var app = builder.Build();

// Important branch: boot-time seeding.
// - Applies EF migrations (required for container deployments).
// - Ensures an Admin role exists.
// - In Development/Testing, also creates a seed admin user for convenience/E2E.
await app.Services.SeedIdentityAsync();
await app.Services.SeedDevelopmentMonitorsAsync();
await using (var scope = app.Services.CreateAsyncScope())
{
    // Start the scheduler loop.
    await scope.ServiceProvider.GetRequiredService<IMonitorScheduler>().EnsureStartedAsync(CancellationToken.None);
}

// Configure the HTTP request pipeline.
app.UseForwardedHeaders();

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

app.UseSerilogRequestLogging();

// Preserve exception details in HttpContext.Items so /Error can render diagnostics
// even if the exception feature is not populated by upstream middleware.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        context.Items["Hawk.Exception"] = ex;
        throw;
    }
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Hangfire dashboard should be restricted later to Admin users.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new HangfireDashboardAuthFilter()]
});

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
