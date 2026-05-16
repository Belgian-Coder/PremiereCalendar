(() => {
  if (window.premiereCommandPalette?.initialized) {
    return;
  }

  const toggleSelector = "[data-command-palette-toggle]";
  const panelSelector = "[data-command-palette-panel]";

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
      return true;
    }

    return false;
  }

  function closePalette() {
    if (!document.querySelector(panelSelector)) {
      return false;
    }

    return togglePalette();
  }

  document.addEventListener("keydown", (event) => {
    if (event.defaultPrevented || isEditableTarget(event.target)) {
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
