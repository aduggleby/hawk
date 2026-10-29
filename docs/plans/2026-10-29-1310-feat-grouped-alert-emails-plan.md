---
title: Grouped alert emails (digest)
type: feat
status: completed
date: 2026-10-29
product_contract_source: ce-plan-bootstrap
target_version: 0.15.0
---

# Grouped alert emails (digest)

## Product Contract

### Summary

Alert emails are held briefly and sent as one summary per recipient once alerts stop arriving for 30 s (never later than 2 min after the first). A single held alert is sent as today's detailed email.

### Problem Frame

A shared dependency failing makes many monitors fail in the same scheduler tick, and each one sends its own email; the recovery produces a second burst. The user gets a dozen mails within seconds and has to piece the incident together. One incident should produce one readable email, without real outages arriving noticeably later.

### Requirements

- R1. Failure, failure-reminder and recovery alerts (HTTP and heartbeat) are queued instead of sent inline when grouping is on.
- R2. A recipient's queued alerts are sent once no new alert for that recipient set arrived for `Hawk:Alerting:DigestWindowSeconds` (default 30), and at the latest `Hawk:Alerting:DigestMaxDelaySeconds` (default 120) after the oldest queued alert.
- R3. One queued alert: send exactly today's email (subject and body). Two or more: one summary email.
- R4. The summary lists every event in time order, so a monitor that failed and recovered inside the window shows both, followed by each alert's full details.
- R5. `DigestWindowSeconds = 0` restores today's immediate per-alert sending.
- R6. Queued alerts survive restarts; a failed send is retried by the queue (not by the next monitor run) and given up after 24 h.
- R7. Run diagnostics show whether the alert was queued, sent (and when) or failed.

### Key Decisions

- Sliding 30 s window with a 2 min cap (session-settled: user-approved — chosen over a 5 min cap and a fixed non-sliding 30 s window: bounded delay while still collapsing bursts).
- Single alert keeps today's email (session-settled: user-approved — chosen over always using the summary layout).
- Flaps show both events (session-settled: user-approved — chosen over net state only).
- Server config only (session-settled: user-approved — chosen over an additional per-user setting).
- Grouping is per recipient set; alert state counts an alert as handled once queued; the queue retries failed sends (confirmed in the scoping synthesis).

### Scope Boundaries

- No per-user settings, no UI for the queue, no Slack/webhook channels.
- Identity emails (confirmation, password reset) are unaffected.

## Planning Contract

### Key Technical Decisions

- **Outbox table `PendingAlerts`** written in the same `SaveChanges`/transaction as the run and alert state (all three alert paths already save after `EvaluateAlertsAsync`), so state and queue cannot diverge. No FK to `Monitors`: a monitor deleted during the window still gets its alert sent. Stores the fully rendered single-alert subject/HTML plus summary fields (kind, monitor name, short detail, time), so flushing needs no monitor data.
- **Recipients** stored as the normalized (trimmed, lowercased, distinct, sorted) comma-joined list; groups are formed in memory (provider-agnostic).
- **Flush** = `AlertDigestSender.FlushAsync` under the Hangfire lock `hawk:alert-digest` (busy = skip). Each enqueue schedules a delayed flush at `now + window`; a minutely recurring job `hawk-alert-digest` is the safety net and retry driver (always registered: pings and manual runs queue alerts even with the scheduler off; a busy lock schedules a 10 s retry). A group is due when `newest + window <= now` or `oldest + maxDelay <= now`; if groups remain, the flush schedules itself for the earliest due time.
- **Send-ability checks stay at enqueue time** (email disabled, no From, no recipients) and keep writing today's `AlertError`, so misconfiguration is still visible on the run and still retried by the next run.
- **Run status**: at enqueue `AlertSent = true`, `AlertError = null` (queued counts as handled, matching the alert state and `SeedFromHistory`). The diagnostics page looks up the `PendingAlert` by `RunId` (queued / sent alone or in a summary / retrying / failed). When the outbox gives up after 24 h it sets the run back to `AlertSent = false` with the error.
- **Retention**: sent/failed rows are deleted after 7 days by the flush.

### Implementation Units

1. **Model + migrations** — `Hawk.Web/Data/Alerting/PendingAlert.cs`, `ApplicationDbContext` (DbSet, index on `SentAt`/`FailedAt`), migration `AddPendingAlerts` in both sets.
2. **Options** — `AlertDigestOptions` (read + clamp: window 0–600, max delay window–3600).
3. **Executor** — `MonitorExecutor` builds subject/HTML as today, then `DeliverAsync`: window 0 sends inline (today's code), else adds a `PendingAlert` and schedules a flush after save.
4. **Digest sender** — `Hawk.Web/Services/Monitoring/AlertDigestSender.cs`: pure `Plan` (due groups) and `BuildDigest` (subject + HTML), `FlushAsync`, `EnsureStarted`.
5. **Diagnostics** — `Pages/Monitors/Runs/Details` shows queued/sent/failed.
6. **Config/docs** — e2e compose files set window 0 (existing email assertions expect immediate mail), AGENTS.md, CHANGELOG, version 0.15.0.

### Test Scenarios (`Hawk.Tests/AlertDigestTests.cs`, Postgres integration)

- Plan: group not due inside the window; due after window; due by max delay while alerts keep arriving; separate recipient sets are separate groups; next-check time is the earliest due time.
- Digest: 1 row = stored subject/HTML unchanged; 2+ rows = subject counts failures/recoveries, events in time order, fail+recover of one monitor both listed, names HTML-encoded.
- Flush (InMemory sender stub): sends one email per group, marks rows sent, sets run `AlertSent`; send exception keeps rows unsent with error and attempts; rows older than 24 h unsent are marked failed.
- Executor: window 0 sends inline as before; window > 0 queues and updates alert state (no second failure alert on the next run).
- Migrations up to date for both providers (existing test).

### Risks

- Crash between send and marking sent re-sends the group once (acceptable; at-least-once).
- Rollback to 0.14.0: the table is additive; delete the `hawk-alert-digest` recurring job and scheduled `IAlertDigestSender` jobs; unsent queued alerts are lost.

## Production Deployment (0.15.0), 2026-10-29

- Source commit `c2cd3ab` (pushed to GitHub `main`); image `registry.dualconsult.net/dualconsult/hawk@sha256:47bc9e29bc4e7ff098e925d66119c49fdfadf6744790e83c651dae495819c1e7`, built from `git archive HEAD` (not pushed to GHCR).
- Verified before deploy: `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test` 444 passed; `ando run` green; Postgres E2E 18 passed; independent review findings addressed (send timeout handling, recurring flush always registered, busy-lock retry, email-disabled re-check, extra tests).
- Backup: `/srv/dualconsult/backups/hawk/hawk-pre-0.15.0-20261029T122848Z.dump` on the db VM (88 MB, `pg_restore -l` lists 30 tables).
- Infra: `f5868c6` (`apps/hawk/release.env` only), `scripts/deploy-app.sh hawk app edge`.
- Live: v0.15.0, 0 restarts; migration `20261029121816_AddPendingAlerts` applied; recurring `hawk-alert-digest` registered and its first run succeeded; `/live` 200, `/api/v1/monitors` 401.
- Rollback: pin 0.14.0 (`sha256:2f28e041…`) and redeploy; delete the `hawk-alert-digest` recurring job and scheduled `IAlertDigestSender` jobs in the Hangfire dashboard. The table is additive; queued unsent alerts are lost.
