(() => {
  if (window.premiereCalendarTheme?.initialized) {
    window.premiereCalendarTheme.reapply();
    return;
  }

  const key = "premiere-calendar:theme";
  const cookieKey = "premiere-calendar-theme";
  const validThemes = new Set(["light", "dark"]);
  let applying = false;
  let enhancedNavigationRegistered = false;

  const readSavedTheme = () => {
    try {
      const saved = window.localStorage.getItem(key);
      return validThemes.has(saved) ? saved : null;
    } catch {
      return null;
    }
  };

  const readCookieTheme = () => {
    const match = document.cookie.match(new RegExp(`(?:^|; )${cookieKey}=([^;]*)`));
    const value = match ? decodeURIComponent(match[1]) : null;
    return validThemes.has(value) ? value : null;
  };

  const saveCookieTheme = (theme) => {
    document.cookie = `${cookieKey}=${encodeURIComponent(theme)}; Max-Age=31536000; Path=/; SameSite=Lax`;
  };

  const saveTheme = (theme) => {
    try {
      window.localStorage.setItem(key, theme);
    } catch {
      // Ignore blocked storage; the visible theme still changes for this page.
    }

    saveCookieTheme(theme);
  };

  const preferredTheme = () =>
    window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";

  const currentTheme = () => {
    const theme = document.documentElement.dataset.theme;
    return validThemes.has(theme) ? theme : preferredTheme();
  };

  const resolvedTheme = () => readSavedTheme() ?? readCookieTheme() ?? preferredTheme();

  const applyTheme = (theme) => {
    applying = true;
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
    if (document.body) {
      document.body.dataset.theme = theme;
    }

    document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
      button.setAttribute("aria-pressed", theme === "dark" ? "true" : "false");
      button.setAttribute("title", theme === "dark" ? "Switch to light theme" : "Switch to dark theme");
    });
    applying = false;
  };

  const reapplySavedTheme = () => applyTheme(resolvedTheme());

  const handleClick = (event) => {
    const button = event.target.closest("[data-theme-toggle]");
    if (!button) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    const nextTheme = currentTheme() === "dark" ? "light" : "dark";
    saveTheme(nextTheme);
    applyTheme(nextTheme);
  };

  document.addEventListener("click", handleClick, true);

  document.addEventListener("DOMContentLoaded", reapplySavedTheme);
  window.addEventListener("pageshow", reapplySavedTheme);
  window.addEventListener("storage", (event) => {
    if (event.key === key) {
      reapplySavedTheme();
    }
  });

  new MutationObserver(() => {
    if (applying) {
      return;
    }

    const saved = readSavedTheme();
    if (saved && currentTheme() !== saved) {
      applyTheme(saved);
    }
  }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });

  const registerBlazorEnhancedNavigation = () => {
    if (!enhancedNavigationRegistered && window.Blazor?.addEventListener) {
      enhancedNavigationRegistered = true;
      window.Blazor.addEventListener("enhancedload", reapplySavedTheme);
    }
  };

  document.addEventListener("DOMContentLoaded", registerBlazorEnhancedNavigation);
  window.setTimeout(registerBlazorEnhancedNavigation, 0);

  window.premiereCalendarTheme = {
    initialized: true,
    reapply: reapplySavedTheme,
    apply: (theme) => {
      if (validThemes.has(theme)) {
        saveTheme(theme);
        applyTheme(theme);
      }
    },
    current: currentTheme
  };
})();
