// <file>
// <summary>
// MonitorEditor on PostgreSQL: create, update (stamp conflicts, child replacement flags, heartbeat deadline rules) and delete (cascade).
// The editor uses set-based ExecuteDelete, so the InMemory provider cannot run it.
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Infrastructure;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monitor = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

public sealed class MonitorEditorTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<ServiceProvider> CreateAsync()
    {
        if (fixture.UnavailableReason is not null && Environment.GetEnvironmentVariable("HAWK_REQUIRE_POSTGRES_TESTS") == "1")
            throw new InvalidOperationException($"HAWK_REQUIRE_POSTGRES_TESTS=1 but {fixture.UnavailableReason}");
        Skip.If(fixture.UnavailableReason is not null, fixture.UnavailableReason);

        var services = new ServiceCollection();
        services.AddHawkDbContext(DatabaseProvider.Postgres, fixture.NewDatabaseConnectionString());
        services.AddLogging();
        services.AddHawkIdentity();
        services.AddScoped<MonitorEditor>();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
        return provider;
    }

    private static async Task<T> InScopeAsync<T>(ServiceProvider provider, Func<MonitorEditor, ApplicationDbContext, Task<T>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<MonitorEditor>(), scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static MonitorForm HttpForm(string name = "web", int headers = 0)
    {
        var form = new MonitorForm { Name = name, Url = "https://example.com/", IntervalSeconds = 60 };
        form.HeaderNames = Enumerable.Range(0, headers).Select(i => $"X-H{i}").ToArray();
        form.HeaderValues = Enumerable.Range(0, headers).Select(i => $"v{i}").ToArray();
        return form;
    }

    private static MonitorForm HeartbeatForm(int period = 3600, int grace = 300) =>
        new() { Name = "hb", Type = MonitorType.Heartbeat, IntervalSeconds = period, GraceSeconds = grace };

    private static Task<Monitor?> LoadAsync(ServiceProvider provider, Guid id) =>
        InScopeAsync(provider, async (_, db) => await db.Monitors.AsNoTracking()
            .Include(m => m.Headers).Include(m => m.MatchRules).FirstOrDefaultAsync(m => m.Id == id));

    [SkippableFact]
    public async Task Create_maps_an_http_form_and_a_heartbeat_form()
    {
        await using var provider = await CreateAsync();
        var form = HttpForm(" web ", headers: 2);
        form.Method = "get";
        form.MatchModes = [ContentMatchMode.Regex, ContentMatchMode.None];
        form.MatchPatterns = ["ok+", "ignored"];
        var http = await InScopeAsync(provider, (editor, _) => editor.CreateAsync(form, "user-1", CancellationToken.None));
        var saved = (await LoadAsync(provider, http.Id))!;
        Assert.Equal("web", saved.Name);
        Assert.Equal("GET", saved.Method);
        Assert.Equal("user-1", saved.CreatedByUserId);
        Assert.Equal(2, saved.Headers.Count);
        Assert.Equal("ok+", Assert.Single(saved.MatchRules).Pattern);
        Assert.Null(saved.HeartbeatToken);

        var before = DateTimeOffset.UtcNow;
        var hb = await InScopeAsync(provider, (editor, _) => editor.CreateAsync(HeartbeatForm(), "user-1", CancellationToken.None));
        var savedHb = (await LoadAsync(provider, hb.Id))!;
        Assert.Matches("^[0-9a-f]{32}$", savedHb.HeartbeatToken);
        Assert.Equal("", savedHb.Url);
        Assert.True(savedHb.NextRunAt >= before.AddSeconds(3900));
        Assert.Empty(savedHb.Headers);
    }

    [SkippableFact]
    public async Task Update_with_a_stale_stamp_conflicts_and_changes_nothing_but_without_a_stamp_the_last_write_wins()
    {
        await using var provider = await CreateAsync();
        var m = await InScopeAsync(provider, (editor, _) => editor.CreateAsync(HttpForm(), null, CancellationToken.None));
        var stale = m.ConcurrencyStamp;

        var form = HttpForm("renamed");
        Assert.Equal(MonitorUpdateOutcome.Updated, await InScopeAsync(provider, (e, _) => e.UpdateAsync(m.Id, form, stale, true, true, CancellationToken.None)));
        var afterFirst = (await LoadAsync(provider, m.Id))!;
        Assert.NotEqual(stale, afterFirst.ConcurrencyStamp);

        var second = HttpForm("stale write");
        Assert.Equal(MonitorUpdateOutcome.Conflict, await InScopeAsync(provider, (e, _) => e.UpdateAsync(m.Id, second, stale, true, true, CancellationToken.None)));
        Assert.Equal("renamed", (await LoadAsync(provider, m.Id))!.Name);

        Assert.Equal(MonitorUpdateOutcome.Updated, await InScopeAsync(provider, (e, _) => e.UpdateAsync(m.Id, second, null, true, true, CancellationToken.None)));
        Assert.Equal("stale write", (await LoadAsync(provider, m.Id))!.Name);

        Assert.Equal(MonitorUpdateOutcome.NotFound, await InScopeAsync(provider, (e, _) => e.UpdateAsync(Guid.NewGuid(), second, null, true, true, CancellationToken.None)));
    }

    [SkippableFact]
    public async Task Update_replaces_children_only_when_asked_so_more_than_five_headers_survive_a_partial_update()
    {
        await using var provider = await CreateAsync();
        var m = await InScopeAsync(provider, (editor, _) => editor.CreateAsync(HttpForm(headers: 7), null, CancellationToken.None));

        var rename = HttpForm("renamed"); // carries no headers
        await InScopeAsync(provider, (e, _) => e.UpdateAsync(m.Id, rename, null, replaceHeaders: false, replaceMatchRules: false, CancellationToken.None));
        Assert.Equal(7, (await LoadAsync(provider, m.Id))!.Headers.Count);

        await InScopeAsync(provider, (e, _) => e.UpdateAsync(m.Id, rename, null, replaceHeaders: true, replaceMatchRules: false, CancellationToken.None));
        Assert.Empty((await LoadAsync(provider, m.Id))!.Headers);
    }

    [SkippableFact]
    public async Task Update_moves_a_heartbeat_deadline_only_for_period_grace_or_re_enable()
    {
        await using var provider = await CreateAsync();
        var m = await InScopeAsync(provider, (editor, _) => editor.CreateAsync(HeartbeatForm(), null, CancellationToken.None));
        var deadline = (await LoadAsync(provider, m.Id))!.NextRunAt; // as stored (microsecond precision)
        var token = m.HeartbeatToken;

        var rename = HeartbeatForm();
        rename.Name = "renamed";
        await InScopeAsync(provider, (e, _) => e.UpdateAsync(m.Id, rename, null, false, false, CancellationToken.None));
        var renamed = (await LoadAsync(provider, m.Id))!;
        Assert.Equal(deadline, renamed.NextRunAt);
        Assert.Equal(token, renamed.HeartbeatToken);
        Assert.Equal(MonitorType.Heartbeat, renamed.Type);

        var longer = HeartbeatForm(period: 86400);
        await InScopeAsync(provider, (e, _) => e.UpdateAsync(m.Id, longer, null, false, false, CancellationToken.None));
        Assert.True((await LoadAsync(provider, m.Id))!.NextRunAt > deadline!.Value.AddHours(20));
    }

    [SkippableFact]
    public async Task Delete_cascades_honours_the_stamp_and_reports_missing_monitors()
    {
        await using var provider = await CreateAsync();
        var m = await InScopeAsync(provider, (editor, _) => editor.CreateAsync(HttpForm(headers: 1), null, CancellationToken.None));
        await InScopeAsync(provider, async (_, db) =>
        {
            db.MonitorRuns.Add(new MonitorRun { MonitorId = m.Id, StartedAt = DateTimeOffset.UtcNow, State = "completed" });
            db.MonitorAlertStates.Add(new Hawk.Web.Data.Alerting.MonitorAlertState { MonitorId = m.Id });
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(MonitorDeleteOutcome.Conflict, await InScopeAsync(provider, (e, _) => e.DeleteAsync(m.Id, Guid.NewGuid(), CancellationToken.None)));
        Assert.NotNull(await LoadAsync(provider, m.Id));

        Assert.Equal(MonitorDeleteOutcome.Deleted, await InScopeAsync(provider, (e, _) => e.DeleteAsync(m.Id, m.ConcurrencyStamp, CancellationToken.None)));
        await InScopeAsync(provider, async (_, db) =>
        {
            Assert.False(await db.Monitors.AnyAsync(x => x.Id == m.Id));
            Assert.False(await db.MonitorRuns.AnyAsync(r => r.MonitorId == m.Id));
            Assert.False(await db.MonitorHeaders.AnyAsync(h => h.MonitorId == m.Id));
            Assert.False(await db.MonitorAlertStates.AnyAsync(s => s.MonitorId == m.Id));
            return 0;
        });

        Assert.Equal(MonitorDeleteOutcome.NotFound, await InScopeAsync(provider, (e, _) => e.DeleteAsync(m.Id, null, CancellationToken.None)));
        Assert.Equal(MonitorDeleteOutcome.NotFound, await InScopeAsync(provider, (e, _) => e.DeleteAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)));
    }
}
