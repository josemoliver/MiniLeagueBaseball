// Game keyboard shortcuts. The decision panel registers while it is on screen and decides what each key means.
window.mlbKeys = (() => {
    let handler = null;
    let hintHandler = null;
    // 0 and - are the two shift alignments (digits 1-9 are taken).
    const wanted = /^(Digit[0-9]|Numpad[0-9]|Minus|NumpadSubtract|Enter|NumpadEnter|KeyP|KeyS|KeyB|KeyH)$/;

    function typing(el) {
        return el && (/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) || el.isContentEditable);
    }

    return {
        register(ref) {
            this.unregister();
            handler = e => {
                if (e.repeat || e.ctrlKey || e.altKey || e.metaKey) return;
                if (!wanted.test(e.code)) return;
                if (typing(e.target)) return;
                if (document.querySelector('.mud-dialog')) return;   // a dialog is open: leave the keys to it
                e.preventDefault();                                   // so Enter does not also "click" a focused button
                ref.invokeMethodAsync('OnKey', e.code, e.shiftKey);
            };
            document.addEventListener('keydown', handler);
        },
        unregister() {
            if (handler) document.removeEventListener('keydown', handler);
            handler = null;
        },
        focusHint() {
            document.getElementById('play-hint-button')?.focus();
        },
        registerHint(ref) {
            this.unregisterHint();
            hintHandler = e => {
                if (e.code !== 'KeyH' || e.repeat || e.ctrlKey || e.altKey || e.metaKey || typing(e.target)) return;
                e.preventDefault();
                ref.invokeMethodAsync('CloseFromKey');
            };
            document.addEventListener('keydown', hintHandler);
        },
        unregisterHint() {
            if (hintHandler) document.removeEventListener('keydown', hintHandler);
            hintHandler = null;
        },
    };
})();
