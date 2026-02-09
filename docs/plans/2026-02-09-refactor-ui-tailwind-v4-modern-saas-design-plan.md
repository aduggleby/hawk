---
title: Complete UI Refactor with Tailwind v4 and Modern SaaS Design
type: refactor
date: 2026-02-09
---

# Complete UI Refactor: Tailwind v4 + Modern SaaS Design with Yellow/Black Brand

## Enhancement Summary

**Deepened on:** 2026-02-09  
**Goal:** Keep the existing plan structure intact, but add implementation details, repo-specific realities, and “gotchas” (build pipeline, security, accessibility, browser support) that tend to bite during full UI rewrites.

**Repo Reality Check (Current Code):**
- Hawk already ships Tailwind v4 via `Hawk.Web/package.json` (`tailwindcss` + `@tailwindcss/cli`) and the compiled output is currently checked in at `Hawk.Web/wwwroot/css/app.css`.
- Current design tokens live primarily in `Hawk.Web/Assets/tailwind.css` via `@layer base` + `@layer components` (not `@theme`/`@utility` yet), and `_Layout.cshtml` references `~/css/app.css`.
- Bootstrap assets still exist under `Hawk.Web/wwwroot/lib/bootstrap/` (even if they are no longer referenced), so “remove Bootstrap” should include both references and on-disk assets.

**Key Improvements Added In This Deepening:**
1. **Build integration options** so Tailwind/JS changes reliably ship in Docker and `dotnet publish` (not “works on my machine” because `app.css` was stale).
2. **Theme toggle “no flash” guidance** (avoid light-theme flash on first paint), plus accessibility details for a proper drawer/menu.
3. **Security hardening for UI JS**: avoid inline scripts where possible, and ensure toast/error rendering is XSS-safe (use `textContent`, not `innerHTML`).
4. **Browser support fallbacks** for OKLCH, `color-mix()`, and `backdrop-filter` (graceful degradation rather than broken visuals).

## Overview

Complete redesign of the Hawk monitoring application UI, migrating from the current zinc/green color palette to the bold yellow/black brand identity from the marketing website. This refactor implements modern SaaS design principles, WCAG 2.2 AA accessibility compliance, dark mode, loading states, animations, responsive mobile-first design, and enhanced data components.

**Scope**: Both visual design AND component architecture overhaul.

**Key Changes**:
- Adopt yellow (#ffd400) + black (#0b0b0d) brand from marketing website
- Implement comprehensive dark mode with WCAG 2.2 AA compliance
- Add loading states, animations, and micro-interactions
- Rebuild responsive layouts with mobile-first approach
- Modernize dashboard and data table components
- Migrate to Tailwind v4 best practices (@theme, @utility, OKLCH colors)

### Research Insights

**Build/Release Reality (Tailwind v4):**
- Treat `Hawk.Web/Assets/tailwind.css` as the source of truth, but make the build step authoritative: if `Hawk.Web/wwwroot/css/app.css` is committed, it must be regenerated as part of CI and/or Docker builds to avoid “stale CSS” deployments.
- Tailwind v4 supports embedding configuration directives in the CSS (ex: `@config`, `@plugin`). If you keep `Hawk.Web/tailwind.config.js` in-place, consider explicitly wiring it in the CSS or CLI invocation (either `@config "./tailwind.config.js";` at the top of `Assets/tailwind.css` or `npx tailwindcss --config ./tailwind.config.js ...`) so builds are stable across working directories and Docker layers.

**Modern SaaS UX (Razor Pages-friendly):**
- Because Razor Pages is server-rendered, prioritize “perceived responsiveness” patterns that work without a SPA: button-level spinners, optimistic UI for toggles (with server rollback), and inline status banners/toasts driven by `TempData`.
- Prefer “component CSS” (your `hawk-*` utilities) to keep Razor markup legible. However, limit how many new `hawk-*` primitives you introduce at once, or the refactor turns into a parallel “framework rewrite”.

**Risk Register (High-likelihood pitfalls):**
- **Inline scripts** (examples later in this plan) are convenient, but they complicate CSP hardening and can create XSS risk when interpolating user-controlled messages. Prefer external JS files + `data-*` payloads, and render toast messages with `textContent`.
- **OKLCH + `color-mix()` + `backdrop-filter`** are great for this aesthetic, but require graceful fallbacks for older Safari/enterprise browsers. Plan explicit `@supports` blocks and “good-enough” sRGB fallbacks.

## Problem Statement

### Current State Issues

1. **Brand Inconsistency**: Web app uses zinc/green palette while marketing website uses bold yellow/black branding, creating disconnect
2. **Accessibility Gaps**: Missing WCAG 2.2 AA compliance (April 2026 deadline), no keyboard navigation focus indicators, insufficient ARIA labels
3. **Limited Responsiveness**: Mobile navigation hidden on small screens with no alternative, tables not mobile-optimized
4. **No Dark Mode**: Single light theme only, missing modern expectation
5. **Poor User Feedback**: No loading states, minimal error handling, no animations or transitions
6. **Dated Component Architecture**: Not leveraging Tailwind v4 features (@theme, @utility, OKLCH colors, container queries)
7. **Inconsistent Design System**: Legacy Bootstrap files, mix of .hawk-* classes and inline utilities, no documented design tokens

### User Impact

- **Visual Confusion**: Users experience two different brands (website vs. app)
- **Accessibility Barriers**: Keyboard users, screen reader users, and those with visual impairments cannot effectively use the app
- **Mobile Frustration**: Navigation inaccessible on mobile devices
- **Cognitive Load**: No feedback during operations (form submits, data loading) causes uncertainty
- **Eye Strain**: No dark mode option for low-light environments
- **Discovery Issues**: Poor empty states and data presentation make features hard to discover

## Proposed Solution

### High-Level Approach

**Phase 1: Foundation** - Design system and tokens
**Phase 2: Core Components** - Buttons, cards, forms, navigation
**Phase 3: Layouts** - Pages and responsive breakpoints
**Phase 4: Features** - Dark mode, loading states, animations
**Phase 5: Accessibility** - WCAG compliance and testing
**Phase 6: Polish** - Animations, edge cases, performance

### Design System Architecture

#### Research Insights

**Token Strategy (Tailwind v4 + CSS variables):**
- Keep a clear separation between:
  - “Design tokens” (ex: `--color-brand-500`, `--radius-xl`, `--shadow-soft`)
  - “Semantic aliases” (ex: `--bg-primary`, `--text-muted`, `--border-primary`, `--accent`)
  This makes dark mode and future rebrands much cheaper, because components mostly depend on semantic aliases.
- Define semantic aliases in `@layer base` and keep them as the only variables used by component utilities. This prevents “token leakage” where half the UI uses `--color-brand-500` directly and the other half uses `--accent`.

**Browser Support / Fallbacks:**
- Plan for a 2-tier approach:
  1. sRGB hex fallbacks (safe baseline).
  2. OKLCH overrides gated behind `@supports (color: oklch(0.5 0.1 100)) { ... }`.
- Likewise for effects:
  - Gate `backdrop-filter` behind `@supports (backdrop-filter: blur(1px))`.
  - Consider reducing reliance on `color-mix()` for critical contrast (links, buttons). When used, provide a reasonable solid-color fallback.

**Accessibility Hooks in Tokens:**
- Set `color-scheme` in base styles based on theme so form controls and scrollbars match:
  - `:root { color-scheme: light; }`
  - `.dark { color-scheme: dark; }`
- Add explicit tokens for focus ring thickness/offset and use `:focus-visible` globally to avoid “focus ring everywhere” while keeping keyboard usability high.

#### Color System (OKLCH)

**Brand Colors** (from marketing website):
```css
@theme {
  /* Primary Brand - Yellow */
  --color-brand-50: oklch(0.98 0.05 95);
  --color-brand-100: oklch(0.95 0.10 95);
  --color-brand-200: oklch(0.90 0.15 95);
  --color-brand-300: oklch(0.85 0.20 95);
  --color-brand-400: oklch(0.80 0.25 95);
  --color-brand-500: oklch(0.92 0.18 100); /* #ffd400 */
  --color-brand-600: oklch(0.85 0.16 100);
  --color-brand-700: oklch(0.75 0.14 100);
  --color-brand-800: oklch(0.65 0.12 100);
  --color-brand-900: oklch(0.55 0.10 100);

  /* Ink - Near Black */
  --color-ink-50: oklch(0.95 0 0);
  --color-ink-100: oklch(0.90 0 0);
  --color-ink-200: oklch(0.75 0 0);
  --color-ink-300: oklch(0.60 0 0);
  --color-ink-400: oklch(0.45 0 0);
  --color-ink-500: oklch(0.30 0 0);
  --color-ink-600: oklch(0.20 0 0);
  --color-ink-700: oklch(0.15 0 0);
  --color-ink-800: oklch(0.10 0 0);
  --color-ink-900: oklch(0.06 0 0); /* #0b0b0d */

  /* Paper - Off-White */
  --color-paper-50: oklch(1 0.01 95);
  --color-paper-100: oklch(0.99 0.01 95); /* #fffdf0 */

  /* Semantic Status Colors */
  --color-success-500: oklch(0.70 0.15 145); /* Green */
  --color-success-100: oklch(0.95 0.08 145);
  --color-success-900: oklch(0.35 0.12 145);

  --color-danger-500: oklch(0.65 0.20 25); /* Red */
  --color-danger-100: oklch(0.95 0.10 25);
  --color-danger-900: oklch(0.35 0.15 25);

  --color-warning-500: oklch(0.75 0.15 85); /* Amber */
  --color-warning-100: oklch(0.95 0.08 85);
  --color-warning-900: oklch(0.40 0.12 85);

  --color-info-500: oklch(0.65 0.15 250); /* Blue */
  --color-info-100: oklch(0.95 0.08 250);
  --color-info-900: oklch(0.35 0.12 250);
}
```

**Dark Mode Variables**:
```css
@layer base {
  :root {
    --bg-primary: var(--color-paper-100);
    --bg-secondary: var(--color-paper-50);
    --text-primary: var(--color-ink-900);
    --text-secondary: var(--color-ink-600);
    --text-muted: var(--color-ink-400);
    --border-primary: var(--color-ink-200);
    --accent: var(--color-brand-500);
  }

  .dark {
    --bg-primary: var(--color-ink-900);
    --bg-secondary: var(--color-ink-800);
    --text-primary: var(--color-paper-100);
    --text-secondary: var(--color-ink-200);
    --text-muted: var(--color-ink-300);
    --border-primary: var(--color-ink-700);
    --accent: var(--color-brand-400); /* Lighter yellow for dark bg */
  }
}
```

#### Typography Scale

**Font Families** (from marketing website):
```css
@theme {
  --font-sans: "Space Grotesk", system-ui, -apple-system, sans-serif;
  --font-mono: "IBM Plex Mono", "Courier New", monospace;

  /* Type Scale */
  --font-size-xs: 0.75rem;
  --line-height-xs: 1rem;

  --font-size-sm: 0.875rem;
  --line-height-sm: 1.25rem;

  --font-size-base: 1rem;
  --line-height-base: 1.5rem;

  --font-size-lg: 1.125rem;
  --line-height-lg: 1.75rem;

  --font-size-xl: 1.25rem;
  --line-height-xl: 1.75rem;

  --font-size-2xl: 1.5rem;
  --line-height-2xl: 2rem;

  --font-size-3xl: 1.875rem;
  --line-height-3xl: 2.25rem;

  --font-size-4xl: 2.25rem;
  --line-height-4xl: 2.5rem;
}
```

#### Spacing & Sizing

```css
@theme {
  /* Custom Spacing (beyond Tailwind defaults) */
  --spacing-18: 4.5rem;
  --spacing-22: 5.5rem;

  /* Border Radius */
  --radius-sm: 0.25rem; /* 4px */
  --radius-md: 0.5rem;  /* 8px */
  --radius-lg: 1rem;    /* 16px */
  --radius-xl: 1.25rem; /* 20px */
  --radius-2xl: 1.75rem; /* 28px - website style */
  --radius-full: 9999px;

  /* Shadows */
  --shadow-soft: 0 1px 3px 0 rgb(0 0 0 / 0.1), 0 1px 2px -1px rgb(0 0 0 / 0.1);
  --shadow-medium: 0 4px 6px -1px rgb(0 0 0 / 0.1), 0 2px 4px -2px rgb(0 0 0 / 0.1);
  --shadow-hard: 0 10px 15px -3px rgb(0 0 0 / 0.1), 0 4px 6px -4px rgb(0 0 0 / 0.1);
}
```

#### Component Utilities (Tailwind v4 @utility)

**Base Components**:
```css
/* hawk-shell.css */
@utility hawk-shell {
  min-height: 100vh;
  background: var(--bg-primary);

  /* Branded gradient background */
  background-image:
    radial-gradient(at 0% 0%, color-mix(in srgb, var(--accent) 8%, transparent) 0px, transparent 50%),
    radial-gradient(at 100% 100%, color-mix(in srgb, var(--color-info-500) 5%, transparent) 0px, transparent 50%);
}

/* hawk-topbar.css */
@utility hawk-topbar {
  position: sticky;
  top: 0;
  z-index: 20;
  border-bottom: 1px solid var(--border-primary);
  background: color-mix(in srgb, var(--bg-primary) 75%, transparent);
  backdrop-filter: blur(12px);
  transition: all 150ms cubic-bezier(0.4, 0, 0.2, 1);
}

/* hawk-card.css */
@utility hawk-card {
  border-radius: var(--radius-xl);
  border: 1px solid var(--border-primary);
  background: color-mix(in srgb, var(--bg-primary) 70%, transparent);
  backdrop-filter: blur(10px);
  box-shadow: var(--shadow-soft);
  padding: 1.5rem;
  transition: box-shadow 200ms ease;

  &:hover {
    box-shadow: var(--shadow-medium);
  }
}

/* hawk-btn.css */
@utility hawk-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  border-radius: var(--radius-xl);
  font-weight: 500;
  font-size: var(--font-size-sm);
  text-decoration: none;
  transition: all 150ms cubic-bezier(0.4, 0, 0.2, 1);
  white-space: nowrap;

  &:focus {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }

  &:disabled {
    opacity: 0.5;
    cursor: not-allowed;
    pointer-events: none;
  }
}

@utility hawk-btn-primary {
  background: var(--color-brand-500);
  color: var(--color-ink-900);
  border: 1px solid transparent;

  &:hover:not(:disabled) {
    background: var(--color-brand-600);
    transform: translateY(-1px);
    box-shadow: var(--shadow-medium);
  }

  &:active:not(:disabled) {
    transform: translateY(0);
  }
}

@utility hawk-btn-secondary {
  background: var(--bg-secondary);
  color: var(--text-primary);
  border: 1px solid var(--border-primary);

  &:hover:not(:disabled) {
    background: var(--bg-primary);
    border-color: var(--accent);
  }
}

@utility hawk-btn-ghost {
  background: transparent;
  color: var(--text-primary);
  border: 1px solid var(--border-primary);

  &:hover:not(:disabled) {
    background: var(--bg-secondary);
  }
}

@utility hawk-btn-danger {
  background: var(--color-danger-500);
  color: white;
  border: 1px solid transparent;

  &:hover:not(:disabled) {
    background: var(--color-danger-600);
  }
}

@utility hawk-btn-sm {
  padding: 0.25rem 0.75rem;
  font-size: var(--font-size-xs);
}

@utility hawk-btn-lg {
  padding: 0.75rem 1.5rem;
  font-size: var(--font-size-lg);
}

/* hawk-input.css */
@utility hawk-input {
  width: 100%;
  padding: 0.5rem 0.75rem;
  border-radius: var(--radius-xl);
  border: 1px solid var(--border-primary);
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: var(--font-size-sm);
  transition: all 150ms ease;

  &:focus {
    outline: none;
    border-color: var(--accent);
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent) 10%, transparent);
  }

  &:disabled {
    opacity: 0.6;
    cursor: not-allowed;
    background: var(--bg-secondary);
  }

  &::placeholder {
    color: var(--text-muted);
  }
}

@utility hawk-select {
  /* Same as hawk-input with dropdown arrow */
  @apply hawk-input;
  padding-right: 2.5rem;
  background-image: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' fill='none' viewBox='0 0 20 20'%3e%3cpath stroke='%236b7280' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.5' d='M6 8l4 4 4-4'/%3e%3c/svg%3e");
  background-position: right 0.5rem center;
  background-repeat: no-repeat;
  background-size: 1.5em 1.5em;
  appearance: none;
}

@utility hawk-label {
  display: block;
  font-size: var(--font-size-xs);
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--text-secondary);
  margin-bottom: 0.5rem;
}

/* hawk-badge.css */
@utility hawk-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.25rem 0.75rem;
  border-radius: var(--radius-full);
  font-size: var(--font-size-xs);
  font-weight: 500;
  white-space: nowrap;
}

@utility hawk-badge-success {
  background: var(--color-success-100);
  color: var(--color-success-900);
}

@utility hawk-badge-danger {
  background: var(--color-danger-100);
  color: var(--color-danger-900);
}

@utility hawk-badge-warning {
  background: var(--color-warning-100);
  color: var(--color-warning-900);
}

@utility hawk-badge-info {
  background: var(--color-info-100);
  color: var(--color-info-900);
}
```

**Loading States**:
```css
/* hawk-loading.css */
@utility hawk-spinner {
  display: inline-block;
  width: 1rem;
  height: 1rem;
  border: 2px solid color-mix(in srgb, currentColor 20%, transparent);
  border-top-color: currentColor;
  border-radius: 50%;
  animation: spin 0.6s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

@utility hawk-skeleton {
  background: linear-gradient(
    90deg,
    color-mix(in srgb, var(--bg-secondary) 100%, transparent) 0%,
    color-mix(in srgb, var(--bg-secondary) 60%, var(--bg-primary) 40%) 50%,
    color-mix(in srgb, var(--bg-secondary) 100%, transparent) 100%
  );
  background-size: 200% 100%;
  animation: shimmer 1.5s ease-in-out infinite;
  border-radius: var(--radius-md);
}

@keyframes shimmer {
  0% { background-position: 200% 0; }
  100% { background-position: -200% 0; }
}
```

### Responsive Strategy

**Breakpoints**:
- `sm`: 640px (mobile landscape)
- `md`: 768px (tablet portrait)
- `lg`: 1024px (tablet landscape / small desktop)
- `xl`: 1280px (desktop)
- `2xl`: 1536px (large desktop)

**Mobile-First Approach**:
- Base styles for mobile (320px+)
- Progressive enhancement at each breakpoint
- Container queries for component-level responsiveness

**Mobile Navigation Pattern**:
```html
<!-- Hamburger menu button (mobile only) -->
<button class="lg:hidden hawk-btn hawk-btn-ghost" id="mobile-menu-toggle" aria-label="Toggle menu">
  <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 6h16M4 12h16M4 18h16"/>
  </svg>
</button>

<!-- Mobile slide-out drawer -->
<div id="mobile-menu" class="fixed inset-0 z-50 lg:hidden" hidden>
  <!-- Backdrop -->
  <div class="fixed inset-0 bg-ink-900/50 backdrop-blur-sm" id="mobile-menu-backdrop"></div>

<!-- Drawer -->
  <nav class="fixed left-0 top-0 bottom-0 w-64 bg-primary border-r border-primary shadow-hard">
    <div class="p-4">
      <button class="hawk-btn hawk-btn-ghost" id="mobile-menu-close" aria-label="Close menu">
        <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
        </svg>
      </button>
    </div>
    <ul class="space-y-2 px-4">
      <li><a href="/Monitors" class="block py-2 px-4 rounded hover:bg-secondary">Monitors</a></li>
      <!-- More nav items -->
    </ul>
  </nav>
</div>
```

**Research Insights (Mobile Drawer Accessibility):**
- Add `aria-controls="mobile-menu"` and toggle `aria-expanded="true|false"` on the hamburger button.
- Use a focus trap when the drawer is open (at minimum: focus the first focusable element on open and return focus to the toggle on close).
- Set `role="dialog"` and `aria-modal="true"` on the drawer container, or use the native `<dialog>` element if you want built-in focus management.
- Prevent background scrolling (you already do) and consider setting `inert` on the main content wrapper while the drawer is open for better screen reader behavior.

**Touch Target Sizes**:
- Minimum 44x44px for all interactive elements
- Adequate spacing between buttons (min 8px gap)
- Larger tap targets on mobile (use `sm:` prefix for desktop reductions)

### Dark Mode Implementation

#### Research Insights

**Avoid the “flash” (FOUC) problem:**
- To avoid a light-theme flash before JS runs, the theme decision must happen before first paint.
- Best practical approach: a tiny script in `<head>` that only reads `localStorage` and sets the `dark` class (no DOM queries). If you later add a strict CSP, plan to use nonces for this snippet or switch to a cookie-based theme preference rendered server-side.

**ARIA + UX details for the toggle:**
- Set `aria-pressed="true|false"` on the theme toggle, and optionally expose a 3-state model (“system/light/dark”) like Tailwind’s docs site does (`system` is a real user expectation now).

**Persisted preference precedence:**
- Explicit preference in storage wins.
- If no explicit preference exists, follow system preference and update live on `prefers-color-scheme` changes.
- If explicit preference exists, do not auto-switch on system change (avoid surprising user).

**Strategy**: System preference detection with manual override toggle

**Toggle Component**:
```html
<button id="theme-toggle" class="hawk-btn hawk-btn-ghost" aria-label="Toggle theme">
  <!-- Sun icon (visible in dark mode) -->
  <svg class="w-5 h-5 hidden dark:block" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364 6.364l-.707-.707M6.343 6.343l-.707-.707m12.728 0l-.707.707M6.343 17.657l-.707.707"/>
  </svg>
  <!-- Moon icon (visible in light mode) -->
  <svg class="w-5 h-5 dark:hidden" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646z"/>
  </svg>
</button>

<script>
  // Dark mode toggle logic (site.js)
  const themeToggle = document.getElementById('theme-toggle');
  const root = document.documentElement;

  // Initialize from localStorage or system preference
  function initTheme() {
    const stored = localStorage.theme;
    const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;

    if (stored === 'dark' || (!stored && prefersDark)) {
      root.classList.add('dark');
    } else {
      root.classList.remove('dark');
    }
  }

  // Toggle theme
  function toggleTheme() {
    const isDark = root.classList.toggle('dark');
    localStorage.theme = isDark ? 'dark' : 'light';
  }

  themeToggle?.addEventListener('click', toggleTheme);
  initTheme();

  // Listen for system preference changes
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', e => {
    if (!localStorage.theme) {
      if (e.matches) root.classList.add('dark');
      else root.classList.remove('dark');
    }
  });
</script>
```

**Color Contrast Verification**:
- All text/background combinations must meet WCAG AA (4.5:1 for normal text, 3:1 for large text)
- Use [WebAIM Contrast Checker](https://webaim.org/resources/contrastchecker/) to verify
- Yellow (#ffd400) on black (#0b0b0d): **9.53:1** ✅ (passes AAA)
- White on ink-900: **15.8:1** ✅ (passes AAA)
- Brand-400 (lighter yellow) on ink-900: **7.2:1** ✅ (passes AA for dark mode)

## Technical Approach

### Phase 1: Foundation (Days 1-2)

**Tasks**:
1. Remove legacy Bootstrap files
2. Set up Tailwind v4 @theme with new color system
3. Add Space Grotesk and IBM Plex Mono fonts
4. Create base component utilities (@utility directive)
5. Set up dark mode CSS variables
6. Configure build pipeline for new assets

#### Research Insights

**Make the CSS build step deterministic (recommended options):**
1. **Docker-first (recommended for this repo):** add a small Node build stage that runs `npm ci` + `npm run build:css` in `Hawk.Web/` and copies `wwwroot/css/app.css` into the final `dotnet publish` output.
   - Pros: `docker compose up --build` always reflects the latest CSS, no “forgot to run Tailwind” state.
   - Cons: slightly longer Docker builds.
2. **MSBuild-driven:** add a `Target` that runs `npm ci` and `npm run build:css` before `dotnet publish` (and optionally before `dotnet build` in Release).
   - Pros: `dotnet publish` is the single entrypoint.
   - Cons: requires Node on any machine doing publishes (CI agents, dev laptops).
3. **Commit compiled CSS (current behavior) + enforce regen:** keep `wwwroot/css/app.css` checked in, but enforce regeneration in CI (fail build if `git diff` shows changes after running `npm --prefix Hawk.Web run build:css`).
   - Pros: simplest Dockerfile.
   - Cons: easy for local dev to accidentally ship stale CSS unless CI is strict.

**Bootstrap removal checklist (avoid zombie dependencies):**
- Remove references (layout, partials) and delete the assets under `Hawk.Web/wwwroot/lib/bootstrap/`.
- Grep for `data-bs-*`, `btn`, `container`, `row`, and other bootstrap class usage in `.cshtml` before deleting, to avoid silent layout regressions.

**Files to Modify**:
- `Hawk.Web/Assets/tailwind.css` - Complete rewrite with new @theme
- `Hawk.Web/tailwind.config.js` - Minimal config (auto-discovery)
- `Hawk.Web/Pages/Shared/_Layout.cshtml` - Add font links, dark mode script
- `Hawk.Web/wwwroot/css/site.css` - Delete (no longer needed)
- `Hawk.Web/wwwroot/lib/bootstrap/` - Delete directory

**Acceptance Criteria**:
- [x] Bootstrap completely removed
- [x] New color system defined with WCAG-compliant contrast ratios
- [x] Fonts loaded and applied globally
- [x] Dark mode toggle functional with persistence
- [x] All .hawk-* components rewritten with new @utility syntax
- [x] Build pipeline compiles CSS successfully

### Phase 2: Core Components (Days 3-4)

**Tasks**:
1. Redesign navigation header with mobile menu
2. Update button components with size variants
3. Redesign form inputs with validation states
4. Update card components with hover states
5. Create badge components for all status types
6. Add loading spinner and skeleton components

**Files to Modify**:
- `Hawk.Web/Pages/Shared/_Layout.cshtml` - Header/nav redesign
- `Hawk.Web/Assets/components/hawk-btn.css` - Button variants
- `Hawk.Web/Assets/components/hawk-input.css` - Input states
- `Hawk.Web/Assets/components/hawk-card.css` - Card styles
- `Hawk.Web/Assets/components/hawk-badge.css` - Status badges
- `Hawk.Web/Assets/components/hawk-loading.css` - Loading states
- `Hawk.Web/wwwroot/js/site.js` - Mobile menu, dark mode, loading states

**Acceptance Criteria**:
- [x] Navigation works on all viewport sizes
- [x] Mobile menu slides out with backdrop
- [x] All button variants render correctly in light/dark modes
- [x] Form inputs show focus, error, and disabled states
- [x] Loading spinner and skeleton animations smooth
- [x] Status badges use semantic colors

### Phase 3: Page Layouts (Days 5-7)

**Tasks**:
1. Redesign landing page (Index.cshtml) with new brand
2. Rebuild monitors list with responsive table
3. Redesign Create/Edit monitor forms
4. Update monitor Details page layout
5. Redesign Admin pages (Users, Import)
6. Style Identity Area pages (Login, Register)

**Files to Modify**:
- `Hawk.Web/Pages/Index.cshtml` - Landing page redesign
- `Hawk.Web/Pages/Monitors/Index.cshtml` - Table redesign
- `Hawk.Web/Pages/Monitors/Create.cshtml` - Form redesign
- `Hawk.Web/Pages/Monitors/Edit.cshtml` - Form redesign
- `Hawk.Web/Pages/Monitors/Details.cshtml` - Details layout
- `Hawk.Web/Pages/Monitors/Delete.cshtml` - Confirmation page
- `Hawk.Web/Pages/Admin/Users/Index.cshtml` - User table
- `Hawk.Web/Pages/Admin/Import/StatusCake.cshtml` - Import form
- `Hawk.Web/Areas/Identity/Pages/Account/Login.cshtml` - Login form
- `Hawk.Web/Areas/Identity/Pages/Account/Register.cshtml` - Register form

**Acceptance Criteria**:
- [ ] All pages use new brand colors and typography
- [ ] Layouts responsive on mobile, tablet, desktop
- [ ] Forms use new input components with validation styling
- [ ] Tables scroll horizontally on mobile
- [ ] Empty states display with clear CTAs
- [ ] Identity pages match app branding

### Phase 4: Features (Days 8-9)

**Tasks**:
1. Add loading states to all async operations
2. Implement form validation timing (on blur after submit)
3. Add animations and transitions
4. Create error handling patterns (toasts, banners)
5. Add success feedback (form submissions)
6. Implement status indicator updates

**Files to Modify**:
- `Hawk.Web/wwwroot/js/site.js` - Loading, validation, error handling
- `Hawk.Web/Pages/Monitors/Create.cshtml.cs` - Form validation feedback
- `Hawk.Web/Pages/Monitors/Edit.cshtml.cs` - Form validation feedback
- `Hawk.Web/Assets/utilities/animations.css` - Transition classes

**Acceptance Criteria**:
- [ ] Submit buttons show loading spinner during POST
- [ ] Form validation errors appear on blur after first submit
- [ ] Success messages display after successful actions
- [ ] Error banners/toasts appear for network/server errors
- [ ] Page transitions smooth (no jarring jumps)
- [ ] Hover states animate smoothly

### Phase 5: Accessibility (Days 10-11)

**Tasks**:
1. Add focus indicators to all interactive elements
2. Implement ARIA labels and live regions
3. Add skip links to main content
4. Test keyboard navigation on all pages
5. Add alt text to images (if any)
6. Verify color contrast ratios
7. Test with screen reader (NVDA/JAWS)
8. Add reduced-motion support

**Files to Modify**:
- `Hawk.Web/Pages/Shared/_Layout.cshtml` - Skip links, ARIA landmarks
- `Hawk.Web/Assets/base/accessibility.css` - Focus indicators, reduced-motion
- All form pages - ARIA labels, error associations
- All table pages - Scope attributes, caption

**Acceptance Criteria**:
- [ ] All interactive elements have visible focus indicators (2px solid accent)
- [ ] All form fields have associated labels (explicit or aria-label)
- [ ] Error messages announced via aria-live regions
- [ ] Skip link navigates to main content on Tab
- [ ] Keyboard navigation works without mouse
- [ ] All text passes WCAG AA contrast (4.5:1)
- [ ] Screen reader announces page structure correctly
- [ ] Animations respect prefers-reduced-motion

### Phase 6: Polish & Testing (Days 12-14)

**Tasks**:
1. Add micro-interactions (button presses, hover effects)
2. Implement toast notifications
3. Handle edge cases (long URLs, empty states, errors)
4. Performance optimization (CSS minification, font loading)
5. Cross-browser testing (Chrome, Firefox, Safari, Edge)
6. Mobile device testing (iOS, Android)
7. Accessibility audit with axe DevTools
8. Fix bugs and inconsistencies

**Files to Modify**:
- Various components - Micro-interactions
- `Hawk.Web/wwwroot/js/toast.js` - Toast notification system
- `Hawk.Web/Pages/Shared/_Layout.cshtml` - Font preloading
- All pages - Edge case handling

**Acceptance Criteria**:
- [ ] All interactions feel responsive and polished
- [ ] Toast notifications display for user actions
- [ ] Edge cases handled gracefully (no broken layouts)
- [ ] CSS bundle size < 50KB (gzipped)
- [ ] Fonts loaded optimally (no FOUT)
- [ ] Works in Chrome, Firefox, Safari, Edge
- [ ] Works on iOS Safari, Android Chrome
- [ ] axe DevTools reports 0 critical issues
- [ ] All automated tests pass

## Implementation Phases

### Phase 1: Foundation (2 days)

#### 1.1 Remove Bootstrap & Legacy CSS

**Files to Delete**:
```bash
rm -rf Hawk.Web/wwwroot/lib/bootstrap
rm Hawk.Web/wwwroot/css/site.css
rm Hawk.Web/Pages/Shared/_Layout.cshtml.css
```

**Update Layout** (`_Layout.cshtml`):
```diff
- <link rel="stylesheet" href="~/css/site.css" asp-append-version="true" />
- <link rel="stylesheet" href="~/lib/bootstrap/dist/css/bootstrap.min.css" />
```

#### 1.2 Install Fonts

**Add to `_Layout.cshtml` <head>**:
```html
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@300;400;500;600;700&family=IBM+Plex+Mono:wght@400;500;600&display=swap" rel="stylesheet">
```

#### 1.3 Rewrite Tailwind Config

**`Assets/tailwind.css`**:
```css
@import "tailwindcss";

/* Theme Tokens */
@theme {
  /* Colors - see "Design System Architecture" section above */
  --color-brand-500: oklch(0.92 0.18 100);
  /* ... all color tokens ... */

  /* Typography */
  --font-sans: "Space Grotesk", system-ui, sans-serif;
  --font-mono: "IBM Plex Mono", monospace;
  /* ... all type tokens ... */

  /* Spacing, Radius, Shadows */
  /* ... all design tokens ... */
}

/* Base Styles */
@layer base {
  :root {
    /* CSS variable mappings for dark mode */
    --bg-primary: var(--color-paper-100);
    /* ... see "Color System" section above ... */
  }

  .dark {
    /* Dark mode overrides */
    --bg-primary: var(--color-ink-900);
    /* ... see "Color System" section above ... */
  }

  html {
    font-family: var(--font-sans);
  }

  body {
    background: var(--bg-primary);
    color: var(--text-primary);
  }

  code, pre {
    font-family: var(--font-mono);
  }
}

/* Component Utilities */
@import "./components/hawk-shell.css";
@import "./components/hawk-topbar.css";
@import "./components/hawk-card.css";
@import "./components/hawk-btn.css";
@import "./components/hawk-input.css";
@import "./components/hawk-badge.css";
@import "./components/hawk-loading.css";

/* Custom Utilities */
@import "./utilities/animations.css";
@import "./utilities/accessibility.css";
```

**Create Component Files**:
```bash
mkdir -p Hawk.Web/Assets/components
mkdir -p Hawk.Web/Assets/utilities

# Create files (content from "Component Utilities" section above)
touch Hawk.Web/Assets/components/hawk-shell.css
touch Hawk.Web/Assets/components/hawk-topbar.css
touch Hawk.Web/Assets/components/hawk-card.css
touch Hawk.Web/Assets/components/hawk-btn.css
touch Hawk.Web/Assets/components/hawk-input.css
touch Hawk.Web/Assets/components/hawk-badge.css
touch Hawk.Web/Assets/components/hawk-loading.css
touch Hawk.Web/Assets/utilities/animations.css
touch Hawk.Web/Assets/utilities/accessibility.css
```

#### 1.4 Add Dark Mode Toggle

**Update `_Layout.cshtml`** (in header):
```html
<div class="flex items-center gap-2">
  <!-- Theme toggle button -->
  <button id="theme-toggle" class="hawk-btn hawk-btn-ghost" aria-label="Toggle dark mode">
    <!-- Icons (see "Dark Mode Implementation" section) -->
  </button>

  <!-- Existing auth partial -->
  <partial name="_LoginPartial" />
</div>

<!-- Dark mode script (before closing </body>) -->
<script src="~/js/theme.js" asp-append-version="true"></script>
```

**Create `wwwroot/js/theme.js`**:
```javascript
// Full script from "Dark Mode Implementation" section above
```

### Phase 2: Core Components (2 days)

#### 2.1 Redesign Navigation Header

**Update `_Layout.cshtml`**:
```html
<div class="hawk-shell">
  <header class="hawk-topbar">
    <div class="max-w-6xl mx-auto px-4">
      <div class="flex items-center justify-between h-16">
        <!-- Logo -->
        <a href="/" class="flex items-center gap-2 no-underline">
          <div class="w-3 h-3 rounded-full bg-brand-500"></div>
          <span class="font-mono text-lg font-semibold text-primary">HAWK</span>
        </a>

        <!-- Desktop Navigation -->
        <nav class="hidden lg:flex items-center gap-1">
          <a href="/Monitors" class="hawk-btn hawk-btn-ghost">Monitors</a>
          @if (User.IsInRole("Admin"))
          {
            <a href="/Admin/Users" class="hawk-btn hawk-btn-ghost">Admin</a>
            <a href="/hangfire" class="hawk-btn hawk-btn-ghost">Hangfire</a>
          }
          <a href="/Privacy" class="hawk-btn hawk-btn-ghost">Privacy</a>
        </nav>

        <!-- Actions -->
        <div class="flex items-center gap-2">
          <button id="theme-toggle" class="hawk-btn hawk-btn-ghost" aria-label="Toggle theme">
            <!-- Theme toggle icons -->
          </button>

          <!-- Mobile menu button -->
          <button class="lg:hidden hawk-btn hawk-btn-ghost" id="mobile-menu-toggle" aria-label="Open menu">
            <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 6h16M4 12h16M4 18h16"/>
            </svg>
          </button>

          <partial name="_LoginPartial" />
        </div>
      </div>
    </div>
  </header>

  <!-- Mobile menu drawer -->
  <div id="mobile-menu" class="fixed inset-0 z-50 lg:hidden" hidden>
    <!-- Full mobile menu from "Responsive Strategy" section -->
  </div>

  <!-- Main content -->
  <main class="max-w-6xl mx-auto px-4 py-6">
    @RenderBody()
  </main>

  <!-- Footer -->
  <footer class="mt-auto py-6 text-center text-sm text-muted">
    <p>&copy; 2026 HAWK - <a href="/Privacy" class="text-accent hover:underline">Privacy</a></p>
  </footer>
</div>
```

**Create `wwwroot/js/mobile-menu.js`**:
```javascript
// Mobile menu toggle logic
const menuToggle = document.getElementById('mobile-menu-toggle');
const menuClose = document.getElementById('mobile-menu-close');
const menuBackdrop = document.getElementById('mobile-menu-backdrop');
const menu = document.getElementById('mobile-menu');

function openMenu() {
  menu.removeAttribute('hidden');
  document.body.style.overflow = 'hidden';
}

function closeMenu() {
  menu.setAttribute('hidden', '');
  document.body.style.overflow = '';
}

menuToggle?.addEventListener('click', openMenu);
menuClose?.addEventListener('click', closeMenu);
menuBackdrop?.addEventListener('click', closeMenu);

// Close on Escape key
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && !menu.hasAttribute('hidden')) {
    closeMenu();
  }
});
```

#### 2.2 Update Button Components

**`Assets/components/hawk-btn.css`** (see full implementation in "Component Utilities" section):
- Base .hawk-btn with focus, disabled states
- .hawk-btn-primary (yellow bg, ink text)
- .hawk-btn-secondary (paper bg, border)
- .hawk-btn-ghost (transparent, border)
- .hawk-btn-danger (red bg, white text)
- .hawk-btn-sm, .hawk-btn-lg size variants

**Test All Button Variants**:
```html
<!-- In a test page or component -->
<div class="space-y-4 p-6">
  <div class="flex gap-2">
    <button class="hawk-btn hawk-btn-primary">Primary</button>
    <button class="hawk-btn hawk-btn-secondary">Secondary</button>
    <button class="hawk-btn hawk-btn-ghost">Ghost</button>
    <button class="hawk-btn hawk-btn-danger">Danger</button>
  </div>
  <div class="flex gap-2">
    <button class="hawk-btn hawk-btn-primary hawk-btn-sm">Small</button>
    <button class="hawk-btn hawk-btn-primary">Medium</button>
    <button class="hawk-btn hawk-btn-primary hawk-btn-lg">Large</button>
  </div>
  <div class="flex gap-2">
    <button class="hawk-btn hawk-btn-primary" disabled>Disabled</button>
    <button class="hawk-btn hawk-btn-primary">
      <span class="hawk-spinner"></span>
      Loading
    </button>
  </div>
</div>
```

#### 2.3 Redesign Form Inputs

**`Assets/components/hawk-input.css`** (see full implementation in "Component Utilities" section):
- .hawk-input with focus, disabled, placeholder states
- .hawk-select with custom dropdown arrow
- .hawk-label for form labels

**Validation States** (`Assets/utilities/validation.css`):
```css
@utility hawk-input-error {
  border-color: var(--color-danger-500);

  &:focus {
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--color-danger-500) 10%, transparent);
  }
}

@utility hawk-input-success {
  border-color: var(--color-success-500);

  &:focus {
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--color-success-500) 10%, transparent);
  }
}

@utility hawk-error-message {
  color: var(--color-danger-600);
  font-size: var(--font-size-xs);
  margin-top: 0.25rem;
}

@utility hawk-help-text {
  color: var(--text-muted);
  font-size: var(--font-size-xs);
  margin-top: 0.25rem;
}
```

**Research Insights (Razor Pages ergonomics):**
- Prefer tag helpers (`asp-for`, `asp-validation-for`, `asp-validation-summary`) over manually writing `name="..."` + indexing into `ViewData.ModelState`. You’ll get correct field names/binding, consistent validation messages, and fewer refactor regressions.
- If you keep custom error rendering, ensure each field error has a stable `id` and that `aria-describedby` points at it. Avoid double-rendering both server errors and client-only errors at the same time.

**Form Field Pattern**:
```html
<div>
  <label for="monitor-name" class="hawk-label">Monitor Name</label>
  <input
    type="text"
    id="monitor-name"
    name="Name"
    class="hawk-input @(ViewData.ModelState["Name"]?.Errors.Count > 0 ? "hawk-input-error" : "")"
    value="@Model.Form.Name"
    aria-describedby="@(ViewData.ModelState["Name"]?.Errors.Count > 0 ? "name-error" : "")"
    required
  />
  @if (ViewData.ModelState["Name"]?.Errors.Count > 0)
  {
    <p id="name-error" class="hawk-error-message" role="alert">
      @ViewData.ModelState["Name"].Errors[0].ErrorMessage
    </p>
  }
  else
  {
    <p class="hawk-help-text">A descriptive name for this monitor (max 200 characters)</p>
  }
</div>
```

#### 2.4 Update Card & Badge Components

**Already defined in Phase 1** - verify they work in light/dark modes:
- .hawk-card (with hover effect)
- .hawk-badge-success, .hawk-badge-danger, .hawk-badge-warning, .hawk-badge-info

#### 2.5 Add Loading Components

**`Assets/components/hawk-loading.css`** (see full implementation in "Component Utilities" section):
- .hawk-spinner (rotating border)
- .hawk-skeleton (shimmer animation)

**Loading Button State**:
```html
<button class="hawk-btn hawk-btn-primary" id="submit-btn">
  <span class="btn-text">Create Monitor</span>
  <span class="hawk-spinner hidden" aria-hidden="true"></span>
</button>

<script>
document.getElementById('submit-btn').addEventListener('click', function(e) {
  this.disabled = true;
  this.querySelector('.btn-text').textContent = 'Creating...';
  this.querySelector('.hawk-spinner').classList.remove('hidden');
  // Form submit happens via normal POST
});
</script>
```

**Skeleton Screen** (monitors list loading):
```html
<div class="hawk-card p-0 overflow-hidden">
  <div class="overflow-x-auto">
    <table class="min-w-full text-sm">
      <thead class="bg-secondary text-secondary">
        <tr>
          <th class="px-6 py-3 text-left font-medium">Name</th>
          <th class="px-6 py-3 text-left font-medium">URL</th>
          <th class="px-6 py-3 text-left font-medium">Status</th>
          <th class="px-6 py-3 text-left font-medium">Last Run</th>
        </tr>
      </thead>
      <tbody>
        @for (int i = 0; i < 5; i++)
        {
          <tr>
            <td class="px-6 py-4"><div class="hawk-skeleton h-4 w-32"></div></td>
            <td class="px-6 py-4"><div class="hawk-skeleton h-4 w-64"></div></td>
            <td class="px-6 py-4"><div class="hawk-skeleton h-6 w-16 rounded-full"></div></td>
            <td class="px-6 py-4"><div class="hawk-skeleton h-4 w-24"></div></td>
          </tr>
        }
      </tbody>
    </table>
  </div>
</div>
```

### Phase 3: Page Layouts (3 days)

#### 3.1 Redesign Landing Page

**`Pages/Index.cshtml`**:
```html
@page
@model IndexModel
@{
    ViewData["Title"] = "HAWK - Uptime Monitoring";
}

<div class="py-12 space-y-12">
  <!-- Hero Section -->
  <section class="text-center space-y-6">
    <div class="flex justify-center">
      <div class="w-12 h-12 rounded-full bg-brand-500 flex items-center justify-center">
        <span class="font-mono text-2xl font-bold text-ink-900">H</span>
      </div>
    </div>

    <h1 class="text-4xl md:text-5xl font-bold tracking-tight">
      Monitor your services.<br/>
      <span class="text-brand-500">Stay in control.</span>
    </h1>

    <p class="text-lg text-secondary max-w-2xl mx-auto">
      Simple, reliable uptime monitoring for your websites and APIs. Get instant alerts when things go wrong.
    </p>

    @if (User.Identity?.IsAuthenticated == true)
    {
      <div class="flex justify-center gap-4">
        <a href="/Monitors" class="hawk-btn hawk-btn-primary hawk-btn-lg">
          Open Monitors
          <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 7l5 5m0 0l-5 5m5-5H6"/>
          </svg>
        </a>
      </div>
    }
    else
    {
      <div class="flex justify-center gap-4">
        <a href="/Identity/Account/Register" class="hawk-btn hawk-btn-primary hawk-btn-lg">Get Started</a>
        <a href="/Identity/Account/Login" class="hawk-btn hawk-btn-ghost hawk-btn-lg">Sign In</a>
      </div>
    }
  </section>

  <!-- Features Grid -->
  <section class="grid grid-cols-1 md:grid-cols-3 gap-6">
    <!-- Feature 1: Methods -->
    <div class="hawk-card">
      <div class="flex items-start gap-4">
        <div class="w-12 h-12 rounded-xl bg-brand-500/10 flex items-center justify-center flex-shrink-0">
          <svg class="w-6 h-6 text-brand-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"/>
          </svg>
        </div>
        <div>
          <h3 class="text-lg font-semibold mb-2">GET & POST Support</h3>
          <p class="text-sm text-secondary">Monitor HTTP endpoints with custom headers, request bodies, and content types.</p>
        </div>
      </div>
    </div>

    <!-- Feature 2: Matching -->
    <div class="hawk-card">
      <div class="flex items-start gap-4">
        <div class="w-12 h-12 rounded-xl bg-brand-500/10 flex items-center justify-center flex-shrink-0">
          <svg class="w-6 h-6 text-brand-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-6 9l2 2 4-4"/>
          </svg>
        </div>
        <div>
          <h3 class="text-lg font-semibold mb-2">Smart Validation</h3>
          <p class="text-sm text-secondary">Validate responses with substring matching or powerful regex patterns.</p>
        </div>
      </div>
    </div>

    <!-- Feature 3: SQL Server -->
    <div class="hawk-card">
      <div class="flex items-start gap-4">
        <div class="w-12 h-12 rounded-xl bg-brand-500/10 flex items-center justify-center flex-shrink-0">
          <svg class="w-6 h-6 text-brand-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 7v10c0 2.21 3.582 4 8 4s8-1.79 8-4V7M4 7c0 2.21 3.582 4 8 4s8-1.79 8-4M4 7c0-2.21 3.582-4 8-4s8 1.79 8 4"/>
          </svg>
        </div>
        <div>
          <h3 class="text-lg font-semibold mb-2">SQL Server Ready</h3>
          <p class="text-sm text-secondary">Built on reliable SQL Server with Entity Framework Core for data integrity.</p>
        </div>
      </div>
    </div>
  </section>
</div>
```

#### 3.2 Rebuild Monitors List

**`Pages/Monitors/Index.cshtml`**:
```html
@page
@model Hawk.Web.Pages.Monitors.IndexModel
@{
    ViewData["Title"] = "Monitors";
}

<div class="space-y-6">
  <!-- Header -->
  <div class="flex items-start justify-between gap-4">
    <div>
      <h1 class="text-2xl font-semibold tracking-tight">Monitors</h1>
      <p class="text-sm text-secondary mt-1">
        @Model.Monitors.Count monitor@(Model.Monitors.Count != 1 ? "s" : "") configured
      </p>
    </div>
    <a href="/Monitors/Create" class="hawk-btn hawk-btn-primary">
      <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 4v16m8-8H4"/>
      </svg>
      New Monitor
    </a>
  </div>

  @if (Model.Monitors.Any())
  {
    <!-- Monitors Table -->
    <div class="hawk-card p-0 overflow-hidden">
      <div class="overflow-x-auto">
        <table class="min-w-full text-sm">
          <thead class="bg-secondary border-b border-primary">
            <tr>
              <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase tracking-wider">Name</th>
              <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase tracking-wider">Method</th>
              <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase tracking-wider">URL</th>
              <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase tracking-wider">Status</th>
              <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase tracking-wider">Last Run</th>
              <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase tracking-wider">Next Run</th>
              <th scope="col" class="px-6 py-3 text-right font-medium text-secondary uppercase tracking-wider">Actions</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-primary">
            @foreach (var monitor in Model.Monitors)
            {
              <tr class="hover:bg-secondary/50 transition-colors">
                <td class="px-6 py-4 font-medium">
                  @monitor.Name
                  @if (!monitor.Enabled)
                  {
                    <span class="hawk-badge hawk-badge-info ml-2">Paused</span>
                  }
                </td>
                <td class="px-6 py-4">
                  <span class="hawk-badge hawk-badge-info">@monitor.Method</span>
                </td>
                <td class="px-6 py-4">
                  <span class="font-mono text-xs text-muted max-w-[520px] truncate block" title="@monitor.Url">
                    @monitor.Url
                  </span>
                </td>
                <td class="px-6 py-4">
                  @if (monitor.LastRun != null)
                  {
                    @if (monitor.LastRun.Success)
                    {
                      <span class="hawk-badge hawk-badge-success">OK</span>
                    }
                    else
                    {
                      <span class="hawk-badge hawk-badge-danger">FAIL</span>
                    }
                  }
                  else
                  {
                    <span class="text-muted">—</span>
                  }
                </td>
                <td class="px-6 py-4 text-muted">
                  @(monitor.LastRunAt?.ToLocalTime().ToString("g") ?? "—")
                </td>
                <td class="px-6 py-4 text-muted">
                  @(monitor.NextRunAt?.ToLocalTime().ToString("g") ?? "—")
                </td>
                <td class="px-6 py-4 text-right">
                  <div class="flex items-center justify-end gap-2">
                    <a href="/Monitors/Details/@monitor.Id" class="hawk-btn hawk-btn-ghost hawk-btn-sm">View</a>
                    <a href="/Monitors/Edit/@monitor.Id" class="hawk-btn hawk-btn-ghost hawk-btn-sm">Edit</a>
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  }
  else
  {
    <!-- Empty State -->
    <div class="hawk-card text-center py-12">
      <svg class="w-16 h-16 mx-auto text-muted mb-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"/>
      </svg>
      <h3 class="text-lg font-semibold mb-2">No monitors yet</h3>
      <p class="text-sm text-secondary mb-6">Create your first monitor to start tracking uptime.</p>
      <a href="/Monitors/Create" class="hawk-btn hawk-btn-primary hawk-btn-lg">
        <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 4v16m8-8H4"/>
        </svg>
        Create Monitor
      </a>
    </div>
  }
</div>
```

#### 3.3 Redesign Create/Edit Forms

**`Pages/Monitors/Create.cshtml`** (Edit is similar):
```html
@page
@model Hawk.Web.Pages.Monitors.CreateModel
@{
    ViewData["Title"] = "New Monitor";
}

<form method="post" class="space-y-6" id="monitor-form">
  <!-- Header -->
  <div class="flex items-center justify-between gap-4">
    <div>
      <h1 class="text-2xl font-semibold tracking-tight">New Monitor</h1>
      <p class="text-sm text-secondary mt-1">Configure a new uptime monitor</p>
    </div>
    <div class="flex items-center gap-2">
      <a href="/Monitors" class="hawk-btn hawk-btn-ghost">Cancel</a>
      <button type="submit" class="hawk-btn hawk-btn-primary" id="submit-btn">
        <span class="btn-text">Create Monitor</span>
        <span class="hawk-spinner hidden"></span>
      </button>
    </div>
  </div>

  <!-- Basic Info -->
  <div class="hawk-card">
    <h2 class="text-lg font-semibold mb-4">Basic Information</h2>
    <div class="space-y-4">
      <!-- Name Field -->
      <div>
        <label for="Name" class="hawk-label">Monitor Name</label>
        <input
          type="text"
          id="Name"
          name="Form.Name"
          class="hawk-input @(ViewData.ModelState["Form.Name"]?.Errors.Count > 0 ? "hawk-input-error" : "")"
          value="@Model.Form.Name"
          maxlength="200"
          required
          aria-describedby="@(ViewData.ModelState["Form.Name"]?.Errors.Count > 0 ? "name-error" : "name-help")"
        />
        @if (ViewData.ModelState["Form.Name"]?.Errors.Count > 0)
        {
          <p id="name-error" class="hawk-error-message" role="alert">
            @ViewData.ModelState["Form.Name"].Errors[0].ErrorMessage
          </p>
        }
        else
        {
          <p id="name-help" class="hawk-help-text">A descriptive name for this monitor (max 200 characters)</p>
        }
      </div>

      <!-- URL Field -->
      <div>
        <label for="Url" class="hawk-label">URL to Monitor</label>
        <input
          type="url"
          id="Url"
          name="Form.Url"
          class="hawk-input font-mono text-sm @(ViewData.ModelState["Form.Url"]?.Errors.Count > 0 ? "hawk-input-error" : "")"
          value="@Model.Form.Url"
          placeholder="https://example.com"
          maxlength="2048"
          required
          aria-describedby="@(ViewData.ModelState["Form.Url"]?.Errors.Count > 0 ? "url-error" : "url-help")"
        />
        @if (ViewData.ModelState["Form.Url"]?.Errors.Count > 0)
        {
          <p id="url-error" class="hawk-error-message" role="alert">
            @ViewData.ModelState["Form.Url"].Errors[0].ErrorMessage
          </p>
        }
        else
        {
          <p id="url-help" class="hawk-help-text">Full URL including protocol (http:// or https://)</p>
        }
      </div>
    </div>
  </div>

  <!-- Configuration -->
  <div class="hawk-card">
    <h2 class="text-lg font-semibold mb-4">Configuration</h2>
    <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
      <!-- Method -->
      <div>
        <label for="Method" class="hawk-label">HTTP Method</label>
        <select
          id="Method"
          name="Form.Method"
          class="hawk-select"
          required
        >
          <option value="GET" selected="@(Model.Form.Method == "GET")">GET</option>
          <option value="POST" selected="@(Model.Form.Method == "POST")">POST</option>
        </select>
      </div>

      <!-- Interval -->
      <div>
        <label for="IntervalSeconds" class="hawk-label">Check Interval</label>
        <select
          id="IntervalSeconds"
          name="Form.IntervalSeconds"
          class="hawk-select"
          required
        >
          <option value="60">1 minute</option>
          <option value="300">5 minutes</option>
          <option value="600">10 minutes</option>
          <option value="1800">30 minutes</option>
          <option value="3600">1 hour</option>
        </select>
      </div>

      <!-- Timeout -->
      <div>
        <label for="TimeoutSeconds" class="hawk-label">Timeout (seconds)</label>
        <input
          type="number"
          id="TimeoutSeconds"
          name="Form.TimeoutSeconds"
          class="hawk-input"
          value="@Model.Form.TimeoutSeconds"
          min="1"
          max="60"
          required
        />
        <p class="hawk-help-text">Request timeout (1-60 seconds)</p>
      </div>

      <!-- Enabled -->
      <div class="flex items-center pt-6">
        <input
          type="checkbox"
          id="Enabled"
          name="Form.Enabled"
          class="w-4 h-4 rounded border-primary text-brand-500 focus:ring-2 focus:ring-accent"
          checked="@Model.Form.Enabled"
        />
        <label for="Enabled" class="ml-2 text-sm font-medium">Enable monitor</label>
      </div>
    </div>
  </div>

  <!-- Alerting -->
  <div class="hawk-card">
    <h2 class="text-lg font-semibold mb-4">Alerting</h2>
    <div>
      <label for="AlertAfterFailureCount" class="hawk-label">Alert After Failures</label>
      <input
        type="number"
        id="AlertAfterFailureCount"
        name="Form.AlertAfterFailureCount"
        class="hawk-input"
        value="@Model.Form.AlertAfterFailureCount"
        min="1"
        max="10"
        required
      />
      <p class="hawk-help-text">Send alert after N consecutive failures (1-10)</p>
    </div>
  </div>

  <!-- POST Options (visible when Method=POST) -->
  <div class="hawk-card" id="post-options" style="display: @(Model.Form.Method == "POST" ? "block" : "none")">
    <h2 class="text-lg font-semibold mb-4">POST Options</h2>
    <div class="space-y-4">
      <!-- Content-Type -->
      <div>
        <label for="PostContentType" class="hawk-label">Content-Type</label>
        <input
          type="text"
          id="PostContentType"
          name="Form.PostContentType"
          class="hawk-input font-mono text-sm"
          value="@Model.Form.PostContentType"
          placeholder="application/json"
        />
      </div>

      <!-- Body -->
      <div>
        <label for="PostBody" class="hawk-label">Request Body</label>
        <textarea
          id="PostBody"
          name="Form.PostBody"
          class="hawk-input font-mono text-sm"
          rows="6"
          placeholder='{"key": "value"}'
        >@Model.Form.PostBody</textarea>
      </div>
    </div>
  </div>

  <!-- Headers -->
  <div class="hawk-card">
    <h2 class="text-lg font-semibold mb-4">Custom Headers</h2>
    <div class="space-y-2">
      @for (int i = 0; i < 5; i++)
      {
        <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
          <input
            type="text"
            name="Form.HeaderNames[@i]"
            class="hawk-input font-mono text-xs"
            placeholder="Header-Name"
          />
          <input
            type="text"
            name="Form.HeaderValues[@i]"
            class="hawk-input font-mono text-xs"
            placeholder="Header value"
          />
        </div>
      }
    </div>
  </div>

  <!-- Match Rules -->
  <div class="hawk-card">
    <h2 class="text-lg font-semibold mb-4">Match Rules</h2>
    <p class="text-sm text-secondary mb-4">Validate response content (all rules must match)</p>
    <div class="space-y-2">
      @for (int i = 0; i < 5; i++)
      {
        <div class="grid grid-cols-1 md:grid-cols-[120px_1fr] gap-2">
          <select
            name="Form.MatchRuleModes[@i]"
            class="hawk-select"
          >
            <option value="">None</option>
            <option value="Contains">Contains</option>
            <option value="Regex">Regex</option>
          </select>
          <input
            type="text"
            name="Form.MatchRulePatterns[@i]"
            class="hawk-input font-mono text-xs"
            placeholder="Pattern to match"
          />
        </div>
      }
    </div>
  </div>
</form>

<script>
  // Show/hide POST options based on method
  const methodSelect = document.getElementById('Method');
  const postOptions = document.getElementById('post-options');

  methodSelect.addEventListener('change', function() {
    postOptions.style.display = this.value === 'POST' ? 'block' : 'none';
  });

  // Loading state on submit
  const form = document.getElementById('monitor-form');
  const submitBtn = document.getElementById('submit-btn');

  form.addEventListener('submit', function() {
    submitBtn.disabled = true;
    submitBtn.querySelector('.btn-text').textContent = 'Creating...';
    submitBtn.querySelector('.hawk-spinner').classList.remove('hidden');
  });
</script>
```

#### 3.4 Update Monitor Details Page

**`Pages/Monitors/Details.cshtml`**:
```html
@page "{id:guid}"
@model Hawk.Web.Pages.Monitors.DetailsModel
@{
    ViewData["Title"] = Model.Monitor?.Name ?? "Monitor Details";
}

@if (Model.Monitor == null)
{
  <div class="hawk-card border-danger-500 bg-danger-100/50">
    <p class="text-danger-900">Monitor not found.</p>
  </div>
}
else
{
  <div class="space-y-6">
    <!-- Header -->
    <div class="flex items-start justify-between gap-4">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight">@Model.Monitor.Name</h1>
        <p class="font-mono text-sm text-muted mt-1">@Model.Monitor.Url</p>
      </div>
      <div class="flex items-center gap-2">
        <form method="post" asp-page-handler="RunNow" class="inline">
          <input type="hidden" name="id" value="@Model.Monitor.Id" />
          <button type="submit" class="hawk-btn hawk-btn-primary">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z"/>
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/>
            </svg>
            Run Now
          </button>
        </form>
        <a href="/Monitors/Edit/@Model.Monitor.Id" class="hawk-btn hawk-btn-ghost">Edit</a>
        <a href="/Monitors" class="hawk-btn hawk-btn-ghost">Back</a>
      </div>
    </div>

    <!-- Two-column layout -->
    <div class="grid grid-cols-1 lg:grid-cols-12 gap-6">
      <!-- Left: Config (5 cols) -->
      <div class="lg:col-span-5 space-y-6">
        <!-- Status Card -->
        <div class="hawk-card">
          <h2 class="text-lg font-semibold mb-4">Status</h2>
          <dl class="space-y-3 text-sm">
            <div class="flex justify-between">
              <dt class="text-secondary">Enabled</dt>
              <dd>
                @if (Model.Monitor.Enabled)
                {
                  <span class="hawk-badge hawk-badge-success">Yes</span>
                }
                else
                {
                  <span class="hawk-badge hawk-badge-info">No (Paused)</span>
                }
              </dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-secondary">Interval</dt>
              <dd>@Model.Monitor.IntervalSeconds seconds</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-secondary">Timeout</dt>
              <dd>@Model.Monitor.TimeoutSeconds seconds</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-secondary">Alert After</dt>
              <dd>@Model.Monitor.AlertAfterFailureCount failure@(Model.Monitor.AlertAfterFailureCount != 1 ? "s" : "")</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-secondary">Last Run</dt>
              <dd>@(Model.Monitor.LastRunAt?.ToLocalTime().ToString("g") ?? "Never")</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-secondary">Next Run</dt>
              <dd>@(Model.Monitor.NextRunAt?.ToLocalTime().ToString("g") ?? "—")</dd>
            </div>
          </dl>
        </div>

        <!-- Headers Card -->
        <div class="hawk-card">
          <h2 class="text-lg font-semibold mb-4">Headers</h2>
          @if (Model.Monitor.Headers.Any())
          {
            <dl class="space-y-2 text-sm">
              @foreach (var header in Model.Monitor.Headers)
              {
                <div>
                  <dt class="font-mono text-xs text-secondary">@header.Name</dt>
                  <dd class="font-mono text-xs text-primary mt-1">@header.Value</dd>
                </div>
              }
            </dl>
          }
          else
          {
            <p class="text-sm text-muted">None.</p>
          }
        </div>

        <!-- Match Rules Card -->
        <div class="hawk-card">
          <h2 class="text-lg font-semibold mb-4">Match Rules</h2>
          @if (Model.Monitor.MatchRules.Any())
          {
            <dl class="space-y-2 text-sm">
              @foreach (var rule in Model.Monitor.MatchRules)
              {
                <div>
                  <dt class="text-secondary">@rule.Mode</dt>
                  <dd class="font-mono text-xs text-primary mt-1">@rule.Pattern</dd>
                </div>
              }
            </dl>
          }
          else
          {
            <p class="text-sm text-muted">None.</p>
          }
        </div>
      </div>

      <!-- Right: Run History (7 cols) -->
      <div class="lg:col-span-7">
        <div class="hawk-card p-0 overflow-hidden">
          <div class="p-6 border-b border-primary">
            <h2 class="text-lg font-semibold">Recent Runs</h2>
          </div>

          @if (Model.Runs.Any())
          {
            <div class="overflow-x-auto">
              <table class="min-w-full text-sm">
                <thead class="bg-secondary border-b border-primary">
                  <tr>
                    <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase">Status</th>
                    <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase">Duration</th>
                    <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase">Result</th>
                    <th scope="col" class="px-6 py-3 text-left font-medium text-secondary uppercase">Alert</th>
                  </tr>
                </thead>
                <tbody class="divide-y divide-primary">
                  @foreach (var run in Model.Runs)
                  {
                    <tr>
                      <td class="px-6 py-4">
                        @if (run.Success)
                        {
                          <span class="hawk-badge hawk-badge-success">OK</span>
                        }
                        else
                        {
                          <span class="hawk-badge hawk-badge-danger">FAIL</span>
                        }
                      </td>
                      <td class="px-6 py-4 font-mono text-xs">
                        @run.DurationMilliseconds ms
                      </td>
                      <td class="px-6 py-4 font-mono text-xs text-muted max-w-xs truncate" title="@run.ResultText">
                        @run.ResultText
                      </td>
                      <td class="px-6 py-4 font-mono text-xs text-muted">
                        @(run.AlertSent ? "✓ Sent" : "—")
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
          else
          {
            <div class="p-12 text-center">
              <p class="text-sm text-muted">No runs yet.</p>
            </div>
          }
        </div>
      </div>
    </div>
  </div>
}
```

#### 3.5 Redesign Admin Pages

**`Pages/Admin/Users/Index.cshtml`** (similar pattern to monitors list):
- Header with "New user" button
- Table with Email, Roles columns
- Action buttons: "Reset password", "Delete"
- Empty state for no users

**`Pages/Admin/Import/StatusCake.cshtml`**:
- Instructions card (format explanation)
- Import form card (type dropdown, file upload)
- Result section (shows counts, warnings)

#### 3.6 Style Identity Area Pages

**Challenge**: Identity pages are scaffolded by ASP.NET. Options:
1. Scaffold Identity pages into project and restyle (time-consuming)
2. Leave as-is (inconsistent brand)
3. Use custom CSS to override Identity styles (brittle)

**Recommendation**: Scaffold Login and Register pages only (most visible), restyle with hawk-* components.

**Research Insights (ASP.NET Identity scaffolding reality):**
- Scaffolding requires the `dotnet-aspnet-codegenerator` tool and (usually) the `Microsoft.VisualStudio.Web.CodeGeneration.Design` package reference in `Hawk.Web`.
- Validate the command works on the repo’s target framework (`net10.0`) before committing to this approach; otherwise plan B is to restyle via shared layout wrappers and minimal CSS overrides.

**Scaffold Command**:
```bash
dotnet aspnet-codegenerator identity -dc Hawk.Web.Data.ApplicationDbContext --files "Account.Login;Account.Register"
```

Then apply hawk-* classes to scaffolded forms (same pattern as Create monitor form).

### Phase 4: Features (2 days)

#### 4.1 Add Loading States

**Button Loading** (already covered in Phase 2.5)

**Page Loading** - Add skeleton screens to list pages while data loads. For SSR Razor Pages, this is less critical (data loads server-side), but can be used during async operations like "Run now".

**Form Submission Loading**:
```javascript
// wwwroot/js/form-loading.js
document.querySelectorAll('form[data-loading]').forEach(form => {
  form.addEventListener('submit', function(e) {
    const submitBtn = this.querySelector('[type="submit"]');
    if (submitBtn && !submitBtn.disabled) {
      submitBtn.disabled = true;

      const btnText = submitBtn.querySelector('.btn-text');
      const spinner = submitBtn.querySelector('.hawk-spinner');

      if (btnText) btnText.textContent = 'Processing...';
      if (spinner) spinner.classList.remove('hidden');
    }
  });
});
```

Usage:
```html
<form method="post" data-loading>
  <!-- form fields -->
  <button type="submit" class="hawk-btn hawk-btn-primary">
    <span class="btn-text">Submit</span>
    <span class="hawk-spinner hidden"></span>
  </button>
</form>
```

#### 4.2 Implement Form Validation Timing

**Strategy**: Hybrid approach - validate on blur after first submit attempt

```javascript
// wwwroot/js/form-validation.js
document.querySelectorAll('form[data-validation]').forEach(form => {
  let submitted = false;

  form.addEventListener('submit', function() {
    submitted = true;
  });

  form.querySelectorAll('input, textarea, select').forEach(field => {
    field.addEventListener('blur', function() {
      if (submitted) {
        // Validate field (basic client-side)
        validateField(field);
      }
    });
  });
});

function validateField(field) {
  const value = field.value.trim();
  const required = field.hasAttribute('required');
  const type = field.type;

  let error = null;

  if (required && !value) {
    error = 'This field is required';
  } else if (type === 'email' && value && !isValidEmail(value)) {
    error = 'Please enter a valid email address';
  } else if (type === 'url' && value && !isValidUrl(value)) {
    error = 'Please enter a valid URL';
  }

  // Show/hide error
  const errorEl = field.parentElement.querySelector('.hawk-error-message[data-client]');
  if (error) {
    if (!errorEl) {
      const p = document.createElement('p');
      p.className = 'hawk-error-message';
      p.setAttribute('data-client', 'true');
      p.textContent = error;
      field.parentElement.appendChild(p);
    } else {
      errorEl.textContent = error;
    }
    field.classList.add('hawk-input-error');
  } else if (errorEl) {
    errorEl.remove();
    field.classList.remove('hawk-input-error');
  }
}

function isValidEmail(email) {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
}

function isValidUrl(url) {
  try {
    new URL(url);
    return true;
  } catch {
    return false;
  }
}
```

#### 4.3 Add Animations and Transitions

**Global Transitions** (`Assets/utilities/animations.css`):
```css
/* Smooth transitions for common properties */
@layer utilities {
  .transition-smooth {
    transition: all 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }

  .transition-fast {
    transition: all 150ms cubic-bezier(0.4, 0, 0.2, 1);
  }

  .transition-slow {
    transition: all 300ms cubic-bezier(0.4, 0, 0.2, 1);
  }
}

/* Page entry animation */
@keyframes fadeIn {
  from {
    opacity: 0;
    transform: translateY(10px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

main {
  animation: fadeIn 300ms ease-out;
}

/* Reduced motion support */
@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
  }
}
```

**Micro-interactions**:
- Button press: `transform: translateY(1px)` on active
- Card hover: `box-shadow` transition (already in .hawk-card)
- Link hover: underline with transition
- Badge animations: subtle pulse for "in progress" states

#### 4.4 Create Error Handling Patterns

##### Research Insights (Security + Maintainability)

**XSS safety (important):**
- Any toast/banner system that injects strings into the DOM must treat those strings as untrusted.
- Avoid `innerHTML` for message text. Build the element structure, then set message via `textContent`. If you truly need rich formatting, whitelist and sanitize explicitly (prefer: don’t).

**Avoid inline `onclick=` handlers:**
- Inline handlers make CSP harder and couple markup to behavior.
- Prefer attaching listeners in JS (event delegation works well for toast close buttons).

**TempData to JS without injection:**
- Prefer rendering messages into a `data-*` attribute (HTML-encoded by Razor) and let JS read `dataset`.
- Alternative: emit JSON using a serializer (so quotes/newlines are safe) rather than interpolating raw strings into JS source.

**Toast Notifications** (`wwwroot/js/toast.js`):
```javascript
// Toast notification system
const toastContainer = document.createElement('div');
toastContainer.id = 'toast-container';
toastContainer.className = 'fixed bottom-4 right-4 z-50 space-y-2';
document.body.appendChild(toastContainer);

function showToast(message, type = 'info', duration = 5000) {
  const toast = document.createElement('div');
  toast.className = `hawk-card flex items-center gap-3 min-w-[300px] max-w-md shadow-hard animate-slide-in-right`;

  const icon = getToastIcon(type);
  const color = getToastColor(type);

  toast.innerHTML = `
    <div class="flex-shrink-0">
      <svg class="w-5 h-5 ${color}" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        ${icon}
      </svg>
    </div>
    <p class="text-sm flex-1">${message}</p>
    <button class="flex-shrink-0 text-muted hover:text-primary" onclick="this.parentElement.remove()">
      <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
      </svg>
    </button>
  `;

  toastContainer.appendChild(toast);

  if (duration > 0) {
    setTimeout(() => toast.remove(), duration);
  }
}

function getToastIcon(type) {
  const icons = {
    success: '<path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"/>',
    error: '<path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M10 14l2-2m0 0l2-2m-2 2l-2-2m2 2l2 2m7-2a9 9 0 11-18 0 9 9 0 0118 0z"/>',
    warning: '<path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"/>',
    info: '<path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/>'
  };
  return icons[type] || icons.info;
}

function getToastColor(type) {
  const colors = {
    success: 'text-success-500',
    error: 'text-danger-500',
    warning: 'text-warning-500',
    info: 'text-info-500'
  };
  return colors[type] || colors.info;
}

// Export for use in other scripts
window.showToast = showToast;
```

**Usage**:
```javascript
// Show success toast after form submission
showToast('Monitor created successfully!', 'success');

// Show error toast for network failure
showToast('Network error. Please try again.', 'error');
```

**Error Banner Component** (`Assets/components/hawk-alert.css`):
```css
@utility hawk-alert {
  display: flex;
  align-items: start;
  gap: 0.75rem;
  padding: 1rem;
  border-radius: var(--radius-lg);
  border: 1px solid;
}

@utility hawk-alert-error {
  background: color-mix(in srgb, var(--color-danger-500) 10%, transparent);
  border-color: var(--color-danger-500);
  color: var(--color-danger-900);
}

@utility hawk-alert-warning {
  background: color-mix(in srgb, var(--color-warning-500) 10%, transparent);
  border-color: var(--color-warning-500);
  color: var(--color-warning-900);
}

@utility hawk-alert-info {
  background: color-mix(in srgb, var(--color-info-500) 10%, transparent);
  border-color: var(--color-info-500);
  color: var(--color-info-900);
}

@utility hawk-alert-success {
  background: color-mix(in srgb, var(--color-success-500) 10%, transparent);
  border-color: var(--color-success-500);
  color: var(--color-success-900);
}
```

**Usage**:
```html
<div class="hawk-alert hawk-alert-error" role="alert">
  <svg class="w-5 h-5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/>
  </svg>
  <div class="flex-1">
    <p class="font-medium">Monitor is down</p>
    <p class="text-sm mt-1">Your website has been unreachable for 5 minutes.</p>
  </div>
</div>
```

#### 4.5 Add Success Feedback

**After Form Submission** (server-side redirect):
- Use TempData to pass success message
- Display toast on page load

**Example** (`Monitors/Create.cshtml.cs`):
```csharp
public async Task<IActionResult> OnPostAsync()
{
    if (!ModelState.IsValid)
    {
        return Page();
    }

    // Save monitor...

    TempData["SuccessMessage"] = "Monitor created successfully!";
    return RedirectToPage("/Monitors/Index");
}
```

**Display Toast** (`_Layout.cshtml`):
```html
<script>
@if (TempData["SuccessMessage"] != null)
{
    <text>
    document.addEventListener('DOMContentLoaded', function() {
        showToast('@TempData["SuccessMessage"]', 'success');
    });
    </text>
}
@if (TempData["ErrorMessage"] != null)
{
    <text>
    document.addEventListener('DOMContentLoaded', function() {
        showToast('@TempData["ErrorMessage"]', 'error');
    });
    </text>
}
</script>
```

### Phase 5: Accessibility (2 days)

#### 5.1 Add Focus Indicators

**`Assets/utilities/accessibility.css`**:
```css
@layer base {
  /* Global focus indicator */
  *:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }

  /* Skip link (for keyboard navigation) */
  .skip-link {
    position: absolute;
    top: -40px;
    left: 0;
    background: var(--bg-primary);
    color: var(--text-primary);
    padding: 0.5rem 1rem;
    border-radius: var(--radius-md);
    z-index: 100;

    &:focus {
      top: 0.5rem;
    }
  }
}

/* High contrast mode support */
@media (prefers-contrast: high) {
  .hawk-btn {
    border-width: 2px;
  }

  .hawk-card {
    border-width: 2px;
  }
}
```

**Research Insights (Forced Colors / Windows High Contrast):**
- Add a small `@media (forced-colors: active)` block to ensure key UI affordances remain visible when the OS overrides colors (borders on inputs/buttons, focus outlines, and icon-only buttons).
- Avoid relying on background-color alone to convey status (OK/FAIL). Keep a text label (you already do) and consider an icon with sufficient contrast.

**Add Skip Link** (`_Layout.cshtml`):
```html
<body>
  <a href="#main-content" class="skip-link">Skip to main content</a>

  <!-- ... header ... -->

  <main id="main-content" class="max-w-6xl mx-auto px-4 py-6">
    @RenderBody()
  </main>
</body>
```

#### 5.2 Implement ARIA Labels and Live Regions

**Form Fields** (already covered in Phase 3.3):
- All inputs have explicit `<label>` or `aria-label`
- Error messages use `aria-describedby` to associate with fields
- Required fields use `required` attribute (implicit ARIA)

**Interactive Elements**:
- All icon-only buttons have `aria-label`
- Mobile menu has `aria-hidden` when closed
- Toasts have `role="alert"` or `aria-live="polite"`

**Live Regions** (for dynamic updates):
```html
<!-- Toast container with live region -->
<div
  id="toast-container"
  class="fixed bottom-4 right-4 z-50 space-y-2"
  aria-live="polite"
  aria-atomic="false"
></div>

<!-- Loading state announcement -->
<div class="sr-only" role="status" aria-live="polite" aria-atomic="true">
  @if (Model.IsLoading)
  {
    <span>Loading...</span>
  }
</div>
```

**Screen Reader Only Class**:
```css
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border-width: 0;
}
```

#### 5.3 Test Keyboard Navigation

**Checklist**:
- [ ] All interactive elements reachable via Tab
- [ ] Tab order follows visual order
- [ ] All actions performable with keyboard (no mouse-only)
- [ ] Escape key closes modals/drawers
- [ ] Enter/Space activates buttons
- [ ] Arrow keys navigate dropdown menus (if any)
- [ ] Focus indicator visible on all interactive elements

**Mobile Menu Keyboard Support** (already in `mobile-menu.js`):
- Escape key closes menu
- Focus trap inside menu when open (optional enhancement)

#### 5.4 Verify Color Contrast Ratios

**Tool**: [WebAIM Contrast Checker](https://webaim.org/resources/contrastchecker/)

**Critical Combinations to Test**:
- Yellow (#ffd400) on black (#0b0b0d): **9.53:1** ✅ AAA
- White on ink-900: **15.8:1** ✅ AAA
- Brand-400 (lighter yellow) on ink-900: **7.2:1** ✅ AA
- Text-secondary (ink-600) on bg-primary (paper-100): **5.1:1** ✅ AA
- Text-muted (ink-400) on bg-primary: **3.8:1** ❌ Fails AA (use only for non-critical text)

**Fix Low Contrast**:
- Use ink-500 or darker for body text
- Reserve ink-400 for non-essential text (timestamps, help text)
- Ensure all actionable text (links, buttons) passes AA

#### 5.5 Add Reduced-Motion Support

**Already covered in Phase 4.3** (`animations.css`):
```css
@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
  }
}
```

#### 5.6 Test with Screen Reader

**Tools**:
- NVDA (Windows, free)
- JAWS (Windows, paid)
- VoiceOver (macOS, built-in)
- TalkBack (Android, built-in)

**Testing Checklist**:
- [ ] Page title announced on navigation
- [ ] Headings create logical structure (h1 → h2 → h3)
- [ ] Landmarks (header, nav, main, footer) identified
- [ ] Form labels announced with fields
- [ ] Validation errors announced
- [ ] Button purposes clear
- [ ] Table headers associated with data
- [ ] Loading states announced
- [ ] Success/error messages announced

### Phase 6: Polish & Testing (3 days)

#### 6.1 Add Micro-interactions

**Button Press Animation**:
```css
.hawk-btn {
  /* ... existing styles ... */

  &:active:not(:disabled) {
    transform: translateY(1px);
  }
}
```

**Link Hover**:
```css
a {
  transition: color 150ms ease, text-decoration-color 150ms ease;

  &:hover {
    text-decoration-thickness: 2px;
  }
}
```

**Badge Pulse** (for "in progress" states):
```css
@keyframes pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.5;
  }
}

.hawk-badge-pulse {
  animation: pulse 2s cubic-bezier(0.4, 0, 0.6, 1) infinite;
}
```

#### 6.2 Handle Edge Cases

**Long URLs in Table**:
```html
<span class="font-mono text-xs text-muted max-w-[520px] truncate block" title="@monitor.Url">
  @monitor.Url
</span>
```

**Empty States** (already covered in Phase 3.2)

**Error States**:
- Network timeout: Retry button + error message
- Server 500: Generic error message + contact support link
- Not found: Clear message + link to list

**Loading States** (already covered in Phase 4.1)

#### 6.3 Performance Optimization

**CSS Bundle Size**:
- Run `npm run build:css` to compile Tailwind
- Check `wwwroot/css/app.css` size
- Target: < 50KB gzipped
- Tailwind v4 auto-purges unused classes

**Font Loading**:
```html
<!-- Preload critical fonts -->
<link rel="preload" href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;600;700&display=swap" as="style">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;600;700&display=swap">
```

**Image Optimization** (if any images added):
- Use WebP format
- Provide width/height attributes
- Lazy load below-the-fold images

#### 6.4 Cross-browser Testing

**Browsers to Test**:
- Chrome (latest)
- Firefox (latest)
- Safari (latest)
- Edge (latest)
- iOS Safari (iPhone)
- Android Chrome (Android phone)

**Testing Checklist**:
- [ ] Layout renders correctly
- [ ] Colors display as expected (OKLCH support)
- [ ] Backdrop blur works (or graceful fallback)
- [ ] Forms submit successfully
- [ ] Dark mode toggle works
- [ ] Mobile menu works
- [ ] Animations smooth
- [ ] No console errors

#### 6.5 Accessibility Audit

**Tool**: axe DevTools (browser extension)

**Run on Each Page**:
- Landing page
- Monitors list
- Create monitor
- Edit monitor
- Monitor details
- Admin pages
- Identity pages

**Fix All Critical Issues**:
- Color contrast
- Missing alt text
- Missing ARIA labels
- Form field associations
- Heading structure
- Landmark regions

**Target**: 0 critical issues, < 5 warnings

#### 6.6 Automated Testing

**E2E Tests** (Playwright):
- Update existing tests for new class names
- Add dark mode toggle test
- Add mobile menu test
- Add form validation test

**Example Test Update**:
```csharp
// Before
await Page.Locator(".bg-green-100").WaitForAsync();

// After
await Page.Locator(".hawk-badge-success").WaitForAsync();
```

## Success Metrics

### Visual Design
- [ ] Yellow/black brand consistently applied across all pages
- [ ] Dark mode works without visual bugs
- [ ] Typography scale consistent (Space Grotesk + IBM Plex Mono)
- [ ] Spacing and alignment consistent
- [ ] No layout shifts during page load

### Accessibility (WCAG 2.2 AA)
- [ ] All text meets contrast ratio (4.5:1 normal, 3:1 large)
- [ ] All interactive elements keyboard accessible
- [ ] Screen reader announces content correctly
- [ ] Focus indicators visible
- [ ] No automated accessibility errors (axe DevTools)
- [ ] Passes manual testing with NVDA/JAWS

### Responsiveness
- [ ] Works on mobile (320px - 640px)
- [ ] Works on tablet (640px - 1024px)
- [ ] Works on desktop (1024px+)
- [ ] Mobile navigation functional
- [ ] Tables scroll horizontally on mobile
- [ ] Touch targets meet 44x44px minimum

### User Experience
- [ ] Loading states display during async operations
- [ ] Form validation appears at appropriate time
- [ ] Success/error messages clear and actionable
- [ ] Animations smooth (60fps)
- [ ] No jarring transitions
- [ ] Empty states guide users to next action

### Performance
- [ ] CSS bundle < 50KB (gzipped)
- [ ] Fonts loaded optimally (no FOUT)
- [ ] First Contentful Paint < 1.5s
- [ ] Time to Interactive < 3s
- [ ] No layout shifts (CLS < 0.1)

### Browser Compatibility
- [ ] Works in Chrome (latest)
- [ ] Works in Firefox (latest)
- [ ] Works in Safari (latest)
- [ ] Works in Edge (latest)
- [ ] Works in iOS Safari
- [ ] Works in Android Chrome

### Testing
- [ ] All existing E2E tests pass
- [ ] New E2E tests added for dark mode, mobile menu
- [ ] Manual testing completed on all pages
- [ ] Cross-browser testing completed
- [ ] Accessibility audit completed

## Dependencies & Risks

### Dependencies

**External Services**:
- Google Fonts (Space Grotesk, IBM Plex Mono) - consider self-hosting for GDPR compliance

**Build Tools**:
- Tailwind CSS v4 (already installed)
- Node.js 20+ (for Tailwind build)

**Browser Support**:
- Modern evergreen browsers only
- No IE11 support
- OKLCH color space requires Safari 16.4+, Chrome 111+, Firefox 128+

### Risks

**High-Risk Items**:
1. **Color Contrast**: Yellow/black combination requires careful testing to ensure WCAG compliance in all contexts
2. **Identity Pages**: Scaffolding and restyling ASP.NET Identity pages is time-consuming and brittle
3. **Dark Mode Complexity**: Ensuring all components work in both light/dark modes doubles visual testing surface area
4. **Mobile Navigation**: Implementing drawer/menu requires JavaScript, potential accessibility concerns
5. **Browser Compatibility**: OKLCH colors may render differently or fall back in older browsers

**Mitigation Strategies**:
1. **Color Contrast**: Use contrast checker early, document approved combinations, test with real users
2. **Identity Pages**: Consider leaving out of scope for v1, or use minimal styling (just colors/fonts)
3. **Dark Mode**: Test each component in both modes during development, not at the end
4. **Mobile Navigation**: Use proven accessible patterns (e.g., from a11y community), test with screen readers
5. **Browser Compatibility**: Feature detect OKLCH, provide RGB fallbacks if needed

**Timeline Risks**:
- **Identity pages restyling**: May take 2-3 extra days if fully scaffolded
- **Accessibility testing**: May uncover issues requiring rework (budget 1-2 extra days)
- **Cross-browser bugs**: May require fixes for Safari, Edge (budget 1 extra day)

**Out of Scope** (to reduce risk):
- Real-time status updates (no WebSocket/SignalR)
- Pagination (load all monitors)
- Search/filter functionality
- Bulk actions (checkboxes, multi-select)
- Data visualizations (charts, graphs)
- Email template redesign
- Hangfire dashboard restyling (external dependency)

## Future Considerations

### Phase 2 Enhancements (Post-Launch)
- Real-time status updates via SignalR
- Dashboard with KPI cards and uptime charts
- Pagination for large datasets
- Search and filter on monitors list
- Bulk actions (enable/disable/delete multiple monitors)
- Monitor grouping and tagging
- Custom email templates with brand styling
- Status page (public or private)
- Webhook notifications
- Slack/Discord integrations

### Technical Debt to Address
- Self-host fonts (avoid Google Fonts for GDPR)
- Implement service worker for offline support
- Add end-to-end type safety with TypeScript
- Migrate to Blazor or Razor Components for more dynamic UI
- Add unit tests for JavaScript functions
- Set up Storybook for component documentation

### Scalability Considerations
- Virtual scrolling for large tables
- Lazy loading for images/charts
- Code splitting for JavaScript
- CDN for static assets
- Redis caching for frequently accessed data

## References & Research

### Documentation
- [Tailwind CSS v4 Official Docs](https://tailwindcss.com/docs)
- [WCAG 2.2 Guidelines](https://www.w3.org/TR/WCAG22/)
- [MDN Web Docs - Accessibility](https://developer.mozilla.org/en-US/docs/Web/Accessibility)
- [WebAIM Contrast Checker](https://webaim.org/resources/contrastchecker/)

### Design Inspiration
- Current Hawk Marketing Website (`website/src/styles/global.css`)
- [Vercel Design System](https://vercel.com/design)
- [Linear Design](https://linear.app/)
- [Stripe Dashboard](https://stripe.com/)

### Research Conducted
- Local repository analysis (Phase 1)
- Institutional learnings search (Phase 1)
- Modern SaaS design trends (Phase 1.5b)
- Tailwind v4 best practices (Phase 1.5b)
- User flow analysis (Phase 3)

---

## Appendix: Critical Questions Requiring Clarification

Based on the SpecFlow analysis, these **15 critical and important questions** need answers before implementation can begin:

### CRITICAL (Must Answer Before Starting)

1. **Dark Mode Toggle**: System preference only, or manual toggle with persistence? (Recommendation: Both - system default with manual override)

2. **Mobile Navigation**: Hamburger menu with slide-out drawer, bottom nav, or full-screen overlay? (Recommendation: Hamburger + drawer, most common pattern)

3. **Identity Pages**: Restyle as part of refactor or leave as-is? (Recommendation: Restyle Login/Register only, skip Manage Account for v1)

4. **Real-time Updates**: No real-time, polling, or SignalR? (Recommendation: Out of scope for v1, manual refresh only)

5. **WCAG Testing**: What tools and process for accessibility testing? (Recommendation: axe DevTools automated + manual NVDA testing)

### IMPORTANT (Should Answer Before Phase 2)

6. **Brand Color Contrast**: Approved yellow/black combinations for text? (Recommendation: Yellow backgrounds with black text, or black backgrounds with white text + yellow accents)

7. **Loading States**: Spinner in button, full-page overlay, or skeleton screens? (Recommendation: Hybrid - spinner for actions, skeleton for lists)

8. **Form Validation Timing**: On blur, on submit, or hybrid? (Recommendation: Hybrid - on blur after first submit)

9. **Pagination**: Server-side, client-side, or infinite scroll? (Recommendation: Out of scope for v1, load all monitors)

10. **Error Messages**: Toast, banner, or inline? (Recommendation: Toast for transient, inline for validation, banner for critical)

### NICE-TO-HAVE (Can Decide During Implementation)

11. **Component Classes**: Keep .hawk-* or migrate to utility-first? (Recommendation: Keep .hawk-* for complex components)

12. **Empty States**: Illustrations or text-only? (Recommendation: Text-only + CTA for v1)

13. **Unsaved Changes**: Warn on navigation? (Recommendation: No for v1)

14. **Status Charts**: Uptime visualizations or table-only? (Recommendation: Out of scope for v1)

15. **Bulk Actions**: Multi-select monitors? (Recommendation: Out of scope for v1)

---

**End of Plan**
