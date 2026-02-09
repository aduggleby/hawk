// Mobile navigation drawer for small screens.
//
// Uses `hidden` attribute for state + body overflow lock while open.
// Minimal focus management: focus close button on open, restore focus on close.

(() => {
  const menu = document.getElementById("mobile-menu");
  const openBtn = document.getElementById("mobile-menu-toggle");
  const closeBtn = document.getElementById("mobile-menu-close");
  const backdrop = document.getElementById("mobile-menu-backdrop");
  const panel = document.getElementById("mobile-menu-panel");

  if (!menu || !openBtn || !closeBtn || !backdrop || !panel) return;

  let lastActive = null;
  let closeTimer = null;

  function isOpen() {
    return !menu.hasAttribute("hidden");
  }

  function setExpanded(expanded) {
    openBtn.setAttribute("aria-expanded", expanded ? "true" : "false");
  }

  function open() {
    if (isOpen()) return;
    if (closeTimer) {
      clearTimeout(closeTimer);
      closeTimer = null;
    }
    lastActive = document.activeElement;
    menu.removeAttribute("hidden");
    document.body.style.overflow = "hidden";
    setExpanded(true);

    // Kick transitions (next frame so the browser applies initial styles first).
    requestAnimationFrame(() => {
      backdrop.classList.remove("opacity-0");
      backdrop.classList.add("opacity-100");
      panel.classList.remove("-translate-x-full");
      panel.classList.add("translate-x-0");
      closeBtn.focus();
    });
  }

  function close() {
    if (!isOpen()) return;
    backdrop.classList.remove("opacity-100");
    backdrop.classList.add("opacity-0");
    panel.classList.remove("translate-x-0");
    panel.classList.add("-translate-x-full");

    // Let the slide/fade finish before hiding.
    closeTimer = window.setTimeout(() => {
      menu.setAttribute("hidden", "");
      document.body.style.overflow = "";
      setExpanded(false);
      if (lastActive && typeof lastActive.focus === "function") {
        lastActive.focus();
      } else {
        openBtn.focus();
      }
      lastActive = null;
      closeTimer = null;
    }, 210);
  }

  openBtn.addEventListener("click", open);
  closeBtn.addEventListener("click", close);
  backdrop.addEventListener("click", close);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") close();
  });
})();
