// Clipboard and print helpers for the box score.
window.mlbExport = {
    async copy(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },
    print() {
        window.print();
    },
};
