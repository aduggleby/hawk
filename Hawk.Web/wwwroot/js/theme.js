// Theme toggle (dark/light) for Hawk.Web.
//
// Uses a simple localStorage key and a `dark` class on <html>.
// Storage key is namespaced to avoid collisions with other apps.

(() => {
  const STORAGE_KEY = "hawk.theme";
  const root = document.documentElement;
  const btn = document.getElementById("theme-toggle");

  function applyTheme(mode) {
    const prefersDark = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
    const effective =
      mode === "dark" ? "dark" :
      mode === "light" ? "light" :
      (prefersDark ? "dark" : "light");

    root.classList.toggle("dark", effective === "dark");

    if (btn) {
      btn.setAttribute("aria-pressed", effective === "dark" ? "true" : "false");
    }
  }

  function getStoredMode() {
    try {
      return localStorage.getItem(STORAGE_KEY);
    } catch {
      return null;
    }
  }

  function setStoredMode(mode) {
    try {
      if (!mode) localStorage.removeItem(STORAGE_KEY);
      else localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      // ignore
    }
  }

  function toggleTheme() {
    const isDark = root.classList.contains("dark");
    setStoredMode(isDark ? "light" : "dark");
    applyTheme(getStoredMode());
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
      if (!getStoredMode()) applyTheme(null);
    });
  }
})();

