# Hawk Agent API (`/api/v1`)

A JSON API for agents and scripts. It lets a caller see which monitors are failing and why, change who is alerted and when, pause or resume monitors, and queue checks. Hawk version 0.11.0 and later.

## Authentication

- Create a key in the UI under Account -> API keys (`/Identity/Account/Manage/ApiKeys`). Choose a name and a scope. The plaintext key (`hawk_` followed by 43 characters) is shown once, right after creation. Hawk stores only a SHA-256 hash.
- Send the key as `Authorization: Bearer hawk_...`. A key in the query string is ignored and the request gets 401. A browser login cookie never authorizes `/api` requests.
- Scopes: `read` allows every `GET`. `write` also allows `PATCH`, `PUT` and `POST`.
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

The list entry plus configuration, alert state and effective alert recipients. The heartbeat ping token is never returned. Extra fields: `method`, `intervalSeconds`, `timeoutSeconds`, `allowedStatusCodes`, `alertAfterConsecutiveFailures`, `alertEmailOverride`, `runRetentionDays`, `heartbeatGraceSeconds` (heartbeats only), `lastPingAt`, `createdAt`, `alertState` (`consecutiveFailures`, `failureIncidentOpenedAt`, `lastFailureAlertSentAt`, `lastFailureAlertError`, `pendingRecoveryAlert`, `lastRecoveryAlertSentAt`, `lastRecoveryAlertError`) and `alertRecipients` (`{ "source": "monitor|account|owner|admins", "addresses": [...] }`).

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
3. `PATCH .../alerting` to change the threshold or recipient, `POST .../pause` while fixing, `POST .../run` to verify, `POST .../resume` afterwards.
