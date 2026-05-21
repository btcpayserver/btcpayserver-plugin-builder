(function () {
    const embedPage = document.querySelector("[data-embed-page]");
    if (!embedPage || window.parent === window) {
        return;
    }

    let lastHeight = 0;
    let heightPostQueued = false;
    let hiddenPluginIdentifiers = new Set();

    function applyHostColorMode(colorMode) {
        if (colorMode !== "light" && colorMode !== "dark") {
            return;
        }

        document.documentElement.setAttribute("data-btcpay-theme", colorMode);

        const darkThemeLink = document.getElementById("DarkThemeLinkTag");
        if (darkThemeLink) {
            darkThemeLink.disabled = colorMode !== "dark";
        }

        scheduleHeightPost();
    }

    function postReady() {
        window.parent.postMessage({ type: "pb:ready" }, "*");
    }

    function getContentHeight() {
        const rectHeight = embedPage.getBoundingClientRect ? embedPage.getBoundingClientRect().height : 0;
        return Math.max(
            embedPage.scrollHeight || 0,
            embedPage.offsetHeight || 0,
            Math.ceil(rectHeight)
        );
    }

    function postHeight() {
        heightPostQueued = false;
        const height = Math.ceil(getContentHeight()) + 4;
        if (!height || Math.abs(height - lastHeight) < 2) {
            return;
        }

        lastHeight = height;
        window.parent.postMessage({
            type: "pb:content-height",
            height: height
        }, "*");
    }

    function scheduleHeightPost() {
        if (heightPostQueued) {
            return;
        }

        heightPostQueued = true;
        window.requestAnimationFrame(postHeight);
    }

    function normalizeIdentifier(identifier) {
        return typeof identifier === "string" ? identifier.trim().toLowerCase() : "";
    }

    function normalizeHiddenPluginIdentifiers(value) {
        const identifiers = new Set();
        if (!Array.isArray(value)) {
            return identifiers;
        }

        value.forEach(function (identifier) {
            const normalizedIdentifier = normalizeIdentifier(identifier);
            if (normalizedIdentifier) {
                identifiers.add(normalizedIdentifier);
            }
        });

        return identifiers;
    }

    function applyHiddenPluginFilter() {
        if (embedPage.dataset.embedPage !== "list") {
            return;
        }

        let visibleCount = 0;
        document.querySelectorAll("[data-plugin-card]").forEach(function (card) {
            const identifier = normalizeIdentifier(card.dataset.pluginIdentifier);
            const shouldHide = identifier && hiddenPluginIdentifiers.has(identifier);
            card.hidden = shouldHide;
            if (!shouldHide) {
                visibleCount += 1;
            }
        });

        const emptyState = document.querySelector("[data-plugin-directory-empty-state]");
        if (emptyState) {
            emptyState.hidden = visibleCount !== 0;
        }

        scheduleHeightPost();
    }

    function postSelection(slug, identifier) {
        if (!slug) {
            return;
        }

        window.parent.postMessage({
            type: "pb:plugin-selected",
            slug: slug,
            identifier: identifier || null
        }, "*");
    }

    function buildDetailsUrl(slug) {
        const url = new URL("/public/plugins/" + encodeURIComponent(slug), window.location.origin);
        url.searchParams.set("embed", "1");
        return url.toString();
    }

    function handleHostContext(event) {
        const data = event.data;
        if (!data || typeof data !== "object" || data.type !== "btcpay:host-context") {
            return;
        }

        hiddenPluginIdentifiers = normalizeHiddenPluginIdentifiers(data.hiddenPluginIdentifiers);
        applyHostColorMode(data.colorMode);
        applyHiddenPluginFilter();

        const currentSlug = embedPage.dataset.pluginSlug || "";
        const selectedSlug = typeof data.selectedSlug === "string" ? data.selectedSlug : "";

        if (embedPage.dataset.embedPage === "list") {
            return;
        }

        if (!selectedSlug || selectedSlug === currentSlug) {
            return;
        }

        window.location.replace(buildDetailsUrl(selectedSlug));
    }

    postReady();
    scheduleHeightPost();

    if (embedPage.dataset.embedPage === "details") {
        postSelection(embedPage.dataset.pluginSlug || "", embedPage.dataset.pluginIdentifier || "");
    }

    document.querySelectorAll("img").forEach(function (image) {
        image.addEventListener("load", scheduleHeightPost);
        image.addEventListener("error", scheduleHeightPost);
    });

    document.querySelectorAll("a[data-plugin-slug]").forEach(function (link) {
        link.addEventListener("click", function (event) {
            if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0) {
                return;
            }

            event.preventDefault();
            postSelection(link.dataset.pluginSlug || "", link.dataset.pluginIdentifier || "");
        });
    });

    window.addEventListener("message", handleHostContext);
    window.addEventListener("load", scheduleHeightPost);
    window.addEventListener("resize", scheduleHeightPost);

    if (window.ResizeObserver) {
        const resizeObserver = new window.ResizeObserver(scheduleHeightPost);
        resizeObserver.observe(embedPage);
    }

    if (window.MutationObserver) {
        const mutationObserver = new window.MutationObserver(scheduleHeightPost);
        mutationObserver.observe(embedPage, { childList: true, subtree: true, attributes: true });
    }

    if (document.fonts && document.fonts.ready) {
        document.fonts.ready.then(scheduleHeightPost);
    }

    window.setTimeout(scheduleHeightPost, 0);
    window.setTimeout(scheduleHeightPost, 150);
    window.setTimeout(scheduleHeightPost, 500);
})();
