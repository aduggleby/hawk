# Changelog

All notable changes to this project will be documented in this file.

The format is based on "Keep a Changelog", and this project follows Semantic Versioning.

## [Unreleased]

### Added

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
