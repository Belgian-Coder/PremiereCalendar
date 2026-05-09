const dayAutoloadSelector = "[data-day-autoload-sentinel]";
const dayLoadAllSelector = "[data-day-load-all]";

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
      { rootMargin: "1200px 0px" });
  }

  for (const sentinel of sentinels) {
    if (sentinel.dataset.autoloadObserved === "true") {
      continue;
    }

    sentinel.dataset.autoloadObserved = "true";
    window.premiereCalendarDayAutoloadObserver.observe(sentinel);
  }
}

function loadMoreUntilComplete(sentinel) {
  if (!(sentinel instanceof HTMLElement) || sentinel.dataset.autoloadLoading === "true") {
    return;
  }

  sentinel.dataset.autoloadLoading = "true";

  const pump = () => {
    if (!document.documentElement.contains(sentinel)) {
      return;
    }

    const button = sentinel.closest("[data-testid='calendar-day']")?.querySelector(dayLoadAllSelector);
    if (!(button instanceof HTMLElement)) {
      sentinel.dataset.autoloadLoading = "false";
      return;
    }

    button.click();
    sentinel.dataset.autoloadLoading = "false";
  };

  pump();
}

if (window.premiereCalendarDomObserver) {
  window.premiereCalendarDomObserver.register(initializeDayAutoload);
} else {
  initializeDayAutoload([document]);
  document.addEventListener("DOMContentLoaded", () => initializeDayAutoload([document]));
}
