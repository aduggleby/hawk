# Hawk Project Notes (For Agents)

This repository is building an ASP.NET Razor Pages uptime checker and URL verifier with Hangfire-based scheduling, SQL Server storage, and Docker-first deployment.

## Current State (As Of 2026-02-09)

- Solution: `Hawk.sln`
- Web app: `Hawk.Web` (Razor Pages + ASP.NET Core Identity)
- Unit tests: `Hawk.Tests` (xUnit)
- E2E tests: `e2e` (Playwright, Chromium; dockerized headed runs)
- Mock server: `Hawk.MockServer` (deterministic endpoints + Resend-compatible `/emails` capture)

## Auth / Users

- Auth is ASP.NET Core Identity (Individual auth).
- Roles are enabled (`AddRoles<IdentityRole>()`).
- A seed admin user is created at startup:
  - Email: `ad@dualconsult.com`
  - Password: `Hawk!2026-Admin#1`
  - Role: `Admin`
- Seeding and migration are executed on startup in:
  - `Hawk.Web/Data/Seeding/IdentitySeeder.cs`
  - Called from `Hawk.Web/Program.cs`

Environment overrides (preferred for Docker/CI):
- `Hawk__SeedAdmin__Email`
- `Hawk__SeedAdmin__Password`

## Migrations On Startup

- The app currently calls `db.Database.MigrateAsync()` during startup seeding.
- Goal: keep this behavior for container deployments (web container applies migrations against the SQL Server container at boot).
- Current behavior includes retries to handle SQL Server still starting.

## URL Checking Core

Implemented v1 URL checker service in `Hawk.Web/Services/UrlCheckModels.cs`:
- Supports GET/POST/PUT/PATCH (POST includes body + content-type).
- Supports custom headers.
- Supports content verification rules:
  - `Contains` (case-insensitive)
  - `Regex` (case-insensitive, singleline; 2s match timeout)
- Buffers up to 256KB of response for matching/snippet.

## Unit Tests

`Hawk.Tests/UrlCheckerTests.cs` covers:
- GET `https://example.com` with contains match
- GET `https://example.com` with regex match
- POST `https://example.com` returns a failure result but should not throw

## E2E (Playwright)

Location: `e2e/`
- Config: `e2e/playwright.config.ts`
  - Expects `HAWK_BASE_URL` to point at a running app (docker compose provides this).
  - Runs Chromium headed (`headless: false`).
- Tests: `e2e/tests/smoke.spec.ts`
  - Home page loads
  - Seed admin can log in
- Tests: `e2e/tests/monitors.spec.ts`
  - Creates GET/POST monitors, verifies OK/FAIL states
  - Verifies schedule execution using the Testing-only 5s interval
  - Verifies failure alerts hit the mock `/emails` endpoint

Run:
- Docker (recommended): `docker compose -f docker-compose.e2e.yml up --build --exit-code-from e2e e2e`

## HTTPS Redirection Toggle (For E2E)

`Hawk.Web/Program.cs` has a guard:
- `Hawk:DisableHttpsRedirection` (env `Hawk__DisableHttpsRedirection`)

E2E uses HTTP because Playwright `webServer.url` expects a stable plain URL.

## Docker / SQL Server / Hangfire (Planned)

Target architecture:
- `docker-compose.yml` with:
  - `web` (ASP.NET)
  - `db` (SQL Server)
- `mock` (mock server; optional but useful for testing alerting)
- App uses SQL Server for:
  - Identity
  - Hangfire storage
  - Monitoring configuration + check history
- The web container should:
  - Apply EF migrations on startup
  - Register Hangfire server and dashboard

Scheduling requirements:
- Fixed intervals only (user chooses from allowed values).
- For E2E testing, add a special 5-second interval available only in `Testing` environment.

Implementation:
- Scheduling is a self-scheduling Hangfire tick job (`IMonitorScheduler.TickAsync`) that enqueues due monitors.
- The user-facing interval list includes `5s` only when `ASPNETCORE_ENVIRONMENT=Testing`.

## Email Alerts (Planned)

Failures trigger email via a Resend-compatible API:
- Configurable via environment variables (API key + base URL).
- Must be mockable for E2E (dependency injection: swap implementation in `Testing`).

Implementation:
- `Hawk.Web/Services/Email/ResendCompatibleEmailSender.cs` posts to `${BaseUrl}/emails` with Bearer auth.
- E2E points `Hawk__Resend__BaseUrl` at `Hawk.MockServer` and asserts captured payloads via `GET /emails`.

## Logging (Planned)

Requirement:
- Serilog rolling application log.
- Delete old logs after 30 days.

Implementation notes:
- Use Serilog sinks to roll by day.
- Add retention (`retainedFileCountLimit`) sized for ~30 days (or use a cleanup job).
- Ensure logs write to a mounted folder in Docker.

Implementation:
- Serilog file sink rolls daily to `logs/hawk-.log` with `retainedFileCountLimit: 30`.

## E2E In Docker (Planned)

Requirement:
- Run E2E tests in Docker with:
  - Unique DB instance per run (e.g., unique compose project name and db volume).
  - Chromium in headed mode (not headless) using a virtual display (Xvfb).
  - Save screenshots of major app pages to `./screenshots` on the host.
  - Mock dependent services (email, outbound HTTP checks) for deterministic tests.

Implementation notes:
- Prefer Playwright official image for browser deps.
- Use `xvfb-run` to drive headed Chromium.
- Mount `./screenshots:/work/screenshots`.

Implementation:
- `docker-compose.e2e.yml` runs `web` + `db` + `mock` + `e2e`.
- `e2e/Dockerfile` runs headed Chromium via `Xvfb` (not headless) and waits for `HAWK_BASE_URL`.
- Screenshots are written to host `./screenshots` by `e2e/tests/helpers.ts`.

## Deployment To Proxmox VM (Planned)

User asked:
- Deploy to a new VM on `alex-office-proxmox1`.
- Use an existing public key from `~/.ssh` or `~/.sshkeys` (keys exist locally).

Implementation notes:
- Needs clarification outside this file:
  - How to provision VM (Terraform, Proxmox API, cloud-init template, manual).
  - SSH user, VM OS image, network, domain/ports.
- Once VM exists: install Docker, copy compose, configure env, run `docker compose up -d`.

## Git Workflow

- User requested: create git commits for major changes going forward.
- Keep commits scoped:
  - Infra (Docker/compose) separate from app features
  - E2E harness changes separate from app behavior when possible
