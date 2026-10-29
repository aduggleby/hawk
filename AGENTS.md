# Hawk Project Notes (For Agents)

This repository is building an ASP.NET Razor Pages uptime checker and URL verifier with Hangfire-based scheduling, SQL Server or PostgreSQL storage, and Docker-first deployment.

## Current State (As Of 2026-10-29)

- Solution: `Hawk.sln`
- Web app: `Hawk.Web` (Razor Pages + ASP.NET Core Identity)
- Unit tests: `Hawk.Tests` (xUnit)
- E2E tests: `e2e` (Playwright, Chromium; dockerized headed runs; SQL Server and PostgreSQL stacks)
- Monitor types: HTTP checks and heartbeat (push) monitors (see "Heartbeat Monitors")
- Mock server: `Hawk.MockServer` (deterministic endpoints + Resend-compatible `/emails` capture + `/flaky` alternating endpoint)
- UI: Tailwind CSS v4 with custom component classes (`hawk-btn`, `hawk-card`, etc.), dark mode support, mobile nav drawer. Bootstrap has been removed.
- Database: SQL Server (default) or PostgreSQL 17, selected by `Hawk:Database:Provider` (see "Database Providers")
- SQLite: not used (previous experimentation, if any, should not be reintroduced unless explicitly requested)
- Version: `0.12.0`

## Ports

Reserved ports for this project:

- Web: `17800` (container port `8080`)
- Mock server: `17801` (container port `8081`)
- SQL Server (dev compose only): `17833` (container port `1433`)
- PostgreSQL (`docker-compose.postgres.yml` only): `17834` (container port `5432`)

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

- The app currently calls `db.Database.MigrateAsync()` during startup seeding (both providers).
- Goal: keep this behavior for container deployments (web container applies migrations against the database container at boot).
- Current behavior includes retries (20 attempts, backoff up to 10s) to handle the database still starting.

## Database Providers

- `Hawk:Database:Provider` (env `Hawk__Database__Provider`): `SqlServer` (default; missing/empty also means SqlServer) or `Postgres`, case-insensitive. Any other value throws at startup. Parsed by `DatabaseSetup.GetProvider` in `Hawk.Web/Infrastructure/DatabaseSetup.cs`.
- One connection string, `ConnectionStrings:DefaultConnection`, for EF Core, Identity and Hangfire.
- EF Core registration (`DatabaseSetup.AddHawkDbContext`):
  - SqlServer: `ApplicationDbContext` with `UseSqlServer`.
  - Postgres: `PostgresApplicationDbContext` (a subclass with no model changes) registered as `ApplicationDbContext` with `UseNpgsql`. Application code always depends on `ApplicationDbContext`.
- Hangfire storage (`DatabaseSetup.UseHawkStorage`): `Hangfire.SqlServer` for SqlServer; `Hangfire.PostgreSql` (schema `hangfire`, `PrepareSchemaIfNecessary=true`, 5 min sliding invisibility timeout, 5s queue poll) for Postgres. `CommandBatchMaxTimeout`, `UseRecommendedIsolationLevel` and `DisableGlobalLocks` have no PostgreSQL equivalent. `PostgreSqlDistributedLockException` derives from `DistributedLockTimeoutException`, so the scheduler/alert lock handling works unchanged on both.
- Identity registration lives in `IdentitySetup.AddHawkIdentity` (`Hawk.Web/Infrastructure/IdentitySetup.cs`) and is shared with the EF design-time factories, because `AddDefaultIdentity` sets `Stores.MaxLengthForKeys = 128`, which changes the Identity part of the model.
- Model rules (keep the model provider-agnostic):
  - No column types or provider-specific SQL in `OnModelCreating`; no raw SQL anywhere (`scripts/` diagnostics are SQL Server only and not used by the app).
  - String comparisons in LINQ: SQL Server's default collation is case-insensitive, PostgreSQL's is case-sensitive. For user-facing sorting/dedupe either sort/compare in memory with `StringComparer.OrdinalIgnoreCase` or use `.ToLower()` on both sides. Identity lookups use the normalized columns and are fine.
  - `DateTimeOffset` values must be UTC (offset 0); Npgsql rejects other offsets for `timestamptz`. Use `DateTimeOffset.UtcNow`; normalise parsed input with `ToUniversalTime()` (see `StatusCakeImporter`).
  - PostgreSQL `text` rejects NUL (`\0`) characters (error 22021; SQL Server accepts them). Response snippets and request body snippets replace `\0` with U+FFFD (`UrlChecker.MakeSnippet`, `MonitorExecutor`); `MonitorForm.Validate` and bulk import reject `\0` in user input.
  - NULL ordering differs (SQL Server: first in ASC, PostgreSQL: last). Order nullable columns explicitly (see `MonitorScheduler`: `OrderBy(m => m.NextRunAt == null ? 0 : 1).ThenBy(m => m.NextRunAt)`).

- Upgrades are one-way: `ReplaceRowVersionWithConcurrencyStamp` (0.9.27) drops `RowVersion`, and 0.9.26 fails against the migrated schema. Back up the SQL Server database (native `BACKUP DATABASE`) before upgrading; rollback = restore + older image tag.

### Migrations (two sets)

Microsoft's "one context type per provider" approach (single assembly, separate folders):

- SQL Server: `Hawk.Web/Data/Migrations/` bound to `ApplicationDbContext` (the existing history; never regenerate or rename these).
- PostgreSQL: `Hawk.Web/Data/Migrations/Postgres/` bound to `PostgresApplicationDbContext` (starts with `InitialPostgres`).
- EF matches migrations to the exact context type, so the sets never mix.
- EF Core packages (`SqlServer`, `Design`, `Tools`, `InMemory`) and the `dotnet-ef` tool manifest are all 10.0.4 (Npgsql EF 10.0.3 requires EF Core 10.0.4). `Design` 10.0.4 needs `Microsoft.VisualStudio.Web.CodeGeneration.Design` 10.0.2+ (10.0.0 causes NU1608 Roslyn conflicts). Keep them on one patch level.
- Design-time factories (`Hawk.Web/Data/DesignTimeDbContextFactories.cs`) create each context without starting the app or a database, independent of `Hawk:Database:Provider`.

After every model change, add a migration for **both** providers (from the repo root, with `dotnet tool restore` done once):

```bash
dotnet ef migrations add <Name> --project Hawk.Web --context ApplicationDbContext --output-dir Data/Migrations
dotnet ef migrations add <Name> --project Hawk.Web --context PostgresApplicationDbContext --output-dir Data/Migrations/Postgres
```

`DatabaseSetupTests.Migrations_are_up_to_date_with_the_model` fails when either set is missing a model change (startup would otherwise fail with `PendingModelChangesWarning`).

## Health Endpoints

- `GET /live`: process up, runs no checks (no DB). Always `200 Healthy`.
- `GET /ready`: `DatabaseHealthCheck` (`db.Database.CanConnectAsync`, 5s timeout); `200 Healthy` or `503 Unhealthy`.
- Both are mapped with `MapHealthChecks(...).AllowAnonymous()` in `Program.cs`; request logging for them is at `Verbose` so probes don't flood the log.
- Plain-HTTP probes need `Hawk__DisableHttpsRedirection=true` (otherwise they get a redirect). The app server's Caddy health-checks `/ready` and blocks `/ready` and `/health/*` publicly.

## Allowed Status Codes

- `Monitor.AllowedStatusCodes` — optional comma-separated list of additional HTTP status codes treated as success (e.g. `404,429`).
- 2xx codes are always success regardless of this setting.
- Parsed by `Hawk.Web/Services/Monitoring/AllowedStatusCodesParser.cs` (validates range 100–599, normalizes/deduplicates).
- Evaluated in `MonitorExecutor` when determining run pass/fail.
- Shown on Create/Edit forms and on the monitor Details page.
- With `Monitor.FollowRedirects = false` a 3xx response is checked itself, so it passes only when its code is listed here (for example `301`).

## URL Checking Core

Implemented v1 URL checker service in `Hawk.Web/Services/UrlCheckModels.cs`:
- Supports GET/POST/PUT/PATCH (POST includes body + content-type).
- Supports custom headers.
- Supports content verification rules:
  - `Contains` (case-insensitive)
  - `Regex` (case-insensitive, singleline; 2s match timeout)
- Buffers up to 256KB of response for matching/snippet.
- Redirects: `Monitor.FollowRedirects` (bool, default true; migration column default true, `UrlCheckRequest.FollowRedirects`). `Program.cs` registers two named clients, `urlchecks` and `urlchecks-noredirect` (same config, `SocketsHttpHandler { AllowAutoRedirect = false }`); `UrlChecker(httpClient, noRedirectClient)` picks by the request and returns an error result (never a silent follow) if no no-redirect client is given. Without following, the 3xx response's headers (`Location`) show in run diagnostics and contains/regex rules run against the 3xx body. Form checkbox on Create/Edit (HTTP only, Create/Edit parity), shown on Details; heartbeats store true. JSON export writes `followRedirects` for HTTP monitors only (still format v3; a missing field imports as true, unknown fields are ignored by the parser); StatusCake import and the seeder use true. Mock server: `GET /redirect[?code=302]` redirects to `/ok` (301 default).

## Unit Tests

`Hawk.Tests/UrlCheckerTests.cs` covers:
- GET `https://example.com` with contains match
- GET `https://example.com` with regex match
- POST `https://example.com` returns a failure result but should not throw

Database-related tests:
- `DatabaseSetupTests` — provider parsing/validation, per-provider context registration, disjoint migration sets, no pending model changes for either provider (no DB needed).
- `ConcurrencyStampTests` — concurrency token configuration and semantics (EF InMemory).
- `PostgresIntegrationTests` — real PostgreSQL 17 via Testcontainers (`postgres:17-alpine`, needs Docker; skipped via `SkippableFact` when Docker is unavailable, **failed** instead when `HAWK_REQUIRE_POSTGRES_TESTS=1` is set — use that in CI/release checks: `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test Hawk.sln -v:m`): migrations, Edit page concurrency (no conflict after a scheduler tick + executor run, conflict after a concurrent edit or pause), scheduler NULL ordering, Hangfire.PostgreSql alert lock timeout, StatusCake case-insensitive dedupe + UTC timestamps, `/ready` check.

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
- PostgreSQL: same suite and topology on `postgres:17` (tmpfs, project `hawk-e2e-postgres`):
  `docker compose -f docker-compose.e2e.postgres.yml down --remove-orphans; docker compose -f docker-compose.e2e.postgres.yml up --build --force-recreate --renew-anon-volumes --exit-code-from e2e e2e; docker compose -f docker-compose.e2e.postgres.yml down --remove-orphans`
  - Note: this writes screenshots to host `./screenshots` (mounted into the Playwright container).
  - Preferred: `npm --prefix e2e test` which performs `docker compose down` first to guarantee a fresh DB each run.

## URL Routing

- Lowercase URLs are enforced via `AddRouting` options in `Program.cs`. Query strings are **not** lowercased (`LowercaseQueryStrings = false`): Identity confirmation/reset tokens are case-sensitive and lowercasing them made every emailed link invalid.

## HTTPS Redirection Toggle (For E2E)

`Hawk.Web/Program.cs` has a guard:
- `Hawk:DisableHttpsRedirection` (env `Hawk__DisableHttpsRedirection`)

E2E uses HTTP because Playwright `webServer.url` expects a stable plain URL.

## Forwarded Headers / Reverse Proxy

- `app.UseForwardedHeaders()` is called early in the pipeline (before auth) to consume `X-Forwarded-For`, `X-Forwarded-Host`, and `X-Forwarded-Proto`.
- `KnownIPNetworks` and `KnownProxies` are cleared so that any upstream proxy address is trusted — appropriate for container and appliance deployments where the proxy IP is not stable.
- Auth cookie secure policy (`CookieSecurePolicy`): `Always` by default; set to `SameAsRequest` when `Hawk:DisableHttpsRedirection=true`. This ensures cookies work over plain HTTP inside the container network while TLS is terminated by the proxy.
- Auth cookie name: `Hawk.Auth`; `HttpOnly=true`, `IsEssential=true`, `SameSite=Lax`.
- Auth challenges and principal validations are logged at `Warning`/`Debug` level under the `Hawk.Auth` logger to aid login diagnostics.

## Docker / Database / Hangfire

Implemented architecture:
- `docker-compose.yml` runs:
  - `web` (ASP.NET)
  - `db` (SQL Server)
  - `mock` (optional mock server; useful for testing alerting)
- `docker-compose.postgres.yml` (project `hawk-postgres`) is the same stack on `postgres:17` with `Hawk__Database__Provider=Postgres` (same web/mock ports; run one stack at a time).
- App uses the configured database for:
  - Identity
  - Hangfire storage
  - Monitoring configuration + check history
- The web container:
  - Multi-stage Docker build: `node:20-alpine` stage compiles Tailwind CSS, then .NET SDK stage builds the app
  - Runs as the non-root .NET user (`USER $APP_UID`, UID 1654). The image creates and chowns `/app/logs`, `/var/lib/hawk/dpkeys` and `/var/lib/hawk/keys`, so installs without volumes and fresh named volumes work. Volumes/host paths created by older (root) images must be `chown -R 1654:1654` (startup logs an error if key files are unreadable, a warning if the keys directory is not writable).
  - Read-only root filesystem: supported with `--read-only --tmpfs /tmp`, a writable DataProtection keys volume, and either `Hawk__Logging__File__Enabled=false` or `Hawk__Logging__File__Path` on a writable volume (the app server uses `/var/log/hawk`). Nothing else is written at runtime (Tailwind is built into the image; large form uploads buffer to `/tmp`).
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

## Timestamp Rendering (Browser Local Time)

- All timestamps shown in the UI (monitors index last-run/next-run columns, monitor detail run history, run diagnostics page) are rendered in the **browser's local time zone** via `data-utc` HTML attributes.
- Server-side Razor renders a `<time data-utc="ISO8601-UTC" data-utc-format="short|detailed">fallback UTC string</time>` element. The JS in `wwwroot/js/site.js` rewrites the text content using `Intl.DateTimeFormat` on page load.
- `data-utc-format` accepts `"short"` (date + time, no seconds) or `"detailed"` (date + time + seconds + timezone name). Defaults to `"short"` if omitted.
- Hovering over any timestamp shows a tooltip with both the local time and UTC time.
- The server-side fallback text (shown until JS runs) is the UTC value formatted as `"g" UTC`.

## Error Page

- `Hawk.Web/Pages/Error.cshtml(.cs)` — styled error page; Production shows only a generic message and request ID, with exceptions logged server-side.
- Only used in non-Development environments (Development uses the built-in developer exception page).
- Non-Production diagnostics display the failing path, exception type/message, stack trace, and a **Copy technical details** button. These fields, controls, and their scripts are absent in Production.
- Exception capture uses `IExceptionHandlerPathFeature`, `HttpContext.Items["Hawk.Exception"]` (set by middleware in `Program.cs`), and `IExceptionHandlerFeature`.

## Flash Messages

- `Hawk.Web/Pages/Shared/_Flash.cshtml` renders flash messages from `TempData["FlashError"]` and `TempData["FlashInfo"]`.
- Included in `_Layout.cshtml` so flash messages appear on any page after a redirect.
- Registration uses flash messages for email delivery errors. Anonymous ForgotPassword and ResendEmailConfirmation actions return generic responses and log provider failures so they do not expose account existence or exception details.

## Account Security

- `AuthCookieSetup.AddHawkApplicationCookie` preserves Identity's security-stamp validator while adding Hawk logging. Validation runs on the first authenticated request after one minute, rejects reset/deleted-account cookies, and refreshes role claims.
- `AuthAbuseProtection` rate-limits only login and anonymous password-reset/confirmation-resend POSTs by the forwarded client IP; rejected requests return 429 and `Retry-After`.
- `Hawk:Security` settings: `LoginRequestsPerMinute` (10), `EmailRequestsPer15Minutes` (5), `FailedLoginAttempts` (5), `LockoutMinutes` (5), `EmailCooldownMinutes` (1). Invalid out-of-range values fail startup.
- Anonymous account-email actions share a bounded, expiring `AccountEmailCooldown` keyed by user ID. Scheduled monitor alerts do not use it.

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
  - **Profile** (`/Identity/Account/Manage/Index`) — display name and email; unconfirmed accounts get a **Send confirmation email** button (`OnPostSendConfirmationAsync`, covered by `e2e/tests/email-confirmation.spec.ts`).
  - **Security** (`/Identity/Account/Manage/ChangePassword`) — change password.
  - **Alerts & Crawler** (`/Identity/Account/Manage/Settings`) — alert email override + User-Agent override + run retention override.
  - **API keys** (`/Identity/Account/Manage/ApiKeys`) — create (name + `read`/`write` scope; plaintext shown once on the create response), list (display prefix, created, last used) and revoke one/all. See "Agent API".
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
- **Distributed alert lock** — before evaluating/updating `MonitorAlertState`, the executor acquires a Hangfire distributed lock keyed `hawk:monitor-alert:{monitorId}` with a 60-second timeout. This prevents duplicate alerts when multiple Hangfire workers check the same monitor simultaneously. If the lock is busy (timeout), alert evaluation is skipped for that run and the run records `AlertError = "Alert state lock busy; skipped alert evaluation to avoid duplicate alert."`.
- **Failure log in alert emails** — failure and failure-reminder emails include a log of up to 20 recent failure entries (start time + HTTP status code) from the current incident, fetched by `GetFailureLogEntriesAsync`.

## Run Retention

- `Monitor.RunRetentionDays` — optional per-monitor run history retention override (1–3650 days).
- `UserMonitorSettings.RunRetentionDays` — optional account-wide retention default, stored in `Hawk.Web/Data/Monitoring/UserMonitorSettings.cs`.
- Resolution order (in `MonitorExecutor.ResolveRunRetentionDaysAsync`):
  1. Per-monitor `RunRetentionDays`.
  2. Account-wide `UserMonitorSettings.RunRetentionDays`.
  3. Server default `Hawk:Monitoring:RunRetentionDaysDefault` (default 90).
- Pruning runs `ExecuteDeleteAsync` on `MonitorRuns` older than the cutoff after every run.

## Alert State Seeding And LastRunAt

- `MonitorExecutor.GetOrCreateAlertStateAsync` seeds a missing `MonitorAlertState` from history via `MonitorAlertingDecider.SeedFromHistory`, using only `State == "completed"` runs and excluding the run being evaluated (the pre-created "Run now" run is saved as `running` before alert evaluation and must not be counted).
- `Monitor.LastRunAt` is written set-based (`ExecuteUpdateAsync`, `SetLastRunAtAsync`) so it never conflicts with (or renews) the monitor's concurrency stamp. A conflict after the alert email was sent would fail the save and lose the run record and alert-state update.

## Failure Alert Retry Rule

- `MonitorAlertingDecider.OnFailure` returns `Failure` when the consecutive-failure count equals the threshold, and also whenever the count is above the threshold while `LastFailureAlertSentAt` is null (first send failed, or the threshold was lowered in Edit while failing). The alert therefore retries on every failing run until a send succeeds; after that it is sent once, reminders keep their N-hour cadence and recovery works as normal.

## Monitor Edit Concurrency

- `Monitor.ConcurrencyStamp` and `MonitorAlertState.ConcurrencyStamp` — app-managed `Guid` concurrency tokens (`.IsConcurrencyToken()`), provider-agnostic. They replaced the SQL Server `rowversion` columns (migration `ReplaceRowVersionWithConcurrencyStamp`; existing rows get `Guid.Empty`, which is fine).
- Why: a `rowversion` changes on every write, including scheduler/executor bookkeeping, so the Edit page reported a spurious conflict whenever a run finished while the form was open.
- The stamp is renewed (`Guid.NewGuid()`) only on intentional changes:
  - Monitor: create/import/StatusCake import (entity default), Edit save, pause/resume (index "Pause all"/"Resume all"/batch, set in the same `ExecuteUpdateAsync`).
  - MonitorAlertState: `MonitorExecutor` renews it when a run actually changed the state (checked via the change tracker).
  - Never on `NextRunAt`/`LastRunAt` bookkeeping (scheduler and executor write those set-based).
  - The Delete page deletes set-based (`ExecuteDeleteAsync`; children cascade in the database), so it never fails on a stamp change.
  - The StatusCake alert import saves runs and then advances `LastRunAt` set-based in one transaction (`StatusCakeImporter.SaveAlertImportAsync`); the InMemory provider (unit tests) falls back to a tracked update.
- `MonitorForm.ConcurrencyStamp` carries the token through the Edit form as a hidden field; `Edit.cshtml.cs` sets it as the original value, renews it, and catches `DbUpdateConcurrencyException` with a "changed by someone else" flash message.

## Monitors Index — Failing Group

- The monitors index page groups monitors into two sections: **Failing** (most recent run failed) and **All monitors**.
- Failing monitors are queried separately using a join on the most recent `MonitorRun` where `Success == false`.
- Failing monitors appear at the top in a distinct section so operators can immediately see what needs attention.

## Monitors Index — Batch Actions

- The monitors index supports batch actions on selected monitors via checkboxes.
- When monitors are selected, the default action bar (Import StatusCake, Pause all, Resume all, New monitor) is replaced by a selection action bar with: **Run now**, **Pause**, **Resume**, and **Uncheck all**.
- **Batch Run now** enqueues a Hangfire `RunAsync` job for each selected monitor and shows a flash message with the count of queued runs.

## Monitor Detail Page

- `Hawk.Web/Pages/Monitors/Details.cshtml(.cs)` shows monitor configuration, headers, match rules, and the 25 most recent runs.
- **Run now** button enqueues an immediate Hangfire job for the monitor (reason `"manual"`), creates a `MonitorRun` record up front (state `"queued"`), and redirects to the run diagnostics page which auto-refreshes until the run completes.
- Each run in the history table links to the run diagnostics page.
- Runs that are not yet completed show `QUEUED` or `RUNNING` badges instead of OK/FAIL.

## MonitorRun State

- `MonitorRun.State` — a `[MaxLength(16)]` string property tracking run lifecycle: `"queued"` → `"running"` → `"completed"`.
- When a run is triggered via **Run now**, a `MonitorRun` is created with state `"queued"` before the Hangfire job is enqueued. The executor transitions it through `"running"` → `"completed"`.
- Scheduled runs (no pre-existing `runId`) create a new `MonitorRun` as before and go directly to `"completed"`.
- The run diagnostics page (`Runs/Details.cshtml`) auto-refreshes (1s for the first 30s, then every 15s) until the run state is `"completed"` or the monitor timeout is exceeded.
- The monitors index last-run-success query filters to only `State == "completed"` runs.

## Monitor JSON Export / Import

- `Hawk.Web/Pages/Monitors/MonitorJsonPort.cs` contains the JSON model (`MonitorExportEnvelope`, `MonitorExportModel`) and mapping helpers.
- **Format v2** (`MonitorJsonPort.CurrentVersion = 2`): envelope `{ version, exportedAtUtc, monitors[] }`; each monitor has the config fields, `headers`, `matchRules` and an optional `ownerEmail`. Excluded: ids, run history, alert state, `LastRunAt`/`NextRunAt`/`CreatedAt`, `ConcurrencyStamp`, per-user settings. Headers/match rules are written in a deterministic order.
- The parser (`TryParse`) accepts v1 files (no `ownerEmail`) and three shapes: an envelope with a `monitors` array, a bare array, or a single object. Versions above 2 are rejected.
- **Single monitor export** — `Details.cshtml.cs` `OnGetExport` (emits v2 without `ownerEmail`). **Single import** — `Create.cshtml.cs` `OnPostImport` prefills the create form with the first monitor (unchanged).
- Export uses `System.Text.Json` with `JsonStringEnumConverter` (camelCase) and `WriteIndented`.

## Bulk Transfer (Admin: Export / Import all monitors)

- Page: `Hawk.Web/Pages/Admin/Transfer.cshtml(.cs)` (`/Admin/Transfer`, covered by the `/Admin` folder `AdminOnly` policy, linked in the Account menu as **Export / Import**).
- Logic lives in `Hawk.Web/Services/Import/MonitorTransferService.cs` (scoped). It uses plain EF Core LINQ only (no raw SQL) and compares strings in C#, so it is database-provider-agnostic.
- **Export all** (`?handler=Export`) downloads `hawk-monitors-<yyyyMMdd-HHmmss>.json`: every monitor of every user ordered by name, with `ownerEmail` resolved from `CreatedByUserId`.
- **Import** is two steps: upload -> dry-run preview (`PlanAsync`, writes nothing), then **Confirm import**. The JSON is carried in a hidden field (max 2 MB) and is re-parsed and re-validated on confirm (`ImportAsync`); preview state is never trusted.
- Planning (`MonitorTransferService.Plan`, pure and unit tested): duplicates = same trimmed Name + Url, case-insensitive, against existing monitors and earlier entries in the file (duplicates are skipped before validation); other monitors go through `MonitorJsonPort.TryCreateMonitor` (which runs `MonitorForm.Validate`). Owner = user whose email matches `ownerEmail` (case-insensitive), else the importing admin.
- **All or nothing:** if any non-duplicate monitor is invalid, the confirm step refuses and imports nothing. Inserts run in one `BeginTransactionAsync` transaction.
- Option **Create all imported monitors paused** (default on): forces `IsPaused = true`; when off, source `IsPaused` is kept. Source `Enabled` is always kept. `NextRunAt` is left null (due immediately once unpaused).
- Re-running the same import is safe: everything is skipped as a duplicate. After import a flash reports imported / skipped / paused.
- **Validation:** imported monitors pass `Validator.TryValidateObject` on `MonitorForm` (`[Required]/[MaxLength]/[Range]`) plus `MonitorForm.Validate`, header name/value length (200/2000) and match pattern length (2000). These are hard errors listed per monitor in the preview, and the confirm refuses. An unparsable regex is only a per-item preview **warning** (the monitor imports and fails every run until fixed). Create/Edit reject invalid regex patterns via `MonitorForm.Validate` (key `Form.MatchPatterns[i]`).
- **Serialisation:** `ImportAsync` takes an app-wide (single-instance) `SemaphoreSlim`, re-plans inside it, then inserts in a transaction, so double submits cannot insert twice (there is no unique index). The confirm button is also disabled on submit. Export is sorted in memory with ordinal comparison (collation independent). Size limit (2 MB) is measured after normalising CRLF to LF. `null` monitor lists/entries are parse errors.
- Exports from a `Testing` instance that use the 5-second interval are refused by Production (the interval is not allowed there); change those intervals before importing.
- Tests: `Hawk.Tests/MonitorTransferTests.cs`, `e2e/tests/monitor-transfer.spec.ts`.

## Heartbeat Monitors (Push / Dead Man's Switch)

A second monitor type (`Monitor.Type` = `MonitorType.Http` or `Heartbeat`; immutable after creation). The client calls `GET|HEAD|POST /ping/{token}`; if no ping arrives within period + grace the monitor fails and uses the normal alerting (threshold, reminders, recovery, recipients). Plan: `docs/plans/2026-10-23-heartbeat-monitors-plan.md`.

- Fields on `Monitor`: `Type`, `HeartbeatToken` (32 lowercase hex = 128 random bits, unique index, NULL for HTTP), `HeartbeatGraceSeconds`, `LastPingAt`. `IntervalSeconds` is the period; heartbeats store `Url = ""`, `Method = "GET"`. `HeartbeatTokens` (`Services/Monitoring/HeartbeatTokens.cs`): `Generate`, `TryNormalize` (lowercase + 32-hex check, always before any DB lookup), `InitialDeadline`.
- Periods: 1 min, 5 min, 15 min, 1 h, 6 h, 12 h, 1 day, 7 days (+5s in `Testing`). Grace: 1 min, 5 min, 15 min, 1 h, 6 h, 1 day (+0s and 5s in `Testing`). Defined in `MonitorIntervals`.
- **Invariant 1: the scheduler never writes a heartbeat's `NextRunAt`** (it is the deadline). Only create/import/resume/re-enable/edit-of-period-or-grace (`now + period + grace`), a ping (`now + period + grace`) and a recorded miss (`now + period`) write it. The scheduler's due query is unchanged (overdue heartbeats are enqueued every tick until a job moves the deadline); its set-based bump is filtered to `Type == Http`. Index resume/Resume all update heartbeats first, grouped by `(IntervalSeconds, HeartbeatGraceSeconds)` with a precomputed UTC deadline, in the same statement that unpauses them.
- **Invariant 2: every heartbeat state change happens under the Hangfire lock `hawk:monitor-alert:{id}`, after reloading** the monitor and alert state. Missed check (`MonitorExecutor.ExecuteAsync`, reason `schedule`, always returns null): lock timeout (10s) = record nothing; exits if paused/disabled/not due (`NextRunAt > now`, which covers stale jobs after resume or a ping in between); records a FAIL run "No ping received since ...", sets `NextRunAt = now + period`, runs the shared alert evaluation (`EvaluateAlertsAsync`). Ping (`IMonitorExecutor.RecordPingAsync`, inline in the request, not enqueued): lock (5s), reload, paused/disabled = `200 "paused"` and nothing written, else one transaction with the OK run (reason `ping`; diagnostics hold method, `/ping/{token}` placeholder, User-Agent, client IP), `LastPingAt`/`NextRunAt` and the recovery alert. Ping lock timeout writes nothing and returns `503` with `Retry-After: 5` (`curl --retry` retries it). Edit, pause/resume and Regenerate do not take the lock, so both heartbeat paths claim their write with a conditional set-based update on the observed `ConcurrencyStamp` (miss: also on the observed `NextRunAt`; ping: also on the token) before any alert/email; 0 rows = miss exits, ping reloads once and then returns 404 (token gone) or 503. Every user change (Edit, pause/resume, Regenerate) must renew `ConcurrencyStamp`. `reason == "manual"` returns null; Details **Run now**, run diagnostics **Run again** and batch **Run now** exclude heartbeats (batch reports queued and skipped).
- Endpoint: `Infrastructure/HeartbeatEndpoints.cs` (`MapHawkHeartbeats`, anonymous minimal API). Unknown or malformed token = 404; malformed tokens never reach the executor. The handler ignores `RequestAborted` (15s own timeout) so a client abort cannot leave a half-applied ping. Email is registered once via `EmailSetup.AddHawkEmail` as a typed client with a 5s timeout (recovery email is sent inside the request; clients typically use `curl -m 10`).
- Rate limiting (`AuthAbuseProtection`, chained with the login/email limiter): `Hawk:Security:PingRequestsPerMinute` (30, per normalized token, valid-format tokens only) and `PingRequestsPerMinutePerIp` (300, any `/ping/*` request incl. malformed). 429 with `Retry-After`.
- **Keep tokens out of logs**: Serilog request logging redacts `/ping/*` to `/ping/{redacted}` (`HeartbeatEndpoints.RedactPath`); `Microsoft.AspNetCore.Hosting.Diagnostics` and `HttpsPolicy` are `Warning` in `appsettings.json`; the executor logs only the monitor id on ping errors. Caddy (infra repo) must `log_skip /ping/*` in `apps/hawk/site.caddy` (Caddy 2.11), committed and pushed before deploy.
- Form: `MonitorForm.Url` is `string?` without `[Required]`; `Validate` requires the URL only for Http and checks period/grace for heartbeats. Create has the type selector (JS shows/hides sections; the two interval selects share the field name `Form.IntervalSeconds`, the inactive one is disabled). Edit takes the type from the **persisted** entity (never the form), only resets the deadline when period, grace or `Enabled` (false to true) change, and has a **Regenerate URL** POST handler (new token, old URL 404s).
- Details shows `{Request.Scheme}://{Request.Host}/ping/{token}` with a copy button, a `curl -fsS -m 10 --retry 3` example, last ping and deadline.
- Alert emails have heartbeat wording (subject `(NO_PING)` / `(PING_RECEIVED)`, failure log lists times only). **Inherited fix:** `MonitorAlertingDecider.OnSuccess` uses `wasAlertedIncident = state.LastFailureAlertSentAt is not null`, so raising the threshold during an alerted incident no longer suppresses the recovery email.
- JSON export **v3** (`type`, `heartbeatGraceSeconds`, `heartbeatToken`; v1/v2 files still parse). Bulk transfer identity for heartbeats: a valid token = same monitor (skipped if the token exists in the DB or earlier in the file; same-name heartbeats with different tokens both import and a moved monitor keeps its URL); without a (valid) token dedupe by trimmed name and issue a new token. Single import (Create prefill) always issues a new token. Imported heartbeats get a deadline. `StatusCakeImporter` matching/dedupe is restricted to `Type == Http`.
- Seed: `[Seed] Heartbeat` (paused, 1 h + 5 min grace).
- Tests: `Hawk.Tests/HeartbeatTests.cs` (pure logic), `HeartbeatHttpTests.cs` (Create page POSTs), heartbeat cases in `PostgresIntegrationTests.cs` (real executor/endpoint handler, ExecuteUpdate, unique index, locks), limiter cases in `AuthAbuseProtectionTests.cs`, `e2e/tests/heartbeat.spec.ts`.

## Agent API

A versioned JSON API under `/api/v1` for agents (e.g. Claude Code) using per-user API keys. Reference for callers: `docs/agent-api.md`. Plans: `docs/plans/2026-10-29-0654-feat-agent-api-plan.md` (0.11.0), `docs/plans/2026-10-29-1000-feat-agent-api-monitor-crud-plan.md` (0.12.0 create/edit/delete).

- Files: `Infrastructure/ApiEndpoints.cs` (`MapHawkApi`, thin handlers), `ApiModels.cs` (response records, `ApiJson.Options`: web defaults + camelCase enums, nulls kept), `ApiKeyAuthentication.cs` (scheme, policies, handler), `ApiErrors.cs` (problem+json writer, `UseHawkApiErrors`), `Services/ApiKeys/ApiKeyService.cs`, `Data/ApiKeys/ApiKey.cs`, `Pages/.../Manage/ApiKeys.cshtml`. Shared services used by both UI and API: `MonitorStatusService`, `MonitorCommands`, `MonitorEditor`, `AlertRecipientResolver`, `AccountAlertSettings`, `AlertSettingsValidator`.
- Keys: `hawk_` + 43 base64url chars (32 random bytes); only the SHA-256 hash is stored (unique index) plus a 12-char non-secret `DisplayPrefix`. Plaintext is shown once, never put in TempData or logs. `LastUsedAt` is touched at most every 5 minutes. A key acts as its user (all monitors visible, no roles). Revoked by the user, by any password reset (admin or Forgot password), and deleted with the user. Lockout and password change do not revoke.
- Auth: scheme `ApiKey` is never a default; policies `ApiRead` (read or write scope) and `ApiWrite` (write scope) pin the scheme, so a login cookie never authorizes `/api` and a key never authorizes pages. Only `Authorization: Bearer` is read (query string ignored). Challenge = 401 `unauthorized`, forbid = 403 `insufficient_scope`.
- Errors: every `/api/*` response is JSON. `UseHawkApiErrors` (after the exception handler, before routing) turns exceptions into 500 `internal_error` and empty 400/404/405/415 into problem+json (`validation_failed`, `not_found`, `method_not_allowed`). There is deliberately no `/api/{**path}` catch-all route (it would turn 405s into 404s); unknown `/api` paths get the middleware's 404. Write handlers parse their own body (`ReadBodyAsync`: JSON object, max 16 KB, no duplicate properties) because minimal-API binding rejects bad bodies with an empty response.
- Limits: `AuthAbuseProtection` chain adds a per-IP limiter for any `/api/*` request (`ApiRequestsPerMinutePerIp`, 600) and a per-key limiter keyed on the SHA-256 of well-formed keys (`ApiRequestsPerMinute`, 120); 429 `rate_limited` with `Retry-After`.
- Invariants: PATCH is a merge patch (property presence, not nullable DTOs); pause/resume/PATCH are set-based and renew `ConcurrencyStamp`, so an open Edit form detects them; heartbeat resume sets the deadline via the same path as the UI (invariant 1 of "Heartbeat Monitors" holds); run-now returns the in-flight manual run (started within timeout + 60 s) instead of queuing a duplicate, rejects disabled monitors and heartbeats with 409; the ping token is never returned. Write handlers log `user id, key id, monitor id, action` at Information (logger `Hawk.Api`). Key writes (touch, revoke) branch on `db.Database.IsRelational()` for EF InMemory tests.
- Monitor create/edit/delete (0.12.0): `Services/Monitoring/MonitorEditor.cs` (`CreateAsync`, `UpdateAsync(id, form, expectedStamp?, replaceHeaders, replaceMatchRules)`, `DeleteAsync(id, expectedStamp?)`) is the one implementation of entity mapping, heartbeat deadline rules and stamp handling for the Create/Edit/Delete pages and the API. It takes an already validated `MonitorForm` and is set-based for deletes (relational providers only). `UpdateAsync` forces the persisted type, renews the stamp, checks `expectedStamp` through the EF original value (conflict = `Conflict` outcome, no retry) and replaces children only when asked, so a merge patch that omits `headers` keeps more than the form's 5 slots. The API maps JSON to a `MonitorForm` in `Infrastructure/ApiMonitorBody.cs` (errors keyed by JSON field name; HTTP-only fields on a heartbeat, `heartbeatGraceSeconds` on HTTP, `heartbeatToken`/`ownerEmail`/`isPaused`, unknown fields and a type change are 400) and validates with `MonitorValidation` (attributes + child limits, shared with `MonitorJsonPort.TryCreateMonitor`) plus `MonitorForm.Validate`. PATCH builds the form from the persisted monitor first (merge patch) and passes `ifMatch ?? snapshot.ConcurrencyStamp` as the expected stamp, so a user change made since the snapshot is a conflict (409 `conflict` without If-Match, 412 with it) instead of being reverted by the stale form; scheduler/executor bookkeeping never renews the stamp. At most `MonitorForm.MaxHeaders`/`MaxMatchRules` (5, the form's slots, also used by the pages) headers and match rules are accepted, because the Edit page would silently drop more; a blank `alertEmailOverride` clears it. `followRedirects` is a bool (HTTP only). matchRules take `mode` (alias `type`, because the detail response reports `type`). `ApiMonitorDetail` has `concurrencyStamp` (also the quoted `ETag`) and `pingUrl`, which is set only in the create response of a heartbeat (the only place the token is returned). `If-Match` (quoted or bare GUID; malformed = 400) on PATCH/DELETE: mismatch = 412 `precondition_failed`; DELETE without it is unconditional like the Delete page. Write handlers log at Information (`monitor.create`, `monitor.update`); delete logs at Warning with the name.
- Tests: `MonitorEditorTests` (Postgres), `ApiKeyServiceTests`, `ApiAuthHttpTests`, `ApiKeysPageHttpTests`, `ApiHttpTests` (InMemory host: auth, scope, errors, reads), `ApiPostgresHttpTests` (pause/resume/PATCH/run over HTTP on Postgres), `MonitorStatusTests`, `e2e/tests/api.spec.ts`.

## Run Diagnostics Page

- `Hawk.Web/Pages/Monitors/Runs/Details.cshtml(.cs)` — displays full diagnostics for a single run:
  - Run metadata: result, reason, timestamps, duration, HTTP status, alert status, error.
  - Request: URL, method, content-type, timeout, headers JSON, body snippet.
  - Response: content-type, content-length, headers JSON, response snippet, match results JSON.
- Accessible from the run history table on the monitor detail page.

## Monitor Test Page

- `Hawk.Web/Pages/Monitors/Test.cshtml(.cs)` — compatibility GET/HEAD route that redirects to monitor details without executing.
- **Run now** and diagnostics **Run again** use antiforgery-protected POSTs to the monitor Details handler, queue a run, and redirect to passive saved diagnostics.

## Monitor Form Validation

- `Hawk.Web/Pages/Monitors/MonitorFormValidation.cs` maps custom `ValidationResult` errors to Razor `ModelStateDictionary` keys (prefixed with `Form.`).
- Create and Edit pages call `MonitorFormValidation.AddResults()` to surface server-side validation errors inline.
- Optional form sections (headers, match rules, advanced settings) are collapsed by default and expand via `<details>` elements.
- The POST options section (body, content-type) is hidden when the selected HTTP method is not POST, and shown dynamically via JavaScript when the user selects POST.
- **Create/Edit parity rule:** when changing monitor form UI/behavior on `Hawk.Web/Pages/Monitors/Create.cshtml`, apply the equivalent change to `Hawk.Web/Pages/Monitors/Edit.cshtml` (and vice versa) unless explicitly requested otherwise.

## Dev Tooling

### run-dev.sh / stop-dev.sh

- `run-dev.sh` starts the full local dev environment: restores .NET packages, installs/builds Tailwind, starts `db` + `mock` via Docker Compose, and runs `Hawk.Web` with `dotnet watch`.
- Runs in tmux by default (`hawk-dev` session); use `--no-tmux` for foreground mode, `--no-deps` to skip Docker services, `--postgres` to use PostgreSQL (`docker-compose.postgres.yml`, `localhost:17834`, `PG_PORT`/`POSTGRES_PASSWORD` overrides) instead of SQL Server.
- `stop-dev.sh` stops all processes started by `run-dev.sh` (dotnet watch, Docker containers of either stack, tmux session).

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
- Serilog file sink rolls daily to `<Path>/hawk-YYYYMMDD.log`; one file per day, so `retainedFileCountLimit` = retention in days.
- `Hawk:Logging:File:Enabled` (env `Hawk__Logging__File__Enabled`, default `true`): when `false`, no file sink is added (console only).
- `Hawk:Logging:File:Path` (default `logs`, i.e. `/app/logs` in the image) and `Hawk:Logging:File:RetainedDays` (default 30, clamped 1–3650).
- App server (prod1): per-app log directory convention, `Path=/var/log/hawk`, `RetainedDays=90`, bind-mounted from `/srv/dualconsult/data/hawk/logs` (see the infra repo's `templates/app/README.md`).
- Startup logs `Database provider: ... File logging: ...`.
- `Microsoft.EntityFrameworkCore.Database.Command` is at `Warning` in `appsettings.json` (Serilog `MinimumLevel.Override`), so per-tick scheduler queries are not logged; failed commands still log at `Error`.

## DataProtection Keys (Docker)

Problem:
- ASP.NET Core Identity cookies are protected using DataProtection keys.
- If keys are not persisted, a container restart invalidates existing auth cookies.

Implementation:
- When running in a container (`DOTNET_RUNNING_IN_CONTAINER=true`), keys are persisted to `/var/lib/hawk/dpkeys`.
- `docker-compose.yml` mounts a named volume `hawk_dpkeys` at `/var/lib/hawk/dpkeys`.
- Override path via `Hawk:DataProtection:KeysPath` if needed (the app server uses `/var/lib/hawk/keys`).
- Key files are written with mode `0600`; after the switch to the non-root image, keys created by a root container are unreadable until the volume is chowned to 1654.

## E2E In Docker

Mock email capture must be explicitly enabled with `Hawk__MockServer__EmailCaptureEnabled=true` and `ASPNETCORE_ENVIRONMENT=Testing` (Development is also allowed for local use). All `/emails` endpoints are absent in Production even if the switch is enabled. Enabled captures retain body/timestamp but no Authorization value.

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
- `docker-compose.e2e.postgres.yml` is the same topology on `postgres:17` (tmpfs data dir) with `Hawk__Database__Provider=Postgres`.
- `e2e/Dockerfile` runs headed Chromium via `Xvfb` (not headless) and waits for `HAWK_BASE_URL`.
- Screenshots are written to host `./screenshots` by `e2e/tests/helpers.ts`.
 - Playwright is forced to run serially in Docker via `E2E_DOCKER=1` in `docker-compose.e2e.yml`.
- If screenshot capture is flaky under Docker/Xvfb, `snap()` retries and then logs a warning instead of failing the whole suite.

## Upgrading To The Non-Root Image

Upgrading an existing deployment to `0.9.27`+ (non-root image): the `hawk_dpkeys` volume and `./logs` bind mount were written by root. Before (or right after) `docker compose up -d --build`, run:
`sudo docker run --rm -v hawk_hawk_dpkeys:/v alpine chown -R 1654:1654 /v && sudo chown -R 1654:1654 ./logs`
(otherwise logins fail and file logging silently stops). The same applies to the local `docker-compose.yml` stack (`./logs` is owned by your user).

## Configuration Cheatsheet

- Database:
  - `Hawk__Database__Provider` (`SqlServer` default, or `Postgres`; anything else fails startup)
  - `ConnectionStrings__DefaultConnection` (used by EF Core + Identity + Hangfire; SQL Server or Npgsql syntax to match the provider). For PostgreSQL append `GSS Encryption Mode=Disable` (otherwise Npgsql prints `Cannot load library libgssapi_krb5.so.2` at startup; the image has no Kerberos libs).
  - `SA_PASSWORD` (SQL Server compose), `POSTGRES_PASSWORD` (PostgreSQL compose)
- Logging / container:
  - `Hawk__Logging__File__Enabled` (default true; false = console only)
  - `Hawk__Logging__File__Path` (default `logs`), `Hawk__Logging__File__RetainedDays` (default 30)
  - `Hawk__DataProtection__KeysPath` (default `/var/lib/hawk/dpkeys` in containers)
- Health: `GET /live` (no DB), `GET /ready` (DB reachable, else 503)
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
- API rate limits:
  - `Hawk__Security__ApiRequestsPerMinute` (default 120, 1-6000; per API key)
  - `Hawk__Security__ApiRequestsPerMinutePerIp` (default 600, 1-60000; per client IP, any `/api/*` request)
- Heartbeat rate limits:
  - `Hawk__Security__PingRequestsPerMinute` (default 30, 1-600; per token)
  - `Hawk__Security__PingRequestsPerMinutePerIp` (default 300, 1-6000; per client IP)
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
- Scheduler `NextRunAt` updates:
  - `MonitorScheduler` uses `ExecuteUpdateAsync` (set-based) to write `NextRunAt` rather than loading full entities. It never touches `ConcurrencyStamp`, so bookkeeping cannot conflict with Edit-page saves.

## Removed Features

- Privacy page (`/Privacy`) — removed; no longer in nav or routes.
- External auth provider buttons — removed from Login and Register Identity pages (app uses local accounts only).
- Admin user Create page (`/Admin/Users/Create`) — replaced by Edit page (`/Admin/Users/Edit/{id}`).

## Local Tooling Quirks

- `dotnet test -q` sometimes surfaces MSBuild cache/directory errors in this repo; use `dotnet test Hawk.sln -v:m` if you hit that.
