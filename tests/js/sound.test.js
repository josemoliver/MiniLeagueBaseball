// Tests for the synthesized sounds, run with: node --test tests/js/sound.test.js
// A small fake Web Audio API records what gets built so the theme can be checked without a browser.
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

const SOUND_JS = path.join(__dirname, '..', '..', 'src', 'MiniLeague.Web', 'wwwroot', 'js', 'sound.js');

function loadSound({ state = 'running', muted = false } = {}) {
    const made = { oscillators: [], sources: [], starts: [], gesture: false, fades: [] };
    const listeners = new Map();   // event name -> handlers

    const param = () => ({
        value: 0,
        setValueAtTime() {}, exponentialRampToValueAtTime() {}, linearRampToValueAtTime() {},
        cancelScheduledValues() {},
        setTargetAtTime(target) { made.fades.push(target); },   // how a playing tune gets faded out
    });
    const node = extra => ({ connect() {}, disconnect() {}, ...extra });

    class FakeAudioContext {
        constructor() { this.state = state; this.sampleRate = 8000; this.currentTime = 1; this.destination = node(); }
        // like a real browser: audio stays locked until the visitor has interacted with the page
        resume() { if (made.gesture) this.state = 'running'; return Promise.resolve(); }
        createGain() { return node({ gain: param() }); }
        createOscillator() {
            const o = node({ frequency: param(), type: 'sine', start(t) { made.starts.push(t); }, stop() {} });
            made.oscillators.push(o);
            return o;
        }
        createBiquadFilter() { return node({ frequency: param(), Q: param(), type: '' }); }
        createConvolver() { return node({ buffer: null }); }
        createBuffer(ch, len) { return { getChannelData: () => new Float32Array(len) }; }
        createBufferSource() {
            const src = node({ buffer: null, start() {}, stop() {} });
            made.sources.push(src);
            return src;
        }
    }

    const store = new Map(muted ? [['mlb-muted', '1']] : []);
    delete require.cache[SOUND_JS];
    globalThis.window = globalThis;
    globalThis.AudioContext = FakeAudioContext;
    globalThis.localStorage = {
        getItem: k => (store.has(k) ? store.get(k) : null),
        setItem: (k, v) => store.set(k, v),
    };
    globalThis.document = {
        addEventListener: (n, h) => { listeners.set(n, [...(listeners.get(n) || []), h]); },
        removeEventListener: (n, h) => { listeners.set(n, (listeners.get(n) || []).filter(x => x !== h)); },
    };
    require(SOUND_JS);

    const fire = (name, target = {}) => {
        made.gesture = true;
        [...(listeners.get(name) || [])].forEach(h => h({ target }));
    };
    const listening = () => ['pointerdown', 'keydown', 'touchstart'].some(n => (listeners.get(n) || []).length > 0);
    return { sound: globalThis.window.mlbSound, made, fire, listening };
}

const settle = () => new Promise(r => setImmediate(r));
const notPlayBall = { closest: () => null };

test('the theme is a full organ fanfare: many drawbar voices spread over a few seconds', () => {
    const { sound, made } = loadSound();
    sound.playTheme();

    // 14 notes in the score, each with 7 drawbars, plus the wobble LFO
    assert.ok(made.oscillators.length >= 14 * 7, `only ${made.oscillators.length} oscillators`);
    const lastStart = Math.max(...made.starts);
    assert.ok(lastStart - Math.min(...made.starts) > 1.5, 'the notes should be spread over time');
});

test('when the browser allows audio, it plays immediately', () => {
    const { sound, made, listening } = loadSound({ state: 'running' });
    sound.playTheme();

    assert.ok(made.oscillators.length > 0);
    assert.equal(listening(), false);
});

test('it plays only once per launch', () => {
    const { sound, made } = loadSound();
    sound.playTheme();
    const count = made.oscillators.length;
    sound.playTheme();
    sound.playTheme();

    assert.equal(made.oscillators.length, count);
});

test('when audio is blocked, it waits for the first interaction and then plays', async () => {
    const { sound, made, fire, listening } = loadSound({ state: 'suspended' });
    sound.playTheme();

    assert.equal(made.oscillators.length, 0);   // nothing yet
    assert.equal(listening(), true);

    fire('pointerdown', notPlayBall);
    await settle();

    const played = made.oscillators.length;
    assert.ok(played > 0);
    assert.equal(listening(), false);           // and the listeners are gone

    fire('keydown', notPlayBall);
    await settle();
    assert.equal(made.oscillators.length, played);   // no second play
});

test('a key press or a tap also starts it', async () => {
    for (const event of ['keydown', 'touchstart']) {
        const { sound, made, fire } = loadSound({ state: 'suspended' });
        sound.playTheme();
        fire(event, notPlayBall);
        await settle();
        assert.ok(made.oscillators.length > 0, `${event} should start the theme`);
    }
});

test('if the first interaction is PLAY BALL, the theme steps aside', async () => {
    const { sound, made, fire, listening } = loadSound({ state: 'suspended' });
    sound.playTheme();

    fire('pointerdown', { closest: sel => (sel === '[data-no-theme]' ? {} : null) });
    await settle();

    assert.equal(made.oscillators.length, 0);
    assert.equal(listening(), false);
});

test('a muted game never plays the theme, now or later', () => {
    const running = loadSound({ state: 'running', muted: true });
    running.sound.playTheme();
    assert.equal(running.made.oscillators.length, 0);

    const blocked = loadSound({ state: 'suspended', muted: true });
    blocked.sound.playTheme();
    assert.equal(blocked.listening(), false);
});

test('muting while the theme is waiting silences it', async () => {
    const { sound, made, fire } = loadSound({ state: 'suspended' });
    sound.playTheme();
    sound.setMuted(true);

    fire('pointerdown', notPlayBall);
    await settle();

    assert.equal(made.oscillators.length, 0);
});

test('the organ is also available as an ordinary named sound', () => {
    const { sound, made } = loadSound();
    sound.play('organ');

    assert.ok(made.oscillators.length > 0);
});

test('every button on the sound preview page plays a sound that exists', () => {
    const fs = require('node:fs');
    const html = fs.readFileSync(path.join(__dirname, '..', '..', 'tools', 'sound-preview.html'), 'utf8');
    const names = [...html.matchAll(/data-sound="([^"]+)"/g)].map(m => m[1]);

    assert.ok(names.length >= 14, `only ${names.length} buttons`);
    assert.ok(html.includes('wwwroot/js/sound.js'), 'the page must load the real sound code');
    for (const name of names) {
        const { sound, made } = loadSound();
        sound.play(name);
        assert.ok(made.oscillators.length + made.sources.length > 0, `"${name}" makes no sound`);
    }
});

// ── The melodies ─────────────────────────────────────────────────────────────

const abc = (lines, shift = 0) => loadSound().sound.parseAbc(lines.join('\n'), shift);
const midis = notes => notes.map(n => n.midi);

test('ABC reader: pitches, octaves, lengths, rests and key signatures', () => {
    // K:D means F and C are sharp. Lowercase is the octave above; ' goes up and , goes down.
    const notes = abc(['L:1/4', 'K:D', 'D F c d d, d\' z2 A/2 A3/2']);

    assert.deepEqual(midis(notes), [62, 66, 73, 74, 62, 86, null, 69, 69]);
    assert.deepEqual(notes.map(n => n.dur), [1, 1, 1, 1, 1, 1, 2, 0.5, 1.5]);
    assert.deepEqual(notes.map(n => n.start), [0, 1, 2, 3, 4, 5, 6, 8, 8.5]);
});

test('ABC reader: accidentals last to the bar line, and a transposition shifts everything', () => {
    const notes = abc(['K:C', 'F ^F F | F =F'], 0);

    assert.deepEqual(midis(notes), [65, 66, 66, 65, 65]);   // F, F#, F# (same bar), F, F-natural
    assert.deepEqual(midis(abc(['K:C', 'C E G'], -2)), [58, 62, 65]);
});

test('ABC reader: ties join notes, and broken rhythm dots one note and shortens the next', () => {
    const tied = abc(['L:1/4', 'K:C', 'A3- | A z']);
    assert.deepEqual(tied.map(n => [n.midi, n.dur]), [[69, 4], [null, 1]]);

    const dotted = abc(['L:1/8', 'K:C', 'A>B c<d']);
    assert.deepEqual(dotted.map(n => n.dur), [1.5, 0.5, 0.5, 1.5]);
});

test('ABC reader: chord symbols, slurs, repeats, decorations and continuations are ignored', () => {
    const plain = abc(['K:C', 'c d e f']);
    const noisy = abc(['K:C', '|: "C"(c d) .e H f \\', ' :|']);

    assert.deepEqual(midis(noisy), midis(plain));
});

test('Take Me Out to the Ball Game: the famous opening, in C, with the leap up an octave on "me"', () => {
    const notes = loadSound().sound.tune('stretch');

    // Take(C4) me(C5) out(A4) to(G4) the(E4) ball(G4, held) game(D4, held)
    assert.deepEqual(midis(notes).slice(0, 7), [60, 72, 69, 67, 64, 67, 62]);
    assert.deepEqual(notes.slice(0, 7).map(n => n.dur), [2, 1, 1, 1, 1, 3, 3]);

    // it finishes on the tonic, held (then a bar of rest)
    const last = notes.filter(n => n.midi !== null).pop();
    assert.equal(last.midi, 72);
    assert.equal(last.dur, 4);
});

test('Take Me Out to the Ball Game: "buy me some peanuts" has the raised note, and it lasts about twenty seconds', () => {
    const { sound } = loadSound();
    const notes = sound.tune('stretch');

    assert.ok(midis(notes).includes(68), 'a G sharp (the A sharp of the original key)');
    const seconds = (notes[notes.length - 1].start + notes[notes.length - 1].dur) * 0.33;
    assert.ok(seconds > 15 && seconds < 25, `${seconds.toFixed(1)}s`);
});

test('La Raspa (Mexican Hat Dance): two eight-bar parts in six-eight, starting after a pickup rest', () => {
    const notes = loadSound().sound.tune('rally');

    assert.deepEqual(notes.slice(0, 4).map(n => [n.midi, n.dur]), [[null, 2], [67, 1], [72, 2], [67, 1]]);
    const units = notes[notes.length - 1].start + notes[notes.length - 1].dur;
    assert.equal(units, 16 * 6);   // 16 bars of 6 eighths
});

test('Hail to the Chief: the first strain, eight bars of four-four with the dotted rhythm', () => {
    const notes = loadSound().sound.tune('entrance');

    // A4 B4 C#5 D5 C#5 B4, the "(B>c)" pairs dotted
    assert.deepEqual(midis(notes).slice(0, 6), [69, 71, 73, 74, 73, 71]);
    assert.deepEqual(notes.slice(0, 3).map(n => n.dur), [2, 1.5, 0.5]);
    const units = notes[notes.length - 1].start + notes[notes.length - 1].dur;
    assert.equal(units, 8 * 8);
    assert.equal(notes[notes.length - 1].midi, 74);   // ends on D
});

test('every organ tune plays: many voices, spread over time', () => {
    for (const name of ['stretch', 'rally', 'entrance', 'funeral']) {
        const { sound, made } = loadSound();
        sound.play(name);

        assert.ok(made.oscillators.length >= 60, `${name}: only ${made.oscillators.length} oscillators`);
        assert.ok(Math.max(...made.starts) - Math.min(...made.starts) > 2, `${name} is too short`);
    }
});

test('the funeral march is a short, low sting: the dotted chord tread then a held chord', () => {
    const { sound, made } = loadSound();
    sound.play('funeral');

    // 45 notes of 7 drawbars each, plus the wobble
    assert.ok(made.oscillators.length >= 45 * 7);
    const span = Math.max(...made.starts) - Math.min(...made.starts);
    assert.ok(span > 3.5 && span < 6, `${span.toFixed(1)}s`);
});

test('a new tune fades out the one that is still playing, and muting silences it', () => {
    const first = loadSound();
    first.sound.play('stretch');
    assert.equal(first.made.fades.length, 0);
    first.sound.play('entrance');
    assert.deepEqual(first.made.fades, [0]);          // the first tune was faded out

    const muting = loadSound();
    muting.sound.play('rally');
    const playing = muting.made.oscillators.length;
    muting.sound.setMuted(true);
    assert.deepEqual(muting.made.fades, [0]);
    muting.sound.play('stretch');                     // and nothing new plays while muted
    assert.equal(muting.made.oscillators.length, playing);
});

test('a tune can be told to wait, so it follows the play\'s own sound', () => {
    const now = loadSound();
    now.sound.play('stretch');
    const delayed = loadSound();
    delayed.sound.play('stretch', 1.5);

    const firstNote = made => Math.min(...made.starts.filter(t => t > 1));   // the LFO starts at the same time
    assert.ok(Math.min(...delayed.made.starts) >= Math.min(...now.made.starts) + 1.4);
    assert.ok(firstNote(delayed.made) > firstNote(now.made));
});
