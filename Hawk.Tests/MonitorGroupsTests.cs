// <file>
// <summary>
// Monitor groups: pure normalization/validation, the form hook, and group operations (canonicalization, rename, group run)
// on PostgreSQL. The set-based operations cannot run on the InMemory provider.
// </summary>
// </file>

using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Infrastructure;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monitor = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

public sealed class MonitorGroupsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Shop ", "Shop")]
    [InlineData("Shop", "Shop")]
    public void Normalize_trims_and_maps_blank_to_null(string? input, string? expected) =>
        Assert.Equal(expected, MonitorGroups.Normalize(input));

    [Fact]
    public void Validate_accepts_blank_and_100_characters_and_rejects_101_and_nul()
    {
        Assert.Null(MonitorGroups.Validate(null));
        Assert.Null(MonitorGroups.Validate("  "));
        Assert.Null(MonitorGroups.Validate(new string('a', 100)));
        Assert.Null(MonitorGroups.Validate("  " + new string('a', 100) + "  "));
        Assert.NotNull(MonitorGroups.Validate(new string('a', 101)));
        Assert.NotNull(MonitorGroups.Validate("a\0b"));
    }

    [Fact]
    public void Form_validation_reports_group_errors_under_the_Group_key()
    {
        var env = new TestEnv();
        var form = new MonitorForm { Name = "n", Url = "https://example.com/", Group = new string('a', 101) };
        Assert.Contains(form.Validate(env), r => r.MemberNames.Contains("Group"));
        form.Group = "a\0";
        Assert.Contains(form.Validate(env), r => r.MemberNames.Contains("Group"));
        form.Group = " Shop ";
        Assert.DoesNotContain(form.Validate(env), r => r.MemberNames.Contains("Group"));
    }

    [Theory]
    [InlineData("GroupPause")]
    [InlineData("GroupResume")]
    [InlineData("GroupRun")]
    [InlineData("GroupRename")]
    public async Task Index_group_handlers_reject_a_nul_group_before_any_query(string handler)
    {
        // Null services: any query or command would throw, so a flash error proves nothing ran.
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options;
        await using var db = new ApplicationDbContext(opts);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var page = new IndexModel(db, null!, null!, null!)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http },
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NullTempData()),
        };

        var result = handler switch
        {
            "GroupPause" => await page.OnPostGroupPauseAsync("a\0b", default),
            "GroupResume" => await page.OnPostGroupResumeAsync("a\0b", default),
            "GroupRun" => await page.OnPostGroupRunAsync("a\0b", default),
            _ => await page.OnPostGroupRenameAsync("a\0b", "x", default),
        };

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.Contains("NUL", (string)page.TempData["FlashError"]!);
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public void DisplayName_is_the_ordinal_first_spelling()
    {
        Assert.Null(MonitorGroups.DisplayName([]));
        Assert.Equal("SHOP", MonitorGroups.DisplayName(["shop", "SHOP", "Shop"]));
        Assert.Equal("only", MonitorGroups.DisplayName(["only"]));
    }

    [Fact]
    public async Task Canonicalize_adopts_existing_spelling_and_honours_the_exclusions()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options;
        await using var db = new ApplicationDbContext(opts);
        var a = new Monitor { Name = "a", Url = "https://a", Method = "GET", IntervalSeconds = 60, GroupName = "Shop" };
        var b = new Monitor { Name = "b", Url = "https://b", Method = "GET", IntervalSeconds = 60, GroupName = "shop" };
        var c = new Monitor { Name = "c", Url = "https://c", Method = "GET", IntervalSeconds = 60 };
        db.Monitors.AddRange(a, b, c);
        await db.SaveChangesAsync();

        Assert.Null(await MonitorGroups.CanonicalizeAsync(db, null));
        Assert.Equal("Shop", await MonitorGroups.CanonicalizeAsync(db, "SHOP", c.Id));
        Assert.Equal("New", await MonitorGroups.CanonicalizeAsync(db, "New"));
        // The only other member is excluded, so the given spelling wins.
        Assert.Equal("SHOP", await MonitorGroups.CanonicalizeAsync(db, "SHOP", a.Id, excludeGroup: "Shop"));
        // Excluding a monitor leaves the other spelling.
        Assert.Equal("shop", await MonitorGroups.CanonicalizeAsync(db, "SHOP", a.Id));
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Hawk";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

public sealed class MonitorGroupsPostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class RecordingJobs : IBackgroundJobClient
    {
        public int Created;
        public string Create(Job job, IState state) => $"job-{++Created}";
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private async Task<(ServiceProvider Provider, RecordingJobs Jobs)> CreateAsync()
    {
        if (fixture.UnavailableReason is not null && Environment.GetEnvironmentVariable("HAWK_REQUIRE_POSTGRES_TESTS") == "1")
            throw new InvalidOperationException($"HAWK_REQUIRE_POSTGRES_TESTS=1 but {fixture.UnavailableReason}");
        Skip.If(fixture.UnavailableReason is not null, fixture.UnavailableReason);

        var jobs = new RecordingJobs();
        var services = new ServiceCollection();
        services.AddHawkDbContext(DatabaseProvider.Postgres, fixture.NewDatabaseConnectionString());
        services.AddLogging();
        services.AddHawkIdentity();
        services.AddSingleton<IBackgroundJobClient>(jobs);
        services.AddScoped<MonitorEditor>();
        services.AddScoped<MonitorCommands>();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
        return (provider, jobs);
    }

    private static async Task<T> InScopeAsync<T>(ServiceProvider provider, Func<ApplicationDbContext, MonitorEditor, MonitorCommands, Task<T>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        return await action(sp.GetRequiredService<ApplicationDbContext>(), sp.GetRequiredService<MonitorEditor>(), sp.GetRequiredService<MonitorCommands>());
    }

    private static MonitorForm Form(string name, string? group, bool enabled = true) =>
        new() { Name = name, Url = "https://example.com/", IntervalSeconds = 60, Group = group, Enabled = enabled };

    private static Task<Monitor> AddAsync(ServiceProvider p, MonitorForm form) =>
        InScopeAsync(p, (_, editor, _) => editor.CreateAsync(form, null, CancellationToken.None));

    private static Task<Monitor> AddHeartbeatAsync(ServiceProvider p, string group) =>
        AddAsync(p, new MonitorForm { Name = "hb", Type = MonitorType.Heartbeat, IntervalSeconds = 3600, GraceSeconds = 300, Group = group });

    private static Task<Dictionary<string, (string? Group, Guid Stamp)>> LoadAsync(ServiceProvider p) =>
        InScopeAsync(p, async (db, _, _) => (await db.Monitors.AsNoTracking().ToListAsync())
            .ToDictionary(m => m.Name, m => (m.GroupName, m.ConcurrencyStamp)));

    [SkippableFact]
    public async Task Create_normalizes_blank_to_null_and_adopts_the_existing_spelling()
    {
        var (p, _) = await CreateAsync();
        await using var _ = p;
        await AddAsync(p, Form("a", "Shop"));
        await AddAsync(p, Form("b", "shop "));
        await AddAsync(p, Form("c", "   "));
        var all = await LoadAsync(p);
        Assert.Equal("Shop", all["a"].Group);
        Assert.Equal("Shop", all["b"].Group);
        Assert.Null(all["c"].Group);
    }

    [SkippableFact]
    public async Task Update_adopts_the_existing_group_and_the_only_member_may_change_its_casing()
    {
        var (p, _) = await CreateAsync();
        await using var _ = p;
        var a = await AddAsync(p, Form("a", "Shop"));
        var b = await AddAsync(p, Form("b", null));
        var solo = await AddAsync(p, Form("solo", "tools"));

        await InScopeAsync(p, (_, editor, _) => editor.UpdateAsync(b.Id, Form("b", "SHOP "), null, false, false, CancellationToken.None));
        await InScopeAsync(p, (_, editor, _) => editor.UpdateAsync(solo.Id, Form("solo", "Tools"), null, false, false, CancellationToken.None));
        var all = await LoadAsync(p);
        Assert.Equal("Shop", all["b"].Group);
        Assert.Equal("Tools", all["solo"].Group);

        await InScopeAsync(p, (_, editor, _) => editor.UpdateAsync(a.Id, Form("a", " "), null, false, false, CancellationToken.None));
        Assert.Null((await LoadAsync(p))["a"].Group);
    }

    [SkippableFact]
    public async Task Group_filter_matches_case_insensitively_on_postgres()
    {
        var (p, _) = await CreateAsync();
        await using var _ = p;
        await AddAsync(p, Form("a", "Shop"));
        await AddAsync(p, Form("b", "Other"));
        await AddAsync(p, Form("c", null));
        var names = await InScopeAsync(p, async (db, _, _) =>
            await MonitorGroups.InGroup(db.Monitors, "sHOP").Select(m => m.Name).ToListAsync());
        Assert.Equal(["a"], names);
    }

    [SkippableFact]
    public async Task Rename_to_the_identical_name_changes_nothing_and_keeps_stamps()
    {
        var (p, _) = await CreateAsync();
        await using var _ = p;
        await AddAsync(p, Form("a", "Shop"));
        await AddAsync(p, Form("b", "shop"));
        await AddAsync(p, Form("c", "Shop"));
        var before = await LoadAsync(p);

        var result = await InScopeAsync(p, (_, _, c) => c.RenameGroupAsync("Shop", " Shop ", CancellationToken.None));

        Assert.Null(result.Error);
        Assert.Equal(3, result.Renamed);
        Assert.Equal("Shop", result.NewName);
        var after = await LoadAsync(p);
        foreach (var name in new[] { "a", "b", "c" })
            Assert.Equal(before[name], after[name]);
    }

    [SkippableFact]
    public async Task Rename_moves_members_renews_stamps_and_leaves_other_groups_alone()
    {
        var (p, _) = await CreateAsync();
        await using var _ = p;
        await AddAsync(p, Form("a", "Shop"));
        await AddAsync(p, Form("b", "Shop"));
        await AddHeartbeatAsync(p, "Shop");
        await AddAsync(p, Form("other", "Blog"));
        var before = await LoadAsync(p);

        var result = await InScopeAsync(p, (_, _, c) => c.RenameGroupAsync("shop", " Store ", CancellationToken.None));
        Assert.Null(result.Error);
        Assert.Equal(3, result.Renamed);

        var after = await LoadAsync(p);
        foreach (var name in new[] { "a", "b", "hb" })
        {
            Assert.Equal("Store", after[name].Group);
            Assert.NotEqual(before[name].Stamp, after[name].Stamp);
        }
        Assert.Equal("Blog", after["other"].Group);
        Assert.Equal(before["other"].Stamp, after["other"].Stamp);
    }

    [SkippableFact]
    public async Task Rename_into_an_existing_group_with_other_casing_adopts_that_casing()
    {
        var (p, _) = await CreateAsync();
        await using var _ = p;
        await AddAsync(p, Form("a", "Shop"));
        await AddAsync(p, Form("b", "Store"));

        var result = await InScopeAsync(p, (_, _, c) => c.RenameGroupAsync("Shop", "STORE", CancellationToken.None));
        Assert.Equal(1, result.Renamed);
        Assert.Equal("Store", result.NewName);
        var all = await LoadAsync(p);
        Assert.Equal("Store", all["a"].Group);
        Assert.Equal("Store", all["b"].Group);
    }

    [SkippableFact]
    public async Task Rename_that_only_changes_casing_stores_the_new_spelling()
    {
        var (p, _) = await CreateAsync();
        await using var _ = p;
        await AddAsync(p, Form("a", "shop"));
        await AddAsync(p, Form("b", "shop"));

        var result = await InScopeAsync(p, (_, _, c) => c.RenameGroupAsync("shop", "Shop", CancellationToken.None));
        Assert.Equal(2, result.Renamed);
        var all = await LoadAsync(p);
        Assert.Equal("Shop", all["a"].Group);
        Assert.Equal("Shop", all["b"].Group);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tooLong")]
    [InlineData("nul")]
    public async Task Invalid_rename_is_rejected_and_changes_nothing(string target)
    {
        var (p, _) = await CreateAsync();
        await using var _ = p;
        await AddAsync(p, Form("a", "Shop"));
        var before = await LoadAsync(p);
        var to = target switch { "tooLong" => new string('x', 101), "nul" => "a\0b", _ => target };

        var result = await InScopeAsync(p, (_, _, c) => c.RenameGroupAsync("Shop", to, CancellationToken.None));
        Assert.NotNull(result.Error);
        Assert.Equal(0, result.Renamed);
        Assert.Equal(before["a"], (await LoadAsync(p))["a"]);
    }

    [SkippableFact]
    public async Task Group_run_queues_enabled_http_monitors_and_reports_skips_then_in_flight()
    {
        var (p, jobs) = await CreateAsync();
        await using var _ = p;
        await AddAsync(p, Form("a", "Shop"));
        await AddAsync(p, Form("b", "Shop"));
        await AddAsync(p, Form("off", "Shop", enabled: false));
        await AddHeartbeatAsync(p, "Shop");
        await AddAsync(p, Form("other", "Blog"));

        var first = await InScopeAsync(p, (_, _, c) => c.QueueGroupRunAsync("SHOP", CancellationToken.None));
        Assert.Equal(new GroupRunResult(Queued: 2, AlreadyInFlight: 0, Skipped: 2), first);
        Assert.Equal(2, jobs.Created);

        var second = await InScopeAsync(p, (_, _, c) => c.QueueGroupRunAsync("Shop", CancellationToken.None));
        Assert.Equal(new GroupRunResult(Queued: 0, AlreadyInFlight: 2, Skipped: 2), second);
        Assert.Equal(2, jobs.Created);
    }
}
