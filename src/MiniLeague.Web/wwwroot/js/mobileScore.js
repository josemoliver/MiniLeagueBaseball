window.mlbMobileScore = (() => {
    let observer;

    return {
        observe(score, strip) {
            observer?.disconnect();
            observer = new IntersectionObserver(([entry]) => {
                // WHY: the compact score takes over only after the full scoreboard passes behind the app bar.
                strip.classList.toggle("visible", !entry.isIntersecting && entry.boundingClientRect.bottom <= 48);
            }, { rootMargin: "-48px 0px 0px 0px" });
            observer.observe(score);
        },
        disconnect() {
            observer?.disconnect();
            observer = undefined;
        }
    };
})();
