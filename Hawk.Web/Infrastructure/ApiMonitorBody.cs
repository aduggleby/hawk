// <file>
// <summary>
// Maps the JSON body of POST /monitors and PATCH /monitors/{id} onto a MonitorForm and validates it with the same rules as the UI.
// Errors are keyed by JSON field name. Field names match the monitor JSON export (matchRules take "mode"; "type" is accepted as
// an alias because the monitor detail response reports a rule's mode as "type").
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Infrastructure;

internal static partial class ApiMonitorBody
{
    private static readonly HashSet<string> HttpOnly = new(StringComparer.Ordinal)
    {
        "url", "method", "timeoutSeconds", "allowedStatusCodes", "followRedirects", "contentType", "body", "headers", "matchRules",
    };

    private static readonly Dictionary<string, string> Rejected = new(StringComparer.Ordinal)
    {
        ["heartbeatToken"] = "The server issues the ping token; it cannot be set.",
        ["ownerEmail"] = "The caller owns the monitors it creates; the owner cannot be set.",
        ["isPaused"] = "Use POST /monitors/{id}/pause and /resume to pause or resume a monitor.",
    };

    /// <summary>A form holding the monitor's persisted values, the base of a merge patch (same mapping as the Edit page).</summary>
    public static MonitorForm FromEntity(MonitorEntity m, IReadOnlyList<MonitorHeader> headers, IReadOnlyList<MonitorMatchRule> rules) => new()
    {
        Name = m.Name,
        Group = m.GroupName,
        Type = m.Type,
        Url = m.Url,
        Method = m.Method,
        Enabled = m.Enabled,
        TimeoutSeconds = m.TimeoutSeconds,
        IntervalSeconds = m.IntervalSeconds,
        GraceSeconds = m.HeartbeatGraceSeconds,
        AlertAfterConsecutiveFailures = m.AlertAfterConsecutiveFailures,
        AlertEmailOverride = m.AlertEmailOverride,
        AllowedStatusCodes = m.AllowedStatusCodes,
        FollowRedirects = m.FollowRedirects,
        RunRetentionDays = m.RunRetentionDays,
        ContentType = m.ContentType,
        Body = m.Body,
        HeaderNames = headers.Select(h => h.Name).ToArray(),
        HeaderValues = headers.Select(h => h.Value).ToArray(),
        MatchModes = rules.Select(r => r.Mode).ToArray(),
        MatchPatterns = rules.Select(r => r.Pattern).ToArray(),
    };

    /// <summary>
    /// Applies the properties present in <paramref name="body"/> to <paramref name="form"/> (a new form for create, the persisted values
    /// for a patch). <paramref name="persistedType"/> is null on create. Returns the errors by JSON field name; reports which child lists were sent.
    /// </summary>
    public static Dictionary<string, List<string>> Apply(
        JsonElement body, MonitorForm form, MonitorType? persistedType, out bool headersPresent, out bool rulesPresent)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        headersPresent = false;
        rulesPresent = false;

        // The type decides which other fields apply, so it is resolved first.
        var type = persistedType ?? MonitorType.Http;
        if (body.TryGetProperty("type", out var typeValue))
        {
            var text = typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString() : null;
            MonitorType? parsed = text?.ToLowerInvariant() switch
            {
                "http" => MonitorType.Http,
                "heartbeat" => MonitorType.Heartbeat,
                _ => null,
            };
            if (parsed is null)
                Add(errors, "type", "Must be 'http' or 'heartbeat'.");
            else if (persistedType is not null && parsed != persistedType)
                Add(errors, "type", "The type of a monitor cannot be changed.");
            else
                type = parsed.Value;
        }

        form.Type = type;
        var heartbeat = type == MonitorType.Heartbeat;

        string? s;
        foreach (var property in body.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;
            if (name == "type")
                continue;
            if (Rejected.TryGetValue(name, out var rejection))
            {
                Add(errors, name, rejection);
                continue;
            }

            if (heartbeat && HttpOnly.Contains(name))
            {
                Add(errors, name, "Does not apply to heartbeat monitors.");
                continue;
            }

            if (!heartbeat && name == "heartbeatGraceSeconds")
            {
                Add(errors, name, "Only applies to heartbeat monitors.");
                continue;
            }

            switch (name)
            {
                case "name": form.Name = String(errors, name, value, out s) ? s ?? string.Empty : form.Name; break;
                // Blank clears the group, like null.
                case "group": form.Group = String(errors, name, value, out s) ? s : form.Group; break;
                case "url": form.Url = String(errors, name, value, out s) ? s : form.Url; break;
                case "method": form.Method = String(errors, name, value, out s) ? s ?? string.Empty : form.Method; break;
                case "alertEmailOverride":
                    // Blank clears the override, like null.
                    form.AlertEmailOverride = String(errors, name, value, out s) ? (string.IsNullOrWhiteSpace(s) ? null : s) : form.AlertEmailOverride;
                    break;
                case "allowedStatusCodes": form.AllowedStatusCodes = String(errors, name, value, out s) ? s : form.AllowedStatusCodes; break;
                case "contentType": form.ContentType = String(errors, name, value, out s) ? s : form.ContentType; break;
                case "body": form.Body = String(errors, name, value, out s) ? s : form.Body; break;
                case "followRedirects":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) form.FollowRedirects = value.GetBoolean();
                    else Add(errors, name, "Must be true or false.");
                    break;
                case "enabled":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) form.Enabled = value.GetBoolean();
                    else Add(errors, name, "Must be true or false.");
                    break;
                case "timeoutSeconds": form.TimeoutSeconds = Int(errors, name, value, form.TimeoutSeconds); break;
                case "intervalSeconds": form.IntervalSeconds = Int(errors, name, value, form.IntervalSeconds); break;
                case "heartbeatGraceSeconds": form.GraceSeconds = Int(errors, name, value, form.GraceSeconds); break;
                case "alertAfterConsecutiveFailures":
                    form.AlertAfterConsecutiveFailures = Int(errors, name, value, form.AlertAfterConsecutiveFailures);
                    break;
                case "runRetentionDays":
                    if (value.ValueKind == JsonValueKind.Null) form.RunRetentionDays = null;
                    else form.RunRetentionDays = Int(errors, name, value, form.RunRetentionDays);
                    break;
                case "headers":
                    headersPresent = true;
                    ReadHeaders(errors, value, form);
                    break;
                case "matchRules":
                    rulesPresent = true;
                    ReadRules(errors, value, form);
                    break;
                default:
                    Add(errors, name, "Unknown property.");
                    break;
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates the form like the UI: attributes, <see cref="MonitorForm.Validate"/> (environment-dependent intervals, regexes, NUL characters)
    /// and the header and pattern limits of bulk import. Errors are keyed by JSON field name.
    /// </summary>
    public static Dictionary<string, List<string>> Validate(MonitorForm form, IHostEnvironment env)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var heartbeat = form.Type == MonitorType.Heartbeat;
        var results = MonitorValidation.Attributes(form).Concat(form.Validate(env));
        if (!heartbeat)
            results = results.Concat(MonitorValidation.ChildLimits(
                form.HeaderNames.Zip(form.HeaderValues, (n, v) => ((string?)n, (string?)v)),
                form.MatchModes.Zip(form.MatchPatterns, (m, p) => (m, (string?)p))));

        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.ErrorMessage))
                continue;
            var members = result.MemberNames.Any() ? result.MemberNames : [string.Empty];
            foreach (var member in members)
                Add(errors, JsonName(member), result.ErrorMessage);
        }

        return errors;
    }

    private static void ReadHeaders(Dictionary<string, List<string>> errors, JsonElement value, MonitorForm form)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            Add(errors, "headers", "Must be an array of { \"name\", \"value\" } objects.");
            return;
        }

        if (value.GetArrayLength() > MonitorForm.MaxHeaders)
        {
            Add(errors, "headers", $"At most {MonitorForm.MaxHeaders} headers are supported.");
            return;
        }

        var names = new List<string>();
        var values = new List<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var prefix = $"headers[{index++}]";
            string? name = null, headerValue = null;
            if (item.ValueKind != JsonValueKind.Object)
            {
                Add(errors, prefix, "Must be an object with \"name\" and \"value\".");
                continue;
            }

            foreach (var p in item.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "name": name = RequiredString(errors, $"{prefix}.name", p.Value, allowEmpty: false); break;
                    case "value": headerValue = RequiredString(errors, $"{prefix}.value", p.Value, allowEmpty: true); break;
                    default: Add(errors, $"{prefix}.{p.Name}", "Unknown property."); break;
                }
            }

            if (name is null && !errors.ContainsKey($"{prefix}.name")) Add(errors, $"{prefix}.name", "Required.");
            if (headerValue is null && !errors.ContainsKey($"{prefix}.value")) Add(errors, $"{prefix}.value", "Required.");
            names.Add(name ?? string.Empty);
            values.Add(headerValue ?? string.Empty);
        }

        form.HeaderNames = names.ToArray();
        form.HeaderValues = values.ToArray();
    }

    private static void ReadRules(Dictionary<string, List<string>> errors, JsonElement value, MonitorForm form)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            Add(errors, "matchRules", "Must be an array of { \"mode\", \"pattern\" } objects.");
            return;
        }

        if (value.GetArrayLength() > MonitorForm.MaxMatchRules)
        {
            Add(errors, "matchRules", $"At most {MonitorForm.MaxMatchRules} match rules are supported.");
            return;
        }

        var modes = new List<ContentMatchMode>();
        var patterns = new List<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var prefix = $"matchRules[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                Add(errors, prefix, "Must be an object with \"mode\" and \"pattern\".");
                continue;
            }

            ContentMatchMode? mode = null;
            string? pattern = null;
            foreach (var p in item.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "mode" or "type":
                        var text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                        ContentMatchMode? parsed = text?.ToLowerInvariant() switch
                        {
                            "contains" => ContentMatchMode.Contains,
                            "regex" => ContentMatchMode.Regex,
                            _ => null,
                        };
                        if (parsed is null) Add(errors, $"{prefix}.mode", "Must be 'contains' or 'regex'.");
                        else if (mode is not null && mode != parsed) Add(errors, $"{prefix}.mode", "\"mode\" and \"type\" disagree.");
                        else mode = parsed;
                        break;
                    case "pattern": pattern = RequiredString(errors, $"{prefix}.pattern", p.Value, allowEmpty: false); break;
                    default: Add(errors, $"{prefix}.{p.Name}", "Unknown property."); break;
                }
            }

            if (mode is null && !errors.ContainsKey($"{prefix}.mode")) Add(errors, $"{prefix}.mode", "Required: 'contains' or 'regex'.");
            if (pattern is null && !errors.ContainsKey($"{prefix}.pattern")) Add(errors, $"{prefix}.pattern", "Required.");
            modes.Add(mode ?? ContentMatchMode.None);
            patterns.Add(pattern ?? string.Empty);
        }

        form.MatchModes = modes.ToArray();
        form.MatchPatterns = patterns.ToArray();
    }

    private static string? RequiredString(Dictionary<string, List<string>> errors, string field, JsonElement value, bool allowEmpty)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            Add(errors, field, "Must be a string.");
            return null;
        }

        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            Add(errors, field, "Must not be blank.");
            return null;
        }

        return text;
    }

    /// <summary>Reads a string or null; false (with an error recorded) for any other JSON type.</summary>
    private static bool String(Dictionary<string, List<string>> errors, string field, JsonElement value, out string? text)
    {
        text = null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                text = value.GetString();
                return true;
            default:
                Add(errors, field, "Must be a string or null.");
                return false;
        }
    }

    private static int Int(Dictionary<string, List<string>> errors, string field, JsonElement value, int current) =>
        Int(errors, field, value, (int?)current) ?? current;

    private static int? Int(Dictionary<string, List<string>> errors, string field, JsonElement value, int? current)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        Add(errors, field, "Must be a whole number.");
        return current;
    }

    private static void Add(Dictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var list))
            errors[field] = list = [];
        if (!list.Contains(message))
            list.Add(message);
    }

    /// <summary>Maps a MonitorForm member name (or an already JSON-style name) to the API field name.</summary>
    private static string JsonName(string member)
    {
        if (member.Length == 0)
            return "request";
        if (ArrayMember().Match(member) is { Success: true } match)
        {
            var index = match.Groups["i"].Value;
            return match.Groups["array"].Value switch
            {
                "MatchPatterns" => $"matchRules[{index}].pattern",
                "MatchModes" => $"matchRules[{index}].mode",
                "HeaderNames" => $"headers[{index}].name",
                _ => $"headers[{index}].value",
            };
        }

        return member switch
        {
            "GraceSeconds" => "heartbeatGraceSeconds",
            _ => char.ToLowerInvariant(member[0]) + member[1..],
        };
    }

    [GeneratedRegex(@"^(?<array>MatchPatterns|MatchModes|HeaderNames|HeaderValues)\[(?<i>\d+)\]$")]
    private static partial Regex ArrayMember();
}
