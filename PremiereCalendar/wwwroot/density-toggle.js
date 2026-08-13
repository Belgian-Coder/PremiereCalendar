(() => {
    const apply = (compact) => {
        document.documentElement.dataset.density = compact ? "compact" : "comfortable";
        return compact;
    };
    window.premiereCalendarDensity = {
        initialize: (key) => {
            let compact = true;
            try {
                const saved = window.localStorage?.getItem(key);
                compact = saved !== "comfortable";
            } catch { }
            return apply(compact);
        },
        toggle: (key) => {
            const compact = document.documentElement.dataset.density !== "compact";
            try { window.localStorage?.setItem(key, compact ? "compact" : "comfortable"); } catch { }
            return apply(compact);
        }
    };
})();
