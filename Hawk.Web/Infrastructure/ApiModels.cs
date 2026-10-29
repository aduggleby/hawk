// <file>
// <summary>
// Response models and JSON options of the /api/v1 endpoints (camelCase, enums as camelCase strings, UTC ISO 8601 timestamps).
// </summary>
// </file>

using System.Text.Json;
using System.Text.Json.Serialization;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;

namespace Hawk.Web.Infrastructure;

/// <summary>Serializer options for API responses: same converter settings as <c>MonitorJsonPort</c>, but nulls are kept so shapes stay stable.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed record ApiCaller(string Id, string? Email);

public sealed record ApiKeyInfo(Guid Id, string Name, string Scope);

public sealed record ApiEndpointInfo(string Method, string Path, string Scope, string Description);

public sealed record ApiIndexResponse(ApiCaller User, ApiKeyInfo? Key, IReadOnlyList<ApiEndpointInfo> Endpoints);

public sealed record ApiFailureSummary(
    DateTimeOffset LastRunAt, int? StatusCode, string? Error, int ConsecutiveFailures, DateTimeOffset? FailingSince);

public sealed record ApiMonitorListItem(
    Guid Id,
    string Name,
    MonitorType Type,
    string? Url,
    MonitorState State,
    MonitorHealth Health,
    bool OwnedByCaller,
    ApiFailureSummary? Failure,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt);

public sealed record ApiMonitorList(IReadOnlyList<ApiMonitorListItem> Monitors);

public sealed record ApiAlertState(
    int ConsecutiveFailures,
    DateTimeOffset? FailureIncidentOpenedAt,
    DateTimeOffset? LastFailureAlertSentAt,
    string? LastFailureAlertError,
    bool PendingRecoveryAlert,
    DateTimeOffset? LastRecoveryAlertSentAt,
    string? LastRecoveryAlertError);

public sealed record ApiAlertRecipients(AlertRecipientSource Source, IReadOnlyList<string> Addresses);

/// <summary>
/// Monitor configuration as the Details page shows it. <see cref="ConcurrencyStamp"/> is the ETag value for If-Match.
/// <see cref="PingUrl"/> (with the heartbeat ping token) is set only in the response to creating a heartbeat; it is null everywhere else.
/// </summary>
public sealed record ApiMonitorDetail(
    Guid Id,
    string Name,
    MonitorType Type,
    string? Url,
    MonitorState State,
    MonitorHealth Health,
    bool OwnedByCaller,
    ApiFailureSummary? Failure,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    string Method,
    int IntervalSeconds,
    int TimeoutSeconds,
    string? AllowedStatusCodes,
    bool? FollowRedirects,
    int AlertAfterConsecutiveFailures,
    string? AlertEmailOverride,
    int? RunRetentionDays,
    int? HeartbeatGraceSeconds,
    DateTimeOffset? LastPingAt,
    DateTimeOffset CreatedAt,
    ApiAlertState AlertState,
    ApiAlertRecipients AlertRecipients,
    IReadOnlyList<ApiHeader> Headers,
    IReadOnlyList<ApiMatchRule> MatchRules,
    Guid ConcurrencyStamp,
    string? PingUrl);

public sealed record ApiHeader(string Name, string Value);

public sealed record ApiMatchRule(ContentMatchMode Type, string Pattern);

public sealed record ApiRunSummary(
    Guid Id,
    string State,
    string? Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? DurationMs,
    bool? Success,
    int? StatusCode,
    string? Error);

public sealed record ApiRunList(IReadOnlyList<ApiRunSummary> Runs);

public sealed record ApiRunDetail(
    Guid Id,
    string State,
    string? Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? DurationMs,
    bool? Success,
    int? StatusCode,
    string? Error,
    string? RequestUrl,
    string? RequestMethod,
    string? RequestContentType,
    int? RequestTimeoutMs,
    JsonElement RequestHeaders,
    string? RequestBodySnippet,
    JsonElement ResponseHeaders,
    string? ResponseContentType,
    long? ResponseContentLength,
    string? ResponseSnippet,
    JsonElement MatchResults,
    bool AlertSent,
    string? AlertError);

public sealed record ApiAlertingResponse(
    int AlertAfterConsecutiveFailures, string? AlertEmailOverride, ApiAlertRecipients AlertRecipients);

public sealed record ApiAccountAlerting(string? AlertEmail, string AppliesTo);

public sealed record ApiPauseResumeResponse(bool Changed, MonitorState State);

public sealed record ApiRunQueued(Guid RunId, string RunUrl, string PollAdvice);
