// <file>
// <summary>
// The /api/v1 JSON API for agents: read monitors and runs, change alert settings, pause/resume monitors and queue runs.
// Authenticated only with API keys (policies ApiRead / ApiWrite); handlers are thin wrappers over the shared monitor services.
// </summary>
// </file>

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Infrastructure;

public static class ApiEndpoints
{
    public const string Prefix = "/api/v1";
    public const int DefaultRunLimit = 25;
    public const int MaxRunLimit = 100;

    /// <summary>Largest accepted request body of a write endpoint.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    private const string AccountNote = "monitors owned by the caller";
    private const string PollAdvice =
        "Poll runUrl until its state is 'completed'; stop after the monitor's timeoutSeconds plus 60 seconds.";

    private static readonly ApiEndpointInfo[] EndpointList =
    [
        new("GET", Prefix, "read", "The caller, the API key and this endpoint list."),
        new("GET", Prefix + "/monitors?group=", "read", "All monitors with state, health, group and failure summary; group= filters case-insensitively (empty = ungrouped)."),
        new("GET", Prefix + "/monitors/{id}", "read", "One monitor: configuration, alert state and alert recipients."),
        new("GET", Prefix + "/monitors/{id}/runs?limit=", "read", "Recent runs, newest first (default 25, max 100)."),
        new("GET", Prefix + "/monitors/{id}/runs/{runId}", "read", "One run with request and response diagnostics."),
        new("POST", Prefix + "/monitors", "write", "Create an HTTP or heartbeat monitor owned by the caller; a heartbeat response includes the pingUrl."),
        new("PATCH", Prefix + "/monitors/{id}", "write", "Edit a monitor (merge patch; headers and matchRules replace the list; optional If-Match)."),
        new("DELETE", Prefix + "/monitors/{id}", "write", "Delete a monitor with its runs and alert state; cannot be undone (optional If-Match)."),
        new("PATCH", Prefix + "/monitors/{id}/alerting", "write", "Change alertAfterConsecutiveFailures (1-20) and/or alertEmailOverride (null clears)."),
        new("GET", Prefix + "/account/alerting", "read", "The caller's account-wide alert email."),
        new("PUT", Prefix + "/account/alerting", "write", "Replace the account-wide alert email: {\"alertEmail\": string|null}."),
        new("POST", Prefix + "/monitors/{id}/pause", "write", "Pause a monitor."),
        new("POST", Prefix + "/monitors/{id}/resume", "write", "Resume a paused monitor."),
        new("POST", Prefix + "/monitors/{id}/run", "write", "Queue a run now; poll the returned runUrl."),
        new("GET", Prefix + "/groups", "read", "Monitor groups with monitorCount, failingCount and pausedCount."),
        new("POST", Prefix + "/groups/pause", "write", "Pause every enabled monitor in a group: {\"group\": string}."),
        new("POST", Prefix + "/groups/resume", "write", "Resume every paused monitor in a group: {\"group\": string}."),
        new("POST", Prefix + "/groups/run", "write", "Queue a run for every HTTP monitor in a group: {\"group\": string}."),
        new("POST", Prefix + "/groups/rename", "write", "Rename a group (merges into an existing group of that name): {\"group\": string, \"newName\": string}."),
    ];

    /// <summary>
    /// Maps the <c>/api/v1</c> group (unknown <c>/api/*</c> paths are handled by <see cref="ApiErrors.UseHawkApiErrors"/>).
    /// </summary>
    public static IEndpointRouteBuilder MapHawkApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        api.MapGet("", GetIndexAsync).RequireAuthorization(ApiKeyAuthentication.ReadPolicy);
        api.MapGet("/monitors", GetMonitorsAsync).RequireAuthorization(ApiKeyAuthentication.ReadPolicy);
        api.MapGet("/groups", GetGroupsAsync).RequireAuthorization(ApiKeyAuthentication.ReadPolicy);
        api.MapGet("/monitors/{id:guid}", GetMonitorAsync).RequireAuthorization(ApiKeyAuthentication.ReadPolicy);
        api.MapGet("/monitors/{id:guid}/runs", GetRunsAsync).RequireAuthorization(ApiKeyAuthentication.ReadPolicy);
        api.MapGet("/monitors/{id:guid}/runs/{runId:guid}", GetRunAsync).RequireAuthorization(ApiKeyAuthentication.ReadPolicy);
        api.MapGet("/account/alerting", GetAccountAlertingAsync).RequireAuthorization(ApiKeyAuthentication.ReadPolicy);

        api.MapPost("/monitors", CreateMonitorAsync).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapMethods("/monitors/{id:guid}", [HttpMethods.Patch], PatchMonitorAsync).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapDelete("/monitors/{id:guid}", DeleteMonitorAsync).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapMethods("/monitors/{id:guid}/alerting", [HttpMethods.Patch], PatchAlertingAsync).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapPut("/account/alerting", PutAccountAlertingAsync).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapPost("/monitors/{id:guid}/pause", (Guid id, HttpContext http, MonitorCommands commands, ILoggerFactory logs, CancellationToken ct) =>
            PauseResumeAsync(id, "pause", http, commands.PauseMonitorAsync, logs, ct)).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapPost("/monitors/{id:guid}/resume", (Guid id, HttpContext http, MonitorCommands commands, ILoggerFactory logs, CancellationToken ct) =>
            PauseResumeAsync(id, "resume", http, commands.ResumeMonitorAsync, logs, ct)).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapPost("/monitors/{id:guid}/run", RunAsync).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapPost("/groups/pause", (HttpContext http, ApplicationDbContext db, MonitorCommands commands, ILoggerFactory logs, CancellationToken ct) =>
            GroupChangeAsync("pause", http, db, commands.PauseAsync, logs, ct)).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapPost("/groups/resume", (HttpContext http, ApplicationDbContext db, MonitorCommands commands, ILoggerFactory logs, CancellationToken ct) =>
            GroupChangeAsync("resume", http, db, commands.ResumeAsync, logs, ct)).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapPost("/groups/run", GroupRunAsync).RequireAuthorization(ApiKeyAuthentication.WritePolicy);
        api.MapPost("/groups/rename", GroupRenameAsync).RequireAuthorization(ApiKeyAuthentication.WritePolicy);

        // No catch-all route: UseHawkApiErrors already turns unknown /api paths into JSON 404 and wrong methods into JSON 405,
        // and a catch-all endpoint would turn those 405s into 404s.
        return app;
    }

    // ---- reads ----

    private static async Task<IResult> GetIndexAsync(HttpContext http, ApplicationDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var scope = http.User.FindFirstValue(ApiKeyAuthentication.ScopeClaim) ?? "read";
        ApiKeyInfo? key = null;
        if (Guid.TryParse(http.User.FindFirstValue(ApiKeyAuthentication.KeyIdClaim), out var keyId))
        {
            var name = await db.ApiKeys.AsNoTracking()
                .Where(k => k.Id == keyId && k.UserId == userId)
                .Select(k => k.Name)
                .FirstOrDefaultAsync(ct);
            if (name is not null)
                key = new ApiKeyInfo(keyId, name, scope);
        }

        return Json(new ApiIndexResponse(new ApiCaller(userId, http.User.FindFirstValue(ClaimTypes.Email)), key, EndpointList));
    }

    private static async Task<IResult> GetMonitorsAsync(
        HttpContext http, ApplicationDbContext db, MonitorStatusService statuses, CancellationToken ct)
    {
        var userId = UserId(http);
        var query = db.Monitors.AsNoTracking();
        if (http.Request.Query.TryGetValue("group", out var groupFilter))
        {
            // group= (empty) lists the ungrouped monitors; a name matches case-insensitively.
            var group = MonitorGroups.Normalize(groupFilter.ToString());
            if (MonitorGroups.Validate(group) is { } groupError)
                return Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid", errors: Errors("group", groupError));
            query = group is null ? query.Where(m => m.GroupName == null) : MonitorGroups.InGroup(query, group);
        }

        var monitors = await query.ToListAsync(ct);
        monitors.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        var status = await statuses.GetStatusesAsync(monitors, ct);

        return Json(new ApiMonitorList(monitors
            .Select(m => new ApiMonitorListItem(
                m.Id, m.Name, m.GroupName, m.Type, UrlOf(m), status[m.Id].State, status[m.Id].Health,
                m.CreatedByUserId == userId, FailureOf(status[m.Id]), m.LastRunAt, m.NextRunAt))
            .ToList()));
    }

    private static async Task<IResult> GetGroupsAsync(ApplicationDbContext db, MonitorStatusService statuses, CancellationToken ct)
    {
        var monitors = await db.Monitors.AsNoTracking().Where(m => m.GroupName != null).ToListAsync(ct);
        var status = await statuses.GetStatusesAsync(monitors, ct);

        // Group in memory ignoring case (database collations differ); the display spelling is the first stored one in ordinal order.
        var groups = monitors
            .GroupBy(m => m.GroupName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ApiGroupSummary(
                MonitorGroups.DisplayName(g.Select(m => m.GroupName!))!,
                g.Count(),
                // The Index rule: the last completed run failed, even when the monitor is also overdue.
                g.Count(m => status[m.Id].Failure is not null),
                g.Count(m => status[m.Id].State == MonitorState.Paused)))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
        return Json(new ApiGroupList(groups));
    }

    private static async Task<IResult> GetMonitorAsync(
        Guid id, HttpContext http, ApplicationDbContext db, MonitorStatusService statuses, AlertRecipientResolver resolver, CancellationToken ct) =>
        await DetailAsync(id, http, db, statuses, resolver, StatusCodes.Status200OK, includePingUrl: false, ct);

    /// <summary>The monitor detail response; sets the ETag. <paramref name="includePingUrl"/> is true only when creating a heartbeat.</summary>
    private static async Task<IResult> DetailAsync(
        Guid id, HttpContext http, ApplicationDbContext db, MonitorStatusService statuses, AlertRecipientResolver resolver,
        int statusCode, bool includePingUrl, CancellationToken ct)
    {
        var m = await db.Monitors.AsNoTracking()
            .Include(x => x.Headers).Include(x => x.MatchRules).AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return MonitorNotFound();

        var status = (await statuses.GetStatusesAsync([m], ct))[id];
        var state = await db.MonitorAlertStates.AsNoTracking().FirstOrDefaultAsync(s => s.MonitorId == id, ct);
        var heartbeat = m.Type == MonitorType.Heartbeat;
        http.Response.Headers.ETag = ETagOf(m.ConcurrencyStamp);

        return Json(new ApiMonitorDetail(
            m.Id, m.Name, m.GroupName, m.Type, UrlOf(m), status.State, status.Health, m.CreatedByUserId == UserId(http), FailureOf(status),
            m.LastRunAt, m.NextRunAt, m.Method, m.IntervalSeconds, m.TimeoutSeconds, m.AllowedStatusCodes,
            heartbeat ? null : m.FollowRedirects,
            m.AlertAfterConsecutiveFailures, m.AlertEmailOverride, m.RunRetentionDays,
            heartbeat ? m.HeartbeatGraceSeconds : null, m.LastPingAt, m.CreatedAt,
            new ApiAlertState(
                state?.ConsecutiveFailures ?? 0, state?.FailureIncidentOpenedAt, state?.LastFailureAlertSentAt,
                state?.LastFailureAlertError, state?.PendingRecoveryAlert ?? false, state?.LastRecoveryAlertSentAt,
                state?.LastRecoveryAlertError),
            await RecipientsAsync(resolver, m, ct),
            // Heartbeats show neither on the Details page; same deterministic order as MonitorJsonPort.
            heartbeat ? [] : m.Headers.OrderBy(h => h.Name, StringComparer.Ordinal).ThenBy(h => h.Value, StringComparer.Ordinal)
                .Select(h => new ApiHeader(h.Name, h.Value)).ToList(),
            heartbeat ? [] : m.MatchRules.OrderBy(r => r.Mode).ThenBy(r => r.Pattern, StringComparer.Ordinal)
                .Select(r => new ApiMatchRule(r.Mode, r.Pattern)).ToList(),
            m.ConcurrencyStamp,
            // Same URL the Details page shows.
            includePingUrl && m.HeartbeatToken is { } token ? $"{http.Request.Scheme}://{http.Request.Host}/ping/{token}" : null),
            statusCode);
    }

    private static async Task<IResult> GetRunsAsync(Guid id, HttpContext http, ApplicationDbContext db, CancellationToken ct)
    {
        var limit = DefaultRunLimit;
        if (http.Request.Query.TryGetValue("limit", out var raw))
        {
            if (!int.TryParse(raw.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)
                return Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid",
                    errors: Errors("limit", $"Must be a whole number from 1 to {MaxRunLimit}."));
            limit = Math.Min(limit, MaxRunLimit);
        }

        if (!await db.Monitors.AnyAsync(m => m.Id == id, ct)) return MonitorNotFound();

        var runs = await db.MonitorRuns.AsNoTracking()
            .Where(r => r.MonitorId == id)
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);
        return Json(new ApiRunList(runs.Select(RunSummary).ToList()));
    }

    private static async Task<IResult> GetRunAsync(Guid id, Guid runId, ApplicationDbContext db, CancellationToken ct)
    {
        var r = await db.MonitorRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId && x.MonitorId == id, ct);
        if (r is null)
            return Problem(StatusCodes.Status404NotFound, "not_found", "Run not found");

        var s = RunSummary(r);
        var completed = IsCompleted(r);
        return Json(new ApiRunDetail(
            s.Id, s.State, s.Reason, s.StartedAt, s.FinishedAt, s.DurationMs, s.Success, s.StatusCode, s.Error,
            r.RequestUrl, r.RequestMethod, r.RequestContentType, r.RequestTimeoutMs, ParseJson(r.RequestHeadersJson),
            r.RequestBodySnippet, ParseJson(r.ResponseHeadersJson), r.ResponseContentType, r.ResponseContentLength,
            r.ResponseSnippet, ParseJson(r.MatchResultsJson), completed && r.AlertSent, completed ? r.AlertError : null));
    }

    private static async Task<IResult> GetAccountAlertingAsync(HttpContext http, AccountAlertSettings settings, CancellationToken ct) =>
        Json(new ApiAccountAlerting(await settings.GetEmailAsync(UserId(http), ct), AccountNote));

    // ---- writes ----

    private static async Task<IResult> PatchAlertingAsync(
        Guid id, HttpContext http, ApplicationDbContext db, MonitorCommands commands, AlertRecipientResolver resolver,
        ILoggerFactory logs, CancellationToken ct)
    {
        var (body, bodyError) = await ReadBodyAsync(http, ct);
        if (bodyError is not null) return bodyError;

        // Merge patch: only properties that are present change; null is allowed for the email only.
        var errors = new Dictionary<string, string[]>();
        int? threshold = null;
        string? email = null;
        var emailPresent = false;
        foreach (var property in body.EnumerateObject())
        {
            switch (property.Name)
            {
                case "alertAfterConsecutiveFailures":
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
                    {
                        if (AlertSettingsValidator.ValidateThreshold(value) is { } thresholdError)
                            errors[property.Name] = [thresholdError];
                        else
                            threshold = value;
                    }
                    else
                    {
                        errors[property.Name] = [$"Must be a whole number from {AlertSettingsValidator.MinThreshold} to {AlertSettingsValidator.MaxThreshold}; null is not allowed."];
                    }
                    break;
                case "alertEmailOverride":
                    (email, emailPresent) = ParseEmail(property, errors);
                    break;
                default:
                    errors[property.Name] = ["Unknown property."];
                    break;
            }
        }

        if (errors.Count > 0)
            return Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid", errors: errors);
        if (threshold is not null || emailPresent)
        {
            var clearEmail = emailPresent && string.IsNullOrWhiteSpace(email);
            if (!await commands.UpdateAlertingAsync(id, threshold, email, clearEmail, ct)) return MonitorNotFound();
            LogWrite(http, logs, id, "alerting.update");
        }

        var m = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return MonitorNotFound();
        return Json(new ApiAlertingResponse(m.AlertAfterConsecutiveFailures, m.AlertEmailOverride, await RecipientsAsync(resolver, m, ct)));
    }

    private static async Task<IResult> PutAccountAlertingAsync(
        HttpContext http, AccountAlertSettings settings, ILoggerFactory logs, CancellationToken ct)
    {
        var (body, bodyError) = await ReadBodyAsync(http, ct);
        if (bodyError is not null) return bodyError;

        var errors = new Dictionary<string, string[]>();
        string? email = null;
        var present = false;
        foreach (var property in body.EnumerateObject())
        {
            if (property.Name != "alertEmail")
            {
                errors[property.Name] = ["Unknown property."];
            }
            else
            {
                (email, present) = ParseEmail(property, errors);
            }
        }

        if (!present && !errors.ContainsKey("alertEmail"))
            errors["alertEmail"] = ["Required; send null to clear the account alert email."];
        if (errors.Count > 0)
            return Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid", errors: errors);

        var userId = UserId(http);
        await settings.SetEmailAsync(userId, email, ct);
        LogWrite(http, logs, null, "account.alerting.update");
        return Json(new ApiAccountAlerting(await settings.GetEmailAsync(userId, ct), AccountNote));
    }

    /// <summary>Reads a nullable email property, recording validation errors; Present is false when the value has the wrong type.</summary>
    private static (string? Email, bool Present) ParseEmail(JsonProperty property, Dictionary<string, string[]> errors)
    {
        if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        {
            errors[property.Name] = ["Must be an email address string, or null to clear it."];
            return (null, false);
        }

        var email = property.Value.GetString();
        if (AlertSettingsValidator.ValidateEmail(email) is { } emailError)
            errors[property.Name] = [emailError];
        return (email, true);
    }

    // ---- monitor create / edit / delete ----

    private static async Task<IResult> CreateMonitorAsync(
        HttpContext http, ApplicationDbContext db, MonitorEditor editor, IHostEnvironment env, MonitorStatusService statuses,
        AlertRecipientResolver resolver, ILoggerFactory logs, CancellationToken ct)
    {
        var (body, bodyError) = await ReadBodyAsync(http, ct);
        if (bodyError is not null) return bodyError;

        var form = new MonitorForm();
        var errors = ApiMonitorBody.Apply(body, form, persistedType: null, out _, out _);
        if (errors.Count == 0)
            errors = ApiMonitorBody.Validate(form, env);
        if (errors.Count > 0)
            return ValidationProblem(errors);

        var m = await editor.CreateAsync(form, UserId(http), ct);
        LogWrite(http, logs, m.Id, "monitor.create");
        http.Response.Headers.Location = $"{Prefix}/monitors/{m.Id}";
        return await DetailAsync(m.Id, http, db, statuses, resolver, StatusCodes.Status201Created, includePingUrl: true, ct);
    }

    private static async Task<IResult> PatchMonitorAsync(
        Guid id, HttpContext http, ApplicationDbContext db, MonitorEditor editor, IHostEnvironment env, MonitorStatusService statuses,
        AlertRecipientResolver resolver, ILoggerFactory logs, CancellationToken ct)
    {
        if (!TryReadIfMatch(http, out var ifMatch, out var ifMatchError)) return ifMatchError!;
        var (body, bodyError) = await ReadBodyAsync(http, ct);
        if (bodyError is not null) return bodyError;

        var m = await db.Monitors.AsNoTracking()
            .Include(x => x.Headers).Include(x => x.MatchRules).AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return MonitorNotFound();
        if (ifMatch is not null && ifMatch != m.ConcurrencyStamp) return PreconditionFailed();

        // Merge patch: start from the persisted values and apply the properties that are present.
        var form = ApiMonitorBody.FromEntity(m, m.Headers.ToList(), m.MatchRules.ToList());
        var errors = ApiMonitorBody.Apply(body, form, m.Type, out var headersPresent, out var rulesPresent);
        if (errors.Count == 0)
            errors = ApiMonitorBody.Validate(form, env);
        if (errors.Count > 0)
            return ValidationProblem(errors);

        // Without If-Match the form still came from the snapshot above, so a change made since then must conflict instead of being reverted.
        switch (await editor.UpdateAsync(id, form, ifMatch ?? m.ConcurrencyStamp, replaceHeaders: headersPresent, replaceMatchRules: rulesPresent, ct))
        {
            case MonitorUpdateOutcome.NotFound:
                return MonitorNotFound();
            case MonitorUpdateOutcome.Conflict:
                return ifMatch is not null
                    ? PreconditionFailed()
                    : Problem(StatusCodes.Status409Conflict, "conflict", "The monitor changed while you were editing", "GET the monitor again and retry the request.");
        }

        LogWrite(http, logs, id, "monitor.update");
        return await DetailAsync(id, http, db, statuses, resolver, StatusCodes.Status200OK, includePingUrl: false, ct);
    }

    private static async Task<IResult> DeleteMonitorAsync(
        Guid id, HttpContext http, ApplicationDbContext db, MonitorEditor editor, ILoggerFactory logs, CancellationToken ct)
    {
        if (!TryReadIfMatch(http, out var ifMatch, out var ifMatchError)) return ifMatchError!;

        var m = await db.Monitors.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Name, x.ConcurrencyStamp }).FirstOrDefaultAsync(ct);
        if (m is null) return MonitorNotFound();
        if (ifMatch is not null && ifMatch != m.ConcurrencyStamp) return PreconditionFailed();

        switch (await editor.DeleteAsync(id, ifMatch, ct))
        {
            case MonitorDeleteOutcome.NotFound:
                return MonitorNotFound();
            case MonitorDeleteOutcome.Conflict:
                return PreconditionFailed();
        }

        // Deleting is irreversible, so it is logged at Warning with the name.
        logs.CreateLogger("Hawk.Api").LogWarning(
            "API write: user {UserId} key {KeyId} monitor {MonitorId} action {Action} name {Name}",
            UserId(http), http.User.FindFirstValue(ApiKeyAuthentication.KeyIdClaim), id, "monitor.delete", m.Name);
        return Results.NoContent();
    }

    private static string ETagOf(Guid stamp) => $"\"{stamp:D}\"";

    /// <summary>Reads an optional <c>If-Match</c>: the monitor's concurrencyStamp, quoted (as in the ETag) or bare. Anything else is a 400.</summary>
    private static bool TryReadIfMatch(HttpContext http, out Guid? stamp, out IResult? error)
    {
        stamp = null;
        error = null;
        var values = http.Request.Headers.IfMatch;
        if (values.Count == 0)
            return true;

        var raw = values.Count == 1 ? values[0]?.Trim() ?? string.Empty : string.Empty;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1];
        if (Guid.TryParseExact(raw, "D", out var parsed))
        {
            stamp = parsed;
            return true;
        }

        error = Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid",
            "If-Match must be the monitor's concurrencyStamp (the ETag of a previous response), quoted or bare.");
        return false;
    }

    private static IResult PreconditionFailed() => Problem(StatusCodes.Status412PreconditionFailed, "precondition_failed",
        "The monitor changed since you read it", "Its concurrencyStamp no longer matches If-Match. GET the monitor again and retry.");

    private static IResult ValidationProblem(Dictionary<string, List<string>> errors) =>
        Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid",
            errors: errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

    private static async Task<IResult> PauseResumeAsync(
        Guid id, string action, HttpContext http, Func<Guid, CancellationToken, Task<PauseResumeOutcome>> command,
        ILoggerFactory logs, CancellationToken ct)
    {
        var outcome = await command(id, ct);
        switch (outcome)
        {
            case PauseResumeOutcome.NotFound:
                return MonitorNotFound();
            case PauseResumeOutcome.Disabled:
                return Problem(StatusCodes.Status409Conflict, "monitor_disabled", "The monitor is disabled",
                    "Enable the monitor in the UI first.");
        }

        if (outcome == PauseResumeOutcome.Changed)
            LogWrite(http, logs, id, action);
        // The command reports only that nothing changed; the state after the call is paused/active by construction.
        var state = action == "pause" ? MonitorState.Paused : MonitorState.Active;
        return Json(new ApiPauseResumeResponse(outcome == PauseResumeOutcome.Changed, state));
    }

    private static async Task<IResult> RunAsync(Guid id, HttpContext http, MonitorCommands commands, ILoggerFactory logs, CancellationToken ct)
    {
        var result = await commands.QueueManualRunAsync(id, reuseInFlight: true, rejectDisabled: true, ct);
        switch (result.Outcome)
        {
            case ManualRunOutcome.NotFound:
                return MonitorNotFound();
            case ManualRunOutcome.Heartbeat:
                return Problem(StatusCodes.Status409Conflict, "heartbeat_not_runnable", "Heartbeat monitors cannot be run",
                    "A heartbeat monitor waits for its client to call the ping URL.");
            case ManualRunOutcome.Disabled:
                return Problem(StatusCodes.Status409Conflict, "monitor_disabled", "The monitor is disabled",
                    "Enable the monitor in the UI first.");
        }

        var runId = result.RunId!.Value;
        var runUrl = $"{Prefix}/monitors/{id}/runs/{runId}";
        if (result.Outcome == ManualRunOutcome.Queued)
            LogWrite(http, logs, id, "run");
        http.Response.Headers.Location = runUrl;
        return Json(new ApiRunQueued(runId, runUrl, PollAdvice), StatusCodes.Status202Accepted);
    }

    // ---- groups ----

    /// <summary>Reads the group name of a group endpoint body, rejecting unknown fields; <paramref name="extra"/> are the other allowed fields.</summary>
    private static async Task<(JsonElement Body, string? Group, IResult? Error)> ReadGroupBodyAsync(
        HttpContext http, ApplicationDbContext db, string? extra, CancellationToken ct)
    {
        var (body, bodyError) = await ReadBodyAsync(http, ct);
        if (bodyError is not null) return (default, null, bodyError);

        var errors = new Dictionary<string, string[]>();
        foreach (var property in body.EnumerateObject())
            if (property.Name != "group" && property.Name != extra)
                errors[property.Name] = ["Unknown property."];

        string? group = null;
        if (!body.TryGetProperty("group", out var value) || value.ValueKind != JsonValueKind.String ||
            MonitorGroups.Normalize(group = value.GetString()) is null)
            errors["group"] = ["Required: the name of an existing group, as a non-empty string."];
        else if (MonitorGroups.Validate(group) is { } groupError)
            errors["group"] = [groupError];
        else
            group = MonitorGroups.Normalize(group);

        if (errors.Count > 0)
            return (default, null, Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid", errors: errors));
        if (!await MonitorGroups.InGroup(db.Monitors, group!).AnyAsync(ct))
            return (default, null, Problem(StatusCodes.Status404NotFound, "not_found", "Group not found"));
        return (body, group, null);
    }

    private static async Task<IResult> GroupChangeAsync(
        string action, HttpContext http, ApplicationDbContext db, Func<IQueryable<MonitorEntity>, CancellationToken, Task<int>> command,
        ILoggerFactory logs, CancellationToken ct)
    {
        var (_, group, error) = await ReadGroupBodyAsync(http, db, extra: null, ct);
        if (error is not null) return error;

        var changed = await command(MonitorGroups.InGroup(db.Monitors, group!), ct);
        LogGroupWrite(http, logs, group!, $"group.{action}", changed);
        return Json(new ApiGroupChanged(changed));
    }

    private static async Task<IResult> GroupRunAsync(
        HttpContext http, ApplicationDbContext db, MonitorCommands commands, ILoggerFactory logs, CancellationToken ct)
    {
        var (_, group, error) = await ReadGroupBodyAsync(http, db, extra: null, ct);
        if (error is not null) return error;

        var result = await commands.QueueGroupRunAsync(group!, ct);
        LogGroupWrite(http, logs, group!, "group.run", result.Queued);
        return Json(new ApiGroupRun(result.Queued, result.AlreadyInFlight, result.Skipped));
    }

    private static async Task<IResult> GroupRenameAsync(
        HttpContext http, ApplicationDbContext db, MonitorCommands commands, ILoggerFactory logs, CancellationToken ct)
    {
        var (body, group, error) = await ReadGroupBodyAsync(http, db, extra: "newName", ct);
        if (error is not null) return error;

        if (!body.TryGetProperty("newName", out var value) || value.ValueKind != JsonValueKind.String)
            return Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid",
                errors: Errors("newName", "Required: the new group name, as a non-empty string."));

        var result = await commands.RenameGroupAsync(group!, value.GetString()!, ct);
        if (result.Error is not null)
            return Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid", errors: Errors("newName", result.Error));

        LogGroupWrite(http, logs, group!, "group.rename", result.Renamed);
        return Json(new ApiGroupRenamed(result.Renamed, result.NewName!));
    }

    private static void LogGroupWrite(HttpContext http, ILoggerFactory logs, string group, string action, int count) =>
        logs.CreateLogger("Hawk.Api").LogInformation(
            "API write: user {UserId} key {KeyId} group {Group} action {Action} count {Count}",
            UserId(http), http.User.FindFirstValue(ApiKeyAuthentication.KeyIdClaim), group, action, count);

    // ---- helpers ----

    private static string UserId(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private static string? UrlOf(MonitorEntity m) => m.Type == MonitorType.Http ? m.Url : null;

    private static ApiFailureSummary? FailureOf(MonitorStatus status) => status.Failure is { } f
        ? new ApiFailureSummary(f.LastRunAt, f.StatusCode, f.Error, f.ConsecutiveFailures, f.FailingSince)
        : null;

    private static async Task<ApiAlertRecipients> RecipientsAsync(AlertRecipientResolver resolver, MonitorEntity m, CancellationToken ct)
    {
        var recipients = await resolver.ResolveAsync(m, ct);
        return new ApiAlertRecipients(recipients.Source, recipients.Addresses);
    }

    // Queued/running runs hold placeholder values (success false, "Queued"); report them as not yet known.
    private static bool IsCompleted(MonitorRun r) => r.State is "completed" or "";

    private static ApiRunSummary RunSummary(MonitorRun r)
    {
        var completed = IsCompleted(r);
        return new ApiRunSummary(
            r.Id, completed ? "completed" : r.State, r.Reason, r.StartedAt,
            completed ? r.FinishedAt : null, completed ? r.DurationMs : null, completed ? r.Success : null,
            completed ? r.StatusCode : null, completed ? r.ErrorMessage : null);
    }

    private static JsonElement ParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }

    private static void LogWrite(HttpContext http, ILoggerFactory logs, Guid? monitorId, string action) =>
        logs.CreateLogger("Hawk.Api").LogInformation(
            "API write: user {UserId} key {KeyId} monitor {MonitorId} action {Action}",
            UserId(http), http.User.FindFirstValue(ApiKeyAuthentication.KeyIdClaim), monitorId, action);

    private static Dictionary<string, string[]> Errors(string field, string message) => new() { [field] = [message] };

    private static IResult Json<T>(T value, int status = StatusCodes.Status200OK) =>
        Results.Json(value, ApiJson.Options, statusCode: status);

    private static IResult MonitorNotFound() => Problem(StatusCodes.Status404NotFound, "not_found", "Monitor not found");

    private static IResult Problem(int status, string code, string title, string? detail = null, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new ProblemResult(status, code, title, detail, errors);

    private sealed class ProblemResult(
        int status, string code, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => ApiErrors.WriteProblemAsync(httpContext, status, code, title, detail, errors);
    }

    /// <summary>
    /// Reads a write body: <c>application/json</c>, at most 16 KB, a JSON object without duplicate property names.
    /// The handler parses it itself because minimal-API binding rejects bad bodies with an empty response before any filter runs.
    /// </summary>
    private static async Task<(JsonElement Body, IResult? Error)> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        IResult Invalid(string detail) =>
            Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid", detail);

        var request = http.Request;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !(contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
              contentType.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase)))
            return (default, Invalid("Send the body with Content-Type: application/json."));
        if (request.ContentLength > MaxBodyBytes)
            return (default, Invalid($"The body must be at most {MaxBodyBytes} bytes."));

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
                return (default, Invalid($"The body must be at most {MaxBodyBytes} bytes."));
        }

        if (buffer.Length == 0)
            return (default, Invalid("The body is empty; send a JSON object."));

        buffer.Position = 0;
        try
        {
            using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
            var root = doc.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
                return (default, Invalid("The body must be a JSON object."));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!seen.Add(property.Name))
                    return (default, Invalid($"Duplicate property '{property.Name}'."));
            return (root, null);
        }
        catch (JsonException)
        {
            return (default, Invalid("The body is not valid JSON."));
        }
    }
}
