// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", () => retry());

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

const automaticRetryDelayMilliseconds = 5000;
const maximumAutomaticRetryAttempts = 12;
let reconnectInProgress = false;
let resumeInProgress = false;
let recoveryEnabled = false;
let automaticRetryAttempts = 0;
let retryTimer;

function stopAutomaticRetry() {
    recoveryEnabled = false;
    clearTimeout(retryTimer);
    retryTimer = undefined;
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    window.removeEventListener("online", retryWhenOnline);
}

function enableAutomaticRetry() {
    recoveryEnabled = true;
    document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    window.addEventListener("online", retryWhenOnline);
    scheduleAutomaticRetry();
}

function scheduleAutomaticRetry() {
    if (!recoveryEnabled || reconnectInProgress || retryTimer !== undefined
        || document.visibilityState !== "visible" || navigator.onLine === false
        || automaticRetryAttempts >= maximumAutomaticRetryAttempts) {
        return;
    }

    retryTimer = setTimeout(async () => {
        retryTimer = undefined;
        if (document.visibilityState === "visible" && navigator.onLine !== false) {
            automaticRetryAttempts++;
            await retry();
        }
    }, automaticRetryDelayMilliseconds);
}

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        if (!reconnectModal.open) {
            reconnectModal.showModal();
        }
    } else if (event.detail.state === "hide") {
        stopAutomaticRetry();
        automaticRetryAttempts = 0;
        if (reconnectModal.open) {
            reconnectModal.close();
        }
    } else if (event.detail.state === "failed") {
        automaticRetryAttempts = 0;
        enableAutomaticRetry();
    } else if (event.detail.state === "rejected") {
        stopAutomaticRetry();
        location.reload();
    }
}

async function retry() {
    if (reconnectInProgress) {
        return;
    }

    reconnectInProgress = true;
    clearTimeout(retryTimer);
    retryTimer = undefined;
    enableAutomaticRetry();

    try {
        // false means the server rejected the circuit; exceptions mean it is unavailable.
        const successful = await Blazor.reconnect();
        stopAutomaticRetry();
        if (!successful) {
            location.reload();
        }
    } catch {
        // Keep recovery listeners active while a bounded visible retry window runs.
    } finally {
        reconnectInProgress = false;
        scheduleAutomaticRetry();
    }
}

async function resume() {
    if (resumeInProgress) {
        return;
    }

    resumeInProgress = true;
    try {
        const successful = await Blazor.resumeCircuit();
        stopAutomaticRetry();
        if (!successful) {
            location.reload();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    } finally {
        resumeInProgress = false;
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        automaticRetryAttempts = 0;
        await retry();
    } else {
        clearTimeout(retryTimer);
        retryTimer = undefined;
    }
}

async function retryWhenOnline() {
    automaticRetryAttempts = 0;
    if (document.visibilityState === "visible") {
        await retry();
    }
}
