# Hawk

ASP.NET Razor Pages uptime checker and URL verifier with Hangfire scheduling, SQL Server storage, and Docker-first deployment. Styled with Tailwind CSS v4 and supports dark mode.

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

    image: ghcr.io/aduggleby/hawk:latest
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

### 4) Expose The App (Reverse Proxy)

For HTTPS, use the built-in TrueNAS ingress features (or Traefik) and proxy to `http://YOUR_TRUENAS_IP:17800`.

## Building And Publishing An Image (For TrueNAS)

TrueNAS pulls images from registries; it typically cannot build from your local workstation.

From this repo, you can build and push `Hawk.Web`:

```bash
docker build -t ghcr.io/aduggleby/hawk:latest -f Hawk.Web/Dockerfile .
docker push ghcr.io/aduggleby/hawk:latest
```

Then set `image: ghcr.io/aduggleby/hawk:latest` in the YAML (or your own org if you publish a fork).

## Admin User Management

Admin users can manage other users from the Admin panel:

- **Admin** -> **Users**: list all users.
- **Create**: add a new user with email, password, and optional Admin role.
- **Reset Password**: reset a user's password.
- **Delete**: remove a user.

## Alert Policy

Each monitor has an `AlertAfterConsecutiveFailures` setting (1–20, default 1).

- `1`: alert on first failure after a success (default).
- `N > 1`: alert only after `N` consecutive failures, reducing noise from transient blips.

## Import From StatusCake

Hawk can import:

- StatusCake uptime tests (creates Hawk monitors)
- StatusCake uptime alerts (imports run history)

Import UI:

- In Hawk, go to `Admin` -> `Import StatusCake`.

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

## Ando Build And Release

This repo includes an Ando build script: `build.csando`.

### Build And Test

```bash
ando run
```

### Publish (Artifacts + Container Image)

Publishes the app to `./artifacts/publish/Hawk.Web` and builds a multi-arch (amd64 + arm64) container image pushed to GHCR.
The image contains only the ASP.NET app. SQL Server is external and configured via `ConnectionStrings__DefaultConnection`.

Override the destination with `GHCR_IMAGE=ghcr.io/<owner>/<name>`.
Ensure auth is available (recommended: `GITHUB_TOKEN` in CI, or `gh auth login` locally).

```bash
ando run --dind -p publish
```

### Versioning And CHANGELOG

- Project version is set to `0.9.7` in the `.csproj` files.
- The intent is to use `ando release` which automatically bumps versions from there.
- Changelog is tracked in `CHANGELOG.md`.
