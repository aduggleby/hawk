---
title: Monitor groups and Tower redesign - Plan
type: feat
date: 2026-10-29
artifact_contract: ce-unified-plan/v1
product_contract_source: ce-plan
execution: code
---

# Monitor groups and Tower redesign - Plan

## Goal Capsule

- **Objective:** Monitors can be organized into groups everywhere (UI, agent API, JSON export/import). Hawk is then restyled as the "Tower" dense ops board.
- **Means:** Release A (0.13.0): a free-text `Monitor.GroupName` column, group-aware Index/Create/Edit/Details pages, and API fields and group endpoints built on the existing `MonitorEditor`/`MonitorCommands`. Release B (0.14.0): a 15-minute run-bucket rollup table filled by a single-writer Hangfire job, Tower tokens and self-hosted Archivo, and new layouts for the monitors overview, monitor details (HTTP and heartbeat) and run diagnostics.
- **Authority:** This plan's Product Contract (confirmed in chat on 2026-10-29) > `AGENTS.md` rules (provider-agnostic EF, both migration sets, Create/Edit parity, heartbeat invariants) > the mockups (`hawk-mockups/refresh/option-1`, `hawk-mockups/groups`, outside the repo) > implementer judgment.
- **Stop conditions:** Stop and ask if a requirement would change check execution or alerting behavior, or if a heartbeat invariant cannot hold.

---

## Product Contract

### Problem Frame

Hawk lists about 30 monitors in one flat table. Operators cannot see related monitors together (one product, one customer) or act on them as a set. The UI is a generic SaaS table that does not show at a glance what is down, since when, or how a monitor behaved over the last day. The user chose the "Tower" direction from three design mockups: a dense, dark-first board with a red Attention strip, a group rail, and 24-hour result strips.

### Requirements

Groups (release A, 0.13.0):

- R1. A monitor has an optional group: free text, trimmed, max 100 characters, no NUL, blank means no group. A group exists while at least one monitor uses it. There is no separate group entity.
- R2. Group names compare case-insensitively. On save, a name that matches an existing group case-insensitively takes that group's existing spelling, so groups do not split on casing.
- R3. Create and Edit have a Group field with suggestions from existing groups (Create/Edit parity). Details shows the group as a breadcrumb (`Monitors / <group> / <name>`) and a chip.
- R4. The monitors index shows failing monitors in one global section at the top, as today. Below it, every monitor is listed by group, sorted by group name, with ungrouped monitors last under "Ungrouped". A failing monitor appears both at the top and in its group section, where it is marked failing. Each group header shows its monitor count and how many are failing; counts and group actions always cover all members.
- R5. Each group header has a select-all checkbox and Pause, Resume and Run actions for that group. The Ungrouped section has select-all only (no group actions). Selection and the existing batch bar work across groups. Run queues HTTP monitors that are enabled, reuses a manual run already in flight, and reports heartbeats and disabled monitors as skipped.
- R6. Rename a group from its header: every monitor in it moves to the new name. When the new name matches another existing group case-insensitively, the members merge into it and take its spelling (R2); the page asks for confirmation first and names the target group. A rename that only changes the casing of the same group stores the new spelling. Rename validates the new name like R1 (blank is an error) and is a user change that renews each monitor's `ConcurrencyStamp`.
- R7. Agent API:
  - Monitor list items and detail carry `group` (string or null).
  - Create and PATCH accept `group`: absent leaves it unchanged, null or blank clears it, same validation as R1.
  - `GET /api/v1/monitors?group=<name>` filters case-insensitively. An unknown group returns an empty list. `?group=` (empty) returns ungrouped monitors.
  - `GET /api/v1/groups` lists groups with `name`, `monitorCount`, `failingCount` (last completed run failed, the same rule as the Index), `pausedCount`.
  - `POST /api/v1/groups/pause`, `/resume`, `/run` and `/rename` (write scope) act on a group named in the JSON body (`{"group": "..."}`; rename takes `{"group", "newName"}`). `group` is a required non-empty string; an unknown group returns 404; an invalid `newName` returns 400 `validation_failed` keyed `newName`. The responses report counts (pause/resume: `changed`; run: `queued`, `alreadyInFlight`, `skipped`; rename: `renamed`).
  - `GET /api/v1` lists the new endpoints. `docs/agent-api.md` documents them.
- R8. The JSON export writes `group` per monitor (still format version 3; a missing field means no group). Single import (Create prefill) and bulk transfer import it, and the bulk preview shows each monitor's group. Duplicate detection does not change. StatusCake import and seeded monitors have no group (seeded monitors may use a `[Seed]` group).

Tower (release B, 0.14.0):

- R9. The whole app uses the Tower look:
  - Tokens for background, panel, line, text, muted, up, down, paused, chart, down tint.
  - A dark palette and a light ("day shift") palette. Theme follows the OS by default; the existing theme toggle (system/light/dark, `hawk.theme` in localStorage) keeps working.
  - Archivo variable font (width axis) self-hosted from `wwwroot`, replacing the current unpkg fonts; monospace uses a system stack. No external font requests.
  - The Tower status colors (up green, down red, paused grey) replace the current badge colors.
- R10. Monitors overview:
  - A headline sentence computed from current state (e.g. "2 of 31 monitors are down. Longest outage: API health, 3 h 12 min." or "All 31 monitors are up.").
  - A red Attention strip with one card per failing monitor (name, kind, reason, since when, last status). It replaces the R4 top section; the cards have no checkboxes, and the monitors stay selectable in their group rows.
  - A group rail (desktop: left column; mobile: horizontal chips) with counts and down counts. Selecting a group scrolls to it.
  - 35 px rows with checkbox, status, name, kind, a 24h strip, last response time and last run. Below 640 px a row has two lines: checkbox, status and name, then the strip at full width; kind, response time and last run are hidden. Checkboxes and group action buttons have at least 44 px tap targets on touch layouts.
  - The R4/R5 group sections and batch actions keep working in the new layout.
- R11. The 24h strip has 96 cells of 15 minutes. A cell is grey when there were no runs, green when all runs passed, and red when any failed; red cells are always full height, so failure does not rely on color alone. Other cells' height scales with the bucket's average response time relative to the highest bucket average of that monitor in the 24 h, with a minimum of 25% (heartbeats: full height). A tooltip shows the bucket time, run count, failures and average response time. Each strip has a text alternative for assistive technology ("Last 24 hours: 3 of 96 intervals failed").
- R12. Monitor details (HTTP):
  - Leads with a large status word and its start time:
    - DOWN since `<incident start>`.
    - UP since `<time>`: the first successful run after the newest bucket with failures, or that bucket's end when those runs are pruned. Without any failing bucket it reads "UP since `<created>`" when the monitor is younger than the rollup history, otherwise "UP for more than 90 days".
    - PAUSED, DISABLED.
    - PENDING with "first check at `<next run>`" before the first completed run (heartbeats: WAITING with "first ping due by `<deadline>`").
  - A 24h response-time chart.
  - A 90-day uptime row plus 24h/7d/30d uptime figures. The row has one cell per UTC day, oldest first. Cell colors: 100% up; 99% to under 100% up at reduced opacity; 95% to under 99% down tint; under 95% down; grey without runs.
  - Empty states: the chart and the row show "No data yet" when there are no buckets, and missing uptime figures show an em dash.
  - Recent runs, configuration, alert recipients and actions as today.
- R13. Heartbeat details: the same lead, the ping URL with copy button, the curl example, last ping, deadline, and a 24h ping timeline (cells from the same buckets).
- R14. Run diagnostics uses the Tower layout with the same content as today.
- R15. 90-day and 24h data survive run pruning. Days before the rollup existed, gaps after the rollup job was down, and historical runs added by the StatusCake import are filled from runs that are still retained.
- R16. The API monitor detail gains `uptime` (`last24h`, `last7d`, `last30d` as percentages or null when there are no runs) from the same rollup.

### Acceptance Examples

- AE1. Monitors A ("Shop") and B ("shop ") exist; editing B saves it with group "Shop". The index shows one Shop group with 2 monitors.
- AE2. A group "Shop" with 3 HTTP monitors and 1 heartbeat; clicking the group's Run queues 3 runs and reports 1 skipped. Pause pauses all 4 and an open Edit form of one of them reports a conflict on save.
- AE3. `PATCH {"group": null}` clears the group; `PATCH {"name": "x"}` keeps it. `GET /api/v1/monitors?group=SHOP` returns the Shop monitors. `POST /api/v1/groups/rename {"group":"Shop","newName":"Store"}` returns `renamed: 4`.
- AE4. Export then import into an empty instance keeps every monitor's group.
- AE5. A monitor with run retention of 1 day still shows 90 days of uptime cells on Details for the days the rollup recorded.
- AE6. A failing monitor appears in the Attention strip and the headline counts it. Its 24h strip has red cells for the failing buckets.

### Scope Boundaries

- Out of scope:
  - Logbook and Mosaic as separate themes.
  - A wall-display or kiosk mode.
  - Any change to check execution, scheduling or alerting.
  - Group-level settings (alert recipients, intervals) and nested groups.
  - Group-level permissions: any signed-in user and any write key can act on any group, as with monitors.
- The 24h strip and 90-day row use UTC buckets. Day cells are labeled with the browser's local date of the UTC day start; they are not re-bucketed per time zone.

---

## Planning Contract

### Key Technical Decisions

- KTD1. Column `Monitor.GroupName` (`[MaxLength(100)]`, nullable, no index), not `Group`, because `GROUP` is a reserved word and "group" already names the Failing section in code (`IsInFailingGroup`). No index because the group filter uses `ToLower()` (KTD3), which cannot seek, and installs are small. The API and JSON field is still `group`. One migration `AddMonitorGroupName` per provider set.
- KTD2. Normalization in one helper, `MonitorGroups` (`Services/Monitoring/MonitorGroups.cs`), used by pages, API, imports and rename:
  - `Normalize(string?)`: trim; blank becomes null.
  - `Validate(string?)`: at most 100 characters, no NUL. `MonitorForm.Validate` and rename call it.
  - `CanonicalizeAsync(db, name, excludeMonitorId?, excludeGroup?)`: loads distinct non-null group names of other monitors and picks one equal under `OrdinalIgnoreCase`, in memory (the `AGENTS.md` collation rule). `MonitorEditor.UpdateAsync` excludes the monitor being edited, so editing the only member of "shop" to "Shop" stores "Shop". `RenameGroupAsync` excludes the source group, so a case-only rename stores the new spelling.
  - `MonitorEditor.CreateAsync`/`UpdateAsync` call `Normalize` and `CanonicalizeAsync`. Bulk transfer canonicalizes against existing groups plus earlier file entries.
- KTD3. Group filters in queries use `m.GroupName != null && m.GroupName.ToLower() == name.ToLower()`, which translates on both providers. Group operations live in `MonitorCommands` over that query, and the page and the API call the same methods:
  - Pause and resume: `PauseAsync`/`ResumeAsync`, which already renew stamps and set heartbeat deadlines (heartbeat invariant 1).
  - Run: new `QueueGroupRunAsync(group)` calls `QueueManualRunAsync(id, reuseInFlight: true, rejectDisabled: true)` for each HTTP monitor in the group and returns `queued`, `alreadyInFlight` and `skipped` (heartbeats and disabled monitors). The Index batch "run" for selected IDs is unchanged.
  - Rename: new `RenameGroupAsync(from, to)`, a set-based `ExecuteUpdateAsync` of `GroupName` and `ConcurrencyStamp` over the group query, after `Validate` and `CanonicalizeAsync` (KTD2).
- KTD4. Group names stay out of URL paths (slashes, encoding, case): group endpoints take the name in a JSON body, read with the existing `ReadBodyAsync` (16 KB, object, no duplicate properties). Each handler checks its own allowlist of fields (`group`; rename also `newName`) and rejects unknown fields, as `ApiMonitorBody` does.
- KTD5. Rollup storage: one table `MonitorRunBuckets` (`MonitorId`, `BucketStart` (UTC, 15-minute aligned), `RunCount`, `FailureCount`, `TotalDurationMs` (long)), primary key (`MonitorId`, `BucketStart`), FK to `Monitors` with cascade delete (the Delete page uses `ExecuteDeleteAsync` and relies on the database cascade). Daily uptime is the sum of a day's buckets, computed in memory for one monitor (at most 8,640 rows for 90 days). The 24h strip for the index reads the last 96 buckets of all monitors (about 3,000 rows).
- KTD6. One writer fills the table: `RunBucketRollup` (`Services/Monitoring/RunBucketRollup.cs`). Every write path takes the Hangfire distributed lock `hawk:run-buckets` through the job storage connection, as `MonitorScheduler` does with `hawk:scheduler:tick`; a busy lock skips the call. Jobs use `[AutomaticRetry(Attempts = 0)]`, as the scheduler does, because the next minute's run repairs any gap.
  - `RecomputeAsync(from, to)`: processes the range one UTC day at a time. It loads completed runs of the day as `(MonitorId, StartedAt, Success, DurationMs)`, buckets them in C#, loads the existing bucket rows of that day, and saves with one `SaveChanges`.
  - Write rule: buckets that started less than 23 hours ago are overwritten with the computed values, because run retention is at least one day, so their runs are complete. Older buckets are written only when missing or when the computed `RunCount` is higher than the stored one. Pruning only lowers counts, so recomputing old days never shrinks history.
  - Refresh (recurring job `hawk-run-buckets`, every minute): `RecomputeAsync(start, now)` where `start` is the earlier of now minus 30 minutes and the newest stored bucket start, but not more than 7 days back. The 30 minutes exceed the maximum timeout (300 s) plus slack, so runs that finish late are counted; the newest-bucket start repairs gaps after downtime. It also deletes buckets older than 95 days.
  - Rejected: writing from each run completion in `MonitorExecutor` (six save points, including the ping request path under a 5 s lock). Without raw SQL there is no upsert, so two workers finishing runs for one monitor in one bucket would race on insert.
  - Consequence: the newest cell lags up to about a minute behind. The headline, Attention strip and status use live data from `MonitorStatusService`, so the lag only affects the strips and uptime figures.
- KTD7. Backfill and late history:
  - On every startup (after migrations), enqueue `BackfillAsync`, which is `RecomputeAsync(now - 90 days, now)`. Because of the write rule it is idempotent and cheap to repeat, so a failed backfill is retried on the next start, and nothing depends on the table being empty.
  - `StatusCakeImporter.SaveAlertImportAsync` enqueues `RecomputeAsync` over the UTC days of the imported runs.
  - Scheduling off: when `Hawk:Scheduler:Enabled=false`, startup calls `RecurringJob.RemoveIfExists("hawk-run-buckets")` and skips the backfill, and every job method returns early when the setting is off (as `MonitorScheduler.TickAsync` does).
- KTD8. Theme: the existing mechanism stays (`.dark` class set before first paint from `hawk.theme` or `prefers-color-scheme`). Tower tokens are CSS custom properties on `:root` (light) and `.dark` (dark) in `Assets/tailwind.css`, exposed to Tailwind v4 via `@theme`. The existing `hawk-*` component classes are restyled on those tokens instead of adding a parallel class set. Dark-first means the dark palette is the design reference, not the default for light-OS users.
- KTD9. Font: download the Archivo variable woff2 with the `wdth` axis (62-125, weights 100-900, OFL; from the Archivo upstream repository or the Google Fonts CSS2 variable response) into `Hawk.Web/wwwroot/fonts/`, with the OFL license file next to it, and declare it with `@font-face` (`font-stretch: 62% 125%`). The Dockerfile `COPY . .` already includes `wwwroot`. Verify the width axis works in the browser (a static file silently ignores `font-stretch`).
- KTD10. View data: `MonitorBoardService` (`Services/Monitoring/MonitorBoardService.cs`) builds the index view model (groups, failing list, headline, per-monitor 24h cells), and `MonitorHistoryService` builds the details data (24h buckets, 90-day days, uptime figures). Pages stay thin, and the API `uptime` field (R16) and the `GET /api/v1/groups` counts reuse them. The headline wording is a pure function with unit tests.
- KTD11. Charts are server-rendered inline SVG and CSS grids, with no chart library. The existing `data-utc` time rendering is used for bucket and day labels.

### Assumptions

- About 30-100 monitors. The index query budget (one monitors query, the existing last-run query, one buckets query) stays well under 100 ms on Postgres at that size. Larger installs would need paging, which is deferred.
- The two mockups are the visual reference. Pixel parity is not required; layout structure, tokens and the listed elements are.

### Sequencing

Release A (U1-U4) ships and is deployed before release B (U5-U9) starts. Release A's Index changes are limited to group sections and header actions inside the current layout, so release B's layout rewrite discards little. U5 (rollup) does not depend on U6 (tokens and font), so they may run in parallel; U7-U9 depend on both.

---

## Implementation Units

### U1. Group model, normalization and editor

- **Goal:** Store the group and normalize it on every write path.
- **Requirements:** R1, R2, R6 (service part)
- **Dependencies:** none
- **Files:** `Hawk.Web/Data/Monitoring/Monitor.cs`, `Hawk.Web/Data/ApplicationDbContext.cs`, `Hawk.Web/Data/Migrations/*AddMonitorGroupName*`, `Hawk.Web/Data/Migrations/Postgres/*AddMonitorGroupName*`, `Hawk.Web/Services/Monitoring/MonitorGroups.cs` (new), `Hawk.Web/Services/Monitoring/MonitorEditor.cs`, `Hawk.Web/Services/Monitoring/MonitorCommands.cs` (rename), `Hawk.Web/Pages/Monitors/MonitorForm.cs`, `Hawk.Tests/MonitorGroupsTests.cs` (new), `Hawk.Tests/MonitorEditorTests.cs`
- **Approach:**
  1. Add `GroupName` (KTD1); generate both migrations with the commands in `AGENTS.md`.
  2. Add `MonitorForm.Group` (`[MaxLength(100)]`); `MonitorForm.Validate` calls `MonitorGroups.Validate`.
  3. `MonitorGroups` per KTD2; `MonitorEditor` maps the form's group through it on create and update.
  4. `MonitorCommands.RenameGroupAsync(from, to)` and `QueueGroupRunAsync(group)` per KTD3. Rename returns the count or a validation error (blank, too long, NUL).
- **Patterns to follow:** `MonitorEditor` stamp handling; `MonitorCommands.PauseAsync` set-based update.
- **Test scenarios:**
  - Covers AE1. Saving "shop " when "Shop" exists stores "Shop".
  - Blank or whitespace group stores null.
  - 101 characters or a NUL fails validation with key `Group`.
  - Rename of "Shop" to "Store" updates all members, renews each stamp, leaves other groups alone; rename into an existing group with other casing adopts that casing.
  - Rename "shop" to "Shop" stores "Shop"; editing the only member of "shop" to "Shop" stores "Shop".
  - Rename to blank, 101 characters or a NUL is rejected and changes nothing.
  - `QueueGroupRunAsync` on a group with 2 HTTP, 1 disabled HTTP and 1 heartbeat monitor returns 2 queued and 2 skipped; a second call right after returns 2 already in flight.
  - Postgres: the group filter matches case-insensitively (`ToLower` translation).
  - `Migrations_are_up_to_date_with_the_model` stays green.
- **Verification:** Unit and Postgres tests green; both migration sets contain the column and index.

### U2. Group UI (current design)

- **Goal:** Groups on Create, Edit, Details and Index.
- **Requirements:** R3, R4, R5, R6
- **Dependencies:** U1
- **Files:** `Hawk.Web/Pages/Monitors/Create.cshtml(.cs)`, `Edit.cshtml(.cs)`, `Details.cshtml(.cs)`, `Index.cshtml(.cs)`, `Hawk.Tests/MonitorGroupsHttpTests.cs` (new), `e2e/tests/groups.spec.ts` (new)
- **Approach:**
  1. Create/Edit: a text input `Form.Group` with a `<datalist>` of existing group names (sorted `OrdinalIgnoreCase`), placed under Name, for both types.
  2. Details: breadcrumb and chip linking to `Index#group-<anchor>`. The anchor is a slug of the lowercased name plus the first 6 hex characters of its SHA-256, so distinct names never share an anchor; Ungrouped uses `group-none`.
  3. Index: build sections from all monitors (KTD10 can start here as a small helper and move into `MonitorBoardService` in U7). The Failing section is unchanged; failing monitors also appear in their group, marked failing (R4). Duplicate checkbox values are fine because the batch handler already de-duplicates IDs.
  4. Group headers sit inside the existing `batch-form`, so they must not contain forms. The select-all checkbox (JS checks every checkbox of the group's monitors, in both sections) feeds the existing batch bar. Pause, Resume and Run are buttons with `formaction` (`?handler=GroupPause`, ...) and `name="group" value="<name>"`, posting to new handlers that call KTD3.
  5. Rename uses one form outside `batch-form` (a small dialog with a name input). Before submitting, JS asks for confirmation when the new name matches another existing group case-insensitively ("Merge into <name> (N monitors)?").
  6. Flash messages report counts as the existing handlers do.
- **Patterns to follow:** existing Index batch handlers and flash messages; Create/Edit parity rule.
- **Test scenarios:**
  - Index renders monitors under their group headers with Ungrouped last; a failing monitor shows in the top section and in its group, and a group whose members all fail still has its header and counts.
  - Covers AE2. Group Run queues HTTP runs and reports skipped heartbeats; group Pause pauses all and renews stamps.
  - Group Rename moves members; an empty new name shows a flash error.
  - Create and Edit save the group; Edit shows the current group.
  - e2e: create two monitors in one group, see the group header, pause the group, see both paused; then select monitors in two groups and run the batch Resume, which still posts to the batch handler.
- **Verification:** HTTP tests and the new e2e spec green on both stacks.

### U3. Groups in the API and JSON

- **Goal:** API and export/import parity for groups.
- **Requirements:** R7, R8
- **Dependencies:** U1
- **Files:** `Hawk.Web/Infrastructure/ApiEndpoints.cs`, `ApiModels.cs`, `ApiMonitorBody.cs`, `Hawk.Web/Pages/Monitors/MonitorJsonPort.cs`, `Hawk.Web/Services/Import/MonitorTransferService.cs`, `Hawk.Web/Pages/Admin/Transfer.cshtml`, `Hawk.Web/Data/Seeding/MonitorSeeder.cs`, `docs/agent-api.md`, `Hawk.Tests/ApiHttpTests.cs`, `ApiPostgresHttpTests.cs`, `MonitorTransferTests.cs`, `e2e/tests/api.spec.ts`
- **Approach:**
  1. `group` on `ApiMonitorListItem` and `ApiMonitorDetail`; `ApiMonitorBody` accepts `group` with merge-patch semantics (R7).
  2. `?group=` filter on the list (KTD3; empty value = ungrouped).
  3. `GET /api/v1/groups`: groups from monitors plus `GetStatusesAsync`, sorted `OrdinalIgnoreCase`, display spelling from the stored value. `failingCount` counts monitors whose status has a non-null `Failure` (the `IsInFailingGroup` rule), not `Health == Failing`, which `Overdue` would hide.
  4. `POST /api/v1/groups/{pause,resume,run,rename}` per KTD3/KTD4; 404 `not_found` when no monitor has the group; Information logs with `user id, key id, group, action, count`.
  5. `MonitorExportModel.Group`; parser accepts a missing field; transfer plan canonicalizes groups (KTD2) and the preview table shows the group column.
  6. Seeder puts `[Seed]` monitors in group `Seed`.
- **Patterns to follow:** existing pause/resume/run handlers and `ApiMonitorBody` field handling; `EndpointList`.
- **Test scenarios:**
  - Covers AE3. PATCH null clears, PATCH without `group` keeps, list filter is case-insensitive, rename returns the count.
  - `?group=` returns only ungrouped monitors; unknown group returns `[]`.
  - Group endpoints: read key gets 403, unknown group 404, missing, empty or non-string `group` 400, unknown body field 400.
  - Rename with a `newName` of 101 characters or containing NUL returns 400 keyed `newName`.
  - A group's `failingCount` from the API equals the Index header count, including an overdue monitor whose last run failed.
  - Group run returns `queued`, `alreadyInFlight` and `skipped` counts (Postgres).
  - Covers AE4. Export/import round trip keeps groups; a v3 file without `group` imports with no group.
  - `GET /api/v1/groups` counts failing and paused correctly.
- **Verification:** API tests green; `docs/agent-api.md` documents every new field and endpoint.

### U4. Release A docs and version

- **Goal:** Ship 0.13.0.
- **Requirements:** all of release A
- **Dependencies:** U2, U3
- **Files:** `Hawk.Web/Hawk.Web.csproj`, `CHANGELOG.md`, `AGENTS.md` (new "Monitor Groups" section, API section update, version), `README.md` version line if present
- **Approach:** Version 0.13.0; changelog entry; document the column, normalization, endpoints and invariants in `AGENTS.md`.
- **Test expectation:** none -- documentation and version only.
- **Verification:** The version shows in the footer; docs match the shipped behavior.

### U5. Run-bucket rollup

- **Goal:** Durable 15-minute run buckets for strips and uptime.
- **Requirements:** R11, R15, R16 (data)
- **Dependencies:** release A deployed
- **Files:** `Hawk.Web/Data/Monitoring/MonitorRunBucket.cs` (new), `Hawk.Web/Data/ApplicationDbContext.cs`, migrations `AddMonitorRunBuckets` in both sets, `Hawk.Web/Services/Monitoring/RunBucketRollup.cs` (new), `Hawk.Web/Services/Import/StatusCakeImporter.cs`, `Hawk.Web/Program.cs` (recurring job registration, startup backfill enqueue), `Hawk.Tests/RunBucketRollupTests.cs` (new), `Hawk.Tests/PostgresIntegrationTests.cs`
- **Approach:**
  1. Entity and mapping per KTD5.
  2. `RunBucketRollup.Bucket(IEnumerable<runTuple>)`: pure, unit-tested aggregation; `Merge(existing, computed, now)`: pure write rule (KTD6).
  3. `RecomputeAsync`, `RefreshAsync` and `BackfillAsync` per KTD6/KTD7, all under the `hawk:run-buckets` lock.
  4. Startup: register `RecurringJob.AddOrUpdate("hawk-run-buckets", ..., Cron.Minutely)` and enqueue the backfill where the scheduler is started; when scheduling is off, remove the recurring job instead (KTD7).
  5. StatusCake alert import enqueues the recompute of the imported days.
- **Execution note:** Write the pure bucketing and merge tests first and see them fail.
- **Test scenarios:**
  - Runs at 10:14:59 and 10:15:00 land in different buckets; only `completed` runs count.
  - Recomputing a window twice yields identical rows (idempotent).
  - A run completing 4 minutes after it started is counted on the next refresh.
  - Merge rule: a recent bucket is overwritten with lower counts; a bucket older than 23 hours keeps its stored counts when the recomputed count is lower, and takes the recomputed values when higher.
  - Covers AE5. After runs older than one day are pruned, a backfill keeps the existing buckets unchanged.
  - Deleting a monitor cascades its buckets (Postgres, via `ExecuteDeleteAsync`).
  - Gap repair: with the newest bucket 2 hours old and runs in between, one refresh fills every missing bucket.
  - Backfill over 3 days of seeded runs produces the same buckets as incremental refreshes.
  - Concurrency (Postgres): a backfill and a refresh started together over the same buckets both complete without a key violation (one waits or skips on the lock), and the rows are correct afterwards.
  - Imported StatusCake runs from 5 days ago appear in buckets after the import's recompute job runs.
  - With `Hawk:Scheduler:Enabled=false`, startup removes `hawk-run-buckets` and a queued refresh does nothing.
  - Buckets older than 95 days are deleted.
- **Verification:** Tests green on InMemory and Postgres; in a dev run the table fills within a minute of runs.

### U6. Tower tokens, font and shell

- **Goal:** Tower look across all pages through tokens and restyled component classes.
- **Requirements:** R9
- **Dependencies:** none (can run in parallel with U5)
- **Files:** `Hawk.Web/Assets/tailwind.css`, `Hawk.Web/Assets/components/*.css`, `Hawk.Web/wwwroot/fonts/` (Archivo woff2 + OFL), `Hawk.Web/Pages/Shared/_Layout.cshtml`, `Hawk.Web/Pages/Shared/_Flash.cshtml`, Identity layout pages under `Hawk.Web/Areas/Identity/Pages/` only where they hard-code colors
- **Approach:**
  1. Tokens per KTD8 with the mockup's values:
     - Dark: `--bg #10151B`, `--panel #161C24`, `--line #273039`, `--text #E9EDF1`, `--muted #8A96A3`, `--up #39AD8C`, `--down #F0533F`, `--paused #6B7682`, `--chart #8FB4D9`, `--down-tint rgba(240, 83, 63, 0.12)`, `--hover rgba(233, 237, 241, 0.045)`, `--faint rgba(233, 237, 241, 0.10)`.
     - Light: `--bg #E9ECEF`, `--panel #FFFFFF`, `--line #D3D8DE`, `--text #141A21`, `--muted #5C6773`, `--up #1C8A68`, `--down #D03A2A`, `--paused #6B7682`, `--chart #24598F`, `--down-tint rgba(208, 58, 42, 0.09)`, `--hover rgba(20, 26, 33, 0.04)`, `--faint rgba(20, 26, 33, 0.10)`.
  2. Font per KTD9; remove the two unpkg font links from `_Layout.cshtml`; `--font-sans` is Archivo and `--font-mono` a system monospace stack; `.condensed` (`font-stretch: 68%`) and `.num` (tabular numerals) utilities.
  3. Topbar: condensed wordmark, compact nav, theme toggle unchanged in behavior; mobile drawer kept.
  4. Restyle `hawk-btn`, `hawk-card`, `hawk-input`, `hawk-badge`, `hawk-alert`, tables (2 px radius, 1 px lines, status colors).
- **Test expectation:** none -- styling; verified by screenshots and the existing e2e suite.
- **Verification:**
  - Screenshots of Index, Details, Create, Login and Admin pages in dark and light at desktop and 390 px width: no console errors, no horizontal overflow.
  - Computed `font-family` is Archivo and `font-stretch` changes rendered width.
  - No external font requests (no `unpkg.com`, no `fonts.googleapis.com`).
  - Focus outlines are visible on buttons, links and inputs in both themes, and text and status colors keep at least 4.5:1 (text) and 3:1 (status marks) contrast against their surfaces.

### U7. Monitors overview (Tower)

- **Goal:** The Tower board as the monitors index.
- **Requirements:** R4, R5, R6, R10, R11
- **Dependencies:** U2, U5, U6
- **Files:** `Hawk.Web/Services/Monitoring/MonitorBoardService.cs` (new), `Hawk.Web/Pages/Monitors/Index.cshtml(.cs)`, `Hawk.Web/Pages/Monitors/_StatusStrip.cshtml` (new partial), `Hawk.Web/wwwroot/js/site.js`, `Hawk.Tests/MonitorBoardTests.cs` (new), `e2e/tests/monitors.spec.ts`, `e2e/tests/groups.spec.ts`
- **Approach:**
  1. `MonitorBoardService` per KTD10: sections, failing list (`IsInFailingGroup`), headline (pure `Headline.For(...)`), 96 cells per monitor from buckets.
  2. Page layout per the mockup: headline, Attention strip, rail plus board, 35 px rows; group headers keep U2's actions; batch bar unchanged in behavior.
  3. Strip partial renders 96 cells (CSS grid) per R11: heights, full-height red cells, `title` tooltip text and an `aria-label` summary on the strip; individual cells are `aria-hidden`.
  4. Responsive row and touch targets per R10.
  5. Keep the element names and IDs the e2e suite selects on, or update the specs in the same unit.
- **Test scenarios:**
  - Headline wording: none down, one down, several down, all paused, no monitors.
  - Covers AE6. A failing bucket renders a red cell; an empty bucket renders grey; cell count is 96.
  - The index still supports select, batch pause/resume/run and group actions (e2e).
- **Verification:** e2e green on both stacks; screenshots match the mockup structure.

### U8. Monitor details and heartbeat details (Tower)

- **Goal:** The Tower details page for both monitor types, and API uptime.
- **Requirements:** R3 (breadcrumb kept), R12, R13, R15, R16
- **Dependencies:** U5, U6
- **Files:** `Hawk.Web/Services/Monitoring/MonitorHistoryService.cs` (new), `Hawk.Web/Pages/Monitors/Details.cshtml(.cs)`, `Hawk.Web/Infrastructure/ApiEndpoints.cs`, `ApiModels.cs`, `docs/agent-api.md`, `Hawk.Tests/MonitorHistoryTests.cs` (new), `Hawk.Tests/ApiHttpTests.cs`, `e2e/tests/heartbeat.spec.ts`
- **Approach:**
  1. `MonitorHistoryService`: 96 buckets, 90 day cells, uptime for 24h/7d/30d (null without runs).
  2. Status lead per R12. State and failing-since come from `MonitorStatusService`. Up-since comes from the buckets (newest bucket with `FailureCount > 0`), refined to the first successful completed run after that bucket's last failed run when those runs are still retained. This is one bounded query on the monitor's runs within that bucket and the next ones, never a scan of the full history.
  3. Response-time chart as inline SVG polyline of bucket averages (KTD11); heartbeat branch shows the ping timeline instead. Empty states and the day color scale per R12.
  4. Existing actions (Run now, Edit, Export, Delete, Regenerate URL) and the recent runs table keep their handlers.
  5. API detail gains `uptime` (R16).
- **Test scenarios:**
  - Uptime: 3 of 4 runs passed in a day gives 75%; a day without runs is null.
  - 90 cells always render, oldest first; the color thresholds map 100%, 99.5%, 97% and 90% to the four R12 states.
  - Status lead for failing, up, paused, disabled, pending HTTP and waiting heartbeat monitors.
  - Up-since with 1-day run retention and a failing bucket 10 days ago reads from the bucket, not from the retained runs; with no failing bucket and a monitor older than the rollup history it reads "UP for more than 90 days".
  - Heartbeat details show the ping URL and timeline; HTTP details do not.
  - API detail `uptime` matches the service; null for a new monitor.
- **Verification:** Tests green; heartbeat and monitor e2e specs green; screenshots.

### U9. Run diagnostics, release B docs and version

- **Goal:** Finish the Tower pages and ship 0.14.0.
- **Requirements:** R14, all of release B
- **Dependencies:** U6, U7, U8
- **Files:** `Hawk.Web/Pages/Monitors/Runs/Details.cshtml`, `Hawk.Web/Hawk.Web.csproj`, `CHANGELOG.md`, `AGENTS.md`, `README.md`
- **Approach:** Restyle run diagnostics in the Tower layout (status lead, request and response panels); version 0.14.0; document the rollup table, job, tokens and font in `AGENTS.md`.
- **Test expectation:** none for the restyle -- existing run diagnostics e2e coverage applies.
- **Verification:** e2e green; docs updated.

---

## Verification Contract

- Per release, before deploy:
  - `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test Hawk.sln -v:m` green.
  - `ando run` exits 0 (no `-p publish`).
  - E2E green on SQL Server (`docker compose -f docker-compose.e2e.yml ...`) and PostgreSQL (`docker-compose.e2e.postgres.yml`).
  - Release B also: screenshots (Docker Playwright) of the main pages in both themes and at mobile width, with no console errors or horizontal overflow.
- Code review per release: ce-code-review personas on Opus plus a Codex pass; fix findings and re-review when fixes are material.

## Definition of Done

- Every unit's verification met; both migration sets include the new column/table; `AGENTS.md`, `CHANGELOG.md` and `docs/agent-api.md` describe the shipped behavior.
- Each release deployed to prod1 per the 0.12.0 recipe: push `main`, image from `git archive HEAD` to `registry.dualconsult.net`, `pg_dump` backup, `release.env` pin committed and pushed in the infra repo, `scripts/deploy-app.sh hawk app edge`, live checks (migration applied, `/live` 200, `/api/v1` 401, new pages load), and a deployment record appended to this plan.
- Rollback: both migrations are additive, so the previous image digest runs against the new schema. After rolling release B back to a 0.13.x image, delete the `hawk-run-buckets` recurring job in the Hangfire dashboard; the old image cannot load its type and would log an error every minute.

---

## Appendix

### Deferred

- Index paging or virtualization for installs with hundreds of monitors.
- Local-time-zone day buckets for the 90-day row.
- Group-level alert settings.
- Flow analysis (spec-flow) was not run separately: the flows follow the two confirmed mockups.

---

## Production Deployment — Release A (0.13.0), 2026-10-29

- Source commit `106388a` (pushed to GitHub `main`); image `registry.dualconsult.net/dualconsult/hawk@sha256:f61f9dd7e547b3887d8a34a8d20a5d862675db94b40b666061577b2f669dbd3e`, built from `git archive HEAD` (not pushed to GHCR).
- Backup: `/srv/dualconsult/backups/hawk/hawk-pre-0.13.0-20261029T103001Z.dump` on the db VM. Release pin: infra commit `bd3bffa`; deployed with `scripts/deploy-app.sh hawk app edge`.
- Verification:
  - `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test`: 386/386.
  - `ando run`: exit 0, 298 passed, 88 PostgreSQL tests skipped inside the ando container. All PostgreSQL test classes now share one container (`PostgresCollection`); before, each class waited about a minute for an unavailable container and the test step hit ando's 5-minute limit.
  - E2E: 18/18 on SQL Server and 18/18 on PostgreSQL.
- Review: correctness, security, project standards, testing, maintainability, API contract and data migration reviewers plus a Codex adversarial pass (`gpt-6-luna`, independence verified). Fixed in `399ee12`:
  - NUL in group lookups is rejected (it caused a PostgreSQL 500).
  - One display-spelling helper.
  - Same-name rename is a no-op.
  - Dead `OtherMonitors` removed.
  - Two e2e specs fixed for the twin links of failing monitors.
- Accepted, not changed:
  - A stale page can act on a later group that reuses a name, which comes with name-based groups.
  - Concurrent group runs can race the existing in-flight check, which predates this change.
- Live checks:
  - Migration `AddMonitorGroupName` applied; all 48 monitors have no group.
  - The container runs v0.13.0 with 0 restarts, UID 1654 and a read-only root filesystem.
  - `/live` returns 200 and `/ready` returns 404.
  - `/api/v1` and `/api/v1/groups` without a key return 401 problem+json.
- Rollback: the 0.12.0 digest `sha256:5dffc17d…` runs against the new schema (nullable column).

## Production Deployment — Release B (0.14.0), 2026-10-29

- Commits: U5 `6a213d1`, shared strip `59578cf`, U7+U8 `a725f5b`, U9 `2e20513`, docs/version `5e17cf2`, topbar fix `03ccd8b`, review fixes `1d53ee7` and `d542c27`, rollback note `78d19d4` (pushed to `main`).
- Verification: 431 unit/integration tests (PostgreSQL required), e2e 18/18 on SQL Server and PostgreSQL, `ando run` green locally, screenshots (dark/light, desktop/390 px) without console errors or overflow, ce-code-review (Opus correctness/security, Sonnet personas, Codex gpt-6-luna cross-model) plus an Opus re-review of `1d53ee7`; `d542c27` was covered by tests and PostgreSQL e2e only.
- Image: `registry.dualconsult.net/dualconsult/hawk@sha256:2f28e041f178b7b9e5b07345c3ae089d538cfc98801d95450fe115402b26595f` (from `git archive HEAD` at `78d19d4`).
- Backup: `/srv/dualconsult/backups/hawk/hawk-pre-0.14.0-20261029T113944Z.dump` on the db VM.
- Infra: `e2cc48c` (`apps/hawk/release.env` only), `scripts/deploy-app.sh hawk app edge`.
- Live: v0.14.0, 0 restarts, UID 1654, read-only root; migration `20261029103344_AddMonitorRunBuckets` applied; startup backfill succeeded in about 9 s (37,845 buckets, 46 monitors, back to 2026-10-12); recurring `hawk-run-buckets` registered and its first refresh succeeded; no rollup errors in the log; `/live` 200, `/ready` 404 (blocked publicly by Caddy), `/api/v1/monitors` 401.
- Rollback: pin the 0.13.0 digest `sha256:f61f9dd7e547b3887d8a34a8d20a5d862675db94b40b666061577b2f669dbd3e` (the new table is additive), then delete the `hawk-run-buckets` recurring job and any enqueued, scheduled or failed `IRunBucketRollup` jobs in the Hangfire dashboard.
- Known, not fixed: `.dockerignore` excludes `Hawk.Web/wwwroot/lib/`, so client-side jQuery validation has never shipped (server validation unaffected); a StatusCake import into an old bucket whose runs are already pruned does not raise that bucket's counts; with the scheduler disabled the import-triggered recompute does nothing (by design).
