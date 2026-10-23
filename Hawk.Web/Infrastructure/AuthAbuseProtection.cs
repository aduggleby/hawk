using System.Globalization;
using System.Threading.RateLimiting;
using Hawk.Web.Services.Monitoring;
using Microsoft.AspNetCore.RateLimiting;

namespace Hawk.Web.Infrastructure;

public sealed class HawkSecurityOptions
{
    public int LoginRequestsPerMinute { get; init; } = 10;
    public int EmailRequestsPer15Minutes { get; init; } = 5;
    public int FailedLoginAttempts { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 5;
    public int EmailCooldownMinutes { get; init; } = 1;
    public int PingRequestsPerMinute { get; init; } = 30;
    public int PingRequestsPerMinutePerIp { get; init; } = 300;

    public static HawkSecurityOptions Read(IConfiguration? configuration)
    {
        var section = configuration?.GetSection("Hawk:Security");
        return new HawkSecurityOptions
        {
            LoginRequestsPerMinute = ReadBounded(section, nameof(LoginRequestsPerMinute), 10, 1, 100),
            EmailRequestsPer15Minutes = ReadBounded(section, nameof(EmailRequestsPer15Minutes), 5, 1, 100),
            FailedLoginAttempts = ReadBounded(section, nameof(FailedLoginAttempts), 5, 2, 20),
            LockoutMinutes = ReadBounded(section, nameof(LockoutMinutes), 5, 1, 60),
            EmailCooldownMinutes = ReadBounded(section, nameof(EmailCooldownMinutes), 1, 1, 60),
            PingRequestsPerMinute = ReadBounded(section, nameof(PingRequestsPerMinute), 30, 1, 600),
            PingRequestsPerMinutePerIp = ReadBounded(section, nameof(PingRequestsPerMinutePerIp), 300, 1, 6000)
        };
    }

    private static int ReadBounded(IConfigurationSection? section, string key, int defaultValue, int min, int max)
    {
        var raw = section?[key];
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new InvalidOperationException($"Hawk:Security:{key} must be an integer from {min} to {max}.");
        return value;
    }
}

public static class AuthAbuseProtection
{
    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan EmailWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PingWindow = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddHawkAuthAbuseProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = HawkSecurityOptions.Read(configuration);
        services.AddSingleton(settings);
        services.AddSingleton(sp => new AccountEmailCooldown(
            TimeSpan.FromMinutes(settings.EmailCooldownMinutes),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        services.AddRateLimiter(options =>
        {
            var authLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path.Value?.TrimEnd('/');
                if (!HttpMethods.IsPost(context.Request.Method))
                    return RateLimitPartition.GetNoLimiter("other");

                var category = path switch
                {
                    var p when string.Equals(p, "/Identity/Account/Login", StringComparison.OrdinalIgnoreCase) => "login",
                    var p when string.Equals(p, "/Identity/Account/ForgotPassword", StringComparison.OrdinalIgnoreCase) => "email",
                    var p when string.Equals(p, "/Identity/Account/ResendEmailConfirmation", StringComparison.OrdinalIgnoreCase) => "email",
                    _ => null
                };
                if (category is null) return RateLimitPartition.GetNoLimiter("other");

                // Use RemoteIpAddress after Forwarded Headers has applied the proxy address.
                var key = $"{category}:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
                var isLogin = category == "login";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = isLogin ? settings.LoginRequestsPerMinute : settings.EmailRequestsPer15Minutes,
                    Window = isLogin ? LoginWindow : EmailWindow,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            // Heartbeat pings (any method): one budget per token and one per client IP (chained IP first, so a rejected IP allocates no token partitions). The per-IP budget also bounds
            // floods of random tokens; malformed tokens are rejected before any lookup and only count against the IP.
            var pingTokenLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (!path.StartsWith(HeartbeatEndpoints.PathPrefix, StringComparison.OrdinalIgnoreCase))
                    return RateLimitPartition.GetNoLimiter("other");

                // Valid-format tokens only: bounds the number of live partitions to what can reach the database.
                if (!HeartbeatTokens.TryNormalize(path[HeartbeatEndpoints.PathPrefix.Length..].TrimEnd('/'), out var token))
                    return RateLimitPartition.GetNoLimiter("other");

                return RateLimitPartition.GetFixedWindowLimiter($"ping-token:{token}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PingRequestsPerMinute,
                    Window = PingWindow,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            var pingIpLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!(context.Request.Path.Value ?? string.Empty).StartsWith(HeartbeatEndpoints.PathPrefix, StringComparison.OrdinalIgnoreCase))
                    return RateLimitPartition.GetNoLimiter("other");

                return RateLimitPartition.GetFixedWindowLimiter(
                    $"ping-ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PingRequestsPerMinutePerIp,
                        Window = PingWindow,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(authLimiter, pingIpLimiter, pingTokenLimiter);

            options.OnRejected = (context, _) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                var requestPath = context.HttpContext.Request.Path.Value;
                var fallback = (requestPath?.StartsWith(HeartbeatEndpoints.PathPrefix, StringComparison.OrdinalIgnoreCase) ?? false) ? PingWindow
                    : string.Equals(requestPath?.TrimEnd('/'), "/Identity/Account/Login", StringComparison.OrdinalIgnoreCase) ? LoginWindow : EmailWindow;
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan remaining)
                    ? remaining : fallback;
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    .ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
        });
        return services;
    }

    public static IApplicationBuilder UseHawkAuthAbuseProtection(this IApplicationBuilder app) => app.UseRateLimiter();
}

/// <summary>Single-instance, bounded cooldown shared by anonymous account-email actions.</summary>
public sealed class AccountEmailCooldown(TimeSpan interval, TimeProvider clock)
{
    private const int MaxAccounts = 10_000;
    private readonly Dictionary<string, DateTimeOffset> _nextAllowed = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private DateTimeOffset _nextSweepAt;

    public bool TryBegin(string userId)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_nextAllowed.TryGetValue(userId, out var next) && next > now)
                return false;

            // Sweep at most once per interval unless the bounded store fills up.
            if (now >= _nextSweepAt || _nextAllowed.Count >= MaxAccounts)
            {
                foreach (var expired in _nextAllowed.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
                    _nextAllowed.Remove(expired);
                _nextSweepAt = now + interval;
            }

            if (_nextAllowed.Count >= MaxAccounts) return false;
            _nextAllowed[userId] = now + interval;
            return true;
        }
    }
}
