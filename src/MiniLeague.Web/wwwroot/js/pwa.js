// Installing the app, and keeping an installed copy up to date.
window.mlbPwa = (() => {
    let installPrompt = null;      // the browser's "install" prompt, saved until the player asks for it
    const listeners = new Set();
    const notify = () => listeners.forEach(f => { try { f(); } catch { } });

    const standalone = () =>
        (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) || window.navigator.standalone === true;

    window.addEventListener('beforeinstallprompt', e => {
        e.preventDefault();        // keep it for our own menu item
        installPrompt = e;
        notify();
    });
    window.addEventListener('appinstalled', () => { installPrompt = null; notify(); });

    // ── The service worker, and the "update available" banner ──
    let bannerWired = false;
    function showUpdate(worker, reg) {
        const banner = document.getElementById('update-banner');
        if (!banner) return;
        banner.hidden = false;
        // WHY: showUpdate can fire twice (waiting at page open, then statechange); wire the click once.
        if (bannerWired) return;
        bannerWired = true;
        banner.querySelector('button').addEventListener('click', () => {
            let reloaded = false;
            const reload = () => { if (!reloaded) { reloaded = true; window.location.reload(); } };
            navigator.serviceWorker.addEventListener('controllerchange', reload);
            // WHY: the worker captured when the banner appeared turns redundant if a newer deploy landed while
            // the page stayed open; posting to it is silently ignored. Ask whichever worker is waiting now.
            const target = (reg && reg.waiting) || worker;
            target.postMessage({ type: 'SKIP_WAITING' });
            // HACK: if no controllerchange arrives (stale or missing worker), reload anyway: the reload
            // itself picks up the newest worker, so the button can never be a dead click.
            setTimeout(reload, 2000);
        });
    }

    function register() {
        if (!('serviceWorker' in navigator)) return;
        navigator.serviceWorker.register('service-worker.js').then(reg => {
            // an update was already waiting when the page opened
            if (reg.waiting && navigator.serviceWorker.controller) showUpdate(reg.waiting, reg);
            reg.addEventListener('updatefound', () => {
                const worker = reg.installing;
                if (!worker) return;
                worker.addEventListener('statechange', () => {
                    if (worker.state === 'installed' && navigator.serviceWorker.controller) showUpdate(worker, reg);
                });
            });
        }).catch(() => { /* offline support is a bonus: the game still runs without it */ });
    }

    if (document.readyState === 'complete') register();
    else window.addEventListener('load', register);

    return {
        /** True when the browser is willing to install the app and it is not installed already. */
        canInstall: () => !!installPrompt && !standalone(),
        isInstalled: standalone,
        /** Shows the browser's install dialog. Resolves to "accepted", "dismissed" or "unavailable". */
        async install() {
            if (!installPrompt) return 'unavailable';
            const prompt = installPrompt;
            installPrompt = null;
            prompt.prompt();
            const choice = await prompt.userChoice;
            notify();
            return choice.outcome;
        },
        onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    };
})();
