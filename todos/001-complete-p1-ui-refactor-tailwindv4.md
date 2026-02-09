---
status: complete
priority: p1
issue_id: "001"
tags: [ui, tailwind, accessibility, refactor]
dependencies: []
---

# UI Refactor: Tailwind v4 + Yellow/Black Brand (Phase 1-2)

Execute the plan in `docs/plans/2026-02-09-refactor-ui-tailwind-v4-modern-saas-design-plan.md`, starting with foundation work:
- Token system + dark mode primitives
- Component utilities (`hawk-*`)
- Layout shell/nav updates
- Remove Bootstrap leftovers
- Deterministic CSS build for Docker/publish

## Problem Statement

The web app UI is currently a mix of legacy Bootstrap assets and Tailwind utilities with a zinc/green palette. It lacks dark mode and does not match the marketing site brand (yellow/black). Accessibility and responsive navigation also need to be upgraded to meet WCAG 2.2 AA expectations.

## Findings

- Tailwind v4 is already present and compiled output is written to `Hawk.Web/wwwroot/css/app.css` via `Hawk.Web/package.json` (`npm run build:css`).
- `_Layout.cshtml` currently references only `~/css/app.css`, so Bootstrap isn’t used at runtime but still exists on disk under `Hawk.Web/wwwroot/lib/bootstrap/`.
- `Hawk.Web/Assets/tailwind.css` currently defines `hawk-*` component classes using `@layer components` with zinc/green styling; it does not yet use `@theme`/`@utility`.
- No JS behavior exists yet (`Hawk.Web/wwwroot/js/site.js` is a stub), so dark mode + mobile drawer behavior must be introduced.

## Proposed Solutions

### Option 1: Follow plan directly (recommended)

**Approach:** Implement Phase 1 then Phase 2, keeping changes incremental and committed in logical units. Add a deterministic CSS build step for Docker/publish to prevent stale CSS.

**Pros:**
- Matches the written plan
- Lowest risk of “half-migrated” design system
- Makes future phases (pages + polish) straightforward

**Cons:**
- Touches multiple files early (CSS, layout, JS, Docker/MSBuild)

**Effort:** 1-3 days for Phase 1-2 (depending on build integration choice)

**Risk:** Medium

## Recommended Action

1. Foundation: implement design tokens and semantic variables, dark mode class toggle (no-flash), and split component CSS per plan.
2. Remove Bootstrap assets and any leftover references.
3. Make CSS build deterministic for Docker/publish (Docker stage or MSBuild target).
4. Phase 2: update `_Layout.cshtml` shell/topbar and add mobile drawer + theme toggle behavior in external JS.

## Technical Details

**Affected files (expected):**
- `Hawk.Web/Assets/tailwind.css`
- `Hawk.Web/tailwind.config.js`
- `Hawk.Web/Pages/Shared/_Layout.cshtml`
- `Hawk.Web/wwwroot/js/site.js` (or split into `theme.js`, `mobile-menu.js`, etc.)
- `Hawk.Web/wwwroot/lib/bootstrap/` (delete)
- `Hawk.Web/Dockerfile` and/or `Hawk.Web/Hawk.Web.csproj` (CSS build integration)

## Acceptance Criteria

- [x] Bootstrap assets removed and no Bootstrap references remain
- [x] Brand tokens + semantic variables exist with light/dark modes
- [x] Theme toggle works and persists (and no light flash on load)
- [x] Layout shell uses new brand styling, has usable mobile navigation
- [x] CSS build is deterministic for Docker/publish (no stale `app.css`)

## Work Log

### 2026-02-09 - Kickoff

**By:** Codex

**Actions:**
- Created this todo to track Phase 1-2 execution from the plan.

### 2026-02-09 - Phase 1 Foundation Complete

**By:** Codex

**Actions:**
- Replaced `Hawk.Web/Assets/tailwind.css` with a tokenized Tailwind v4 `@theme` setup and semantic CSS variables (light + dark).
- Added component CSS using `@utility` under `Hawk.Web/Assets/components/` and base utilities under `Hawk.Web/Assets/utilities/`.
- Updated `Hawk.Web/Pages/Shared/_Layout.cshtml` to load fonts, add skip link, add a theme toggle, and apply theme pre-paint.
- Added `Hawk.Web/wwwroot/js/theme.js` for persisted theme toggling (`localStorage` key `hawk.theme`).
- Removed legacy Bootstrap assets and template CSS (`Hawk.Web/wwwroot/lib/bootstrap/**`, `Hawk.Web/wwwroot/css/site.css`, `Hawk.Web/Pages/Shared/_Layout.cshtml.css`).
- Regenerated CSS with `npm --prefix Hawk.Web run build:css` (Tailwind v4.1.18).

### 2026-02-09 - Phase 2 Navigation + Inputs

**By:** Codex

**Actions:**
- Updated `Hawk.Web/Pages/Shared/_LoginPartial.cshtml` to use `hawk-btn` + variants correctly.
- Added a mobile navigation drawer (slide + backdrop) in `Hawk.Web/Pages/Shared/_Layout.cshtml`.
- Implemented mobile drawer behavior in `Hawk.Web/wwwroot/js/mobile-menu.js` (Escape/backdrop close, focus restore).
- Added focus styles for `.hawk-input` in `Hawk.Web/Assets/components/hawk-input.css`.
- Regenerated CSS with `npm --prefix Hawk.Web run build:css`.

### 2026-02-09 - Docker Build Includes Tailwind Build

**By:** Codex

**Actions:**
- Updated `Hawk.Web/Dockerfile` to build Tailwind CSS in a Node stage and copy `wwwroot/css/app.css` into the `dotnet publish` stage, preventing stale CSS in container deployments.
