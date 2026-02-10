# Modern UI Design Best Practices (2026)

Research compiled: 2026-02-09

This document provides actionable recommendations for implementing modern SaaS UI design with Tailwind CSS v4, focusing on accessibility, dark mode, responsive design, and enhanced user experience patterns.

---

## 1. Tailwind CSS v4 Design Systems and Component Architecture

### CSS-First Architecture

**Source**: [Tailwind CSS Official Documentation](https://tailwindcss.com/docs/theme), [FrontendTools Best Practices](https://www.frontendtools.tech/blog/tailwind-css-best-practices-design-system-patterns)

Tailwind v4 introduces a CSS-first architecture that replaces JavaScript configuration files with native CSS theme variables, providing better performance and easier theme sharing.

#### Using @theme Directive

The `@theme` directive is the foundation of Tailwind v4 design systems. It creates CSS custom properties AND generates corresponding utility classes.

**Key Rule**: Use `@theme` for design tokens that need utility classes; use `:root` only for variables without utilities.

```css
@import "tailwindcss";

@theme {
  /* Primary Brand - Yellow */
  --color-brand-50: oklch(0.98 0.05 95);
  --color-brand-100: oklch(0.95 0.10 95);
  --color-brand-500: oklch(0.92 0.18 100); /* #ffd400 */
  --color-brand-900: oklch(0.55 0.10 100);

  /* Ink - Near Black */
  --color-ink-50: oklch(0.95 0 0);
  --color-ink-900: oklch(0.06 0 0); /* #0b0b0d */

  /* Paper - Off-White */
  --color-paper-50: oklch(1 0.01 95);
  --color-paper-100: oklch(0.99 0.01 95); /* #fffdf0 */

  /* Semantic Status Colors */
  --color-success-500: oklch(0.70 0.15 145);
  --color-danger-500: oklch(0.65 0.20 25);
  --color-warning-500: oklch(0.75 0.15 85);
}
```

#### Theme Variable Namespaces

| Namespace | Generated Utilities | Example |
|-----------|-------------------|---------|
| `--color-*` | Color utilities | `bg-brand-500`, `text-ink-900` |
| `--font-*` | Font family utilities | `font-sans`, `font-serif` |
| `--text-*` | Font size utilities | `text-xl`, `text-2xl` |
| `--spacing-*` | Spacing/sizing utilities | `px-4`, `max-h-16` |
| `--radius-*` | Border radius utilities | `rounded-sm`, `rounded-lg` |
| `--shadow-*` | Box shadow utilities | `shadow-md`, `shadow-lg` |
| `--breakpoint-*` | Responsive variants | `sm:*`, `md:*` |
| `--animate-*` | Animation utilities | `animate-spin`, `animate-bounce` |

#### Using @utility Directive

Create custom utilities that automatically integrate into Tailwind's utility layer:

```css
@utility container-card {
  container-type: inline-size;
  container-name: card;
}

@utility focus-brand {
  outline: 2px solid oklch(0.92 0.18 100);
  outline-offset: 2px;
}
```

### Component Architecture Patterns

**Source**: [DEV Community - Production Design System](https://dev.to/saswatapal/building-a-production-design-system-with-tailwind-css-v4-1d9e), [DEV Community - Oxide Engine](https://dev.to/dataformathub/tailwind-css-v4-deep-dive-why-the-oxide-engine-changes-everything-in-2026-2595)

#### Compound Components Pattern

For complex components, use the compound component pattern for flexibility and composability:

```javascript
// Hero component with sub-components
export function Hero({ children, className }) {
  return (
    <section className={cn("relative py-16 px-4", className)}>
      {children}
    </section>
  );
}

Hero.Title = function HeroTitle({ children, className }) {
  return (
    <h1 className={cn("text-4xl font-bold text-ink-900", className)}>
      {children}
    </h1>
  );
};

Hero.Description = function HeroDescription({ children, className }) {
  return (
    <p className={cn("mt-4 text-lg text-ink-700", className)}>
      {children}
    </p>
  );
};
```

#### Feature-Based Folder Structure

```
src/
├── components/
│   ├── ui/              # Base UI components (Button, Card, Input)
│   ├── forms/           # Form-specific components
│   ├── layouts/         # Layout components
│   └── dashboard/       # Dashboard-specific components
├── styles/
│   ├── theme.css        # @theme definitions
│   ├── utilities.css    # @utility definitions
│   └── components.css   # @layer components
└── lib/
    └── utils.ts         # cn() helper and utilities
```

#### Performance Improvements

- **Full builds**: Up to 5x faster
- **Incremental builds**: Over 100x faster (measured in microseconds)
- **Native cascade layers**: More explicit control over specificity

---

## 2. Dark Mode Implementation with CSS Variables

**Source**: [Tailwind CSS Dark Mode](https://tailwindcss.com/docs/dark-mode), [Magic Patterns Blog](https://www.magicpatterns.com/blog/implementing-dark-mode), [618Media Guide](https://618media.com/en/blog/dark-mode-with-css-a-comprehensive-guide/)

### CSS Variables Structure

Define all colors as CSS variables to enable easy theme switching:

```css
@theme {
  /* Light mode colors (default) */
  --color-background: var(--color-paper-100);
  --color-foreground: var(--color-ink-900);
  --color-muted: var(--color-ink-300);
  --color-border: var(--color-ink-200);
}

/* Dark mode colors */
[data-theme="dark"] {
  --color-background: var(--color-ink-900);
  --color-foreground: var(--color-paper-100);
  --color-muted: var(--color-ink-700);
  --color-border: var(--color-ink-800);
}
```

### Theme Switching Implementation

#### HTML Setup

Use a `data-theme` attribute on the root element:

```html
<html data-theme="light">
  <!-- or data-theme="dark" or data-theme="auto" -->
</html>
```

#### JavaScript Theme Toggle

```javascript
// theme-toggle.js
function initTheme() {
  // Check localStorage first
  const storedTheme = localStorage.getItem('theme');

  if (storedTheme === 'dark' || storedTheme === 'light') {
    setTheme(storedTheme);
  } else {
    // Check system preference
    const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    setTheme(prefersDark ? 'dark' : 'light');
  }
}

function setTheme(theme) {
  document.documentElement.setAttribute('data-theme', theme);
  localStorage.setItem('theme', theme);
}

function toggleTheme() {
  const current = document.documentElement.getAttribute('data-theme');
  const next = current === 'dark' ? 'light' : 'dark';
  setTheme(next);
}

// Initialize on page load
initTheme();

// Listen for system preference changes
window.matchMedia('(prefers-color-scheme: dark)')
  .addEventListener('change', (e) => {
    if (!localStorage.getItem('theme')) {
      setTheme(e.matches ? 'dark' : 'light');
    }
  });
```

#### Three-Way Theme Toggle Component

Support light, dark, and system preferences:

```javascript
const THEMES = ['light', 'dark', 'auto'];

function ThemeToggle() {
  const [theme, setTheme] = useState('auto');

  useEffect(() => {
    const stored = localStorage.getItem('theme') || 'auto';
    setTheme(stored);
    applyTheme(stored);
  }, []);

  function applyTheme(value) {
    if (value === 'auto') {
      const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
      document.documentElement.setAttribute('data-theme', prefersDark ? 'dark' : 'light');
    } else {
      document.documentElement.setAttribute('data-theme', value);
    }
    localStorage.setItem('theme', value);
  }

  function cycleTheme() {
    const currentIndex = THEMES.indexOf(theme);
    const nextTheme = THEMES[(currentIndex + 1) % THEMES.length];
    setTheme(nextTheme);
    applyTheme(nextTheme);
  }

  return (
    <button onClick={cycleTheme} aria-label="Toggle theme">
      {theme === 'light' && <SunIcon />}
      {theme === 'dark' && <MoonIcon />}
      {theme === 'auto' && <AutoIcon />}
    </button>
  );
}
```

### System Preferences Integration

Use the `prefers-color-scheme` media query:

```css
@media (prefers-color-scheme: dark) {
  [data-theme="auto"] {
    --color-background: var(--color-ink-900);
    --color-foreground: var(--color-paper-100);
  }
}
```

### Accessibility Considerations

- Use [WebAIM Contrast Checker](https://webaim.org/resources/contrastchecker/) to test contrast ratios
- Ensure all color combinations meet WCAG 2.2 AA requirements (4.5:1 for normal text, 3:1 for large text)
- Test both light and dark themes with screen readers
- Provide clear visual feedback for the theme toggle button

---

## 3. WCAG 2.2 AA Accessibility Compliance

**Source**: [Mivi WCAG Checklist](https://mivibzzz.com/resources/accessibility/wcag-checklist), [WebAIM Checklist](https://webaim.org/standards/wcag/checklist), [Level Access Guide](https://www.levelaccess.com/blog/wcag-2-2-aa-summary-and-checklist-for-website-owners/)

### Key Legal Requirements for 2026

- **DOJ Deadline**: April 2026 requires WCAG 2.1 Level AA compliance
- **WCAG 2.2 Status**: Became ISO/IEC 40500:2025 standard in October 2025
- **Recommendation**: Implement WCAG 2.1 AA (legal requirement) + WCAG 2.2 AA (best practice)

### New WCAG 2.2 Criteria (Level AA)

#### 1. Focus Not Obscured - Minimum (2.4.11)

When a UI component receives keyboard focus, at least a portion must remain visible.

**Implementation**:

```css
/* Ensure focus indicator is never hidden */
@utility focus-visible-always {
  outline: 2px solid var(--color-brand-500);
  outline-offset: 2px;
  z-index: 10;
}

/* Prevent sticky headers from covering focused elements */
.sticky-header {
  z-index: 9;
}

/* Scroll padding to account for fixed headers */
html {
  scroll-padding-top: 80px;
}
```

#### 2. Accessible Authentication (3.3.8)

Authentication processes must offer accessible alternatives (password managers, biometric logins).

**Implementation**:

```html
<!-- Support password managers -->
<input
  type="password"
  name="password"
  autocomplete="current-password"
  aria-describedby="password-requirements"
/>

<!-- Provide alternative authentication methods -->
<button type="button" onclick="biometricAuth()">
  Sign in with Touch ID
</button>

<!-- Avoid CAPTCHA or cognitive puzzles -->
<!-- Use honeypot fields or time-based validation instead -->
```

#### 3. Target Size - Minimum (2.5.8)

Touch targets must be at least 24x24 CSS pixels (recommendation: 44x44 pixels).

**Implementation**:

```css
@theme {
  /* Minimum touch target size */
  --min-touch-target: 44px;
}

@utility touch-target {
  min-width: var(--min-touch-target);
  min-height: var(--min-touch-target);
}

/* Apply to all interactive elements */
button, a, input, select, textarea {
  @apply touch-target;
}
```

### WCAG 2.1 AA Core Requirements

#### Color Contrast

- **Normal text**: 4.5:1 contrast ratio
- **Large text** (18pt+ or 14pt+ bold): 3:1 contrast ratio
- **UI components and graphics**: 3:1 contrast ratio

**Implementation with OKLCH**:

```css
@theme {
  /* High contrast text on paper background */
  --color-text-primary: oklch(0.06 0 0);    /* Nearly black */
  --color-background: oklch(0.99 0.01 95);  /* Off-white */

  /* Contrast ratio: ~19:1 ✓ */

  /* Sufficient contrast for secondary text */
  --color-text-secondary: oklch(0.45 0 0);  /* Dark gray */
  /* Contrast ratio: ~7:1 ✓ */

  /* Minimum for borders and dividers */
  --color-border: oklch(0.75 0 0);          /* Medium gray */
  /* Contrast ratio: ~3.5:1 ✓ */
}
```

#### Keyboard Navigation

All functionality must be keyboard accessible:

```css
/* Always show focus indicators */
*:focus-visible {
  outline: 2px solid var(--color-brand-500);
  outline-offset: 2px;
}

/* Skip to content link */
.skip-link {
  position: absolute;
  top: -40px;
  left: 0;
  background: var(--color-background);
  padding: 8px;
  z-index: 100;
}

.skip-link:focus {
  top: 0;
}
```

#### ARIA Labels and Roles

```html
<!-- Descriptive button labels -->
<button aria-label="Close dialog">
  <XIcon aria-hidden="true" />
</button>

<!-- Semantic landmarks -->
<nav aria-label="Main navigation">
  <!-- nav items -->
</nav>

<main id="main-content">
  <!-- main content -->
</main>

<!-- Loading states -->
<div role="status" aria-live="polite" aria-busy="true">
  Loading monitors...
</div>

<!-- Error messages -->
<div role="alert" aria-live="assertive">
  Failed to save monitor. Please try again.
</div>
```

### Practical Implementation Checklist

1. **Keyboard Navigation**
   - All interactive elements reachable via Tab
   - Logical tab order
   - Visible focus indicators
   - Escape key closes modals
   - Arrow keys for navigation in lists

2. **Screen Reader Support**
   - Descriptive alt text for images
   - ARIA labels for icon-only buttons
   - ARIA live regions for dynamic content
   - Semantic HTML5 elements
   - Heading hierarchy (h1 → h2 → h3)

3. **Color and Contrast**
   - Meet 4.5:1 ratio for text
   - Don't rely on color alone for information
   - Test in high contrast mode
   - Support dark mode

4. **Touch Targets**
   - Minimum 44x44 pixels
   - Adequate spacing between targets
   - Test on mobile devices

5. **Forms**
   - Label all inputs
   - Associate errors with fields
   - Provide clear instructions
   - Support autocomplete
   - Don't disable paste

---

## 4. Mobile-First Responsive Design

**Source**: [Tailwind CSS Responsive Design](https://tailwindcss.com/docs/responsive-design), [BrowserStack Breakpoints](https://www.browserstack.com/guide/responsive-design-breakpoints), [Keel Info Solution](https://www.keelis.com/blog/responsive-web-design-in-2026:-trends-and-best-practices)

### Content-Driven Breakpoints

**Key Principle**: Design for content needs, not specific devices. Define breakpoints where the content naturally requires adjustment.

#### Modern Breakpoints (2026)

```css
@theme {
  /* Mobile-first breakpoints */
  --breakpoint-xs: 20rem;    /* 320px - Small phones */
  --breakpoint-sm: 30rem;    /* 480px - Large phones */
  --breakpoint-md: 48rem;    /* 768px - Tablets */
  --breakpoint-lg: 64rem;    /* 1024px - Small laptops */
  --breakpoint-xl: 80rem;    /* 1280px - Large laptops */
  --breakpoint-2xl: 96rem;   /* 1536px - Desktops */
}
```

### Mobile-First Approach

Always start with mobile styles, then add complexity at larger breakpoints:

```html
<!-- Mobile-first: stack by default, row at md -->
<div class="flex flex-col md:flex-row gap-4">
  <div class="w-full md:w-1/2">Column 1</div>
  <div class="w-full md:w-1/2">Column 2</div>
</div>

<!-- Typography: smaller on mobile, larger on desktop -->
<h1 class="text-2xl md:text-4xl lg:text-5xl font-bold">
  Heading
</h1>

<!-- Padding: less on mobile, more on desktop -->
<section class="px-4 md:px-8 lg:px-16 py-8 md:py-12 lg:py-16">
  <!-- content -->
</section>
```

### Container Queries

**Major 2026 Shift**: Component-based responsiveness using container queries.

```css
@theme {
  /* Container query support */
}

@utility container-card {
  container-type: inline-size;
  container-name: card;
}

/* Component adapts to its container, not viewport */
.stat-card {
  @apply container-card;
}

@container card (min-width: 400px) {
  .stat-value {
    font-size: 2rem;
  }
}

@container card (min-width: 600px) {
  .stat-card {
    display: grid;
    grid-template-columns: 1fr 1fr;
  }
}
```

### Fluid Typography with clamp()

```css
@theme {
  /* Fluid font sizes */
  --text-sm: clamp(0.875rem, 0.8rem + 0.375vw, 1rem);
  --text-base: clamp(1rem, 0.9rem + 0.5vw, 1.125rem);
  --text-lg: clamp(1.125rem, 1rem + 0.625vw, 1.25rem);
  --text-xl: clamp(1.25rem, 1.1rem + 0.75vw, 1.5rem);
  --text-2xl: clamp(1.5rem, 1.3rem + 1vw, 2rem);
  --text-3xl: clamp(1.875rem, 1.6rem + 1.375vw, 2.5rem);
  --text-4xl: clamp(2.25rem, 1.9rem + 1.75vw, 3rem);
}
```

### Mobile Navigation Patterns

```html
<!-- Hamburger menu for mobile, full nav for desktop -->
<nav class="flex items-center justify-between">
  <!-- Logo always visible -->
  <div class="flex items-center">
    <img src="/logo.svg" alt="Hawk" class="h-8" />
  </div>

  <!-- Desktop navigation -->
  <div class="hidden md:flex items-center gap-6">
    <a href="/monitors">Monitors</a>
    <a href="/incidents">Incidents</a>
    <a href="/settings">Settings</a>
  </div>

  <!-- Mobile menu button -->
  <button
    class="md:hidden touch-target"
    aria-label="Toggle menu"
    aria-expanded="false"
  >
    <MenuIcon />
  </button>
</nav>

<!-- Mobile menu (hidden by default) -->
<div
  class="md:hidden fixed inset-0 bg-background z-50 hidden"
  id="mobile-menu"
>
  <div class="flex flex-col gap-4 p-6">
    <a href="/monitors" class="text-lg">Monitors</a>
    <a href="/incidents" class="text-lg">Incidents</a>
    <a href="/settings" class="text-lg">Settings</a>
  </div>
</div>
```

### Responsive Data Tables

```html
<!-- Card view on mobile, table on desktop -->
<div class="overflow-x-auto">
  <!-- Mobile: Card layout -->
  <div class="md:hidden space-y-4">
    <div class="border rounded-lg p-4">
      <div class="font-bold">Monitor Name</div>
      <div class="text-sm text-muted">example.com</div>
      <div class="mt-2 flex items-center gap-2">
        <span class="inline-flex items-center px-2 py-1 rounded text-xs bg-success-100 text-success-900">
          Healthy
        </span>
      </div>
    </div>
    <!-- More cards... -->
  </div>

  <!-- Desktop: Table layout -->
  <table class="hidden md:table w-full">
    <thead>
      <tr>
        <th>Name</th>
        <th>URL</th>
        <th>Status</th>
      </tr>
    </thead>
    <tbody>
      <tr>
        <td>Monitor Name</td>
        <td>example.com</td>
        <td>
          <span class="badge-success">Healthy</span>
        </td>
      </tr>
      <!-- More rows... -->
    </tbody>
  </table>
</div>
```

---

## 5. Loading States and Skeleton Screens

**Source**: [LogRocket Skeleton Design](https://blog.logrocket.com/ux-design/skeleton-loading-screen-design/), [Nielsen Norman Group](https://www.nngroup.com/articles/skeleton-screens/), [Mobbin Design Examples](https://mobbin.com/glossary/skeleton)

### When to Use Skeleton Screens

- Content loading takes >300ms
- Predictable content structure
- Progressive data loading
- Initial page load

**Don't use for**:
- Very fast operations (<300ms)
- Unpredictable content structure
- Button/form submission feedback (use spinners instead)

### Design Principles

1. **Accuracy**: Match the final content layout precisely
2. **Simplicity**: Use simple shapes (rectangles, circles)
3. **Animation**: Subtle pulse or wave animation
4. **Progressive**: Replace placeholders as content loads
5. **Accessibility**: Respect `prefers-reduced-motion`

### Implementation with Tailwind

#### Basic Skeleton Components

```css
@theme {
  --color-skeleton: oklch(0.90 0 0);
  --color-skeleton-highlight: oklch(0.95 0 0);
}

@keyframes pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.5;
  }
}

@keyframes wave {
  0% {
    transform: translateX(-100%);
  }
  100% {
    transform: translateX(100%);
  }
}

@utility skeleton-base {
  background-color: var(--color-skeleton);
  border-radius: 4px;
  animation: pulse 2s ease-in-out infinite;
}

@utility skeleton-wave {
  position: relative;
  overflow: hidden;
  background-color: var(--color-skeleton);

  &::after {
    content: '';
    position: absolute;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    background: linear-gradient(
      90deg,
      transparent,
      var(--color-skeleton-highlight),
      transparent
    );
    animation: wave 1.5s linear infinite;
  }
}

/* Respect reduced motion preference */
@media (prefers-reduced-motion: reduce) {
  .skeleton-base,
  .skeleton-wave {
    animation: none;
  }

  .skeleton-wave::after {
    display: none;
  }
}
```

#### Skeleton Patterns

```html
<!-- Text skeleton -->
<div class="skeleton-base h-4 w-full"></div>
<div class="skeleton-base h-4 w-3/4 mt-2"></div>
<div class="skeleton-base h-4 w-5/6 mt-2"></div>

<!-- Avatar + text skeleton -->
<div class="flex items-center gap-3">
  <div class="skeleton-base w-12 h-12 rounded-full"></div>
  <div class="flex-1">
    <div class="skeleton-base h-4 w-32"></div>
    <div class="skeleton-base h-3 w-24 mt-2"></div>
  </div>
</div>

<!-- Card skeleton -->
<div class="border rounded-lg p-4">
  <div class="skeleton-base h-6 w-48"></div>
  <div class="skeleton-base h-4 w-full mt-4"></div>
  <div class="skeleton-base h-4 w-5/6 mt-2"></div>
  <div class="flex gap-2 mt-4">
    <div class="skeleton-base h-8 w-20"></div>
    <div class="skeleton-base h-8 w-24"></div>
  </div>
</div>

<!-- Table skeleton -->
<div class="space-y-2">
  <div class="flex gap-4">
    <div class="skeleton-base h-10 w-48"></div>
    <div class="skeleton-base h-10 flex-1"></div>
    <div class="skeleton-base h-10 w-24"></div>
  </div>
  <!-- Repeat for more rows -->
</div>

<!-- Chart skeleton -->
<div class="flex items-end gap-2 h-48">
  <div class="skeleton-base w-12 h-32"></div>
  <div class="skeleton-base w-12 h-40"></div>
  <div class="skeleton-base w-12 h-24"></div>
  <div class="skeleton-base w-12 h-36"></div>
  <div class="skeleton-base w-12 h-28"></div>
</div>
```

#### React Component Example

```javascript
function MonitorCardSkeleton() {
  return (
    <div className="border rounded-lg p-4" role="status" aria-label="Loading monitor">
      <div className="skeleton-wave h-6 w-48" />
      <div className="skeleton-wave h-4 w-full mt-4" />
      <div className="skeleton-wave h-4 w-2/3 mt-2" />
      <div className="flex gap-2 mt-4">
        <div className="skeleton-wave h-8 w-20" />
        <div className="skeleton-wave h-8 w-24" />
      </div>
    </div>
  );
}

function MonitorsList() {
  const { data, loading, error } = useMonitors();

  if (loading) {
    return (
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        {Array.from({ length: 6 }).map((_, i) => (
          <MonitorCardSkeleton key={i} />
        ))}
      </div>
    );
  }

  if (error) {
    return <ErrorState error={error} />;
  }

  return (
    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
      {data.map((monitor) => (
        <MonitorCard key={monitor.id} monitor={monitor} />
      ))}
    </div>
  );
}
```

### Progressive Loading

Replace skeletons as content becomes available:

```javascript
function MonitorCard({ monitor, loading = false }) {
  if (loading) {
    return <MonitorCardSkeleton />;
  }

  return (
    <div className="border rounded-lg p-4">
      {monitor.name ? (
        <h3 className="text-lg font-bold">{monitor.name}</h3>
      ) : (
        <div className="skeleton-base h-6 w-48" />
      )}

      {monitor.url ? (
        <p className="text-sm text-muted mt-2">{monitor.url}</p>
      ) : (
        <div className="skeleton-base h-4 w-full mt-2" />
      )}

      {monitor.status ? (
        <StatusBadge status={monitor.status} />
      ) : (
        <div className="skeleton-base h-8 w-20 mt-4" />
      )}
    </div>
  );
}
```

---

## 6. Form Validation UX Patterns

**Source**: [Smashing Magazine Inline Validation](https://www.smashingmagazine.com/2022/09/inline-validation-web-forms-ux/), [Nielsen Norman Group Guidelines](https://www.nngroup.com/articles/errors-forms-design-guidelines/), [LogRocket Form Validation](https://blog.logrocket.com/ux-design/ux-form-validation-inline-after-submission/)

### Validation Strategy

**Hybrid Approach** (Best for most forms):
- **During input**: Real-time validation for critical fields (email, password)
- **After blur**: Validation when user leaves a field
- **On submit**: Comprehensive validation before submission

### Key Principles

1. **Don't validate prematurely**: Wait until user has finished typing
2. **Remove errors immediately**: When input is corrected
3. **Positive feedback**: Show success indicators for valid input
4. **Clear error messages**: Specific, human-readable, actionable
5. **Error proximity**: Place errors near the problematic field

### Implementation

#### HTML Structure

```html
<div class="form-field">
  <label for="email" class="form-label">
    Email address
    <span class="text-danger-500" aria-label="required">*</span>
  </label>

  <input
    type="email"
    id="email"
    name="email"
    class="form-input"
    aria-describedby="email-error email-hint"
    aria-invalid="false"
    required
  />

  <p id="email-hint" class="form-hint">
    We'll never share your email with anyone else.
  </p>

  <p id="email-error" class="form-error" role="alert" hidden>
    <!-- Error message inserted here -->
  </p>

  <p id="email-success" class="form-success" hidden>
    <CheckIcon /> Valid email address
  </p>
</div>
```

#### CSS Styles

```css
@theme {
  --color-input-border: var(--color-ink-300);
  --color-input-border-focus: var(--color-brand-500);
  --color-input-border-error: var(--color-danger-500);
  --color-input-border-success: var(--color-success-500);
}

.form-field {
  @apply mb-4;
}

.form-label {
  @apply block text-sm font-medium mb-1 text-ink-900;
}

.form-input {
  @apply block w-full px-3 py-2 border rounded-md;
  @apply focus:outline-none focus:ring-2 focus:ring-brand-500 focus:border-transparent;
  border-color: var(--color-input-border);

  &:focus {
    border-color: var(--color-input-border-focus);
  }

  &[aria-invalid="true"] {
    border-color: var(--color-input-border-error);
  }

  &.valid {
    border-color: var(--color-input-border-success);
  }
}

.form-hint {
  @apply mt-1 text-xs text-ink-600;
}

.form-error {
  @apply mt-1 text-sm text-danger-500 flex items-start gap-1;
}

.form-success {
  @apply mt-1 text-sm text-success-500 flex items-center gap-1;
}
```

#### JavaScript Validation

```javascript
class FormValidator {
  constructor(form) {
    this.form = form;
    this.fields = {};
    this.setupValidation();
  }

  setupValidation() {
    // Validate on blur (after user leaves field)
    this.form.querySelectorAll('input, textarea, select').forEach(field => {
      field.addEventListener('blur', () => this.validateField(field));

      // Remove error as user types (after initial validation)
      field.addEventListener('input', () => {
        if (field.getAttribute('aria-invalid') === 'true') {
          this.validateField(field);
        }
      });
    });

    // Validate entire form on submit
    this.form.addEventListener('submit', (e) => {
      e.preventDefault();
      if (this.validateForm()) {
        this.submitForm();
      }
    });
  }

  validateField(field) {
    const value = field.value.trim();
    const type = field.type;
    const required = field.hasAttribute('required');

    // Get error container
    const errorId = field.getAttribute('aria-describedby')?.split(' ')
      .find(id => id.includes('error'));
    const errorElement = errorId ? document.getElementById(errorId) : null;

    let error = null;

    // Required validation
    if (required && !value) {
      error = 'This field is required';
    }
    // Email validation
    else if (type === 'email' && value) {
      const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
      if (!emailRegex.test(value)) {
        error = 'Please enter a valid email address';
      }
    }
    // Password validation
    else if (field.name === 'password' && value) {
      if (value.length < 8) {
        error = 'Password must be at least 8 characters';
      } else if (!/[A-Z]/.test(value)) {
        error = 'Password must contain at least one uppercase letter';
      } else if (!/[0-9]/.test(value)) {
        error = 'Password must contain at least one number';
      }
    }
    // URL validation
    else if (type === 'url' && value) {
      try {
        new URL(value);
      } catch {
        error = 'Please enter a valid URL';
      }
    }

    // Update UI
    if (error) {
      this.showError(field, errorElement, error);
      return false;
    } else {
      this.clearError(field, errorElement);
      if (value) {
        this.showSuccess(field);
      }
      return true;
    }
  }

  showError(field, errorElement, message) {
    field.setAttribute('aria-invalid', 'true');
    field.classList.remove('valid');

    if (errorElement) {
      errorElement.textContent = message;
      errorElement.hidden = false;
    }
  }

  clearError(field, errorElement) {
    field.setAttribute('aria-invalid', 'false');

    if (errorElement) {
      errorElement.textContent = '';
      errorElement.hidden = true;
    }
  }

  showSuccess(field) {
    field.classList.add('valid');
  }

  validateForm() {
    let isValid = true;
    const fields = this.form.querySelectorAll('input, textarea, select');

    fields.forEach(field => {
      if (!this.validateField(field)) {
        isValid = false;
      }
    });

    // Focus first invalid field
    if (!isValid) {
      const firstInvalid = this.form.querySelector('[aria-invalid="true"]');
      firstInvalid?.focus();
    }

    return isValid;
  }

  async submitForm() {
    const formData = new FormData(this.form);

    try {
      const response = await fetch(this.form.action, {
        method: this.form.method,
        body: formData,
      });

      if (response.ok) {
        this.showSuccessMessage();
      } else {
        this.showErrorMessage('Failed to submit form. Please try again.');
      }
    } catch (error) {
      this.showErrorMessage('Network error. Please check your connection.');
    }
  }
}

// Initialize
document.querySelectorAll('form[data-validate]').forEach(form => {
  new FormValidator(form);
});
```

### Specific Validation Patterns

#### Real-time Email Validation (Debounced)

```javascript
let emailTimeout;
emailInput.addEventListener('input', (e) => {
  clearTimeout(emailTimeout);
  emailTimeout = setTimeout(() => {
    validateEmail(e.target.value);
  }, 500); // Wait 500ms after user stops typing
});
```

#### Password Strength Indicator

```html
<div class="form-field">
  <label for="password">Password</label>
  <input type="password" id="password" name="password" />

  <div class="password-strength mt-2">
    <div class="strength-bar">
      <div class="strength-fill" style="width: 0%"></div>
    </div>
    <p class="strength-text text-xs mt-1">Strength: None</p>
  </div>

  <ul class="password-requirements text-xs mt-2 space-y-1">
    <li class="requirement" data-check="length">
      <span class="icon">○</span> At least 8 characters
    </li>
    <li class="requirement" data-check="uppercase">
      <span class="icon">○</span> One uppercase letter
    </li>
    <li class="requirement" data-check="number">
      <span class="icon">○</span> One number
    </li>
    <li class="requirement" data-check="special">
      <span class="icon">○</span> One special character
    </li>
  </ul>
</div>
```

```javascript
function updatePasswordStrength(password) {
  const requirements = {
    length: password.length >= 8,
    uppercase: /[A-Z]/.test(password),
    lowercase: /[a-z]/.test(password),
    number: /[0-9]/.test(password),
    special: /[^A-Za-z0-9]/.test(password),
  };

  const score = Object.values(requirements).filter(Boolean).length;
  const percentage = (score / 5) * 100;

  // Update strength bar
  const fill = document.querySelector('.strength-fill');
  fill.style.width = `${percentage}%`;

  // Update strength text and color
  const strengthText = document.querySelector('.strength-text');
  if (score === 0) {
    strengthText.textContent = 'Strength: None';
    fill.className = 'strength-fill';
  } else if (score <= 2) {
    strengthText.textContent = 'Strength: Weak';
    fill.className = 'strength-fill bg-danger-500';
  } else if (score <= 4) {
    strengthText.textContent = 'Strength: Medium';
    fill.className = 'strength-fill bg-warning-500';
  } else {
    strengthText.textContent = 'Strength: Strong';
    fill.className = 'strength-fill bg-success-500';
  }

  // Update requirement checkmarks
  Object.entries(requirements).forEach(([check, passed]) => {
    const req = document.querySelector(`[data-check="${check}"]`);
    const icon = req?.querySelector('.icon');
    if (icon) {
      icon.textContent = passed ? '✓' : '○';
      req.className = passed ? 'requirement text-success-500' : 'requirement text-ink-600';
    }
  });
}
```

---

## 7. Toast Notification Systems

**Source**: [Sara Soueidan ARIA Live Regions](https://www.sarasoueidan.com/blog/accessible-notifications-with-aria-live-regions-part-1/), [MDN ARIA Live Regions](https://developer.mozilla.org/en-US/docs/Web/Accessibility/ARIA/Guides/Live_regions), [Adrian Roselli Toast Messages](https://adrianroselli.com/2020/01/defining-toast-messages.html)

### Key Accessibility Requirements

1. **Live regions must exist at page load** (to prime accessibility tree)
2. **Use `role="status"` for polite announcements** (most toasts)
3. **Use `role="alert"` for critical errors** (assertive announcements)
4. **Avoid interactive elements in toasts** (problematic for screen readers)
5. **Provide adequate time to read** (minimum 5 seconds)
6. **Support dismiss actions** (keyboard accessible)

### Implementation

#### HTML Structure

```html
<!-- Toast container - exists on page load -->
<div
  id="toast-container"
  class="fixed top-4 right-4 z-50 flex flex-col gap-2 pointer-events-none"
  aria-live="polite"
  aria-atomic="false"
>
  <!-- Toasts inserted here -->
</div>

<!-- For critical errors, use a separate assertive container -->
<div
  id="alert-container"
  class="fixed top-4 right-4 z-50 flex flex-col gap-2 pointer-events-none"
  role="alert"
  aria-live="assertive"
  aria-atomic="true"
>
  <!-- Critical alerts inserted here -->
</div>
```

#### CSS Styles

```css
@keyframes toast-slide-in {
  from {
    transform: translateX(100%);
    opacity: 0;
  }
  to {
    transform: translateX(0);
    opacity: 1;
  }
}

@keyframes toast-slide-out {
  from {
    transform: translateX(0);
    opacity: 1;
  }
  to {
    transform: translateX(100%);
    opacity: 0;
  }
}

.toast {
  @apply pointer-events-auto;
  @apply min-w-[300px] max-w-md;
  @apply bg-ink-900 text-paper-100;
  @apply rounded-lg shadow-lg;
  @apply p-4 flex items-start gap-3;
  animation: toast-slide-in 0.3s ease-out;

  &.toast-exiting {
    animation: toast-slide-out 0.3s ease-out;
  }
}

.toast-success {
  @apply bg-success-900 text-success-50;
}

.toast-error {
  @apply bg-danger-900 text-danger-50;
}

.toast-warning {
  @apply bg-warning-900 text-warning-50;
}

.toast-info {
  @apply bg-ink-900 text-paper-100;
}

/* Respect reduced motion */
@media (prefers-reduced-motion: reduce) {
  .toast {
    animation: none;
  }
}
```

#### JavaScript Toast Manager

```javascript
class ToastManager {
  constructor() {
    this.container = document.getElementById('toast-container');
    this.alertContainer = document.getElementById('alert-container');
    this.toasts = new Map();
  }

  /**
   * Show a toast notification
   * @param {string} message - The message to display
   * @param {object} options - Configuration options
   * @param {string} options.type - 'success', 'error', 'warning', or 'info'
   * @param {number} options.duration - Duration in ms (0 = no auto-dismiss)
   * @param {boolean} options.dismissible - Show close button
   * @param {boolean} options.critical - Use assertive live region
   */
  show(message, options = {}) {
    const {
      type = 'info',
      duration = 5000,
      dismissible = true,
      critical = false,
    } = options;

    const toast = this.createToast(message, type, dismissible);
    const container = critical ? this.alertContainer : this.container;

    // Add to DOM
    container.appendChild(toast);

    // Store reference
    const id = Date.now().toString();
    this.toasts.set(id, toast);

    // Auto-dismiss after duration
    if (duration > 0) {
      setTimeout(() => {
        this.dismiss(id);
      }, duration);
    }

    return id;
  }

  createToast(message, type, dismissible) {
    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.setAttribute('role', 'status');

    // Icon
    const icon = this.getIcon(type);

    // Message
    const messageEl = document.createElement('div');
    messageEl.className = 'flex-1 text-sm';
    messageEl.textContent = message;

    toast.appendChild(icon);
    toast.appendChild(messageEl);

    // Dismiss button
    if (dismissible) {
      const closeBtn = document.createElement('button');
      closeBtn.className = 'ml-2 hover:opacity-75 focus:outline-none focus-visible:ring-2';
      closeBtn.setAttribute('aria-label', 'Dismiss notification');
      closeBtn.innerHTML = `
        <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 20 20">
          <path fill-rule="evenodd" d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z" clip-rule="evenodd" />
        </svg>
      `;

      closeBtn.addEventListener('click', () => {
        const id = Array.from(this.toasts.entries())
          .find(([_, t]) => t === toast)?.[0];
        if (id) this.dismiss(id);
      });

      toast.appendChild(closeBtn);
    }

    return toast;
  }

  getIcon(type) {
    const iconWrapper = document.createElement('div');
    iconWrapper.className = 'flex-shrink-0';
    iconWrapper.setAttribute('aria-hidden', 'true');

    const icons = {
      success: '<svg class="w-5 h-5" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clip-rule="evenodd" /></svg>',
      error: '<svg class="w-5 h-5" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z" clip-rule="evenodd" /></svg>',
      warning: '<svg class="w-5 h-5" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clip-rule="evenodd" /></svg>',
      info: '<svg class="w-5 h-5" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a1 1 0 000 2v3a1 1 0 001 1h1a1 1 0 100-2v-3a1 1 0 00-1-1H9z" clip-rule="evenodd" /></svg>',
    };

    iconWrapper.innerHTML = icons[type] || icons.info;
    return iconWrapper;
  }

  dismiss(id) {
    const toast = this.toasts.get(id);
    if (!toast) return;

    // Add exit animation
    toast.classList.add('toast-exiting');

    // Remove after animation completes
    setTimeout(() => {
      toast.remove();
      this.toasts.delete(id);
    }, 300);
  }

  dismissAll() {
    this.toasts.forEach((_, id) => this.dismiss(id));
  }
}

// Create global instance
const toast = new ToastManager();

// Convenience methods
window.toast = {
  success: (msg, opts) => toast.show(msg, { ...opts, type: 'success' }),
  error: (msg, opts) => toast.show(msg, { ...opts, type: 'error', critical: true }),
  warning: (msg, opts) => toast.show(msg, { ...opts, type: 'warning' }),
  info: (msg, opts) => toast.show(msg, { ...opts, type: 'info' }),
};
```

#### Usage Examples

```javascript
// Success notification
toast.success('Monitor created successfully');

// Error notification (critical, assertive)
toast.error('Failed to save changes. Please try again.');

// Warning with custom duration
toast.warning('Your session will expire in 5 minutes', {
  duration: 10000,
});

// Info notification without auto-dismiss
toast.info('New features available in Settings', {
  duration: 0,
  dismissible: true,
});

// Usage in async operations
async function createMonitor(data) {
  try {
    const response = await fetch('/api/monitors', {
      method: 'POST',
      body: JSON.stringify(data),
    });

    if (response.ok) {
      toast.success('Monitor created successfully');
      return response.json();
    } else {
      toast.error('Failed to create monitor');
    }
  } catch (error) {
    toast.error('Network error. Please check your connection.');
  }
}
```

### Best Practices

1. **Keep messages concise**: 1-2 sentences maximum
2. **Use clear language**: Avoid technical jargon
3. **Provide context**: "Monitor created" → "Monitor 'example.com' created successfully"
4. **Don't stack too many**: Limit to 3 visible toasts at once
5. **Position consistently**: Top-right or bottom-right (avoid bottom-left on mobile)
6. **Ensure contrast**: Meet WCAG 2.2 AA requirements (4.5:1)
7. **Test with screen readers**: Verify announcements are clear

---

## 8. OKLCH Color System Usage

**Source**: [Evil Martians OKLCH Guide](https://evilmartians.com/chronicles/oklch-in-css-why-quit-rgb-hsl), [LogRocket OKLCH Palettes](https://blog.logrocket.com/oklch-css-consistent-accessible-color-palettes), [UX Collective OKLCH Explained](https://uxdesign.cc/oklch-explained-for-designers-dc6af4433611)

### Why OKLCH?

1. **Perceptually Uniform**: Equal changes in lightness values produce equal perceived brightness changes
2. **Predictable Contrast**: Easier to calculate and maintain WCAG contrast ratios
3. **Wide Gamut**: Supports 30% more colors than RGB (P3 color space)
4. **Consistent Brightness**: Different hues at the same lightness appear equally bright

### OKLCH Format

```
oklch(lightness chroma hue)
```

- **Lightness**: 0-1 or 0%-100% (0 = black, 1 = white)
- **Chroma**: 0-0.4+ (0 = grayscale, higher = more saturated)
- **Hue**: 0-360 degrees (color wheel angle)

### Browser Support (2026)

- Chrome 111+
- Safari 15.4+
- Firefox 113+
- Native support in all modern browsers

### Generating Color Palettes

#### Manual Approach

```css
@theme {
  /* Start with base hue and chroma */
  --hue-brand: 100;        /* Yellow */
  --chroma-brand: 0.18;    /* Medium saturation */

  /* Generate palette by varying lightness */
  --color-brand-50: oklch(0.98 0.05 var(--hue-brand));
  --color-brand-100: oklch(0.95 0.08 var(--hue-brand));
  --color-brand-200: oklch(0.90 0.12 var(--hue-brand));
  --color-brand-300: oklch(0.85 0.15 var(--hue-brand));
  --color-brand-400: oklch(0.80 0.18 var(--hue-brand));
  --color-brand-500: oklch(0.75 var(--chroma-brand) var(--hue-brand)); /* Base */
  --color-brand-600: oklch(0.65 var(--chroma-brand) var(--hue-brand));
  --color-brand-700: oklch(0.55 0.16 var(--hue-brand));
  --color-brand-800: oklch(0.45 0.14 var(--hue-brand));
  --color-brand-900: oklch(0.35 0.12 var(--hue-brand));
}
```

#### Using Online Tools

- [OKLCH Color Picker](https://oklch.net/)
- [OKLCH Palette Generator](https://oklch.org/)

### Converting Existing Colors

Convert HEX to OKLCH:

```javascript
// Example: #ffd400 (yellow) → oklch(0.92 0.18 100)

// Use online converter or browser DevTools
// Chrome DevTools automatically shows OKLCH in color picker
```

### Practical Applications

#### Generating Semantic Colors

```css
@theme {
  /* Success (Green) - Hue ~145 */
  --color-success-50: oklch(0.95 0.08 145);
  --color-success-500: oklch(0.70 0.15 145);
  --color-success-900: oklch(0.35 0.12 145);

  /* Danger (Red) - Hue ~25 */
  --color-danger-50: oklch(0.95 0.10 25);
  --color-danger-500: oklch(0.65 0.20 25);
  --color-danger-900: oklch(0.35 0.15 25);

  /* Warning (Amber) - Hue ~85 */
  --color-warning-50: oklch(0.95 0.08 85);
  --color-warning-500: oklch(0.75 0.15 85);
  --color-warning-900: oklch(0.40 0.12 85);

  /* Info (Blue) - Hue ~240 */
  --color-info-50: oklch(0.95 0.08 240);
  --color-info-500: oklch(0.65 0.18 240);
  --color-info-900: oklch(0.35 0.15 240);
}
```

#### Ensuring WCAG Compliance

OKLCH makes it easier to predict contrast ratios:

```css
@theme {
  /* Background: high lightness (0.99) */
  --color-background: oklch(0.99 0.01 95);

  /* Text: low lightness (0.15) */
  --color-text-primary: oklch(0.15 0 0);
  /* Contrast ratio: ~16:1 ✓ */

  /* Secondary text: medium lightness (0.50) */
  --color-text-secondary: oklch(0.50 0 0);
  /* Contrast ratio: ~6:1 ✓ */

  /* Borders: lightness (0.75) */
  --color-border: oklch(0.75 0 0);
  /* Contrast ratio: ~3.5:1 ✓ */
}
```

#### Dark Mode with OKLCH

```css
@theme {
  /* Light mode */
  --color-bg-light: oklch(0.99 0.01 95);
  --color-text-light: oklch(0.15 0 0);

  /* Dark mode */
  --color-bg-dark: oklch(0.15 0 0);
  --color-text-dark: oklch(0.95 0.01 95);
}

/* Apply based on theme */
[data-theme="light"] {
  --color-background: var(--color-bg-light);
  --color-foreground: var(--color-text-light);
}

[data-theme="dark"] {
  --color-background: var(--color-bg-dark);
  --color-foreground: var(--color-text-dark);
}
```

#### Color Manipulation with color-mix()

Tailwind v4 supports `color-mix()` for advanced color manipulation:

```css
/* Lighten a color */
.lighten {
  background: color-mix(in oklch, var(--color-brand-500) 80%, white);
}

/* Darken a color */
.darken {
  background: color-mix(in oklch, var(--color-brand-500) 80%, black);
}

/* Increase opacity */
.translucent {
  background: color-mix(in oklch, var(--color-brand-500) 50%, transparent);
}

/* Blend two colors */
.blend {
  background: color-mix(in oklch, var(--color-brand-500) 50%, var(--color-ink-900));
}
```

### Migration Strategy

1. **Audit existing colors**: List all HEX/RGB values in use
2. **Convert to OKLCH**: Use online tools or browser DevTools
3. **Test contrast**: Verify WCAG compliance with [WebAIM Contrast Checker](https://webaim.org/resources/contrastchecker/)
4. **Update theme file**: Replace values in `@theme` directive
5. **Test in browsers**: Verify appearance in Chrome, Safari, Firefox
6. **Add fallbacks**: For older browsers (if needed)

```css
/* With fallback (optional) */
.button {
  background: #ffd400; /* Fallback for old browsers */
  background: oklch(0.92 0.18 100); /* Modern browsers */
}
```

---

## Additional Resources

### Official Documentation
- [Tailwind CSS v4 Docs](https://tailwindcss.com/)
- [MDN Web Accessibility](https://developer.mozilla.org/en-US/docs/Web/Accessibility)
- [WCAG 2.2 Guidelines](https://www.w3.org/WAI/standards-guidelines/wcag/)

### Design Systems Examples
- [Shadcn UI](https://ui.shadcn.com/) - Modern component library with Tailwind
- [GitLab Pajamas](https://design.gitlab.com/) - Comprehensive design system
- [Cloudscape Design System](https://cloudscape.design/) - AWS design patterns

### Testing Tools
- [WebAIM Contrast Checker](https://webaim.org/resources/contrastchecker/)
- [WAVE Accessibility Checker](https://wave.webaim.org/)
- [axe DevTools](https://www.deque.com/axe/devtools/)
- [Lighthouse](https://developer.chrome.com/docs/lighthouse/) (built into Chrome DevTools)

### Color Tools
- [OKLCH Color Picker](https://oklch.net/)
- [OKLCH Palette Generator](https://oklch.org/)
- [Tailwind CSS Color Generator](https://uicolors.app/create)

---

## Summary of Key Recommendations

1. **Adopt Tailwind v4 CSS-First Architecture**
   - Use `@theme` for design tokens
   - Leverage OKLCH for color definitions
   - Implement compound component patterns

2. **Implement Comprehensive Dark Mode**
   - Use CSS variables for all colors
   - Support light, dark, and system preferences
   - Test contrast in both modes

3. **Ensure WCAG 2.2 AA Compliance**
   - Meet 4.5:1 contrast ratio for text
   - Ensure keyboard accessibility
   - Use ARIA labels appropriately
   - Make touch targets 44x44 pixels minimum

4. **Build Mobile-First Responsive Layouts**
   - Start with mobile styles
   - Use content-driven breakpoints
   - Leverage container queries for components
   - Implement fluid typography with clamp()

5. **Enhance User Experience**
   - Add skeleton screens for loading states
   - Implement inline form validation
   - Use accessible toast notifications
   - Provide clear error messages and feedback

6. **Use OKLCH Color System**
   - Ensures perceptual uniformity
   - Simplifies contrast compliance
   - Supports wider color gamut
   - Future-proof color management

---

**Next Steps**: Review this research and incorporate applicable patterns into the refactor plan at `/home/alex/Source/hawk/docs/plans/2026-02-09-refactor-ui-tailwind-v4-modern-saas-design-plan.md`.
