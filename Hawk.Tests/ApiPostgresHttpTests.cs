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
