---
title: UI polish from design review
type: feat
status: completed
date: 2026-10-29
product_contract_source: ce-plan-bootstrap
target_version: 0.16.0
---

# UI polish from design review

## Summary

Implement all 14 recommendations of a design review of the Tower UI (board, monitor and heartbeat details, run diagnostics, Create/Edit, alert emails) without new features, new pages or dependencies.

## Source

A designer review (Fable) produced before/after mockups from live pages with demo data. The mockups were local only (`/tmp/hawk-demo/mockups/fable/`); the items and decisions are recorded here.

## Items

1. Board group headers: one **Actions** `<details>` menu instead of Run/Pause/Resume/Rename buttons; "N monitors" pluralised.
2. Checkboxes: a `<label>` carries the 44 px touch target; the input stays 20 px (group select-all and rows).
3. Headline: `--text` with a down square while something is down (`.is-down` set server-side instead of `:has()`).
4. Page actions: **New monitor** plus a **More** menu (Import StatusCake, Pause all, Resume all), all widths.
5. Heartbeat attention card: last-ping time rendered as `<time data-utc>` (parsed from the reason written with `HeartbeatTokens.MissSince`).
6. Response-time chart: axis labels drawn after the bands with a panel-coloured halo; `overflow: visible` so the top label is not clipped.
7. Details: last/next check beside the uptime figures; Config in one value style with muted defaults ("Default recipient", "2xx only").
8. Details actions: Run now, Edit, Export JSON (Back removed; breadcrumb). Run diagnostics: Run again only.
9. Recent runs: ~30 px rows, result as square + word, time cell does not wrap.
10. Hover border only on interactive cards.
11. Heartbeat details: last ping / due by with the status, ping strip full width, Ping URL card in the left column, no Duration column.
12. Run diagnostics: muted "None" for empty blocks (`{}`/`[]` only for stored JSON fields), error in `--down`, alert line in words.
13. Create/Edit: one "Monitor" card (type, name|group, URL), "Check and alerts", one hint style, 880 px (Create/Edit parity).
14. Alert emails: `AlertEmailHtml` table layout with inline styles and hex colours; readable UTC times; summary table with an event-coloured bar; single alerts in the same card; one Result line instead of Status + Error; no duplicate When when a failure log exists; "At X" instead of "Between X and X".

Additions from the reviews: menus close on Escape and outside click (`site.js`, `details.hawk-menu` / `details[data-menu]`); the mobile More panel opens rightwards; flash messages pluralise.

## Verification

- Unit/integration: `HAWK_REQUIRE_POSTGRES_TESTS=1 dotnet test Hawk.sln -v:m`.
- E2E (Postgres stack): `monitors.spec.ts` navigates back through the breadcrumb; `groups.spec.ts` opens the Actions menu.
- Visual: demo stack screenshots at 1440 px (dark/light) and 390 px touch; the designer re-checked the implementation against the mockups.

## Review record

- Code review (independent): E2E break on the removed back link (fixed), `{}` bodies hidden as "None" (fixed), parse/format coupling for the heartbeat time (shared helper + test), email colours, single-alert shell, menu Escape/outside click, docs (all fixed).
- Design re-check: all 14 items done; fixed the off-screen mobile More panel, clipped chart label, repeated facts in the email block and wrapping run times.

## Deployment record

- 2026-10-29: 0.16.0 deployed to prod1 (`registry.dualconsult.net/dualconsult/hawk@sha256:8c40bc5121bf6004bef9918efc95717bbbece6ac795c5d59f7d39a8b6877d4d0`, app commit `a738091`, infra pin `fd08911`). No schema change.
- Backup before deploy: `/srv/dualconsult/backups/hawk/hawk-pre-0.16.0-20261029-141624.dump` on `dc-prod1-db`.
- Checks: footer v0.16.0, `/live` 200, `/api/v1/monitors` 401, 0 restarts, no errors in the log.
- Rollback: pin 0.15.0 (`sha256:47bc9e29bc4e7ff098e925d66119c49fdfadf6744790e83c651dae495819c1e7`) and redeploy; no data step needed.
