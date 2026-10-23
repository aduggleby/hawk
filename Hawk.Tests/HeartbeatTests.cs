// <file>
// <summary>
// Pure-logic tests for heartbeat monitors: tokens, form validation, JSON export v3, import identity, decider and log redaction.
// Anything needing ExecuteUpdate/transactions/unique indexes lives in PostgresIntegrationTests.
// </summary>
// </file>

using System.Text.Json;
using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Infrastructure;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services.Import;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

public sealed class HeartbeatTests
{
    private static readonly IHostEnvironment Production = new FakeEnv("Production");
    private static readonly IHostEnvironment Testing = new FakeEnv("Testing");

    // ---- tokens ----

    [Fact]
    public void Generated_tokens_are_32_lowercase_hex_and_unique()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => HeartbeatTokens.Generate()).ToList();
        Assert.All(tokens, t =>
        {
            Assert.Equal(32, t.Length);
            Assert.Matches("^[0-9a-f]{32}$", t);
            Assert.True(HeartbeatTokens.TryNormalize(t, out var normalized));
            Assert.Equal(t, normalized);
        });
        Assert.Equal(50, tokens.Distinct().Count());
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789abcdef", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef0", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("0123456789abcdef0123456789abcde%", false)]
    [InlineData(" 123456789abcdef0123456789abcdef", false)]
    public void TryNormalize_accepts_only_32_hex_characters_and_lowercases(string? raw, bool expected)
    {
        Assert.Equal(expected, HeartbeatTokens.TryNormalize(raw, out var token));
        if (expected)
            Assert.Equal(raw!.ToLowerInvariant(), token);
    }

    [Fact]
    public void Initial_deadline_is_period_plus_grace()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddSeconds(3900), HeartbeatTokens.InitialDeadline(now, 3600, 300));
    }

    [Fact]
    public void Period_and_grace_lists_match_the_spec_and_testing_adds_short_values()
    {
        Assert.Equal([60, 300, 900, 3600, 21600, 43200, 86400, 604800], MonitorIntervals.AllowedHeartbeatPeriods(Production));
        Assert.Equal([60, 300, 900, 3600, 21600, 86400], MonitorIntervals.AllowedHeartbeatGraces(Production));
        Assert.Equal(5, MonitorIntervals.AllowedHeartbeatPeriods(Testing)[0]);
        Assert.Equal([0, 5], MonitorIntervals.AllowedHeartbeatGraces(Testing).Take(2));
    }

    // ---- form validation ----

    [Fact]
    public void Http_monitor_with_empty_url_is_rejected()
    {
        var form = new MonitorForm { Name = "x", Url = "", Method = "GET", IntervalSeconds = 60 };
        var error = Assert.Single(form.Validate(Production));
        Assert.Contains(nameof(MonitorForm.Url), error.MemberNames);

        form.Url = null;
        Assert.Contains(form.Validate(Production), e => e.MemberNames.Contains(nameof(MonitorForm.Url)));
    }

    [Fact]
    public void Heartbeat_needs_no_url_or_http_fields_but_checks_period_and_grace()
    {
        var form = new MonitorForm { Name = "Backup", Type = MonitorType.Heartbeat, Url = null, Method = "POST", IntervalSeconds = 3600, GraceSeconds = 300, ContentType = null, Body = null };
        Assert.Empty(form.Validate(Production));

        form.IntervalSeconds = 120;
        Assert.Contains(form.Validate(Production), e => e.MemberNames.Contains(nameof(MonitorForm.IntervalSeconds)));

        form.IntervalSeconds = 5;
        Assert.NotEmpty(form.Validate(Production));
        Assert.Empty(form.Validate(Testing));

        form.GraceSeconds = 0;
        Assert.Contains(form.Validate(Production), e => e.MemberNames.Contains(nameof(MonitorForm.GraceSeconds)));
        Assert.Empty(form.Validate(Testing));
    }

    // ---- decider ----

    [Fact]
    public void Recovery_is_sent_after_the_threshold_was_raised_during_an_alerted_incident()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var state = new MonitorAlertState { MonitorId = Guid.NewGuid() };
        Assert.Equal(MonitorAlertKind.Failure, MonitorAlertingDecider.OnFailure(state, 1, now, TimeSpan.FromHours(24)).Kind);
        state.LastFailureAlertSentAt = now;

        // The user raises the threshold from 1 to 3 while the incident is open and alerted.
        var decision = MonitorAlertingDecider.OnSuccess(state, threshold: 3, now.AddMinutes(5));

        Assert.Equal(MonitorAlertKind.Recovered, decision.Kind);
    }

    [Fact]
    public void Success_without_an_alerted_incident_sends_no_recovery()
    {
        var state = new MonitorAlertState { MonitorId = Guid.NewGuid(), ConsecutiveFailures = 2 };
        Assert.Equal(MonitorAlertKind.None, MonitorAlertingDecider.OnSuccess(state, 3, DateTimeOffset.UtcNow).Kind);
    }

    // ---- JSON export v3 ----

    [Fact]
    public void V3_round_trip_keeps_heartbeat_fields_and_v2_files_still_parse()
    {
        var source = new MonitorEntity
        {
            Name = "Nightly backup",
            Type = MonitorType.Heartbeat,
            Url = "",
            Method = "GET",
            IntervalSeconds = 86400,
            HeartbeatGraceSeconds = 3600,
            HeartbeatToken = HeartbeatTokens.Generate(),
        };
        var json = JsonSerializer.Serialize(new MonitorExportEnvelope { Monitors = [MonitorJsonPort.ToModel(source)] }, MonitorJsonPort.JsonOptions);

        Assert.Contains("\"heartbeat\"", json);
        Assert.True(MonitorJsonPort.TryParse(json, out var parsed, out var error), error);
        Assert.Equal(3, parsed!.Version);
        var m = Assert.Single(parsed.Monitors);
        Assert.Equal(MonitorType.Heartbeat, m.Type);
        Assert.Equal(86400, m.IntervalSeconds);
        Assert.Equal(3600, m.HeartbeatGraceSeconds);
        Assert.Equal(source.HeartbeatToken, m.HeartbeatToken);

        Assert.True(MonitorJsonPort.TryParse("""{ "version": 2, "monitors": [{ "name": "Old", "url": "https://example.com" }] }""", out var v2, out _));
        Assert.Equal(MonitorType.Http, v2!.Monitors[0].Type);
        Assert.False(MonitorJsonPort.TryParse("""{ "version": 4, "monitors": [] }""", out _, out _));
    }

    [Fact]
    public void Creating_a_heartbeat_from_the_model_keeps_a_valid_token_issues_one_otherwise_and_sets_the_deadline()
    {
        var token = HeartbeatTokens.Generate();
        var model = new MonitorExportModel { Name = "HB", Type = MonitorType.Heartbeat, IntervalSeconds = 3600, HeartbeatGraceSeconds = 300, HeartbeatToken = token.ToUpperInvariant() };

        Assert.True(MonitorJsonPort.TryCreateMonitor(model, "u1", Production, out var kept, out var error, forcePaused: true), error);
        Assert.Equal(token, kept.HeartbeatToken);
        Assert.True(kept.IsPaused);
        Assert.Equal(string.Empty, kept.Url);
        Assert.NotNull(kept.NextRunAt);
        Assert.True(kept.NextRunAt > DateTimeOffset.UtcNow.AddSeconds(3800));

        Assert.True(MonitorJsonPort.TryCreateMonitor(model with { HeartbeatToken = "nonsense" }, "u1", Production, out var fresh, out _));
        Assert.True(HeartbeatTokens.TryNormalize(fresh.HeartbeatToken, out _));
        Assert.NotEqual(token, fresh.HeartbeatToken);

        Assert.False(MonitorJsonPort.TryCreateMonitor(model with { IntervalSeconds = 61 }, "u1", Production, out _, out _));
    }

    [Fact]
    public async Task Undefined_monitor_types_are_rejected_by_validation_and_bulk_import()
    {
        var form = new MonitorForm { Name = "x", Type = (MonitorType)2, Url = "https://example.com", IntervalSeconds = 60 };
        Assert.Contains(form.Validate(Production), e => e.MemberNames.Contains(nameof(MonitorForm.Type)));

        var (db, svc) = Create();
        var json = """[{ "name": "x", "type": 2, "url": "https://example.com", "intervalSeconds": 60 }]""";
        var plan = await svc.PlanAsync(json, "admin", createPaused: false, CancellationToken.None);
        Assert.Equal(1, plan.InvalidCount);
        Assert.Equal(0, (await svc.ImportAsync(json, "admin", false, CancellationToken.None)).Imported);
        Assert.Equal(0, await db.Monitors.CountAsync());
    }

    // ---- bulk import identity ----

    [Fact]
    public async Task Same_name_heartbeats_with_different_tokens_both_import_and_a_reimport_is_skipped()
    {
        var (db, svc) = Create();
        var json = Json(Heartbeat("Daily backup", HeartbeatTokens.Generate()), Heartbeat("Daily backup", HeartbeatTokens.Generate()));

        var first = await svc.ImportAsync(json, "admin", createPaused: true, CancellationToken.None);
        Assert.Equal(2, first.Imported);
        Assert.Equal(2, await db.Monitors.CountAsync(m => m.Type == MonitorType.Heartbeat));
        Assert.All(await db.Monitors.ToListAsync(), m => Assert.Null(m.LastPingAt));

        var second = await svc.ImportAsync(json, "admin", createPaused: true, CancellationToken.None);
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.SkippedDuplicates);
        Assert.Equal(2, await db.Monitors.CountAsync());
    }

    [Fact]
    public async Task Heartbeat_without_a_token_dedupes_by_name_and_gets_a_new_token()
    {
        var (db, svc) = Create();
        var json = Json(Heartbeat("Cron A", null), Heartbeat("Cron A", null), Heartbeat("Cron B", null));

        var result = await svc.ImportAsync(json, "admin", createPaused: true, CancellationToken.None);

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.SkippedDuplicates);
        var tokens = await db.Monitors.Select(m => m.HeartbeatToken).ToListAsync();
        Assert.All(tokens, t => Assert.True(HeartbeatTokens.TryNormalize(t, out _)));
        Assert.Equal(2, tokens.Distinct().Count());

        var again = await svc.ImportAsync(json, "admin", createPaused: true, CancellationToken.None);
        Assert.Equal(0, again.Imported);
    }

    [Fact]
    public async Task An_existing_token_is_the_same_monitor_and_is_skipped()
    {
        var (db, svc) = Create();
        var token = HeartbeatTokens.Generate();
        db.Monitors.Add(new MonitorEntity { Name = "Renamed locally", Type = MonitorType.Heartbeat, Url = "", Method = "GET", IntervalSeconds = 3600, HeartbeatToken = token });
        await db.SaveChangesAsync();

        var result = await svc.ImportAsync(Json(Heartbeat("Original name", token)), "admin", createPaused: true, CancellationToken.None);

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.SkippedDuplicates);
    }

    [Fact]
    public async Task Export_includes_the_heartbeat_token()
    {
        var (db, svc) = Create();
        var token = HeartbeatTokens.Generate();
        db.Monitors.Add(new MonitorEntity { Name = "HB", Type = MonitorType.Heartbeat, Url = "", Method = "GET", IntervalSeconds = 3600, HeartbeatGraceSeconds = 300, HeartbeatToken = token });
        await db.SaveChangesAsync();

        var json = await svc.ExportAllJsonAsync(CancellationToken.None);

        Assert.True(MonitorJsonPort.TryParse(json, out var env, out _));
        Assert.Equal(token, Assert.Single(env!.Monitors).HeartbeatToken);
    }

    // ---- log redaction ----

    [Theory]
    [InlineData("/ping/0123456789abcdef0123456789abcdef", "/ping/{redacted}")]
    [InlineData("/PING/anything", "/ping/{redacted}")]
    [InlineData("/Monitors/Index", "/Monitors/Index")]
    [InlineData("/pingpong", "/pingpong")]
    public void Ping_paths_are_redacted_for_request_logging(string path, string expected)
    {
        Assert.Equal(expected, HeartbeatEndpoints.RedactPath(path));
    }

    // ---- helpers ----

    private static string Json(params MonitorExportModel[] models) =>
        JsonSerializer.Serialize(new MonitorExportEnvelope { Monitors = [.. models] }, MonitorJsonPort.JsonOptions);

    private static MonitorExportModel Heartbeat(string name, string? token) => new()
    {
        Name = name,
        Type = MonitorType.Heartbeat,
        IntervalSeconds = 3600,
        HeartbeatGraceSeconds = 300,
        HeartbeatToken = token,
    };

    private static (ApplicationDbContext Db, MonitorTransferService Service) Create()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new ApplicationDbContext(options);
        return (db, new MonitorTransferService(db, Production));
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
