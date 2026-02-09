# Changelog

All notable changes to this project will be documented in this file.

The format is based on "Keep a Changelog", and this project follows Semantic Versioning.

## [Unreleased]

### Added

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
