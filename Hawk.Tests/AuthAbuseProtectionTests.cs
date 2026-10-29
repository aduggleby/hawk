using System.Net;
using Hawk.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hawk.Tests;

public class AuthAbuseProtectionTests
{
    [Fact]
    public async Task Login_posts_are_limited_per_forwarded_client_and_other_methods_are_unaffected()
    {
        await using var app = await CreateAppAsync(new Dictionary<string, string?>
        {
            ["Hawk:Security:LoginRequestsPerMinute"] = "2"
        });
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/Identity/Account/Login", "198.51.100.10")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, "/Identity/Account/Login", "198.51.100.10")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, "/Identity/Account/Login", "198.51.100.10")).StatusCode);

        using var rejected = await Send(client, HttpMethod.Post, "/Identity/Account/Login/", "198.51.100.10");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(int.TryParse(rejected.Headers.GetValues("Retry-After").Single(), out var seconds));
        Assert.True(seconds > 0);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, "/Identity/Account/Login", "198.51.100.11")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/Identity/Account/Login", "198.51.100.10")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, "/ready", "198.51.100.10")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_email_actions_share_one_client_limit()
    {
        await using var app = await CreateAppAsync(new Dictionary<string, string?>
        {
            ["Hawk:Security:EmailRequestsPer15Minutes"] = "2"
        });
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, "/Identity/Account/ForgotPassword", "203.0.113.4")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, "/Identity/Account/ResendEmailConfirmation", "203.0.113.4")).StatusCode);
        using var rejected = await Send(client, HttpMethod.Post, "/Identity/Account/ForgotPassword", "203.0.113.4");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, "/Identity/Account/ResendEmailConfirmation", "203.0.113.5")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/Identity/Account/ForgotPassword", "203.0.113.4")).StatusCode);
    }

    [Fact]
    public async Task Pings_are_limited_per_normalized_token_for_any_method()
    {
        await using var app = await CreateAppAsync(new Dictionary<string, string?>
        {
            ["Hawk:Security:PingRequestsPerMinute"] = "2",
            ["Hawk:Security:PingRequestsPerMinutePerIp"] = "100"
        });
        using var client = app.GetTestClient();
        const string token = "0123456789abcdef0123456789abcdef";

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, $"/ping/{token}", "198.51.100.1")).StatusCode);
        // Case variants and other methods share the budget (the database lookup is case-insensitive on SQL Server).
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, $"/ping/{token.ToUpperInvariant()}", "198.51.100.2")).StatusCode);
        using var rejected = await Send(client, HttpMethod.Head, $"/ping/{token}", "198.51.100.3");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(int.TryParse(rejected.Headers.GetValues("Retry-After").Single(), out var seconds) && seconds is > 0 and <= 60);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/ping/fedcba9876543210fedcba9876543210", "198.51.100.1")).StatusCode);
    }

    [Fact]
    public async Task Floods_of_distinct_unknown_or_malformed_tokens_are_limited_per_client_ip()
    {
        await using var app = await CreateAppAsync(new Dictionary<string, string?>
        {
            ["Hawk:Security:PingRequestsPerMinute"] = "30",
            ["Hawk:Security:PingRequestsPerMinutePerIp"] = "3"
        });
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/ping/00000000000000000000000000000001", "203.0.113.9")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/ping/00000000000000000000000000000002", "203.0.113.9")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/ping/not-a-token", "203.0.113.9")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(client, HttpMethod.Get, "/ping/00000000000000000000000000000003", "203.0.113.9")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/ping/00000000000000000000000000000003", "203.0.113.10")).StatusCode);
        // Other paths are never touched by the ping limiters.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/ready", "203.0.113.9")).StatusCode);
    }

    [Fact]
    public async Task Api_requests_are_limited_per_key_and_other_keys_on_the_same_ip_pass()
    {
        await using var app = await CreateAppAsync(new Dictionary<string, string?>
        {
            ["Hawk:Security:ApiRequestsPerMinute"] = "2",
            ["Hawk:Security:ApiRequestsPerMinutePerIp"] = "100"
        });
        using var client = app.GetTestClient();
        var keyA = "Bearer hawk_" + new string('a', 43);
        var keyB = "Bearer hawk_" + new string('b', 43);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/api/v1/monitors", "198.51.100.1", keyA)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Post, "/api/v1/monitors/x/pause", "198.51.100.1", keyA)).StatusCode);
        using var rejected = await Send(client, HttpMethod.Get, "/api/v1", "198.51.100.1", keyA);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"rate_limited\"", await rejected.Content.ReadAsStringAsync());
        Assert.True(int.TryParse(rejected.Headers.GetValues("Retry-After").Single(), out var seconds) && seconds is > 0 and <= 60);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/api/v1", "198.51.100.1", keyB)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/ready", "198.51.100.1", keyA)).StatusCode);
    }

    [Fact]
    public async Task Api_floods_of_random_or_malformed_keys_are_limited_per_client_ip()
    {
        await using var app = await CreateAppAsync(new Dictionary<string, string?>
        {
            ["Hawk:Security:ApiRequestsPerMinute"] = "100",
            ["Hawk:Security:ApiRequestsPerMinutePerIp"] = "3"
        });
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/api/v1", "203.0.113.9", "Bearer hawk_" + new string('a', 43))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/api/v1", "203.0.113.9", "Bearer hawk_" + new string('b', 43))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/api/unknown", "203.0.113.9", "Bearer garbage")).StatusCode);
        using var rejected = await Send(client, HttpMethod.Get, "/api/v1", "203.0.113.9", "Bearer hawk_" + new string('c', 43));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/api/v1", "203.0.113.10", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Get, "/ready", "203.0.113.9")).StatusCode);
    }

    [Fact]
    public void Account_cooldown_is_shared_bounded_and_expires()
    {
        var clock = new ManualTimeProvider();
        var cooldown = new AccountEmailCooldown(TimeSpan.FromMinutes(1), clock);

        Assert.True(cooldown.TryBegin("account-a"));
        Assert.False(cooldown.TryBegin("account-a"));
        Assert.True(cooldown.TryBegin("account-b"));

        for (var i = 0; i < 9_998; i++)
            Assert.True(cooldown.TryBegin($"account-{i}"));
        Assert.False(cooldown.TryBegin("one-too-many"));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(cooldown.TryBegin("account-a"));
        Assert.True(cooldown.TryBegin("one-too-many"));
    }

    [Theory]
    [InlineData("LoginRequestsPerMinute", "0")]
    [InlineData("EmailRequestsPer15Minutes", "101")]
    [InlineData("FailedLoginAttempts", "1")]
    [InlineData("LockoutMinutes", "bad")]
    [InlineData("EmailCooldownMinutes", "61")]
    [InlineData("PingRequestsPerMinute", "0")]
    [InlineData("PingRequestsPerMinute", "601")]
    [InlineData("PingRequestsPerMinutePerIp", "6001")]
    [InlineData("PingRequestsPerMinutePerIp", "x")]
    [InlineData("ApiRequestsPerMinute", "0")]
    [InlineData("ApiRequestsPerMinute", "6001")]
    [InlineData("ApiRequestsPerMinutePerIp", "0")]
    [InlineData("ApiRequestsPerMinutePerIp", "60001")]
    [InlineData("ApiRequestsPerMinutePerIp", "x")]
    public void Security_settings_reject_out_of_bounds_values(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Hawk:Security:{key}"] = value
        }).Build();

        Assert.Throws<InvalidOperationException>(() => HawkSecurityOptions.Read(config));
    }

    private static async Task<WebApplication> CreateAppAsync(Dictionary<string, string?> overrides)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(overrides);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        builder.Services.AddHawkAuthAbuseProtection(builder.Configuration);

        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            return next();
        });
        app.UseForwardedHeaders();
        app.UseHawkAuthAbuseProtection();
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string ip, string? authorization = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await client.SendAsync(request);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
