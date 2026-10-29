// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

(() => {
    const formatters = {
        short: new Intl.DateTimeFormat(undefined, {
            dateStyle: "short",
            timeStyle: "short"
        }),
        detailed: new Intl.DateTimeFormat(undefined, {
            year: "numeric",
            month: "2-digit",
            day: "2-digit",
            hour: "2-digit",
            minute: "2-digit",
            second: "2-digit",
            timeZoneName: "short"
        }),
        utcTitle: new Intl.DateTimeFormat(undefined, {
            year: "numeric",
            month: "2-digit",
            day: "2-digit",
            hour: "2-digit",
            minute: "2-digit",
            second: "2-digit",
            timeZone: "UTC",
            timeZoneName: "short"
        })
    };

    for (const el of document.querySelectorAll("[data-utc]")) {
        const value = el.getAttribute("data-utc");
        if (!value) continue;

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) continue;

        const format = el.getAttribute("data-utc-format") || "short";
        const formatter = formatters[format] || formatters.short;
        el.textContent = formatter.format(date);

        const utcLabel = formatters.utcTitle.format(date);
        const localLabel = formatters.detailed.format(date);
        el.title = `Local: ${localLabel}\nUTC: ${utcLabel}`;
    }
})();

// <details> menus (Account, More, group Actions): close on Escape (focus back on the toggle) and on a click outside.
(() => {
    const openMenus = () => document.querySelectorAll("details.hawk-menu[open], details[data-menu][open]");

    document.addEventListener("keydown", (event) => {
        if (event.key !== "Escape") return;
        for (const menu of openMenus()) {
            menu.open = false;
            if (menu.contains(document.activeElement)) menu.querySelector("summary")?.focus();
        }
    });

    document.addEventListener("click", (event) => {
        for (const menu of openMenus()) {
            if (!menu.contains(event.target)) menu.open = false;
        }
    });
})();
