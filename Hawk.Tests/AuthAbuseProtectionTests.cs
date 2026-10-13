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

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string ip)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return await client.SendAsync(request);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
