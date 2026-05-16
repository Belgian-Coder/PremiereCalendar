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

  function focusPaletteItem() {
    const focus = () => {
      const panel = document.querySelector(panelSelector);
      const item = panel?.querySelector(paletteItemSelector);
      if (item instanceof HTMLElement) {
        item.focus({ preventScroll: true });
      }
    };

    requestAnimationFrame(() => requestAnimationFrame(focus));
    window.setTimeout(focus, 80);
    window.setTimeout(focus, 200);
    window.setTimeout(focus, 500);
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
