using System.Net;
using Microsoft.AspNetCore.Identity;

namespace Hawk.Tests;

public class CookieSecurityTests
{
    [Fact]
    public async Task Unchanged_cookie_stays_authenticated_after_validation_and_renewal()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        using var secure = await app.Client.GetAsync("/test/secure");
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        app.Clock.Advance(TimeSpan.FromDays(8));
        using var renewed = await app.Client.GetAsync("/test/admin");
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.True(renewed.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("change")]
    [InlineData("delete")]
    public async Task Old_cookie_is_rejected_after_account_revocation(string operation)
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var before = await app.Client.GetAsync("/test/secure");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await app.WithUserAsync(async (users, user) =>
        {
            var result = operation switch
            {
                "delete" => await users.DeleteAsync(user),
                "change" => await users.ChangePasswordAsync(user, SecurityTestApplication.Password, "changed-password-123"),
                _ => await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), "reset-password-123")
            };
            Assert.True(result.Succeeded);
        });

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        using var secure = await app.Client.GetAsync("/test/secure");
        Assert.Equal(HttpStatusCode.Redirect, secure.StatusCode);
        Assert.Contains("/Identity/Account/Login", secure.Headers.Location!.OriginalString);
        app.Clock.Advance(TimeSpan.FromDays(8));
        using var stale = await app.Client.GetAsync("/test/admin");
        Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.Contains("/Identity/Account/Login", stale.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Existing_cookie_loses_removed_admin_role_when_principal_refreshes()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var before = await app.Client.GetAsync("/test/admin");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await app.WithUserAsync(async (users, user) => Assert.True((await users.RemoveFromRoleAsync(user, "Admin")).Succeeded));

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        using var forbidden = await app.Client.GetAsync("/test/admin");
        Assert.Equal(HttpStatusCode.Redirect, forbidden.StatusCode);
        Assert.Contains("/Identity/Account/AccessDenied", forbidden.Headers.Location!.OriginalString);
        using var secure = await app.Client.GetAsync("/test/secure");
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
    }
}
