// <file>
// <summary>
// Unit tests for the v2 monitor export format and the bulk import planning/commit logic.
// </summary>
// </file>

using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services;
using Hawk.Web.Services.Import;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="MonitorJsonPort"/> v2 and <see cref="MonitorTransferService"/>.
/// </summary>
public sealed class MonitorTransferTests
{
    /// <summary>
    /// Entity to model to JSON to parse keeps every exported field.
    /// </summary>
    [Fact]
    public void V2_round_trip_preserves_every_field()
    {
        var source = SampleMonitor("Round Trip", "https://example.com/rt");
        source.Enabled = true;
        source.IsPaused = true;
        source.Method = "POST";
        source.ContentType = "application/json";
        source.Body = "{\"a\":1}";
        source.TimeoutSeconds = 25;
        source.IntervalSeconds = 300;
        source.AlertAfterConsecutiveFailures = 3;
        source.AlertEmailOverride = "alerts@example.com";
        source.AllowedStatusCodes = "404,429";
        source.RunRetentionDays = 45;

        var envelope = new MonitorExportEnvelope { Monitors = [MonitorJsonPort.ToModel(source, "owner@example.com")] };
        var json = JsonSerializer.Serialize(envelope, MonitorJsonPort.JsonOptions);

        Assert.True(MonitorJsonPort.TryParse(json, out var parsed, out var error), error);
        Assert.NotNull(parsed);
        Assert.Equal(MonitorJsonPort.CurrentVersion, parsed.Version);

        var m = Assert.Single(parsed.Monitors);
        Assert.Equal(source.Name, m.Name);
        Assert.Equal(source.Url, m.Url);
        Assert.Equal("POST", m.Method);
        Assert.True(m.Enabled);
        Assert.True(m.IsPaused);
        Assert.Equal("owner@example.com", m.OwnerEmail);
        Assert.Equal(25, m.TimeoutSeconds);
        Assert.Equal(300, m.IntervalSeconds);
        Assert.Equal(3, m.AlertAfterConsecutiveFailures);
        Assert.Equal("alerts@example.com", m.AlertEmailOverride);
        Assert.Equal("404,429", m.AllowedStatusCodes);
        Assert.Equal(45, m.RunRetentionDays);
        Assert.Equal("application/json", m.ContentType);
        Assert.Equal("{\"a\":1}", m.Body);
        Assert.Equal(
            source.Headers.Select(h => (h.Name, h.Value)).OrderBy(h => h.Name),
            m.Headers.Select(h => (h.Name, h.Value)).OrderBy(h => h.Name));
        Assert.Equal(
            source.MatchRules.Select(r => (r.Mode, r.Pattern)).OrderBy(r => r.Pattern),
            m.MatchRules.Select(r => (r.Mode, r.Pattern)).OrderBy(r => r.Pattern));

        // Ids, timestamps and run state must not leak into the file.
        Assert.DoesNotContain("rowVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nextRunAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\"", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plan then insert produces entities equal to the exported ones, and the import is a no-op the second time.
    /// </summary>
    [Fact]
    public async Task Import_round_trip_creates_equal_monitor_and_rerun_is_all_duplicates()
    {
        var (db, svc) = Create();
        var source = SampleMonitor("Imported", "https://example.com/imp");
        var json = JsonSerializer.Serialize(
            new MonitorExportEnvelope { Monitors = [MonitorJsonPort.ToModel(source)] },
            MonitorJsonPort.JsonOptions);

        var first = await svc.ImportAsync(json, "admin-1", createPaused: true, CancellationToken.None);
        Assert.Equal(1, first.Imported);

        var saved = await db.Monitors.Include(m => m.Headers).Include(m => m.MatchRules).SingleAsync();
        Assert.Equal("Imported", saved.Name);
        Assert.True(saved.Enabled);
        Assert.True(saved.IsPaused);
        Assert.Equal("admin-1", saved.CreatedByUserId);
        Assert.Equal(2, saved.Headers.Count);
        Assert.Equal(2, saved.MatchRules.Count);
        Assert.Contains(saved.MatchRules, r => r.Mode == ContentMatchMode.Regex && r.Pattern == "ok+");

        var second = await svc.ImportAsync(json, "admin-1", createPaused: true, CancellationToken.None);
        Assert.Equal(0, second.Imported);
        Assert.Equal(1, second.SkippedDuplicates);
        Assert.Equal(1, await db.Monitors.CountAsync());
    }

    /// <summary>
    /// A v1 file (no version-2 fields, no ownerEmail) still parses in all three shapes.
    /// </summary>
    [Fact]
    public void V1_files_parse_in_all_shapes()
    {
        const string monitor = """{ "name": "Old", "url": "https://example.com", "method": "GET", "headers": [{ "name": "X", "value": "1" }] }""";

        Assert.True(MonitorJsonPort.TryParse($$"""{ "version": 1, "exportedAtUtc": "2026-01-01T00:00:00Z", "monitors": [{{monitor}}] }""", out var env, out _));
        Assert.Equal(1, env!.Version);
        Assert.Null(env.Monitors[0].OwnerEmail);
        Assert.Single(env.Monitors[0].Headers);

        Assert.True(MonitorJsonPort.TryParse($"[{monitor}]", out env, out _));
        Assert.Equal("Old", Assert.Single(env!.Monitors).Name);

        Assert.True(MonitorJsonPort.TryParse(monitor, out env, out _));
        Assert.Equal("Old", Assert.Single(env!.Monitors).Name);
    }

    /// <summary>
    /// Future format versions are rejected rather than half-imported.
    /// </summary>
    [Fact]
    public void Unsupported_version_is_rejected()
    {
        Assert.False(MonitorJsonPort.TryParse("""{ "version": 99, "monitors": [] }""", out _, out var error));
        Assert.Contains("version", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Name and URL are compared case-insensitively, including duplicates within the file itself.
    /// </summary>
    [Fact]
    public async Task Duplicate_detection_is_case_insensitive()
    {
        var (db, svc) = Create();
        db.Monitors.Add(SampleMonitor("My Site", "https://Example.com/Health"));
        await db.SaveChangesAsync();

        var json = """
            [
              { "name": "MY SITE", "url": "https://example.com/health" },
              { "name": "Other", "url": "https://example.com/other" },
              { "name": "other", "url": "HTTPS://EXAMPLE.COM/OTHER" }
            ]
            """;

        var plan = await svc.PlanAsync(json, "admin-1", createPaused: true, CancellationToken.None);

        Assert.Equal(3, plan.Total);
        Assert.Equal(1, plan.ValidCount);
        Assert.Equal(2, plan.DuplicateCount);
        Assert.Equal(MonitorImportStatus.Duplicate, plan.Items[0].Status);
        Assert.Equal(MonitorImportStatus.Valid, plan.Items[1].Status);
        Assert.Equal(MonitorImportStatus.Duplicate, plan.Items[2].Status);
    }

    /// <summary>
    /// One invalid non-duplicate monitor makes the whole import refuse and write nothing.
    /// </summary>
    [Fact]
    public async Task Import_is_all_or_nothing_when_one_monitor_is_invalid()
    {
        var (db, svc) = Create();
        var json = """
            [
              { "name": "Good", "url": "https://example.com/good" },
              { "name": "Bad", "url": "not-a-url" }
            ]
            """;

        var result = await svc.ImportAsync(json, "admin-1", createPaused: true, CancellationToken.None);

        Assert.Equal(0, result.Imported);
        Assert.False(result.Plan.CanImport);
        Assert.Equal(1, result.Plan.InvalidCount);
        Assert.Equal(1, result.Plan.ValidCount);
        Assert.NotNull(result.Plan.Items[1].Error);
        Assert.Equal(0, await db.Monitors.CountAsync());
    }

    /// <summary>
    /// Owners are matched by email (case-insensitive); unmatched owners fall back to the importing admin.
    /// The paused option overrides or keeps the source pause state, and disabled stays disabled.
    /// </summary>
    [Fact]
    public async Task Owner_mapping_and_paused_option()
    {
        var (db, svc) = Create();
        db.Users.Add(new IdentityUser { Id = "u1", UserName = "bob@example.com", Email = "Bob@Example.com" });
        await db.SaveChangesAsync();

        var json = """
            [
              { "name": "A", "url": "https://example.com/a", "ownerEmail": "bob@example.COM", "isPaused": false },
              { "name": "B", "url": "https://example.com/b", "ownerEmail": "nobody@example.com", "isPaused": true },
              { "name": "C", "url": "https://example.com/c", "enabled": false }
            ]
            """;

        var plan = await svc.PlanAsync(json, "admin-1", createPaused: false, CancellationToken.None);
        Assert.Equal(1, plan.OwnersMatched);
        Assert.Equal(2, plan.OwnersAssignedToAdmin);
        Assert.Equal(2, plan.Items.Count(i => i.PausedOrDisabledAtSource));

        await svc.ImportAsync(json, "admin-1", createPaused: false, CancellationToken.None);
        var byName = await db.Monitors.ToDictionaryAsync(m => m.Name);
        Assert.Equal("u1", byName["A"].CreatedByUserId);
        Assert.Equal("admin-1", byName["B"].CreatedByUserId);
        Assert.False(byName["A"].IsPaused);
        Assert.True(byName["B"].IsPaused);
        Assert.False(byName["C"].Enabled);

        var (db2, svc2) = Create();
        await svc2.ImportAsync(json, "admin-1", createPaused: true, CancellationToken.None);
        Assert.All(await db2.Monitors.ToListAsync(), m => Assert.True(m.IsPaused));
    }

    /// <summary>
    /// Export-all includes every user's monitors ordered by name, with the owner's email.
    /// </summary>
    [Fact]
    public async Task ExportAll_orders_by_name_and_includes_owner_email()
    {
        var (db, svc) = Create();
        db.Users.Add(new IdentityUser { Id = "u1", UserName = "bob@example.com", Email = "bob@example.com" });
        var b = SampleMonitor("Beta", "https://example.com/b");
        b.CreatedByUserId = "u1";
        db.Monitors.AddRange(b, SampleMonitor("Alpha", "https://example.com/a"));
        await db.SaveChangesAsync();

        var json = await svc.ExportAllJsonAsync(CancellationToken.None);

        Assert.True(MonitorJsonPort.TryParse(json, out var env, out _));
        Assert.Equal(MonitorJsonPort.CurrentVersion, env!.Version);
        Assert.Equal(["Alpha", "Beta"], env.Monitors.Select(m => m.Name));
        Assert.Null(env.Monitors[0].OwnerEmail);
        Assert.Equal("bob@example.com", env.Monitors[1].OwnerEmail);
    }

    /// <summary>
    /// Field-level problems are reported as invalid in the preview and the confirm refuses (nothing written).
    /// </summary>
    [Theory]
    [InlineData("""{ "name": "", "url": "https://example.com/a" }""")]
    [InlineData("""{ "name": "T", "url": "https://example.com/a", "timeoutSeconds": 100000 }""")]
    [InlineData("""{ "name": "T", "url": "https://example.com/a", "headers": [{ "name": "X", "value": "__LONG__" }] }""")]
    public async Task Field_level_problems_are_invalid_and_confirm_refuses(string monitorJson)
    {
        var (db, svc) = Create();
        var json = "[" + monitorJson.Replace("__LONG__", new string('v', 2001)) + "]";

        var plan = await svc.PlanAsync(json, "admin-1", createPaused: true, CancellationToken.None);
        Assert.Equal(1, plan.InvalidCount);
        Assert.NotNull(plan.Items[0].Error);

        var result = await svc.ImportAsync(json, "admin-1", createPaused: true, CancellationToken.None);
        Assert.Equal(0, result.Imported);
        Assert.Equal(0, await db.Monitors.CountAsync());
    }

    /// <summary>
    /// Null monitor lists or entries yield a parse error rather than an exception.
    /// </summary>
    [Theory]
    [InlineData("""{ "monitors": null }""")]
    [InlineData("""{ "monitors": [null] }""")]
    [InlineData("""[null]""")]
    public async Task Null_monitors_give_parse_error(string json)
    {
        var (_, svc) = Create();
        var plan = await svc.PlanAsync(json, "admin-1", createPaused: true, CancellationToken.None);
        Assert.NotNull(plan.ParseError);
        Assert.False(plan.CanImport);
    }

    /// <summary>
    /// Null header/match-rule lists are treated as empty.
    /// </summary>
    [Fact]
    public void Null_child_lists_are_normalized()
    {
        Assert.True(MonitorJsonPort.TryParse("""[{ "name": "N", "url": "https://example.com", "headers": null, "matchRules": null }]""", out var env, out _));
        Assert.Empty(env!.Monitors[0].Headers);
        Assert.Empty(env.Monitors[0].MatchRules);
    }

    /// <summary>
    /// CRLF added by browsers in the hidden field does not push an accepted file over the size limit.
    /// </summary>
    [Fact]
    public async Task Size_check_ignores_crlf_expansion()
    {
        var (_, svc) = Create();
        var body = "[{\"name\":\"A\",\"url\":\"https://example.com/a\"}]";
        var padding = new string(' ', MonitorTransferService.MaxImportBytes - body.Length - 10);
        var lf = padding + "\n" + body;
        var crlf = lf.Replace("\n", "\r\n");

        var plan = await svc.PlanAsync(crlf, "admin-1", createPaused: true, CancellationToken.None);
        Assert.Null(plan.ParseError);
    }

    /// <summary>
    /// Concurrent confirms insert each monitor once.
    /// </summary>
    [Fact]
    public async Task Concurrent_imports_do_not_double_insert()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var json = """[{ "name": "Once", "url": "https://example.com/once" }]""";

        var tasks = Enumerable.Range(0, 4).Select(_ =>
        {
            var db = new ApplicationDbContext(opts);
            return new MonitorTransferService(db, new FakeEnv("Production")).ImportAsync(json, "a", true, CancellationToken.None);
        });
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Sum(r => r.Imported));
        await using var check = new ApplicationDbContext(opts);
        Assert.Equal(1, await check.Monitors.CountAsync());
    }

    /// <summary>
    /// Alert-state seeding counts only the supplied completed history (caller excludes the in-flight run).
    /// </summary>
    [Fact]
    public void Alert_seed_counts_only_given_history()
    {
        var t = DateTimeOffset.UtcNow;
        var empty = Hawk.Web.Services.Monitoring.MonitorAlertingDecider.SeedFromHistory(Guid.NewGuid(), []);
        Assert.Equal(0, empty.ConsecutiveFailures);
        Assert.Null(empty.FailureIncidentOpenedAt);

        var history = new List<Hawk.Web.Services.Monitoring.MonitorAlertingDecider.HistoricRun>
        {
            new(t.AddMinutes(-1), false, true),
            new(t.AddMinutes(-2), false, false),
            new(t.AddMinutes(-3), true, false),
            new(t.AddMinutes(-4), false, true),
        };
        var seeded = Hawk.Web.Services.Monitoring.MonitorAlertingDecider.SeedFromHistory(Guid.NewGuid(), history);
        Assert.Equal(2, seeded.ConsecutiveFailures);
        Assert.Equal(t.AddMinutes(-2), seeded.FailureIncidentOpenedAt);
        Assert.Equal(t.AddMinutes(-1), seeded.LastFailureAlertSentAt);
    }

    /// <summary>
    /// An unparsable regex is a per-item warning, not a refusal: the monitor is still imported.
    /// </summary>
    [Fact]
    public async Task Invalid_regex_is_warning_and_still_imports()
    {
        var (db, svc) = Create();
        var json = """[{ "name": "BadRe", "url": "https://example.com/a", "matchRules": [{ "mode": "regex", "pattern": "(unclosed" }] }]""";

        var plan = await svc.PlanAsync(json, "admin-1", createPaused: true, CancellationToken.None);
        Assert.Equal(0, plan.InvalidCount);
        Assert.Equal(1, plan.WarningCount);
        Assert.Contains("fail every run", plan.Items[0].Warnings![0]);

        var result = await svc.ImportAsync(json, "admin-1", createPaused: true, CancellationToken.None);
        Assert.Equal(1, result.Imported);
        Assert.Equal(1, await db.Monitors.CountAsync());
    }

    /// <summary>
    /// Create/Edit validation reports an invalid regex on the matching pattern field.
    /// </summary>
    [Fact]
    public void Form_validation_rejects_invalid_regex()
    {
        var form = new MonitorForm { Name = "N", Url = "https://example.com", Method = "GET", IntervalSeconds = 60 };
        form.MatchModes[1] = ContentMatchMode.Regex;
        form.MatchPatterns[1] = "(unclosed";
        form.MatchModes[2] = ContentMatchMode.Contains;
        form.MatchPatterns[2] = "(fine";

        var errors = form.Validate(new FakeEnv("Production")).ToList();
        var e = Assert.Single(errors);
        Assert.Contains("MatchPatterns[1]", e.MemberNames);

        var ms = new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();
        MonitorFormValidation.AddResults(ms, errors);
        Assert.True(ms.ContainsKey("Form.MatchPatterns[1]"));

        form.MatchPatterns[1] = "ok+";
        Assert.Empty(form.Validate(new FakeEnv("Production")));
    }

    private static MonitorEntity SampleMonitor(string name, string url)
    {
        var m = new MonitorEntity { Name = name, Url = url, Method = "GET" };
        m.Headers.Add(new MonitorHeader { Name = "X-Test", Value = "hawk" });
        m.Headers.Add(new MonitorHeader { Name = "X-Trace", Value = "rt" });
        m.MatchRules.Add(new MonitorMatchRule { Mode = ContentMatchMode.Contains, Pattern = "hello" });
        m.MatchRules.Add(new MonitorMatchRule { Mode = ContentMatchMode.Regex, Pattern = "ok+" });
        return m;
    }

    private static (ApplicationDbContext Db, MonitorTransferService Service) Create()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var db = new ApplicationDbContext(opts);
        return (db, new MonitorTransferService(db, new FakeEnv("Production")));
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
