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
  }
};
