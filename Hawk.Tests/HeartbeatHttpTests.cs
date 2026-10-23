// <file>
// <summary>
// Real Razor Pages POSTs (antiforgery, model binding, validation) for heartbeat monitors on an in-memory database.
// The Edit page uses ExecuteDelete/transactions, so its heartbeat behaviour is covered in PostgresIntegrationTests.
// </summary>
// </file>

using System.Net;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hawk.Tests;

public class HeartbeatHttpTests
{
    [Fact]
    public async Task Create_page_accepts_a_heartbeat_without_url_and_details_shows_the_ping_url()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();

        var before = DateTimeOffset.UtcNow;
        using var created = await PostAsync(app, "/Monitors/Create", "/Monitors/Create", new()
        {
            ["Form.Type"] = "Heartbeat",
            ["Form.Name"] = "Nightly backup",
            ["Form.Url"] = "",
            ["Form.IntervalSeconds"] = "3600",
            ["Form.GraceSeconds"] = "300",
            ["Form.AlertAfterConsecutiveFailures"] = "1",
            ["Form.Enabled"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var m = await LoadSingleAsync(app);
        Assert.Equal(MonitorType.Heartbeat, m.Type);
        Assert.Equal(string.Empty, m.Url);
        Assert.Matches("^[0-9a-f]{32}$", m.HeartbeatToken);
        Assert.Equal(3600, m.IntervalSeconds);
        Assert.Equal(300, m.HeartbeatGraceSeconds);
        Assert.True(m.NextRunAt >= before.AddSeconds(3900));
        Assert.Null(m.LastPingAt);

        using var details = await app.Client.GetAsync($"/Monitors/Details/{m.Id}");
        details.EnsureSuccessStatusCode();
        var html = await details.Content.ReadAsStringAsync();
        Assert.Contains($"/ping/{m.HeartbeatToken}", html);
        Assert.Contains("curl -fsS -m 10 --retry 3", html);
        Assert.DoesNotContain("Run now", html);
    }

    [Fact]
    public async Task Edit_index_and_run_diagnostics_pages_render_for_a_heartbeat()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using (await PostAsync(app, "/Monitors/Create", "/Monitors/Create", new()
        {
            ["Form.Type"] = "Heartbeat",
            ["Form.Name"] = "Rendered HB",
            ["Form.IntervalSeconds"] = "3600",
            ["Form.GraceSeconds"] = "300",
            ["Form.AlertAfterConsecutiveFailures"] = "1",
            ["Form.Enabled"] = "true",
        })) { }
        var m = await LoadSingleAsync(app);
        var runId = Guid.NewGuid();
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.MonitorRuns.Add(new MonitorRun { Id = runId, MonitorId = m.Id, StartedAt = now, FinishedAt = now, Success = true, Reason = "ping", RequestMethod = "GET", RequestUrl = "/ping/{token}" });
            await db.SaveChangesAsync();
        }

        var edit = await app.Client.GetStringAsync($"/Monitors/Edit/{m.Id}");
        Assert.Contains("Regenerate URL", edit);
        Assert.Contains("Expected every", edit);
        Assert.DoesNotContain("Form.Url", edit);
        Assert.DoesNotContain("Form.MatchPatterns", edit);

        var index = await app.Client.GetStringAsync("/Monitors");
        Assert.Contains("Rendered HB", index);
        Assert.Contains("Heartbeat", index);

        var run = await app.Client.GetStringAsync($"/Monitors/Runs/Details/{m.Id}/{runId}");
        Assert.DoesNotContain("Run again", run);
        Assert.Contains("/ping/{token}", run);
    }

    [Fact]
    public async Task Create_page_still_requires_a_url_for_http_monitors()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();

        using var response = await PostAsync(app, "/Monitors/Create", "/Monitors/Create", new()
        {
            ["Form.Type"] = "Http",
            ["Form.Name"] = "No url",
            ["Form.Url"] = "",
            ["Form.Method"] = "GET",
            ["Form.IntervalSeconds"] = "60",
            ["Form.TimeoutSeconds"] = "15",
            ["Form.AlertAfterConsecutiveFailures"] = "1",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("URL is required", await response.Content.ReadAsStringAsync());
        await using var scope = app.App.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.CountAsync());
    }

    private static async Task<HttpResponseMessage> PostAsync(SecurityTestApplication app, string formPage, string action, Dictionary<string, string> fields)
    {
        using var get = await app.Client.GetAsync(formPage);
        get.EnsureSuccessStatusCode();
        fields["__RequestVerificationToken"] = SecurityTestApplication.AntiforgeryToken(await get.Content.ReadAsStringAsync());
        return await app.Client.PostAsync(action, new FormUrlEncodedContent(fields));
    }

    private static async Task<Hawk.Web.Data.Monitoring.Monitor> LoadSingleAsync(SecurityTestApplication app)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking().SingleAsync();
    }
}
