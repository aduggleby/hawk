// <file>
// <summary>
// JSON import/export model and mapping helpers for monitor configurations.
// </summary>
// </file>

using System.Text.Json;
using System.Text.Json.Serialization;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors;

public sealed record MonitorExportEnvelope
{
    public int Version { get; init; } = 1;
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<MonitorExportModel> Monitors { get; init; } = [];
}

public sealed record MonitorExportModel
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public bool Enabled { get; init; } = true;
    public bool IsPaused { get; init; }
    public int TimeoutSeconds { get; init; } = 15;
    public int IntervalSeconds { get; init; } = 60;
    public int AlertAfterConsecutiveFailures { get; init; } = 1;
    public string? AlertEmailOverride { get; init; }
    public string? AllowedStatusCodes { get; init; }
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

    public static MonitorExportModel ToModel(MonitorEntity monitor)
    {
        return new MonitorExportModel
        {
            Name = monitor.Name,
            Url = monitor.Url,
            Method = monitor.Method,
            Enabled = monitor.Enabled,
            IsPaused = monitor.IsPaused,
            TimeoutSeconds = monitor.TimeoutSeconds,
            IntervalSeconds = monitor.IntervalSeconds,
            AlertAfterConsecutiveFailures = monitor.AlertAfterConsecutiveFailures,
            AlertEmailOverride = monitor.AlertEmailOverride,
            AllowedStatusCodes = monitor.AllowedStatusCodes,
            RunRetentionDays = monitor.RunRetentionDays,
            ContentType = monitor.ContentType,
            Body = monitor.Body,
            Headers = monitor.Headers
                .Select(h => new MonitorHeaderExportModel { Name = h.Name, Value = h.Value })
                .ToList(),
            MatchRules = monitor.MatchRules
                .Select(r => new MonitorMatchRuleExportModel { Mode = r.Mode, Pattern = r.Pattern })
                .ToList(),
        };
    }

    public static bool TryParse(string json, out MonitorExportEnvelope? envelope, out string? error)
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
                envelope = new MonitorExportEnvelope { Monitors = monitors };
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

                return true;
            }

            var one = JsonSerializer.Deserialize<MonitorExportModel>(json, JsonOptions);
            if (one is null)
            {
                error = "Failed to parse monitor payload.";
                return false;
            }

            envelope = new MonitorExportEnvelope { Monitors = [one] };
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
        out string? error)
    {
        monitor = default!;
        error = null;

        var form = new MonitorForm
        {
            Name = model.Name,
            Url = model.Url,
            Method = model.Method,
            Enabled = model.Enabled,
            TimeoutSeconds = model.TimeoutSeconds,
            IntervalSeconds = model.IntervalSeconds,
            AlertAfterConsecutiveFailures = model.AlertAfterConsecutiveFailures,
            AlertEmailOverride = model.AlertEmailOverride,
            AllowedStatusCodes = model.AllowedStatusCodes,
            RunRetentionDays = model.RunRetentionDays,
            ContentType = model.ContentType,
            Body = model.Body,
        };

        var validationError = form.Validate(env).Select(v => v.ErrorMessage).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            error = validationError;
            return false;
        }

        monitor = new MonitorEntity
        {
            Name = (model.Name ?? string.Empty).Trim(),
            Url = (model.Url ?? string.Empty).Trim(),
            Method = (model.Method ?? "GET").Trim().ToUpperInvariant(),
            Enabled = model.Enabled,
            IsPaused = model.Enabled && model.IsPaused,
            TimeoutSeconds = model.TimeoutSeconds,
            IntervalSeconds = model.IntervalSeconds,
            AlertAfterConsecutiveFailures = model.AlertAfterConsecutiveFailures,
            AlertEmailOverride = string.IsNullOrWhiteSpace(model.AlertEmailOverride) ? null : model.AlertEmailOverride.Trim(),
            AllowedStatusCodes = AllowedStatusCodesParser.Normalize(model.AllowedStatusCodes),
            RunRetentionDays = model.RunRetentionDays,
            ContentType = string.IsNullOrWhiteSpace(model.ContentType) ? null : model.ContentType.Trim(),
            Body = model.Body,
            CreatedByUserId = createdByUserId,
        };

        foreach (var header in model.Headers)
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

        foreach (var rule in model.MatchRules)
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
}
