const dayAutoloadSelector = "[data-day-autoload-sentinel]";
const dayLoadMoreSelector = "[data-day-load-more]";
const observedDayAutoloadSentinels = new Set();

function findDayAutoloadSentinels(roots) {
  const sentinels = [];

  for (const root of roots) {
    if (!(root instanceof Element) && !(root instanceof Document)) {
      continue;
    }

    if (root instanceof Element && root.matches(dayAutoloadSelector)) {
      sentinels.push(root);
    }

    sentinels.push(...root.querySelectorAll(dayAutoloadSelector));
  }

  return sentinels;
}

function initializeDayAutoload(roots = [document]) {
  const sentinels = findDayAutoloadSentinels(roots);

  if (!("IntersectionObserver" in window)) {
    for (const sentinel of sentinels) {
      loadMoreUntilComplete(sentinel);
    }
    return;
  }

  if (!window.premiereCalendarDayAutoloadObserver) {
    window.premiereCalendarDayAutoloadObserver = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) {
            continue;
          }

          loadMoreUntilComplete(entry.target);
        }
      },
      { rootMargin: "400px 0px" });
  }

  for (const sentinel of sentinels) {
    if (sentinel.dataset.autoloadObserved === "true") {
      continue;
    }

    sentinel.dataset.autoloadObserved = "true";
    window.premiereCalendarDayAutoloadObserver.observe(sentinel);
    observedDayAutoloadSentinels.add(sentinel);
  }
}

function loadMoreUntilComplete(sentinel) {
  if (!(sentinel instanceof HTMLElement) || sentinel.dataset.autoloadLoading === "true") {
    return;
  }

  sentinel.dataset.autoloadLoading = "true";

  const pump = () => {
    if (!document.documentElement.contains(sentinel)) {
      window.premiereCalendarDayAutoloadObserver?.unobserve(sentinel);
      observedDayAutoloadSentinels.delete(sentinel);
      return;
    }

    const button = sentinel.closest("[data-testid='calendar-day']")?.querySelector(dayLoadMoreSelector);
    if (!(button instanceof HTMLElement)) {
      sentinel.dataset.autoloadLoading = "false";
      return;
    }

    button.click();
    sentinel.dataset.autoloadLoading = "false";
  };

  pump();
}

function cleanupObservedDayAutoloadSentinel(sentinel) {
  if (!observedDayAutoloadSentinels.has(sentinel)) {
    return;
  }

  window.premiereCalendarDayAutoloadObserver.unobserve(sentinel);
  observedDayAutoloadSentinels.delete(sentinel);
}

function cleanupDetachedDayAutoloadSentinels(roots = []) {
  if (!window.premiereCalendarDayAutoloadObserver) {
    return;
  }

  if (roots.length > 0) {
    for (const root of roots) {
      if (!(root instanceof Element)) {
        continue;
      }

      if (root.matches(dayAutoloadSelector)) {
        cleanupObservedDayAutoloadSentinel(root);
      }

      for (const sentinel of root.querySelectorAll(dayAutoloadSelector)) {
        cleanupObservedDayAutoloadSentinel(sentinel);
      }
    }

    return;
  }

  for (const sentinel of Array.from(observedDayAutoloadSentinels)) {
    if (document.documentElement.contains(sentinel)) {
      continue;
    }

    cleanupObservedDayAutoloadSentinel(sentinel);
  }
}

if (window.premiereCalendarDomObserver) {
  window.premiereCalendarDomObserver.register(initializeDayAutoload);
  window.premiereCalendarDomObserver.registerCleanup(cleanupDetachedDayAutoloadSentinels);
} else {
  initializeDayAutoload([document]);
  document.addEventListener("DOMContentLoaded", () => initializeDayAutoload([document]));
}
