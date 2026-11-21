// Synthesized sound effects (Web Audio API) — no audio files required.
window.mlbSound = (() => {
    let ctx = null;
    let muted = false;
    try { muted = localStorage.getItem('mlb-muted') === '1'; } catch { }

    function ac() {
        if (!ctx) {
            const C = window.AudioContext || window.webkitAudioContext;
            if (!C) return null;
            ctx = new C();
        }
        if (ctx.state === 'suspended') ctx.resume();
        return ctx;
    }

    function noiseBuffer(c, seconds) {
        const buf = c.createBuffer(1, Math.max(1, Math.floor(c.sampleRate * seconds)), c.sampleRate);
        const d = buf.getChannelData(0);
        for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
        return buf;
    }

    // Gain node with attack/decay envelope, connected to output.
    function env(c, t, peak, attack, total) {
        const g = c.createGain();
        g.gain.setValueAtTime(0.0001, t);
        g.gain.exponentialRampToValueAtTime(peak, t + attack);
        g.gain.exponentialRampToValueAtTime(0.0001, t + total);
        g.connect(c.destination);
        return g;
    }

    function tone(c, t, freq, endFreq, dur, peak, type = 'sine') {
        const o = c.createOscillator();
        o.type = type;
        o.frequency.setValueAtTime(freq, t);
        if (endFreq !== freq) o.frequency.exponentialRampToValueAtTime(endFreq, t + dur);
        o.connect(env(c, t, peak, 0.005, dur));
        o.start(t);
        o.stop(t + dur + 0.02);
    }

    function noise(c, t, dur, peak, filterType, freq, q = 1, attack = 0.003) {
        const src = c.createBufferSource();
        src.buffer = noiseBuffer(c, dur + 0.05);
        const f = c.createBiquadFilter();
        f.type = filterType;
        f.frequency.value = freq;
        f.Q.value = q;
        src.connect(f);
        f.connect(env(c, t, peak, attack, dur));
        src.start(t);
        src.stop(t + dur + 0.05);
    }

    const crack = (c, t, power = 1) => {
        noise(c, t, 0.06, 0.9 * power, 'highpass', 1800);
        tone(c, t, 1100, 300, 0.07, 0.5 * power, 'triangle');
    };
    const thud = (c, t) => {
        tone(c, t, 140, 50, 0.16, 0.7);
        noise(c, t, 0.08, 0.25, 'lowpass', 400);
    };
    const mittPop = (c, t) => {
        noise(c, t, 0.07, 0.8, 'bandpass', 900, 2);
        tone(c, t, 220, 90, 0.09, 0.4);
    };
    // Crowd: band-passed noise with a swelling envelope and a fast shimmer.
    const cheer = (c, t, dur, level) => {
        const src = c.createBufferSource();
        src.buffer = noiseBuffer(c, dur + 0.1);
        const f = c.createBiquadFilter();
        f.type = 'bandpass';
        f.frequency.setValueAtTime(700, t);
        f.frequency.linearRampToValueAtTime(1500, t + dur * 0.35);
        f.frequency.linearRampToValueAtTime(900, t + dur);
        f.Q.value = 0.6;
        const g = c.createGain();
        g.gain.setValueAtTime(0.0001, t);
        g.gain.linearRampToValueAtTime(level, t + dur * 0.25);
        g.gain.linearRampToValueAtTime(level * 0.7, t + dur * 0.6);
        g.gain.linearRampToValueAtTime(0.0001, t + dur);
        const lfo = c.createOscillator();
        const lfoGain = c.createGain();
        lfo.frequency.value = 7;
        lfoGain.gain.value = level * 0.25;
        lfo.connect(lfoGain);
        lfoGain.connect(g.gain);
        src.connect(f);
        f.connect(g);
        g.connect(c.destination);
        src.start(t); lfo.start(t);
        src.stop(t + dur + 0.1); lfo.stop(t + dur + 0.1);
    };
    const arpeggio = (c, t, notes, step, peak = 0.3) =>
        notes.forEach((n, i) => tone(c, t + i * step, n, n, step * 1.6, peak, 'square'));

    // ── Ballpark organ ───────────────────────────────────────────────────────
    // A drawbar-style organ: stacked sine "footages", a Leslie-like wobble, key click and a little hall reverb.

    // [harmonic of the played note, level]: 16', 8', 4', 2 2/3', 2', 1 1/3', 1'
    const DRAWBARS = [[0.5, 0.5], [1, 1.0], [2, 0.8], [3, 0.35], [4, 0.45], [6, 0.12], [8, 0.2]];

    function hall(c) {
        const len = Math.floor(c.sampleRate * 1.4);
        const buf = c.createBuffer(2, len, c.sampleRate);
        for (let ch = 0; ch < 2; ch++) {
            const d = buf.getChannelData(ch);
            for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 2.6);
        }
        const conv = c.createConvolver();
        conv.buffer = buf;
        return conv;
    }

    // Everything the organ plays goes through one bus: wobble, then dry plus reverb.
    // Starting a new tune (or muting) fades the previous one out.
    let currentOrgan = null;

    function stopOrgan() {
        if (!currentOrgan) return;
        const { c, master } = currentOrgan;
        currentOrgan = null;
        try {
            master.gain.cancelScheduledValues(c.currentTime);
            master.gain.setTargetAtTime(0, c.currentTime, 0.04);
        } catch { }
    }

    function organBus(c, t, seconds) {
        stopOrgan();
        const input = c.createGain();
        const master = c.createGain();
        master.gain.value = 1;
        input.connect(master);
        currentOrgan = { c, master };
        const wobble = c.createGain();
        wobble.gain.value = 0.9;
        const lfo = c.createOscillator();
        const depth = c.createGain();
        lfo.frequency.value = 6.4;
        depth.gain.value = 0.1;
        lfo.connect(depth);
        depth.connect(wobble.gain);
        lfo.start(t);
        lfo.stop(t + seconds + 2);

        const reverb = hall(c);
        const wet = c.createGain();
        wet.gain.value = 0.28;
        master.connect(wobble);
        wobble.connect(c.destination);     // dry
        wobble.connect(reverb);            // wet: through the hall and out
        reverb.connect(wet);
        wet.connect(c.destination);
        return { input };
    }

    function organNote(c, out, t, freq, dur, vel = 1) {
        const level = 0.14 * vel;
        const g = c.createGain();
        g.gain.setValueAtTime(0.0001, t);
        g.gain.exponentialRampToValueAtTime(level, t + 0.012);
        g.gain.setValueAtTime(level, t + Math.max(dur - 0.05, 0.02));
        g.gain.exponentialRampToValueAtTime(0.0001, t + dur + 0.07);
        g.connect(out);
        for (const [ratio, amount] of DRAWBARS) {
            const o = c.createOscillator();
            const og = c.createGain();
            o.type = 'sine';
            o.frequency.value = freq * ratio;
            og.gain.value = amount / DRAWBARS.length * 2.2;
            o.connect(og);
            og.connect(g);
            o.start(t);
            o.stop(t + dur + 0.1);
        }
        // key click
        const src = c.createBufferSource();
        src.buffer = noiseBuffer(c, 0.03);
        const f = c.createBiquadFilter();
        f.type = 'highpass';
        f.frequency.value = 3000;
        const cg = c.createGain();
        cg.gain.setValueAtTime(0.05 * vel, t);
        cg.gain.exponentialRampToValueAtTime(0.0001, t + 0.025);
        src.connect(f);
        f.connect(cg);
        cg.connect(out);
        src.start(t);
        src.stop(t + 0.04);
    }

    // The classic ballpark "Charge!" fanfare, with a rising run-up and a big final chord.
    const NOTE = { C3: 130.81, C4: 261.63, E4: 329.63, G4: 392.0, C5: 523.25, E5: 659.25, G5: 783.99 };
    const BEAT = 0.4;
    const THEME = [
        // run-up
        ['C4', 0.0, 0.3], ['E4', 0.3, 0.3], ['G4', 0.6, 0.3], ['C5', 0.9, 0.45],
        // da-da-da-DA... da-DAAAH!
        ['G4', 1.6, 0.25], ['C5', 1.85, 0.25], ['E5', 2.1, 0.25], ['G5', 2.35, 0.5],
        ['E5', 2.85, 0.25], ['G5', 3.1, 1.4],
        // closing chord
        ['C3', 4.7, 2.2], ['C4', 4.7, 2.2], ['E4', 4.7, 2.2], ['G4', 4.7, 2.2], ['C5', 4.7, 2.2], ['E5', 4.7, 2.2],
    ];
    const THEME_SECONDS = 4.7 * BEAT + 2.2 * BEAT;

    function organTheme(c, t) {
        const bus = organBus(c, t, THEME_SECONDS);
        for (const [note, start, beats] of THEME)
            organNote(c, bus.input, t + start * BEAT, NOTE[note], beats * BEAT);
    }

    // ── Ballpark tunes ───────────────────────────────────────────────────────
    // Public-domain melodies kept as ABC notation (a plain-text music format) so the notes can be checked
    // against the sources. Lengths are in units of the tune's L: field; "unit" is seconds per unit.

    // Take Me Out to the Ball Game (Albert Von Tilzer, 1908). The chorus: lines 1-4 ("...I don't care if I
    // never get back") and the closing bars. Transcription: Erich Rickheit's traditional-tune collection.
    const TAKE_ME_OUT = `
        M:3/4
        L:1/4
        K:D
        D2 d| B A F| A3| E3| D2 d| B A F| A3-| A2 z| B ^A B| F G A| B2 G| E3| B2 B| B c d| e c B| A F E|
        B3| c3| d3-| d z2|`;

    // La Raspa, the Mexican Hat Dance (traditional). Parts A and B. Transcription: John Chambers.
    const LA_RASPA = `
        M:6/8
        L:1/8
        K:C
        z2G | c2G c2G | cz2 z2G | cdc B2c | dz2 z2G | B2G B2G | Bz2 z2G | BcB A2B | cz2 |
        e2g | ccc cBc | dz2 d2f | BBB BAB | cz2 e2g | ccc cBc | dz2 d2f | BBB BAB | cz2 |`;

    // Hail to the Chief (James Sanderson, 1812). The first strain. Transcription: John Chambers.
    const HAIL_TO_THE_CHIEF = `
        M:4/4
        L:1/8
        K:D
        A2 (B>c) d2 (c>B) | (A>B) (AF) E2 D2 | A2 (d>e) f2 (e>d) | (e>d) ef e4 |
        A2 (B>c) d2 (c>B) | (A>B) (AF) E2 D2 | A2 (d>c) (Bd) AF | A2 (d>e) d4 ||`;

    const TUNES = {
        stretch:  { abc: TAKE_ME_OUT,       unit: 0.33, shift: -2, vel: 0.9 },   // played in C
        rally:    { abc: LA_RASPA,          unit: 0.15, shift: 0,  vel: 0.9 },
        entrance: { abc: HAIL_TO_THE_CHIEF, unit: 0.14, shift: 0,  vel: 0.9 },
    };

    // Which sharps or flats each key signature carries (just the keys used above, plus a few common ones).
    const KEY_ALTERATIONS = {
        C: {}, G: { F: 1 }, D: { F: 1, C: 1 }, A: { F: 1, C: 1, G: 1 },
        F: { B: -1 }, Bb: { B: -1, E: -1 }, Eb: { B: -1, E: -1, A: -1 },
    };
    const SEMITONES = { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 };

    // Reads ABC notation into [{ midi, start, dur }] (null midi for rests), in units of L.
    // Handles the parts these tunes use: accidentals, octave marks, lengths, ties, rests, broken
    // rhythm (>), and ignores bar lines, repeats, slurs, chord symbols and decorations.
    function parseAbc(abc, shift = 0) {
        let key = 'C';
        let body = '';
        for (const raw of abc.split('\n')) {
            const line = raw.trim();
            const k = /^K:\s*([A-G][b#]?)/.exec(line);
            if (k) { key = k[1]; body = ''; continue; }
            if (/^[A-Za-z]:/.test(line)) continue;      // other header fields (M:, L:, ...)
            body += line + ' ';
        }
        const signature = KEY_ALTERATIONS[key] || {};
        const notes = [];
        let t = 0, tie = false, broken = 0, barAlterations = {};

        for (let i = 0; i < body.length;) {
            const ch = body[i];
            if (ch === '"') { i = body.indexOf('"', i + 1) + 1 || body.length; continue; }
            if (ch === '{') { i = body.indexOf('}', i) + 1 || body.length; continue; }
            if (ch === '|') { barAlterations = {}; i++; continue; }

            let accidental = null;
            if (/[\^_=]/.test(ch)) {
                let j = i;
                while (/[\^_=]/.test(body[j])) j++;
                const run = body.slice(i, j);
                accidental = run[0] === '=' ? 0 : (run[0] === '^' ? 1 : -1) * run.length;
                i = j;
            }

            const letter = body[i];
            const isNote = /[A-Ga-g]/.test(letter || '');
            const isRest = letter === 'z' || letter === 'Z';
            if (!isNote && !isRest) { i++; continue; }   // bars, repeats, slurs, spaces, decorations
            i++;

            let octave = isNote && letter === letter.toLowerCase() ? 5 : 4;
            while (body[i] === "'" || body[i] === ',') { octave += body[i] === "'" ? 1 : -1; i++; }

            let digits = '';
            while (/[0-9]/.test(body[i] || '')) digits += body[i++];
            let dur = digits ? parseInt(digits, 10) : 1;
            if (body[i] === '/') {
                let slashes = 0;
                while (body[i] === '/') { slashes++; i++; }
                let den = '';
                while (/[0-9]/.test(body[i] || '')) den += body[i++];
                dur = dur / (den ? parseInt(den, 10) : Math.pow(2, slashes));
            }

            let tieNext = false, brokenNext = 0;
            for (;;) {
                if (body[i] === '-') { tieNext = true; i++; }
                else if (body[i] === '>') { brokenNext = 1; i++; }
                else if (body[i] === '<') { brokenNext = -1; i++; }
                else break;
            }

            // broken rhythm: a>b lengthens a and shortens b by half of a's length, and a<b the reverse
            if (broken) { dur *= broken > 0 ? 0.5 : 1.5; broken = 0; }
            if (brokenNext) {
                dur *= brokenNext > 0 ? 1.5 : 0.5;
                broken = brokenNext;
            }

            let midi = null;
            if (isNote) {
                const name = letter.toUpperCase();
                const id = name + octave;
                if (accidental !== null) barAlterations[id] = accidental;
                const alter = id in barAlterations ? barAlterations[id] : (signature[name] || 0);
                midi = 12 * (octave + 1) + SEMITONES[name] + alter + shift;
            }

            const last = notes[notes.length - 1];
            if (tie && midi !== null && last && last.midi === midi) last.dur += dur;
            else notes.push({ midi, start: t, dur });
            t += dur;
            tie = tieNext;
        }
        return notes;
    }

    const parsedTunes = {};
    function tune(name) {
        if (!parsedTunes[name]) parsedTunes[name] = parseAbc(TUNES[name].abc, TUNES[name].shift);
        return parsedTunes[name];
    }

    const midiToHz = m => 440 * Math.pow(2, (m - 69) / 12);

    function playTune(c, t, name) {
        const { unit, vel } = TUNES[name];
        const list = tune(name);
        const last = list[list.length - 1];
        const seconds = (last.start + last.dur) * unit;
        const bus = organBus(c, t, seconds);
        for (const n of list)
            if (n.midi !== null) organNote(c, bus.input, t + n.start * unit, midiToHz(n.midi), n.dur * unit * 0.96, vel);
        return seconds;   // how long the tune runs, so the game can wait for it
    }

    // The slow dotted tread under Chopin's Funeral March (Piano Sonata No. 2, third movement), the first two
    // bars and the chord that follows. [midi note, start beat, beats], read from a MIDI file of the score.
    const FUNERAL_CHORD_A = [53, 58, 34, 41, 46];      // F3 Bb3 over Bb1 F2 Bb2
    const FUNERAL_CHORD_B = [54, 58, 37, 42, 49];      // Gb3 Bb3 over Db2 Gb2 Db3
    const FUNERAL = (() => {
        const score = [];
        for (const bar of [0, 4]) {
            FUNERAL_CHORD_A.forEach(m => score.push([m, bar + 0, 0.9]));
            FUNERAL_CHORD_B.forEach(m => score.push([m, bar + 1, 0.7]));
            score.push([58, bar + 1.75, 0.2]);                                         // the short pickup
            FUNERAL_CHORD_A.forEach(m => score.push([m, bar + 2, 0.9]));
            FUNERAL_CHORD_B.filter(m => m !== 58).forEach(m => score.push([m, bar + 3, 0.9]));
        }
        FUNERAL_CHORD_A.forEach(m => score.push([m, 8, 2.2]));                         // the last chord, held
        return score;
    })();
    const FUNERAL_BEAT = 0.5;

    function funeralMarch(c, t) {
        const seconds = 10 * FUNERAL_BEAT;
        const bus = organBus(c, t, seconds);
        for (const [m, beat, beats] of FUNERAL)
            organNote(c, bus.input, t + beat * FUNERAL_BEAT, midiToHz(m), beats * FUNERAL_BEAT, 0.8);
        return seconds;
    }

    const sounds = {
        organ: (c, t) => organTheme(c, t),
        stretch: (c, t) => playTune(c, t, 'stretch'),
        rally: (c, t) => playTune(c, t, 'rally'),
        entrance: (c, t) => playTune(c, t, 'entrance'),
        funeral: (c, t) => funeralMarch(c, t),
        hit: (c, t) => { crack(c, t); cheer(c, t + 0.1, 1.1, 0.12); },
        homerun: (c, t) => {
            crack(c, t, 1.3);
            cheer(c, t + 0.15, 3.2, 0.3);
            arpeggio(c, t + 0.5, [523, 659, 784, 1047], 0.12);
        },
        out: (c, t) => thud(c, t),
        strikeout: (c, t) => mittPop(c, t),
        walk: (c, t) => { tone(c, t, 880, 880, 0.06, 0.25, 'square'); tone(c, t + 0.1, 880, 880, 0.06, 0.25, 'square'); },
        doubleplay: (c, t) => { thud(c, t); thud(c, t + 0.2); },
        steal: (c, t) => noise(c, t, 0.25, 0.4, 'bandpass', 2500, 1.5, 0.12),
        caught: (c, t) => { noise(c, t, 0.15, 0.4, 'bandpass', 2500, 1.5, 0.08); thud(c, t + 0.18); },
        error: (c, t) => arpeggio(c, t, [440, 370, 311], 0.1, 0.25),
        cheer: (c, t) => cheer(c, t, 1.6, 0.2),
        playball: (c, t) => arpeggio(c, t, [392, 392, 523], 0.14, 0.25),
        gameover: (c, t) => { arpeggio(c, t, [523, 659, 784, 659, 784, 1047], 0.15); cheer(c, t + 0.2, 2.5, 0.25); },
        blip: (c, t) => tone(c, t, 660, 660, 0.05, 0.2, 'square'),
    };

    // The theme for the startup page. Browsers keep audio locked until the visitor does something, so if
    // it can't start yet it waits for the first click, key press or tap, then plays once.
    let themeOffered = false;

    function playTheme() {
        if (themeOffered) return;
        themeOffered = true;
        if (muted) return;
        const c = ac();
        if (!c) return;
        if (c.state === 'running') {
            sounds.organ(c, c.currentTime + 0.05);
            return;
        }

        const events = ['pointerdown', 'keydown', 'touchstart'];
        const start = e => {
            events.forEach(n => document.removeEventListener(n, start, true));
            if (muted) return;
            // Going straight into a game: the game's own jingle is about to play.
            if (e.target && e.target.closest && e.target.closest('[data-no-theme]')) return;
            Promise.resolve(c.resume()).then(() => sounds.organ(c, c.currentTime + 0.05));
        };
        events.forEach(n => document.addEventListener(n, start, true));
    }

    return {
        playTheme,
        /**
         * @param delay seconds to wait first (so a tune can follow a play's own sound)
         * @returns how many seconds the sound runs for (0 for short effects, or when muted)
         */
        play(name, delay = 0) {
            if (muted) return 0;
            const c = ac();
            const fn = sounds[name];
            if (!c || !fn) return 0;
            const length = fn(c, c.currentTime + 0.01 + delay);
            return typeof length === 'number' ? length : 0;
        },
        stopOrgan,
        tune,              // the parsed notes of a tune (used by the tests)
        parseAbc,
        isMuted: () => muted,
        setMuted(value) {
            muted = !!value;
            if (muted) stopOrgan();
            try { localStorage.setItem('mlb-muted', muted ? '1' : '0'); } catch { }
        },
    };
})();
