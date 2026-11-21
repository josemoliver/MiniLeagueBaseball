// Game keyboard shortcuts. The decision panel registers while it is on screen and decides what each key means.
window.mlbKeys = (() => {
    let handler = null;
    const wanted = /^(Digit[1-9]|Numpad[1-9]|Enter|NumpadEnter|KeyP|KeyS|KeyB)$/;

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
    };
})();
