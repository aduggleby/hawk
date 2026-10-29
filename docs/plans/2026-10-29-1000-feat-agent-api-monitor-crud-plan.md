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

### Acceptance examples

- AE1. A `POST` with `{name, url, intervalSeconds: 60}` returns 201. Defaults match the Create form: `GET`, enabled, timeout and alert threshold from the form defaults. The list then contains the monitor with `ownedByCaller: true`.
- AE2. A heartbeat `POST` returns a `pingUrl`. Calling it records a ping. A `GET` on the detail never contains the token.
- AE3. `PATCH {"intervalSeconds": 300}` on a heartbeat moves the deadline. `PATCH {"name": "x"}` does not.
- AE4. A `PATCH` with a stale `If-Match` returns 412 and changes nothing. A `PATCH` with `{"type": "heartbeat"}` on an HTTP monitor returns 400.
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
- U3. Version 0.12.0, CHANGELOG, AGENTS.md "Agent API" section.

## Verification Contract

- `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test Hawk.sln -v:m` green.
- E2E green on SQL Server and PostgreSQL.
- Code review (Opus reviewers plus Codex), with fixes applied.
- Deploy per the 0.11.0 Release section: push, image from `git archive HEAD`, `pg_dump`, `release.env` pin, `deploy-app.sh hawk app edge`, live checks, record the deployment here.
