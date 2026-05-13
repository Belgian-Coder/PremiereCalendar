(() => {
  if (window.premiereCalendarDomObserver?.initialized) {
    return;
  }

  const callbacks = new Set();
  const cleanupCallbacks = new Set();
  const roots = new Set();
  const cleanupRoots = new Set();
  let scheduled = false;
  let cleanupScheduled = false;

  const requestFlush = () => {
    if (scheduled) {
      return;
    }

    scheduled = true;
    window.requestAnimationFrame(() => {
      scheduled = false;
      const currentRoots = Array.from(roots);
      const currentCleanupRoots = Array.from(cleanupRoots);
      const shouldCleanup = cleanupScheduled;
      roots.clear();
      cleanupRoots.clear();
      cleanupScheduled = false;

      if (currentRoots.length > 0) {
        for (const callback of callbacks) {
          callback(currentRoots);
        }
      }

      if (shouldCleanup) {
        for (const callback of cleanupCallbacks) {
          callback(currentCleanupRoots);
        }
      }
    });
  };

  const schedule = (root) => {
    roots.add(root || document);
    requestFlush();
  };

  const scheduleCleanup = (root) => {
    if (root) {
      cleanupRoots.add(root);
    }

    cleanupScheduled = true;
    requestFlush();
  };

  const observer = new MutationObserver((mutations) => {
    for (const mutation of mutations) {
      if (mutation.type === "attributes" && mutation.target instanceof Element) {
        schedule(mutation.target);
      }

      for (const node of mutation.addedNodes) {
        if (node.nodeType === Node.ELEMENT_NODE) {
          schedule(node);
        }
      }

      for (const node of mutation.removedNodes) {
        if (node.nodeType === Node.ELEMENT_NODE) {
          scheduleCleanup(node);
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
    registerCleanup(callback) {
      cleanupCallbacks.add(callback);
      return () => cleanupCallbacks.delete(callback);
    },
    schedule
  };

  document.addEventListener("DOMContentLoaded", () => schedule(document));
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true,
    attributes: true,
    attributeFilter: ["data-lazy-src"]
  });
})();
