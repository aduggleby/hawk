// Theme toggle (system/light/dark) for Hawk.Web.
//
// Uses a simple localStorage key and a `dark` class on <html>.
// Storage key is namespaced to avoid collisions with other apps.

(() => {
  const STORAGE_KEY = "hawk.theme";
  const root = document.documentElement;
  const btn = document.getElementById("theme-toggle");
  const icons = {
    system: btn?.querySelector('[data-theme-icon="system"]') ?? null,
    light: btn?.querySelector('[data-theme-icon="light"]') ?? null,
    dark: btn?.querySelector('[data-theme-icon="dark"]') ?? null
  };
  const label = btn?.querySelector("[data-theme-label]") ?? null;

  function normalizeMode(mode) {
    return mode === "light" || mode === "dark" || mode === "system" ? mode : "system";
  }

  function applyTheme(mode) {
    const normalized = normalizeMode(mode);
    const prefersDark = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
    const effective =
      normalized === "dark" ? "dark" :
      normalized === "light" ? "light" :
      (prefersDark ? "dark" : "light");

    root.classList.toggle("dark", effective === "dark");
    root.dataset.themeMode = normalized;

    if (btn) {
      btn.setAttribute("aria-pressed", effective === "dark" ? "true" : "false");
      btn.setAttribute("aria-label", `Theme: ${normalized}`);
      btn.setAttribute("title", `Theme: ${normalized}`);
    }

    if (label) {
      label.textContent = normalized === "system" ? "Sys" : normalized === "light" ? "Light" : "Dark";
    }

    for (const [key, icon] of Object.entries(icons)) {
      if (!icon) continue;
      if (key === normalized) {
        icon.classList.remove("hidden");
      } else {
        icon.classList.add("hidden");
      }
    }
  }

  function getStoredMode() {
    try {
      return normalizeMode(localStorage.getItem(STORAGE_KEY));
    } catch {
      return "system";
    }
  }

  function setStoredMode(mode) {
    const normalized = normalizeMode(mode);
    try {
      localStorage.setItem(STORAGE_KEY, normalized);
    } catch {
      // ignore
    }
  }

  function toggleTheme() {
    const current = getStoredMode();
    const next =
      current === "system" ? "light" :
      current === "light" ? "dark" :
      "system";
    setStoredMode(next);
    applyTheme(next);
  }

  // Ensure aria state is correct even if theme was applied before paint.
  applyTheme(getStoredMode());

  if (btn) {
    btn.addEventListener("click", toggleTheme);
  }

  // React to system preference changes only when user hasn't explicitly chosen.
  const media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
  if (media) {
    media.addEventListener("change", () => {
      if (getStoredMode() === "system") applyTheme("system");
    });
  }
})();
