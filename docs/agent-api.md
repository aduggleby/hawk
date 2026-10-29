# Hawk Agent API (`/api/v1`)

A JSON API for agents and scripts. It lets a caller see which monitors are failing and why, change who is alerted and when, pause or resume monitors, queue checks, and create, edit or delete monitors. Hawk version 0.11.0 and later; creating, editing and deleting monitors needs 0.12.0.

## Authentication

- Create a key in the UI under Account -> API keys (`/Identity/Account/Manage/ApiKeys`). Choose a name and a scope. The plaintext key (`hawk_` followed by 43 characters) is shown once, right after creation. Hawk stores only a SHA-256 hash.
- Send the key as `Authorization: Bearer hawk_...`. A key in the query string is ignored and the request gets 401. A browser login cookie never authorizes `/api` requests.
- Scopes: `read` allows every `GET`. `write` also allows `PATCH`, `PUT`, `POST` and `DELETE`.
- A key acts as the user who created it. It sees every monitor, as the UI does, and has no admin powers (`ownedByCaller` tells whether the caller created the monitor).
- Keys are revoked one at a time or all at once on the API keys page. A revoked key gets 401 on its next request. Resetting the user's password (by an admin or through Forgot password) revokes all of that user's keys. A password change or a lockout leaves keys working. Deleting a user deletes their keys.

```bash
export HAWK=https://hawk.example.com
export KEY=hawk_...
curl -fsS -H "Authorization: Bearer $KEY" $HAWK/api/v1
```

## Conventions

- Requests with a body need `Content-Type: application/json` (or a `+json` type), at most 16384 bytes, and a JSON object without duplicate property names.
- Property names are camelCase. Enum values are camelCase strings. Timestamps are UTC ISO 8601. Nulls are kept in responses so shapes stay stable.
- Every response has `Cache-Control: no-store`.
- Unknown properties in a write body are rejected with 400.

### Errors

Every `/api/*` error is `application/problem+json` with a stable `code`:

```json
{ "type": "about:blank", "title": "The request is not valid", "status": 400, "detail": null,
  "code": "validation_failed", "errors": { "alertAfterConsecutiveFailures": ["Alert threshold must be between 1 and 20."] } }
```

| Status | `code` | Meaning |
|---|---|---|
| 400 | `validation_failed` | Bad body, wrong content type, unknown property, invalid value, bad `limit`. `errors` lists fields where applicable. |
| 401 | `unauthorized` | Missing, malformed, unknown or revoked key. Has `WWW-Authenticate: Bearer`, never a redirect. |
| 403 | `insufficient_scope` | A read key on a write endpoint. |
| 404 | `not_found` | Unknown monitor or run, or an unknown `/api` path. |
| 405 | `method_not_allowed` | Wrong method for the path. |
| 409 | `monitor_disabled` | The monitor is disabled (enable it in the UI). |
| 409 | `heartbeat_not_runnable` | Run requested for a heartbeat monitor. |
| 409 | `conflict` | A `PATCH` without `If-Match`: the monitor changed while you were editing (someone saved, paused or resumed it since the request read it). Nothing was changed; read the monitor again and retry. |
| 412 | `precondition_failed` | `If-Match` does not match the monitor's current `concurrencyStamp`. Nothing was changed. |
| 429 | `rate_limited` | Over a rate limit. Honour `Retry-After` (seconds). |
| 500 | `internal_error` | Unexpected failure. |

### Rate limits

Fixed one-minute windows. Per key: `Hawk__Security__ApiRequestsPerMinute` (default 120, range 1-6000). Per client IP, for any `/api/*` request: `Hawk__Security__ApiRequestsPerMinutePerIp` (default 600, range 1-60000). Over the limit returns 429 `rate_limited` with `Retry-After`.

## Monitor model

- `type`: `http` or `heartbeat`.
- `state`: `disabled` (not enabled), else `paused`, else `active`.
- `health`, first rule that applies:
  1. `overdue` (enabled and unpaused monitors only): a heartbeat whose deadline passed, or an HTTP monitor whose last run is older than twice its interval plus its timeout plus one scheduler tick (checks have stopped running).
  2. `failing`: the last completed run failed.
  3. `ok`: the last completed run succeeded.
  4. `unknown`: no completed run (a queued or running run does not count).
  A paused monitor whose last run failed is `state: paused, health: failing`.
- Right after resuming (or re-enabling) an HTTP monitor that was paused longer than the overdue window, `health` may briefly read `overdue` until its next scheduled run.
- `failure` (null unless the last completed run failed): `{ lastRunAt, statusCode, error, consecutiveFailures, failingSince }`.

## Endpoints

### GET /api/v1 (read)

The caller, the key and the endpoint list.

```json
{ "user": { "id": "…", "email": "ad@dualconsult.com" },
  "key": { "id": "…", "name": "Claude Code", "scope": "write" },
  "endpoints": [ { "method": "GET", "path": "/api/v1", "scope": "read", "description": "…" } ] }
```

### GET /api/v1/monitors (read)

All monitors, sorted by name (case-insensitive), in one call.

```json
{ "monitors": [ { "id": "…", "name": "Web", "type": "http", "url": "https://example.com",
  "state": "active", "health": "failing", "ownedByCaller": true,
  "failure": { "lastRunAt": "2026-10-29T08:00:00+00:00", "statusCode": 500, "error": null, "consecutiveFailures": 3, "failingSince": "2026-10-29T07:58:00+00:00" },
  "lastRunAt": "…", "nextRunAt": "…" } ] }
```

`url` is null for heartbeat monitors.

### GET /api/v1/monitors/{id} (read)

The list entry plus configuration, alert state and effective alert recipients. The heartbeat ping token is never returned. Extra fields: `concurrencyStamp` (also the `ETag` response header, quoted), `pingUrl` (always null here), `method`, `intervalSeconds`, `timeoutSeconds`, `allowedStatusCodes`, `followRedirects` (HTTP only, null for heartbeats), `alertAfterConsecutiveFailures`, `alertEmailOverride`, `runRetentionDays`, `heartbeatGraceSeconds` (heartbeats only), `lastPingAt`, `createdAt`, `alertState` (`consecutiveFailures`, `failureIncidentOpenedAt`, `lastFailureAlertSentAt`, `lastFailureAlertError`, `pendingRecoveryAlert`, `lastRecoveryAlertSentAt`, `lastRecoveryAlertError`) and `alertRecipients` (`{ "source": "monitor|account|owner|admins", "addresses": [...] }`), `headers` (`[{ "name", "value" }]`) and `matchRules` (`[{ "type": "contains|regex", "pattern" }]`), both as the Details page shows them, sorted deterministically and empty for heartbeats. A rule's mode is reported as `type`; write requests call it `mode` (and also accept `type`, so a value read from here can be sent back unchanged).

Errors: 404 `not_found`.

### GET /api/v1/monitors/{id}/runs?limit= (read)

Recent runs, newest first. `limit` defaults to 25 and is capped at 100. A non-numeric or less-than-1 `limit` returns 400.

```json
{ "runs": [ { "id": "…", "state": "completed", "reason": "schedule", "startedAt": "…", "finishedAt": "…",
  "durationMs": 120, "success": false, "statusCode": 500, "error": null } ] }
```

For runs that are still `queued` or `running`, `finishedAt`, `durationMs`, `success`, `statusCode` and `error` are null.

### GET /api/v1/monitors/{id}/runs/{runId} (read)

One run with diagnostics: the summary fields plus `requestUrl`, `requestMethod`, `requestContentType`, `requestTimeoutMs`, `requestHeaders`, `requestBodySnippet`, `responseHeaders`, `responseContentType`, `responseContentLength`, `responseSnippet`, `matchResults`, `alertSent`, `alertError`. Returns 404 if the run belongs to a different monitor.

### POST /api/v1/monitors (write)

Creates an HTTP or heartbeat monitor owned by the key's user (enabled by default, not paused). Returns 201 with a `Location` header, an `ETag` header and the monitor detail. The body is validated exactly like the Create form: field rules, environment-dependent intervals, regular expressions, NUL characters, and the header and pattern length limits of bulk import. Errors are 400 `validation_failed` with `errors` keyed by the field names below (`headers[0].name`, `matchRules[1].pattern`; `request` for a problem with the whole body).

Fields (names as in the monitor JSON export): `name` (required), `type` (`http` default, or `heartbeat`), `url` (HTTP, required), `method` (`GET` default, or `POST`), `enabled` (default true), `timeoutSeconds` (default 15), `intervalSeconds` (default 60; the allowed values are the UI's: 1 min, 5 min, 15 min or 1 h for HTTP, 1 min to 7 days for a heartbeat period, grace 1 min to 1 day), `heartbeatGraceSeconds` (heartbeat, default 300), `alertAfterConsecutiveFailures` (default 1), `alertEmailOverride`, `allowedStatusCodes` (for example `"404,429"`), `followRedirects` (default true), `runRetentionDays`, `contentType` (default `application/json`, required for POST), `body` (required for POST, may be empty), `headers` (`[{ "name", "value" }]`, at most 5) and `matchRules` (`[{ "mode": "contains|regex", "pattern" }]`, at most 5; the Create and Edit forms have 5 slots of each and would silently drop more, so more return 400). A blank `alertEmailOverride` clears it. The defaults are the Create form's. HTTP-only fields on a heartbeat, and `heartbeatGraceSeconds` on an HTTP monitor, return 400. `heartbeatToken`, `ownerEmail` and `isPaused` are always rejected: the server issues tokens, the caller is the owner, and pause and resume have their own endpoints. Unknown properties return 400.

```bash
curl -fsS -X POST -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"name":"Website","url":"https://example.com","intervalSeconds":300,"matchRules":[{"mode":"contains","pattern":"Example"}]}' \
  $HAWK/api/v1/monitors
```

For a heartbeat the response also has `pingUrl` (`https://host/ping/<token>`). **This is the only response that contains the ping token**; store it, because `GET` never returns it and the API cannot regenerate it (use the UI's Regenerate URL if it is lost). The deadline starts at now + period + grace.

```bash
curl -fsS -X POST -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"name":"Nightly backup","type":"heartbeat","intervalSeconds":86400,"heartbeatGraceSeconds":3600}' \
  $HAWK/api/v1/monitors
# ... && curl -fsS -m 10 --retry 3 "$PING_URL"
```

### PATCH /api/v1/monitors/{id} (write)

Edits a monitor with merge-patch semantics: an absent field is unchanged, `null` clears `alertEmailOverride`, `allowedStatusCodes`, `contentType`, `body` and `runRetentionDays`. `headers` and `matchRules` replace the whole list when present and are untouched when absent. `type` cannot change: sending a different one returns 400 (the same value is accepted). Validation is the same as for create, applied to the merged result; nothing changes on an error. Returns 200 with the updated detail and the new `ETag`.

- Like the Edit page, saving an HTTP monitor schedules its next run one interval from now. A heartbeat's deadline moves only when the period, the grace or `enabled` (false to true) changes.
- Every successful edit renews `concurrencyStamp`, so an open Edit form in the UI reports a conflict.

```bash
curl -fsS -X PATCH -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"intervalSeconds":60,"headers":[{"name":"Authorization","value":"Bearer x"}]}' $HAWK/api/v1/monitors/$ID
```

### Optimistic concurrency: ETag and If-Match

`GET /monitors/{id}`, `POST /monitors` and `PATCH /monitors/{id}` return `ETag: "<concurrencyStamp>"` and the same value as `concurrencyStamp` in the body. `PATCH` and `DELETE` accept an optional `If-Match` header with that value, quoted or bare. If it no longer matches, the call returns 412 `precondition_failed` and changes nothing; read the monitor again and retry. A malformed `If-Match` (`*`, a weak `W/` tag, a list, a non-GUID) returns 400. Without `If-Match` the request still conflicts if the monitor was changed by someone else after the request read it: you get 409 `conflict` (the monitor changed while you were editing; read it again and retry) and the other change is kept, rather than it being overwritten with stale values. Only writes that are not racing are unconditional. Pause, resume and alert changes also renew the stamp; run results and scheduler bookkeeping do not.

### Checking a redirect

By default a check follows redirects and judges the final response. Set `followRedirects: false` to judge the redirect itself, for example to assert that an old URL permanently redirects. A 3xx counts as success only when its code is in `allowedStatusCodes` (2xx always succeeds). The `Location` header is in the run's response headers (`GET /monitors/{id}/runs/{runId}` diagnostics). `contains` and `regex` match rules are evaluated against the body of the 3xx response itself when not following.

```bash
curl -fsS -X POST -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"name":"old-url redirects","url":"https://example.com/old","followRedirects":false,"allowedStatusCodes":"301","matchRules":[{"mode":"contains","pattern":"moved"}]}' \
  $HAWK/api/v1/monitors
```

### DELETE /api/v1/monitors/{id} (write)

Deletes the monitor together with its headers, match rules, run history and alert state. **This cannot be undone**: there is no soft delete and no restore. Returns 204 with no body, or 404 `not_found` if the monitor does not exist (a second delete is a 404). Any write key can delete any monitor, as any signed-in user can in the UI. The delete is logged at Warning with the monitor name.

```bash
curl -fsS -X DELETE -H "Authorization: Bearer $KEY" -H 'If-Match: "<concurrencyStamp>"' $HAWK/api/v1/monitors/$ID
```

### PATCH /api/v1/monitors/{id}/alerting (write)

Merge patch of `alertAfterConsecutiveFailures` (integer 1-20) and `alertEmailOverride` (email string, at most 320 characters). A field that is absent stays unchanged. `alertEmailOverride: null` or a blank string clears the override. `alertAfterConsecutiveFailures: null`, an unknown property or an invalid value returns 400 and changes nothing. `{}` changes nothing and returns 200.

```bash
curl -fsS -X PATCH -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"alertAfterConsecutiveFailures": 3}' $HAWK/api/v1/monitors/$ID/alerting
```

Response: `{ "alertAfterConsecutiveFailures": 3, "alertEmailOverride": "ops@x.com", "alertRecipients": { "source": "monitor", "addresses": ["ops@x.com"] } }`.

The change renews the monitor's concurrency stamp, so an open Edit form reports a conflict instead of overwriting it.

### GET /api/v1/account/alerting (read) and PUT (write)

The caller's account-wide alert email. `PUT` body: `{ "alertEmail": "a@b.c" }`; `alertEmail` is required and `null` clears it.

```json
{ "alertEmail": "a@b.c", "appliesTo": "monitors owned by the caller" }
```

The setting applies only to monitors the caller owns (those created by the caller), not to every monitor the key can see.

### POST /api/v1/monitors/{id}/pause and /resume (write)

No body. Works for HTTP and heartbeat monitors. Resuming a heartbeat sets its deadline to now + period + grace.

```json
{ "changed": true, "state": "paused" }
```

A monitor already in the target state returns 200 with `changed: false`. A disabled monitor returns 409 `monitor_disabled`.

### POST /api/v1/monitors/{id}/run (write)

Queues a run like the UI's **Run now** (reason `manual`) and returns 202 with a `Location` header:

```json
{ "runId": "…", "runUrl": "/api/v1/monitors/…/runs/…", "pollAdvice": "Poll runUrl until its state is 'completed'; stop after the monitor's timeoutSeconds plus 60 seconds." }
```

- Paused monitors can be run. Disabled monitors return 409 `monitor_disabled`, heartbeats 409 `heartbeat_not_runnable`.
- If a manual run started within the monitor timeout plus 60 seconds is still queued or running, that run's id is returned instead of queuing another. An older unfinished run is treated as lost and a new one is queued.
- Poll `runUrl` (for example every 2 seconds) until `state` is `completed` (states: `queued`, `running`, `completed`), and give up after `timeoutSeconds` plus 60 seconds. A completed run carries `success`, `statusCode` and `error`.

```bash
RUN=$(curl -fsS -X POST -H "Authorization: Bearer $KEY" $HAWK/api/v1/monitors/$ID/run | jq -r .runUrl)
until [ "$(curl -fsS -H "Authorization: Bearer $KEY" $HAWK$RUN | jq -r .state)" = completed ]; do sleep 2; done
curl -fsS -H "Authorization: Bearer $KEY" $HAWK$RUN | jq '{success, statusCode, error}'
```

## Typical agent flow

1. `GET /api/v1/monitors`, filter on `health` in (`failing`, `overdue`).
2. `GET /api/v1/monitors/{id}` and `.../runs` for the cause (`failure`, `alertState`, run diagnostics).
3. `PATCH .../alerting` (or `PATCH /monitors/{id}` for any setting) to change the threshold or recipient, `POST .../pause` while fixing, `POST .../run` to verify, `POST .../resume` afterwards.

## Setting up monitoring

1. `POST /api/v1/monitors` for each URL or job (heartbeat). Keep a heartbeat's `pingUrl`.
2. `POST .../run` on an HTTP monitor to check it works.
3. Change the configuration later with `PATCH /api/v1/monitors/{id}`; remove a monitor you no longer need with `DELETE` (irreversible).
