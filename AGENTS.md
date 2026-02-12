# Hawk Project Notes (For Agents)

This repository is building an ASP.NET Razor Pages uptime checker and URL verifier with Hangfire-based scheduling, SQL Server storage, and Docker-first deployment.

## Current State (As Of 2026-02-12)

- Solution: `Hawk.sln`
- Web app: `Hawk.Web` (Razor Pages + ASP.NET Core Identity)
- Unit tests: `Hawk.Tests` (xUnit)
- E2E tests: `e2e` (Playwright, Chromium; dockerized headed runs)
- Mock server: `Hawk.MockServer` (deterministic endpoints + Resend-compatible `/emails` capture + `/flaky` alternating endpoint)
- UI: Tailwind CSS v4 with custom component classes (`hawk-btn`, `hawk-card`, etc.), dark mode support, mobile nav drawer. Bootstrap has been removed.
- Primary database: SQL Server (EF Core SQL Server provider)
- SQLite: not used (previous experimentation, if any, should not be reintroduced unless explicitly requested)
- Version: `0.9.22`

## Ports

Reserved ports for this project:

- Web: `17800` (container port `8080`)
- Mock server: `17801` (container port `8081`)
- SQL Server (dev compose only): `17833` (container port `1433`)

## Auth / Users

- Auth is ASP.NET Core Identity (Individual auth).
- Roles are enabled (`AddRoles<IdentityRole>()`).
- In `Development` and `Testing`, a seed admin user is created at startup:
  - Email: `ad@dualconsult.com`
  - Password: `Hawk!2026-Admin#1`
  - Role: `Admin`
- In `Production` (and other non-Development environments), Hawk does not seed an admin user. The first user to register is promoted to the `Admin` role automatically.
- Seeding and migration are executed on startup in:
  - `Hawk.Web/Data/Seeding/IdentitySeeder.cs`
  - Called from `Hawk.Web/Program.cs`

Environment overrides (preferred for Docker/CI):
- `Hawk__SeedAdmin__Email`
- `Hawk__SeedAdmin__Password`

Admin users can manage other users via Admin -> Users (edit name/email/roles, reset password, delete).

## Migrations On Startup

- The app currently calls `db.Database.MigrateAsync()` during startup seeding.
- Goal: keep this behavior for container deployments (web container applies migrations against the SQL Server container at boot).
- Current behavior includes retries to handle SQL Server still starting.

## Allowed Status Codes

- `Monitor.AllowedStatusCodes` — optional comma-separated list of additional HTTP status codes treated as success (e.g. `404,429`).
- 2xx codes are always success regardless of this setting.
- Parsed by `Hawk.Web/Services/Monitoring/AllowedStatusCodesParser.cs` (validates range 100–599, normalizes/deduplicates).
- Evaluated in `MonitorExecutor` when determining run pass/fail.
- Shown on Create/Edit forms and on the monitor Details page.

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
  - In Docker, runs serially by default (`E2E_DOCKER=1` forces `workers=1`, `fullyParallel=false`) to avoid shared-DB flakes.
  - Override with `PLAYWRIGHT_WORKERS=<n>` if you intentionally want parallelism.
- Tests: `e2e/tests/smoke.spec.ts`
  - Home page loads
  - Seed admin can log in
- Tests: `e2e/tests/monitors.spec.ts`
  - Creates GET/POST monitors, verifies OK/FAIL states
  - Verifies schedule execution using the Testing-only 5s interval
  - Verifies failure alerts hit the mock `/emails` endpoint

Run:
- Docker (recommended): `docker compose -f docker-compose.e2e.yml up --build --exit-code-from e2e e2e`
  - Note: this writes screenshots to host `./screenshots` (mounted into the Playwright container).
  - Preferred: `npm --prefix e2e test` which performs `docker compose down` first to guarantee a fresh DB each run.

## URL Routing

- Lowercase URLs and lowercase query strings are enforced via `AddRouting` options in `Program.cs`.

## HTTPS Redirection Toggle (For E2E)

`Hawk.Web/Program.cs` has a guard:
- `Hawk:DisableHttpsRedirection` (env `Hawk__DisableHttpsRedirection`)

E2E uses HTTP because Playwright `webServer.url` expects a stable plain URL.

## Docker / SQL Server / Hangfire

Implemented architecture:
- `docker-compose.yml` runs:
  - `web` (ASP.NET)
  - `db` (SQL Server)
  - `mock` (optional mock server; useful for testing alerting)
- App uses SQL Server for:
  - Identity
  - Hangfire storage
  - Monitoring configuration + check history
- The web container:
  - Multi-stage Docker build: `node:20-alpine` stage compiles Tailwind CSS, then .NET SDK stage builds the app
  - Applies EF migrations on startup
  - Runs Hangfire server and dashboard (dashboard is Admin-only)

Scheduling requirements:
- Fixed intervals only (user chooses from allowed values).
- For E2E testing, add a special 5-second interval available only in `Testing` environment.

Implementation:
- Scheduling is a self-scheduling Hangfire tick job (`IMonitorScheduler.TickAsync`) that enqueues due monitors.
- The user-facing interval list includes `5s` only when `ASPNETCORE_ENVIRONMENT=Testing`.
  - This is enforced in code via `MonitorIntervals.AllowedSeconds(env)`.

Handy commands:
- Run locally: `docker compose up -d --build`
- App: `http://localhost:17800`
- Mock server: `http://localhost:17801`

## Startup Banner

- `Hawk.Web/Infrastructure/StartupBanner.cs` prints an ASCII art banner and version to the console on startup.
- Called from `Program.cs` before the host starts.

## App Version Footer

- `Hawk.Web/Infrastructure/AppVersion.cs` reads the assembly `InformationalVersion` (stripping the `+commit` suffix) and falls back to the assembly version.
- The footer in `_Layout.cshtml` displays the version (e.g. `v0.9.20`) alongside a link to the GitHub repo and author.

## Error Page

- `Hawk.Web/Pages/Error.cshtml(.cs)` — styled error page with exception diagnostics.
- Only used in non-Development environments (Development uses the built-in developer exception page).
- Displays request ID, failing path, exception type, and message in a grid layout.
- An expandable `<details>` section shows the full stack trace and inner exception chain when diagnostics are available.
- Exception capture uses multiple fallback sources: `IExceptionHandlerPathFeature`, `HttpContext.Items["UnhandledException"]` (set by custom middleware in `Program.cs`), and `IExceptionHandlerFeature`. This ensures diagnostics render even when the primary feature is unavailable.
- A **Copy technical details** button copies the error info (request ID, path, exception type, message, stack trace) to the clipboard for easy bug reporting.

## Flash Messages

- `Hawk.Web/Pages/Shared/_Flash.cshtml` renders flash messages from `TempData["FlashError"]` and `TempData["FlashInfo"]`.
- Included in `_Layout.cshtml` so flash messages appear on any page after a redirect.
- Used by Identity pages (Register, ForgotPassword, ResendEmailConfirmation) to show email delivery errors to the user instead of crashing.

## Email Alerts

Failures trigger email via a Resend-compatible API:
- Configurable via environment variables (API key + base URL).
- Must be mockable for E2E (dependency injection: swap implementation in `Testing`).

Implementation:
- `Hawk.Web/Services/Email/ResendCompatibleEmailSender.cs` posts to `${BaseUrl}/emails` with Bearer auth.
- `Hawk.Web/Services/Email/IdentityUiEmailSender.cs` adapts the app's `IEmailSender` for ASP.NET Core Identity UI email flows (register confirmation, forgot password, etc.). It checks `Hawk:Email:Enabled` and `Hawk:Email:From` before sending, and throws (with logging) if email is disabled or misconfigured. Identity pages catch these errors and display them via flash messages instead of showing a 500 error.
- E2E points `Hawk__Resend__BaseUrl` at `Hawk.MockServer` and asserts captured payloads via `GET /emails`.

Alert policy:
- Monitors have `AlertAfterConsecutiveFailures` (1..20) controlling when a failure incident should trigger email.
- Alert logic is driven by `MonitorAlertingDecider` (deterministic, stateless decider) operating on a persisted `MonitorAlertState` entity per monitor.
- Default behavior (`1`) is "alert on first failure after a success", not "alert on every failed run".

Alert types:
- **Failure** — sent when consecutive failures reach the threshold.
- **Failure reminder** — re-sent every N hours while the monitor is still failing (default 24h, configurable via `Hawk:Alerting:RepeatFailureAlertEveryHours`, min 1h, max 720h).
- **Recovery** — sent when a monitor returns to success after an alerted failure incident. If the recovery email fails to send, it is retried on subsequent successful runs.

Alert state:
- `Hawk.Web/Data/Alerting/MonitorAlertState.cs` — per-monitor persisted state tracking consecutive failures, incident timestamps, last alert timestamps, pending recovery, and errors.
- State transitions are handled by `MonitorAlertingDecider.OnFailure` / `OnSuccess` in `Hawk.Web/Services/Monitoring/MonitorAlertingDecider.cs`.
- `MonitorExecutor` orchestrates: calls the decider, sends the appropriate email, and updates the state.

Alert recipient resolution (in order):
1. Per-monitor `AlertEmailOverride` field.
2. Account-wide override from `UserAlertSettings` (set in Settings → Alerting).
3. Monitor owner's Identity email.
4. All Admin users (fallback).

## Monitor Pause State

- `Monitor.IsPaused` — a boolean flag that temporarily pauses a monitor while keeping it enabled.
- Paused monitors are skipped by `MonitorScheduler` (the query filters `!m.IsPaused`) and by `MonitorExecutor` (unless reason is `"manual"`).
- The monitors index page shows Enabled/Paused/Disabled badges and supports batch pause/resume (selected or all).
- Paused monitors can still be tested manually via the Test page.

## Account Manage Pages

- Account settings have been restructured under `/Identity/Account/Manage/` with a sidebar layout:
  - **Profile** (`/Identity/Account/Manage/Index`) — display name and email.
  - **Security** (`/Identity/Account/Manage/ChangePassword`) — change password.
  - **Alerts & Crawler** (`/Identity/Account/Manage/Settings`) — alert email override + User-Agent override + run retention override.
- Old routes (`/Account/Settings`, `/Account/Alerting`) redirect to the new locations for back-compat.
- Navigation uses an Account dropdown in the topbar with links to Profile, Security, Alerts & Crawler, plus Admin/Hangfire for admins.

## User Settings

- `Hawk.Web/Areas/Identity/Pages/Account/Manage/Settings.cshtml(.cs)` provides account-wide overrides:
  - **Alert email override** — redirects alert emails for the user's monitors to a different address. Stored in `UserAlertSettings`.
  - **Crawler User-Agent override** — sets a default `User-Agent` for all monitors the user owns (unless the monitor explicitly sets one via headers). Stored in `UserUrlCheckSettings`.
  - **Run retention override** — account-wide default run history retention (in days). Stored in `UserMonitorSettings`. Per-monitor override takes precedence.
- User-Agent can be a preset key (`firefox`, `chrome`, `edge`, `safari`, `curl`) or a full UA string. Resolution is in `Hawk.Web/Services/UrlChecks/UserAgentResolver.cs`.
- Presets can be overridden via config: `Hawk:UrlChecks:UserAgentPresets:<key>`.

## Monitor Execution (MonitorExecutor)

- `Hawk.Web/Services/Monitoring/MonitorExecutor.cs` is the shared execution engine used by both the Hangfire scheduler (`MonitorRunner`) and the interactive test page.
- Produces a `MonitorExecutionResult` record containing the monitor, request, result, and persisted run.
- Handles: loading the monitor + headers + match rules, resolving User-Agent overrides, running the URL check, persisting the run, evaluating alert policy, and sending alerts.
- Each run now stores full request/response diagnostics on the `MonitorRun` entity: `Reason`, `RequestUrl`, `RequestMethod`, `RequestContentType`, `RequestTimeoutMs`, `RequestHeadersJson`, `RequestBodySnippet`, `ResponseHeadersJson`, `ResponseContentType`, `ResponseContentLength`.
- After persisting a run, `MonitorExecutor` calls `PruneRunHistoryAsync` to delete runs older than the resolved retention period.

## Run Retention

- `Monitor.RunRetentionDays` — optional per-monitor run history retention override (1–3650 days).
- `UserMonitorSettings.RunRetentionDays` — optional account-wide retention default, stored in `Hawk.Web/Data/Monitoring/UserMonitorSettings.cs`.
- Resolution order (in `MonitorExecutor.ResolveRunRetentionDaysAsync`):
  1. Per-monitor `RunRetentionDays`.
  2. Account-wide `UserMonitorSettings.RunRetentionDays`.
  3. Server default `Hawk:Monitoring:RunRetentionDaysDefault` (default 90).
- Pruning runs `ExecuteDeleteAsync` on `MonitorRuns` older than the cutoff after every run.

## Monitor Edit Concurrency

- `Monitor.RowVersion` — a SQL Server `rowversion` column used for optimistic concurrency control on the Edit page.
- `MonitorForm.RowVersion` carries the token through the form as a hidden field (base64-encoded).
- `Edit.cshtml.cs` catches `DbUpdateConcurrencyException` and returns an error message asking the user to reload and retry.

## Monitors Index — Failing Group

- The monitors index page groups monitors into two sections: **Failing** (most recent run failed) and **All monitors**.
- Failing monitors are queried separately using a join on the most recent `MonitorRun` where `Success == false`.
- Failing monitors appear at the top in a distinct section so operators can immediately see what needs attention.

## Monitor Detail Page

- `Hawk.Web/Pages/Monitors/Details.cshtml(.cs)` shows monitor configuration, headers, match rules, and the 25 most recent runs.
- **Run now** button enqueues an immediate Hangfire job for the monitor (reason `"manual"`).
- Each run in the history table links to the run diagnostics page.

## Monitor JSON Export / Import

- `Hawk.Web/Pages/Monitors/MonitorJsonPort.cs` contains the JSON model (`MonitorExportEnvelope`, `MonitorExportModel`) and mapping helpers.
- **Export** — `Details.cshtml.cs` has an `OnGetExport` handler that serializes a single monitor to a JSON file download.
- **Import** — `Create.cshtml.cs` has an `OnPostImport` handler that accepts a `.json` file upload, parses it via `MonitorJsonPort.TryParse`, and prefills the create form with the first monitor's values. The user reviews/edits the prefilled form and then clicks Create. For multi-monitor files, only the first monitor is loaded (the rest are noted via flash message).
- The parser accepts three JSON shapes: an envelope object with `monitors` array, a bare array of monitor objects, or a single monitor object.
- Export uses `System.Text.Json` with `JsonStringEnumConverter` (camelCase) and `WriteIndented`.
- Each imported monitor goes through the same `MonitorForm.Validate()` pipeline as the create form.
- Invalid monitors are reported via `TempData` flash messages.
- E2E tests cover export and re-import in `e2e/tests/monitors.spec.ts`.

## Run Diagnostics Page

- `Hawk.Web/Pages/Monitors/Runs/Details.cshtml(.cs)` — displays full diagnostics for a single run:
  - Run metadata: result, reason, timestamps, duration, HTTP status, alert status, error.
  - Request: URL, method, content-type, timeout, headers JSON, body snippet.
  - Response: content-type, content-length, headers JSON, response snippet, match results JSON.
- Accessible from the run history table on the monitor detail page.

## Monitor Test Page

- `Hawk.Web/Pages/Monitors/Test.cshtml(.cs)` — runs a monitor immediately via `IMonitorExecutor` and displays full diagnostics (request details, response headers, match rule results, body snippet).
- Accessible from the monitor detail page.

## Monitor Form Validation

- `Hawk.Web/Pages/Monitors/MonitorFormValidation.cs` maps custom `ValidationResult` errors to Razor `ModelStateDictionary` keys (prefixed with `Form.`).
- Create and Edit pages call `MonitorFormValidation.AddResults()` to surface server-side validation errors inline.
- Optional form sections (headers, match rules, advanced settings) are collapsed by default and expand via `<details>` elements.
- The POST options section (body, content-type) is hidden when the selected HTTP method is not POST, and shown dynamically via JavaScript when the user selects POST.
- **Create/Edit parity rule:** when changing monitor form UI/behavior on `Hawk.Web/Pages/Monitors/Create.cshtml`, apply the equivalent change to `Hawk.Web/Pages/Monitors/Edit.cshtml` (and vice versa) unless explicitly requested otherwise.

## Dev Tooling

### run-dev.sh / stop-dev.sh

- `run-dev.sh` starts the full local dev environment: restores .NET packages, installs/builds Tailwind, starts `db` + `mock` via Docker Compose, and runs `Hawk.Web` with `dotnet watch`.
- Runs in tmux by default (`hawk-dev` session); use `--no-tmux` for foreground mode, `--no-deps` to skip Docker services.
- `stop-dev.sh` stops all processes started by `run-dev.sh` (dotnet watch, Docker containers, tmux session).

### Monitor Seeder

- `Hawk.Web/Data/Seeding/MonitorSeeder.cs` seeds sample monitors in Development/Testing environments using MockServer endpoints.
- Creates monitors named `[Seed] *` (GET contains, GET regex, POST echo, HTTP 500, timeout, flaky, DNS failure).
- All seeded monitors start paused (`IsPaused = true`) so they don't trigger scheduled runs immediately.
- Mock base URL resolved from `Hawk:SeedMocks:BaseUrl`, then `Hawk:Resend:BaseUrl` if it looks local, then defaults to `http://localhost:17801`.
- Called from `Program.cs` during startup.

## Logging

Requirement:
- Serilog rolling application log.
- Delete old logs after 30 days.

Implementation notes:
- Use Serilog sinks to roll by day.
- Add retention (`retainedFileCountLimit`) sized for ~30 days (or use a cleanup job).
- Ensure logs write to a mounted folder in Docker.

Implementation:
- Serilog file sink rolls daily to `logs/hawk-.log` with `retainedFileCountLimit: 30`.

## DataProtection Keys (Docker)

Problem:
- ASP.NET Core Identity cookies are protected using DataProtection keys.
- If keys are not persisted, a container restart invalidates existing auth cookies.

Implementation:
- When running in a container (`DOTNET_RUNNING_IN_CONTAINER=true`), keys are persisted to `/var/lib/hawk/dpkeys`.
- `docker-compose.yml` mounts a named volume `hawk_dpkeys` at `/var/lib/hawk/dpkeys`.
- Override path via `Hawk:DataProtection:KeysPath` if needed.

## E2E In Docker

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
 - Playwright is forced to run serially in Docker via `E2E_DOCKER=1` in `docker-compose.e2e.yml`.
- If screenshot capture is flaky under Docker/Xvfb, `snap()` retries and then logs a warning instead of failing the whole suite.

## Deployment To Proxmox VM

Deployed on `alex-office-proxmox1` using the SSH key `~/.sshkeys/id_ad_dualconsult_com`:

- Proxmox host: `alex-office-proxmox1`
- VM:
  - VMID: `102`
  - Name: `hawk`
  - OS image: `/var/lib/vz/template/iso/ubuntu-22.04-cloudimg-amd64.img`
  - User: `alex` (cloud-init), key-based auth
  - IP (DHCP): `192.168.1.127`
- Deployment path on VM: `/opt/hawk`

Commands (high level):
- Install Docker on the VM (Docker convenience script).
- Copy repo to `/opt/hawk`.
- Create `/opt/hawk/.env` with `SA_PASSWORD` and email env vars.
- Start services: `sudo docker compose up -d --build`.

Service endpoints on the VM:
- Hawk web: `http://192.168.1.127:17800`
- Mock server: `http://192.168.1.127:17801`

## Configuration Cheatsheet

- SQL Server:
  - `ConnectionStrings__DefaultConnection` (used by EF Core + Hangfire)
  - `SA_PASSWORD` (compose)
- Scheduler:
  - `Hawk__Scheduler__Enabled` (default true)
  - `Hawk__Scheduler__TickSeconds` (default 30, or 5 in Testing unless overridden)
- Seed admin:
  - `Hawk__SeedAdmin__Email`
  - `Hawk__SeedAdmin__Password`
- Email:
  - `Hawk__Email__Enabled` (default true)
  - `Hawk__Email__From` (or `Hawk__Resend__From`)
  - `Hawk__Resend__BaseUrl` (Resend-compatible API base URL)
  - `Hawk__Resend__ApiKey`
- URL checks:
  - `Hawk__UrlChecks__UserAgent` (default `firefox`; preset key or full UA string)
  - `Hawk__UrlChecks__UserAgentPresets__<key>` (override built-in preset values)
- Monitoring:
  - `Hawk__Monitoring__RunRetentionDaysDefault` (default 90; server-wide fallback for run history retention)
- Alerting:
  - `Hawk__Alerting__RepeatFailureAlertEveryHours` (default 24; how often to re-send failure reminders while a monitor is still failing, min 1, max 720)

## Git Workflow

- User requested: create git commits for major changes going forward.
- Keep commits scoped:
  - Infra (Docker/compose) separate from app features
  - E2E harness changes separate from app behavior when possible

## Refactoring Notes (Important)

- Monitor ownership:
  - `Monitor.CreatedByUserId` stores the Identity user id (not the email/username).
  - Implemented in `Hawk.Web/Pages/Monitors/Create.cshtml.cs` using `UserManager.GetUserId(User)`.
- Scheduler vs runner state:
  - `MonitorScheduler` updates `NextRunAt` when enqueuing due monitors.
  - `MonitorRunner` is the only place that updates `LastRunAt` (including invalid config runs).
- EF include behavior:
  - `MonitorRunner` uses `.AsSplitQuery()` when loading `Monitor.Headers` and `Monitor.MatchRules` to avoid cartesian explosion.

## Removed Features

- Privacy page (`/Privacy`) — removed; no longer in nav or routes.
- External auth provider buttons — removed from Login and Register Identity pages (app uses local accounts only).
- Admin user Create page (`/Admin/Users/Create`) — replaced by Edit page (`/Admin/Users/Edit/{id}`).

## Local Tooling Quirks

- `dotnet test -q` sometimes surfaces MSBuild cache/directory errors in this repo; use `dotnet test Hawk.sln -v:m` if you hit that.
