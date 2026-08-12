(() => {
    const apply = (compact) => {
        document.documentElement.dataset.density = compact ? "compact" : "comfortable";
        return compact;
    };
    window.premiereCalendarDensity = {
        initialize: (key) => {
            let compact = false;
            try { compact = window.localStorage?.getItem(key) === "compact"; } catch { }
            return apply(compact);
        },
        toggle: (key) => {
            const compact = document.documentElement.dataset.density !== "compact";
            try { window.localStorage?.setItem(key, compact ? "compact" : "comfortable"); } catch { }
            return apply(compact);
        }
    };
})();
