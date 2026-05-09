const filterPaneSelector = "[data-filter-pane]";
const filterCloseSelector = "[data-filter-close]";
const dayButtonSelector = "[data-day-button]";
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
    scrollSelectedDayIntoView
};

if (window.premiereCalendarDomObserver) {
    window.premiereCalendarDomObserver.register(initializeWeekControls);
} else {
    initializeWeekControls([document]);
    document.addEventListener("DOMContentLoaded", () => initializeWeekControls([document]));
}
