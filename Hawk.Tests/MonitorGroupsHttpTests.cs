// <file>
// <summary>
// Group UI over real Razor Pages requests: Create/Edit group field, Details breadcrumb, Index sections and the group
// Pause/Resume/Run/Rename handlers. Set-based handlers run on PostgreSQL (the InMemory provider cannot ExecuteUpdate).
// </summary>
// </file>

using System.Net;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MonitorGroupsHttpTests(PostgresFixture fixture)
{
    private async Task<SecurityTestApplication> CreatePostgresAppAsync()
    {
        if (fixture.UnavailableReason is not null && Environment.GetEnvironmentVariable("HAWK_REQUIRE_POSTGRES_TESTS") == "1")
            throw new InvalidOperationException($"HAWK_REQUIRE_POSTGRES_TESTS=1 but {fixture.UnavailableReason}");
        Skip.If(fixture.UnavailableReason is not null, fixture.UnavailableReason);
        var app = await SecurityTestApplication.CreateAsync(fixture.NewDatabaseConnectionString());
        await app.LoginAsync();
        return app;
    }

    [Fact]
    public void Anchor_is_stable_case_insensitive_and_distinguishes_names_with_the_same_slug()
    {
        Assert.Equal("group-none", MonitorGroups.Anchor(null));
        Assert.Equal("group-none", MonitorGroups.Anchor("  "));
        Assert.Equal(MonitorGroups.Anchor("Shop Front"), MonitorGroups.Anchor("shop front"));
        Assert.Matches("^group-shop-front-[0-9a-f]{6}$", MonitorGroups.Anchor("Shop Front"));
        Assert.NotEqual(MonitorGroups.Anchor("a b"), MonitorGroups.Anchor("a-b"));
    }

    [Fact]
    public async Task Create_saves_the_group_and_details_shows_breadcrumb_and_chip()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();

        var form = await app.Client.GetStringAsync("/Monitors/Create");
        Assert.Contains("Form.Group", form);
        Assert.Contains("<datalist", form);

        using var created = await PostAsync(app, "/Monitors/Create", "/Monitors/Create", HttpFields("Shop", "web"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var m = await LoadSingleAsync(app);
        Assert.Equal("Shop", m.GroupName);

        var details = await app.Client.GetStringAsync($"/Monitors/Details/{m.Id}");
        Assert.Contains($"/monitors#{MonitorGroups.Anchor("Shop")}".ToLowerInvariant(), details.ToLowerInvariant());
        Assert.Contains("Shop", details);
    }

    [Fact]
    public async Task Create_rejects_a_group_over_100_characters_and_lists_existing_groups_in_the_datalist()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        await SeedAsync(app, Mon("a", "beta"), Mon("b", "Alpha"), Mon("c", "ALPHA"), Mon("d", null));

        var html = await app.Client.GetStringAsync("/Monitors/Create");
        var alpha = html.IndexOf("<option value=\"ALPHA\"", StringComparison.Ordinal);
        var beta = html.IndexOf("<option value=\"beta\"", StringComparison.Ordinal);
        Assert.True(alpha >= 0 && beta > alpha, "datalist sorted ignoring case");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<option value=\"alpha\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        using var response = await PostAsync(app, "/Monitors/Create", "/Monitors/Create", HttpFields(new string('x', 101), "toolong"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("at most 100", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_page_saves_a_group_for_a_heartbeat_too()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        using var created = await PostAsync(app, "/Monitors/Create", "/Monitors/Create", new()
        {
            ["Form.Type"] = "Heartbeat",
            ["Form.Name"] = "hb",
            ["Form.Group"] = "  Jobs ",
            ["Form.IntervalSeconds"] = "3600",
            ["Form.GraceSeconds"] = "300",
            ["Form.AlertAfterConsecutiveFailures"] = "1",
            ["Form.Enabled"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("Jobs", (await LoadSingleAsync(app)).GroupName);
    }

    [Fact]
    public async Task Index_renders_groups_sorted_with_ungrouped_last_and_failing_monitors_as_row_and_attention_card()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        var failing = Mon("FailingOne", "beta");
        failing.LastRunAt = DateTimeOffset.UtcNow;
        await SeedAsync(app, Mon("Plain", null), Mon("InAlpha", "alpha"), failing, Mon("BetaOk", "beta"));
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MonitorRuns.Add(new MonitorRun { MonitorId = failing.Id, StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow, Success = false, State = "completed" });
            await db.SaveChangesAsync();
        }

        var html = await app.Client.GetStringAsync("/Monitors");
        var alpha = html.IndexOf($"id=\"{MonitorGroups.Anchor("alpha")}\"", StringComparison.Ordinal);
        var beta = html.IndexOf($"id=\"{MonitorGroups.Anchor("beta")}\"", StringComparison.Ordinal);
        var none = html.IndexOf("id=\"group-none\"", StringComparison.Ordinal);
        Assert.True(alpha >= 0 && beta > alpha && none > beta, $"order alpha={alpha} beta={beta} none={none}");
        // Once as a row in its group, once as an Attention card.
        Assert.Equal(2, CountOf(html, ">FailingOne<"));
        Assert.Contains("data-testid=\"attention\"", html);
        Assert.Contains("Ungrouped", html);
        Assert.Contains("1 down", html);
        // Ungrouped has select-all only: no group action buttons for it.
        Assert.Equal(2, CountOf(html, "handler=GroupPause"));
    }

    [Fact]
    public async Task Index_batch_controls_live_in_the_batch_form_and_selection_buttons_post_BatchAction()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        await SeedAsync(app, Mon("one", "Shop"), Mon("two", null));

        var html = await app.Client.GetStringAsync("/Monitors");
        var formStart = html.IndexOf("id=\"batch-form\"", StringComparison.Ordinal);
        var formEnd = html.IndexOf("</form>", formStart, StringComparison.Ordinal);
        Assert.True(formStart >= 0 && formEnd > formStart);
        var form = html[formStart..formEnd];

        // Every monitor checkbox and the group select-all and group buttons are inside the batch form.
        Assert.Equal(CountOf(html, "<input class=\"hawk-check\" type=\"checkbox\" name=\"SelectedMonitorIds\""), CountOf(form, "<input class=\"hawk-check\" type=\"checkbox\" name=\"SelectedMonitorIds\""));
        Assert.Equal(2, CountOf(form, "<input class=\"hawk-check\" type=\"checkbox\" name=\"SelectedMonitorIds\""));
        Assert.Contains("data-group-select=", form);
        Assert.Contains("handler=GroupPause", form);

        // The selection bar sits outside the form and its buttons submit it via the form attribute with BatchAction.
        var bar = html[html.IndexOf("id=\"monitors-actions-selected\"", StringComparison.Ordinal)..];
        bar = bar[..bar.IndexOf("id=\"monitors-uncheck-all\"", StringComparison.Ordinal)];
        var buttons = System.Text.RegularExpressions.Regex.Matches(bar, "<button[^>]*>").Select(m => m.Value).ToList();
        Assert.True(buttons.Count == 3, string.Join("\n", buttons) + "\n---\n" + bar);
        Assert.All(buttons, b =>
        {
            Assert.Contains("form=\"batch-form\"", b);
            Assert.Contains("name=\"BatchAction\"", b);
            Assert.DoesNotContain("formaction", b);
        });
        Assert.Matches("<form[^>]*handler=Batch[^>]*id=\"batch-form\"|<form[^>]*id=\"batch-form\"[^>]*handler=Batch", html);
    }

    [Fact]
    public async Task Index_keeps_the_header_of_a_group_whose_members_all_fail()
    {
        await using var app = await SecurityTestApplication.CreateAsync();
        await app.LoginAsync();
        var only = Mon("Solo", "Down");
        only.LastRunAt = DateTimeOffset.UtcNow;
        await SeedAsync(app, only);
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MonitorRuns.Add(new MonitorRun { MonitorId = only.Id, StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow, Success = false, State = "completed" });
            await db.SaveChangesAsync();
        }

        var html = await app.Client.GetStringAsync("/Monitors");
        Assert.Contains($"id=\"{MonitorGroups.Anchor("Down")}\"", html);
        Assert.Contains("1 down", html);
    }

    [SkippableFact]
    public async Task Group_run_queues_http_runs_and_reports_skipped_heartbeats_and_disabled()
    {
        await using var app = await CreatePostgresAppAsync();
        await SeedAsync(app, Mon("one", "Shop"), Mon("two", "shop"), Mon("off", "Shop", enabled: false), Heartbeat("hb", "Shop"), Mon("other", "Else"));

        var flash = await PostGroupAsync(app, "GroupRun", "Shop");

        Assert.Contains("Queued 2 monitor run(s).", flash);
        Assert.Contains("Skipped 2", flash);
        Assert.Equal(2, app.Jobs.Created.Count);
    }

    [SkippableFact]
    public async Task Group_pause_and_resume_change_only_the_group_and_renew_stamps()
    {
        await using var app = await CreatePostgresAppAsync();
        var a = Mon("a", "Shop");
        var b = Mon("b", "SHOP");
        var c = Mon("c", "Else");
        await SeedAsync(app, a, b, c);

        var flash = await PostGroupAsync(app, "GroupPause", "shop");
        Assert.Contains("Paused 2 monitor(s)", flash);
        var all = await LoadAllAsync(app);
        Assert.True(all.Single(m => m.Name == "a").IsPaused);
        Assert.True(all.Single(m => m.Name == "b").IsPaused);
        Assert.False(all.Single(m => m.Name == "c").IsPaused);
        Assert.NotEqual(a.ConcurrencyStamp, all.Single(m => m.Name == "a").ConcurrencyStamp);
        Assert.Equal(c.ConcurrencyStamp, all.Single(m => m.Name == "c").ConcurrencyStamp);

        flash = await PostGroupAsync(app, "GroupResume", "Shop");
        Assert.Contains("Resumed 2 monitor(s)", flash);
        Assert.All((await LoadAllAsync(app)).Where(m => m.Name != "c"), m => Assert.False(m.IsPaused));
    }

    [SkippableFact]
    public async Task Group_rename_moves_members_merges_case_insensitively_and_rejects_blank()
    {
        await using var app = await CreatePostgresAppAsync();
        var a = Mon("a", "Old");
        await SeedAsync(app, a, Mon("b", "Old"), Mon("c", "Target"));

        var flash = await PostRenameAsync(app, "Old", "New");
        Assert.Contains("Renamed", flash);
        Assert.Equal(2, (await LoadAllAsync(app)).Count(m => m.GroupName == "New"));
        Assert.NotEqual(a.ConcurrencyStamp, (await LoadAllAsync(app)).Single(m => m.Name == "a").ConcurrencyStamp);

        flash = await PostRenameAsync(app, "New", "target");
        Assert.Contains("Renamed", flash);
        Assert.Equal(3, (await LoadAllAsync(app)).Count(m => m.GroupName == "Target"));

        flash = await PostRenameAsync(app, "Target", "   ");
        Assert.Contains("required", flash);
        Assert.Equal(3, (await LoadAllAsync(app)).Count(m => m.GroupName == "Target"));
    }

    [SkippableFact]
    public async Task Edit_shows_and_saves_the_group()
    {
        await using var app = await CreatePostgresAppAsync();
        var m = Mon("e", "Before");
        await SeedAsync(app, m);

        var html = await app.Client.GetStringAsync($"/Monitors/Edit/{m.Id}");
        Assert.Contains("value=\"Before\"", html);
        var stamp = System.Text.RegularExpressions.Regex.Match(html, "name=\"Form.ConcurrencyStamp\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var fields = HttpFields("After", "e");
        fields["Form.ConcurrencyStamp"] = stamp;
        using var saved = await PostAsync(app, $"/Monitors/Edit/{m.Id}", $"/Monitors/Edit/{m.Id}", fields);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("After", (await LoadAllAsync(app)).Single().GroupName);
    }

    [SkippableFact]
    public async Task Batch_handler_still_works_with_a_duplicated_monitor_id()
    {
        await using var app = await CreatePostgresAppAsync();
        var m = Mon("dup", "Shop");
        await SeedAsync(app, m);

        using var get = await app.Client.GetAsync("/Monitors");
        var token = SecurityTestApplication.AntiforgeryToken(await get.Content.ReadAsStringAsync());
        using var response = await app.Client.PostAsync("/Monitors?handler=Batch", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token), new("BatchAction", "pause"),
            new("SelectedMonitorIds", m.Id.ToString()), new("SelectedMonitorIds", m.Id.ToString()),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True((await LoadAllAsync(app)).Single().IsPaused);
    }

    private static int CountOf(string html, string needle) =>
        System.Text.RegularExpressions.Regex.Matches(html, System.Text.RegularExpressions.Regex.Escape(needle)).Count;

    private static MonitorEntity Mon(string name, string? group, bool enabled = true) => new()
    {
        Name = name, Url = "https://example.com/" + name, Method = "GET", IntervalSeconds = 60, GroupName = group, Enabled = enabled,
    };

    private static MonitorEntity Heartbeat(string name, string? group) => new()
    {
        Name = name, Type = MonitorType.Heartbeat, Url = "", Method = "GET", IntervalSeconds = 3600, HeartbeatGraceSeconds = 300,
        HeartbeatToken = HeartbeatTokens.Generate(), GroupName = group,
    };

    private static Dictionary<string, string> HttpFields(string group, string name) => new()
    {
        ["Form.Type"] = "Http",
        ["Form.Name"] = name,
        ["Form.Group"] = group,
        ["Form.Url"] = "https://example.com/x",
        ["Form.Method"] = "GET",
        ["Form.IntervalSeconds"] = "60",
        ["Form.TimeoutSeconds"] = "15",
        ["Form.AlertAfterConsecutiveFailures"] = "1",
        ["Form.Enabled"] = "true",
    };

    private static async Task SeedAsync(SecurityTestApplication app, params MonitorEntity[] monitors)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Monitors.AddRange(monitors);
        await db.SaveChangesAsync();
    }

    private static async Task<List<MonitorEntity>> LoadAllAsync(SecurityTestApplication app)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Monitors.AsNoTracking().ToListAsync();
    }

    private static async Task<MonitorEntity> LoadSingleAsync(SecurityTestApplication app) => (await LoadAllAsync(app)).Single();

    private static async Task<HttpResponseMessage> PostAsync(SecurityTestApplication app, string formPage, string action, Dictionary<string, string> fields)
    {
        using var get = await app.Client.GetAsync(formPage);
        get.EnsureSuccessStatusCode();
        fields["__RequestVerificationToken"] = SecurityTestApplication.AntiforgeryToken(await get.Content.ReadAsStringAsync());
        return await app.Client.PostAsync(action, new FormUrlEncodedContent(fields));
    }

    /// <summary>Posts a group handler from the index page, follows the redirect and returns the page text (flash included).</summary>
    private static async Task<string> PostGroupAsync(SecurityTestApplication app, string handler, string group)
    {
        using var response = await PostAsync(app, "/Monitors", $"/Monitors?handler={handler}", new() { ["Group"] = group });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return WebUtility.HtmlDecode(await app.Client.GetStringAsync("/Monitors"));
    }

    private static async Task<string> PostRenameAsync(SecurityTestApplication app, string group, string newName)
    {
        using var response = await PostAsync(app, "/Monitors", "/Monitors?handler=GroupRename", new() { ["Group"] = group, ["NewGroupName"] = newName });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return WebUtility.HtmlDecode(await app.Client.GetStringAsync("/Monitors"));
    }
}
