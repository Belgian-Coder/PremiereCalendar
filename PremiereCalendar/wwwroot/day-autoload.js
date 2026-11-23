const dayAutoloadSelector = "[data-day-autoload-sentinel], [data-day-window-previous]";
const dayLoadMoreSelector = "[data-day-load-more]";
const dayLoadPreviousSelector = "[data-day-load-previous]";
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

    const isPrevious = sentinel.matches("[data-day-window-previous]");
    const buttonSelector = isPrevious ? dayLoadPreviousSelector : dayLoadMoreSelector;
    const button = sentinel.closest("[data-testid='calendar-day']")?.querySelector(buttonSelector);
    if (!(button instanceof HTMLElement)) {
      sentinel.dataset.autoloadLoading = "false";
      return;
    }

    const day = sentinel.closest("[data-testid='calendar-day']");
    if (day instanceof HTMLElement) {
      // IntersectionObserver reports only visibility changes. A click ignored while a move is pending,
      // or a move that completes without toggling pending, would otherwise stall autoload while the
      // sentinel stays in view, so resume once the window settles.
      repumpWhenWindowSettles(day);
    }

    if (!(button instanceof HTMLButtonElement && button.disabled) && day?.dataset.dayWindowPending !== "true") {
      button.click();
    }
    sentinel.dataset.autoloadLoading = "false";
  };

  pump();
}

function isNearDayAutoloadViewport(element) {
  const rect = element.getBoundingClientRect();
  return rect.bottom >= -400 && rect.top <= window.innerHeight + 400;
}

function repumpWhenWindowSettles(day) {
  if (day.premiereCalendarRepump) {
    return;
  }

  const observer = new MutationObserver(() => {
    if (document.documentElement.contains(day) && day.dataset.dayWindowPending === "true") {
      return;
    }

    observer.disconnect();
    day.premiereCalendarRepump = null;
    if (!document.documentElement.contains(day)) {
      return;
    }

    // Resume at most one direction per settle so forward and previous loads cannot ping-pong.
    const next = [day.querySelector("[data-day-autoload-sentinel]"), day.querySelector("[data-day-window-previous]")]
      .find((candidate) => candidate instanceof HTMLElement && isNearDayAutoloadViewport(candidate));
    if (next) {
      loadMoreUntilComplete(next);
    }
  });
  observer.observe(day, {
    attributes: true,
    attributeFilter: ["data-day-window-pending", "data-day-window-start", "data-day-window-end"]
  });
  day.premiereCalendarRepump = observer;
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

window.premiereCalendarDayWindow = {
  measureTrimHeight(dayElement, itemCount) {
    if (!(dayElement instanceof HTMLElement) || !Number.isFinite(itemCount) || itemCount <= 0) {
      return 0;
    }

    const cards = Array.from(dayElement.querySelectorAll("[data-testid='premiere-card']"));
    if (cards.length === 0) {
      return 0;
    }

    const firstRect = cards[0].getBoundingClientRect();
    const nextCard = cards[Math.min(Math.trunc(itemCount), cards.length - 1)];
    const nextRect = nextCard.getBoundingClientRect();
    if (nextRect.top > firstRect.top) {
      return nextRect.top - firstRect.top;
    }

    const lastRemoved = cards[Math.min(Math.trunc(itemCount), cards.length) - 1];
    const lastRect = lastRemoved.getBoundingClientRect();
    return Math.max(0, lastRect.bottom - firstRect.top);
  }
};
