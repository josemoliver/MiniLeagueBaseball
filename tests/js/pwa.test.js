// Tests for the installable-app pieces: the offline service worker and the install/update script.
// Run with: node --test tests/js/pwa.test.js
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const WWWROOT = path.join(__dirname, '..', '..', 'src', 'MiniLeague.Web', 'wwwroot');
const read = name => fs.readFileSync(path.join(WWWROOT, name), 'utf8');

// ── The service worker ───────────────────────────────────────────────────────

const ORIGIN = 'https://minileague.example';
const ASSETS = [
    { url: 'index.html', hash: 'sha256-index' },
    { url: 'manifest.webmanifest', hash: 'sha256-manifest' },
    { url: '_framework/dotnet.native.abc123.wasm', hash: 'sha256-wasm' },
    { url: '_framework/MiniLeague.Web.dll', hash: 'sha256-dll' },
    { url: 'css/site.css', hash: 'sha256-css' },
    { url: 'js/sound.js', hash: 'sha256-sound' },
    { url: 'data/teams.json', hash: 'sha256-teams' },
    { url: 'data/ATTRIBUTION.md', hash: 'sha256-md' },
    { url: 'images/minileague-baseball.webp', hash: 'sha256-art' },
    { url: 'icons/icon-192.png', hash: 'sha256-icon' },
    { url: '_content/MudBlazor/MudBlazor.min.js.map', hash: 'sha256-map' },
    { url: 'service-worker.js', hash: 'sha256-sw' },
];

function loadWorker({ version = 'v1', preexistingCaches = {}, network = null } = {}) {
    const listeners = {};
    const stores = new Map(Object.entries(preexistingCaches).map(([name, files]) => [name, new Map(Object.entries(files))]));
    const fetched = [];
    const caches = {
        open: async name => {
            if (!stores.has(name)) stores.set(name, new Map());
            const store = stores.get(name);
            return {
                addAll: async requests => requests.forEach(r => store.set(r.url, { from: r.url.replace(ORIGIN + '/', ''), integrity: r.integrity, cache: r.cache })),
                match: async request => {
                    const key = typeof request === 'string' ? new URL(request, ORIGIN + '/').href : request.url;
                    return store.get(key);
                },
            };
        },
        keys: async () => [...stores.keys()],
        delete: async name => stores.delete(name),
    };
    class FakeRequest { constructor(url, init = {}) { this.url = new URL(url, ORIGIN + '/').href; Object.assign(this, init); } }

    const sandbox = {
        console: { info() {} },
        URL,
        Request: FakeRequest,
        caches,
        fetch: async request => { fetched.push(request.url); return network ? network(request) : { from: 'network', url: request.url }; },
        self: {
            origin: ORIGIN,
            addEventListener: (type, fn) => { listeners[type] = fn; },
            importScripts: () => { sandbox.self.assetsManifest = { version, assets: ASSETS }; },
            skipWaiting: () => { sandbox.skipped = true; },
        },
        skipped: false,
    };
    vm.runInNewContext(read('service-worker.published.js'), sandbox);

    const run = async (type, event) => {
        let pending;
        listeners[type]({ ...event, waitUntil: p => { pending = p; }, respondWith: p => { pending = p; } });
        return pending;
    };
    return { run, stores, fetched, sandbox, listeners };
}

test('the worker listens for install, activate, fetch and update messages', () => {
    const { listeners } = loadWorker();
    for (const type of ['install', 'activate', 'fetch', 'message'])
        assert.equal(typeof listeners[type], 'function', type);
});

test('install caches the game files named in the build manifest, with integrity checks', async () => {
    const { run, stores } = loadWorker({ version: 'v7' });
    await run('install', {});

    const cache = stores.get('offline-cache-v7');
    assert.ok(cache, 'the cache is named after the build');
    const cached = [...cache.values()].map(v => v.from);

    for (const file of ['index.html', 'manifest.webmanifest', '_framework/dotnet.native.abc123.wasm',
        '_framework/MiniLeague.Web.dll', 'css/site.css', 'js/sound.js', 'data/teams.json',
        'images/minileague-baseball.webp', 'icons/icon-192.png'])
        assert.ok(cached.includes(file), `${file} should work offline`);

    assert.ok(![...cache.values()].some(v => v.from === 'service-worker.js'), 'the worker never caches itself');
    assert.ok(!cached.some(f => f.endsWith('.map')), 'source maps are not worth caching');
    assert.ok(!cached.some(f => f.endsWith('.md')));
    for (const entry of cache.values()) {
        assert.ok(entry.integrity.startsWith('sha256-'), 'each file is verified against its hash');
        assert.equal(entry.cache, 'no-cache');
    }
});

test('activate removes the caches of older builds, and only ours', async () => {
    const { run, stores } = loadWorker({
        version: 'v2',
        preexistingCaches: { 'offline-cache-v1': {}, 'offline-cache-v2': {}, 'somebody-elses-cache': {} },
    });
    await run('activate', {});

    assert.deepEqual([...stores.keys()].sort(), ['offline-cache-v2', 'somebody-elses-cache']);
});

test('fetch serves cached files without touching the network', async () => {
    const { run, fetched } = loadWorker({ preexistingCaches: { 'offline-cache-v1': {} } });
    await run('install', {});

    const response = await run('fetch', { request: new (class { constructor() { this.url = ORIGIN + '/css/site.css'; this.method = 'GET'; this.mode = 'no-cors'; } })() });

    assert.equal(response.from, 'css/site.css');
    assert.deepEqual(fetched, []);
});

test('opening any page address while offline serves the app shell (so /game and /shortcuts work)', async () => {
    const { run, fetched } = loadWorker();
    await run('install', {});

    for (const url of ['/game', '/shortcuts', '/']) {
        const response = await run('fetch', { request: { url: ORIGIN + url, method: 'GET', mode: 'navigate' } });
        assert.equal(response.from, 'index.html', url);
    }
    assert.deepEqual(fetched, []);
});

test('a navigation to a real file is not turned into the app shell', async () => {
    const { run } = loadWorker();
    await run('install', {});

    const response = await run('fetch', { request: { url: ORIGIN + '/manifest.webmanifest', method: 'GET', mode: 'navigate' } });

    assert.equal(response.from, 'manifest.webmanifest');
});

test('anything not cached, and anything that is not a GET, goes to the network', async () => {
    const { run, fetched } = loadWorker();
    await run('install', {});

    const post = await run('fetch', { request: { url: ORIGIN + '/css/site.css', method: 'POST', mode: 'cors' } });
    const other = await run('fetch', { request: { url: 'https://fonts.googleapis.com/css?family=Roboto', method: 'GET', mode: 'cors' } });

    assert.equal(post.from, 'network');
    assert.equal(other.from, 'network');
    assert.equal(fetched.length, 2);
});

test('the player can let a waiting update take over', () => {
    const { listeners, sandbox } = loadWorker();

    listeners.message({ data: { type: 'SOMETHING_ELSE' } });
    assert.equal(sandbox.skipped, false);
    listeners.message({ data: { type: 'SKIP_WAITING' } });
    assert.equal(sandbox.skipped, true);
});

test('a new build installs beside the old one instead of replacing it mid-game', async () => {
    const old = loadWorker({ version: 'v1' });
    await old.run('install', {});
    // the new worker has a different cache name, so the running game keeps the files it started with
    const next = loadWorker({ version: 'v2', preexistingCaches: { 'offline-cache-v1': {} } });
    await next.run('install', {});

    assert.ok(next.stores.has('offline-cache-v1') && next.stores.has('offline-cache-v2'));
});

// ── The install and update script ────────────────────────────────────────────

function loadPwa({ standalone = false, serviceWorker = null } = {}) {
    const events = new Map();
    const elements = {};
    const banner = {
        hidden: true,
        button: { handlers: [], addEventListener(t, fn) { this.handlers.push(fn); } },
        querySelector() { return this.button; },
    };
    elements['update-banner'] = banner;

    const win = {
        addEventListener: (t, fn) => { events.set(t, [...(events.get(t) || []), fn]); },
        matchMedia: q => ({ matches: standalone && q.includes('standalone') }),
        navigator: { standalone: false },
        location: { reloaded: 0, reload() { this.reloaded++; } },
    };
    const swEvents = new Map();
    const nav = serviceWorker && {
        controller: 'controller' in serviceWorker ? serviceWorker.controller : {},
        addEventListener: (t, fn) => { swEvents.set(t, [...(swEvents.get(t) || []), fn]); },
        register: async file => { (serviceWorker.registered ||= []).push(file); return serviceWorker.registration; },
    };
    const sandbox = {
        window: win,
        document: { readyState: 'complete', getElementById: id => elements[id] || null },
        navigator: nav ? { serviceWorker: nav } : {},
        setTimeout,
    };
    sandbox.window.document = sandbox.document;
    sandbox.window.location = win.location;
    vm.runInNewContext(read('js/pwa.js'), { ...sandbox, window: win });
    const fire = (type, e = {}) => (events.get(type) || []).forEach(fn => fn(e));
    return { pwa: win.mlbPwa, fire, banner, win, swEvents };
}

const settle = () => new Promise(r => setImmediate(r));

test('the app can be installed once the browser offers it, and not before', async () => {
    const { pwa, fire } = loadPwa();
    assert.equal(pwa.canInstall(), false);

    let prevented = false;
    fire('beforeinstallprompt', { preventDefault() { prevented = true; } });

    assert.equal(prevented, true, 'the browser banner is held back for our own menu item');
    assert.equal(pwa.canInstall(), true);
});

test('choosing Install shows the browser prompt and reports the answer', async () => {
    const { pwa, fire } = loadPwa();
    let shown = 0;
    fire('beforeinstallprompt', { preventDefault() {}, prompt() { shown++; }, userChoice: Promise.resolve({ outcome: 'accepted' }) });

    const outcome = await pwa.install();

    assert.equal(outcome, 'accepted');
    assert.equal(shown, 1);
    assert.equal(pwa.canInstall(), false, 'the prompt can only be used once');
    assert.equal(await pwa.install(), 'unavailable');
});

test('an app that is already installed does not offer to install again', () => {
    const { pwa, fire } = loadPwa({ standalone: true });
    fire('beforeinstallprompt', { preventDefault() {}, prompt() {}, userChoice: Promise.resolve({ outcome: 'dismissed' }) });

    assert.equal(pwa.isInstalled(), true);
    assert.equal(pwa.canInstall(), false);
});

test('installing from the browser menu clears the offer', () => {
    const { pwa, fire } = loadPwa();
    fire('beforeinstallprompt', { preventDefault() {}, prompt() {}, userChoice: Promise.resolve({}) });
    assert.equal(pwa.canInstall(), true);

    fire('appinstalled');

    assert.equal(pwa.canInstall(), false);
});

test('listeners hear when the install offer changes', () => {
    const { pwa, fire } = loadPwa();
    let calls = 0;
    const stop = pwa.onChange(() => calls++);

    fire('beforeinstallprompt', { preventDefault() {} });
    stop();
    fire('appinstalled');

    assert.equal(calls, 1);
});

test('the service worker is registered when the page loads', async () => {
    const serviceWorker = { registration: { addEventListener() {} } };
    loadPwa({ serviceWorker });
    await settle();

    assert.deepEqual(serviceWorker.registered, ['service-worker.js']);
});

test('when a new version has finished installing, the update banner appears and reloads on request', async () => {
    const posted = [];
    const worker = { state: 'installing', postMessage: m => posted.push(m), handlers: [], addEventListener(t, fn) { this.handlers.push(fn); } };
    const registration = {
        waiting: null,
        installing: worker,
        handlers: [],
        addEventListener(t, fn) { this.handlers.push(fn); },
    };
    const { banner, swEvents, win } = loadPwa({ serviceWorker: { registration } });
    await settle();

    assert.equal(banner.hidden, true);
    registration.handlers.forEach(fn => fn());          // updatefound
    worker.state = 'installed';
    worker.handlers.forEach(fn => fn());                // statechange
    assert.equal(banner.hidden, false, 'the banner is shown');

    banner.button.handlers.forEach(fn => fn());         // the player taps Reload
    assert.deepEqual(JSON.parse(JSON.stringify(posted)), [{ type: 'SKIP_WAITING' }]);   // (round-trip: the objects come from the script's own VM)
    (swEvents.get('controllerchange') || []).forEach(fn => fn());
    assert.equal(win.location.reloaded, 1, 'and the page reloads onto the new version');
});

test('an update that was already waiting when the page opened is offered straight away', async () => {
    const waiting = { postMessage() {} };
    const { banner } = loadPwa({ serviceWorker: { registration: { waiting, addEventListener() {} } } });
    await settle();

    assert.equal(banner.hidden, false);
});

test('the very first install shows no update banner', async () => {
    const worker = { state: 'installing', postMessage() {}, handlers: [], addEventListener(t, fn) { this.handlers.push(fn); } };
    const registration = { waiting: null, installing: worker, handlers: [], addEventListener(t, fn) { this.handlers.push(fn); } };
    const { banner } = loadPwa({ serviceWorker: { registration, controller: null } });
    await settle();

    registration.handlers.forEach(fn => fn());
    worker.state = 'installed';
    worker.handlers.forEach(fn => fn());

    assert.equal(banner.hidden, true, 'nothing is being replaced, so there is nothing to announce');
});

test('a browser without service workers just plays the game', () => {
    const { pwa } = loadPwa();   // no serviceWorker support

    assert.equal(pwa.canInstall(), false);
});
