(function() {
    const COLOR_MODES = ['light', 'dark'];
    const THEME_ATTR = 'data-btcpay-theme';
    const STORE_ATTR = 'btcpay-theme';
    const mediaMatcher = window.matchMedia('(prefers-color-scheme: dark)');

    window.setColorMode = userMode => {
        if (userMode === 'system') {
            window.localStorage.removeItem(STORE_ATTR);
            document.documentElement.removeAttribute(THEME_ATTR);
        } else if (COLOR_MODES.includes(userMode)) {
            window.localStorage.setItem(STORE_ATTR, userMode);
            document.documentElement.setAttribute(THEME_ATTR, userMode);
        }
        const user = window.localStorage.getItem(STORE_ATTR);
        const system = mediaMatcher.matches ? COLOR_MODES[1] : COLOR_MODES[0];
        const mode = user || system;

        document.getElementById('DarkThemeLinkTag').disabled = mode !== 'dark';

        // Update trigger icons to reflect current selection
        const selected = user ? (user === 'dark' ? 'themes-dark' : 'themes-light') : 'themes-system';
        document.querySelectorAll('.btcpay-theme-trigger use').forEach(function(use) {
            var href = use.getAttribute('href');
            use.setAttribute('href', href.replace(/#.*$/, '#' + selected));
        });
    }

    // set initial mode
    setColorMode(window.localStorage.getItem(STORE_ATTR));

    // listen for system mode changes
    mediaMatcher.addEventListener('change', e => {
        const userMode = window.localStorage.getItem(STORE_ATTR);
        if (!userMode) setColorMode('system');
    });

    // click handler for theme switch buttons
    document.addEventListener('click', function(e) {
        const btn = e.target.closest('.btcpay-theme-switch [data-theme]') || e.target.closest('[data-theme].btcpay-theme-switch');
        if (!btn) return;
        e.preventDefault();
        setColorMode(btn.dataset.theme);
        btn.blur();
    });
})();
