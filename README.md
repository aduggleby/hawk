# Hawk

ASP.NET Razor Pages uptime checker and URL verifier with Hangfire scheduling, SQL Server or PostgreSQL storage, and Docker-first deployment. Styled with Tailwind CSS v4 and supports dark mode.

## Screenshots

- [All screenshots](screenshots/)
- [Monitor details screenshot](screenshots/13-monitor-details-with-runs.png)

![Monitor details](screenshots/13-monitor-details-with-runs.png)

## Local Dev Scripts

From the repo root:

```bash
./run-dev.sh
```

This script:
- Restores .NET packages
- Installs/builds Tailwind assets
- Starts `db` + `mock` via Docker Compose
- Runs `Hawk.Web` with `dotnet watch` in Development mode on `http://localhost:17800`

Optional tmux mode:

```bash
./run-dev.sh --tmux
tmux attach -t hawk-dev
```

Use PostgreSQL 17 instead of SQL Server (starts `docker-compose.postgres.yml`, Postgres on `localhost:17834`):

```bash
./run-dev.sh --postgres
```

Stop all dev processes started by the script:

```bash
./stop-dev.sh
```

## Using An External SQL Server

Hawk uses a single SQL Server connection string for EF Core (Identity + app data) and Hangfire storage.

Set the connection string via the standard ASP.NET Core environment variable:

- `ConnectionStrings__DefaultConnection`

### Create Database + Login/User (T-SQL)

Run this once on your SQL Server as a sysadmin (for example `sa`). This creates:

- Database: `Hawk`
- Login/User: `hawk` (SQL authentication)

Generate a strong password:

```bash
openssl rand -base64 32
```

If you want to run this via `sqlcmd` from a machine with `sqlcmd` installed:

```bash
# -C tells sqlcmd to trust the server certificate (useful for dev/self-signed TLS).
sqlcmd -S YOUR_SQL_HOST,1433 -U sa -P 'YOUR_SA_PASSWORD' -C
```

Then paste the SQL below and type `exit` to quit.

```sql
-- Create DB (idempotent-ish)
IF DB_ID(N'Hawk') IS NULL
BEGIN
  CREATE DATABASE [Hawk];
END
GO

-- Create login (server-level)
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'hawk')
BEGIN
  CREATE LOGIN [hawk]
    WITH PASSWORD = N'CHANGE_ME_strong_password',
         CHECK_POLICY = ON,
         CHECK_EXPIRATION = OFF;
END
GO

-- Create user + grant permissions (database-level)
USE [Hawk];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'hawk')
BEGIN
  CREATE USER [hawk] FOR LOGIN [hawk];
END
GO

-- Hawk applies migrations on startup and needs to create/alter tables.
-- db_owner is the simplest way to ensure EF Core + Hangfire can manage schema.
EXEC sp_addrolemember N'db_owner', N'hawk';
GO
```

Notes:
- `GO` is a batch separator for tools like SSMS/Azure Data Studio/sqlcmd.
- For production hardening, you can replace `db_owner` with a tighter permission set, but you must ensure schema migrations + Hangfire can still run.

### Configure Hawk To Use The External Server

Example connection string (adjust host/port and encryption settings to match your server):

```text
Server=YOUR_SQL_HOST,1433;Database=Hawk;User Id=hawk;Password=...;Encrypt=true;TrustServerCertificate=false;
```

Local dev (bash):

```bash
export ConnectionStrings__DefaultConnection="Server=192.168.1.50,1433;Database=Hawk;User Id=hawk;Password=CHANGE_ME;TrustServerCertificate=true;Encrypt=false"
dotnet run --project Hawk.Web
```

Docker (run the web container and point it at your SQL Server):

```bash
docker run --rm -p 17800:8080 \
  -e ASPNETCORE_URLS="http://+:8080" \
  -e ConnectionStrings__DefaultConnection="Server=192.168.1.50,1433;Database=Hawk;User Id=hawk;Password=CHANGE_ME;TrustServerCertificate=true;Encrypt=false" \
  ghcr.io/aduggleby/hawk:latest
```

## Using PostgreSQL

SQL Server is the default provider, so existing installs are unchanged. To use PostgreSQL (tested with 17), set the provider and a Npgsql connection string:

| Setting | Value |
| --- | --- |
| `Hawk__Database__Provider` | `Postgres` (default: `SqlServer`; any other value stops startup with an error) |
| `ConnectionStrings__DefaultConnection` | `Host=YOUR_PG_HOST;Port=5432;Database=hawk;Username=hawk;Password=...;GSS Encryption Mode=Disable` |

The same connection string is used for EF Core (Identity + app data) and Hangfire. On startup Hawk applies its EF migrations (PostgreSQL has its own migration set) and Hangfire creates/upgrades its tables in the schema `hangfire`. The database user therefore needs permission to create tables (for example, own the database):

```sql
CREATE ROLE hawk LOGIN PASSWORD 'CHANGE_ME';
CREATE DATABASE hawk OWNER hawk;
```

Docker:

```bash
docker run --rm -p 17800:8080 \
  -e Hawk__Database__Provider=Postgres \
  -e ConnectionStrings__DefaultConnection="Host=192.168.1.50;Port=5432;Database=hawk;Username=hawk;Password=CHANGE_ME;GSS Encryption Mode=Disable" \
  -e Hawk__DisableHttpsRedirection=true \
  ghcr.io/aduggleby/hawk:latest
```

`GSS Encryption Mode=Disable` stops Npgsql from probing for Kerberos/GSSAPI on every connection; without it the container prints `Cannot load library libgssapi_krb5.so.2` to stderr at startup (harmless, but noisy). Leave it out only if you use Kerberos.

Local stack on PostgreSQL: `docker compose -f docker-compose.postgres.yml up -d --build` (same ports as `docker-compose.yml`; run one stack at a time).

Notes:
- Switching provider does not move data. To move monitors between installs (for example SQL Server to PostgreSQL), use **Admin -> Transfer** (export all / import), see [Export / Import All Monitors](#export--import-all-monitors-admin).
- All timestamps are stored in UTC (`timestamptz`).

## Health Endpoints

Both endpoints are anonymous and lightweight, intended for container/reverse-proxy health checks:

| Endpoint | Meaning |
| --- | --- |
| `GET /live` | The process is up. Does not touch the database. Always `200 Healthy`. |
| `GET /ready` | The database is reachable. `200 Healthy`, or `503 Unhealthy` when it is not. |

If HTTPS redirection is enabled (the default), plain-HTTP probes get a redirect; set `Hawk__DisableHttpsRedirection=true` when TLS is terminated by a proxy. Consider blocking `/ready` at your public reverse proxy.

## Container Image: Non-Root User And Read-Only Root Filesystem

Since `0.9.27` the image runs as the non-root .NET user (`app`, UID/GID `1654`) instead of root.

- Installs **without volumes** (such as the TrueNAS YAML below) keep working: the image owns `/app/logs` and `/var/lib/hawk` for that user.
- Installs **with volumes or host paths** created by an older image are owned by root. DataProtection key files are written with mode `0600`, so the new user cannot read them and logins/forms fail (Hawk logs an error at startup). Fix the ownership once, then restart:

  ```bash
  # Host path (TrueNAS host path, bind mount):
  sudo chown -R 1654:1654 /path/to/hawk/dpkeys /path/to/hawk/logs
  # Named Docker volume:
  docker run --rm -v hawk_hawk_dpkeys:/v alpine chown -R 1654:1654 /v
  ```

  Alternatively keep the old behaviour by running the container as root (`user: "0:0"` in compose).
- File logging: `Hawk__Logging__File__Enabled` (default `true`) writes daily rolling logs (`hawk-YYYYMMDD.log`) to `Hawk__Logging__File__Path` (default `/app/logs`) and keeps `Hawk__Logging__File__RetainedDays` files (default `30`). Set it to `false` for console-only logging, or point the path at a writable volume when using a read-only root filesystem (`--read-only --tmpfs /tmp`). The only other write location is the DataProtection keys path (`Hawk__DataProtection__KeysPath`, default `/var/lib/hawk/dpkeys`), which must be a writable volume.

## Install On TrueNAS SCALE (Install via YAML)

These instructions target TrueNAS SCALE's **Apps** feature using **Install via YAML** (Docker Compose format). Hawk connects to an existing SQL Server instance (external to the Hawk container).

### Prereqs

- TrueNAS SCALE 24.10 or later.
- SQL Server already running as a TrueNAS app (or otherwise reachable over TCP).

### 1) Create Database And User

Create the `Hawk` database and the `hawk` login/user on your SQL Server.

Use the SQL script in the **Using An External SQL Server** section above.

In Production (and other non-Development environments), Hawk does not seed an admin user. The **first user to register** will be promoted to the `Admin` role automatically.

### 2) Open The YAML Installation Wizard

1. Apps -> Discover Apps.
2. Click the three-dot menu (top right).
3. Select **Install via YAML**.

### 3) Configure The Application

Application name: `hawk`

Paste the following YAML and replace the placeholder values:

```yaml
services:
  hawk:
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8080
      - Hawk__DisableHttpsRedirection=true

      # External SQL Server (use your TrueNAS host IP / SQL port)
      - >-
        ConnectionStrings__DefaultConnection=Server=YOUR_TRUENAS_IP,1433;Database=Hawk;User Id=hawk;
        Password=YOUR_HAWK_PASSWORD;TrustServerCertificate=true;Encrypt=true

      # Email (Resend-compatible)
      - Hawk__Email__From=Hawk <hawk@yourdomain>
      - Hawk__Resend__ApiKey=YOUR_RESEND_APIKEY
      - Hawk__Resend__BaseUrl=https://api.resend.com
      # For official Resend, BaseUrl can be omitted or set to: https://api.resend.com

      # URL check defaults (optional)
      - Hawk__UrlChecks__UserAgent=firefox

    # Pin a version tag: with `:latest` and `pull_policy: always`, every app restart
    # silently upgrades Hawk (including one-way database migrations).
    image: ghcr.io/aduggleby/hawk:0.9.27
    ports:
      - '17800:8080'
    pull_policy: always
    restart: unless-stopped
```

Values to replace:

| Placeholder | Replace with |
| --- | --- |
| `YOUR_TRUENAS_IP` | Your TrueNAS host IP (example: `192.168.1.100`) |
| `1433` | SQL Server mapped port (change if different) |
| `YOUR_HAWK_PASSWORD` | Password you set for the `hawk` login |
| `YOUR_RESEND_APIKEY` | Resend (or compatible gateway) API key |
| `Hawk <hawk@yourdomain>` | Your verified sender address |

Pin the image to a version tag (as above) and change the tag deliberately to upgrade. With `:latest` and `pull_policy: always`, TrueNAS pulls and runs a new version on every app restart.

Upgrading from `0.9.26` (or earlier) to `0.9.27`:

- **The database migration is one-way.** `0.9.27` replaces the `RowVersion` columns with `ConcurrencyStamp` (`ReplaceRowVersionWithConcurrencyStamp`); `0.9.26` returns HTTP 500 against a migrated database. **Take a native SQL Server backup first**, for example `BACKUP DATABASE [Hawk] TO DISK = N'/var/opt/mssql/backup/Hawk-pre-0.9.27.bak' WITH COPY_ONLY, INIT;`.
- **Rollback means restoring that backup** (`RESTORE DATABASE ... WITH REPLACE`) and pinning the image back to `:0.9.26`.
- If you run `:latest` with `pull_policy: always`, pin `:0.9.26` until you have taken the backup and are ready to upgrade.
- The image now runs as a non-root user. The YAML above has no volumes and needs no change. If you added volumes or host paths, see [Container Image: Non-Root User](#container-image-non-root-user-and-read-only-root-filesystem).

### 4) Expose The App (Reverse Proxy)

For HTTPS, use the built-in TrueNAS ingress features (or Traefik) and proxy to `http://YOUR_TRUENAS_IP:17800`.

Hawk automatically processes `X-Forwarded-For`, `X-Forwarded-Host`, and `X-Forwarded-Proto` headers from any upstream proxy. When `Hawk__DisableHttpsRedirection=true`, the auth cookie secure policy is set to `SameAsRequest` so cookies work over plain HTTP inside the container network while HTTPS is terminated by the proxy.

## Building And Publishing An Image (For TrueNAS)

TrueNAS pulls images from registries; it typically cannot build from your local workstation.

From this repo, you can build and push `Hawk.Web`:

```bash
docker build -t ghcr.io/aduggleby/hawk:latest -f Hawk.Web/Dockerfile .
docker push ghcr.io/aduggleby/hawk:latest
```

Then set `image: ghcr.io/aduggleby/hawk:<version>` (a pinned tag such as `0.9.27`) in the YAML (or your own org if you publish a fork). Tag each pushed image with its version as well as `latest`.

## Admin User Management

Admin users can manage other users from the Admin panel:

- **Admin** -> **Users**: list all users.
- **Edit**: edit a user's name, email, and role assignments.
- **Reset Password**: reset a user's password.
- **Delete**: remove a user.

## Account Security

Login cookies validate the account's security stamp on the first authenticated request after a one-minute interval. Password changes/resets and account deletion invalidate old sessions; periodic validation refreshes role assignments.

Failed password attempts count toward a temporary lockout. Login and anonymous account-email POSTs are rate-limited by client IP. Password-reset and confirmation-resend actions share a per-account cooldown; provider failures and suppressed sends return generic results, with diagnostics in server logs. Scheduled monitor alerts do not use these account-email limits.

| Setting | Default |
|---------|---------|
| `Hawk__Security__LoginRequestsPerMinute` | 10 |
| `Hawk__Security__EmailRequestsPer15Minutes` | 5 |
| `Hawk__Security__FailedLoginAttempts` | 5 |
| `Hawk__Security__LockoutMinutes` | 5 |
| `Hawk__Security__EmailCooldownMinutes` | 1 |

Excess client requests receive HTTP 429 with `Retry-After`. Limits and cooldowns are held in memory for the current app instance. Production error pages show only a generic message and request ID; full exceptions stay in server logs.

Manual monitor execution uses antiforgery-protected **Run now**/**Run again** POST forms and redirects to saved diagnostics. Opening or refreshing a diagnostics page does not run another check. Older `/monitors/test/<id>` links redirect to monitor details without executing.

The mock server's `/emails` capture/list/reset endpoints are disabled by default. Enable them only for Development or Testing with `Hawk__MockServer__EmailCaptureEnabled=true`; Production keeps them absent regardless of that setting. Captured data omits bearer headers. The local Compose stacks bind mock port 17801 to loopback; E2E mocks are internal to their Docker network.

## Allowed Status Codes

By default, Hawk treats any 2xx response as success and everything else as failure. You can override this per monitor by setting **Allowed Status Codes** — a comma-separated list of additional HTTP status codes that should count as success (e.g. `404,429`).

- 2xx codes are always treated as success regardless of this setting.
- Codes must be in the range 100–599.
- Useful for monitoring endpoints that intentionally return non-2xx responses (health checks behind auth returning 401, soft 404 pages, rate-limited APIs, etc.).

## Alert Policy

Each monitor has an `AlertAfterConsecutiveFailures` setting (1–20, default 1).

- `1`: alert on first failure after a success (default).
- `N > 1`: alert only after `N` consecutive failures, reducing noise from transient blips.

### Alert Types

- **Failure alert** — sent when consecutive failures reach the configured threshold.
- **Failure reminder** — re-sent periodically while the monitor remains failing (default every 24 hours). Configure via `Hawk__Alerting__RepeatFailureAlertEveryHours` (min 1h, max 720h).
- **Recovery alert** — sent when a monitor returns to success after an alerted failure incident. If the recovery email fails to deliver, Hawk retries on subsequent successful runs.

### Alert Recipient Resolution

When a monitor triggers an alert, the email recipient is resolved in this order:

1. **Per-monitor override** — the `AlertEmailOverride` field on the monitor.
2. **Account-wide override** — set in **Settings** → **Alerting** by the monitor owner.
3. **Owner's login email** — the Identity email of the user who created the monitor.
4. **Admin fallback** — all users with the `Admin` role.

## Heartbeat Monitors

A heartbeat monitor is a dead man's switch for jobs Hawk cannot reach (cron jobs, backups behind a firewall). Choose **Heartbeat** when creating a monitor, set **Expected every** (1 min to 7 days) and **Grace**, and call the ping URL shown on the monitor page when the job succeeds:

```bash
curl -fsS -m 10 --retry 3 https://hawk.example.com/ping/<token>
```

- `GET`, `HEAD` and `POST` are accepted and return `200 OK`; an unknown token returns `404`. The URL is a secret: **Regenerate URL** on the Edit page invalidates the old one.
- Every ping is an OK run. If no ping arrives within period + grace, a failed run is recorded (and again every period while it stays silent), using the normal alert threshold, reminders and recovery email.
- New, imported and resumed heartbeats get a full period plus grace before they can fail. Pings to a paused or disabled heartbeat return `200` and are ignored.
- Pings are rate limited per token (`Hawk__Security__PingRequestsPerMinute`, default 30) and per client IP (`Hawk__Security__PingRequestsPerMinutePerIp`, default 300). Ping tokens are redacted from Hawk's request log; configure your reverse proxy to skip access logging for `/ping/*` (Caddy: `log_skip /ping/*`).

## Monitor Pause

Monitors can be temporarily paused without disabling them. A paused monitor:

- Skips scheduled checks (the scheduler ignores it).
- Can still be tested manually via the **Test** button on the details page.
- Shows a **Paused** badge in the monitors list (vs. **Enabled** or **Disabled**).

Pause, resume, and run from the monitors list:

- **Pause all / Resume all** — applies to all monitors.
- **Pause selected / Resume selected** — batch action on checked monitors.
- **Run now (selected)** — enqueue immediate Hangfire jobs for all checked monitors.

## Monitors Index

The monitors index page groups monitors into two sections:

- **Failing** — monitors whose most recent completed run failed. These appear at the top so you can immediately see what needs attention.
- **All monitors** — the full list below the failing group.

Batch actions apply to checked monitors. When monitors are selected, the action bar switches to: **Run now**, **Pause**, **Resume**, and **Uncheck all**. Without a selection, the bar shows **New monitor**, **Import StatusCake**, **Pause all**, and **Resume all**.

Timestamps (last run, next run, run history) are displayed in the **browser's local time zone**. Hovering over a timestamp shows a tooltip with both the local time and the UTC time.

## User Settings

Each user can configure account-wide overrides from the **Account** dropdown (Profile, Security, Alerts & Crawler):

- **Alert email override** — redirect all alert emails for your monitors to a different address.
- **Crawler User-Agent** — set a default `User-Agent` header for all monitors you own (unless the monitor explicitly sets one via headers). You can use a preset (`firefox`, `chrome`, `edge`, `safari`, `curl`) or paste a full UA string.
- **Run retention** — account-wide default run history retention (in days) for your monitors. Per-monitor override takes precedence. If empty, Hawk uses the server default (90 days).

## Run Retention

Run history is automatically pruned after each check. Retention is resolved in this order:

1. **Per-monitor override** — the `RunRetentionDays` field on the monitor (1–3650).
2. **Account-wide default** — set in **Settings** → **Alerts & Crawler** by the monitor owner.
3. **Server default** — `Hawk__Monitoring__RunRetentionDaysDefault` (default 90 days).

## Test A Monitor

From a monitor's detail page, click **Test** to run it immediately and see full diagnostics:

- Request details (method, URL, timeout, headers, body).
- Response headers, status code, and timing.
- Match rule results (pass/fail per rule).
- Response body snippet (up to 256KB).

Alternatively, click **Run now** on the detail page to enqueue an immediate Hangfire job. You'll be redirected to the run diagnostics page, which auto-refreshes until the run completes (showing Queued → Running → Completed status).

## Run Diagnostics

Click any run in the monitor's run history table to view full diagnostics:

- **Run metadata** — result (OK/FAIL), reason, timestamps, duration, HTTP status, alert status, error message.
- **Request** — URL, method, content-type, timeout, headers (JSON), body snippet.
- **Response** — content-type, content-length, headers (JSON), response body snippet, match results (JSON).

## Import From StatusCake

Hawk can import:

- StatusCake uptime tests (creates Hawk monitors)
- StatusCake uptime alerts (imports run history)

Import UI:

- In Hawk, go to `Monitors` -> `Import StatusCake`.

### Export Tests From StatusCake (JSON)

Export the raw JSON from the StatusCake API and save it to a file:

```bash
export STATUSCAKE_API_TOKEN="..."
curl -sS "https://api.statuscake.com/v1/uptime?limit=100" \
  -H "Authorization: Bearer $STATUSCAKE_API_TOKEN" \
  > statuscake-uptime.json
```

Upload `statuscake-uptime.json` with import type `Tests`.

Notes:

- Only HTTP/HTTPS tests are imported.
- The StatusCake id is appended to the monitor name as `(sc:<id>)` so alerts can be mapped later.
- If a test uses `do_not_find` (inverted body match), Hawk imports it as disabled and emits a warning (Hawk v1 does not support inverted matches).

### Export Alerts From StatusCake (JSON)

The StatusCake alerts API is per-test. Export alerts for one or more tests:

```bash
export STATUSCAKE_API_TOKEN="..."
TEST_ID="123"
curl -sS "https://api.statuscake.com/v1/uptime/${TEST_ID}/alerts?limit=100" \
  -H "Authorization: Bearer $STATUSCAKE_API_TOKEN" \
  > "alerts-${TEST_ID}.json"
```

Hawk expects alerts in this combined format:

```json
[
  { "test_id": "123", "data": [ /* alerts */ ] },
  { "test_id": "124", "data": [ /* alerts */ ] }
]
```

You can build that file like this (requires `jq`):

```bash
jq -n --slurpfile a alerts-123.json '{test_id:"123", data: $a[0].data}' > alerts-123.wrapped.json
jq -n --slurpfile a alerts-124.json '{test_id:"124", data: $a[0].data}' > alerts-124.wrapped.json
jq -s '.' alerts-*.wrapped.json > statuscake-alerts.json
```

Upload `statuscake-alerts.json` with import type `Alerts`.

## JSON Export / Import

Hawk supports exporting and importing monitor configurations as JSON. This is useful for backing up monitors, migrating between instances, or sharing configurations.

### Export

- **Single monitor** — on the monitor detail page, click **Export JSON**. Downloads a `.json` file containing the monitor configuration (name, URL, method, headers, match rules, alert settings, etc.).
- The export format is a JSON envelope with a `version` field and a `monitors` array.

### Import

- On the **New monitor** (create) page, click **Import JSON** and select a previously exported `.json` file.
- The first monitor in the file prefills the create form. Review or edit the values, then click **Create**.
- Accepted formats:
  - An envelope object with a `monitors` array (the export format).
  - A bare JSON array of monitor objects.
  - A single monitor object.
- The imported monitor is validated using the same rules as the create form. Invalid fields are reported via flash messages.
- The created monitor is assigned to the current user.

### Export / Import All Monitors (Admin)

Admins can move every monitor to another Hawk instance under **Account -> Export / Import** (`/Admin/Transfer`).

- **Export all monitors** downloads `hawk-monitors-<timestamp>.json` with every monitor of every user (headers, match rules, alert settings and the owner's email). Run history, alert state and user settings are not included. The file can contain secrets such as auth headers, so store it safely.
- **Import**: upload the file to see a dry-run preview (monitors in file, valid, invalid with errors, duplicates, owner mapping, paused/disabled at source). Nothing is written until you click **Confirm import**.
- Monitors whose name and URL match an existing monitor (case-insensitive) are skipped, so re-running an import is safe.
- Owners are matched by email; unmatched monitors belong to the importing admin.
- **Create all imported monitors paused** is on by default, recommended for migrations so the new instance does not run checks or send alerts until you resume the monitors.
- Import is all or nothing: if any non-duplicate monitor is invalid, nothing is imported. Files up to 2 MB are accepted; v1 files are still readable. Exports from a Testing instance that use the 5-second interval are refused by Production (that interval is not allowed there).

### JSON Schema (per monitor)

```json
{
  "name": "My Monitor",
  "url": "https://example.com",
  "method": "GET",
  "enabled": true,
  "isPaused": false,
  "timeoutSeconds": 15,
  "intervalSeconds": 60,
  "alertAfterConsecutiveFailures": 1,
  "alertEmailOverride": null,
  "allowedStatusCodes": "404,429",
  "runRetentionDays": null,
  "contentType": null,
  "body": null,
  "headers": [
    { "name": "Authorization", "value": "Bearer token" }
  ],
  "matchRules": [
    { "mode": "contains", "pattern": "OK" }
  ]
}
```

## Ando Build And Release

This repo includes an Ando build script: `build.csando`.

### Build And Test

```bash
ando run
```

### Publish (Artifacts + Container Image)

Publishes the app to `./artifacts/publish/Hawk.Web` and builds a multi-arch (amd64 + arm64) container image pushed to GHCR.
The image contains only the ASP.NET app. The database (SQL Server or PostgreSQL) is external and configured via `Hawk__Database__Provider` and `ConnectionStrings__DefaultConnection`.

Override the destination with `GHCR_IMAGE=ghcr.io/<owner>/<name>`.
Ensure auth is available (recommended: `GITHUB_TOKEN` in CI, or `gh auth login` locally).

```bash
ando run --dind -p publish
```

### Versioning And CHANGELOG

- Project version is set to `0.12.0` in the web and test `.csproj` files.
- The intent is to use `ando release` which automatically bumps versions from there.
- Changelog is tracked in `CHANGELOG.md`.
