(() => {
  if (window.premiereCalendarDomObserver?.initialized) {
    return;
  }

  const callbacks = new Set();
  const roots = new Set();
  let scheduled = false;

  const schedule = (root) => {
    roots.add(root || document);
    if (scheduled) {
      return;
    }

    scheduled = true;
    window.requestAnimationFrame(() => {
      scheduled = false;
      const currentRoots = Array.from(roots);
      roots.clear();

      for (const callback of callbacks) {
        callback(currentRoots);
      }
    });
  };

  const observer = new MutationObserver((mutations) => {
    for (const mutation of mutations) {
      for (const node of mutation.addedNodes) {
        if (node.nodeType === Node.ELEMENT_NODE) {
          schedule(node);
        }
      }
    }
  });

  window.premiereCalendarDomObserver = {
    initialized: true,
    register(callback) {
      callbacks.add(callback);
      callback([document]);
      return () => callbacks.delete(callback);
    },
    schedule
  };

  document.addEventListener("DOMContentLoaded", () => schedule(document));
  observer.observe(document.documentElement, { childList: true, subtree: true });
})();
