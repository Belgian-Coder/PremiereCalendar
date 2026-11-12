(() => {
const lastSeenStorage = () => {
  try {
    const session = window.sessionStorage;
    if (session) {
      session.getItem("premiere-calendar:view-sync:probe");
      return session;
    }
  } catch {
    // Fall through to localStorage.
  }

  return window.localStorage;
};

window.premiereViewSync = {
  getOrCreateDeviceId() {
    const key = "premiere-calendar:view-sync:device-id";
    const createId = () => {
      if (window.crypto?.randomUUID) {
        return window.crypto.randomUUID().replaceAll("-", "");
      }

      return `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`;
    };

    try {
      let deviceId = window.localStorage?.getItem(key);
      if (!deviceId) {
        deviceId = createId();
        window.localStorage?.setItem(key, deviceId);
      }

      return deviceId;
    } catch {
      window.__premiereViewSyncFallbackDeviceId ??= createId();
      return window.__premiereViewSyncFallbackDeviceId;
    }
  },

  // The URL this device last published or applied for a route, with the group revision it carried.
  // Stored per tab (sessionStorage) so a second tab on the same device never inherits it;
  // localStorage is only a fallback when sessionStorage is unavailable.
  getLastSeen(routeKey) {
    try {
      const raw = lastSeenStorage().getItem(`premiere-calendar:view-sync:last:${routeKey}`);
      if (!raw) {
        return null;
      }

      const value = JSON.parse(raw);
      if (value && typeof value.url === "string" && value.url && Number.isFinite(value.revision)) {
        return {
          url: value.url,
          revision: value.revision,
          groupId: typeof value.groupId === "string" ? value.groupId : null
        };
      }
    } catch {
      // Missing or corrupt storage behaves like "never seen".
    }

    return null;
  },

  setLastSeen(routeKey, url, revision, groupId) {
    try {
      lastSeenStorage().setItem(
        `premiere-calendar:view-sync:last:${routeKey}`,
        JSON.stringify({ url, revision, groupId }));
    } catch {
      // Storage can be unavailable; the page then simply follows the old behaviour.
    }
  }
};
})();
