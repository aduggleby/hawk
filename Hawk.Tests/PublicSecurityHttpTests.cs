using System.Net;
using Hawk.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hawk.Tests;

public class PublicSecurityHttpTests
{
    [Fact]
    public async Task Monitor_navigation_is_passive_and_execution_requires_valid_antiforgery_token()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        var id = Guid.NewGuid();
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Monitors.Add(new Hawk.Web.Data.Monitoring.Monitor
            {
                Id = id, Name = "Paused POST", Url = "https://example.com", Method = "POST",
                Body = "private-payload", IsPaused = true, TimeoutSeconds = 15, IntervalSeconds = 60
            });
            await db.SaveChangesAsync();
        }

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var request = new HttpRequestMessage(method, $"/Monitors/Test/{id}");
            using var response = await app.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        Assert.Empty(app.Jobs.Created);
        Assert.Equal(0, app.Executor.Executions);
        Assert.Equal(0, app.Emails.Sent);
        Assert.Equal(0, await CountRunsAsync(app));

        var action = $"/Monitors/Details/{id}?handler=RunNow";
        using var forged = await app.Client.PostAsync(action, new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Empty(app.Jobs.Created);
        Assert.Equal(0, await CountRunsAsync(app));

        using var details = await app.Client.GetAsync($"/Monitors/Details/{id}");
        details.EnsureSuccessStatusCode();
        var token = SecurityTestApplication.AntiforgeryToken(await details.Content.ReadAsStringAsync());
        using var run = await app.Client.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);
        Assert.Single(app.Jobs.Created);
        Assert.Equal(1, await CountRunsAsync(app));

        var location = run.Headers.Location!;
        for (var refresh = 0; refresh < 2; refresh++)
        {
            using var diagnostics = await app.Client.GetAsync(location);
            diagnostics.EnsureSuccessStatusCode();
            var html = await diagnostics.Content.ReadAsStringAsync();
            Assert.Contains("Run again", html);
            Assert.Contains("method=\"post\"", html);
            SecurityTestApplication.AntiforgeryToken(html);
        }
        Assert.Single(app.Jobs.Created);
        Assert.Equal(1, await CountRunsAsync(app));
        Assert.Equal(0, app.Executor.Executions);
    }

    [Fact]
    public async Task Anonymous_production_exception_has_no_private_data_in_rendered_page_or_scripts()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        using var response = await app.Client.GetAsync("/test/error");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Something went wrong", html);
        Assert.Contains("Request ID", html);
        Assert.DoesNotContain(SecurityTestApplication.PrivateException, html);
        Assert.DoesNotContain("InvalidOperationException", html);
        Assert.DoesNotContain("Stack trace", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tech-details", html);
        Assert.DoesNotContain("copy-tech", html);
    }

    [Theory]
    [InlineData("/Identity/Account/ResetPassword?code=%21", "The password reset link is invalid.")]
    [InlineData("/Identity/Account/ConfirmEmail?userId=owner&code=%21", "Error confirming your email.")]
    public async Task Malformed_account_tokens_return_normal_pages_without_stack_traces(string path, string validationMessage)
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        using var response = await app.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("FormatException", html);
        Assert.DoesNotContain("Stack trace", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(validationMessage, html);
    }

    [Fact]
    public async Task Failed_password_attempts_lock_account_and_correct_password_works_after_expiry()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await SubmitAccountFormAsync(app, "Login", SecurityTestApplication.UserEmail, "wrong-password");
            Assert.Equal(attempt < 4 ? HttpStatusCode.OK : HttpStatusCode.Redirect, response.StatusCode);
        }
        using var blocked = await SubmitAccountFormAsync(app, "Login", SecurityTestApplication.UserEmail, SecurityTestApplication.Password);
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        Assert.Contains("Lockout", blocked.Headers.Location!.OriginalString);
        await app.WithUserAsync(async (users, user) =>
        {
            Assert.True(await users.IsLockedOutAsync(user));
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddSeconds(-1))).Succeeded);
        });

        using var recovered = await SubmitAccountFormAsync(app, "Login", SecurityTestApplication.UserEmail, SecurityTestApplication.Password);
        Assert.Equal(HttpStatusCode.Redirect, recovered.StatusCode);
        using var secure = await app.Client.GetAsync("/test/secure");
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
    }

    [Fact]
    public async Task Email_actions_share_account_cooldown_and_sender_failures_are_generic()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        using var first = await SubmitAccountFormAsync(app, "ForgotPassword", SecurityTestApplication.UserEmail);
        using var suppressed = await SubmitAccountFormAsync(app, "ResendEmailConfirmation", SecurityTestApplication.UserEmail);
        Assert.Equal(1, app.Emails.Sent);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, suppressed.StatusCode);

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        app.Emails.Fail = true;
        using var failed = await SubmitAccountFormAsync(app, "ForgotPassword", SecurityTestApplication.UserEmail);
        using var unknown = await SubmitAccountFormAsync(app, "ForgotPassword", "unknown@example.com");
        using var cooldown = await SubmitAccountFormAsync(app, "ForgotPassword", SecurityTestApplication.UserEmail);
        Assert.Equal(2, app.Emails.Sent);
        Assert.Equal(unknown.StatusCode, failed.StatusCode);
        Assert.Equal(unknown.Headers.Location, failed.Headers.Location);
        Assert.Equal(unknown.Headers.Location, cooldown.Headers.Location);

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        using var failedResend = await SubmitAccountFormAsync(app, "ResendEmailConfirmation", SecurityTestApplication.UserEmail);
        using var unknownResend = await SubmitAccountFormAsync(app, "ResendEmailConfirmation", "unknown@example.com");
        Assert.Equal(3, app.Emails.Sent);
        var failedHtml = await failedResend.Content.ReadAsStringAsync();
        var unknownHtml = await unknownResend.Content.ReadAsStringAsync();
        Assert.Equal(unknownResend.StatusCode, failedResend.StatusCode);
        Assert.Contains("Verification email sent", failedHtml);
        Assert.Contains("Verification email sent", unknownHtml);
        Assert.DoesNotContain(SecurityTestApplication.PrivateException, failedHtml);
        Assert.DoesNotContain("InvalidOperationException", failedHtml);
    }

    private static async Task<HttpResponseMessage> SubmitAccountFormAsync(SecurityTestApplication app, string page, string email, string? password = null)
    {
        var path = $"/Identity/Account/{page}";
        using var form = await app.Client.GetAsync(path);
        form.EnsureSuccessStatusCode();
        var values = new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["__RequestVerificationToken"] = SecurityTestApplication.AntiforgeryToken(await form.Content.ReadAsStringAsync())
        };
        if (password is not null) values["Input.Password"] = password;
        return await app.Client.PostAsync(path, new FormUrlEncodedContent(values));
    }

    private static async Task<int> CountRunsAsync(SecurityTestApplication app)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MonitorRuns.CountAsync();
    }
}
