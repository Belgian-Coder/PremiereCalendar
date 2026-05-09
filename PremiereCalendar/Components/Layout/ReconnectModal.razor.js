// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

let reconnectInProgress = false;
let resumeInProgress = false;

function stopVisibilityRetry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
}

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        if (!reconnectModal.open) {
            reconnectModal.showModal();
        }
    } else if (event.detail.state === "hide") {
        stopVisibilityRetry();
        if (reconnectModal.open) {
            reconnectModal.close();
        }
    } else if (event.detail.state === "failed") {
        stopVisibilityRetry();
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        stopVisibilityRetry();
        location.reload();
    }
}

async function retry() {
    if (reconnectInProgress) {
        return;
    }

    reconnectInProgress = true;
    stopVisibilityRetry();

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            location.reload();
            return;
        }

        stopVisibilityRetry();
    } catch (err) {
        // We got an exception, server is currently unavailable
        stopVisibilityRetry();
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } finally {
        reconnectInProgress = false;
    }
}

async function resume() {
    if (resumeInProgress) {
        return;
    }

    resumeInProgress = true;
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            stopVisibilityRetry();
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
        await retry();
    }
}
