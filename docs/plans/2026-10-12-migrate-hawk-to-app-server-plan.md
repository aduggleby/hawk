# Plan: Move Hawk from office TrueNAS to the DualConsult app server

Created: 2026-10-12 · Status: **DONE: live on prod1 since 2026-10-12 ~20:38 CEST.**
- Phases 1–5 ✅.
- 31 monitors imported; cutover and a live reboot test passed.
- Remaining:
  - retire TrueNAS after ~2 weeks (keep a DB backup);
  - rotate the email and Headscale keys that were shared in chat;
  - a registration on/off setting in Hawk (currently blocked at Caddy for non-admin IPs).

## 1. Goal and outcome

Hawk moves from the office TrueNAS (`alex-office-nas.ohana`, apps `ix-hawk` + `ix-sqlserver`) to `https://hawk.dualconsult.net` on the new dedicated app server (Proxmox host, Docker apps VM). It replaces the `prod1.dualconsult.net` cloud VM; see `~/Source/DualConsultInfrastructure/docs/app-server-build.md`. Hawk follows that server's generic per-app layout and is the first app migrated after the build.

What you will see:

- The same monitors (config, headers, match rules, paused/enabled state) on the new URL.
- A fresh user base: you register first and become Admin.
- Alerts still go out through the same Resend-compatible email API, using the existing send token.
- The TrueNAS instance stays available, stopped, as a rollback until cutover is confirmed.

Constraints (from the app-server build plan):

- The apps VM has about 40 GB RAM, so memory is no longer a constraint. That reopens the database decision (Phase 3).
- The server pulls immutable images by digest from `registry.dualconsult.net` and never builds images on the host.
- Persistent data goes in `/srv/dualconsult/data/<app>` (plain directory). The server must recover from any reboot unattended.
- Caddy is the only container that publishes ports. Apps attach to `dualconsult-edge` (static IPs only in `.3`–`.127`).
- Systemd (`dualconsult-app@hawk.service`) owns the lifecycle. The infra source of truth is `~/Source/DualConsultInfrastructure`.
- Hawk is deployed only after the app server reaches Phase 8 of its build plan.

## 2. Phases (in order)

| # | Phase | Where it ships | Why this order |
|---|-------|----------------|----------------|
| 1 | Full monitor export/import | TrueNAS (current SQL Server build) | Gets the data out in a format that doesn't depend on the database provider before anything else changes |
| 2 | Export everything from TrueNAS | Your action | Freezes the source data |
| 3 | PostgreSQL support (MSSQL stays an option) | Code only | No database size cap, and it matches the platform's WAL-G backups; RAM is no longer the reason |
| 4 | App-server deploy | Apps VM + infra repo | New home on the shared platform |
| 5 | Cutover: import, verify, retire TrueNAS | Your action + checks | Avoids running two schedulers and sending duplicate alerts |

---

## 3. Phase 1: Full export/import (deployable to TrueNAS)

**What:** Admin-level "Export all monitors" and "Import monitors" that work in one step (no per-monitor form prefill).

**Why:** The JSON export that exists today handles one monitor at a time, and import only prefills the Create form with the first monitor. That isn't workable for a migration.

Scope of the export:

- Included: every monitor from every user, with all `Monitor` fields, headers, match rules, `IsPaused`/enabled state, interval, timeout, alert settings, `AlertEmailOverride`, `AllowedStatusCodes`, `RunRetentionDays`.
- Each monitor also records the owner's email (`ownerEmail`), so ownership can be remapped later if the same users register again.
- Excluded: users, roles, passwords, run history, `MonitorAlertState`, Hangfire state, and per-user settings (`UserAlertSettings`, `UserUrlCheckSettings`, `UserMonitorSettings`, because they belong to users).
- Envelope: add `version: 2` and `exportedAt`. Keep reading the existing v1 shapes.

Import behaviour:

- Admin only. Upload one `.json` file and see a dry-run preview (counts, validation errors per monitor) before committing.
- All monitors are created in one transaction. Each goes through `MonitorForm.Validate()`, and any invalid monitor aborts the whole import (all or nothing).
- Owner: if a registered user has the same `ownerEmail`, they own the monitor. Otherwise the importing admin does.
- **Every imported monitor is created paused.** You unpause them in bulk (the index already supports "Resume all"), which keeps the app server from sending alerts while TrueNAS is still live.
- Duplicates: skip any monitor whose name and URL match an existing one, and report the count. This makes re-running an import safe.

Code touchpoints: `Pages/Monitors/MonitorJsonPort.cs` (v2 model and mapping), a new admin page `Pages/Admin/MonitorTransfer.cshtml(.cs)` or handlers on the monitors index, and an Admin nav link.

Tests:

- Unit tests: v2 round trip (export, then import, then compare every field), parsing v1 files, all-or-nothing behaviour on an invalid monitor.
- E2E: export all, wipe, import, assert that the monitors are equal and paused.

Ship: bump the version, build the image, push it to the image the TrueNAS app uses (`ghcr.io/aduggleby/hawk`), and redeploy on TrueNAS.

## 4. Phase 2: Export from TrueNAS (your action)

1. Update the TrueNAS Hawk app to the Phase 1 image.
2. Admin, then Export all monitors. Save the file as `hawk-export-YYYYMMDD.json` somewhere outside the repo, because it contains alert emails and possibly auth headers.
3. Write down the current email settings from the TrueNAS app: `Hawk__Resend__BaseUrl`, `Hawk__Resend__ApiKey`, `Hawk__Email__From`. They will be reused on the app server.
4. Optional safety net: take a native `BACKUP DATABASE` `.bak` of the SQL Server `Hawk` database and keep it with the TrueNAS rollback.

## 5. Phase 3: PostgreSQL support (MSSQL remains)

**What:** A setting `Hawk:Database:Provider` = `SqlServer` (default, so existing installs are unchanged) or `Postgres`.

**Decided (2026-10-12): Postgres.** Background: RAM no longer forces Postgres. The alternative is SQL Server **Express** (the Developer edition isn't licensed for production), which has a **10 GB per-database cap**. A run row is about 4–8 KB (nvarchar snippet + headers JSON). A 60 s monitor with 90-day retention keeps about 130k runs, roughly 0.5–1 GB, so Express fits only about 10–20 one-minute monitors before lower retention is required. Check the real size on TrueNAS first (`EXEC sp_spaceused` in `Hawk`, and per-table).

**Recommendation: keep the Postgres port.** It has no size cap, uses the platform's proven WAL-G encrypted backups, and is free. Phases 1–2 don't depend on the database, so they proceed either way.

Design:

- **EF Core:** keep `ApplicationDbContext` and split migrations by provider. Use two migrations assemblies or folders (`Migrations/SqlServer` with the existing history, `Migrations/Postgres` with a fresh initial migration) and pick one at registration with `UseSqlServer(..., x => x.MigrationsAssembly(...))` or `UseNpgsql(...)`. Existing SQL Server databases keep their `__EFMigrationsHistory` without any change.
- **Concurrency tokens:** `Monitor.RowVersion` and `MonitorAlertState.RowVersion` use `[Timestamp]`/`IsRowVersion()` today. On Postgres, map them to `xmin` (`uint`) with provider-conditional configuration in `OnModelCreating`. `MonitorForm.RowVersion` (base64 in a hidden field) needs to handle both shapes.
- **Hangfire:** use `Hangfire.SqlServer` or `Hangfire.PostgreSql`, depending on the provider. The distributed lock `hawk:monitor-alert:{id}` keeps working because both storages support it.
- **Known behaviour differences to audit:**
  - Npgsql `timestamptz` rejects `DateTime` values where `Kind` isn't `Utc`. Audit every `DateTime` write.
  - String comparisons: SQL Server's default collation is case-insensitive and Postgres is case-sensitive. Check search, filters, and duplicate checks (Identity uses normalized columns, so it isn't affected).
  - Raw SQL: none was found (no `FromSql`/`ExecuteSql`). Recheck the diagnostic scripts in `scripts/`; they are MSSQL-only and stay that way.
  - `ExecuteUpdateAsync`/`ExecuteDeleteAsync` work on both providers.
- **Startup:** `MigrateAsync` with retries stays as it is. `MonitorSeeder`/`IdentitySeeder` work on both providers.
- **Dev/test:** `docker-compose.yml` gets a `postgres` profile, or a `docker-compose.postgres.yml`. Run the E2E suite against both providers: the `docker-compose.e2e.yml` matrix becomes `DB_PROVIDER=sqlserver|postgres`.
- **Health endpoints:** add `/live` (process up) and `/ready` (DB reachable), so Caddy can health-check. Caddy hides `/ready` from the public.
- **Docs:** update the README with a "Using PostgreSQL" section and the env vars, and update the AGENTS.md config cheatsheet.

Done when: unit tests pass, the E2E suite passes against both providers, and an existing SQL Server dev DB still starts and migrates without changes.

## 6. Phase 4: App-server deploy (via the platform template)

The app server is built (infra build plan Phases 0–8 done, 2026-10-12). Hawk is deployed as the **first app built from `templates/app`** in `~/Source/DualConsultInfrastructure`; see "Adding an app" in its README.

1. `scripts/new-app.sh hawk hawk.dualconsult.net`. This creates `apps/hawk/` (slot 1: `10.20.1.0/24`, `10.21.1.0/24`) and secrets in `~/.secrets/dualconsult/apps/hawk/`. **Put `walg.key` in KeePass.**
2. **Hawk image changes the template requires** (do these in Phase 3):
   - `Dockerfile`: run as the non-root .NET user (`USER $APP_UID`, i.e. 1654); the image currently runs as **root**;
   - `/live` and `/ready` health endpoints;
   - a config switch to **disable the Serilog file sink** (the read-only rootfs allows writes only to `/tmp`); in containers log to stdout only;
   - DataProtection keys at `Hawk:DataProtection:KeysPath=/var/lib/hawk/keys` (the template's bind mount).
3. **`app.env`:**
   - `Hawk__Database__Provider=Postgres`;
   - `ConnectionStrings__DefaultConnection=Host=postgres;Database=hawk;Username=hawk_migrator;…`. Hawk applies EF migrations and Hangfire creates its schema at startup, so it uses the **migrator** role;
   - `Hawk__DisableHttpsRedirection=true`, `Hawk__DataProtection__KeysPath=/var/lib/hawk/keys`;
   - the email settings reused from TrueNAS (Phase 2), `AllowedHosts=hawk.dualconsult.net`.
4. **Image:** build on the dev machine, push to `registry.dualconsult.net/dualconsult/hawk`, and set the digest in `apps/hawk/release.env`.
5. **DNS** `hawk.dualconsult.net A 37.27.109.163`, then `scripts/deploy-app.sh hawk`.
6. **Verify:**
   - `/live`;
   - registration of the admin user, immediately;
   - `sudo systemctl start dualconsult-app-dbbackup@hawk.service`, then `sudo dc-db-restore-drill hawk` (it must show the EF migration count and tables);
   - a reboot test.

Networking: Hawk's outbound checks and email go out through `dualconsult-edge` (NAT via the host). Checks against apps on prod1 use the hairpin NAT (tested).

## 7. Phase 5: Cutover

1. Deploy to the app server with the scheduler on. This is safe because there are no monitors yet.
2. **Register your admin account right away.** The first user to register becomes Admin, and the site is public as soon as DNS and Caddy are live. Consider keeping Caddy returning 503 or IP-restricting `hawk.caddy` until this step is done.
3. Import `hawk-export-YYYYMMDD.json`. Check the preview counts against TrueNAS, then commit. All monitors arrive paused.
4. Spot-check a few monitors with the Test page and confirm that a test alert email arrives through the reused token.
5. **Switch over:** pause all monitors on TrueNAS, then resume all monitors on the app server. Allow less than 5 minutes between the two.
6. **Reboot test:** reboot the apps VM, then the host, and confirm Hawk, Caddy, and the registry return with no manual steps and that scheduled runs resume.
7. Watch for 24–48 h: runs completing, no unexpected alerts, backups OK.
8. Stop the TrueNAS `hawk` app and keep it, together with its SQL DB, for about 2 weeks as a rollback. Then remove the `ix-hawk` app. Remove `ix-sqlserver` only if nothing else uses it.

**Rollback:** pause all monitors on the app server and resume them on TrueNAS. The TrueNAS data is unchanged, apart from the pause flags.

## 8. Risks

- **The first user to register becomes Admin on a public URL.** Mitigated by step 5.2. A follow-up could add a `Hawk:Registration:Enabled` toggle.
- **Hawk can't report its own outage:** if the app server is down, nothing monitors your sites or the server itself. Consider pointing an external check (for example, the TrueNAS Hawk kept running for this one URL) at `hawk.dualconsult.net/live`.
- **Data at rest is no longer encrypted on the app server.** Only the WAL-G backups are encrypted client-side.
- **The Postgres port touches all data access.** Mitigated by running the E2E suite on both providers.
- **Outbound checks now come from a Hetzner IP:** targets that allowlist the office IP, or internal/`.ohana` URLs, will start failing. Review the monitor list for LAN-only targets before cutover.
- **The export file contains secrets** (auth headers in monitors). Handle it like a credential and delete it after cutover.

## 9. Open questions

1. ~~Database~~ **Decided 2026-10-12:** migrate to Postgres; SQL Server stays as a second provider.
2. Should run history be migrated? It is excluded for now, so the app server starts with empty graphs and history.
3. Are any current monitors pointing at LAN/Tailscale-only targets (`*.ohana`, `192.168.*`)? These can't be reached from the app server.
4. Should Phase 3 add a registration on/off toggle, or is step 5.2 enough?
5. Is there a sender address for Hawk on selfmx (for example `hawk@internal.dualconsult.net`), or does it keep its current `From`?
6. Does `ghcr.io/aduggleby/hawk` remain the public image for TrueNAS users, alongside the private registry?
