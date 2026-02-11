// <file>
// <summary>
// StatusCake import helpers.
//
// Supports importing:
// - Uptime tests (as Hawk monitors)
// - Uptime alerts (as Hawk monitor run history)
//
// The importer intentionally accepts exported JSON payloads (not direct API calls) so that:
// - No secrets need to be stored in Hawk
// - E2E tests can remain deterministic without external network dependencies
// </summary>
// </file>

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Import;

/// <summary>
/// Summary of an import operation.
/// </summary>
/// <param name="MonitorsCreated">Number of monitors created.</param>
/// <param name="RunsCreated">Number of monitor runs created.</param>
/// <param name="Warnings">Warnings encountered during import.</param>
public sealed record StatusCakeImportResult(int MonitorsCreated, int RunsCreated, IReadOnlyList<string> Warnings);

/// <summary>
/// Imports StatusCake exports into Hawk.
/// </summary>
public sealed class StatusCakeImporter(ApplicationDbContext db, IHostEnvironment env, ILogger<StatusCakeImporter> logger)
{
    /// <summary>
    /// Imports uptime tests from a StatusCake JSON export.
    /// </summary>
    /// <param name="jsonStream">Stream containing either the raw API response or an array of test objects.</param>
    /// <param name="createdByUserId">Identity user id of the importing user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<StatusCakeImportResult> ImportTestsAsync(Stream jsonStream, string? createdByUserId, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);

        var tests = ExtractArray(doc.RootElement, warnings, "data") ?? ExtractArray(doc.RootElement, warnings, "tests");
        if (tests is null)
            return new StatusCakeImportResult(0, 0, warnings);

        var allowedIntervals = MonitorIntervals.AllowedSeconds(env).OrderBy(x => x).ToArray();
        var created = 0;

        foreach (var t in tests.Value.EnumerateArray())
        {
            // Branch: required fields missing or invalid.
            var id = TryGetString(t, "id") ?? TryGetString(t, "test_id");
            var name = TryGetString(t, "name") ?? TryGetString(t, "website_name");
            // StatusCake API uses "website_url". Other exports may use "WebsiteURL" or "WebsiteHost".
            var url = TryGetStringCaseInsensitive(t, "website_url")
                      ?? TryGetStringCaseInsensitive(t, "WebsiteURL")
                      ?? TryGetStringCaseInsensitive(t, "WebsiteHost");

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                warnings.Add($"Skipped test (missing name/url). id={id ?? "(unknown)"}");
                continue;
            }

            var testType = TryGetStringCaseInsensitive(t, "test_type");
            if (!string.IsNullOrWhiteSpace(testType) && !string.Equals(testType, "HTTP", StringComparison.OrdinalIgnoreCase) && !string.Equals(testType, "HTTPS", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped non-HTTP test '{name}' (type={testType}).");
                continue;
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                // Branch: user exports often include bare hosts; default to https.
                if (!url.Contains("://", StringComparison.Ordinal))
                    url = "https://" + url.Trim();
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out _))
            {
                warnings.Add($"Skipped test '{name}' (invalid url={url}).");
                continue;
            }

            var checkRate = TryGetInt(t, "check_rate") ?? TryGetIntCaseInsensitive(t, "CheckRate");
            var interval = MapIntervalSeconds(checkRate, allowedIntervals);

            var paused = TryGetBool(t, "paused") ?? TryGetBoolCaseInsensitive(t, "Paused") ?? false;
            var timeout = TryGetInt(t, "timeout") ?? TryGetIntCaseInsensitive(t, "Timeout");

            var postRaw = TryGetStringCaseInsensitive(t, "post_raw");
            var postBody = TryGetStringCaseInsensitive(t, "post_body");
            var method = (!string.IsNullOrWhiteSpace(postRaw) || !string.IsNullOrWhiteSpace(postBody)) ? "POST" : "GET";
            var body = string.IsNullOrWhiteSpace(postRaw) ? postBody : postRaw;

            // Track StatusCake id in the name to enable later alert imports without schema changes.
            var finalName = string.IsNullOrWhiteSpace(id) ? name.Trim() : $"{name.Trim()} (sc:{id.Trim()})";

            // Branch: avoid creating duplicates if the same export is imported twice.
            var existing = await db.Monitors.AnyAsync(m => m.Name == finalName, cancellationToken);
            if (existing)
            {
                warnings.Add($"Skipped '{finalName}' (already exists).");
                continue;
            }

            var monitor = new MonitorEntity
            {
                Name = finalName,
                Url = url.Trim(),
                Method = method,
                // Imported monitors always start paused until reviewed in Hawk.
                Enabled = true,
                IsPaused = true,
                TimeoutSeconds = Math.Clamp(timeout ?? 30, 1, 300),
                IntervalSeconds = interval,
                ContentType = null,
                Body = body,
                CreatedByUserId = createdByUserId,
            };

            if (!paused)
                warnings.Add($"Imported '{finalName}' as paused by default.");

            // Optional content check: find_string + do_not_find is supported only for the "find" case.
            var findString = TryGetStringCaseInsensitive(t, "find_string") ?? TryGetStringCaseInsensitive(t, "FindString");
            var doNotFind = TryGetBoolCaseInsensitive(t, "do_not_find") ?? TryGetBoolCaseInsensitive(t, "DoNotFind") ?? false;
            if (!string.IsNullOrWhiteSpace(findString) && !doNotFind)
            {
                monitor.MatchRules.Add(new MonitorMatchRule { Mode = ContentMatchMode.Contains, Pattern = findString.Trim() });
            }
            else if (!string.IsNullOrWhiteSpace(findString) && doNotFind)
            {
                // Branch: inverted match is not represented in Hawk v1.
                monitor.Enabled = false;
                monitor.IsPaused = false;
                warnings.Add($"Imported '{finalName}' as disabled because StatusCake test uses DoNotFind (manual adjustment required).");
            }

            // Optional headers: StatusCake "custom_header" can be a newline-separated string of "Name: Value".
            var customHeader = TryGetStringCaseInsensitive(t, "custom_header");
            if (!string.IsNullOrWhiteSpace(customHeader))
            {
                foreach (var line in customHeader.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var idx = line.IndexOf(':', StringComparison.Ordinal);
                    if (idx <= 0) continue;
                    var hn = line[..idx].Trim();
                    var hv = line[(idx + 1)..].Trim();
                    if (hn.Length == 0) continue;
                    monitor.Headers.Add(new MonitorHeader { Name = hn, Value = hv });
                }
            }

            db.Monitors.Add(monitor);
            created++;
        }

        if (created != 0)
            await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StatusCake test import complete: created={Created} warnings={Warnings}", created, warnings.Count);
        return new StatusCakeImportResult(created, 0, warnings);
    }

    /// <summary>
    /// Imports uptime alerts from a StatusCake JSON export.
    /// </summary>
    /// <param name="jsonStream">
    /// Stream containing either:
    /// - A single raw API response from GET /v1/uptime/{id}/alerts (object with a "data" array), OR
    /// - An array of objects: [{ "test_id": "123", "data": [ ...alerts... ] }, ...]
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<StatusCakeImportResult> ImportAlertsAsync(Stream jsonStream, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);

        var createdRuns = 0;

        // Branch: array-of-groups format (preferred for importing multiple tests at once).
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in doc.RootElement.EnumerateArray())
            {
                var testId = TryGetStringCaseInsensitive(group, "test_id") ?? TryGetStringCaseInsensitive(group, "id");
                var data = ExtractArray(group, warnings, "data");
                if (string.IsNullOrWhiteSpace(testId) || data is null)
                {
                    warnings.Add("Skipped alert group (missing test_id or data array).");
                    continue;
                }

                createdRuns += await ImportAlertArrayAsync(testId!, data.Value, warnings, cancellationToken);
            }

            if (createdRuns != 0)
                await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("StatusCake alert import complete: runsCreated={RunsCreated} warnings={Warnings}", createdRuns, warnings.Count);
            return new StatusCakeImportResult(0, createdRuns, warnings);
        }

        // Branch: single-response format (no test id present). We can only import if the user included it manually.
        var singleTestId = TryGetStringCaseInsensitive(doc.RootElement, "test_id");
        var alerts = ExtractArray(doc.RootElement, warnings, "data") ?? ExtractArray(doc.RootElement, warnings, "alerts");
        if (string.IsNullOrWhiteSpace(singleTestId) || alerts is null)
        {
            warnings.Add("Alerts import expects either an array of groups or a single object with 'test_id' and 'data'.");
            return new StatusCakeImportResult(0, 0, warnings);
        }

        createdRuns += await ImportAlertArrayAsync(singleTestId!, alerts.Value, warnings, cancellationToken);
        if (createdRuns != 0)
            await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StatusCake alert import complete: runsCreated={RunsCreated} warnings={Warnings}", createdRuns, warnings.Count);
        return new StatusCakeImportResult(0, createdRuns, warnings);
    }

    private async Task<int> ImportAlertArrayAsync(string testId, JsonElement alerts, List<string> warnings, CancellationToken cancellationToken)
    {
        var marker = $"(sc:{testId.Trim()})";
        var monitor = await db.Monitors
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(m => m.Name.Contains(marker), cancellationToken);

        if (monitor is null)
        {
            warnings.Add($"No monitor found matching StatusCake test_id={testId}. Import tests first (monitor names include '{marker}').");
            return 0;
        }

        var createdRuns = 0;
        DateTimeOffset? latest = null;

        foreach (var a in alerts.EnumerateArray())
        {
            var triggeredAt = TryGetDateTimeOffsetCaseInsensitive(a, "triggered_at")
                              ?? TryGetDateTimeOffsetCaseInsensitive(a, "created_at")
                              ?? TryGetDateTimeOffsetCaseInsensitive(a, "time");

            if (triggeredAt is null)
            {
                warnings.Add($"Skipped alert (missing timestamp) for test_id={testId}.");
                continue;
            }

            latest = latest is null ? triggeredAt : (triggeredAt > latest ? triggeredAt : latest);

            var status = TryGetStringCaseInsensitive(a, "status") ?? "down";
            var statusCode = TryGetIntCaseInsensitive(a, "status_code");
            var success = !string.Equals(status, "down", StringComparison.OrdinalIgnoreCase);

            // Branch: avoid creating duplicates if the import is re-run.
            var already = await db.MonitorRuns.AnyAsync(r =>
                r.MonitorId == monitor.Id
                && r.StartedAt == triggeredAt.Value
                && r.ErrorMessage != null
                && r.ErrorMessage.StartsWith("Imported StatusCake alert"),
                cancellationToken);

            if (already)
                continue;

            db.MonitorRuns.Add(new MonitorRun
            {
                MonitorId = monitor.Id,
                StartedAt = triggeredAt.Value,
                FinishedAt = triggeredAt.Value,
                Reason = "import",
                RequestUrl = monitor.Url,
                RequestMethod = monitor.Method,
                RequestContentType = monitor.ContentType,
                RequestTimeoutMs = Math.Clamp(monitor.TimeoutSeconds, 1, 300) * 1000,
                RequestHeadersJson = "{}",
                RequestBodySnippet = string.IsNullOrWhiteSpace(monitor.Body) ? null : monitor.Body[..Math.Min(monitor.Body.Length, 4000)],
                DurationMs = 0,
                StatusCode = statusCode,
                Success = success,
                ErrorMessage = $"Imported StatusCake alert: {status}",
                ResponseSnippet = null,
                ResponseHeadersJson = "{}",
                ResponseContentType = null,
                ResponseContentLength = null,
                MatchResultsJson = "[]",
            });
            createdRuns++;
        }

        if (latest is not null && (monitor.LastRunAt is null || latest > monitor.LastRunAt))
            monitor.LastRunAt = latest;

        return createdRuns;
    }

    private static JsonElement? ExtractArray(JsonElement root, List<string> warnings, string arrayPropertyName)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;

        if (root.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"Expected an object or array for '{arrayPropertyName}'.");
            return null;
        }

        if (root.TryGetProperty(arrayPropertyName, out var data) && data.ValueKind == JsonValueKind.Array)
            return data;

        warnings.Add($"Expected '{arrayPropertyName}' to be an array.");
        return null;
    }

    private static int MapIntervalSeconds(int? checkRateSeconds, int[] allowedSeconds)
    {
        // Branch: missing check rate; default to the smallest allowed.
        if (checkRateSeconds is null || checkRateSeconds <= 0)
            return allowedSeconds.First();

        foreach (var a in allowedSeconds)
        {
            if (a >= checkRateSeconds.Value)
                return a;
        }
        return allowedSeconds.Last();
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        if (!obj.TryGetProperty(propertyName, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? TryGetStringCaseInsensitive(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
        }
        return null;
    }

    private static int? TryGetInt(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        if (!obj.TryGetProperty(propertyName, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i))
            return i;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s))
            return s;
        return null;
    }

    private static int? TryGetIntCaseInsensitive(JsonElement obj, string propertyName)
    {
        var s = TryGetStringCaseInsensitive(obj, propertyName);
        return int.TryParse(s, out var i) ? i : null;
    }

    private static bool? TryGetBool(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        if (!obj.TryGetProperty(propertyName, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.True) return true;
        if (p.ValueKind == JsonValueKind.False) return false;
        if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i != 0;
        return null;
    }

    private static bool? TryGetBoolCaseInsensitive(JsonElement obj, string propertyName)
    {
        var s = TryGetStringCaseInsensitive(obj, propertyName);
        if (string.IsNullOrWhiteSpace(s))
            return null;
        if (bool.TryParse(s, out var b))
            return b;
        if (int.TryParse(s, out var i))
            return i != 0;
        return null;
    }

    private static DateTimeOffset? TryGetDateTimeOffsetCaseInsensitive(JsonElement obj, string propertyName)
    {
        var s = TryGetStringCaseInsensitive(obj, propertyName);
        if (string.IsNullOrWhiteSpace(s))
            return null;
        if (DateTimeOffset.TryParse(s, out var dto))
            return dto;
        return null;
    }
}
