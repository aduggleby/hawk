---
title: Heartbeat (Push) Monitors - Plan
type: feat
date: 2026-10-23
artifact_contract: ce-unified-plan/v1
execution: code
status: completed
---

# Heartbeat (Push) Monitors - Plan

## Goal Capsule

Add a second monitor type, **Heartbeat**: a dead man's switch for clients Hawk cannot reach, such as cron jobs or backups behind a firewall. The client calls a secret URL on Hawk. If no call arrives within the expected period plus a grace time, the monitor fails and uses the existing alerting (threshold, reminders, recovery, recipients).

Decisions made with the user (2026-10-23):

- The type is a field on the existing `Monitor`, not a separate entity.
- Clients authenticate with a secret token in the URL.
- The deadline is a period plus a grace time; there is no cron mode.
- v1 has success pings only: no `/fail`, no `/start`, no stored body.

## Product Contract

### User experience

- **Create**: the user picks the type (HTTP check / Heartbeat) when creating the monitor; it cannot change later. A heartbeat form shows Name, **Expected every** (period), **Grace**, the alert threshold, the alert email override and retention. URL, method, headers, match rules, body and timeout are hidden.
- **Details**: shows the ping URL `https://<host>/ping/<token>` with a copy button, a `curl` example (`curl -fsS -m 10 --retry 3 https://…/ping/<token>`), the last ping time and the current deadline. **Run now** is hidden.
- **Edit**: the period, grace and alert settings can change. **Regenerate URL** issues a new token, which invalidates the old URL (with a confirm prompt).
- **Index**: a `Heartbeat` badge. Missed heartbeats appear in the existing **Failing** group.
- **Run history**: every ping is an OK run; the source IP, method and User-Agent appear in diagnostics. Every missed period is a FAIL run with "No ping received since {LastPingAt}" (or "since monitoring started").
- **Alerts**: the existing failure, reminder and recovery emails, with heartbeat wording ("No ping received since …, expected every 1 h + 5 min grace"). The status-code column in the failure log is empty for missed runs.

### Requirements

- R1. `GET`, `HEAD` or `POST /ping/{token}` is anonymous and returns `200 OK` (plain text) for an active heartbeat and `404` for an unknown token. It needs no antiforgery token or cookie.
- R2. If no ping arrives by `lastPing + period + grace`, a failed run is recorded. While the monitor stays silent, another failed run is recorded every period, so `AlertAfterConsecutiveFailures` and reminders work as they do for HTTP monitors.
- R3. A ping after an alerted incident produces a recovery alert, as for HTTP monitors.
- R4. A new, imported or resumed heartbeat gets a full period plus grace before it can fail. It must not fail immediately.
- R5. Pings are rate-limited per token, so a runaway client cannot flood run history or the database.
- R6. Both SQL Server and PostgreSQL are supported, with migrations for each.
- R7. Pause, disable, delete, retention and export/import keep working for heartbeats.

### Period / grace values

- Period: 1 min, 5 min, 15 min, 1 h, 6 h, 12 h, 1 day, 7 days; plus 5 s in `Testing`.
- Grace: 1 min, 5 min, 15 min, 1 h, 6 h, 1 day; plus 0 s and 5 s in `Testing`.

### Non-goals (v1)

- Fail and start signals, job duration and captured request bodies.
- Cron-expression schedules and time zones.
- A token in a header, or hashed tokens.

## Architecture

Revised after the Codex (`gpt-6-astra`) plan review of 2026-10-23. Review notes: `/tmp/hawk-review/codex-plan-review.md`.

### Three invariants

1. **The scheduler never writes a heartbeat's `NextRunAt`.** For a heartbeat, `NextRunAt` is the deadline. Only these write it:
   - Create, import, resume, re-enable and Edit (when the period or grace changes): `now + period + grace`.
   - A ping: `now + period + grace`.
   - A recorded miss: `now + period`.
   The scheduler's due query stays as it is, but its set-based bump is filtered to `Type == Http`.
2. **Every heartbeat state change happens under the `hawk:monitor-alert:{id}` lock, after reloading.** Pings and missed checks acquire that Hangfire distributed lock first and then re-read the monitor and its alert state. Ordering therefore follows lock acquisition, not job scheduling.
3. **Tests that need `ExecuteUpdate` or `ExecuteDelete` run in `PostgresIntegrationTests`.** InMemory does not support them, so it is only for pure logic.

### Missed check (executor, `reason == "schedule"` on a heartbeat)

- When a heartbeat is overdue (`NextRunAt <= now`), the scheduler enqueues `RunAsync(id, "schedule")` on every tick until a job moves the deadline. Duplicate jobs are harmless: each one checks again under the lock.
- The job:
  1. Acquires the lock. On timeout it records nothing and exits; the next tick retries.
  2. Reloads the monitor and exits if it is paused, disabled or not due (`NextRunAt > now`). This covers a stale job after a resume, and a ping that arrived in between.
  3. Records a failed run: "No ping received by {deadline} (expected every {period} + {grace} grace)".
  4. Sets `NextRunAt = now + period` (set-based) and runs the shared alert evaluation (`OnFailure`).
- If the job throws, the deadline stays in the past and the next tick tries again.
- Because the scheduler doesn't touch heartbeats, they need no interval clamp in the scheduler. The 7-day period is defined in `MonitorIntervals` and the executor.

### Ping (inline in the request)

`GET|HEAD|POST /ping/{token}` is a minimal-API endpoint with `.AllowAnonymous()`, like `/live`. `AuthorizeFolder("/")` applies only to Razor Pages. There is no antiforgery check. The request:

1. Lowercases the token and checks it against `^[0-9a-f]{32}$`. Anything else returns 404 before any database lookup.
2. Looks up a heartbeat by `HeartbeatToken`. No match returns 404.
3. Acquires the per-monitor lock (5 s timeout) and reloads the monitor.
4. If the monitor is paused or disabled, returns `200 "paused"` and writes nothing.
5. Otherwise, in one transaction:
   - Sets `LastPingAt = now` and `NextRunAt = now + period + grace` set-based.
   - Adds a **completed** successful run (reason `ping`). Diagnostics hold the method, the path without the token, User-Agent, the client IP after forwarded headers, and the content type.
   - Runs `OnSuccess`, sends the recovery email if decided, and saves.
6. On lock timeout, it still writes `LastPingAt`/`NextRunAt` and inserts the run with the existing lock-busy `AlertError`. The client always gets 200, and recovery is evaluated on the next ping.
7. Returns `200 "OK"`.

Handling the ping inline means no queued ping runs can be stranded, and a delayed ping cannot be processed after a newer miss. The cost is that a recovery email is sent within the request. That is rare, and the email client's timeout is kept well below the 10 s `curl -m 10` in the example.

### Shared alert evaluation

`MonitorExecutor` branches on `Type` before URI validation, so heartbeats never reach `PersistInvalidConfigAsync`. The existing alert block (decider, send, stamp renewal, `SetLastRunAtAsync`, save) moves into one private method used by three paths: the HTTP check, the missed check and the ping. The ping path is exposed through a new `IMonitorExecutor.RecordPingAsync(...)` method. Heartbeat emails get their own wording, and their failure log lists times only.

`reason == "manual"` on a heartbeat returns null. The Details **Run now** button, the run diagnostics **Run again** button and the index batch **Run now** all exclude heartbeats; the batch reports queued and skipped counts.

**Inherited fix:** `MonitorAlertingDecider.OnSuccess` sets `wasAlertedIncident = state.LastFailureAlertSentAt is not null`. Currently, raising the threshold during an alerted incident suppresses the recovery email. `LastFailureAlertSentAt` is cleared on every success, so the new condition is exact.

### Token

- 128 random bits as 32 lowercase hex characters (`RandomNumberGenerator`, `Convert.ToHexStringLower`). This is provider-neutral: no case differences between SQL Server and PostgreSQL, and one representation for the limiter.
- Stored in plaintext with a unique index; NULL for HTTP monitors.
- Edit can regenerate it.

### Rate limiting

The `ping` category in `AuthAbuseProtection` applies to any method on `/ping/*`. It chains two limiters with `PartitionedRateLimiter.CreateChained`:

- per normalised token: `Hawk:Security:PingRequestsPerMinute`, default 30;
- per client IP: `Hawk:Security:PingRequestsPerMinutePerIp`, default 300.

The per-IP limit caps unknown-token floods from a single source. Rejected requests get 429 with a one-minute Retry-After fallback. Both settings are validated like the existing ones.

### Keep tokens out of logs

- **Serilog request logging**: `GetMessageTemplateProperties` replaces `RequestPath` with `/ping/{redacted}`.
- **ASP.NET Core hosting**: `Microsoft.AspNetCore.Hosting.Diagnostics` is set to `Warning`, so "Request starting" lines with the raw path are not logged. Serilog request logging already covers request logs.
- **Exceptions**: the ping endpoint catches its own exceptions and logs the monitor id, never the path.
- **Caddy (infra repo)**: `log_skip /ping/*` in `apps/hawk/site.caddy`, supported in Caddy 2.11. fail2ban then cannot see ping floods, which is acceptable because the app rate-limits them. This is an infra repo change, committed and pushed before deploy.
- **Accepted residual risk** (Codex implementation review, 2026-10-23): when the upstream is unreachable, Caddy's error logger on the edge VM can still log the request URI, including the token. That log is only readable by admins of the edge VM, and a leaked token only allows faked pings (masking an outage). The token can be regenerated in Edit. The Error page and the request log redact `/ping/*`.

## Code Changes

### Data model (`Hawk.Web/Data/Monitoring/Monitor.cs`)

| Field | Type | Notes |
|---|---|---|
| `Type` | `MonitorType` enum (`Http = 0`, `Heartbeat = 1`), int | Default `Http`; existing rows unchanged |
| `HeartbeatToken` | `string?`, MaxLength 32 | Lowercase hex; unique index (nullable; EF adds the SQL Server filter automatically) |
| `HeartbeatGraceSeconds` | `int` | Default 0 |
| `LastPingAt` | `DateTimeOffset?` | UTC; written set-based |

- `IntervalSeconds` is the period.
- Heartbeats store `Url = ""` and `Method = "GET"`.
- Two migrations named `AddHeartbeatMonitors`. The migration is additive, but the backup step in AGENTS.md still applies.

### Files

- `Services/Monitoring/MonitorIntervals.cs`: heartbeat period and grace lists.
- `Services/Monitoring/HeartbeatTokens.cs` (new): `Generate()`, `TryNormalize(string, out string)`, `InitialDeadline(now, period, grace)`.
- `Services/Monitoring/MonitorScheduler.cs`: the bump is filtered to Http; the due query is unchanged.
- `Services/Monitoring/MonitorExecutor.cs` and `IMonitorExecutor`: the type branch, shared alert evaluation, the missed path, `RecordPingAsync` and heartbeat email wording.
- `Services/Monitoring/MonitorAlertingDecider.cs`: the recovery fix.
- `Infrastructure/HeartbeatEndpoints.cs` (new): `MapHawkHeartbeats()`.
- `Infrastructure/AuthAbuseProtection.cs` and `HawkSecurityOptions`: the ping limiters and settings.
- `Program.cs` and `appsettings.json`: endpoint mapping, request-log redaction, and the hosting log level.
- `Pages/Monitors/MonitorForm.cs`:
  - `Url` becomes `string?` with no `[Required]`, so empty heartbeat URLs bind without implicit required errors.
  - Adds `Type` and `GraceSeconds`.
  - `Validate` requires the URL only for Http and checks heartbeat period and grace against their lists.
  - Bulk import (`MonitorJsonPort.TryCreateMonitor`) goes through the same path.
- `Pages/Monitors/Create.cshtml(.cs)` and `Edit.cshtml(.cs)` (parity rule):
  - Create gets a type selector; JS shows and hides the HTTP and heartbeat sections.
  - Edit takes the type from the **persisted** entity, never from the form.
  - Create sets the token and the deadline.
  - Edit resets the deadline when the period or grace changes or the monitor is re-enabled, and has a `Regenerate` POST handler.
- `Pages/Monitors/Details.cshtml(.cs)`:
  - The ping URL comes from `Request.Scheme`/`Request.Host`, with a copy button.
  - `curl -fsS -m 10 --retry 3 https://…` (https, because Caddy redirects plain http and `curl -f` without `-L` fails).
  - Shows the last ping and the deadline.
  - Run now is hidden, and the handler rejects heartbeats.
- `Pages/Monitors/Index.cshtml(.cs)`:
  - Heartbeat badge.
  - Resume/enable updates set the heartbeat deadline, grouped by `(IntervalSeconds, HeartbeatGraceSeconds)` with precomputed UTC values (as the scheduler does), so there are no date expressions to translate.
  - Batch Run now covers Http monitors only and reports skipped heartbeats.
- `Pages/Monitors/Runs/Details.cshtml(.cs)`: a "Ping" label for request diagnostics, and Run again hidden for heartbeats.
- `Pages/Monitors/MonitorJsonPort.cs`: `CurrentVersion = 3`, with `type`, `heartbeatGraceSeconds` and `heartbeatToken`; the parser accepts versions 1 to 3. Single import (Create prefill) always issues a new token.
- `Services/Import/MonitorTransferService.cs`, heartbeat identity:
  - **With a token**: a duplicate only if the token already exists in the database or earlier in the file. The same 128-bit token means the same monitor, so a re-import is skipped and a move keeps the URL.
  - **Without a token** (or with an invalid one): dedupe by trimmed Name (the empty URL makes Name + Url effectively Name only) and issue a new token.
  - Imported heartbeats get a deadline.
- `Services/Import/StatusCakeImporter.cs`: monitor matching and deduplication are restricted to `Type == Http`.
- `Data/Seeding/MonitorSeeder.cs`: a paused `[Seed] Heartbeat`.
- `AGENTS.md`: a "Heartbeat Monitors" section, cheatsheet entries and version 0.10.0.

## Verification

- **Unit, InMemory or pure** (`Hawk.Tests`):
  - Decider recovery after the threshold is raised.
  - `MonitorForm.Validate` per type; an Http monitor with an empty URL is rejected.
  - `HeartbeatTokens` normalising tokens and rejecting malformed ones.
  - `MonitorJsonPort` v3 round trip and v2 compatibility.
  - `MonitorTransferService.Plan` heartbeat identity: same-name heartbeats with different tokens both import; a re-import is skipped; one without a token dedupes by name.
  - Limiter configuration validation.
- **PostgreSQL integration** (Testcontainers; run with `HAWK_REQUIRE_POSTGRES_TESTS=1`), using the real executor and endpoint:
  - Ping, then miss, then ping again: OK, then FAIL with an alert, then recovery.
  - A stale missed job after a resume does nothing.
  - A ping between the deadline and the missed job leaves no failure.
  - The scheduler leaves heartbeat `NextRunAt` alone.
  - A ping while paused writes nothing.
  - The unique token index, and UTC `timestamptz` values.
  - Unknown token returns 404; a malformed token returns 404 with no query.
  - 429 after the per-token limit.
  - StatusCake history import ignores heartbeats.
  - Batch Run now skips heartbeats.
- **Migrations**: `DatabaseSetupTests.Migrations_are_up_to_date_with_the_model` for both providers.
- **E2E** (`e2e/tests/heartbeat.spec.ts`), in both the SQL Server and Postgres stacks:
  1. Create a heartbeat with a 5 s period and 0 s grace, and check the URL on Details.
  2. Ping it with `request.get` and see an OK run.
  3. Wait and see FAIL plus the failure email in the mock `/emails`.
  4. Ping again and see the recovery email.
  5. Run now is absent.
- **Gate before deploy**:
  - `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test Hawk.sln -v:m`
  - The SQL Server and Postgres e2e runs.
  - An independent Codex review of the diff against the review findings.

## Release

- Version 0.10.0 (a new monitor type, a schema migration and export format v3).

## Resolved Decisions (2026-10-23)

1. **Token export**: the token is exported. On import, an unused token is kept (a move keeps client URLs). A token that already exists means the same monitor, so it is skipped as a duplicate; this replaces the "new token + warning" idea from the review.
2. **Pings while paused or disabled**: return 200 and record nothing (`LastPingAt` is not updated either).
3. **Token storage**: plaintext with a unique index.
4. **HTTP intervals**: unchanged. Heartbeat periods (up to 7 days) live outside the scheduler clamp.
5. **Path**: `/ping/{token}`.
6. **Version**: 0.10.0.

## Production Deployment — 2026-10-23

- Version **0.10.0**; source commit `fcef699247ef2389252fda18dc2d0cf3e78310c5`.
- Image `registry.dualconsult.net/dualconsult/hawk@sha256:e111226e121f82ad7953d6f7a24ba8f08e706ca3aa2dfb42386ef12a5c584c5c`, built from `git archive HEAD`.
- Release pin and Caddy `log_skip /ping/*`: infra commit `629df55`. Deployed with `scripts/deploy-app.sh hawk app edge`.
- Verification before the deploy:
  - `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test`: 161/161 passed.
  - E2E: 13/13 on PostgreSQL and 13/13 on SQL Server.
  - Two Codex (`gpt-6-astra`) implementation reviews; the second found no remaining code defects.
- Live checks:
  - Migration `AddHeartbeatMonitors` applied on PostgreSQL.
  - The container runs 0.10.0 with 0 restarts, UID 1654 and a read-only root filesystem.
  - `/live` returns 200; public `/ready` returns 404.
  - `/ping/<unknown token>` and `/ping/<malformed>` return 404.
  - No warnings or errors in the app log, and test tokens appear in neither the app log nor the Caddy access log.
- Rollback: restore the previous pin (`sha256:c855af1e905e95588c21412d3754bb0fa527e63453610283011c6838a0ce7266`, 0.9.30) and rerun the app deploy. The migration only adds columns, so 0.9.30 still runs against the migrated schema.
