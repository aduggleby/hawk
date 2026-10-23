// <file>
// <summary>
// Application entry point and dependency injection configuration.
// Notes:
// - Identity is configured for username/password logins.
// - Startup seeding applies EF migrations and ensures a seed admin user exists.
// - HTTPS redirection can be disabled (for E2E) via Hawk__DisableHttpsRedirection=true.
// - Database provider (EF Core, Identity, Hangfire) is selected by Hawk:Database:Provider (SqlServer default, or Postgres).
// - GET|HEAD|POST /ping/{token} is the anonymous heartbeat endpoint (secret token: redacted from request logs).
// - GET /live (process up) and GET /ready (database reachable) are anonymous health endpoints.
// - The Serilog file sink can be disabled with Hawk:Logging:File:Enabled=false (console only, read-only root FS).
// </summary>
// </file>

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Seeding;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Hangfire;
using Serilog;
using Serilog.Events;
using Hawk.Web.Services.Email;
using Hawk.Web.Services.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Import;
using Hawk.Web.Infrastructure;
using Hawk.Web.Services.UrlChecks;

StartupBanner.Write();

var builder = WebApplication.CreateBuilder(args);

var fileLoggingEnabled = builder.Configuration.GetValue("Hawk:Logging:File:Enabled", true);
var fileLoggingPath = builder.Configuration.GetValue("Hawk:Logging:File:Path", "logs")!;
var fileLoggingRetainedDays = Math.Clamp(builder.Configuration.GetValue("Hawk:Logging:File:RetainedDays", 30), 1, 3650);
builder.Host.UseSerilog((ctx, services, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console();

    // Branch: rolling file logs (default). Disabled on read-only root filesystems where only stdout is collected.
    // One file per day, so retainedFileCountLimit approximates "delete logs older than N days".
    if (fileLoggingEnabled)
    {
        cfg.WriteTo.File(
            path: Path.Combine(fileLoggingPath, "hawk-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: fileLoggingRetainedDays,
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(2));
    }
});

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
// Throws for unsupported values so a typo can never silently fall back to the wrong database.
var databaseProvider = DatabaseSetup.GetProvider(builder.Configuration);
builder.Services.AddHawkDbContext(databaseProvider, connectionString);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [DatabaseHealthCheck.ReadyTag], timeout: TimeSpan.FromSeconds(5));

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

builder.Services.AddHawkEmail(builder.Configuration);

builder.Services.AddHangfire(cfg =>
{
    // Hangfire storage is co-located with the app DB for simple Docker Compose deployment.
    cfg.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseHawkStorage(databaseProvider, connectionString);
});
builder.Services.AddHangfireServer();

// Persist DataProtection keys in containers so Identity cookies remain valid across restarts.
// Branch: only enabled automatically when running in a container unless explicitly configured.
var dpKeysPath = builder.Configuration["Hawk:DataProtection:KeysPath"];
var runningInContainer = string.Equals(
    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
    "true",
    StringComparison.OrdinalIgnoreCase);
string? persistedKeysPath = null;
if (!string.IsNullOrWhiteSpace(dpKeysPath) || runningInContainer)
{
    dpKeysPath = string.IsNullOrWhiteSpace(dpKeysPath) ? "/var/lib/hawk/dpkeys" : dpKeysPath;
    persistedKeysPath = dpKeysPath;
    Directory.CreateDirectory(dpKeysPath);
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
        .SetApplicationName("Hawk");
}

builder.Services.AddHawkIdentity(builder.Configuration);
builder.Services.AddHawkApplicationCookie(builder.Configuration);
builder.Services.AddHawkAuthAbuseProtection(builder.Configuration);

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
    // Query strings keep their case: Identity tokens (email confirmation, password
    // reset) are case-sensitive Base64Url and break when lowercased.
    options.LowercaseQueryStrings = false;
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
builder.Services.AddScoped<MonitorTransferService>();

var app = builder.Build();

app.Logger.LogInformation("Database provider: {DatabaseProvider}. File logging: {FileLogging} ({FileLoggingPath}, {FileLoggingRetainedDays} days).", databaseProvider, fileLoggingEnabled, Path.GetFullPath(fileLoggingPath), fileLoggingRetainedDays);
if (persistedKeysPath is not null)
{
    // Typical after upgrading to the non-root image with a volume/host path created by an older (root) image.
    var uid = Environment.GetEnvironmentVariable("APP_UID") ?? "current user";
    if (!AreKeyFilesReadable(persistedKeysPath))
    {
        app.Logger.LogError(
            "DataProtection keys in {KeysPath} are not readable by this process ({Uid}). Logins and forms will fail. Fix ownership (chown -R 1654:1654 on the volume/host path) or run the container as root.",
            persistedKeysPath,
            uid);
    }
    else if (!IsDirectoryWritable(persistedKeysPath))
    {
        app.Logger.LogWarning(
            "DataProtection keys path {KeysPath} is not writable by this process ({Uid}). Existing keys still work, but logins break when keys rotate. Fix ownership (chown -R 1654:1654 on the volume/host path).",
            persistedKeysPath,
            uid);
    }
}

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
app.UseHawkAuthAbuseProtection();

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

app.UseSerilogRequestLogging(options =>
{
    // Health probes run every few seconds; keep them out of the default log output.
    options.GetLevel = (httpContext, _, ex) =>
        ex is null && httpContext.Response.StatusCode < 500 && IsHealthPath(httpContext.Request.Path)
            ? LogEventLevel.Verbose
            : ex is not null || httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : LogEventLevel.Information;

    // The ping token is a bearer secret: never write it to the log.
    options.GetMessageTemplateProperties = (httpContext, path, elapsedMs, statusCode) =>
    [
        new LogEventProperty("RequestMethod", new ScalarValue(httpContext.Request.Method)),
        new LogEventProperty("RequestPath", new ScalarValue(HeartbeatEndpoints.RedactPath(path))),
        new LogEventProperty("StatusCode", new ScalarValue(statusCode)),
        new LogEventProperty("Elapsed", new ScalarValue(elapsedMs)),
    ];
});

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

// Health endpoints (anonymous, lightweight). /ready returns 503 when the database is unreachable.
app.MapHealthChecks("/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(DatabaseHealthCheck.ReadyTag) }).AllowAnonymous();

app.MapHawkHeartbeats();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

static bool IsHealthPath(PathString path) =>
    path.Equals("/live", StringComparison.OrdinalIgnoreCase) || path.Equals("/ready", StringComparison.OrdinalIgnoreCase);

static bool AreKeyFilesReadable(string path)
{
    try
    {
        foreach (var file in Directory.EnumerateFiles(path, "*.xml"))
        {
            using var _ = File.OpenRead(file);
        }
        return true;
    }
    catch (Exception)
    {
        return false;
    }
}

static bool IsDirectoryWritable(string path)
{
    try
    {
        var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
        return true;
    }
    catch (Exception)
    {
        return false;
    }
}
