# Changelog

All notable changes to this project will be documented in this file.

The format is based on "Keep a Changelog", and this project follows Semantic Versioning.

## [Unreleased]

## [0.14.0] - 2026-10-29

### Changed

- New "Tower" design across the app: dark and light ("day shift") palettes following the OS (the theme toggle still overrides), self-hosted Archivo font (no external font requests), Tower status colors.
- Monitors overview: a headline sentence (how many monitors are down, longest outage), a red Attention section with one card per failing monitor (replaces the Failing section; each monitor now appears once in its group), a group rail, and dense rows with a 24-hour strip of 96 15-minute cells (red = a failure, height = average response time).
- Monitor details lead with the status and since when (DOWN since, UP since, PAUSED, DISABLED, PENDING, WAITING), show 24h/7d/30d uptime, a 24-hour response-time chart (heartbeats: a 24-hour ping strip) and a 90-day uptime row. Run diagnostics use the same layout.

### Added

- 15-minute run rollup (`MonitorRunBuckets`) kept for 95 days independent of run retention, filled every minute by the Hangfire job `hawk-run-buckets` and backfilled from retained runs on startup and after a StatusCake import. Adds an additive schema migration (`AddMonitorRunBuckets`; back up before upgrading). When rolling back to 0.13.x, delete the `hawk-run-buckets` recurring job and any enqueued, scheduled or failed `IRunBucketRollup` jobs in the Hangfire dashboard.
- Agent API: the monitor detail has `uptime` (`last24h`, `last7d`, `last30d`, percent or null).

## [0.13.0] - 2026-10-29

### Added

- Monitor **groups**: an optional group name per monitor (Create/Edit field with suggestions; names match ignoring case and take an existing group's spelling). The monitors index lists monitors by group under the Failing section (failing monitors appear in both), with per-group select-all, Pause, Resume, Run and Rename (rename into another group merges after a confirmation). Details shows a group breadcrumb.
- Agent API: `group` on monitor list, detail, create and `PATCH`; `GET /api/v1/monitors?group=`; `GET /api/v1/groups` with counts; `POST /api/v1/groups/pause`, `/resume`, `/run` and `/rename`. See `docs/agent-api.md`.
- JSON export/import and the Admin bulk transfer carry `group` (still format version 3; a missing field means no group). Adds an additive schema migration (`AddMonitorGroupName`; back up before upgrading).

## [0.12.0] - 2026-10-29

### Added

- Agent API (version 0.12.0): `POST /api/v1/monitors` creates HTTP or heartbeat monitors (a heartbeat response contains the `pingUrl`), `PATCH /api/v1/monitors/{id}` edits any monitor setting with merge-patch semantics, and `DELETE /api/v1/monitors/{id}` deletes a monitor with its runs and alert state (irreversible). Validation matches the UI. The monitor detail now has `concurrencyStamp` and an `ETag`; `PATCH` and `DELETE` accept `If-Match` and answer 412 `precondition_failed` for a stale value. See `docs/agent-api.md`.
- Per-monitor **Follow redirects** setting (default on, unchanged behavior). When off, the check judges the 3xx response itself and it passes only if its code is in the allowed status codes, for example `301` to assert a permanent redirect. Available on the Create/Edit/Details pages, in the API (`followRedirects`) and in the JSON export/import (`followRedirects`, still format version 3; a missing field means true). Adds an additive schema migration (`AddMonitorFollowRedirects`, existing monitors get true; back up before upgrading). The mock server has `GET /redirect`.

### Fixed

- API monitor create/edit accepts at most 5 headers and 5 match rules (the UI forms' slots) instead of losing the extras on the next Edit save. A `PATCH` without `If-Match` now returns 409 `conflict` when the monitor was changed after it was read, instead of reverting that change. A blank `alertEmailOverride` clears the override.

### Changed

- Creating, editing and deleting monitors now goes through one shared `MonitorEditor` service used by the pages and the API (no behavior change in the UI).

## [0.11.0] - 2026-10-29

### Added

- Agent API (version 0.11.0): per-user API keys (Account -> API keys, `read` or `write` scope, shown once, stored only as SHA-256 hashes) and a JSON API under `/api/v1` with `Authorization: Bearer hawk_...`. Agents can list monitors with state, health and failure summary, read monitor detail, alert state, effective alert recipients and run diagnostics, change a monitor's alert threshold and email (`PATCH`), change the account alert email, pause and resume monitors and queue runs. Errors are `application/problem+json` with a stable `code`. Rate limits: `Hawk__Security__ApiRequestsPerMinute` (120 per key) and `Hawk__Security__ApiRequestsPerMinutePerIp` (600 per IP). Password resets revoke the user's keys. Adds an additive schema migration (back up before upgrading). See `docs/agent-api.md`.

### Fixed

- Public account security (version 0.9.30): preserve Identity cookie validation, check session revocation every minute, rate-limit login and account-email submissions, count failed logins toward lockout, and share an account-email cooldown.
- Production errors and anonymous account-email failures no longer disclose exception details; malformed confirmation/reset tokens return controlled validation errors.
- Manual monitor execution requires an antiforgery-protected POST; the old GET Test route redirects to monitor details, and saved diagnostics provide a protected **Run again** action.
- Mock email capture is disabled unless explicitly enabled in Development or Testing; captures omit bearer credentials and local mock ports bind to loopback.

### Added

- Heartbeat (push) monitors (version 0.10.0): a dead man's switch for cron jobs and backups. The client calls the secret URL `GET|HEAD|POST /ping/{token}`; if no ping arrives within the expected period plus grace, the monitor fails and uses the normal alerting (threshold, reminders, recovery). Per-token and per-IP ping rate limits (`Hawk__Security__PingRequestsPerMinute`, `Hawk__Security__PingRequestsPerMinutePerIp`), ping tokens are redacted from request logs, JSON export format v3, and a **Regenerate URL** action. Adds a schema migration (additive; back up before upgrading). Add `log_skip /ping/*` to the reverse proxy access log so tokens stay out of proxy logs.
- Recovery emails are now also sent when the alert threshold is raised during an alerted incident.
- Profile page: **Send confirmation email** button for unconfirmed accounts (version 0.9.29).
- File logging location and retention are configurable (version 0.9.28): `Hawk__Logging__File__Path` (default `logs`) and `Hawk__Logging__File__RetainedDays` (default 30).
- PostgreSQL support (version 0.9.27): `Hawk__Database__Provider=Postgres` (SQL Server stays the default; unknown values fail startup). Separate PostgreSQL migration set, Hangfire.PostgreSql storage, `docker-compose.postgres.yml` and `docker-compose.e2e.postgres.yml`, `./run-dev.sh --postgres`.
- Health endpoints: `GET /live` (process up) and `GET /ready` (database reachable, `503` otherwise).
- `Hawk__Logging__File__Enabled` (default `true`): set to `false` for console-only logging, e.g. with a read-only root filesystem.

### Changed

- **Upgrade note (0.9.27): the SQL Server migration `ReplaceRowVersionWithConcurrencyStamp` is one-way.** `0.9.26` returns HTTP 500 against a migrated database. Take a native `BACKUP DATABASE` before upgrading; rollback means restoring that backup. TrueNAS installs on `:latest` with `pull_policy: always` upgrade automatically on restart, so pin a version tag (e.g. `:0.9.26`) until ready. The README TrueNAS example now pins a version tag.
- EF Core command logging (`Microsoft.EntityFrameworkCore.Database.Command`) is lowered to `Warning`, so scheduler tick queries no longer flood stdout; failed commands are still logged.
- PostgreSQL connection string examples include `GSS Encryption Mode=Disable` (avoids the `libgssapi_krb5.so.2` stderr message at startup).
- EF Core packages aligned on 10.0.4 (`Microsoft.VisualStudio.Web.CodeGeneration.Design` 10.0.2).
- The container image runs as the non-root .NET user (UID `1654`). Installs without volumes are unaffected; volumes or host paths created by older images must be chowned to `1654:1654` (or run the container as root). Startup logs an error/warning when the DataProtection keys path is unreadable/unwritable.
- Monitor and alert-state optimistic concurrency uses an app-managed `ConcurrencyStamp` instead of SQL Server `rowversion` (migration `ReplaceRowVersionWithConcurrencyStamp`).

### Fixed

- Email confirmation and password reset links no longer fail with "Error confirming your email" (version 0.9.28): generated query strings were lowercased, corrupting the case-sensitive token.
- Edit page: no more spurious "changed by someone else" error when a scheduled run finished while the form was open; only real concurrent edits (or pause/resume) conflict.
- StatusCake import: alert timestamps with a non-UTC offset are stored as UTC (timestamps without offset are read as UTC); duplicate detection by name is case-insensitive on every provider.
- Scheduler: monitors that were never scheduled are picked first on every provider (PostgreSQL sorts NULL last).
- Monitors and Admin users lists sort case-insensitively on every provider.
- PostgreSQL: response bodies with NUL characters (binary content) no longer fail saving the run; NUL characters in monitor fields are rejected.
- Delete page: deleting a monitor that was edited or paused concurrently no longer returns an error.
- StatusCake alert import: runs and `LastRunAt` are saved in one transaction and `LastRunAt` is written set-based, so a concurrent edit or pause can no longer fail the import.
- Alerts: a failed first failure-alert email (or a threshold lowered while failing) no longer drops the incident; the alert retries on each failing run until a send succeeds.
- Bulk import: an unparsable regex is now a preview warning instead of refusing the import.
- Alerts: seeding a new alert state no longer counts the in-flight "Run now" run (threshold 1 suppressed the alert, threshold 2 alerted one run early).
- Create page: the main form posted to the Import handler after an import prefill; it now posts to the create handler.
- Alerts: `LastRunAt` is updated set-based so a rowversion conflict can no longer fail the final save after an alert was sent (which lost the run record and alert-state update).
- Bulk import: field-level validation (required, max length, ranges, header lengths) now applies; malformed files (`null` monitors) return a parse error instead of a 500.
- Bulk import: concurrent or double-submitted confirms no longer insert duplicates; the 2 MB check ignores CRLF expansion; export order is collation independent.

### Added

- Create/Edit: regex match rule patterns are validated on save.
- Admin-only bulk export of all monitors and dry-run bulk import (`/Admin/Transfer`): duplicate skipping (name + URL, case-insensitive), owner mapping by email, optional "create paused", all-or-nothing transaction.
- Monitor JSON export format v2 (`exportedAtUtc`, optional per-monitor `ownerEmail`); v1 files remain importable (version 0.9.26).
- Admin-only StatusCake JSON import for uptime tests and alert history.
- Alerting policy setting: send email only after `N` consecutive failures (default: `1`).
- E2E stability improvements:
  - Always recreate docker compose stack for a clean DB per run.
  - Stable `data-testid` assertions for StatusCake import results.

## [0.9.0] - 2026-02-09

### Added

- ASP.NET Core Razor Pages app with ASP.NET Identity authentication and Admin role.
- Monitor CRUD UI for uptime checks with GET/POST support, custom headers, and request body/content-type.
- Content verification rules (contains and regex).
- Hangfire-based scheduling with a tick loop; Testing environment enables a 5-second interval.
- SQL Server backend (EF Core + Hangfire storage) with automatic migrations on startup.
- Resend-compatible email alert sender with mockable base URL for deterministic testing.
- Serilog rolling file logs with ~30-day retention.
- Docker compose for local dev (web + SQL Server + mock server).
- Dockerized Playwright E2E tests (headed Chromium via Xvfb) saving screenshots to `./screenshots`.
- Basic xUnit tests for URL checking and form validation.
