(() => {
  if (window.premiereCommandPalette?.initialized) {
    return;
  }

  const toggleSelector = "[data-command-palette-toggle]";
  const panelSelector = "[data-command-palette-panel]";
  const paletteItemSelector = ".command-palette-item";
  const blockingOverlaySelector = "[data-filter-pane], [role='dialog']";

  function isEditableTarget(target) {
    if (!(target instanceof HTMLElement)) {
      return false;
    }

    const tagName = target.tagName.toLowerCase();
    return target.isContentEditable
      || tagName === "input"
      || tagName === "select"
      || tagName === "textarea";
  }

  function togglePalette() {
    const toggle = document.querySelector(toggleSelector);
    if (toggle instanceof HTMLElement) {
      toggle.click();
      focusPaletteItem();
      return true;
    }

    return false;
  }

  let cancelPendingFocus = null;

  // A next-frame attempt, then retries for up to ~500 ms (the panel is server-rendered, so on a slow
  // link it can appear late). Any user focus, pointer or keyboard activity cancels the rest.
  function focusPaletteItem() {
    cancelPendingFocus?.();

    const userEvents = ["focusin", "pointerdown", "keydown"];
    let frame = 0;
    const timers = [];
    const cancel = () => {
      cancelAnimationFrame(frame);
      timers.forEach((timer) => window.clearTimeout(timer));
      for (const name of userEvents) {
        document.removeEventListener(name, cancel, true);
      }

      if (cancelPendingFocus === cancel) {
        cancelPendingFocus = null;
      }
    };
    const attempt = () => {
      const panel = document.querySelector(panelSelector);
      const item = panel?.querySelector(paletteItemSelector);
      if (item instanceof HTMLElement) {
        item.focus({ preventScroll: true });
        return true;
      }

      return false;
    };

    cancelPendingFocus = cancel;
    for (const name of userEvents) {
      document.addEventListener(name, cancel, true);
    }

    frame = requestAnimationFrame(() => {
      if (attempt()) {
        cancel();
        return;
      }

      for (const delay of [80, 200, 500]) {
        timers.push(window.setTimeout(() => {
          if (attempt() || delay === 500) {
            cancel();
          }
        }, delay));
      }
    });
  }

  function closePalette() {
    if (!document.querySelector(panelSelector)) {
      return false;
    }

    const toggle = document.querySelector(toggleSelector);
    const closed = togglePalette();
    if (closed && toggle instanceof HTMLElement) {
      requestAnimationFrame(() => toggle.focus({ preventScroll: true }));
    }

    return closed;
  }

  document.addEventListener("keydown", (event) => {
    if (event.defaultPrevented || isEditableTarget(event.target)) {
      return;
    }

    if (event.target instanceof Element && event.target.closest(blockingOverlaySelector)) {
      return;
    }

    const isPaletteShortcut = (event.ctrlKey || event.metaKey)
      && !event.altKey
      && !event.shiftKey
      && event.key.toLowerCase() === "k";

    if (isPaletteShortcut) {
      if (togglePalette()) {
        event.preventDefault();
      }
      return;
    }

    if (event.key === "Escape" && closePalette()) {
      event.preventDefault();
    }
  });

  window.premiereCommandPalette = {
    initialized: true,
    toggle: togglePalette,
    close: closePalette
  };
})();
