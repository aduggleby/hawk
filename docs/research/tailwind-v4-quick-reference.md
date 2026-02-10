---
title: Tailwind CSS v4 Quick Reference for Hawk
date: 2026-02-09
---

# Tailwind CSS v4 Quick Reference

**Status**: ✅ Tailwind v4 is actively maintained (not deprecated). Latest: v4.1 (2026)

## Key Changes from v3 to v4

### Import Statement
```css
/* v3 */
@tailwind base;
@tailwind components;
@tailwind utilities;

/* v4 */
@import "tailwindcss";
```

### Configuration
```css
/* v3: tailwind.config.js */
module.exports = {
  theme: {
    extend: {
      colors: { brand: '#ffd400' }
    }
  }
}

/* v4: CSS @theme directive */
@import "tailwindcss";

@theme {
  --color-brand-500: oklch(0.92 0.18 100);
}
```

### Renamed Utilities
- `shadow-sm` → `shadow-xs`
- `shadow` → `shadow-sm`
- `blur-sm` → `blur-xs`
- `rounded-sm` → `rounded-xs`
- `ring` → `ring-3`
- `outline-none` → `outline-hidden`

### Important Modifier
```html
<!-- v3 -->
<div class="!flex !bg-red-500">

<!-- v4 -->
<div class="flex! bg-red-500!">
```

### CSS Variables in Arbitrary Values
```html
<!-- v3 -->
<div class="bg-[--brand-color]">

<!-- v4 -->
<div class="bg-(--brand-color)">
```

## @theme Directive

### Basic Syntax
```css
@import "tailwindcss";

@theme {
  /* Colors - generates bg-*, text-*, border-* utilities */
  --color-brand-500: oklch(0.92 0.18 100);

  /* Typography - generates font-* utilities */
  --font-sans: "Space Grotesk", sans-serif;
  --text-xl: 1.25rem;
  --leading-xl: 1.75rem;

  /* Spacing - generates p-*, m-*, gap-* utilities */
  --spacing-18: 4.5rem;

  /* Radius - generates rounded-* utilities */
  --radius-2xl: 1.75rem;

  /* Shadows - generates shadow-* utilities */
  --shadow-soft: 0 1px 3px 0 rgb(0 0 0 / 0.1);
}
```

### Remove Default Colors (Optional)
```css
@theme {
  --color-*: initial;  /* Reset all default colors */
  --color-brand: oklch(0.92 0.18 100);
  --color-ink: oklch(0.06 0 0);
}
```

## @utility Directive

### Static Utilities
```css
@utility scrollbar-hidden {
  &::-webkit-scrollbar {
    display: none;
  }
  scrollbar-width: none;
}
```

### Functional Utilities
```css
/* Accepts integer values: tab-4, tab-8 */
@utility tab-* {
  tab-size: --value(integer);
}

/* Uses theme values: tab-github */
@theme {
  --tab-size-github: 8;
}

@utility tab-* {
  tab-size: --value(--tab-size-*);
}
```

### Hawk Button Component
```css
@utility hawk-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  border-radius: var(--radius-sm);
  font-weight: var(--font-weight-medium);
  transition: all 150ms ease-out;

  &:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
}

@utility hawk-btn-primary {
  background: var(--accent);
  color: var(--accent-text);

  &:hover:not(:disabled) {
    background: var(--accent-hover);
  }
}
```

## OKLCH Colors

### Syntax
```css
oklch(L C H)
```
- **L** (Lightness): 0-1 (0=black, 1=white)
- **C** (Chroma): 0-0.4 typical (saturation)
- **H** (Hue): 0-360 degrees

### Examples
```css
--color-brand-500: oklch(0.92 0.18 100);  /* Bright yellow */
--color-ink-900: oklch(0.06 0 0);         /* Near black */
--color-success-500: oklch(0.70 0.15 145); /* Green */
```

### Why OKLCH?
- Perceptually uniform (equal lightness = equal brightness)
- Predictable contrast ratios
- Better gradients (no muddy transitions)
- Wider color gamut (P3 support)

### Tools
- [tailwindcolor.com](https://tailwindcolor.com/) - OKLCH palette generator
- [66colorful.com/tools/tailwind-scale-generator](https://66colorful.com/tools/tailwind-scale-generator) - Generate 50-900 scales
- [inclusivecolors.com](https://www.inclusivecolors.com/) - WCAG-compliant palettes

## Container Queries

### Basic Usage
```html
<div class="@container">
  <div class="text-sm @sm:text-base @lg:text-xl">
    Responsive to container, not viewport
  </div>
</div>
```

### Named Containers
```html
<div class="@container/sidebar">
  <div class="@container/main">
    <div class="@lg/sidebar:hidden @2xl/main:grid-cols-3">
      Target specific containers
    </div>
  </div>
</div>
```

### Max-Width Queries
```html
<div class="@container">
  <div class="@max-sm:flex-col @max-md:text-sm">
    Applies BELOW breakpoint
  </div>
</div>
```

### Custom Container Sizes
```css
@theme {
  --container-card: 400px;
  --container-widget: 600px;
}
```

Usage: `@card:p-6`, `@widget:grid-cols-2`

## Dark Mode

### Class-Based Toggle
```css
@import "tailwindcss";
@custom-variant dark (&:where(.dark, .dark *));
```

```javascript
// Toggle dark mode
document.documentElement.classList.toggle('dark');
localStorage.setItem('theme', isDark ? 'dark' : 'light');
```

### Semantic Color Variables
```css
@layer base {
  :root {
    --bg-primary: var(--color-paper-100);
    --text-primary: var(--color-ink-900);
    --accent: var(--color-brand-500);
  }

  .dark {
    --bg-primary: var(--color-ink-900);
    --text-primary: var(--color-paper-100);
    --accent: var(--color-brand-400);  /* Lighter for dark bg */
  }
}
```

```html
<div class="bg-(--bg-primary) text-(--text-primary)">
  Auto-adapts to dark mode
</div>
```

### FOUC Prevention
```html
<head>
  <!-- BEFORE CSS link -->
  <script>
    (function() {
      const theme = localStorage.getItem('theme');
      if (theme === 'dark' || (!theme && window.matchMedia('(prefers-color-scheme: dark)').matches)) {
        document.documentElement.classList.add('dark');
      }
    })();
  </script>

  <link rel="stylesheet" href="~/css/hawk.css" />
</head>
```

## Accessibility (WCAG 2.2 AA)

### Focus Visible
```css
@layer base {
  *:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
}
```

```html
<button class="hawk-btn focus-visible:ring-3 focus-visible:ring-brand-500">
  Accessible button
</button>
```

### ARIA Labels
```html
<!-- Icon-only button -->
<button aria-label="Toggle dark mode" class="hawk-btn">
  <svg aria-hidden="true"><!-- Icon --></svg>
</button>

<!-- Form input -->
<label for="url" class="sr-only">Monitor URL</label>
<input id="url" type="url" aria-required="true" />

<!-- Loading state -->
<div aria-live="polite" aria-busy="true">
  <span class="hawk-spinner" aria-hidden="true"></span>
  <span class="sr-only">Loading...</span>
</div>

<!-- Alert -->
<div role="alert" aria-live="assertive">
  Monitor created successfully
</div>
```

### Screen Reader Only
```css
@utility sr-only {
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

### Contrast Requirements
- Normal text: 4.5:1 minimum (WCAG AA)
- Large text (18pt+): 3:1 minimum
- UI components: 3:1 minimum

Test with: [webaim.org/resources/contrastchecker](https://webaim.org/resources/contrastchecker/)

## Build Optimization

### Performance
- Full builds: 5x faster than v3
- Incremental builds: 100x faster (microseconds)
- Package size: 35% smaller
- CSS output: Typically <10KB gzipped

### Vite Config (Recommended)
```javascript
// vite.config.js
import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [tailwindcss()],
});
```

### PostCSS Config
```javascript
// postcss.config.mjs
export default {
  plugins: {
    '@tailwindcss/postcss': {},
  },
};
```

### Production Build
```bash
NODE_ENV=production npx @tailwindcss/cli -i input.css -o output.css --minify
```

### Bundle Size Tips
1. Remove unused color namespaces: `--color-*: initial;`
2. Use `@utility` instead of `@apply` chains
3. Use semantic variables instead of full palettes
4. Enable CSS minification
5. Use Brotli/Gzip compression on server

## Migration Checklist

- [ ] Update dependencies to `@tailwindcss/*` v4 packages
- [ ] Replace `@tailwind` directives with `@import "tailwindcss"`
- [ ] Convert `tailwind.config.js` to CSS `@theme`
- [ ] Update PostCSS config
- [ ] Rename deprecated utilities (shadow-sm → shadow-xs)
- [ ] Move important modifier to end (class! not !class)
- [ ] Update CSS variables syntax ([--var] → (--var))
- [ ] Test all responsive breakpoints
- [ ] Test dark mode toggle
- [ ] Run accessibility audit
- [ ] Verify CSS bundle size

## Hawk-Specific Patterns

### Color Palette
```css
@theme {
  --color-brand-500: oklch(0.92 0.18 100);   /* #ffd400 yellow */
  --color-ink-900: oklch(0.06 0 0);          /* #0b0b0d black */
  --color-paper-100: oklch(0.99 0.01 95);    /* #fffdf0 off-white */

  --color-success-500: oklch(0.70 0.15 145);
  --color-danger-500: oklch(0.65 0.20 25);
  --color-warning-500: oklch(0.75 0.15 85);
  --color-info-500: oklch(0.65 0.15 250);
}
```

### Component Naming
Prefix all custom utilities with `hawk-`:
- `hawk-shell` - Layout shell
- `hawk-btn` - Buttons
- `hawk-card` - Cards
- `hawk-badge` - Status badges
- `hawk-spinner` - Loading spinner
- `hawk-skeleton` - Skeleton loading

### File Structure
```
Hawk.Web/Assets/
├── hawk-theme.css          # @theme config
├── components/
│   ├── hawk-shell.css
│   ├── hawk-btn.css
│   ├── hawk-card.css
│   └── hawk-loading.css
└── utilities/
    ├── animations.css
    └── accessibility.css
```

## Common Patterns

### Responsive Card Grid
```html
<div class="@container">
  <div class="grid gap-4 @sm:grid-cols-2 @lg:grid-cols-3">
    <div class="hawk-card">Card 1</div>
    <div class="hawk-card">Card 2</div>
  </div>
</div>
```

### Loading Button
```html
<button
  class="hawk-btn hawk-btn-primary"
  x-data="{ loading: false }"
  :disabled="loading"
>
  <span x-show="!loading">Submit</span>
  <span x-show="loading" class="flex items-center gap-2">
    <span class="hawk-spinner"></span>
    Submitting...
  </span>
</button>
```

### Empty State
```html
<div class="hawk-card text-center py-12">
  <svg class="w-16 h-16 mx-auto text-muted"><!-- Icon --></svg>
  <h3 class="mt-4 text-xl font-semibold text-primary">No data yet</h3>
  <p class="mt-2 text-secondary">Get started by creating your first item.</p>
  <button class="mt-6 hawk-btn hawk-btn-primary">Create Item</button>
</div>
```

### Form Input with Error
```html
<div>
  <label for="url" class="block text-sm font-medium text-secondary">URL</label>
  <input
    id="url"
    type="url"
    class="mt-1 block w-full rounded-sm border px-3 py-2
           border-(--border-primary)
           focus:border-(--accent) focus:ring-3 focus:ring-(--accent)/30
           aria-[invalid=true]:border-danger-500"
    aria-invalid="false"
    aria-describedby="url-error"
  />
  <p id="url-error" class="mt-2 text-sm text-danger-500" role="alert" hidden>
    Please enter a valid URL
  </p>
</div>
```

## Resources

### Official Docs
- [tailwindcss.com/docs](https://tailwindcss.com/docs)
- [Upgrade Guide](https://tailwindcss.com/docs/upgrade-guide)
- [Blog: v4.0 Release](https://tailwindcss.com/blog/tailwindcss-v4)

### Tools
- [OKLCH Color Picker](https://oklch.com/)
- [Tailwind Colors v4](https://tailwindcolor.com/)
- [Inclusive Colors](https://www.inclusivecolors.com/)
- [WebAIM Contrast Checker](https://webaim.org/resources/contrastchecker/)

### Detailed Reference
See: `/home/alex/Source/hawk/docs/research/tailwind-v4-comprehensive-documentation.md`

---

**Last Updated**: 2026-02-09
**Version**: 1.0
