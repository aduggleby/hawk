# Agent API: create, edit and delete monitors

Extends the agent API (`docs/plans/2026-10-29-0654-feat-agent-api-plan.md`, shipped in 0.11.0) so a write-scope key can create, edit and delete monitors. Version 0.12.0.

## Product Contract

### Requirements

- R1. `POST /api/v1/monitors` (write scope) creates an HTTP or heartbeat monitor owned by the key's user. It returns `201`, a `Location` header and the monitor detail. For a heartbeat it also returns `pingUrl`; this is the only API response that contains the ping token.
- R2. `PATCH /api/v1/monitors/{id}` (write scope) edits a monitor with merge-patch semantics. An absent field is unchanged. `headers` and `matchRules` replace the whole list when present. `type` cannot change. It returns the updated detail.
- R3. `DELETE /api/v1/monitors/{id}` (write scope) deletes the monitor with its runs and alert state. It returns `204`, or `404` if the monitor doesn't exist. This cannot be undone.
- R4. Create and edit validate exactly like the UI: the `MonitorForm` attributes, `MonitorForm.Validate` (environment-dependent intervals, regex patterns, NUL characters) and the header and pattern length limits from bulk import. Errors are `400 validation_failed` with field `errors`.
- R5. Optimistic concurrency. The monitor detail returns `concurrencyStamp` and an `ETag`. `PATCH` and `DELETE` accept an optional `If-Match` header; a stale value returns `412 precondition_failed`. Without `If-Match` the last write wins, like the Edit page with a fresh form.
- R6. Heartbeat invariants hold. Create sets the deadline to now + period + grace. Edit resets the deadline only when the period, the grace or `enabled` (false to true) changes. Every edit renews `ConcurrencyStamp`.
- R7. The body is parsed by the handler (16 KB cap, `application/json` only, unknown fields return 400), as in 0.11.0. Field names match the monitor JSON export: `name`, `type`, `url`, `method`, `enabled`, `timeoutSeconds`, `intervalSeconds`, `heartbeatGraceSeconds`, `alertAfterConsecutiveFailures`, `alertEmailOverride`, `allowedStatusCodes`, `runRetentionDays`, `contentType`, `body`, `headers[{name,value}]` and `matchRules[{mode,pattern}]`. `heartbeatToken`, `ownerEmail` and `isPaused` are rejected with 400. The server issues tokens, the caller is the owner, and pause and resume have their own endpoints.
- R8. Write handlers log the user id, key id, monitor id and action. Delete also logs the monitor name, at Warning level.

- R9. Each monitor has a `FollowRedirects` setting, default `true`, which keeps today's behavior. When it is `false`, the check does not follow redirects and evaluates the 3xx response itself. A 3xx counts as success only when it is listed in `allowedStatusCodes` (2xx always succeeds). Example: `followRedirects: false` with `allowedStatusCodes: "301"` asserts a permanent redirect. The response's `Location` header shows in run diagnostics, as all response headers already do.
- R10. `FollowRedirects` is available wherever monitor configuration is:
  - the Create and Edit pages (a checkbox in advanced settings, HTTP only, with Create/Edit parity) and the Details page;
  - the API create, edit and detail (`followRedirects`);
  - the JSON export and import (`followRedirects`). The format stays version 3. A missing field means `true`, so older files and older Hawk versions keep working.
- R11. StatusCake import and seeded monitors use `true`. Heartbeats ignore the setting and store `true`.

### Acceptance examples

- AE1. A `POST` with `{name, url, intervalSeconds: 60}` returns 201. Defaults match the Create form: `GET`, enabled, timeout and alert threshold from the form defaults. The list then contains the monitor with `ownedByCaller: true`.
- AE2. A heartbeat `POST` returns a `pingUrl`. Calling it records a ping. A `GET` on the detail never contains the token.
- AE3. `PATCH {"intervalSeconds": 300}` on a heartbeat moves the deadline. `PATCH {"name": "x"}` does not.
- AE4. A `PATCH` with a stale `If-Match` returns 412 and changes nothing. A `PATCH` with `{"type": "heartbeat"}` on an HTTP monitor returns 400.
- AE6. An HTTP monitor with `followRedirects: false` and `allowedStatusCodes: "301"` that checks a mock endpoint returning 301 passes. The same endpoint with `followRedirects: true` follows the redirect, and the result depends on the target.
- AE5. `DELETE` returns 204. A later `GET` returns 404, and the monitor's runs are gone.

### Scope boundaries

- Out of scope: regenerating heartbeat tokens, bulk operations and changing a monitor's owner.
- Any write key can edit or delete any monitor, matching the UI, where any signed-in user can.

## Planning Contract

- KTD1. Extract a scoped `MonitorEditor` service (`Services/Monitoring/MonitorEditor.cs`) with `CreateAsync`, `UpdateAsync(id, form, expectedStamp?)` and `DeleteAsync(id)`. The Create, Edit and Delete pages and the API all use it, so there is one implementation of the entity mapping, deadline rules and stamp handling. Page behavior does not change.
- KTD2. The API maps JSON to a `MonitorForm` and validates it with one shared helper, the same one bulk import uses. For PATCH, the form is built from the persisted monitor, then the patch is applied and the result validated.
- KTD3. `If-Match` is checked through the EF original-value pattern the Edit page already uses (`DbUpdateConcurrencyException` maps to 412). For DELETE, the check is a conditional `ExecuteDeleteAsync` filtered on the stamp.
- KTD4. The ETag is the quoted `concurrencyStamp`.

## Implementation Units

- U1. Add `MonitorEditor` and switch the pages to it. Tests: page parity (existing Create/Edit/Delete/Heartbeat tests stay green) and the service on Postgres.
- U2. Add the API endpoints, models, docs, the `GET /api/v1` endpoint list, tests (InMemory for validation and errors, Postgres for AE1–AE5) and an e2e spec extension.
- U4. Add `FollowRedirects`: the model plus a migration in both sets, the `urlchecks-noredirect` named HttpClient (`AllowAutoRedirect = false`), `UrlCheckRequest`, `MonitorExecutor`, the form, pages, JSON port, API, a mock-server `/redirect` endpoint, and tests (unit, Postgres, e2e).
- U3. Version 0.12.0, CHANGELOG, AGENTS.md "Agent API" section.

## Verification Contract

- `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test Hawk.sln -v:m` green.
- E2E green on SQL Server and PostgreSQL.
- Code review (Opus reviewers plus Codex), with fixes applied.
- Deploy per the 0.11.0 Release section: push, image from `git archive HEAD`, `pg_dump`, `release.env` pin, `deploy-app.sh hawk app edge`, live checks, record the deployment here.

## Production Deployment — 2026-10-29

- Version **0.12.0**; source commit `2363627` (pushed to GitHub `main`).
- Image `registry.dualconsult.net/dualconsult/hawk@sha256:5dffc17d83d4d67c01a60dcff4b6bda09a99cdb1ae7beffc6bb0baa6d8c5c282`, built from `git archive HEAD` (not pushed to GHCR).
- Backup: `/srv/dualconsult/backups/hawk/hawk-pre-0.12.0-20261029T073833Z.dump` on the db VM.
- Release pin: infra commit `995a130`. Deployed with `scripts/deploy-app.sh hawk app edge`.
- Verification:
  - `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test`: 330/330 passed.
  - `ando run` (DIND is now enabled in `ando.config`): exit 0, 264 passed, 66 Postgres tests skipped inside the ando container.
  - E2E: 16/16 on SQL Server and 16/16 on PostgreSQL.
- Reviews:
  - The monitor CRUD commits got five reviewers plus Codex. Fixed in `2363627`: the API caps headers and match rules at 5, and PATCH returns 409 instead of re-applying a stale snapshot. A blank `alertEmailOverride` now clears the override.
  - The redirect commit got Codex and an Opus reviewer: no defects.
  - Deferred: bulk JSON import still accepts more than 5 headers or match rules (pre-existing). Saving an HTTP monitor still postpones its next run by one interval, as the Edit page does.
- Live checks:
  - Migration `AddMonitorFollowRedirects` applied; all 32 existing monitors have `FollowRedirects = true`.
  - The container runs v0.12.0 with 0 restarts, UID 1654 and a read-only root filesystem.
  - `/live` returns 200 and `/ready` returns 404.
  - `/api/v1`, POST `/api/v1/monitors` and DELETE without a key return 401; `/api/nope` returns 404. These are problem+json, except the DELETE, whose content type was not captured.
