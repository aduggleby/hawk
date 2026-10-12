# Changelog

All notable changes to this project will be documented in this file.

The format is based on "Keep a Changelog", and this project follows Semantic Versioning.

## [Unreleased]

### Fixed

- Alerts: seeding a new alert state no longer counts the in-flight "Run now" run (threshold 1 suppressed the alert, threshold 2 alerted one run early).
- Create page: the main form posted to the Import handler after an import prefill; it now posts to the create handler.
- Alerts: `LastRunAt` is updated set-based so a rowversion conflict can no longer fail the save after an alert was sent and cause a duplicate alert on Hangfire retry.
- Bulk import: field-level validation (required, max length, ranges, header lengths, regex validity) now applies; malformed files (`null` monitors) return a parse error instead of a 500.
- Bulk import: concurrent or double-submitted confirms no longer insert duplicates; the 2 MB check ignores CRLF expansion; export order is collation independent.

### Added

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
