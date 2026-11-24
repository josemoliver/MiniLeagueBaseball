// In development there is no offline cache: a service worker that cached files would show stale builds.
// The real one (service-worker.published.js) replaces this when the app is published.
self.addEventListener('fetch', () => { });
