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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ApiPostgresHttpTests(PostgresFixture fixture)
{
    private sealed class Host(WebApplication app, HttpClient client, string writeKey, string readKey, string userId, Jobs jobs, RaceInterceptor race) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; } = client;
        public string WriteKey { get; } = writeKey;
        public string ReadKey { get; } = readKey;
        public string UserId { get; } = userId;
        public Jobs Jobs { get; } = jobs;
    public RaceInterceptor Race { get; } = race;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    /// <summary>Runs a hook right before the next save that updates a monitor, to simulate a concurrent write between snapshot and save.</summary>
    private sealed class RaceInterceptor : SaveChangesInterceptor
    {
        public Func<Task>? Hook { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Hook is { } hook && eventData.Context!.ChangeTracker.Entries<MonitorEntity>().Any(e => e.State == EntityState.Modified))
            {
                Hook = null;
                await hook();
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
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
        var connectionString = fixture.NewDatabaseConnectionString();
        builder.Services.AddHawkDbContext(DatabaseProvider.Postgres, connectionString);
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
        var race = new RaceInterceptor();
        // Registered after AddHawkDbContext so its options (and the interceptor) apply on top of the production setup.
        builder.Services.AddDbContext<ApplicationDbContext, PostgresApplicationDbContext>(o => o.UseNpgsql(connectionString).AddInterceptors(race));

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
        return new Host(app, client, write.Plaintext, read.Plaintext, user.Id, jobs, race);
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
    public async Task Create_stores_headers_and_match_rules_accepting_mode_or_type_and_five_headers()
    {
        await using var host = await CreateHostAsync();
        var headers = string.Join(",", Enumerable.Range(0, 5).Select(i => $"{{\"name\":\"X-H{i}\",\"value\":\" v{i} \"}}"));

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
        Assert.Equal(5, saved.Headers.Count);
        Assert.Contains(saved.Headers, h => h.Name == "X-H3" && h.Value == "v3");
        Assert.Equal([ContentMatchMode.Contains, ContentMatchMode.Regex], saved.MatchRules.Select(r => r.Mode).Order());
        Assert.Equal("disabled", json.GetProperty("state").GetString());
        Assert.Equal(5, json.GetProperty("headers").GetArrayLength());
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
        for (var i = 0; i < 5; i++)
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
        Assert.Equal(5, renamed.Headers.Count);
        Assert.Single(renamed.MatchRules);
        Assert.Equal(5, json.GetProperty("headers").GetArrayLength());

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
    public async Task Patch_without_if_match_conflicts_when_the_monitor_changed_after_the_snapshot_and_keeps_the_concurrent_change()
    {
        await using var host = await CreateHostAsync();
        var m = await AddAsync(host, Http(host.UserId));
        var path = $"/api/v1/monitors/{m.Id}";
        host.Race.Hook = async () =>
        {
            // A concurrent intentional change (like a pause or an Edit save) that renews the stamp.
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Monitors.Where(x => x.Id == m.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.TimeoutSeconds, 99).SetProperty(x => x.ConcurrencyStamp, Guid.NewGuid()));
        };

        var (status, json, _) = await SendFullAsync(host, HttpMethod.Patch, path, "{\"name\":\"mine\"}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("conflict", json.GetProperty("code").GetString());
        var saved = await GetFullAsync(host, m.Id);
        Assert.Equal("web", saved.Name);
        Assert.Equal(99, saved.TimeoutSeconds);
    }

    [SkippableFact]
    public async Task Patch_with_if_match_that_changed_after_the_snapshot_returns_412()
    {
        await using var host = await CreateHostAsync();
        var m = await AddAsync(host, Http(host.UserId));
        host.Race.Hook = async () =>
        {
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Monitors.Where(x => x.Id == m.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ConcurrencyStamp, Guid.NewGuid()));
        };

        var (status, json, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{m.Id}", "{\"name\":\"mine\"}", ifMatch: m.ConcurrencyStamp.ToString("D"));

        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal("precondition_failed", json.GetProperty("code").GetString());
        Assert.Equal("web", (await GetFullAsync(host, m.Id)).Name);
    }

    [SkippableFact]
    public async Task Blank_alert_email_override_clears_it_on_create_and_patch()
    {
        await using var host = await CreateHostAsync();
        var (status, created, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors",
            "{\"name\":\"a\",\"url\":\"https://example.com/\",\"alertEmailOverride\":\"  \"}");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Null((await GetFullAsync(host, created.GetProperty("id").GetGuid())).AlertEmailOverride);

        var m = await AddAsync(host, Http(host.UserId));
        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{m.Id}", "{\"alertEmailOverride\":\"\"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null((await GetFullAsync(host, m.Id)).AlertEmailOverride);
    }

    [SkippableFact]
    public async Task Follow_redirects_defaults_to_true_round_trips_through_create_and_patch_and_the_column_defaults_to_true()
    {
        await using var host = await CreateHostAsync();

        // Existing rows get true from the migration's column default.
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var columnDefault = await db.Database
                .SqlQueryRaw<string>("select column_default as \"Value\" from information_schema.columns where table_name = 'Monitors' and column_name = 'FollowRedirects'")
                .SingleAsync();
            Assert.Equal("true", columnDefault);
        }

        var (status, created, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors", "{\"name\":\"a\",\"url\":\"https://example.com/\"}");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.True(created.GetProperty("followRedirects").GetBoolean());
        Assert.True((await GetFullAsync(host, created.GetProperty("id").GetGuid())).FollowRedirects);

        (status, created, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors",
            "{\"name\":\"b\",\"url\":\"https://example.com/\",\"followRedirects\":false,\"allowedStatusCodes\":\"301\"}");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.False(created.GetProperty("followRedirects").GetBoolean());
        var id = created.GetProperty("id").GetGuid();
        Assert.False((await GetFullAsync(host, id)).FollowRedirects);

        // Other patches keep it; an explicit patch changes it.
        (status, _, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"name\":\"renamed\"}");
        Assert.False((await GetFullAsync(host, id)).FollowRedirects);
        (status, var patched, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"followRedirects\":true}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(patched.GetProperty("followRedirects").GetBoolean());
        Assert.True((await GetFullAsync(host, id)).FollowRedirects);

        // Heartbeats store true and report no value.
        (_, var hb, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors", "{\"name\":\"hb\",\"type\":\"heartbeat\",\"intervalSeconds\":3600}");
        Assert.Equal(JsonValueKind.Null, hb.GetProperty("followRedirects").ValueKind);
        Assert.True((await GetFullAsync(host, hb.GetProperty("id").GetGuid())).FollowRedirects);
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

    private static MonitorEntity InGroup(string name, string? group, bool paused = false, bool enabled = true)
    {
        var m = Http();
        m.Name = name;
        m.GroupName = group;
        m.IsPaused = paused;
        m.Enabled = enabled;
        return m;
    }

    [SkippableFact]
    public async Task Create_and_patch_handle_the_group_with_merge_patch_semantics_AE3()
    {
        await using var host = await CreateHostAsync();
        await AddAsync(host, InGroup("existing", "Prod"));

        // Create takes the spelling of an existing group (case-insensitive) and trims.
        var (status, json, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors",
            "{\"name\":\"w\",\"url\":\"https://example.com/\",\"intervalSeconds\":60,\"group\":\"  prod \"}");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Prod", json.GetProperty("group").GetString());
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal("Prod", (await GetAsync(host, id)).GroupName);

        // Absent keeps it.
        (status, json, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"name\":\"renamed\"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Prod", json.GetProperty("group").GetString());

        // A new spelling of another existing group canonicalizes; an unknown name is stored as given.
        (_, json, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"group\":\"PROD\"}");
        Assert.Equal("Prod", json.GetProperty("group").GetString());
        (_, json, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"group\":\"Staging\"}");
        Assert.Equal("Staging", json.GetProperty("group").GetString());

        // null and blank clear it.
        (_, json, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"group\":null}");
        Assert.Equal(JsonValueKind.Null, json.GetProperty("group").ValueKind);
        Assert.Null((await GetAsync(host, id)).GroupName);
        await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"group\":\"Staging\"}");
        (_, json, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"group\":\"   \"}");
        Assert.Equal(JsonValueKind.Null, json.GetProperty("group").ValueKind);

        // Validation errors are keyed by the JSON field and change nothing.
        await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", "{\"group\":\"Staging\"}");
        foreach (var bad in new[] { "\"" + new string('g', 101) + "\"", "\"a\\u0000b\"", "5", "true" })
        {
            (status, json, _) = await SendFullAsync(host, HttpMethod.Patch, $"/api/v1/monitors/{id}", $"{{\"group\":{bad}}}");
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(json.GetProperty("errors").TryGetProperty("group", out _), bad);
        }

        (status, json, _) = await SendFullAsync(host, HttpMethod.Post, "/api/v1/monitors",
            "{\"name\":\"bad\",\"url\":\"https://example.com/\",\"intervalSeconds\":60,\"group\":\"" + new string('g', 101) + "\"}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(json.GetProperty("errors").TryGetProperty("group", out _));
        Assert.Equal("Staging", (await GetAsync(host, id)).GroupName);
    }

    [SkippableFact]
    public async Task Group_pause_and_resume_change_only_that_group_case_insensitively_and_renew_stamps()
    {
        await using var host = await CreateHostAsync();
        var a = await AddAsync(host, InGroup("a", "Prod"));
        var b = await AddAsync(host, InGroup("b", "prod"));
        var already = await AddAsync(host, InGroup("c", "Prod", paused: true));
        var disabled = await AddAsync(host, InGroup("d", "Prod", enabled: false));
        var other = await AddAsync(host, InGroup("e", "Staging"));
        var none = await AddAsync(host, InGroup("f", null));

        var (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, "/api/v1/groups/pause", "{\"group\":\"PROD\"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, json.GetProperty("changed").GetInt32());
        Assert.True((await GetAsync(host, a.Id)).IsPaused);
        Assert.NotEqual(a.ConcurrencyStamp, (await GetAsync(host, a.Id)).ConcurrencyStamp);
        Assert.True((await GetAsync(host, b.Id)).IsPaused);
        Assert.False((await GetAsync(host, disabled.Id)).IsPaused);
        Assert.False((await GetAsync(host, other.Id)).IsPaused);
        Assert.False((await GetAsync(host, none.Id)).IsPaused);

        (_, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, "/api/v1/groups/pause", "{\"group\":\"Prod\"}");
        Assert.Equal(0, json.GetProperty("changed").GetInt32());

        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, "/api/v1/groups/resume", "{\"group\":\"prod\"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, json.GetProperty("changed").GetInt32());
        Assert.False((await GetAsync(host, already.Id)).IsPaused);
        Assert.False((await GetAsync(host, a.Id)).IsPaused);
    }

    [SkippableFact]
    public async Task Group_rename_returns_the_count_merges_and_changes_casing_AE3()
    {
        await using var host = await CreateHostAsync();
        var a = await AddAsync(host, InGroup("a", "Old"));
        await AddAsync(host, InGroup("b", "old"));
        var target = await AddAsync(host, InGroup("t", "Target"));
        var none = await AddAsync(host, InGroup("n", null));

        var (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, "/api/v1/groups/rename", "{\"group\":\"OLD\",\"newName\":\"  Fresh \"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, json.GetProperty("renamed").GetInt32());
        Assert.Equal("Fresh", json.GetProperty("group").GetString());
        var renamed = await GetAsync(host, a.Id);
        Assert.Equal("Fresh", renamed.GroupName);
        Assert.NotEqual(a.ConcurrencyStamp, renamed.ConcurrencyStamp);

        // Merge into an existing group takes its spelling.
        (_, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, "/api/v1/groups/rename", "{\"group\":\"fresh\",\"newName\":\"TARGET\"}");
        Assert.Equal(2, json.GetProperty("renamed").GetInt32());
        Assert.Equal("Target", json.GetProperty("group").GetString());
        Assert.Equal("Target", (await GetAsync(host, a.Id)).GroupName);
        Assert.Equal(target.ConcurrencyStamp, (await GetAsync(host, target.Id)).ConcurrencyStamp);

        // Casing-only change of the same group stores the new spelling.
        (_, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, "/api/v1/groups/rename", "{\"group\":\"Target\",\"newName\":\"TARGET\"}");
        Assert.Equal(3, json.GetProperty("renamed").GetInt32());
        Assert.Equal("TARGET", json.GetProperty("group").GetString());
        Assert.Null((await GetAsync(host, none.Id)).GroupName);

        (status, json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, "/api/v1/groups/rename", "{\"group\":\"Target\",\"newName\":\"" + new string('x', 101) + "\"}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(json.GetProperty("errors").TryGetProperty("newName", out _));
        Assert.Equal("TARGET", (await GetAsync(host, a.Id)).GroupName);
    }

    [SkippableFact]
    public async Task Group_run_counts_queued_already_in_flight_and_skipped()
    {
        await using var host = await CreateHostAsync();
        var inFlight = await AddAsync(host, InGroup("running", "Prod"));
        await AddAsync(host, InGroup("fresh", "prod"));
        await AddAsync(host, InGroup("off", "Prod", enabled: false));
        var hb = Heartbeat(paused: false);
        hb.GroupName = "Prod";
        await AddAsync(host, hb);
        await AddAsync(host, InGroup("elsewhere", "Staging"));

        var (status, _) = await SendAsync(host, host.WriteKey, HttpMethod.Post, $"/api/v1/monitors/{inFlight.Id}/run");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Single(host.Jobs.Created);

        (status, var json) = await SendAsync(host, host.WriteKey, HttpMethod.Post, "/api/v1/groups/run", "{\"group\":\"PROD\"}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json.GetProperty("queued").GetInt32());
        Assert.Equal(1, json.GetProperty("alreadyInFlight").GetInt32());
        Assert.Equal(2, json.GetProperty("skipped").GetInt32());
        Assert.Equal(2, host.Jobs.Created.Count);
    }

    [SkippableFact]
    public async Task Groups_endpoint_counts_failing_by_last_run_including_overdue_monitors()
    {
        await using var host = await CreateHostAsync();
        var now = DateTimeOffset.UtcNow;
        var failing = InGroup("failing", "Prod");
        failing.LastRunAt = now.AddSeconds(-30);
        var overdue = InGroup("overdue", "prod");
        overdue.LastRunAt = now.AddDays(-1);
        var okPaused = InGroup("ok", "Prod", paused: true);
        okPaused.LastRunAt = now.AddSeconds(-30);
        foreach (var m in new[] { failing, overdue, okPaused, InGroup("alone", "alpha"), InGroup("none", null) })
            await AddAsync(host, m);
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var (m, ok, at) in new[] { (failing, false, now.AddSeconds(-30)), (overdue, false, now.AddDays(-1)), (okPaused, true, now.AddSeconds(-30)) })
                db.MonitorRuns.Add(new MonitorRun { MonitorId = m.Id, StartedAt = at, FinishedAt = at, State = "completed", Success = ok, StatusCode = ok ? 200 : 500, Reason = "schedule", RequestUrl = "https://example.com/", RequestMethod = "GET" });
            await db.SaveChangesAsync();
        }

        var (status, json) = await SendAsync(host, host.ReadKey, HttpMethod.Get, "/api/v1/groups");

        Assert.Equal(HttpStatusCode.OK, status);
        var groups = json.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(["alpha", "Prod"], groups.Select(g => g.GetProperty("name").GetString()).ToArray());
        var prod = groups[1];
        Assert.Equal(3, prod.GetProperty("monitorCount").GetInt32());
        Assert.Equal(2, prod.GetProperty("failingCount").GetInt32());
        Assert.Equal(1, prod.GetProperty("pausedCount").GetInt32());
    }
}
