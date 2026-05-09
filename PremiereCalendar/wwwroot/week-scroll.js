const filterPaneSelector = "[data-filter-pane]";
const filterCloseSelector = "[data-filter-close]";
const dayButtonSelector = "[data-day-button]";
const autoDayNavigationDebounceMs = 550;
const autoDayNavigationWheelThreshold = 24;
const autoDayNavigationActivationDelta = 760;
const autoDayNavigationMinimumPromptMs = 800;
const autoDayNavigationEdgeTolerance = 4;
const autoDayNavigation = new WeakMap();
const focusableSelector = [
    "a[href]",
    "button:not([disabled])",
    "input:not([disabled])",
    "select:not([disabled])",
    "textarea:not([disabled])",
    "[tabindex]:not([tabindex='-1'])"
].join(",");

function findElements(roots, selector) {
    const elements = [];

    for (const root of roots) {
        if (!(root instanceof Element) && !(root instanceof Document)) {
            continue;
        }

        if (root instanceof Element && root.matches(selector)) {
            elements.push(root);
        }

        elements.push(...root.querySelectorAll(selector));
    }

    return elements;
}

function scrollSelectedDayIntoView(element, behavior = "smooth") {
    if (!(element instanceof HTMLElement)) {
        return;
    }

    const sticky = document.querySelector(".week-sticky-controls");
    const stickyRect = sticky instanceof HTMLElement
        ? sticky.getBoundingClientRect()
        : { height: 0 };
    const stickyTop = sticky instanceof HTMLElement
        ? Number.parseFloat(window.getComputedStyle(sticky).top) || 0
        : 0;
    const targetTop = window.scrollY + element.getBoundingClientRect().top - stickyTop - stickyRect.height - 8;

    window.scrollTo({
        top: Math.max(0, targetTop),
        behavior
    });
}

function focusDayButton(dayElementId) {
    const target = String(dayElementId ?? "");
    const button = Array
        .from(document.querySelectorAll(dayButtonSelector))
        .find(candidate => candidate.getAttribute("data-day-target") === target);

    if (button instanceof HTMLElement) {
        button.focus({ preventScroll: true });
    }
}

function isAtContentTop(element) {
    if (!(element instanceof HTMLElement)) {
        return window.scrollY <= autoDayNavigationEdgeTolerance;
    }

    const sticky = document.querySelector(".week-sticky-controls");
    const stickyBottom = sticky instanceof HTMLElement
        ? sticky.getBoundingClientRect().bottom
        : 0;
    return element.getBoundingClientRect().top >= stickyBottom - autoDayNavigationEdgeTolerance
        || window.scrollY <= autoDayNavigationEdgeTolerance;
}

function isAtPageBottom() {
    const documentElement = document.documentElement;
    const scrollHeight = Math.max(
        documentElement.scrollHeight,
        document.body?.scrollHeight ?? 0);
    return window.scrollY + window.innerHeight >= scrollHeight - autoDayNavigationEdgeTolerance;
}

function findEdgePrompt(element, direction) {
    const promptName = direction > 0 ? "next" : "previous";
    const root = element?.closest?.(".calendar-week-board") ?? document;
    return root.querySelector(`[data-day-scroll-prompt="${promptName}"]`);
}

function showEdgePrompt(element, direction, accumulatedDelta) {
    const prompt = findEdgePrompt(element, direction);
    if (!(prompt instanceof HTMLElement)) {
        return;
    }

    const pull = Math.min(18, Math.max(0, accumulatedDelta / 18));
    prompt.style.setProperty("--edge-scroll-pull", `${pull.toFixed(1)}px`);
    prompt.classList.add("is-active");
    prompt.setAttribute("aria-hidden", "false");
}

function hideEdgePrompt(prompt) {
    if (!(prompt instanceof HTMLElement)) {
        return;
    }

    prompt.classList.remove("is-active");
    prompt.setAttribute("aria-hidden", "true");
    prompt.style.removeProperty("--edge-scroll-pull");
}

function hideEdgePrompts(element) {
    const root = element?.closest?.(".calendar-week-board") ?? document;
    root.querySelectorAll("[data-day-scroll-prompt]").forEach(hideEdgePrompt);
}

function registerAutoDayNavigation(element, dotNetReference) {
    if (!(element instanceof HTMLElement) || !dotNetReference) {
        return;
    }

    disposeAutoDayNavigation(element);

    let lastNavigationAt = 0;
    let activeDirection = 0;
    let accumulatedDelta = 0;
    let promptStartedAt = 0;
    let resetPromptTimer = 0;
    const resetPrompt = () => {
        window.clearTimeout(resetPromptTimer);
        resetPromptTimer = 0;
        activeDirection = 0;
        accumulatedDelta = 0;
        promptStartedAt = 0;
        hideEdgePrompts(element);
    };

    const onWheel = (event) => {
        if (document.querySelector(filterPaneSelector) || Math.abs(event.deltaY) < autoDayNavigationWheelThreshold) {
            return;
        }

        const direction = event.deltaY > 0 ? 1 : -1;
        if ((direction > 0 && !isAtPageBottom()) || (direction < 0 && !isAtContentTop(element))) {
            resetPrompt();
            return;
        }

        const now = Date.now();
        if (now - lastNavigationAt < autoDayNavigationDebounceMs) {
            event.preventDefault();
            return;
        }

        event.preventDefault();
        window.clearTimeout(resetPromptTimer);

        if (activeDirection !== direction) {
            hideEdgePrompts(element);
            activeDirection = direction;
            accumulatedDelta = 0;
            promptStartedAt = now;
        }

        accumulatedDelta += Math.abs(event.deltaY);
        showEdgePrompt(element, direction, accumulatedDelta);
        resetPromptTimer = window.setTimeout(resetPrompt, 1400);

        if (accumulatedDelta < autoDayNavigationActivationDelta
            || now - promptStartedAt < autoDayNavigationMinimumPromptMs) {
            return;
        }

        lastNavigationAt = now;
        resetPrompt();
        dotNetReference
            .invokeMethodAsync("SelectAdjacentDayByScrollAsync", direction)
            .catch(() => {
            });
    };

    element.addEventListener("wheel", onWheel, { passive: false });
    autoDayNavigation.set(element, {
        onWheel,
        resetPrompt
    });
}

function disposeAutoDayNavigation(element) {
    const registration = autoDayNavigation.get(element);
    if (!registration) {
        return;
    }

    element.removeEventListener("wheel", registration.onWheel);
    registration.resetPrompt?.();
    autoDayNavigation.delete(element);
}

function initializeWeekControls(roots = [document]) {
    findElements(roots, dayButtonSelector).forEach(initializeDayButton);
    findElements(roots, filterPaneSelector).forEach(initializeFilterPane);
}

function initializeDayButton(button) {
    if (button.dataset.dayButtonReady === "true") {
        return;
    }

    button.dataset.dayButtonReady = "true";
    button.addEventListener("click", () => {
        const board = document.querySelector("[data-testid='calendar-week']");
        if (board instanceof HTMLElement) {
            scrollSelectedDayIntoView(board, "auto");
        }
    }, { passive: true });
}

function initializeFilterPane(pane) {
    if (pane.dataset.filterPaneReady === "true") {
        return;
    }

    pane.dataset.filterPaneReady = "true";
    const previouslyFocused = document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    let startX = 0;
    let startY = 0;

    requestAnimationFrame(() => {
        const firstFocusable = pane.querySelector(focusableSelector);
        if (firstFocusable instanceof HTMLElement) {
            firstFocusable.focus({ preventScroll: true });
        } else {
            pane.focus({ preventScroll: true });
        }
    });

    pane.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
            event.preventDefault();
            pane.querySelector(filterCloseSelector)?.click();
            return;
        }

        if (event.key !== "Tab") {
            return;
        }

        const focusable = Array
            .from(pane.querySelectorAll(focusableSelector))
            .filter(element => element instanceof HTMLElement && element.offsetParent !== null);
        if (focusable.length === 0) {
            event.preventDefault();
            pane.focus({ preventScroll: true });
            return;
        }

        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        const active = document.activeElement;
        if (event.shiftKey && active === first) {
            event.preventDefault();
            last.focus({ preventScroll: true });
        } else if (!event.shiftKey && active === last) {
            event.preventDefault();
            first.focus({ preventScroll: true });
        }
    });

    pane.querySelectorAll(filterCloseSelector).forEach(button => {
        button.addEventListener("click", () => {
            if (previouslyFocused instanceof HTMLElement && document.contains(previouslyFocused)) {
                requestAnimationFrame(() => previouslyFocused.focus({ preventScroll: true }));
            }
        }, { once: true });
    });

    pane.addEventListener("touchstart", (event) => {
        const touch = event.changedTouches[0];
        startX = touch.clientX;
        startY = touch.clientY;
    }, { passive: true });

    pane.addEventListener("touchend", (event) => {
        const touch = event.changedTouches[0];
        const deltaX = touch.clientX - startX;
        const deltaY = touch.clientY - startY;
        const horizontalSwipe = deltaX < -80 && Math.abs(deltaX) > Math.abs(deltaY) * 1.4;

        if (horizontalSwipe) {
            pane.querySelector(filterCloseSelector)?.click();
        }
    }, { passive: true });
}

window.premiereCalendarWeek = {
    scrollSelectedDayIntoView,
    focusDayButton,
    registerAutoDayNavigation,
    disposeAutoDayNavigation
};

if (window.premiereCalendarDomObserver) {
    window.premiereCalendarDomObserver.register(initializeWeekControls);
} else {
    initializeWeekControls([document]);
    document.addEventListener("DOMContentLoaded", () => initializeWeekControls([document]));
}
