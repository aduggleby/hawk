// <file>
// <summary>
// Real Razor Pages requests (antiforgery, binding, validation) for the Account API keys page on an in-memory database.
// </summary>
// </file>

using System.Net;
using System.Text.RegularExpressions;
using Hawk.Web.Data;
using Hawk.Web.Data.ApiKeys;
using Hawk.Web.Services.ApiKeys;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hawk.Tests;

public class ApiKeysPageHttpTests
{
    private const string PagePath = "/Identity/Account/Manage/ApiKeys";

    [Fact]
    public async Task Create_shows_the_key_once_and_later_lists_never_contain_it()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();

        using var created = await PostAsync(app, PagePath, new() { ["Name"] = "CI agent", ["Scope"] = "Write" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString());
        var html = await created.Content.ReadAsStringAsync();
        var plaintext = Regex.Match(html, "hawk_[A-Za-z0-9_-]{43}").Value;
        Assert.NotEmpty(plaintext);
        Assert.Contains("Copy this key now, it will not be shown again", html);
        Assert.Contains("/api/v1", html);
        Assert.Contains("Authorization: Bearer", html);

        var key = (await ListAsync(app)).Single();
        Assert.Equal("CI agent", key.Name);
        Assert.Equal(ApiKeyScope.Write, key.Scope);
        Assert.StartsWith(key.DisplayPrefix, plaintext);

        var list = await app.Client.GetStringAsync(PagePath);
        Assert.DoesNotContain(plaintext, list);
        Assert.DoesNotContain("Copy this key now", list);
        Assert.Contains("CI agent", list);
        Assert.Contains(key.DisplayPrefix, list);
        Assert.Contains("Never", list);
    }

    [Fact]
    public async Task Empty_page_shows_empty_state_and_no_revoke_all()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        var html = await app.Client.GetStringAsync(PagePath);
        Assert.Contains("No API keys yet", html);
        Assert.DoesNotContain("Revoke all", html);
    }

    [Fact]
    public async Task Create_without_scope_creates_a_read_key()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var created = await PostAsync(app, PagePath, new() { ["Name"] = "default" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(ApiKeyScope.Read, (await ListAsync(app)).Single().Scope);
    }

    [Theory]
    [InlineData("", "Read")]
    [InlineData("   ", "Read")]
    [InlineData("ok", "Bogus")]
    [InlineData("ok", "7")]
    public async Task Invalid_name_or_scope_creates_nothing_and_shows_no_key(string name, string scope)
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var response = await PostAsync(app, PagePath, new() { ["Name"] = name, ["Scope"] = scope });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("field-validation-error", html);
        Assert.DoesNotContain("Copy this key now", html);
        Assert.Empty(await ListAsync(app));
    }

    [Fact]
    public async Task Overlong_name_is_rejected()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var response = await PostAsync(app, PagePath, new() { ["Name"] = new string('a', 101), ["Scope"] = "Read" });
        Assert.Empty(await ListAsync(app));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_sets_RevokedAt_redirects_and_the_key_no_longer_validates()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var created = await PostAsync(app, PagePath, new() { ["Name"] = "temp" });
        var plaintext = Regex.Match(await created.Content.ReadAsStringAsync(), "hawk_[A-Za-z0-9_-]{43}").Value;
        var id = (await ListAsync(app)).Single().Id;
        await AssertValidAsync(app, plaintext, true);

        using var revoked = await PostAsync(app, PagePath, new() { ["keyId"] = id.ToString() }, "Revoke");
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        await AssertValidAsync(app, plaintext, false);
        Assert.Empty(await ListAsync(app));
        Assert.NotNull((await AllAsync(app)).Single().RevokedAt);

        var html = await app.Client.GetStringAsync(PagePath);
        Assert.Contains("revoked", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No API keys yet", html);
    }

    [Fact]
    public async Task Revoke_all_revokes_every_key()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using (await PostAsync(app, PagePath, new() { ["Name"] = "a" })) { }
        using (await PostAsync(app, PagePath, new() { ["Name"] = "b" })) { }
        var html = await app.Client.GetStringAsync(PagePath);
        Assert.Contains("Revoke all", html);
        Assert.Contains("2", html);

        using var response = await PostAsync(app, PagePath, new(), "RevokeAll");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(await ListAsync(app));
    }

    [Fact]
    public async Task Revoking_another_users_key_changes_nothing()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        Guid otherId;
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync("someone-else", "theirs", ApiKeyScope.Read);
            otherId = created.Key.Id;
        }

        using var response = await PostAsync(app, PagePath, new() { ["keyId"] = otherId.ToString() }, "Revoke");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null((await AllAsync(app)).Single(k => k.Id == otherId).RevokedAt);

        using var all = await PostAsync(app, PagePath, new(), "RevokeAll");
        Assert.Null((await AllAsync(app)).Single(k => k.Id == otherId).RevokedAt);
    }

    [Fact]
    public async Task Revoke_confirm_is_a_well_formed_call_inside_the_encoded_onsubmit_attribute()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        const string name = "a\"b'<c>";
        using var created = await PostAsync(app, PagePath, new() { ["Name"] = name });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var html = await app.Client.GetStringAsync(PagePath);
        var raw = Regex.Matches(html, "onsubmit=\"([^\"]*)\"").Select(m => m.Groups[1].Value).Single(v => v.Contains("Revoke the API key", StringComparison.Ordinal) || v.Contains("Revoke&#x20;the", StringComparison.Ordinal) || !v.Contains("Revoke all", StringComparison.Ordinal));
        var decoded = WebUtility.HtmlDecode(raw);
        var match = Regex.Match(decoded, "^return confirm\\((\".*\")\\);$", RegexOptions.Singleline);
        Assert.True(match.Success, $"onsubmit was: {raw}");
        Assert.Equal($"Revoke the API key \"{name}\"? It stops working immediately.", System.Text.Json.JsonSerializer.Deserialize<string>(match.Groups[1].Value));
    }

    [Fact]
    public async Task Name_with_a_NUL_character_is_rejected_by_the_page_model()
    {
        // The host already answers 400 to form values containing NUL; the page model guards on its own (PostgreSQL text rejects NUL).
        await using var app = await SecurityTestApplication.CreateAsync();
        await using var scope = app.App.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var page = new Hawk.Web.Areas.Identity.Pages.Account.Manage.ApiKeysModel(keys, users) { Name = "bad\0name" };
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "u-nul")], "test")),
        };
        page.PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http };

        await page.OnPostAsync(CancellationToken.None);

        Assert.Null(page.NewKey);
        Assert.Contains(page.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage == "Fields must not contain NUL characters.");
        Assert.Empty(await AllAsync(app));
    }

    [Fact]
    public async Task Page_requires_login_and_is_linked_from_the_account_nav()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        using var anonymous = await app.Client.GetAsync(PagePath);
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);

        await app.LoginAsync();
        var html = await app.Client.GetStringAsync(PagePath);
        Assert.Contains(PagePath, html);
        Assert.Contains("API keys", html);
    }

    private static async Task AssertValidAsync(SecurityTestApplication app, string plaintext, bool expected)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().ValidateAsync(plaintext);
        Assert.Equal(expected, result is not null);
    }

    private static async Task<List<ApiKey>> ListAsync(SecurityTestApplication app)
    {
        var all = await AllAsync(app);
        return all.Where(k => k.RevokedAt is null && k.UserId != "someone-else").ToList();
    }

    private static async Task<List<ApiKey>> AllAsync(SecurityTestApplication app)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ApiKeys.AsNoTracking().ToListAsync();
    }

    private static async Task<HttpResponseMessage> PostAsync(SecurityTestApplication app, string page, Dictionary<string, string> fields, string? handler = null)
    {
        using var get = await app.Client.GetAsync(page);
        get.EnsureSuccessStatusCode();
        fields["__RequestVerificationToken"] = SecurityTestApplication.AntiforgeryToken(await get.Content.ReadAsStringAsync());
        var url = handler is null ? page : $"{page}?handler={handler}";
        return await app.Client.PostAsync(url, new FormUrlEncodedContent(fields));
    }
}
