// <file>
// <summary>
// JSON import/export model and mapping helpers for monitor configurations.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors;

public sealed record MonitorExportEnvelope
{
    public int Version { get; init; } = MonitorJsonPort.CurrentVersion;
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<MonitorExportModel> Monitors { get; init; } = [];
}

public sealed record MonitorExportModel
{
    public string Name { get; init; } = string.Empty;
    public MonitorType Type { get; init; } = MonitorType.Http;
    public string Url { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public bool Enabled { get; init; } = true;
    public bool IsPaused { get; init; }
    public string? OwnerEmail { get; init; }
    public int TimeoutSeconds { get; init; } = 15;
    public int IntervalSeconds { get; init; } = 60;
    public int HeartbeatGraceSeconds { get; init; }
    public string? HeartbeatToken { get; init; }
    public int AlertAfterConsecutiveFailures { get; init; } = 1;
    public string? AlertEmailOverride { get; init; }
    public string? AllowedStatusCodes { get; init; }

    /// <summary>Written for HTTP monitors only; a missing value means true (older files and Hawk versions).</summary>
    public bool? FollowRedirects { get; init; }
    public int? RunRetentionDays { get; init; }
    public string? ContentType { get; init; }
    public string? Body { get; init; }
    public List<MonitorHeaderExportModel> Headers { get; init; } = [];
    public List<MonitorMatchRuleExportModel> MatchRules { get; init; } = [];
}

public sealed record MonitorHeaderExportModel
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed record MonitorMatchRuleExportModel
{
    public ContentMatchMode Mode { get; init; }
    public string Pattern { get; init; } = string.Empty;
}

public static class MonitorJsonPort
{
    /// <summary>
    /// Current export format version. Version 2 adds <c>exportedAtUtc</c> and per-monitor <c>ownerEmail</c>; version 3 adds heartbeat
    /// monitors (<c>type</c>, <c>heartbeatGraceSeconds</c>, <c>heartbeatToken</c>). Versions 1 and 2 are still accepted.
    /// </summary>
    public const int CurrentVersion = 3;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static MonitorJsonPort()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public static MonitorExportEnvelope ToEnvelope(MonitorEntity monitor)
    {
        return new MonitorExportEnvelope
        {
            Monitors = [ToModel(monitor)],
        };
    }

    public static MonitorExportModel ToModel(MonitorEntity monitor, string? ownerEmail = null)
    {
        return new MonitorExportModel
        {
            Name = monitor.Name,
            Type = monitor.Type,
            Url = monitor.Url,
            Method = monitor.Method,
            Enabled = monitor.Enabled,
            IsPaused = monitor.IsPaused,
            OwnerEmail = string.IsNullOrWhiteSpace(ownerEmail) ? null : ownerEmail,
            TimeoutSeconds = monitor.TimeoutSeconds,
            IntervalSeconds = monitor.IntervalSeconds,
            HeartbeatGraceSeconds = monitor.HeartbeatGraceSeconds,
            HeartbeatToken = monitor.HeartbeatToken,
            AlertAfterConsecutiveFailures = monitor.AlertAfterConsecutiveFailures,
            AlertEmailOverride = monitor.AlertEmailOverride,
            AllowedStatusCodes = monitor.AllowedStatusCodes,
            FollowRedirects = monitor.Type == MonitorType.Http ? monitor.FollowRedirects : null,
            RunRetentionDays = monitor.RunRetentionDays,
            ContentType = monitor.ContentType,
            Body = monitor.Body,
            Headers = monitor.Headers
                .OrderBy(h => h.Name, StringComparer.Ordinal)
                .ThenBy(h => h.Value, StringComparer.Ordinal)
                .Select(h => new MonitorHeaderExportModel { Name = h.Name, Value = h.Value })
                .ToList(),
            MatchRules = monitor.MatchRules
                .OrderBy(r => r.Mode)
                .ThenBy(r => r.Pattern, StringComparer.Ordinal)
                .Select(r => new MonitorMatchRuleExportModel { Mode = r.Mode, Pattern = r.Pattern })
                .ToList(),
        };
    }

    public static bool TryParse(string json, out MonitorExportEnvelope? envelope, out string? error)
    {
        if (!TryParseCore(json, out envelope, out error))
            return false;

        // Reject malformed content (null monitor list or entries) with a parse error instead of crashing later.
        if (envelope is null || envelope.Monitors is null || envelope.Monitors.Any(m => m is null))
        {
            envelope = null;
            error = "Import file contains a null monitor list or null monitor entries.";
            return false;
        }

        envelope = envelope with
        {
            Monitors = envelope.Monitors
                .Select(m => m with { Headers = m.Headers ?? [], MatchRules = m.MatchRules ?? [] })
                .ToList(),
        };
        return true;
    }

    private static bool TryParseCore(string json, out MonitorExportEnvelope? envelope, out string? error)
    {
        envelope = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Import file is empty.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var monitors = JsonSerializer.Deserialize<List<MonitorExportModel>>(json, JsonOptions) ?? [];
                envelope = new MonitorExportEnvelope { Version = 1, Monitors = monitors };
                return true;
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Expected a JSON object or array.";
                return false;
            }

            if (doc.RootElement.TryGetProperty("monitors", out _))
            {
                envelope = JsonSerializer.Deserialize<MonitorExportEnvelope>(json, JsonOptions);
                if (envelope is null)
                {
                    error = "Failed to parse monitor export payload.";
                    return false;
                }

                if (envelope.Version > CurrentVersion)
                {
                    error = $"Unsupported export version {envelope.Version}. This Hawk version supports up to {CurrentVersion}.";
                    envelope = null;
                    return false;
                }

                return true;
            }

            var one = JsonSerializer.Deserialize<MonitorExportModel>(json, JsonOptions);
            if (one is null)
            {
                error = "Failed to parse monitor payload.";
                return false;
            }

            envelope = new MonitorExportEnvelope { Version = 1, Monitors = [one] };
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    public static bool TryCreateMonitor(
        MonitorExportModel model,
        string? createdByUserId,
        IHostEnvironment env,
        out MonitorEntity monitor,
        out string? error,
        bool forcePaused = false)
    {
        monitor = default!;
        error = null;

        var heartbeat = model.Type == MonitorType.Heartbeat;
        var form = new MonitorForm
        {
            Name = model.Name,
            Type = model.Type,
            Url = model.Url,
            Method = model.Method,
            Enabled = model.Enabled,
            TimeoutSeconds = model.TimeoutSeconds,
            IntervalSeconds = model.IntervalSeconds,
            GraceSeconds = model.HeartbeatGraceSeconds,
            AlertAfterConsecutiveFailures = model.AlertAfterConsecutiveFailures,
            AlertEmailOverride = model.AlertEmailOverride,
            AllowedStatusCodes = model.AllowedStatusCodes,
            FollowRedirects = model.FollowRedirects ?? true,
            RunRetentionDays = model.RunRetentionDays,
            ContentType = model.ContentType,
            Body = model.Body,
        };

        // Field-level attributes first, then the form's cross-field rules, then the header and match-rule limits (HTTP only).
        var validationError = MonitorValidation.Attributes(form)
            .Concat(form.Validate(env))
            .Concat(heartbeat ? [] : MonitorValidation.ChildLimits(
                model.Headers.Select(h => (h?.Name, h?.Value)),
                model.MatchRules.Select(r => (r?.Mode ?? ContentMatchMode.None, r?.Pattern))))
            .Select(v => v.ErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            error = validationError;
            return false;
        }

        monitor = new MonitorEntity
        {
            Name = (model.Name ?? string.Empty).Trim(),
            Type = model.Type,
            Url = heartbeat ? string.Empty : (model.Url ?? string.Empty).Trim(),
            Method = heartbeat ? "GET" : (model.Method ?? "GET").Trim().ToUpperInvariant(),
            Enabled = model.Enabled,
            IsPaused = forcePaused || (model.Enabled && model.IsPaused),
            TimeoutSeconds = model.TimeoutSeconds,
            IntervalSeconds = model.IntervalSeconds,
            AlertAfterConsecutiveFailures = model.AlertAfterConsecutiveFailures,
            AlertEmailOverride = string.IsNullOrWhiteSpace(model.AlertEmailOverride) ? null : model.AlertEmailOverride.Trim(),
            AllowedStatusCodes = heartbeat ? null : AllowedStatusCodesParser.Normalize(model.AllowedStatusCodes),
            FollowRedirects = heartbeat || (model.FollowRedirects ?? true),
            RunRetentionDays = model.RunRetentionDays,
            ContentType = heartbeat || string.IsNullOrWhiteSpace(model.ContentType) ? null : model.ContentType.Trim(),
            Body = heartbeat ? null : model.Body,
            CreatedByUserId = createdByUserId,
        };

        if (heartbeat)
        {
            // A valid exported token is kept (a move keeps client URLs); the caller dedupes by token first.
            monitor.HeartbeatGraceSeconds = model.HeartbeatGraceSeconds;
            monitor.HeartbeatToken = HeartbeatTokens.TryNormalize(model.HeartbeatToken, out var token) ? token : HeartbeatTokens.Generate();
            monitor.NextRunAt = HeartbeatTokens.InitialDeadline(DateTimeOffset.UtcNow, monitor.IntervalSeconds, monitor.HeartbeatGraceSeconds);
            return true;
        }

        foreach (var header in model.Headers.Where(h => h is not null))
        {
            var name = (header.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            monitor.Headers.Add(new MonitorHeader
            {
                Name = name,
                Value = (header.Value ?? string.Empty).Trim(),
            });
        }

        foreach (var rule in model.MatchRules.Where(r => r is not null))
        {
            var pattern = (rule.Pattern ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            if (rule.Mode == ContentMatchMode.None)
                continue;

            monitor.MatchRules.Add(new MonitorMatchRule
            {
                Mode = rule.Mode,
                Pattern = pattern,
            });
        }

        return true;
    }

    /// <summary>
    /// Non-blocking import warnings: an unparsable regex makes the monitor fail every run, but is still importable.
    /// </summary>
    public static IReadOnlyList<string> GetWarnings(MonitorExportModel model)
    {
        var warnings = new List<string>();
        foreach (var rule in model.MatchRules.Where(r => r is not null))
        {
            var pattern = (rule.Pattern ?? string.Empty).Trim();
            if (rule.Mode == ContentMatchMode.Regex && pattern.Length > 0 && !MonitorForm.IsValidRegex(pattern, out _))
                warnings.Add($"Invalid regex '{pattern}': the monitor will fail every run until fixed.");
        }

        return warnings;
    }
}
