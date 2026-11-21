// Thin localStorage wrapper. Storage can be missing or blocked (private windows), so every call is guarded.
window.mlbStorage = {
    get(key) {
        try { return localStorage.getItem(key); } catch { return null; }
    },
    set(key, value) {
        try { localStorage.setItem(key, value); return true; } catch { return false; }
    },
    remove(key) {
        try { localStorage.removeItem(key); } catch { }
    },
};
