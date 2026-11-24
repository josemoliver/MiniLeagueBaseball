// NOTE: Showing the game on a TV. The controlling page asks the browser for a Cast screen (Presentation API); the
// browser shows the /tv page there. Everything the TV needs travels as JSON messages, over the presentation
// connection and over a BroadcastChannel (which also feeds a /tv page opened by hand, for example one cast with
// Chrome's "Cast tab"):
//   {k:'state', snapshot, detailed}  the game as it stands (the TV draws the field itself from it)
//   {k:'play', script, park, speed}  an animated play or scene, as the Detailed view is showing it
//   {k:'skip'}                       the player skipped the animation being shown
//   {k:'sound', name, delay}         a sound for the TV to play (presentation connection only)
//   {k:'nosound'}                    TV to player: the browser will not let the TV make sound, so play it here
//
// NOTE: untested on real hardware: if the Cast screen is an offscreen tab of this same browser (a one-user-agent
// presentation), the TV page hears BOTH transports, so a 'play' could reach it twice. If that ever shows up as a
// doubled animation, give every message an id (page id + counter) and have listen() drop ids it has seen.
window.mlbCast = (() => {
    const channel = 'BroadcastChannel' in window ? new BroadcastChannel('mlb-tv') : null;
    let host = null;          // .NET side of the controlling page
    let request = null;
    let availability = null;
    let connection = null;
    // WHY: the latest state is kept so a TV that opens late, or reconnects, can be brought up to date by its 'hello'.
    let last = null;
    let lastDetailed = false;
    // WHY: in the Detailed view the TV is only told about a change of view here; every later state rides along when
    // the field is redrawn after a play (see mirrorAnimator), so the score never changes before the animation that earned it.
    let postedDetailed = false;
    let receiverHost = null;  // set on the TV page only; also how a page knows it is the TV and must not echo

    const notify = () => {
        if (!host) return;
        host.invokeMethodAsync('OnCastChanged', !!(availability && availability.value),
            !!connection && connection.state !== 'closed' && connection.state !== 'terminated').catch(() => { });
    };

    const stateMessage = () => ({ k: 'state', snapshot: last, detailed: lastDetailed });

    // WHY: both transports on purpose: the presentation connection reaches a Cast screen, the channel reaches a /tv tab
    // opened by hand in the same browser. Whichever exists gets the message.
    const post = msg => {
        const text = JSON.stringify(msg);
        if (channel) channel.postMessage(text);
        if (connection && connection.state === 'connected') connection.send(text);
    };

    // NOTE: send() throws unless the connection is 'connected' (it is 'connecting' for a moment after start()),
    // hence the state check; onconnect sends again once it is.
    const sendLast = () => {
        if (last !== null && connection && connection.state === 'connected') connection.send(JSON.stringify(stateMessage()));
    };

    // WHY: sound goes to the TV only once the TV has proved, over the presentation connection, that it is there.
    // This device then stays quiet (setSilenced keeps the game's timing, which waits for organ tunes to end). If the
    // TV cannot make sound (a browser blocks audio on a page nobody has clicked) it says so and sound comes back here.
    // A TV tab opened by hand talks over the channel only, so it never silences this device: no double sound on one computer.
    let tvHears = false;
    function hearOnTv(on) {
        tvHears = on;
        if (window.mlbSound && window.mlbSound.setSilenced) window.mlbSound.setSilenced(on);
    }

    function fromTv(data) {
        // NOTE: 'hello' is a plain string (not JSON): the TV page announcing itself, from listen() below.
        if (data === 'hello') { sendLast(); hearOnTv(true); return; }
        let msg;
        try { msg = JSON.parse(data); } catch { return; }
        if (msg.k === 'nosound') hearOnTv(false);
    }

    function attach(c) {
        connection = c;
        c.onconnect = () => { sendLast(); notify(); };
        // WHY: connection === c: a late close event of an old connection must not clear a newer one.
        c.onclose = c.onterminate = () => { if (connection === c) { connection = null; hearOnTv(false); notify(); } };
        c.onmessage = e => fromTv(e.data);
        sendLast();
        notify();
    }

    // WHY: wrapping mlbSound.play catches every source of sound (the animator's cues too) without touching SoundService.
    // NOTE: it must return the original's result: SoundService holds the next computer turn for as long as a tune runs.
    // The sound is still "played" here; silencing is sound.js's job (setSilenced), not this wrapper's.
    function mirrorSound() {
        const s = window.mlbSound;
        if (!s || s.mirroredToTv) return;
        const play = s.play;
        s.mirroredToTv = true;
        s.play = function (name, delay) {
            // WHY: !receiverHost: the TV page's own play (the real one is kept in realPlay) must never be relayed back.
            if (!receiverHost && tvHears && connection && connection.state === 'connected' && !s.isMuted())
                connection.send(JSON.stringify({ k: 'sound', name, delay }));
            return play.apply(this, arguments);
        };
    }
    mirrorSound();

    // NOTE: a /tv tab opened by hand asks for the game with 'hello' over the channel; this answers it.
    if (channel) {
        channel.onmessage = e => {
            if (e.data === 'hello' && last !== null) channel.postMessage(JSON.stringify(stateMessage()));
        };
    }

    // WHY: the Detailed view draws everything through mlbAnimator. Passing its calls on makes the TV play the same
    // plays without GameRoom knowing about the TV, and keeps the two in step when a play is skipped.
    // NOTE: !receiverHost in each wrapper: the TV page runs the same script and its animator is wrapped too; without
    // the check it would echo every play it shows back onto the channel.
    function mirrorAnimator() {
        const a = window.mlbAnimator;
        if (!a || a.mirroredToTv) return;
        const { play, show, skip } = a;
        a.mirroredToTv = true;
        a.play = function (script, park, speed) {
            if (!receiverHost) post({ k: 'play', script, park, speed });
            return play.apply(this, arguments);
        };
        // WHY: the state goes out when the field is drawn again, NOT when GameRoom saves: the save happens before the
        // animation starts, and the TV must not show the score before the play that changed it has been seen.
        a.show = function () {
            if (!receiverHost && last !== null) post({ k: 'state', snapshot: last, detailed: true });
            return show.apply(this, arguments);
        };
        a.skip = function () {
            if (!receiverHost) post({ k: 'skip' });
            return skip.apply(this, arguments);
        };
    }
    mirrorAnimator();

    // ── TV page ──
    let animatorUp = false;
    let waiting = [];
    // WHY: one promise chain, so plays and state changes are applied in the order they were sent (a state must land
    // after the play before it, or the TV shows the new score first).
    let queue = Promise.resolve();
    // WHY: a skip can arrive while its play is still waiting for the canvas. Without remembering it the TV would
    // play the whole animation, out of step with the player's page.
    let queued = 0;           // plays received and not started
    let playing = false;
    let skipAsked = false;

    let realPlay = null;      // the real mlbSound.play, kept for the sounds the player's page sends
    let soundChecked = false;
    let tvLinks = [];         // presentation connections to the player's page

    // WHY: a Cast screen has nobody to click it, and browsers can block audio on a page that has had no interaction.
    // Saying so lets the sound go back to the player's device instead of the game being silent everywhere.
    // NOTE: 400 ms gives the audio context time to resume (or to stay suspended) before it is checked.
    function checkSound() {
        if (soundChecked) return;
        soundChecked = true;
        setTimeout(() => {
            if (window.mlbSound.running && !window.mlbSound.running()) {
                tvLinks.forEach(c => { try { c.send(JSON.stringify({ k: 'nosound' })); } catch { } });
            }
        }, 400);
    }

    // WHY: the 3 s limit: a play must never hold up the queue, and so every later state, if the canvas never comes up.
    const animatorReady = () => animatorUp ? Promise.resolve() : new Promise(resolve => {
        const done = () => { clearTimeout(timer); resolve(); };
        const timer = setTimeout(done, 3000);
        waiting.push(done);
    });

    return {
        // ── Controlling page ──

        /** True when this browser can cast a page to a screen. */
        init(ref) {
            host = ref;
            if (!('PresentationRequest' in window)) return false;
            if (!request) {
                request = new PresentationRequest([new URL('tv', document.baseURI).href]);
                navigator.presentation.defaultRequest = request;           // NOTE: lets the browser's own Cast menu start the same presentation
                request.onconnectionavailable = e => attach(e.connection);
                // NOTE: availability is only a hint for the button; the picker itself works without it.
                request.getAvailability()
                    .then(a => { availability = a; a.onchange = notify; notify(); })
                    .catch(() => { });
            }
            return true;
        },

        /** Opens the browser's screen picker. Resolves to "started", "none", "cancelled" or "error". */
        async start() {
            if (!request) return 'error';
            try {
                attach(await request.start());
                return 'started';
            } catch (e) {
                // NOTE: per the Presentation API, NotFoundError means no screens were found and AbortError that the
                // player closed the picker (not worth an error message).
                if (e && e.name === 'NotFoundError') return 'none';
                if (e && e.name === 'AbortError') return 'cancelled';
                return 'error';
            }
        },

        stop() { if (connection) connection.terminate(); },

        /**
         * Remembers the game as it stands. The Classic view shows it at once; the Detailed view only tells the TV
         * when it changes view, because the TV gets each new state when the field is drawn again after a play.
         */
        send(json, detailed) {
            last = json;
            lastDetailed = !!detailed;
            if (!detailed) { postedDetailed = false; post(stateMessage()); }
            else if (!postedDetailed) { postedDetailed = true; post(stateMessage()); }
        },

        // ── TV page ──

        listen(ref) {
            receiverHost = ref;
            // WHY: listen() can run again on the same page (a new TV component), so every piece of TV state starts clean.
            animatorUp = false;
            waiting = [];
            queue = Promise.resolve();
            queued = 0;
            playing = false;
            skipAsked = false;
            // WHY: the animation's own sound cues stay quiet here: the player's page sends every sound it plays (the
            // cues included) as a message, so each is heard exactly once.
            if (window.mlbSound && !realPlay) { realPlay = window.mlbSound.play; window.mlbSound.play = () => 0; }
            soundChecked = false;
            tvLinks = [];

            const run = fn => { queue = queue.then(fn).catch(() => { }); };
            const deliver = data => {
                if (!receiverHost || typeof data !== 'string' || data === 'hello') return;
                let msg;
                try { msg = JSON.parse(data); } catch { return; }
                if (msg.k === 'state') {
                    // NOTE: a Classic state means no canvas, so a later Detailed one must wait for it to come up again.
                    if (!msg.detailed) animatorUp = false;
                    run(() => receiverHost && receiverHost.invokeMethodAsync('OnState', msg.snapshot, !!msg.detailed));
                } else if (msg.k === 'play') {
                    queued++;
                    run(async () => {
                        await animatorReady();
                        queued--;
                        playing = true;
                        const shown = window.mlbAnimator.play(msg.script, msg.park, msg.speed);
                        if (skipAsked) { skipAsked = false; window.mlbAnimator.skip(); }
                        try { await shown; } finally { playing = false; }
                    });
                } else if (msg.k === 'sound' && realPlay) {
                    realPlay.call(window.mlbSound, msg.name, msg.delay || 0);
                    checkSound();
                } else if (msg.k === 'skip' && window.mlbAnimator) {
                    // WHY: skip acts at once only if a play is running; one for a play that has not started is kept
                    // until it does (see skipAsked). queued > 0: a skip with nothing coming must not hit the next, unrelated play.
                    if (playing) window.mlbAnimator.skip();
                    else if (queued > 0) skipAsked = true;
                }
            };
            if (channel) {
                channel.onmessage = e => deliver(e.data);
                channel.postMessage('hello');
            }
            // NOTE: navigator.presentation.receiver exists only in a page that was opened by a presentation.
            const receiver = navigator.presentation && navigator.presentation.receiver;
            if (receiver) {
                const hook = c => { tvLinks.push(c); c.onmessage = e => deliver(e.data); c.send('hello'); };
                receiver.connectionList.then(list => {
                    list.connections.forEach(hook);
                    list.onconnectionavailable = e => hook(e.connection);
                });
            }
        },

        /** The TV's canvas is attached to the animator: plays that were waiting can start. */
        animatorUp() {
            animatorUp = true;
            const ready = waiting;
            waiting = [];
            ready.forEach(done => done());
        },

        unlisten() { receiverHost = null; },
    };
})();
