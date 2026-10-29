// <file>
// <summary>
// HTTP tests for /api/v1 on the InMemory test host: index, reads, request-body handling, error contract.
// Commands that need ExecuteUpdate (pause, resume, PATCH, run) are tested on PostgreSQL in ApiPostgresHttpTests.
// </summary>
// </file>

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.ApiKeys;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.ApiKeys;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

public class ApiHttpTests
{
    private static async Task<(string Plaintext, string UserId)> CreateKeyAsync(SecurityTestApplication app, ApiKeyScope scope)
    {
        await using var services = app.App.Services.CreateAsyncScope();
        var users = services.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.FindByEmailAsync(SecurityTestApplication.UserEmail) ?? throw new InvalidOperationException();
        var created = await services.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync(user.Id, "agent-key", scope);
        return (created.Plaintext, user.Id);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        SecurityTestApplication app, string key, HttpMethod method, string path, string? body = null, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await app.Client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static async Task<T> SeedAsync<T>(SecurityTestApplication app, Func<ApplicationDbContext, Task<T>> seed)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var result = await seed(db);
        await db.SaveChangesAsync();
        return result;
    }

    private static MonitorEntity Http(string name, string? owner, bool paused = false, bool enabled = true) => new()
    {
        Name = name,
        Url = "https://example.com/health",
        Method = "GET",
        IntervalSeconds = 60,
        IsPaused = paused,
        Enabled = enabled,
        CreatedByUserId = owner,
        AlertEmailOverride = "ops@example.com",
    };

    private static MonitorRun Run(Guid monitorId, DateTimeOffset at, bool success, string state = "completed") => new()
    {
        MonitorId = monitorId,
        StartedAt = at,
        FinishedAt = at.AddMilliseconds(40),
        DurationMs = 40,
        State = state,
        Success = success,
        StatusCode = success ? 200 : 500,
        ErrorMessage = success ? null : "boom",
        Reason = "schedule",
        RequestUrl = "https://example.com/health",
        RequestMethod = "GET",
        ResponseSnippet = "snippet",
    };

    [Fact]
    public async Task Index_returns_caller_key_and_endpoints()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, userId) = await CreateKeyAsync(app, ApiKeyScope.Read);

        using var response = await SendAsync(app, key, HttpMethod.Get, "/api/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await JsonAsync(response);
        Assert.Equal(SecurityTestApplication.UserEmail, json.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(userId, json.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("agent-key", json.GetProperty("key").GetProperty("name").GetString());
        Assert.Equal("read", json.GetProperty("key").GetProperty("scope").GetString());
        Assert.Contains(json.GetProperty("endpoints").EnumerateArray(),
            e => e.GetProperty("method").GetString() == "PATCH" && e.GetProperty("path").GetString() == "/api/v1/monitors/{id}/alerting");
    }

    [Fact]
    public async Task List_returns_every_monitor_with_state_health_ownership_and_failure_summary()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, userId) = await CreateKeyAsync(app, ApiKeyScope.Read);
        var now = DateTimeOffset.UtcNow;
        var ids = await SeedAsync(app, async db =>
        {
            var failing = Http("failing", userId);
            failing.LastRunAt = now.AddSeconds(-30);
            var paused = Http("paused", "other-user", paused: true);
            var hb = new MonitorEntity
            {
                Name = "hb", Type = MonitorType.Heartbeat, Url = "", Method = "GET", IntervalSeconds = 3600,
                HeartbeatGraceSeconds = 300, HeartbeatToken = "0123456789abcdef0123456789abcdef", NextRunAt = now.AddMinutes(30),
            };
            db.Monitors.AddRange(failing, paused, hb);
            db.MonitorRuns.Add(Run(failing.Id, now.AddSeconds(-30), false));
            db.MonitorAlertStates.Add(new MonitorAlertState { MonitorId = failing.Id, ConsecutiveFailures = 4, FailureIncidentOpenedAt = now.AddMinutes(-4) });
            await Task.CompletedTask;
            return (failing.Id, paused.Id, hb.Id);
        });

        using var response = await SendAsync(app, key, HttpMethod.Get, "/api/v1/monitors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var monitors = (await JsonAsync(response)).GetProperty("monitors").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!);
        Assert.Equal(3, monitors.Count);
        var f = monitors["failing"];
        Assert.Equal("active", f.GetProperty("state").GetString());
        Assert.Equal("failing", f.GetProperty("health").GetString());
        Assert.True(f.GetProperty("ownedByCaller").GetBoolean());
        Assert.Equal("http", f.GetProperty("type").GetString());
        Assert.Equal(4, f.GetProperty("failure").GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal(500, f.GetProperty("failure").GetProperty("statusCode").GetInt32());
        Assert.Equal("boom", f.GetProperty("failure").GetProperty("error").GetString());
        Assert.Equal(ids.Item1, f.GetProperty("id").GetGuid());
        var p = monitors["paused"];
        Assert.Equal("paused", p.GetProperty("state").GetString());
        Assert.False(p.GetProperty("ownedByCaller").GetBoolean());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("failure").ValueKind);
        var h = monitors["hb"];
        Assert.Equal("heartbeat", h.GetProperty("type").GetString());
        Assert.Equal("unknown", h.GetProperty("health").GetString());
    }

    [Fact]
    public async Task Detail_has_alert_state_and_recipients_but_never_the_heartbeat_token()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, userId) = await CreateKeyAsync(app, ApiKeyScope.Read);
        const string token = "0123456789abcdef0123456789abcdef";
        var (httpId, hbId) = await SeedAsync(app, db =>
        {
            var http = Http("web", userId);
            var hb = new MonitorEntity
            {
                Name = "hb", Type = MonitorType.Heartbeat, Url = "", Method = "GET", IntervalSeconds = 3600,
                HeartbeatGraceSeconds = 300, HeartbeatToken = token, NextRunAt = DateTimeOffset.UtcNow.AddHours(1), CreatedByUserId = userId,
            };
            db.Monitors.AddRange(http, hb);
            db.MonitorAlertStates.Add(new MonitorAlertState { MonitorId = http.Id, ConsecutiveFailures = 2 });
            return Task.FromResult((http.Id, hb.Id));
        });

        using var response = await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{httpId}");
        var json = await JsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, json.GetProperty("alertState").GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal("monitor", json.GetProperty("alertRecipients").GetProperty("source").GetString());
        Assert.Equal("ops@example.com", json.GetProperty("alertRecipients").GetProperty("addresses")[0].GetString());
        Assert.Equal("ops@example.com", json.GetProperty("alertEmailOverride").GetString());
        Assert.Equal(60, json.GetProperty("intervalSeconds").GetInt32());

        using var hbResponse = await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{hbId}");
        var hbText = await hbResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, hbResponse.StatusCode);
        Assert.DoesNotContain(token, hbText);
        Assert.DoesNotContain("heartbeatToken", hbText, StringComparison.OrdinalIgnoreCase);
        using var hbJson = JsonDocument.Parse(hbText);
        Assert.Equal(300, hbJson.RootElement.GetProperty("heartbeatGraceSeconds").GetInt32());
        Assert.Equal("owner", hbJson.RootElement.GetProperty("alertRecipients").GetProperty("source").GetString());

        using var missing = await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{Guid.NewGuid()}");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Runs_default_to_25_cap_at_100_and_reject_bad_limits()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, userId) = await CreateKeyAsync(app, ApiKeyScope.Read);
        var now = DateTimeOffset.UtcNow;
        var (id, otherId, runId, otherRunId) = await SeedAsync(app, db =>
        {
            var m = Http("a", userId);
            var other = Http("b", userId);
            db.Monitors.AddRange(m, other);
            for (var i = 0; i < 120; i++)
                db.MonitorRuns.Add(Run(m.Id, now.AddSeconds(-i), true));
            var otherRun = Run(other.Id, now, true);
            db.MonitorRuns.Add(otherRun);
            var queued = Run(m.Id, now.AddSeconds(5), false, "queued");
            db.MonitorRuns.Add(queued);
            return Task.FromResult((m.Id, other.Id, queued.Id, otherRun.Id));
        });

        var first = await JsonAsync(await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{id}/runs"));
        Assert.Equal(25, first.GetProperty("runs").GetArrayLength());
        Assert.Equal(runId, first.GetProperty("runs")[0].GetProperty("id").GetGuid()); // newest first
        Assert.Equal("queued", first.GetProperty("runs")[0].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("runs")[0].GetProperty("success").ValueKind);

        var capped = await JsonAsync(await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{id}/runs?limit=500"));
        Assert.Equal(100, capped.GetProperty("runs").GetArrayLength());
        var three = await JsonAsync(await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{id}/runs?limit=3"));
        Assert.Equal(3, three.GetProperty("runs").GetArrayLength());

        foreach (var bad in new[] { "abc", "0", "-1", "" })
        {
            using var response = await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{id}/runs?limit={bad}");
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        }

        using var one = await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{id}/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var run = await JsonAsync(one);
        Assert.Equal(runId, run.GetProperty("id").GetGuid());
        Assert.True(run.TryGetProperty("requestUrl", out _));
        Assert.True(run.TryGetProperty("responseSnippet", out _));

        using var wrongMonitor = await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{id}/runs/{otherRunId}");
        await AssertProblemAsync(wrongMonitor, HttpStatusCode.NotFound, "not_found");
        using var unknownMonitor = await SendAsync(app, key, HttpMethod.Get, $"/api/v1/monitors/{Guid.NewGuid()}/runs");
        await AssertProblemAsync(unknownMonitor, HttpStatusCode.NotFound, "not_found");
        Assert.NotEqual(otherId, id);
    }

    [Theory]
    [InlineData("/api/v1/nope")]
    [InlineData("/api/v2/x")]
    [InlineData("/api/v1/monitors/not-a-guid")]
    public async Task Unknown_api_paths_return_404_problem_json(string path)
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, _) = await CreateKeyAsync(app, ApiKeyScope.Read);

        using var authenticated = await SendAsync(app, key, HttpMethod.Get, path);
        await AssertProblemAsync(authenticated, HttpStatusCode.NotFound, "not_found");
        using var anonymous = await app.Client.GetAsync(path);
        await AssertProblemAsync(anonymous, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Handler_failure_returns_500_problem_json()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, _) = await CreateKeyAsync(app, ApiKeyScope.Read);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/monitors");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("X-Test-Break-Db", "1");

        using var response = await app.Client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
    }

    [Fact]
    public async Task Read_key_cannot_write_and_write_endpoints_need_a_key()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, _) = await CreateKeyAsync(app, ApiKeyScope.Read);
        var id = Guid.NewGuid();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Patch, $"/api/v1/monitors/{id}/alerting"), (HttpMethod.Put, "/api/v1/account/alerting"),
            (HttpMethod.Post, $"/api/v1/monitors/{id}/pause"), (HttpMethod.Post, $"/api/v1/monitors/{id}/resume"),
            (HttpMethod.Post, $"/api/v1/monitors/{id}/run"),
        })
        {
            using var forbidden = await SendAsync(app, key, method, path, "{}");
            await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "insufficient_scope");
            using var anonymous = await app.Client.SendAsync(new HttpRequestMessage(method, path));
            await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "unauthorized");
        }
    }

    [Fact]
    public async Task Write_bodies_that_are_not_valid_json_objects_return_400_validation_failed()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, userId) = await CreateKeyAsync(app, ApiKeyScope.Write);
        var id = await SeedAsync(app, db =>
        {
            var m = Http("a", userId);
            db.Monitors.Add(m);
            return Task.FromResult(m.Id);
        });
        var oversize = "{\"alertEmailOverride\":\"" + new string('a', 20_000) + "\"}";

        foreach (var path in new[] { $"/api/v1/monitors/{id}/alerting", "/api/v1/account/alerting" })
        {
            var method = path.Contains("account", StringComparison.Ordinal) ? HttpMethod.Put : HttpMethod.Patch;
            foreach (var (body, contentType) in new[]
            {
                ("{not json", "application/json"), ("", "application/json"), ("{\"alertEmail\":null}", "text/plain"),
                (oversize, "application/json"), ("[]", "application/json"), ("\"x\"", "application/json"), ("null", "application/json"),
                ("{\"alertEmailOverride\":null,\"alertEmailOverride\":\"a@b.c\"}", "application/json"),
            })
            {
                using var response = await SendAsync(app, key, method, path, body, contentType);
                await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            }

            // No body at all and no content type.
            using var noBody = await SendAsync(app, key, method, path);
            await AssertProblemAsync(noBody, HttpStatusCode.BadRequest, "validation_failed");
        }
    }

    [Fact]
    public async Task Patch_validation_errors_return_400_with_field_errors_and_change_nothing()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, userId) = await CreateKeyAsync(app, ApiKeyScope.Write);
        var id = await SeedAsync(app, db =>
        {
            var m = Http("a", userId);
            db.Monitors.Add(m);
            return Task.FromResult(m.Id);
        });

        foreach (var body in new[]
        {
            "{\"alertEmailOverrid\":\"a@b.c\"}", "{\"alertAfterConsecutiveFailures\":0}", "{\"alertAfterConsecutiveFailures\":21}",
            "{\"alertAfterConsecutiveFailures\":null}", "{\"alertAfterConsecutiveFailures\":\"3\"}", "{\"alertAfterConsecutiveFailures\":2.5}",
            "{\"alertEmailOverride\":\"not-an-email\"}", "{\"alertEmailOverride\":5}",
            "{\"alertAfterConsecutiveFailures\":3,\"alertEmailOverrid\":\"a@b.c\"}",
        })
        {
            using var response = await SendAsync(app, key, HttpMethod.Patch, $"/api/v1/monitors/{id}/alerting", body);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            Assert.True((await JsonAsync(response)).GetProperty("errors").EnumerateObject().Any());
        }

        await using var scope = app.App.Services.CreateAsyncScope();
        var monitor = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking().SingleAsync(m => m.Id == id);
        Assert.Equal(1, monitor.AlertAfterConsecutiveFailures);
        Assert.Equal("ops@example.com", monitor.AlertEmailOverride);
    }

    [Fact]
    public async Task Put_account_alerting_validates_the_email_and_requires_the_field()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        var (key, _) = await CreateKeyAsync(app, ApiKeyScope.Write);

        foreach (var body in new[] { "{}", "{\"alertEmail\":\"nope\"}", "{\"alertEmail\":5}", "{\"alertEmail\":null,\"x\":1}" })
        {
            using var response = await SendAsync(app, key, HttpMethod.Put, "/api/v1/account/alerting", body);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        }
    }
}
