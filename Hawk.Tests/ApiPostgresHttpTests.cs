// <file>
// <summary>
// /api/v1 write endpoints over real HTTP (TestServer) on PostgreSQL: pause, resume, PATCH alerting, account alerting and run.
// These reach MonitorCommands, which is set-based (ExecuteUpdate) and so cannot run on the InMemory host.
// </summary>
// </file>

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hawk.Web.Data;
using Hawk.Web.Data.ApiKeys;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Infrastructure;
using Hawk.Web.Services;
using Hawk.Web.Services.ApiKeys;
using Hawk.Web.Services.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

public sealed class ApiPostgresHttpTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class Host(WebApplication app, HttpClient client, string writeKey, string readKey, string userId, Jobs jobs) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; } = client;
        public string WriteKey { get; } = writeKey;
        public string ReadKey { get; } = readKey;
        public string UserId { get; } = userId;
        public Jobs Jobs { get; } = jobs;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class Jobs : IBackgroundJobClient
    {
        public List<Job> Created { get; } = [];
        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return Created.Count.ToString();
        }
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private async Task<Host> CreateHostAsync()
    {
        if (fixture.UnavailableReason is not null && Environment.GetEnvironmentVariable("HAWK_REQUIRE_POSTGRES_TESTS") == "1")
            throw new InvalidOperationException($"HAWK_REQUIRE_POSTGRES_TESTS=1 but {fixture.UnavailableReason}");
        Skip.If(fixture.UnavailableReason is not null, fixture.UnavailableReason);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Services.AddHawkDbContext(DatabaseProvider.Postgres, fixture.NewDatabaseConnectionString());
        builder.Services.AddHawkIdentity();
        builder.Services.AddHawkApiAuthentication();
        builder.Services.AddScoped<ApiKeyService>();
        builder.Services.AddScoped<MonitorStatusService>();
        builder.Services.AddScoped<MonitorCommands>();
        builder.Services.AddScoped<MonitorEditor>();
        builder.Services.AddScoped<AlertRecipientResolver>();
        builder.Services.AddScoped<AccountAlertSettings>();
        var jobs = new Jobs();
        builder.Services.AddSingleton<IBackgroundJobClient>(jobs);

        var app = builder.Build();
        app.UseHawkApiErrors();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHawkApi();
        await app.StartAsync();

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = "api@example.com", Email = "api@example.com" };
        Assert.True((await users.CreateAsync(user, "Password-123!")).Succeeded);
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var write = await keys.CreateAsync(user.Id, "writer", ApiKeyScope.Write);
        var read = await keys.CreateAsync(user.Id, "reader", ApiKeyScope.Read);

        var client = app.GetTestClient();
        return new Host(app, client, write.Plaintext, read.Plaintext, user.Id, jobs);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> SendAsync(Host host, string key, HttpMethod method, string path, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<MonitorEntity> AddAsync(Host host, MonitorEntity monitor)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync();
        return monitor;
    }

    private static async Task<MonitorEntity> GetAsync(Host host, Guid id)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private static MonitorEntity Http(string? owner = null) => new()
    {
        Name = "web", Url = "https://example.com/", Method = "GET", IntervalSeconds = 60,
        AlertEmailOverride = "ops@x.com", CreatedByUserId = owner,
    };

    private static MonitorEntity Heartbeat(bool paused) => new()
    {
        Name = "hb", Type = MonitorType.Heartbeat, Url = "", Method = "GET", IntervalSeconds = 3600, HeartbeatGraceSeconds = 300,
        HeartbeatToken = HeartbeatTokens.Generate(), IsPaused = paused, NextRunAt = DateTimeOffset.UtcNow.AddDays(-2),
    };

    private static async Task<(HttpStatusCode Status, JsonElement Json, HttpResponseMessage Response)> SendFullAsync(
        Host host, HttpMethod method, string path, string? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.WriteKey);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), response);
    }

    private static async Task<MonitorEntity> GetFullAsync(Host host, Guid id)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking()
            .Include(m => m.Headers).Include(m => m.MatchRules).SingleAsync(m => m.Id == id);
    }

    [SkippableFact]
    public async Task Create_http_monitor_applies_form_defaults_and_is_owned_by_the_key_user_AE1()
    {
        await using var host = await CreateHostAsync();

        var (status, json, response) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors", "{\"name\":\" api web \",\"url\":\"https://example.com/health\",\"intervalSeconds\":60}");

        Assert.Equal(HttpStatusCode.Created, status);
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/monitors/{id}", response.Headers.Location?.ToString());
        Assert.Equal($"\"{json.GetProperty("concurrencyStamp").GetGuid():D}\"", response.Headers.ETag?.ToString());
        Assert.Equal("api web", json.GetProperty("name").GetString());
        Assert.Equal("http", json.GetProperty("type").GetString());
        Assert.Equal("GET", json.GetProperty("method").GetString());
        Assert.Equal(15, json.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(1, json.GetProperty("alertAfterConsecutiveFailures").GetInt32());
        Assert.Equal("active", json.GetProperty("state").GetString());
        Assert.True(json.GetProperty("ownedByCaller").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("pingUrl").ValueKind);

        var saved = await GetFullAsync(host, id);
        Assert.Equal(host.UserId, saved.CreatedByUserId);
        Assert.True(saved.Enabled);
        Assert.False(saved.IsPaused);
        var defaults = new Hawk.Web.Pages.Monitors.MonitorForm();
        Assert.Equal(defaults.Method, saved.Method);
        Assert.Equal(defaults.TimeoutSeconds, saved.TimeoutSeconds);
        Assert.Equal(defaults.AlertAfterConsecutiveFailures, saved.AlertAfterConsecutiveFailures);
        Assert.Equal(defaults.ContentType, saved.ContentType);

        var (_, list) = await SendAsync(host, host.ReadKey, HttpMethod.Get, "/api/v1/monitors");
        var item = list.GetProperty("monitors").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == id);
        Assert.True(item.GetProperty("ownedByCaller").GetBoolean());
    }

    [SkippableFact]
    public async Task Create_stores_headers_and_match_rules_accepting_mode_or_type_and_more_than_five_headers()
    {
        await using var host = await CreateHostAsync();
        var headers = string.Join(",", Enumerable.Range(0, 7).Select(i => $"{{\"name\":\"X-H{i}\",\"value\":\" v{i} \"}}"));

        var (status, json, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors",
            "{\"name\":\"rich\",\"url\":\"https://example.com/\",\"method\":\"post\",\"body\":\"{}\",\"allowedStatusCodes\":\"404, 429\"," +
            "\"alertEmailOverride\":\"ops@x.com\",\"runRetentionDays\":30,\"enabled\":false," +
            $"\"headers\":[{headers}],\"matchRules\":[{{\"mode\":\"contains\",\"pattern\":\"ok\"}},{{\"type\":\"regex\",\"pattern\":\"a+\"}}]}}");

        Assert.Equal(HttpStatusCode.Created, status);
        var saved = await GetFullAsync(host, json.GetProperty("id").GetGuid());
        Assert.Equal("POST", saved.Method);
        Assert.False(saved.Enabled);
        Assert.Equal("404,429", saved.AllowedStatusCodes);
        Assert.Equal(30, saved.RunRetentionDays);
        Assert.Equal(7, saved.Headers.Count);
        Assert.Contains(saved.Headers, h => h.Name == "X-H3" && h.Value == "v3");
        Assert.Equal([ContentMatchMode.Contains, ContentMatchMode.Regex], saved.MatchRules.Select(r => r.Mode).Order());
        Assert.Equal("disabled", json.GetProperty("state").GetString());
        Assert.Equal(7, json.GetProperty("headers").GetArrayLength());
    }

    [SkippableFact]
    public async Task Create_heartbeat_returns_the_ping_url_once_and_get_never_contains_the_token_AE2()
    {
        await using var host = await CreateHostAsync();

        var (status, json, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors",
            "{\"name\":\"nightly\",\"type\":\"heartbeat\",\"intervalSeconds\":3600,\"heartbeatGraceSeconds\":300}");

        Assert.Equal(HttpStatusCode.Created, status);
        var id = json.GetProperty("id").GetGuid();
        var saved = await GetFullAsync(host, id);
        Assert.Matches("^[0-9a-f]{32}$", saved.HeartbeatToken);
        Assert.Equal($"http://localhost/ping/{saved.HeartbeatToken}", json.GetProperty("pingUrl").GetString());
        Assert.Equal(host.UserId, saved.CreatedByUserId);
        Assert.False(saved.IsPaused);
        Assert.True(saved.NextRunAt >= DateTimeOffset.UtcNow.AddSeconds(3800));
        Assert.Equal(300, json.GetProperty("heartbeatGraceSeconds").GetInt32());

        var get = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/monitors/{id}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", host.ReadKey) },
        });
        var text = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain(saved.HeartbeatToken!, text);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(text).RootElement.GetProperty("pingUrl").ValueKind);
        var (_, list) = await SendAsync(host, host.ReadKey, HttpMethod.Get, "/api/v1/monitors");
        Assert.DoesNotContain(saved.HeartbeatToken!, list.GetRawText());
    }

    [SkippableFact]
    public async Task Patch_heartbeat_moves_the_deadline_only_for_period_grace_or_re_enable_AE3()
    {
        await using var host = await CreateHostAsync();
        var (_, created, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors", "{\"name\":\"hb\",\"type\":\"heartbeat\",\"intervalSeconds\":3600}");
        var id = created.GetProperty("id").GetGuid();
        var path = $"/api/v1/monitors/{id}";
        var original = await GetFullAsync(host, id);

        var (status, json, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"name\":\"x\"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("x", json.GetProperty("name").GetString());
        var renamed = await GetFullAsync(host, id);
        Assert.Equal(original.NextRunAt, renamed.NextRunAt);
        Assert.Equal(original.HeartbeatToken, renamed.HeartbeatToken);
        Assert.NotEqual(original.ConcurrencyStamp, renamed.ConcurrencyStamp);
        Assert.Equal(renamed.ConcurrencyStamp, json.GetProperty("concurrencyStamp").GetGuid());

        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"intervalSeconds\":86400}");
        Assert.Equal(HttpStatusCode.OK, status);
        var longer = await GetFullAsync(host, id);
        Assert.Equal(86400, longer.IntervalSeconds);
        Assert.True(longer.NextRunAt > original.NextRunAt!.Value.AddHours(20));

        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"enabled\":false}");
        var disabled = await GetFullAsync(host, id);
        Assert.False(disabled.Enabled);
        Assert.Equal(longer.NextRunAt, disabled.NextRunAt);
        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"enabled\":true,\"heartbeatGraceSeconds\":900}");
        var reenabled = await GetFullAsync(host, id);
        Assert.Equal(900, reenabled.HeartbeatGraceSeconds);
        Assert.True(reenabled.NextRunAt >= DateTimeOffset.UtcNow.AddSeconds(86400 + 800));
    }

    [SkippableFact]
    public async Task Patch_http_merges_scalars_and_replaces_headers_and_rules_only_when_present()
    {
        await using var host = await CreateHostAsync();
        var seeded = Http(host.UserId);
        seeded.ContentType = "application/json";
        for (var i = 0; i < 7; i++)
            seeded.Headers.Add(new MonitorHeader { Name = $"X-H{i}", Value = "v" });
        seeded.MatchRules.Add(new MonitorMatchRule { Mode = ContentMatchMode.Contains, Pattern = "ok" });
        var m = await AddAsync(host, seeded);
        var path = $"/api/v1/monitors/{m.Id}";

        var (status, json, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"name\":\"renamed\",\"timeoutSeconds\":30}");
        Assert.Equal(HttpStatusCode.OK, status);
        var renamed = await GetFullAsync(host, m.Id);
        Assert.Equal("renamed", renamed.Name);
        Assert.Equal(30, renamed.TimeoutSeconds);
        Assert.Equal("https://example.com/", renamed.Url);
        Assert.Equal("ops@x.com", renamed.AlertEmailOverride);
        Assert.Equal(7, renamed.Headers.Count);
        Assert.Single(renamed.MatchRules);
        Assert.Equal(7, json.GetProperty("headers").GetArrayLength());

        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, path,
            "{\"headers\":[{\"name\":\"Authorization\",\"value\":\"Bearer t\"}],\"alertEmailOverride\":null,\"runRetentionDays\":10}");
        Assert.Equal(HttpStatusCode.OK, status);
        var replaced = await GetFullAsync(host, m.Id);
        Assert.Equal("Authorization", Assert.Single(replaced.Headers).Name);
        Assert.Single(replaced.MatchRules);
        Assert.Null(replaced.AlertEmailOverride);
        Assert.Equal(10, replaced.RunRetentionDays);

        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"matchRules\":[],\"type\":\"http\"}");
        Assert.Equal(HttpStatusCode.OK, status);
        var cleared = await GetFullAsync(host, m.Id);
        Assert.Empty(cleared.MatchRules);
        Assert.Single(cleared.Headers);

        // Validation errors on an existing monitor change nothing.
        var before = await GetFullAsync(host, m.Id);
        (status, json, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"name\":\"nope\",\"matchRules\":[{\"mode\":\"regex\",\"pattern\":\"(\"}]}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(json.GetProperty("errors").TryGetProperty("matchRules[0].pattern", out _));
        var after = await GetFullAsync(host, m.Id);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
    }

    [SkippableFact]
    public async Task Patch_honours_if_match_in_quoted_and_bare_form_and_rejects_stale_values_AE4()
    {
        await using var host = await CreateHostAsync();
        var m = await AddAsync(host, Http(host.UserId));
        var path = $"/api/v1/monitors/{m.Id}";

        var (status, json, response) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"name\":\"first\"}", ifMatch: $"\"{m.ConcurrencyStamp:D}\"");
        Assert.Equal(HttpStatusCode.OK, status);
        var next = json.GetProperty("concurrencyStamp").GetGuid();
        Assert.NotEqual(m.ConcurrencyStamp, next);
        Assert.Equal($"\"{next:D}\"", response.Headers.ETag?.ToString());

        // The old stamp is now stale: 412, nothing changes, the stamp stays.
        (status, json, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"name\":\"stale\"}", ifMatch: $"\"{m.ConcurrencyStamp:D}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal("precondition_failed", json.GetProperty("code").GetString());
        var saved = await GetFullAsync(host, m.Id);
        Assert.Equal("first", saved.Name);
        Assert.Equal(next, saved.ConcurrencyStamp);

        // Bare GUID works; without If-Match the last write wins.
        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"name\":\"second\"}", ifMatch: next.ToString("D"));
        Assert.Equal(HttpStatusCode.OK, status);
        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"name\":\"third\"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("third", (await GetFullAsync(host, m.Id)).Name);

        // PATCH {"type":"heartbeat"} on an HTTP monitor is a 400.
        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"type\":\"heartbeat\"}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [SkippableFact]
    public async Task Delete_removes_the_monitor_with_its_runs_and_honours_if_match_AE5()
    {
        await using var host = await CreateHostAsync();
        var seeded = Http(host.UserId);
        seeded.Headers.Add(new MonitorHeader { Name = "A", Value = "b" });
        var m = await AddAsync(host, seeded);
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MonitorRuns.Add(new MonitorRun { MonitorId = m.Id, StartedAt = DateTimeOffset.UtcNow, State = "completed", Success = true });
            db.MonitorAlertStates.Add(new Hawk.Web.Data.Alerting.MonitorAlertState { MonitorId = m.Id });
            await db.SaveChangesAsync();
        }

        var path = $"/api/v1/monitors/{m.Id}";
        var (status, json, _) = await SendFullAsync(host, HttpMethod.Delete, path, ifMatch: $"\"{Guid.NewGuid()}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal("precondition_failed", json.GetProperty("code").GetString());
        Assert.NotNull(await GetFullAsync(host, m.Id));

        (status, json, _) = await SendFullAsync(host, HttpMethod.Delete, path, ifMatch: $"\"{m.ConcurrencyStamp:D}\"");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(JsonValueKind.Undefined, json.ValueKind);

        (status, json, _) = await SendFullAsync(host, HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.NotFound, status);
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.MonitorRuns.AnyAsync(r => r.MonitorId == m.Id));
            Assert.False(await db.MonitorHeaders.AnyAsync(h => h.MonitorId == m.Id));
            Assert.False(await db.MonitorAlertStates.AnyAsync(s => s.MonitorId == m.Id));
        }

        (status, _, _) = await SendFullAsync(host, HttpMethod.Delete, path);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [SkippableFact]
    public async Task Patch_alerting_follows_merge_patch_semantics_AE1()
    {
        await using var host = await CreateHostAsync();
        var m = await AddAsync(host, Http());
        var path = $"/api/v1/monitors/{m.Id}/alerting";

        var (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Patch, path, "{\"alertAfterConsecutiveFailures\":3}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, json.GetProperty("alertAfterConsecutiveFailures").GetInt32());
        Assert.Equal("ops@x.com", json.GetProperty("alertEmailOverride").GetString());
        Assert.Equal("monitor", json.GetProperty("alertRecipients").GetProperty("source").GetString());
        var afterThreshold = await GetAsync(host, m.Id);
        Assert.Equal(3, afterThreshold.AlertAfterConsecutiveFailures);
        Assert.Equal("ops@x.com", afterThreshold.AlertEmailOverride);
        Assert.NotEqual(m.ConcurrencyStamp, afterThreshold.ConcurrencyStamp); // R15: an open Edit form sees a conflict

        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Patch, path, "{\"alertEmailOverride\":null}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("alertEmailOverride").ValueKind);
        Assert.Equal("admins", json.GetProperty("alertRecipients").GetProperty("source").GetString());
        var cleared = await GetAsync(host, m.Id);
        Assert.Null(cleared.AlertEmailOverride);
        Assert.Equal(3, cleared.AlertAfterConsecutiveFailures);

        // Blank clears too; set it again first.
        await SendAsync(host, host.WriteKey, HttpMethod.Patch, path, "{\"alertEmailOverride\":\"a@b.co\"}");
        Assert.Equal("a@b.co", (await GetAsync(host, m.Id)).AlertEmailOverride);
        await SendAsync(host, host.WriteKey, HttpMethod.Patch, path, "{\"alertEmailOverride\":\"  \"}");
        Assert.Null((await GetAsync(host, m.Id)).AlertEmailOverride);

        // {} changes nothing, not even the stamp.
        var before = await GetAsync(host, m.Id);
        (status, _) = await SendAsync(host, host.WriteKey, HttpMethod.Patch, path, "{}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(before.ConcurrencyStamp, (await GetAsync(host, m.Id)).ConcurrencyStamp);

        // A typo returns 400 and changes nothing.
        var beforeTypo = await GetAsync(host, m.Id);
        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Patch, path, "{\"alertEmailOverrid\":\"a@b.c\"}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("validation_failed", json.GetProperty("code").GetString());
        var afterTypo = await GetAsync(host, m.Id);
        Assert.Equal(beforeTypo.ConcurrencyStamp, afterTypo.ConcurrencyStamp);
        Assert.Null(afterTypo.AlertEmailOverride);

        (status, _) = await SendAsync(host, host.WriteKey, HttpMethod.Patch, $"/api/v1/monitors/{Guid.NewGuid()}/alerting", "{}");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [SkippableFact]
    public async Task Pause_and_resume_report_changed_and_handle_heartbeats_and_disabled_monitors_AE2()
    {
        await using var host = await CreateHostAsync();
        var http = await AddAsync(host, Http());

        var (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{http.Id}/pause");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(json.GetProperty("changed").GetBoolean());
        Assert.Equal("paused", json.GetProperty("state").GetString());
        var paused = await GetAsync(host, http.Id);
        Assert.True(paused.IsPaused);
        Assert.NotEqual(http.ConcurrencyStamp, paused.ConcurrencyStamp);

        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{http.Id}/pause");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(json.GetProperty("changed").GetBoolean());

        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{http.Id}/resume");
        Assert.True(json.GetProperty("changed").GetBoolean());
        Assert.Equal("active", json.GetProperty("state").GetString());
        Assert.False((await GetAsync(host, http.Id)).IsPaused);
        (_, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{http.Id}/resume");
        Assert.False(json.GetProperty("changed").GetBoolean());

        // A resumed heartbeat gets a fresh deadline: now + period + grace.
        var hb = await AddAsync(host, Heartbeat(paused: true));
        var started = DateTimeOffset.UtcNow;
        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{hb.Id}/resume");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(json.GetProperty("changed").GetBoolean());
        var resumed = await GetAsync(host, hb.Id);
        Assert.False(resumed.IsPaused);
        Assert.InRange(resumed.NextRunAt!.Value, started.AddSeconds(3900), DateTimeOffset.UtcNow.AddSeconds(3900));

        var disabled = Http();
        disabled.Enabled = false;
        disabled = await AddAsync(host, disabled);
        foreach (var action in new[] { "pause", "resume" })
        {
            (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{disabled.Id}/{action}");
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("monitor_disabled", json.GetProperty("code").GetString());
        }

        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{Guid.NewGuid()}/pause");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", json.GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Put_account_alerting_sets_and_clears_the_callers_email()
    {
        await using var host = await CreateHostAsync();

        var (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Put, "/api/v1/account/alerting", "{\"alertEmail\":\" me@x.com \"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("me@x.com", json.GetProperty("alertEmail").GetString());
        Assert.Contains("owned by the caller", json.GetProperty("appliesTo").GetString());

        (_, json) = await SendAsync(host, host.ReadKey, HttpMethod.Get, "/api/v1/account/alerting");
        Assert.Equal("me@x.com", json.GetProperty("alertEmail").GetString());

        // The account email is the recipient source for the caller's monitors without an override.
        var own = Http(host.UserId);
        own.AlertEmailOverride = null;
        own = await AddAsync(host, own);
        (_, json) = await SendAsync(host, host.ReadKey, HttpMethod.Get, $"/api/v1/monitors/{own.Id}");
        Assert.Equal("account", json.GetProperty("alertRecipients").GetProperty("source").GetString());

        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Put, "/api/v1/account/alerting", "{\"alertEmail\":null}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("alertEmail").ValueKind);
        (_, json) = await SendAsync(host, host.ReadKey, HttpMethod.Get, "/api/v1/account/alerting");
        Assert.Equal(JsonValueKind.Null, json.GetProperty("alertEmail").ValueKind);
    }

    [SkippableFact]
    public async Task Run_queues_once_and_rejects_heartbeats_and_disabled_monitors()
    {
        await using var host = await CreateHostAsync();
        var http = await AddAsync(host, Http());

        var (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{http.Id}/run");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var runId = json.GetProperty("runId").GetGuid();
        Assert.Equal($"/api/v1/monitors/{http.Id}/runs/{runId}", json.GetProperty("runUrl").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("pollAdvice").GetString()));
        Assert.Single(host.Jobs.Created);

        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{http.Id}/run");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(runId, json.GetProperty("runId").GetGuid());
        Assert.Single(host.Jobs.Created);

        var (runStatus, run) = await SendAsync(host, host.ReadKey, HttpMethod.Get, $"/api/v1/monitors/{http.Id}/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, runStatus);
        Assert.Equal("queued", run.GetProperty("state").GetString());

        var hb = await AddAsync(host, Heartbeat(paused: false));
        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{hb.Id}/run");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("heartbeat_not_runnable", json.GetProperty("code").GetString());

        var disabled = Http();
        disabled.Enabled = false;
        disabled = await AddAsync(host, disabled);
        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{disabled.Id}/run");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("monitor_disabled", json.GetProperty("code").GetString());

        var paused = Http();
        paused.IsPaused = true;
        paused = await AddAsync(host, paused);
        (status, _) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{paused.Id}/run");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(2, host.Jobs.Created.Count);

        (status, _) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{Guid.NewGuid()}/run");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
