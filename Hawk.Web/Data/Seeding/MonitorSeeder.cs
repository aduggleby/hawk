// <file>
// <summary>
// Development/testing monitor seed data using Hawk.MockServer endpoints.
// Creates deterministic sample monitors so users can immediately test core functionality.
// </summary>
// </file>

using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Data.Seeding;

/// <summary>
/// Startup seeding helpers for sample monitors.
/// </summary>
public static class MonitorSeeder
{
    /// <summary>
    /// Seeds example monitors in Development/Testing only.
    /// </summary>
    public static async Task SeedDevelopmentMonitorsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var env = sp.GetRequiredService<IHostEnvironment>();
        if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            return;

        var db = sp.GetRequiredService<ApplicationDbContext>();
        var config = sp.GetRequiredService<IConfiguration>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("MonitorSeed");

        var seedEmail = config["Hawk:SeedAdmin:Email"] ?? "ad@dualconsult.com";
        var seedUser = await userManager.FindByEmailAsync(seedEmail);
        var ownerId = seedUser?.Id;

        var mockBaseUrl = ResolveMockBaseUrl(config, env);
        var interval = MonitorIntervals.AllowedSeconds(env).FirstOrDefault();
        if (interval <= 0)
            interval = env.IsEnvironment("Testing") ? MonitorIntervals.TestingFiveSeconds : 60;

        var candidates = new[]
        {
            new MonitorEntity
            {
                Name = "[Seed] Mock GET Contains",
                Url = JoinUrl(mockBaseUrl, "/ok"),
                Method = "GET",
                Enabled = true,
                IsPaused = true,
                TimeoutSeconds = 10,
                IntervalSeconds = interval,
                CreatedByUserId = ownerId,
                AlertAfterConsecutiveFailures = 1,
                MatchRules = [new MonitorMatchRule { Mode = ContentMatchMode.Contains, Pattern = "Example Domain" }],
            },
            new MonitorEntity
            {
                Name = "[Seed] Mock GET Regex",
                Url = JoinUrl(mockBaseUrl, "/ok"),
                Method = "GET",
                Enabled = true,
                IsPaused = true,
                TimeoutSeconds = 10,
                IntervalSeconds = interval,
                CreatedByUserId = ownerId,
                AlertAfterConsecutiveFailures = 1,
                MatchRules = [new MonitorMatchRule { Mode = ContentMatchMode.Regex, Pattern = @"OK:\s+Example\s+Domain" }],
            },
            new MonitorEntity
            {
                Name = "[Seed] Mock POST Echo",
                Url = JoinUrl(mockBaseUrl, "/echo"),
                Method = "POST",
                Enabled = true,
                IsPaused = true,
                TimeoutSeconds = 10,
                IntervalSeconds = interval,
                ContentType = "application/json",
                Body = """{"hello":"hawk"}""",
                CreatedByUserId = ownerId,
                AlertAfterConsecutiveFailures = 1,
                Headers =
                [
                    new MonitorHeader { Name = "X-Hawk-Seed", Value = "true" }
                ],
                MatchRules =
                [
                    new MonitorMatchRule { Mode = ContentMatchMode.Contains, Pattern = "\"hello\":\"hawk\"" },
                    new MonitorMatchRule { Mode = ContentMatchMode.Contains, Pattern = "\"contentType\":\"application/json\"" }
                ],
            },
            new MonitorEntity
            {
                Name = "[Seed] Mock HTTP 500",
                Url = JoinUrl(mockBaseUrl, "/error"),
                Method = "GET",
                Enabled = true,
                IsPaused = true,
                TimeoutSeconds = 10,
                IntervalSeconds = interval,
                CreatedByUserId = ownerId,
                AlertAfterConsecutiveFailures = 1,
            },
            new MonitorEntity
            {
                Name = "[Seed] Mock Timeout",
                Url = JoinUrl(mockBaseUrl, "/slow"),
                Method = "GET",
                Enabled = true,
                IsPaused = true,
                TimeoutSeconds = 2,
                IntervalSeconds = interval,
                CreatedByUserId = ownerId,
                AlertAfterConsecutiveFailures = 1,
            },
            new MonitorEntity
            {
                Name = "[Seed] Mock Flaky (Intermittent)",
                Url = JoinUrl(mockBaseUrl, "/flaky"),
                Method = "GET",
                Enabled = true,
                IsPaused = true,
                TimeoutSeconds = 10,
                IntervalSeconds = interval,
                CreatedByUserId = ownerId,
                AlertAfterConsecutiveFailures = 2,
            },
            new MonitorEntity
            {
                Name = "[Seed] External DNS Failure",
                // Intentionally not a mock endpoint to exercise upstream resolution/connection failures.
                Url = "http://nonexistent.hawk.invalid/health",
                Method = "GET",
                Enabled = true,
                IsPaused = true,
                TimeoutSeconds = 5,
                IntervalSeconds = interval,
                CreatedByUserId = ownerId,
                AlertAfterConsecutiveFailures = 1,
            }
        };

        var existingNames = await db.Monitors
            .Where(m => m.Name.StartsWith("[Seed] "))
            .Select(m => m.Name)
            .ToListAsync(cancellationToken);

        var toInsert = candidates
            .Where(c => !existingNames.Contains(c.Name, StringComparer.Ordinal))
            .ToList();

        if (toInsert.Count == 0)
            return;

        db.Monitors.AddRange(toInsert);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded {Count} development mock monitors using base URL {BaseUrl}.", toInsert.Count, mockBaseUrl);
    }

    private static string ResolveMockBaseUrl(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Hawk:SeedMocks:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        var resendBase = config["Hawk:Resend:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(resendBase))
        {
            var normalized = resendBase.Trim();
            if (normalized.Contains("mock", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.TrimEnd('/');
            }
        }

        return env.IsEnvironment("Testing")
            ? "http://mock:8081"
            : "http://localhost:17801";
    }

    private static string JoinUrl(string baseUrl, string path)
        => $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}
