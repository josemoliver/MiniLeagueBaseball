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
    function showUpdate(worker) {
        const banner = document.getElementById('update-banner');
        if (!banner) return;
        banner.hidden = false;
        banner.querySelector('button').addEventListener('click', () => {
            let reloaded = false;
            navigator.serviceWorker.addEventListener('controllerchange', () => {
                if (!reloaded) { reloaded = true; window.location.reload(); }
            });
            worker.postMessage({ type: 'SKIP_WAITING' });
        });
    }

    function register() {
        if (!('serviceWorker' in navigator)) return;
        navigator.serviceWorker.register('service-worker.js').then(reg => {
            // an update was already waiting when the page opened
            if (reg.waiting && navigator.serviceWorker.controller) showUpdate(reg.waiting);
            reg.addEventListener('updatefound', () => {
                const worker = reg.installing;
                if (!worker) return;
                worker.addEventListener('statechange', () => {
                    if (worker.state === 'installed' && navigator.serviceWorker.controller) showUpdate(worker);
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
