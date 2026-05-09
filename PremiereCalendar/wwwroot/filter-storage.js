window.premiereFilterStorage = {
  get(key) {
    return window.localStorage.getItem(key);
  },
  set(key, value) {
    window.localStorage.setItem(key, value ?? "");
  }
};
