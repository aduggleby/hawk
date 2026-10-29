using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Hawk.Web.Data.ApiKeys;
using Hawk.Web.Services.ApiKeys;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Hawk.Tests;

public class ApiAuthHttpTests
{
    private const string Read = "/api/v1/_test/read";
    private const string Write = "/api/v1/_test/write";

    private static async Task<(CreatedApiKey Created, string UserId)> CreateKeyAsync(SecurityTestApplication app, ApiKeyScope scope)
    {
        await using var services = app.App.Services.CreateAsyncScope();
        var users = services.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.FindByEmailAsync(SecurityTestApplication.UserEmail) ?? throw new InvalidOperationException();
        var created = await services.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync(user.Id, "test", scope);
        return (created, user.Id);
    }

    private static Task<HttpResponseMessage> SendAsync(SecurityTestApplication app, HttpMethod method, string path, string? authorization, bool breakDb = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (breakDb) request.Headers.Add("X-Test-Break-Db", "1");
        return app.Client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal((int)status, doc.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Missing_malformed_unknown_and_revoked_keys_return_401_problem_json()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (created, userId) = await CreateKeyAsync(app, ApiKeyScope.Read);
        await using (var scope = app.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApiKeyService>().RevokeAllAsync(userId);

        foreach (var header in new string?[]
        {
            null, "Bearer", "Bearer ", "Basic abc", "hawk_abc", "Bearer not-a-key",
            "Bearer hawk_" + new string('a', 43), "Bearer " + created.Plaintext
        })
        {
            using var response = await SendAsync(app, HttpMethod.Get, Read, header);
            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        }
    }

    [Fact]
    public async Task Login_cookie_without_a_key_is_401_and_never_redirects()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var response = await app.Client.GetAsync(Read);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Cookie_does_not_help_a_request_with_an_invalid_key()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var response = await SendAsync(app, HttpMethod.Get, Read, "Bearer hawk_" + new string('a', 43));
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Read_key_is_forbidden_on_write_and_write_key_passes_both()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var read = (await CreateKeyAsync(app, ApiKeyScope.Read)).Created.Plaintext;
        var write = (await CreateKeyAsync(app, ApiKeyScope.Write)).Created.Plaintext;

        using (var ok = await SendAsync(app, HttpMethod.Get, Read, "Bearer " + read))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var forbidden = await SendAsync(app, HttpMethod.Post, Write, "Bearer " + read))
            await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "insufficient_scope");
        using (var readOk = await SendAsync(app, HttpMethod.Get, Read, "Bearer " + write))
            Assert.Equal(HttpStatusCode.OK, readOk.StatusCode);
        using (var writeOk = await SendAsync(app, HttpMethod.Post, Write, "Bearer " + write))
            Assert.Equal(HttpStatusCode.OK, writeOk.StatusCode);
    }

    [Fact]
    public async Task Key_of_a_deleted_user_is_401()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var key = (await CreateKeyAsync(app, ApiKeyScope.Read)).Created.Plaintext;
        await app.WithUserAsync(async (users, user) => Assert.True((await users.DeleteAsync(user)).Succeeded));

        using var response = await SendAsync(app, HttpMethod.Get, Read, "Bearer " + key);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Key_of_a_locked_out_user_still_works()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var key = (await CreateKeyAsync(app, ApiKeyScope.Read)).Created.Plaintext;
        await app.WithUserAsync(async (users, user) =>
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddDays(1))).Succeeded));

        using var response = await SendAsync(app, HttpMethod.Get, Read, "Bearer " + key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Principal_carries_user_key_and_scope_but_no_roles_even_for_an_admin()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (created, userId) = await CreateKeyAsync(app, ApiKeyScope.Write);
        await app.WithUserAsync(async (users, user) => Assert.True(await users.IsInRoleAsync(user, "Admin")));

        using var response = await SendAsync(app, HttpMethod.Get, Read, "Bearer " + created.Plaintext);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(userId, doc.RootElement.GetProperty("userId").GetString());
        Assert.Equal(created.Key.Id.ToString(), doc.RootElement.GetProperty("keyId").GetString());
        Assert.Equal("write", doc.RootElement.GetProperty("scope").GetString());
        Assert.Empty(doc.RootElement.GetProperty("roles").EnumerateArray());
    }

    [Fact]
    public async Task Successful_authentication_touches_last_used()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var created = (await CreateKeyAsync(app, ApiKeyScope.Read)).Created;
        using var response = await SendAsync(app, HttpMethod.Get, Read, "Bearer " + created.Plaintext);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = app.App.Services.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().ListAsync(created.Key.UserId);
        Assert.NotNull(keys.Single().LastUsedAt);
    }

    [Fact]
    public async Task Key_in_query_string_is_ignored_and_not_echoed()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var key = (await CreateKeyAsync(app, ApiKeyScope.Read)).Created.Plaintext;

        foreach (var name in new[] { "access_token", "key", "api_key" })
        {
            using var response = await SendAsync(app, HttpMethod.Get, $"{Read}?{name}={key}", null);
            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
            Assert.DoesNotContain(key, await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(key, string.Join(";", response.Headers.SelectMany(h => h.Value)));
        }
    }

    [Fact]
    public async Task Key_lookup_failure_is_a_500_problem_not_the_html_error_page()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var key = (await CreateKeyAsync(app, ApiKeyScope.Read)).Created.Plaintext;

        using var response = await SendAsync(app, HttpMethod.Get, Read, "Bearer " + key, breakDb: true);
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain(SecurityTestApplication.PrivateException, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Endpoint_exception_is_a_500_problem_that_hides_details()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var key = (await CreateKeyAsync(app, ApiKeyScope.Read)).Created.Plaintext;

        using var response = await SendAsync(app, HttpMethod.Get, "/api/v1/_test/throw", "Bearer " + key);
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain(SecurityTestApplication.PrivateException, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_api_paths_and_wrong_methods_return_problem_json()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        using var notFound = await SendAsync(app, HttpMethod.Get, "/api/nothing-here", null);
        await AssertProblemAsync(notFound, HttpStatusCode.NotFound, "not_found");
        using var wrongMethod = await SendAsync(app, HttpMethod.Delete, Write, null);
        await AssertProblemAsync(wrongMethod, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
    }
}
