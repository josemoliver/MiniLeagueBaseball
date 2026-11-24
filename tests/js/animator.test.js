// Tests for the animated field player, run with: node --test tests/js/animator.test.js
// A fake canvas records what is drawn and a fake clock drives the animation, so no browser is needed.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SOURCE = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'MiniLeague.Web', 'wwwroot', 'js', 'playAnimator.js'), 'utf8');

// A canvas context that accepts any call and remembers it.
function fakeContext() {
    const calls = [];
    const styles = [];   // every colour set as a fill or a stroke, in order, so a test can see what was painted with what
    const ctx = new Proxy({}, {
        get(target, prop) {
            if (prop === 'calls') return calls;
            if (prop === 'styles') return styles;
            // wider letters for a bigger font, like a real monospace font
            // reading the picture back: by default every pixel is the colour of the stands; a test can say otherwise
            if (prop === 'getImageData') return (x, y, w, h) => {
                const data = new Uint8ClampedArray(w * h * 4);
                for (let j = 0; j < h; j++)
                    for (let i = 0; i < w; i++) {
                        const [r, g, b] = target.pixelAt ? target.pixelAt(x + i, y + j) : [38, 38, 48];
                        const o = (j * w + i) * 4;
                        data[o] = r; data[o + 1] = g; data[o + 2] = b; data[o + 3] = 255;
                    }
                return { data };
            };
            if (prop === 'measureText') return text => ({ width: String(text).length * 0.6 * Number((/(\d+)px/.exec(target.font || '') || [0, 12])[1]) });
            if (prop in target) return target[prop];
            return (...args) => { calls.push([prop, args, target.font]); };   // (the font in use is the third item)
        },
        set(target, prop, value) {
            if (prop === 'fillStyle' || prop === 'strokeStyle') styles.push(value);
            target[prop] = value;
            return true;
        },
    });
    return ctx;
}

function fakeCanvas() {
    const ctx = fakeContext();
    const listeners = {};
    return { width: 0, height: 0, ctx, listeners, getContext: () => ctx, addEventListener: (type, fn) => { listeners[type] = fn; } };
}

function load({ reduced = false, pixelAt = null, dpr = 1 } = {}) {
    let onResize = null;
    const frames = [];
    const created = [];
    const heard = [];
    const sandbox = {
        console,
        URL,
        ResizeObserver: class { constructor(fn) { onResize = fn; } observe() {} },
        setTimeout, clearTimeout,
        performance: { now: () => sandbox.clock },
        clock: 0,
        document: {
            hidden: false, baseURI: 'http://example/',
            listeners: {},
            addEventListener(type, fn) { this.listeners[type] = fn; },
            createElement: () => { const c = fakeCanvas(); if (pixelAt) c.ctx.pixelAt = pixelAt; created.push(c); return c; },
        },
        window: {
            requestAnimationFrame: fn => { frames.push(fn); return frames.length; },
            cancelAnimationFrame: () => {},
            mlbSound: { play: name => heard.push(name) },
            devicePixelRatio: dpr,
            matchMedia: query => ({ matches: reduced && String(query).includes('reduce') }),
        },
    };
    vm.runInNewContext(SOURCE, sandbox);
    const api = sandbox.window.mlbAnimator;
    return {
        api, heard, created, document: sandbox.document, sound: sandbox.window.mlbSound,
        resize: () => onResize && onResize(),
        // moves the fake clock on and runs the animation frames that were waiting
        advance(ms) {
            sandbox.clock += ms;
            const waiting = frames.splice(0);
            waiting.forEach(fn => fn(sandbox.clock));
        },
        pending: () => frames.length,
    };
}

// Shaped like what PlayScriptJson writes.
const SCRIPT = {
    version: 1, outcome: 'Single', duration: 2,
    actors: [
        { id: 'ball', kind: 'ball' },
        { id: 'batter', kind: 'batter', label: 'Ruth', team: 'visitor' },
        { id: 'fielder-Left', kind: 'fielder', label: 'Mays', team: 'home' },
    ],
    tracks: [
        { actorId: 'ball', keys: [
            { t: 0, x: 0, y: 59, z: 5.5, pose: 'stand' },
            { t: 1, x: 0, y: 0, z: 3 },
            { t: 2, x: 100, y: 200, z: 0, ease: 'arc', apex: 40 },
        ] },
        { actorId: 'batter', keys: [
            { t: 0, x: -3.5, y: 0, z: 0, pose: 'stand' },
            { t: 1, x: -3.5, y: 0, z: 0, pose: 'swing' },
            { t: 1.5, x: 63.6, y: 63.6, z: 0, pose: 'run' },
        ] },
        { actorId: 'fielder-Left', keys: [{ t: 0, x: -170, y: 255, z: 0, pose: 'stand' }] },
    ],
    cues: [
        { t: 1, kind: 'sound', value: 'hit' },
        { t: 1.5, kind: 'banner', value: 'Ruth singles.' },
        { t: 1.8, kind: 'sound', value: 'cheer' },
        { t: 1.2, kind: 'effect', value: 'burst', x: 10, y: 250 },
    ],
};

const park = { name: 'Test Park', leftField: 330, leftCenter: 375, center: 400, rightCenter: 375, rightField: 330, leftWallHeight: 8, rightWallHeight: 8 };

// ── The camera ───────────────────────────────────────────────────────────────

test('home plate is low and centered; second base is higher and smaller', () => {
    const { project } = load().api._internals;
    const home = project(0, 0), second = project(0, 127.3);

    assert.equal(Math.round(home.x), 320);
    assert.ok(home.y > 340 && home.y < 395, `home at ${home.y}`);
    assert.ok(second.y < home.y - 100, 'second base is well above home plate');
    assert.ok(second.y > 150 && second.y < 260, `second at ${second.y}`);
    assert.ok(second.s < home.s, 'and smaller');
});

test('first and third base are left and right of center, a little above home', () => {
    const { project } = load().api._internals;
    const first = project(63.6, 63.6), third = project(-63.6, 63.6), home = project(0, 0);

    assert.ok(first.x > 400 && first.x < 520);
    assert.ok(third.x < 240 && third.x > 120);
    assert.ok(Math.abs((first.x - 320) + (third.x - 320)) < 0.01, 'symmetric');
    assert.ok(first.y < home.y && first.y > project(0, 127.3).y);
});

test('farther things are smaller, and height lifts a point up the screen', () => {
    const { project } = load().api._internals;

    assert.ok(project(0, 300).s < project(0, 100).s);
    assert.ok(project(0, 100, 30).y < project(0, 100, 0).y);
    assert.equal(project(5, 100, 30).x, project(5, 100, 0).x);   // height does not move it sideways
});

test('the whole field fits on the picture: the foul poles and the center-field wall are on screen', () => {
    const { project, wallDistance } = load().api._internals;
    const dist = a => wallDistance(park, a);
    const r = deg => deg * Math.PI / 180;
    const pole = project(dist(45) * Math.sin(r(45)), dist(45) * Math.cos(r(45)));
    const lpole = project(dist(-45) * Math.sin(r(-45)), dist(-45) * Math.cos(r(-45)));
    const center = project(0, dist(0));

    assert.ok(pole.x < 640 && lpole.x > 0, `poles at ${lpole.x} and ${pole.x}`);
    assert.ok(center.y > 40 && center.y < 120, `center-field wall at ${center.y}`);
});

// ── The park ─────────────────────────────────────────────────────────────────

test('wall distance follows the posted distances and is clamped past the foul lines', () => {
    const { wallDistance, wallHeight } = load().api._internals;
    const p = { ...park, leftField: 310, leftCenter: 379, center: 390, rightCenter: 380, rightField: 302 };

    assert.equal(wallDistance(p, -45), 310);
    assert.equal(wallDistance(p, 0), 390);
    assert.equal(wallDistance(p, 45), 302);
    assert.equal(wallDistance(p, -33.75), (310 + 379) / 2);
    assert.equal(wallDistance(p, 80), 302);
    assert.equal(wallHeight({ leftWallHeight: 37, rightWallHeight: 3 }, 0), 20);
});

// ── Moving things ────────────────────────────────────────────────────────────

test('sampling interpolates between keys, holds before the first and after the last', () => {
    const { prepare, sample } = load().api._internals;
    const keys = prepare(SCRIPT).tracks.get('batter');

    assert.equal(sample(keys, -1).x, -3.5);
    assert.equal(sample(keys, 99).x, 63.6);
    const mid = sample(keys, 1.25);
    assert.ok(Math.abs(mid.x - (-3.5 + (63.6 + 3.5) / 2)) < 1e-9);
});

test('a pose lasts until the next key, and a key with no pose keeps the last one', () => {
    const { prepare, sample } = load().api._internals;
    const ball = prepare(SCRIPT).tracks.get('ball');
    const batter = prepare(SCRIPT).tracks.get('batter');

    assert.equal(sample(ball, 1.5).pose, 'stand');     // keys 2 and 3 name no pose
    assert.equal(sample(batter, 0.5).pose, 'stand');
    assert.equal(sample(batter, 1.2).pose, 'swing');
    assert.equal(sample(batter, 5).pose, 'run');
});

test('an arc rises to its apex in the middle and lands where it should', () => {
    const { prepare, sample } = load().api._internals;
    const ball = prepare(SCRIPT).tracks.get('ball');

    const middle = sample(ball, 1.5);
    assert.ok(Math.abs(middle.z - (1.5 + 40)) < 1e-9, `z at the middle was ${middle.z}`);   // (3+0)/2 plus the apex
    assert.equal(sample(ball, 2).z, 0);
    assert.ok(sample(ball, 1.1).z < middle.z && sample(ball, 1.9).z < middle.z);
});

test('a rolling ball slows down: it covers more ground early than late', () => {
    const { prepare, sample } = load().api._internals;
    const rolling = prepare({ tracks: [{ actorId: 'b', keys: [
        { t: 0, x: 0, y: 0, z: 0 }, { t: 1, x: 100, y: 0, z: 0, ease: 'roll' },
    ] }] }).tracks.get('b');

    const firstHalf = sample(rolling, 0.5).x;
    assert.ok(firstHalf > 60, `covered ${firstHalf} in the first half`);
    assert.equal(sample(rolling, 1).x, 100);
});

test('sampled travel distance follows position across animation keys', () => {
    const { prepare, sample } = load().api._internals;
    const keys = prepare({ tracks: [{ actorId: 'runner', keys: [
        { t: 0, x: 0, y: 0, z: 0 },
        { t: 1, x: 30, y: 40, z: 0 },
        { t: 2, x: 60, y: 80, z: 0 },
    ] }] }).tracks.get('runner');

    assert.equal(sample(keys, 0.5).distance, 25);
    assert.equal(sample(keys, 1.5).distance, 75);
    assert.equal(sample(keys, 2).distance, 100);
});

test('the message box shows the latest banner so far, and effects last under a second', () => {
    const { prepare, activeBanner, activeEffects } = load().api._internals;
    const { cues } = prepare(SCRIPT);

    assert.equal(activeBanner(cues, 0, 'start'), 'start');
    assert.equal(activeBanner(cues, 1.4, 'start'), 'start');
    assert.equal(activeBanner(cues, 1.5, 'start'), 'Ruth singles.');
    assert.equal(activeEffects(cues, 1.1).length, 0);
    assert.equal(activeEffects(cues, 1.3).length, 1);
    assert.equal(activeEffects(cues, 2.1).length, 0);
});

test('message text wraps on spaces and on newlines', () => {
    const { wrapText } = load().api._internals;
    const ctx = fakeContext();

    assert.deepEqual([...wrapText(ctx, 'ONE\nTWO', 1000)], ['ONE', 'TWO']);
    const lines = wrapText(ctx, 'THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG', 100);
    assert.ok(lines.length > 1);
    assert.ok(lines.every(l => l.length * 7 <= 100));
});

// ── Playing ──────────────────────────────────────────────────────────────────

function start(env = load()) {
    const canvas = fakeCanvas();
    env.api.init(canvas);
    return { env, canvas };
}

test('contact and fielding cues add brief marks, while focus highlights a base', () => {
    const render = cue => {
        const { env, canvas } = start();
        const before = canvas.ctx.calls.length;
        env.api.seek({ version: 1, outcome: 'Test', duration: 1, actors: [], tracks: [], cues: cue ? [cue] : [] }, park, 0.2, '');
        return canvas.ctx.calls.slice(before);
    };
    const count = (calls, method) => calls.filter(([name]) => name === method).length;
    const plain = render(null);
    const focus = render({ t: 0.1, kind: 'effect', value: 'focus', x: 63.6, y: 63.6 });
    const contact = render({ t: 0.1, kind: 'effect', value: 'contact', x: 0, y: 0 });
    const glove = render({ t: 0.1, kind: 'effect', value: 'glove', x: 80, y: 180 });

    assert.equal(count(focus, 'ellipse'), count(plain, 'ellipse') + 1);
    assert.ok(count(contact, 'lineTo') > count(plain, 'lineTo'));
    assert.ok(count(glove, 'lineTo') > count(plain, 'lineTo'));
});

test('a distant diving catch gets a brief magnified inset without replacing the field', () => {
    const script = {
        version: 1, outcome: 'FlyOut', duration: 3,
        actors: [{ id: 'ball', kind: 'ball' }, { id: 'fielder-Left', kind: 'fielder', team: 'home', label: 'Mays' }],
        tracks: [
            { actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 3 }, { t: 2, x: -145, y: 260, z: 1.5 }] },
            { actorId: 'fielder-Left', keys: [
                { t: 0, x: -125, y: 255, z: 0, pose: 'stand' },
                { t: 1.6, x: -136, y: 258, z: 1.6, pose: 'dive' },
                { t: 2, x: -145, y: 260, z: 0, pose: 'dive' },
                { t: 2.7, x: -145, y: 260, z: 0, pose: 'stand' },
            ] },
        ],
        cues: [{ t: 2, kind: 'effect', value: 'glove', x: -145, y: 260 }],
    };
    const { env, canvas } = start();
    const render = (s, t) => {
        const before = canvas.ctx.calls.length;
        env.api.seek(s, park, t, '');
        return canvas.ctx.calls.slice(before);
    };
    const hasInset = calls => calls.some(([name, args]) => name === 'fillText' && args[0] === 'CATCH CLOSE-UP');
    const before = render(script, 1.5);
    const during = render(script, 2);
    const after = render(script, 2.9);
    assert.equal(hasInset(before), false);
    assert.equal(hasInset(during), true);
    assert.equal(hasInset(after), false);
    assert.ok(during.some(([name, args]) => name === 'fillRect' && args[0] === 442 && args[1] === 88));
    assert.ok(during.filter(([name]) => name === 'drawImage').length > before.filter(([name]) => name === 'drawImage').length);

    const ordinary = { ...script, tracks: script.tracks.map(track => track.actorId === 'fielder-Left'
        ? { ...track, keys: track.keys.map(k => ({ ...k, pose: 'catch' })) } : track) };
    assert.equal(hasInset(render(ordinary, 2)), false);
});

test('init sizes the canvas to the fixed picture and draws the field', () => {
    const { canvas } = start();

    assert.equal(canvas.width, 1280);
    assert.equal(canvas.height, 800);
    assert.ok(canvas.ctx.calls.some(([name]) => name === 'drawImage'), 'the backdrop was drawn');
});

test('show draws the starting positions and the message, without playing anything', () => {
    const { env, canvas } = start();
    env.api.show(SCRIPT, park, 'Top of inning 1\nRuth at bat');

    assert.equal(env.api.playing, false);
    assert.equal(env.pending(), 0);
    const texts = canvas.ctx.calls.filter(([name]) => name === 'fillText').map(([, args]) => args[0]);
    assert.ok(texts.includes('TOP OF INNING 1') && texts.includes('RUTH AT BAT'), texts.join('|'));
});

test('seek draws a script at one moment without playing it', () => {
    const { env, canvas } = start();
    env.api.seek(SCRIPT, park, 1.6, '');

    assert.equal(env.api.playing, false);
    assert.equal(env.pending(), 0);
    const texts = canvas.ctx.calls.filter(([name]) => name === 'fillText').map(([, args]) => args[0]);
    assert.ok(texts.includes('RUTH SINGLES.'), 'the banner that has happened by then is shown');
});

test('a play runs until its duration, fires each sound once in order, and then settles', async () => {
    const { env } = start();
    let result = 'pending';
    const done = env.api.play(JSON.stringify(SCRIPT), park, 1).then(r => { result = r; });

    assert.equal(env.api.playing, true);
    env.advance(500);
    assert.deepEqual(env.heard, []);
    env.advance(600);                       // 1.1s: the hit
    assert.deepEqual(env.heard, ['hit']);
    env.advance(300);                       // 1.4s
    env.advance(500);                       // 1.9s: the cheer
    assert.deepEqual(env.heard, ['hit', 'cheer']);
    assert.equal(result, 'pending');

    env.advance(300);                       // 2.2s: over
    await done;
    assert.equal(result, false);            // finished, not skipped
    assert.equal(env.api.playing, false);
    assert.deepEqual(env.heard, ['hit', 'cheer']);
});

test('the speed setting plays the same script faster or slower', async () => {
    const fast = start().env;
    const slow = start().env;
    const quick = fast.api.play(SCRIPT, park, 2);   // twice as fast: 1 second of clock is 2 seconds of script
    const slowly = slow.api.play(SCRIPT, park, 0.5);

    fast.advance(1050);
    slow.advance(1050);
    assert.deepEqual(fast.heard, ['hit', 'cheer']);   // already past 2s of script
    assert.deepEqual(slow.heard, []);                 // only 0.5s of script so far
    await quick;
    assert.equal(slow.api.playing, true);
    slow.api.skip();
    await slowly;
});

test('a close-call hold remains visible for the same real time at Fast speed', async () => {
    const { env, canvas } = start();
    const close = { ...SCRIPT, duration: 2, cues: [
        { t: 1, kind: 'hold', value: '0.22' },
        { t: 1, kind: 'banner', value: 'OUT AT FIRST!' },
        { t: 1.1, kind: 'sound', value: 'out' },
    ] };
    const done = env.api.play(close, park, 2);

    env.advance(500); // Script time reaches the call at 2x speed.
    env.advance(150);
    assert.equal(env.api.playing, true);
    assert.deepEqual(env.heard, []);
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'OUT AT FIRST!'));

    env.advance(100); // The 220 ms real-time hold has ended.
    assert.deepEqual(env.heard, []);
    env.advance(50);
    assert.deepEqual(env.heard, ['out']);
    env.advance(500);
    assert.equal(await done, false);
});

test('skipping finishes at once and quietly, and the promise says it was skipped', async () => {
    const { env, canvas } = start();
    const done = env.api.play(SCRIPT, park, 1);
    env.advance(100);

    env.api.skip();

    assert.equal(await done, true);
    assert.equal(env.api.playing, false);
    assert.deepEqual(env.heard, []);        // the sounds still to come are dropped
    const texts = canvas.ctx.calls.filter(([name]) => name === 'fillText').map(([, args]) => args[0]);
    assert.ok(texts.includes('RUTH SINGLES.'), 'the final message is shown');
});

test('clicking the picture skips the play', async () => {
    const { env, canvas } = start();
    const done = env.api.play(SCRIPT, park, 1);

    canvas.listeners.click();

    assert.equal(await done, true);
});

test('starting another play settles the first one', async () => {
    const { env } = start();
    const first = env.api.play(SCRIPT, park, 1);
    const second = env.api.play(SCRIPT, park, 1);

    assert.equal(await first, true);
    assert.equal(env.api.playing, true);
    env.api.skip();
    await second;
});

test('a script with no length finishes immediately', async () => {
    const { env } = start();
    const setup = { version: 1, outcome: 'Setup', duration: 0, actors: SCRIPT.actors, tracks: SCRIPT.tracks.map(t => ({ actorId: t.actorId, keys: [t.keys[0]] })), cues: [] };

    assert.equal(await env.api.play(setup, park, 1), false);
});

test('stop settles a play that is running and does nothing otherwise', async () => {
    const { env } = start();
    env.api.stop();                         // nothing running: fine
    const done = env.api.play(SCRIPT, park, 1);

    env.api.stop();

    assert.equal(await done, true);
});

test('a sound that cannot play does not break the animation', async () => {
    const { env } = start();
    env.sound.play = () => { throw new Error('no audio'); };

    const done = env.api.play(SCRIPT, park, 4);
    env.advance(600);                       // 2.4s of script: past the sounds

    assert.equal(await done, false);        // still finished normally
});

test('a park with different distances gets its own backdrop', () => {
    const { env, canvas } = start();
    const before = canvas.ctx.calls.filter(([n]) => n === 'drawImage').length;

    env.api.show(SCRIPT, park, '');
    env.api.show(SCRIPT, { ...park, center: 300 }, '');

    assert.ok(canvas.ctx.calls.filter(([n]) => n === 'drawImage').length > before);
});

// ── Phase E: figures, effects and reduced motion ─────────────────────────────

const fillTextsOf = canvas => canvas.ctx.calls.filter(([name]) => name === 'fillText').map(([, args]) => args[0]);

const mover = (id, kind, pose, from, to, extra = {}) => ({
    actor: { id, kind, label: 'Babe Ruth', team: 'home', ...extra },
    track: { actorId: id, keys: [{ t: 0, x: from[0], y: from[1], z: 0, pose }, { t: 1, x: to[0], y: to[1], z: 0 }, { t: 3, x: to[0], y: to[1], z: 0, pose }] },
});

function scriptOf(...items) {
    return {
        version: 1, outcome: 'Test', duration: 3,
        actors: [...items.map(i => i.actor), { id: 'ball', kind: 'ball' }],
        tracks: [...items.map(i => i.track), { actorId: 'ball', keys: [{ t: 0, x: 0, y: 59, z: 5 }] }],
        cues: items.flatMap(i => i.cues || []),
    };
}

test('a figure faces the way he is going: toward the plate shows his front, away his back, sideways a profile', () => {
    const { facingOf } = load().api._internals;
    const actor = { id: 'a', kind: 'fielder' };

    assert.deepEqual({ ...facingOf(actor, { vx: 0, vy: -20, x: 0 }) }, { view: 'front', dir: 1 });
    assert.deepEqual({ ...facingOf(actor, { vx: 0, vy: 20, x: 0 }) }, { view: 'back', dir: 1 });
    assert.deepEqual({ ...facingOf(actor, { vx: 20, vy: 2, x: 0 }) }, { view: 'side', dir: 1 });
    assert.deepEqual({ ...facingOf(actor, { vx: -20, vy: 2, x: 0 }) }, { view: 'side', dir: -1 });
});

test('a figure who is standing faces by his job: the catcher away, the batter toward the plate, the rest the plate', () => {
    const { facingOf } = load().api._internals;
    const still = x => ({ vx: 0, vy: 0, x });

    assert.equal(facingOf({ id: 'c', kind: 'catcher' }, still(0)).view, 'back');
    assert.deepEqual({ ...facingOf({ id: 'b', kind: 'batter' }, still(-3.5)) }, { view: 'side', dir: 1 });   // a right-hander
    assert.deepEqual({ ...facingOf({ id: 'b', kind: 'batter' }, still(3.5)) }, { view: 'side', dir: -1 });   // a left-hander
    assert.equal(facingOf({ id: 'f', kind: 'fielder' }, still(0)).view, 'front');
});

test('each pose changes the figure, and the teams wear different colours', () => {
    const { figureParts } = load().api._internals;
    const actor = team => ({ id: 'f', kind: 'fielder', team });
    const parts = (pose, view = 'front', team = 'home') => figureParts(actor(team), { pose, segT: 0 }, view, 0);
    const top = ps => Math.min(...ps.map(p => p.y));
    const widest = ps => Math.max(...ps.map(p => p.w));

    assert.ok(top(parts('windup')) < top(parts('stand')) - 1.5, 'the windup has his arms overhead');
    assert.ok(top(parts('throw')) < top(parts('stand')), 'a throw has an arm up');
    assert.ok(widest(parts('safe')) >= 15, 'safe has his arms out wide');
    assert.ok(parts('catch').some(p => p.c === '#8b5a2b'), 'a catch shows the glove');
    assert.ok(parts('tag').some(p => p.c === '#8b5a2b'), 'so does a tag');
    assert.notEqual(JSON.stringify(parts('stand')), JSON.stringify(parts('stand', 'back')));
    assert.notEqual(JSON.stringify(parts('stand')), JSON.stringify(parts('stand', 'side')));

    const jersey = team => parts('stand', 'front', team).find(p => p.w >= 6).c;
    assert.notEqual(jersey('home'), jersey('visitor'));
});

test('a runner changes stride with distance traveled, not elapsed time', () => {
    const { figureParts } = load().api._internals;
    const run = (distance, time) => JSON.stringify(figureParts({ id: 'r', kind: 'runner', team: 'home' }, { pose: 'run', distance, segT: 0 }, 'side', time));

    assert.notEqual(run(0, 0), run(20, 0));
    assert.equal(run(20, 0), run(20, 0.1));
});

test('the catcher gets a mask and the batter a helmet', () => {
    const { figureParts } = load().api._internals;
    const outline = kind => figureParts({ id: 'x', kind, team: 'home' }, { pose: 'stand', segT: 0 }, 'front', 0).filter(p => p.c === '#15151b').length;
    const cap = kind => figureParts({ id: 'x', kind, team: 'home' }, { pose: 'stand', segT: 0 }, 'front', 0).find(p => p.c === '#c62828' && p.y < -10);   // (the stockings are the cap's colour too, down low)

    assert.ok(outline('catcher') > outline('fielder'), 'a mask');
    assert.ok(cap('batter').h > cap('fielder').h, 'a bigger cap for the batter');
});

test('sampling reports speed, how long a pose has lasted, and when an actor has reached his last key', () => {
    const { prepare, sample } = load().api._internals;
    const keys = prepare({ tracks: [{ actorId: 'a', keys: [
        { t: 0, x: 0, y: 0, z: 0, pose: 'run' }, { t: 2, x: 20, y: -10, z: 0, pose: 'stand' },
    ] }] }).tracks.get('a');

    const mid = sample(keys, 1);
    assert.equal(mid.vx, 10);
    assert.equal(mid.vy, -5);
    assert.equal(mid.segT, 1);
    assert.equal(mid.ended, false);
    const after = sample(keys, 3.5);
    assert.deepEqual([after.vx, after.vy, after.ended, after.since], [0, 0, true, 1.5]);
});

test('a dive keeps its pose age and travel direction through the catch', () => {
    const { prepare, sample } = load().api._internals;
    const keys = prepare({ tracks: [{ actorId: 'fielder', keys: [
        { t: 0, x: 20, y: 100, z: 0, pose: 'run' },
        { t: 0.8, x: 5, y: 110, z: 1.6, pose: 'dive' },
        { t: 1, x: 0, y: 115, z: 0, pose: 'dive' },
        { t: 1.7, x: 0, y: 115, z: 0, pose: 'stand' },
    ] }] }).tracks.get('fielder');

    assert.ok(Math.abs(sample(keys, 0.9).poseAge - 0.1) < 1e-9);
    assert.ok(Math.abs(sample(keys, 1.2).poseAge - 0.4) < 1e-9);
    assert.equal(sample(keys, 1.2).direction, -1);
    assert.equal(sample(keys, 1.7).previousPose, 'dive');
});

test('a dive lands with a short grass skid, and slide dust settles after the call', () => {
    const stylesAt = (actor, keys, t, reduced = false) => {
        const { env, canvas } = start(load({ reduced }));
        const before = canvas.ctx.styles.length;
        env.api.seek({ version: 1, outcome: 'Test', duration: 2, actors: [actor],
            tracks: [{ actorId: actor.id, keys }], cues: [] }, park, t, '');
        return canvas.ctx.styles.slice(before);
    };
    const fielder = { id: 'fielder', kind: 'fielder', team: 'home' };
    const dive = [
        { t: 0, x: 20, y: 100, z: 0, pose: 'run' },
        { t: 0.8, x: 35, y: 105, z: 1.6, pose: 'dive' },
        { t: 1, x: 40, y: 110, z: 0, pose: 'dive' },
        { t: 1.7, x: 40, y: 110, z: 0, pose: 'stand' },
    ];
    assert.ok(stylesAt(fielder, dive, 1.1).includes('#b6c58e'));
    assert.ok(!stylesAt(fielder, dive, 1.1, true).includes('#b6c58e'));
    assert.ok(!stylesAt(fielder, dive, 1.6).includes('#b6c58e'));

    const runner = { id: 'runner', kind: 'runner', team: 'visitor' };
    const slide = [
        { t: 0, x: 0, y: 0, z: 0, pose: 'run' },
        { t: 0.2, x: 10, y: 0, z: 0, pose: 'slide' },
        { t: 0.5, x: 20, y: 0, z: 0, pose: 'safe' },
    ];
    assert.ok(stylesAt(runner, slide, 0.6).includes('#d2b48c'));
    assert.ok(!stylesAt(runner, slide, 0.9).includes('#d2b48c'));
});

test('an OUT flag shows over a runner who has just been called out, then goes away', () => {
    const runner = mover('r', 'runner', 'out', [0, 60], [40, 60]);   // out at the end of his run
    const soon = start();
    soon.env.api.seek(scriptOf(runner), park, 3.5, '');            // half a second after he got there
    const later = start();
    later.env.api.seek(scriptOf(runner), park, 5.0, '');            // two seconds after

    assert.ok(fillTextsOf(soon.canvas).includes('OUT'));
    assert.ok(!fillTextsOf(later.canvas).includes('OUT'));
});

test('a SAFE flag shows for a runner who slid in safe', () => {
    const { env, canvas } = start();
    env.api.seek(scriptOf(mover('r', 'runner', 'safe', [0, 60], [40, 60])), park, 3.5, '');

    assert.ok(fillTextsOf(canvas).includes('SAFE'));
});

test('the people handling the ball get a name tag, but not someone just standing', () => {
    const busy = start();
    busy.env.api.seek(scriptOf(mover('f', 'fielder', 'catch', [0, 90], [0, 90])), park, 0.5, '');
    const quiet = start();
    quiet.env.api.seek(scriptOf(mover('f', 'fielder', 'stand', [0, 90], [0, 90])), park, 0.5, '');

    assert.ok(fillTextsOf(busy.canvas).includes('RUTH'));
    assert.ok(!fillTextsOf(quiet.canvas).includes('RUTH'));
});

test('surnames for tags are the last word, in capitals, and not too long', () => {
    const { surname } = load().api._internals;

    assert.equal(surname('Babe Ruth'), 'RUTH');
    assert.equal(surname('Pedro Martinez-Fernandez'), 'MARTINEZ-FER');
    assert.equal(surname(null), '');
});

test('the crowd sparkles for 1.4 seconds after a cheer or a home run burst, and not otherwise', () => {
    const { cheering } = load().api._internals;
    const cues = [{ t: 2, kind: 'sound', value: 'cheer' }, { t: 5, kind: 'effect', value: 'burst' }, { t: 7, kind: 'sound', value: 'hit' }];

    assert.equal(cheering(cues, 1.9), false);
    assert.equal(cheering(cues, 2.5), true);
    assert.equal(cheering(cues, 3.5), false);
    assert.equal(cheering(cues, 5.5), true);
    assert.equal(cheering(cues, 7.2), false);   // an ordinary sound is no cheer
});

const sparkle = canvas => canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[2] === 2 && args[3] === 2).length;

test('the crowd sparkles during a cheer, but not for someone who prefers less motion', () => {
    const cheer = scriptOf(mover('f', 'fielder', 'stand', [0, 90], [0, 90]));
    cheer.cues = [{ t: 0.2, kind: 'sound', value: 'cheer' }];

    const normal = start(load());
    normal.env.api.seek(cheer, park, 0.5, '');
    const calm = start(load({ reduced: true }));
    calm.env.api.seek(cheer, park, 0.5, '');

    assert.ok(sparkle(normal.canvas) - sparkle(calm.canvas) >= 55, `${sparkle(normal.canvas)} against ${sparkle(calm.canvas)}`);
});

test('a ball that is moving leaves a trail, except for someone who prefers less motion', () => {
    const ball = { id: 'ball', kind: 'ball' };
    const script = { version: 1, outcome: 'T', duration: 1, actors: [ball], cues: [],
        tracks: [{ actorId: 'ball', keys: [{ t: 0, x: 0, y: 20, z: 3 }, { t: 1, x: 0, y: 250, z: 3 }] }] };
    const trailOf = canvas => canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[2] === args[3] && args[2] <= 4 && args[2] >= 2).length;

    const normal = start(load());
    normal.env.api.seek(script, park, 0.5, '');
    const calm = start(load({ reduced: true }));
    calm.env.api.seek(script, park, 0.5, '');

    assert.ok(trailOf(normal.canvas) > trailOf(calm.canvas));
});

test('a ball that is too high to see is shown as an arrow at the top edge', () => {
    const high = { version: 1, outcome: 'T', duration: 1, actors: [{ id: 'ball', kind: 'ball' }], cues: [],
        tracks: [{ actorId: 'ball', keys: [{ t: 0, x: 0, y: 200, z: 300 }] }] };
    const { env, canvas } = start();
    env.api.seek(high, park, 0, '');

    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'moveTo' && args[1] === 4), 'the arrow tip is at the top');
});

test('the backdrop uses ordered dither for the grass, dirt and track', () => {
    const env = load();
    start(env);

    const patterns = env.created.flatMap(c => c.ctx.calls).filter(([name]) => name === 'createPattern');
    assert.ok(patterns.length >= 5, `${patterns.length} dither patterns`);
});

test('prefersReducedMotion follows the device setting', () => {
    assert.equal(load().api.prefersReducedMotion(), false);
    assert.equal(load({ reduced: true }).api.prefersReducedMotion(), true);
});

// ── Phase F: a play never hangs ──────────────────────────────────────────────

test('in a tab nobody is looking at, a play finishes at once instead of waiting for frames that never come', async () => {
    const env = load();
    start(env);
    env.document.hidden = true;

    assert.equal(await env.api.play(SCRIPT, park, 1), true);
    assert.equal(env.api.playing, false);
    assert.deepEqual(env.heard, []);        // and quietly
    assert.equal(env.pending(), 0);         // no animation frames were asked for
});

test('going to another tab in the middle of a play ends it', async () => {
    const env = load();
    start(env);
    const done = env.api.play(SCRIPT, park, 1);
    env.advance(300);
    assert.equal(env.api.playing, true);

    env.document.hidden = true;
    env.document.listeners.visibilitychange();

    assert.equal(await done, true);
    assert.equal(env.api.playing, false);
});

test('coming back to the tab does not end a play', async () => {
    const env = load();
    start(env);
    const done = env.api.play(SCRIPT, park, 1);

    env.document.hidden = false;
    env.document.listeners.visibilitychange();

    assert.equal(env.api.playing, true);
    env.api.skip();
    await done;
});

test('a play whose frames stop coming is finished by the watchdog', async () => {
    const env = load();
    start(env);
    env.api._internals.config.guardSlack = 0.05;
    const quick = { ...SCRIPT, duration: 0.05 };

    // no frames are ever run, so only the watchdog can finish it
    const result = await Promise.race([
        env.api.play(quick, park, 1),
        new Promise(resolve => setTimeout(() => resolve('hung'), 1500)),
    ]);

    assert.equal(result, true);
    assert.equal(env.api.playing, false);
});

test('a play that finishes in time does not leave the watchdog to fire later', async () => {
    const env = load();
    start(env);
    env.api._internals.config.guardSlack = 0.05;
    const done = env.api.play({ ...SCRIPT, duration: 0.05 }, park, 1);
    env.advance(200);                       // finishes normally
    assert.equal(await done, false);

    const next = env.api.play(SCRIPT, park, 1);   // a new play, with a watchdog of its own
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(env.api.playing, true);    // the first play's watchdog (0.1 s) must not cut the new play short
    env.api.skip();
    await next;
});

// ── Phase F: text stays readable when the picture is shown small, and follows resizing ─────────────────

const fontOf = canvas => Number(/(\d+)px/.exec(canvas.ctx.font)[1]);

test('text is sized so that it comes out readable on the screen, whatever size the picture is shown at', () => {
    const env = load();
    const canvas = fakeCanvas();
    env.api.init(canvas);
    const { textSize } = env.api._internals;

    canvas.clientWidth = 856;                  // a laptop
    assert.equal(textSize(11, 13), 13);
    assert.equal(textSize(9, 8), 8);
    canvas.clientWidth = 640;                  // exactly as drawn
    assert.equal(textSize(11, 13), 13);
    canvas.clientWidth = 358;                  // a phone: about half size
    assert.equal(textSize(11, 13), 20);
    assert.equal(textSize(9, 8), 16);
    canvas.clientWidth = 0;                    // not laid out yet: as drawn
    assert.equal(textSize(11, 13), 13);
});

test('the message box is drawn with bigger letters on a phone', () => {
    const wide = load();
    const wideCanvas = fakeCanvas();
    wideCanvas.clientWidth = 900;
    wide.api.init(wideCanvas);
    wide.api.seek(SCRIPT, park, 1.6, '');

    const phone = load();
    const phoneCanvas = fakeCanvas();
    phoneCanvas.clientWidth = 358;
    phone.api.init(phoneCanvas);
    phone.api.seek(SCRIPT, park, 1.6, '');

    assert.equal(fontOf(wideCanvas), 13);
    assert.ok(fontOf(phoneCanvas) >= 19, `phone font ${fontOf(phoneCanvas)}`);
});

test('the message box grows with its text so that a long line still fits', () => {
    const env = load();
    const canvas = fakeCanvas();
    canvas.clientWidth = 358;
    env.api.init(canvas);
    const long = { ...SCRIPT, cues: [{ t: 0, kind: 'banner', value: 'Joe Smith singles to left center field and two runs score.' }] };
    env.api.seek(long, park, 0, '');

    const boxes = canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[0] === 96 && args[1] === 8);
    assert.equal(boxes.length, 1);
    assert.ok(boxes[0][1][3] > 12 + 20 * 2, `box ${boxes[0][1][3]} tall: more than one line`);
});

test('when the picture is resized it is drawn again at the same moment, not back at the start', () => {
    const env = load();
    const canvas = fakeCanvas();
    env.api.init(canvas);
    env.api.seek(SCRIPT, park, 1.6, '');
    const banners = () => fillTextsOf(canvas).filter(t => t === 'RUTH SINGLES.').length;
    const before = banners();

    env.resize();

    assert.equal(banners(), before + 1, 'the banner of that moment is drawn again');
});

test('resizing during a play does not draw over it', async () => {
    const env = load();
    const canvas = fakeCanvas();
    env.api.init(canvas);
    const done = env.api.play(SCRIPT, park, 1);
    const calls = canvas.ctx.calls.length;

    env.resize();                             // the play draws its own frames

    assert.equal(canvas.ctx.calls.length, calls);
    env.api.skip();
    await done;
});

// ── The crowd only sparkles where the stands really are ──────────────────────────────────────────────────

const GREEN = [46, 106, 28];
const STANDS = [38, 38, 48];

// where the 2x2 sparkles are, found by taking away what the same picture has without a cheer
function sparkles(pixelAt) {
    const cheer = scriptOf(mover('f', 'fielder', 'stand', [0, 90], [0, 90]));
    cheer.cues = [{ t: 0.2, kind: 'sound', value: 'cheer' }];
    const draw = script => {
        const env = load({ pixelAt });
        const { canvas } = start(env);
        env.api.seek(script, park, 0.5, '');
        return canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[2] === 2 && args[3] === 2).map(([, args]) => [args[0], args[1]]);
    };
    const quiet = draw({ ...cheer, cues: [] }).map(String);
    return draw(cheer).filter(p => !quiet.includes(String(p)));
}

test('the crowd sparkles only on seats that can be seen, not where the field is drawn over the stands', () => {
    // the top quarter of the picture is stands; everything below is grass
    const dots = sparkles((x, y) => (y < 100 ? STANDS : GREEN));

    assert.ok(dots.length >= 55, `${dots.length} sparkles`);
    assert.ok(dots.every(([, y]) => y < 101), `a sparkle below the stands at y=${Math.max(...dots.map(d => d[1]))}`);
});

test('and not on stands that are covered by something other than grass either (a sign, the wall)', () => {
    // stands only to the left of x = 200
    const dots = sparkles((x) => (x < 200 ? STANDS : [184, 74, 74]));

    assert.ok(dots.length >= 55);
    assert.ok(dots.every(([x]) => x < 201));
});

test('if the picture cannot be read back there is no sparkle at all, rather than sparkle over the field', () => {
    const dots = sparkles(() => { throw new Error('tainted canvas'); });

    assert.equal(dots.length, 0);
});

test('the sparkles are spread over the stands, not bunched in one spot', () => {
    const dots = sparkles(() => STANDS);
    const columns = new Set(dots.map(([x]) => Math.floor(x / 80)));

    assert.ok(columns.size >= 5, `${columns.size} of 8 columns`);
});

// ── Sharp text: a second canvas at the screen's own resolution ───────────────────────────────────────────

// a picture shown `shown` wide and `high` tall, with a text canvas over it
function withOverlay({ shown = 400, high = 250, dpr = 1 } = {}) {
    const env = load({ dpr });
    const canvas = fakeCanvas();
    const overlay = fakeCanvas();
    canvas.clientWidth = shown;
    canvas.clientHeight = high;
    env.api.init(canvas, overlay);
    return { env, canvas, overlay };
}

test('with a text canvas, labels stay sharp while wall distances stay behind the players', () => {
    const { env, canvas, overlay } = withOverlay();
    env.api.seek(SCRIPT, park, 1.6, '');          // a banner is showing, and the sign is on the wall

    const onText = fillTextsOf(overlay);
    assert.ok(onText.includes('RUTH SINGLES.'), 'the message');
    assert.ok(onText.includes('MINILEAGUE BASEBALL'), 'the sign');
    assert.ok(!onText.includes('400'), 'wall distances are not on the overlay');
    assert.ok(fillTextsOf(canvas).includes('400'), 'wall distances are on the picture');
    const calls = canvas.ctx.calls;
    const number = calls.findIndex(([name, args]) => name === 'fillText' && args[0] === '400');
    const figure = calls.findIndex(([name]) => name === 'translate');
    assert.ok(number >= 0 && figure > number, 'wall distances are painted before player sprites');
});

test('name tags and OUT flags go on the text canvas too', () => {
    const { env, canvas, overlay } = withOverlay();
    const script = scriptOf(mover('f', 'fielder', 'catch', [0, 90], [0, 90]), mover('r', 'runner', 'out', [0, 60], [40, 60]));

    env.api.seek(script, park, 0.5, '');          // the fielder is in the middle of a catch
    assert.ok(fillTextsOf(overlay).includes('RUTH'));

    env.api.seek(script, park, 3.5, '');          // the runner has just been called out
    assert.ok(fillTextsOf(overlay).includes('OUT'));
    assert.ok(fillTextsOf(canvas).includes('400'));
    assert.ok(!fillTextsOf(canvas).includes('OUT'));
});

test('the text canvas has as many pixels as the screen shows, so on a high-density display it is sharper', () => {
    const plain = withOverlay({ shown: 400, high: 250, dpr: 1 });
    const dense = withOverlay({ shown: 400, high: 250, dpr: 3 });

    assert.deepEqual([plain.overlay.width, plain.overlay.height], [400, 250]);
    assert.deepEqual([dense.overlay.width, dense.overlay.height], [1200, 750]);
    // and the pixel picture is drawn at twice 640x400
    assert.deepEqual([dense.canvas.width, dense.canvas.height], [1280, 800]);
});

test('text is still placed in the picture\'s own 640x400 units, scaled up to the text canvas', () => {
    const { env, overlay } = withOverlay({ shown: 320, high: 200, dpr: 2 });
    env.api.seek(SCRIPT, park, 1.6, '');

    const transforms = overlay.ctx.calls.filter(([name]) => name === 'setTransform').map(([, args]) => args);
    assert.deepEqual(transforms[transforms.length - 1], [1, 0, 0, 1, 0, 0]);   // 640 wide drawn on 640 pixels: 1 to 1
});

test('the text canvas is cleared before the text is drawn again, so old text does not pile up', () => {
    const { env, overlay } = withOverlay();
    env.api.seek(SCRIPT, park, 1.6, '');
    const clears = () => overlay.ctx.calls.filter(([name]) => name === 'clearRect').length;
    const before = clears();

    env.api.seek(SCRIPT, park, 0.5, '');

    assert.equal(clears(), before + 1);
});

test('when the picture is resized the text canvas follows, so the text stays sharp', () => {
    const { env, canvas, overlay } = withOverlay({ shown: 400, high: 250, dpr: 2 });
    env.api.seek(SCRIPT, park, 1.6, '');
    assert.equal(overlay.width, 800);

    canvas.clientWidth = 200;
    canvas.clientHeight = 125;
    env.resize();

    assert.deepEqual([overlay.width, overlay.height], [400, 250]);
    assert.ok(fillTextsOf(overlay).filter(t => t === 'RUTH SINGLES.').length >= 2, 'drawn again at the new size');
});

test('before the picture has a size, the text canvas is the size of the picture itself', () => {
    const env = load({ dpr: 1 });
    const canvas = fakeCanvas();
    const overlay = fakeCanvas();
    env.api.init(canvas, overlay);            // clientWidth is not known yet

    assert.deepEqual([overlay.width, overlay.height], [640, 400]);
});

test('the lettering is still drawn if there is no text canvas (it goes on the picture)', () => {
    const env = load();
    const canvas = fakeCanvas();
    env.api.init(canvas);
    env.api.seek(SCRIPT, park, 1.6, '');

    assert.ok(fillTextsOf(canvas).includes('RUTH SINGLES.'));
    assert.ok(fillTextsOf(canvas).includes('MINILEAGUE BASEBALL'));
});

// ── Scenes around the plays: the cheer pose, jumping, the standing crowd and the score card ──────────────────

test('the crowd is standing from the "stand" cue to the end, and it sparkles while it is', () => {
    const { standing, cheering } = load().api._internals;
    const cues = [{ t: 2, kind: 'effect', value: 'stand' }];

    assert.equal(standing(cues, 1.9), false);
    assert.equal(standing(cues, 2), true);
    assert.equal(standing(cues, 30), true);
    assert.equal(cheering(cues, 30), true);
    assert.equal(standing([{ t: 0, kind: 'effect', value: 'burst' }], 1), false);   // a burst is not the crowd getting up
});

test('only a "burst" effect draws the burst: other effects are left to their own drawing', () => {
    const burst = scriptOf({ actor: { id: 'f', kind: 'fielder', team: 'home' }, track: { actorId: 'f', keys: [{ t: 0, x: 0, y: 90, z: 0, pose: 'stand' }] } });
    const polygons = canvas => canvas.ctx.calls.filter(([name]) => name === 'closePath').length;

    burst.cues = [{ t: 0, kind: 'effect', value: 'stand' }];
    const a = start(load({ reduced: true }));
    a.env.api.seek(burst, park, 0.5, '');
    burst.cues = [{ t: 0, kind: 'effect', value: 'burst', x: 0, y: 250 }];
    const b = start(load({ reduced: true }));
    b.env.api.seek(burst, park, 0.5, '');

    assert.ok(polygons(b.canvas) > polygons(a.canvas), 'the burst is a star; the standing crowd is not');
});

test('fans on their feet are drawn as they get up, and all at once for someone who prefers less motion', () => {
    const stand = scriptOf({ actor: { id: 'f', kind: 'fielder', team: 'home' }, track: { actorId: 'f', keys: [{ t: 0, x: 0, y: 90, z: 0, pose: 'stand' }] } });
    stand.cues = [{ t: 0, kind: 'effect', value: 'stand' }];
    const tall = canvas => canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[2] === 2 && args[3] >= 4 && args[3] <= 7).length;

    const early = start(load({ reduced: true }));
    early.env.api.seek(stand, park, 0.05, '');       // calm: the crowd is already up
    const late = start(load());
    late.env.api.seek(stand, park, 0.05, '');        // normal: they are only just getting up

    assert.ok(tall(early.canvas) > tall(late.canvas), `${tall(early.canvas)} against ${tall(late.canvas)}`);
});

test('the score card is the newest card cue so far, split into lines', () => {
    const { activeCard } = load().api._internals;
    const cues = [{ t: 1, kind: 'card', value: 'FINAL\nNYY   3\nCIN   5' }, { t: 5, kind: 'card', value: 'OTHER' }];

    assert.equal(activeCard(cues, 0.5), null);
    assert.deepEqual(Array.from(activeCard(cues, 2)), ['FINAL', 'NYY   3', 'CIN   5']);
    assert.deepEqual(Array.from(activeCard(cues, 6)), ['OTHER']);
});

test('a score card is written out and replaces the message box', () => {
    const { env, overlay } = withOverlay();
    const script = scriptOf({ actor: { id: 'f', kind: 'fielder', team: 'home' }, track: { actorId: 'f', keys: [{ t: 0, x: 0, y: 90, z: 0, pose: 'stand' }] } });
    script.cues = [{ t: 0, kind: 'card', value: 'FINAL\nNYY   3\nCIN   5\nW: Smith' }];
    env.api.seek(script, park, 0.5, 'TOP OF INNING 1');

    const texts = fillTextsOf(overlay);
    for (const line of ['FINAL', 'NYY   3', 'CIN   5', 'W: Smith']) assert.ok(texts.includes(line), line);
    assert.ok(!texts.includes('TOP OF INNING 1'), 'the card takes the place of the message box');
    assert.ok(texts.includes('MINILEAGUE BASEBALL'), 'the sign is still on the wall');
});

test('a figure with both arms up is drawn in the cheer pose, front and side', () => {
    const { figureParts } = load().api._internals;
    const actor = { id: 'm', kind: 'teammate', team: 'home' };
    const armsUp = parts => parts.filter(p => p.y <= -18.5 && p.h >= 6);   // a tall arm reaching above the head

    assert.ok(armsUp(figureParts(actor, { pose: 'cheer', segT: 0 }, 'front', 0)).length >= 2);
    assert.ok(armsUp(figureParts(actor, { pose: 'cheer', segT: 0 }, 'side', 0)).length >= 2);
    assert.equal(armsUp(figureParts(actor, { pose: 'stand', segT: 0 }, 'front', 0)).length, 0);
});

test('a figure that is off the ground is drawn higher than its shadow', () => {
    const grounded = scriptOf({ actor: { id: 'm', kind: 'teammate', team: 'home' }, track: { actorId: 'm', keys: [{ t: 0, x: 0, y: 60, z: 0, pose: 'cheer' }] } });
    const jumping = scriptOf({ actor: { id: 'm', kind: 'teammate', team: 'home' }, track: { actorId: 'm', keys: [{ t: 0, x: 0, y: 60, z: 1.6, pose: 'cheer' }] } });
    const topOf = canvas => Math.min(...canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[3] > 0).map(([, args]) => args[1]));
    const translations = canvas => canvas.ctx.calls.filter(([name, args]) => name === 'translate' && args[0] === 0 && args[1] < 0).length;

    const a = start();
    a.env.api.seek(grounded, park, 0, '');
    const b = start();
    b.env.api.seek(jumping, park, 0, '');

    assert.equal(translations(a.canvas), 0);
    assert.ok(translations(b.canvas) >= 1);
    void topOf;
});

// ── Random events: rain, a man lying down, a dome roof ───────────────────────────────────────────

test('it rains from a "rain" cue until the next "clear" cue', () => {
    const { raining } = load().api._internals;
    const cues = [{ t: 5, kind: 'effect', value: 'clear' }, { t: 1, kind: 'effect', value: 'rain' }, { t: 9, kind: 'effect', value: 'rain' }];

    assert.equal(raining(cues, 0.5), false);
    assert.equal(raining(cues, 1), true);
    assert.equal(raining(cues, 4.9), true);
    assert.equal(raining(cues, 5.1), false);
    assert.equal(raining(cues, 9.5), true);
});

test('rain darkens the picture and draws streaks, but for someone who prefers less motion only the grey light', () => {
    const script = scriptOf({ actor: { id: 'f', kind: 'fielder', team: 'home' }, track: { actorId: 'f', keys: [{ t: 0, x: 0, y: 90, z: 0, pose: 'stand' }] } });
    script.cues = [{ t: 0, kind: 'effect', value: 'rain' }];
    const streaks = canvas => canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[2] === 1 && args[3] === 6).length;
    const wash = canvas => canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[0] === 0 && args[1] === 0 && args[2] === 640 && args[3] === 400).length;

    const normal = start(load());
    normal.env.api.seek(script, park, 1, '');
    const calm = start(load({ reduced: true }));
    calm.env.api.seek(script, park, 1, '');
    script.cues = [];
    const dry = start(load());
    dry.env.api.seek(script, park, 1, '');

    assert.ok(streaks(normal.canvas) >= 100);
    assert.equal(streaks(calm.canvas), 0);
    assert.equal(streaks(dry.canvas), 0);
    assert.ok(wash(calm.canvas) > wash(dry.canvas), 'the grey light is there even when calm');
});

test('a man who is down is drawn lying along the ground', () => {
    const down = scriptOf({ actor: { id: 'p', kind: 'fielder', team: 'home' }, track: { actorId: 'p', keys: [{ t: 0, x: 0, y: 90, z: 0, pose: 'down' }] } });
    const up = scriptOf({ actor: { id: 'p', kind: 'fielder', team: 'home' }, track: { actorId: 'p', keys: [{ t: 0, x: 0, y: 90, z: 0, pose: 'stand' }] } });
    const turned = canvas => canvas.ctx.calls.filter(([name]) => name === 'rotate').length;
    const a = start(); a.env.api.seek(down, park, 0, '');
    const b = start(); b.env.api.seek(up, park, 0, '');

    assert.ok(turned(a.canvas) >= 1);
    assert.equal(turned(b.canvas), 0);
});

test('a dome gets a roof across the top of the picture; an open park does not', () => {
    const roofs = canvas => canvas.ctx.calls.filter(([name, args]) => name === 'fillRect' && args[0] === 0 && args[1] === 0 && args[2] === 640 && args[3] === 34).length;
    const script = scriptOf({ actor: { id: 'p', kind: 'fielder', team: 'home' }, track: { actorId: 'p', keys: [{ t: 0, x: 0, y: 90, z: 0, pose: 'stand' }] } });

    const env = load();
    const open = start(env);
    open.env.api.seek(script, park, 0, '');
    const openRoofs = env.created.flatMap(c => c.ctx.calls).filter(([name, args]) => name === 'fillRect' && args[2] === 640 && args[3] === 34).length;

    const envDome = load();
    const dome = start(envDome);
    dome.env.api.seek(script, { ...park, isDome: true }, 0, '');
    const domeRoofs = envDome.created.flatMap(c => c.ctx.calls).filter(([name, args]) => name === 'fillRect' && args[2] === 640 && args[3] === 34).length;

    assert.equal(openRoofs, 0);
    assert.ok(domeRoofs >= 1);
    void roofs;
});

// ── Wind-ups: the leg kick, the sidearm arm, and left-handers ────────────────────────────────────────────────

test('in the leg-kick pose one leg is drawn much shorter than the other, and the hands are together at the chest', () => {
    const { figureParts } = load().api._internals;
    const pitcher = { id: 'pitcher', kind: 'pitcher', team: 'home', hand: 'right' };
    const legs = parts => parts.filter(p => p.y === -5 && p.w === 2.3).map(p => p.h);

    const kicking = legs(figureParts(pitcher, { pose: 'kick', segT: 0 }, 'front', 0));
    const standing = legs(figureParts(pitcher, { pose: 'stand', segT: 0 }, 'front', 0));

    assert.equal(kicking.length, 2);
    assert.ok(Math.abs(kicking[0] - kicking[1]) >= 3, `${kicking}`);
    assert.equal(standing[0], standing[1]);
    assert.ok(figureParts(pitcher, { pose: 'kick', segT: 0 }, 'front', 0).some(p => p.w === 3.2 && p.h === 2.4), 'hands together');
});

test('in the sidearm pose the throwing arm is a long bar out to the side, not up over the head', () => {
    const { figureParts } = load().api._internals;
    const pitcher = { id: 'pitcher', kind: 'pitcher', team: 'home' };

    const side = figureParts(pitcher, { pose: 'sidearm', segT: 0 }, 'front', 0);
    const over = figureParts(pitcher, { pose: 'throw', segT: 0 }, 'front', 0);

    const bar = parts => parts.some(p => p.w >= 6 && p.h < 2 && p.y < -10);   // (the belt is a bar too, down at the waist)
    assert.ok(bar(side), 'a horizontal arm');
    assert.ok(!bar(over));
    assert.ok(over.some(p => p.y <= -18 && p.h >= 6), 'the overhand arm reaches above the head');
});

test('the glove stays opposite the throwing hand from both front and back', () => {
    const mirrored = (kind, hand, facing) => {
        const script = scriptOf({ actor: { id: kind, kind, team: 'home', hand }, track: { actorId: kind, keys: [{ t: 0, x: 0, y: 60, z: 0, pose: 'throw', facing }] } });
        const { env, canvas } = start();
        env.api.seek(script, park, 0, '');
        return canvas.ctx.calls.filter(([name, args]) => name === 'scale' && args[0] === -1 && args[1] === 1).length;
    };

    for (const kind of ['pitcher', 'fielder', 'catcher']) {
        assert.ok(mirrored(kind, 'right', 'front') >= 1);
        assert.equal(mirrored(kind, 'left', 'front'), 0);
        assert.equal(mirrored(kind, 'right', 'back'), 0);
        assert.ok(mirrored(kind, 'left', 'back') >= 1);
    }
});

// ── Batter stances ───────────────────────────────────────────────────────────────────────────────────────────

const batterScript = (stance, pose = 'stand', x = -3.5) => scriptOf({
    actor: { id: 'batter', kind: 'batter', team: 'visitor', hand: x < 0 ? 'right' : 'left', stance },
    track: { actorId: 'batter', keys: [{ t: 0, x, y: 0, z: 0, pose }] },
});

test('a power hitter stands with his feet wide apart, a slap hitter and an ordinary one do not', () => {
    const { figureParts } = load().api._internals;
    const feet = stance => figureParts({ id: 'b', kind: 'batter', team: 'visitor', stance }, { pose: 'stand', segT: 0 }, 'side', 0)
        .filter(p => p.y === -5 && p.w === 2.3).map(p => p.x);
    const gap = stance => Math.abs(feet(stance)[0] - feet(stance)[1]);

    assert.ok(gap('power') > gap('balanced') + 3, `${gap('power')} against ${gap('balanced')}`);
    assert.equal(gap('slap'), gap('balanced'));
    assert.equal(gap(undefined), gap('balanced'));
});

test('a hitter running is not in his stance: the feet go back to the stride of a runner', () => {
    const { figureParts } = load().api._internals;
    const gapOf = pose => {
        const f = figureParts({ id: 'b', kind: 'batter', team: 'visitor', stance: 'power' }, { pose, segT: 0 }, 'side', 0)
            .filter(p => p.y === -5 && p.w === 2.3).map(p => p.x);
        return Math.abs(f[0] - f[1]);
    };

    assert.ok(gapOf('stand') > gapOf('run') + 1.5);
});

test('the bat is held high and back by a power hitter, short and level by a slap hitter', () => {
    const tipOf = stance => {
        const { env, canvas } = start();
        env.api.seek(batterScript(stance), park, 0, '');
        // the bat is the only line drawn with lineTo after a moveTo at the hands: take the last stroked segment
        const calls = canvas.ctx.calls;
        const last = calls.map(([n], i) => [n, i]).filter(([n]) => n === 'lineTo').pop()[1];
        const to = calls[last][1], from = calls[last - 1][1];
        return { dx: to[0] - from[0], dy: to[1] - from[1] };
    };
    const power = tipOf('power'), slap = tipOf('slap'), plain = tipOf('balanced');
    const length = t => Math.hypot(t.dx, t.dy);

    assert.ok(length(power) > length(plain) && length(plain) > length(slap), `${length(power)} ${length(plain)} ${length(slap)}`);
    assert.ok(power.dx < plain.dx, 'it leans further back for the power hitter');
    assert.ok(Math.abs(slap.dy) < Math.abs(plain.dy), 'and lies flatter for the slap hitter');
});

test('a hitter waiting for the pitch waggles his bat, but not for someone who prefers less motion', () => {
    const angleAt = (env, t) => {
        const { canvas } = start(env);
        env.api.seek(batterScript('balanced'), park, t, '');
        const calls = canvas.ctx.calls;
        const last = calls.map(([n], i) => [n, i]).filter(([n]) => n === 'lineTo').pop()[1];
        return calls[last][1][0] - calls[last - 1][1][0];
    };
    const moved = new Set([0, 0.1, 0.2, 0.3, 0.4].map(t => angleAt(load(), t).toFixed(2)));
    const calm = new Set([0, 0.1, 0.2, 0.3, 0.4].map(t => angleAt(load({ reduced: true }), t).toFixed(2)));

    assert.ok(moved.size > 1);
    assert.equal(calm.size, 1);
});

test('a slap hitter crouches while he waits and swings, and stands up to run', () => {
    const squashes = (stance, pose) => {
        const { env, canvas } = start();
        env.api.seek(batterScript(stance, pose), park, 0, '');
        return canvas.ctx.calls.filter(([n, a]) => n === 'scale' && a[0] === 1 && a[1] < 1).length;
    };

    assert.ok(squashes('slap', 'stand') >= 1);
    assert.ok(squashes('slap', 'swing') >= 1);
    assert.equal(squashes('slap', 'run'), 0);
    assert.equal(squashes('balanced', 'stand'), 0);
    assert.equal(squashes('power', 'stand'), 0);
});

// ── The ball in a hand ───────────────────────────────────────────────────────

const stillFigure = (kind, x, y, pose = 'stand', hand = 'right') => ({ actor: { id: kind, kind, hand }, st: { x, y, z: 0, pose, vx: 0, vy: 0 } });
const stillBall = (x, y, z) => ({ x, y, z, vx: 0, vy: 0 });

test('a ball that sits beside the pitcher is in his throwing hand, on the side he throws from', () => {
    const { holdingHand, project } = load().api._internals;
    const ball = stillBall(2.4, 59.5, 12);
    const origin = project(0, 60.5, 0);

    const right = holdingHand([stillFigure('pitcher', 0, 60.5)], ball);
    const left = holdingHand([stillFigure('pitcher', 0, 60.5, 'stand', 'left')], stillBall(-2.4, 59.5, 12));

    assert.ok(right.x < origin.x, 'from the front, his right hand appears on screen-left');
    assert.ok(left.x > origin.x, 'from the front, his left hand appears on screen-right');
    assert.equal(origin.x - right.x, left.x - origin.x, 'mirror images');
    assert.ok(right.y < origin.y, 'above the ground');
});

test('his hand moves with the wind-up: hands together over his head, then at his chest, then up in the throw', () => {
    const { holdingHand, project } = load().api._internals;
    const origin = project(0, 60.5, 0);
    const at = pose => holdingHand([stillFigure('pitcher', 0, 60.5, pose)], stillBall(2.4, 59.5, 12));

    assert.equal(at('windup').x, Math.round(origin.x));
    assert.equal(at('kick').x, Math.round(origin.x));
    assert.ok(at('windup').y < at('kick').y, 'over his head is above his chest');
    assert.ok(at('throw').y < at('stand').y, 'the throwing arm goes up');
    assert.ok(Math.abs(at('sidearm').x - origin.x) > Math.abs(at('stand').x - origin.x), 'out to the side for a slider');
});

test('the catcher has it in his glove, which is up and to one side of him', () => {
    const { holdingHand, project } = load().api._internals;
    const origin = project(0, -3, 0);

    const glove = holdingHand([stillFigure('catcher', 0, -3, 'catch')], stillBall(0, -4, 2.5));

    assert.ok(glove.x < origin.x, 'on the glove side');
    assert.ok(glove.y < origin.y - 5, 'well above his feet, not down at his behind');
});

test('a diving fielder holds the caught ball at the rotated glove, away from his feet', () => {
    const { holdingHand, project } = load().api._internals;
    const gloveAt = direction => {
        const fielder = { actor: { id: 'fielder-Left', kind: 'fielder', team: 'home' },
            st: { x: -145, y: 260, z: 0, pose: 'dive', direction, poseAge: 0.2, vx: 0, vy: 0 } };
        return holdingHand([fielder], stillBall(-145, 260, 1.5));
    };
    const feet = project(-145, 260);
    const left = gloveAt(-1), right = gloveAt(1);

    assert.ok(left.x < feet.x - 5, 'leftward dive: the glove reaches to the left');
    assert.ok(right.x > feet.x + 5, 'rightward dive: the glove reaches to the right');
    assert.ok(left.y < feet.y + 5 && right.y < feet.y + 5);
});

test('a fielder keeps a caught ball in the glove when standing, while a pitcher holds it in the throwing hand', () => {
    const { holdingHand, project } = load().api._internals;
    const spot = project(0, 60.5, 0);
    const ball = stillBall(0, 60.5, 2);
    assert.ok(holdingHand([stillFigure('fielder', 0, 60.5)], ball).x > spot.x);
    assert.ok(holdingHand([stillFigure('pitcher', 0, 60.5)], ball).x < spot.x);
});

test('a relay fielder keeps facing the target as he turns and plants', () => {
    const { prepare, sample, facingOf, figureParts } = load().api._internals;
    const actor = { id: 'cutoff', kind: 'fielder', team: 'home' };
    const keys = prepare({ tracks: [{ actorId: actor.id, keys: [
        { t: 0, x: 0, y: 140, pose: 'stand' },
        { t: 1, x: 0, y: 140, pose: 'catch' },
        { t: 1.1, x: 0, y: 140, pose: 'turn', facing: 'left' },
        { t: 1.25, x: 0, y: 140, pose: 'plant' },
        { t: 1.42, x: 0, y: 140, pose: 'throw' },
    ] }] }).tracks.get(actor.id);
    assert.equal(facingOf(actor, sample(keys, 1.15)).dir, -1);
    assert.equal(sample(keys, 1.3).facing, 'left');
    const shoes = pose => figureParts(actor, { pose, distance: 0 }, 'side', 0)
        .filter(p => p.c === '#262626').map(p => p.x);
    assert.ok(Math.max(...shoes('plant')) - Math.min(...shoes('plant')) >
        Math.max(...shoes('catch')) - Math.min(...shoes('catch')));

    const { env, canvas } = start();
    const script = { version: 1, outcome: 'Double', duration: 2, actors: [actor],
        tracks: [{ actorId: actor.id, keys: [
            { t: 0, x: 0, y: 140, pose: 'stand' },
            { t: 1, x: 0, y: 140, pose: 'catch' },
            { t: 1.1, x: 0, y: 140, pose: 'turn', facing: 'left' },
            { t: 1.25, x: 0, y: 140, pose: 'plant' },
            { t: 1.42, x: 0, y: 140, pose: 'throw' },
        ] }], cues: [] };
    env.api.seek(script, park, 1.25, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'RELAY CLOSE-UP'));
});

test('a steal call shows the covering fielder and runner in a temporary inset', () => {
    const { env, canvas } = start();
    const script = { version: 1, outcome: 'CaughtStealing', duration: 2,
        actors: [{ id: 'ball', kind: 'ball' }, { id: 'fielder-Short', kind: 'fielder', team: 'home' },
            { id: 'runner-1', kind: 'runner', team: 'visitor' }],
        tracks: [
            { actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 2 }, { t: 1.4, x: 0, y: 127.3, z: 4 }] },
            { actorId: 'fielder-Short', keys: [{ t: 0, x: -20, y: 120, pose: 'run' },
                { t: 1.2, x: 0, y: 127.3, pose: 'plant' }, { t: 1.4, x: 0, y: 127.3, pose: 'catch' },
                { t: 1.6, x: 0, y: 127.3, pose: 'tag' }] },
            { actorId: 'runner-1', keys: [{ t: 0, x: 63.6, y: 63.6, pose: 'run' },
                { t: 1.6, x: 0, y: 127.3, pose: 'out' }] },
        ], cues: [{ t: 1.6, kind: 'effect', value: 'call', x: 0, y: 127.3 }] };
    env.api.seek(script, park, 1.6, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'STEAL AT SECOND'));
    const before = canvas.ctx.calls.length;
    env.api.seek(script, park, 2.2, '');
    assert.ok(!canvas.ctx.calls.slice(before).some(([name, args]) => name === 'fillText' && args[0] === 'STEAL AT SECOND'));
});

test('a ground out at first shows the stretched glove and batter in a call inset', () => {
    const { env, canvas } = start();
    const script = { version: 1, outcome: 'GroundOut', duration: 2,
        actors: [{ id: 'ball', kind: 'ball' }, { id: 'fielder-First', kind: 'fielder', team: 'home' },
            { id: 'batter', kind: 'batter', team: 'visitor' }],
        tracks: [
            { actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 2 }, { t: 1.4, x: 63.6, y: 63.6, z: 4 }] },
            { actorId: 'fielder-First', keys: [{ t: 0, x: 70, y: 65, pose: 'stand' },
                { t: 1.2, x: 63.6, y: 63.6, pose: 'reach', facing: 'left' },
                { t: 1.4, x: 63.6, y: 63.6, pose: 'reach' }] },
            { actorId: 'batter', keys: [{ t: 0, x: -3.5, y: 0, pose: 'run' },
                { t: 1.55, x: 63.6, y: 63.6, pose: 'out' }] },
        ], cues: [{ t: 1.55, kind: 'effect', value: 'call', x: 63.6, y: 63.6 }] };
    env.api.seek(script, park, 1.55, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'PLAY AT FIRST'));
    const { figureParts, handOf } = env.api._internals;
    const fielder = script.actors[1];
    const shoes = pose => figureParts(fielder, { pose }, 'side', 0).filter(p => p.c === '#262626').map(p => p.x);
    assert.ok(Math.max(...shoes('reach')) - Math.min(...shoes('reach')) >
        Math.max(...shoes('stand')) - Math.min(...shoes('stand')));
    assert.ok(handOf('reach', 'side').x > handOf('catch', 'side').x);
});

test('a close scoring play shows the slide and catcher tag at home without replacing the field', () => {
    const { env, canvas } = start();
    const script = { version: 1, outcome: 'Single', duration: 3,
        actors: [{ id: 'ball', kind: 'ball' }, { id: 'catcher', kind: 'catcher', team: 'home' },
            { id: 'runner-3', kind: 'runner', team: 'visitor' }],
        tracks: [
            { actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 3 },
                { t: 1.6, x: 0, y: -2, z: 4 }] },
            { actorId: 'catcher', keys: [{ t: 0, x: 0, y: -5, pose: 'stand' },
                { t: 1.42, x: 0, y: -2, pose: 'plant', facing: 'left' },
                { t: 1.6, x: 0, y: -2, pose: 'catch' }, { t: 1.72, x: 0, y: 0, pose: 'tag' }] },
            { actorId: 'runner-3', keys: [{ t: 0, x: -63.6, y: 63.6, pose: 'run' },
                { t: 1.1, x: -20, y: 20, pose: 'slide' }, { t: 1.4, x: 0, y: 0, pose: 'safe' }] },
        ], cues: [{ t: 0.8, kind: 'effect', value: 'call', x: 0, y: 0 },
            { t: 1.4, kind: 'effect', value: 'call', x: 0, y: 0 },
            { t: 1.84, kind: 'effect', value: 'platecall', x: 0, y: 0 }] };
    env.api.seek(script, park, 1.2, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'PLAY AT HOME'));
    assert.ok(!canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'SAFE'));
    env.api.seek(script, park, 1.55, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'PLAY AT HOME'));
    assert.ok(!canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'SAFE'));
    env.api.seek(script, park, 1.9, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'SAFE'));
    const before = canvas.ctx.calls.length;
    env.api.seek(script, park, 2.4, '');
    assert.ok(!canvas.ctx.calls.slice(before).some(([name, args]) => name === 'fillText' && args[0] === 'PLAY AT HOME'));

    const lateThrow = { ...script, tracks: script.tracks.map(track => track.actorId === 'ball'
        ? { ...track, keys: [track.keys[0], { t: 2.5, x: 0, y: -2, z: 4 }] } : track) };
    const other = start();
    other.env.api.seek(lateThrow, park, 1.55, '');
    assert.ok(!other.canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'PLAY AT HOME'));
});

test('a distant error identifies the misplay in a temporary close-up while runners react', () => {
    const { env, canvas } = start();
    const script = { version: 1, outcome: 'Error', duration: 3,
        actors: [{ id: 'ball', kind: 'ball' }, { id: 'fielder-Left', kind: 'fielder', team: 'home' },
            { id: 'batter', kind: 'batter', team: 'visitor' }],
        tracks: [
            { actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 2 }, { t: 1, x: -140, y: 260, z: 0.5 },
                { t: 1.5, x: -130, y: 270, z: 0.5 }] },
            { actorId: 'fielder-Left', keys: [{ t: 0, x: -125, y: 245, pose: 'run' },
                { t: 1, x: -140, y: 260, pose: 'crouch' }, { t: 1.1, x: -140, y: 260, pose: 'recover' },
                { t: 1.5, x: -130, y: 270, pose: 'crouch' }] },
            { actorId: 'batter', keys: [{ t: 0, x: -3, y: 0, pose: 'stand' },
                { t: 0.5, x: -3, y: 0, pose: 'run' }, { t: 2.5, x: 63.6, y: 63.6, pose: 'stand' }] },
        ], cues: [{ t: 1, kind: 'effect', value: 'error:booted', x: -140, y: 260 }] };
    assert.equal(env.api._internals.prepare(script).looseUntil, 1.5, 'the ball stays loose until the pickup');
    env.api.seek(script, park, 1.2, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'BOOTED BALL'));
    const before = canvas.ctx.calls.length;
    env.api.seek(script, park, 2.5, '');
    assert.ok(!canvas.ctx.calls.slice(before).some(([name, args]) => name === 'fillText' && args[0] === 'BOOTED BALL'));
    assert.ok(canvas.ctx.calls.slice(0, before).filter(([name]) => name === 'stroke').length >
        canvas.ctx.calls.slice(before).filter(([name]) => name === 'stroke').length, 'the misplay and runner surge add motion marks');
});

test('a misjudged fly keeps its error close-up even when the fielder turns before throwing', () => {
    const { env, canvas } = start();
    const script = { version: 1, outcome: 'Error', duration: 3,
        actors: [{ id: 'ball', kind: 'ball' }, { id: 'fielder-Center', kind: 'fielder', team: 'home' }],
        tracks: [
            { actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 2 }, { t: 1.4, x: 0, y: 285, z: 0 },
                { t: 2, x: 0, y: 295, z: 0.5 }] },
            { actorId: 'fielder-Center', keys: [{ t: 0, x: 0, y: 250, pose: 'run' },
                { t: 0.7, x: 0, y: 240, pose: 'turn' }, { t: 2, x: 0, y: 295, pose: 'crouch' },
                { t: 2.5, x: 0, y: 295, pose: 'throw' }] },
        ], cues: [{ t: 1.4, kind: 'effect', value: 'error:misjudged', x: 0, y: 285 }] };
    env.api.seek(script, park, 1.5, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'MISJUDGED FLY'));
    assert.ok(!canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'RELAY CLOSE-UP'));
});

test('the catcher removes his mask under a plate pop and the discarded mask stays by home', () => {
    const { env, canvas } = start();
    const { figureParts, project } = env.api._internals;
    const catcher = { id: 'catcher', kind: 'catcher', team: 'home' };
    const maskPiece = p => p.x === -2.2 && p.y === -14 && p.w === 4.4 && p.h === 2.6;
    assert.ok(figureParts(catcher, { pose: 'catch', distance: 0 }, 'front', 0).some(maskPiece));
    assert.ok(!figureParts({ ...catcher, maskOff: true }, { pose: 'catch', distance: 0 }, 'front', 0).some(maskPiece));
    const script = { version: 1, outcome: 'PopOut', duration: 3,
        actors: [catcher, { id: 'ball', kind: 'ball' }],
        tracks: [
            { actorId: 'catcher', keys: [{ t: 0, x: 0, y: -5, pose: 'stand' },
                { t: 0.3, x: 0, y: -5, pose: 'maskoff' }, { t: 1.8, x: 10, y: -8, pose: 'catch', facing: 'front' }] },
            { actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 3 }, { t: 1.8, x: 10, y: -8, z: 4, ease: 'arc', apex: 55 }] },
        ], cues: [{ t: 0.5, kind: 'effect', value: 'mask', x: 5, y: -6 }] };
    env.api.seek(script, park, 2, '');
    const where = project(5, -6);
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'ellipse' &&
        Math.abs(args[0] - where.x) < 0.01 && Math.abs(args[1] - (where.y - 1)) < 0.01 && args[2] === 5 && args[3] === 3));
});

test('the diving sprite reaches farther with the glove and trails both feet', () => {
    const { figureParts } = load().api._internals;
    const actor = { id: 'fielder-Left', kind: 'fielder', team: 'home' };
    const parts = pose => figureParts(actor, { pose, distance: 0 }, 'side', 0);
    const nearest = (pieces, color) => Math.min(...pieces.filter(p => p.c === color).map(p => p.x));

    assert.ok(Math.min(...parts('dive').filter(p => p.c === '#8b5a2b').map(p => p.y))
        < Math.min(...parts('catch').filter(p => p.c === '#8b5a2b').map(p => p.y)) - 4);
    assert.ok(nearest(parts('dive'), '#262626') < nearest(parts('catch'), '#262626') - 3);
});

test('a ball that is moving, high, or away from everybody is not in a hand', () => {
    const { holdingHand } = load().api._internals;
    const pitcher = [stillFigure('pitcher', 0, 60.5)];
    const fielder = [stillFigure('fielder', 50, 100)];

    assert.equal(holdingHand(pitcher, { x: 2.4, y: 59.5, z: 12, vx: 0, vy: -170 }), null, 'thrown');
    assert.equal(holdingHand(fielder, stillBall(50, 100, 40)), null, 'a fly ball overhead');
    assert.equal(holdingHand(fielder, stillBall(56, 100, 0.5)), null, 'a ball rolling to rest six feet away');
    assert.notEqual(holdingHand(fielder, stillBall(52, 100, 0.5)), null, 'two feet away, he has it');
});

test('only the men who handle the ball hold it: not a batter or a runner', () => {
    const { holdingHand } = load().api._internals;

    assert.equal(holdingHand([stillFigure('batter', 0, 0), stillFigure('runner', 0, 0)], stillBall(0, 0, 3)), null);
});

test('the nearest man has it', () => {
    const { holdingHand, project } = load().api._internals;
    const near = stillFigure('fielder', 60, 100, 'catch');   // his left-hand glove is on screen-right from the front
    const far = stillFigure('fielder', 62.5, 100, 'stand');   // his hand is to the right of him

    const hand = holdingHand([far, near], stillBall(60.5, 100, 0.5));

    assert.ok(hand.x > project(60, 100, 0).x, "it is in the near man's glove, not the other's hand");
});

test('the ball is drawn at the hand, with no shadow, when somebody has it', () => {
    const env = load();
    const canvas = fakeCanvas();
    env.api.init(canvas);
    const script = {
        version: 1, outcome: 'Test', duration: 1,
        actors: [{ id: 'pitcher', kind: 'pitcher', team: 'home', hand: 'right' }, { id: 'ball', kind: 'ball' }],
        tracks: [
            { actorId: 'pitcher', keys: [{ t: 0, x: 0, y: 60.5, z: 0, pose: 'stand' }] },
            { actorId: 'ball', keys: [{ t: 0, x: 2.4, y: 59.5, z: 12 }] },
        ],
        cues: [],
    };
    canvas.ctx.calls.length = 0;
    env.api.seek(script, { name: '' }, 0, '');

    const ellipses = canvas.ctx.calls.filter(([n]) => n === 'ellipse').length;
    const arcs = canvas.ctx.calls.filter(([n]) => n === 'arc');
    assert.equal(arcs.length, 1, 'the ball is drawn once');
    const figureShadows = ellipses;   // only the pitcher's own
    assert.equal(figureShadows, 1, 'and it has no shadow of its own');
});

// ── The score card ───────────────────────────────────────────────────────────

test('the score card is wide enough for its longest line, and no wider than the picture', () => {
    const { cardLayout } = load().api._internals;
    const ctx = fakeContext();

    const short = cardLayout(ctx, ['FINAL', 'NYY 2', 'BRO 1']);
    const decisions = cardLayout(ctx, ['FINAL', 'NYY 2', 'BRO 1', 'W: Hoyt  L: Newcombe  SV: Pipgras']);
    const absurd = cardLayout(ctx, ['FINAL', 'W: ' + 'Somebody-Long-Name '.repeat(12)]);

    assert.equal(short.w, 300, 'a short card keeps its usual size');
    assert.ok(decisions.w > 300, 'a long line makes it wider');
    ctx.font = `bold ${decisions.size}px "Courier New", monospace`;
    assert.ok(ctx.measureText('W: Hoyt  L: Newcombe  SV: Pipgras').width + 30 <= decisions.w, 'with room to spare inside the border');
    assert.ok(absurd.w <= 600, 'never wider than the picture allows');
    assert.ok(absurd.size < short.size, 'the type shrinks when even that is not enough');
});

test('the score card is drawn with its border around all of its text', () => {
    const env = load();
    const canvas = fakeCanvas();
    env.api.init(canvas);
    const script = {
        version: 1, outcome: 'Test', duration: 1, actors: [], tracks: [],
        cues: [{ t: 0, kind: 'card', value: 'FINAL\nNYY    2\nBRO    1\nW: Hoyt  L: Newcombe  SV: Pipgras' }],
    };
    canvas.ctx.calls.length = 0;
    env.api.seek(script, { name: '' }, 0, '');

    const box = canvas.ctx.calls.filter(([n]) => n === 'fillRect').map(([, a]) => a).find(a => a[2] > 300 && a[2] < 600);
    assert.ok(box, 'a card wider than the usual 300 is drawn');
    const texts = canvas.ctx.calls.filter(([n]) => n === 'fillText').map(([, a]) => a);
    assert.ok(texts.length >= 4);
});

// ── Reactions ────────────────────────────────────────────────────────────────

test('a slumped figure has his head sunk into his shoulders, seen from the front and from the side', () => {
    const { figureParts } = load().api._internals;
    const actor = { id: 'p', kind: 'pitcher', team: 'home' };
    const top = (pose, view) => Math.min(...figureParts(actor, { pose, segT: 0 }, view, 0).map(p => p.y));

    for (const view of ['front', 'side'])
        assert.ok(Math.abs(top('slump', view) - top('stand', view) - 2.4) < 1e-9, view);
});

// ── The ballpark: park identity, the wall's details, the infield and foul ground ─────────────────────────

// The canvas the field without its figures is painted on: the one canvas of the picture's size (the others
// are the little tiles that dithering is made from).
const backdropOf = env => env.created.find(c => c.width === 640 && c.height === 400);

// The colours the backdrop was painted with for a park. (Starting the field builds the default park's backdrop
// first, so only what `show` painted after that is counted.)
function backdropStyles(p) {
    const { env } = start();
    const already = backdropOf(env).ctx.styles.length;
    env.api.show(SCRIPT, p, '');
    return backdropOf(env).ctx.styles.slice(already);
}

test('a park with a wall colour gets a wall of that colour, with a lighter line along its top', () => {
    const styles = backdropStyles({ ...park, wallColor: '#2a6e47' });

    assert.ok(styles.includes('#2a6e47'), 'the wall');
    assert.ok(styles.includes('#75a187'), 'a lighter cap, worked out from it');
    assert.ok(!styles.includes('#b84a4a'), 'and none of the default wall');
});

test('a park with no wall colour, or one that is not a colour, keeps the default wall', () => {
    for (const wallColor of [undefined, null, 'green', '#12', '2a6e47']) {
        const styles = backdropStyles({ ...park, wallColor });

        assert.ok(styles.includes('#b84a4a'), `the default wall for ${wallColor}`);
        assert.ok(styles.includes('#e9a0a0'), 'and its default cap');
    }
});

test('the foul poles are yellow, one at each end of the wall', () => {
    const styles = backdropStyles(park);

    assert.equal(styles.filter(c => c === '#ffd54f').length, 2);
});

test('the posted distances are painted on the picture behind the players', () => {
    const { env, canvas, overlay } = withOverlay();
    env.api.show(SCRIPT, { ...park, leftField: 315, leftCenter: 379, center: 390, rightCenter: 380, rightField: 302 }, '');

    const texts = fillTextsOf(canvas);
    for (const feet of ['315', '379', '390', '380', '302']) assert.ok(texts.includes(feet), `${feet} is on the wall`);
    assert.ok(!fillTextsOf(overlay).includes('390'));
});

test('and they follow a play too, not only the picture between plays', () => {
    const { env, canvas } = withOverlay();
    env.api.seek(SCRIPT, park, 1.6, '');

    assert.ok(fillTextsOf(canvas).includes('400'));
});

test('the sign shows the park\'s name above the game\'s name, in capitals', () => {
    const { env, overlay } = withOverlay();
    env.api.show(SCRIPT, { ...park, name: 'Ebbets Field' }, '');

    const texts = fillTextsOf(overlay);
    assert.ok(texts.includes('EBBETS FIELD'));
    assert.ok(texts.includes('MINILEAGUE BASEBALL'), 'the game is still named');
});

test('a long park name is shrunk to fit the sign, and a short one is not', () => {
    const fontOf = (name, text) => {
        const { env, overlay } = withOverlay();
        env.api.show(SCRIPT, { ...park, name }, '');
        const call = overlay.ctx.calls.find(([n, args]) => n === 'fillText' && args[0] === text);
        return Number(/(\d+)px/.exec(call[2])[1]);
    };

    assert.equal(fontOf('Ebbets Field', 'EBBETS FIELD'), 13);
    assert.ok(fontOf('Oakland-Alameda County Coliseum', 'OAKLAND-ALAMEDA COUNTY COLISEUM') < 11);
});

test('a park with no name shows the game\'s name large on the sign; with a name it is the small line under it', () => {
    const brandFont = name => {
        const { env, overlay } = withOverlay();
        env.api.show(SCRIPT, { ...park, name }, '');
        const sizes = overlay.ctx.calls.filter(([n, args]) => n === 'fillText' && args[0] === 'MINILEAGUE BASEBALL')
            .map(c => Number(/(\d+)px/.exec(c[2])[1]));
        return sizes.at(-1);   // the latest draw (starting the field drew the default park's sign first)
    };

    assert.equal(brandFont(''), 14);
    assert.equal(brandFont('Ebbets Field'), 7);
});

test('the infield has bases with a dark rim, a pitcher\'s rubber, on-deck circles and coaches\' boxes', () => {
    const styles = backdropStyles(park);

    assert.equal(styles.filter(c => c === '#6b4528').length, 3, 'a rim under each of the three bases');
    assert.ok(styles.filter(c => c === '#ffffff').length >= 8, 'three bases, the plate, the rubber and two on-deck rings are white');
    assert.equal(styles.filter(c => c === '#f2f2f2').length >= 4, true, 'chalk: the on-deck rings and the coaches\' boxes, and the foul lines');
});

test('each side has a bullpen in foul ground, and no dugout is drawn', () => {
    const styles = backdropStyles(park);

    assert.equal(styles.filter(c => c === '#5a3a22').length, 2, 'a bench in each bullpen');
    assert.equal(styles.filter(c => c === '#17171f').length, 0, 'no dugout floor');
    assert.equal(styles.filter(c => c === '#8a6a45').length, 0, 'no dugout bench');
});

test('the wall numbers lie in the plane of the wall: square at center, squeezed and slanted toward the poles', () => {
    const { env, canvas } = withOverlay();
    env.api.show(SCRIPT, { ...park, leftField: 315, leftCenter: 379, center: 390, rightCenter: 380, rightField: 302 }, '');
    // The transform set just before a number is drawn (the latest draw of it).
    const matrixOf = text => {
        const calls = canvas.ctx.calls;
        const i = calls.map(([n, a]) => n === 'fillText' && a[0] === text).lastIndexOf(true);
        for (let k = i; k >= 0; k--) if (calls[k][0] === 'transform') return calls[k][1];
        return null;
    };
    const center = matrixOf('390'), pole = matrixOf('315');

    assert.ok(center && pole, 'each number has its own transform');
    assert.ok(Math.abs(center[1]) < Math.abs(pole[1]), 'at center the wall faces us (nearly level baseline); toward the pole it runs up or down the picture');
    assert.ok(pole[0] / pole[3] < center[0] / center[3], 'and the digits are squeezed sideways, compared with their height');
});

test('men in the field carry a brown glove, with eyes; batters and runners have bare hands', () => {
    const { figureParts } = load().api._internals;
    const stand = (kind, view = 'front') => figureParts({ id: 'x', kind, team: 'home' }, { pose: 'stand', segT: 0 }, view, 0);
    const gloves = (kind, view) => stand(kind, view).filter(p => p.c === '#8b5a2b').length;

    for (const kind of ['fielder', 'pitcher', 'catcher']) assert.equal(gloves(kind, 'front'), 1, `${kind} has a glove (front)`);
    assert.equal(gloves('fielder', 'side'), 1, 'and in the side view');
    for (const kind of ['batter', 'runner', 'teammate']) assert.equal(gloves(kind, 'front'), 0, `${kind} has none`);
    assert.ok(stand('fielder').filter(p => p.c === '#ffffff').length >= 2, 'two eyes with whites');
    assert.ok(stand('fielder', 'side').some(p => p.c === '#ffffff'), 'an eye from the side');
});

test('the bat is drawn in the batter\'s box, but not once he is standing on a base', () => {
    const batStrokes = (x, y) => {
        const script = scriptOf({
            actor: { id: 'batter', kind: 'batter', team: 'visitor', hand: 'right', stance: 'balanced' },
            track: { actorId: 'batter', keys: [{ t: 0, x, y, z: 0, pose: 'stand' }] },
        });
        const { env, canvas } = start();
        env.api.seek(script, park, 0, '');
        return canvas.ctx.calls.filter(([n, , ]) => n === 'stroke').length;
    };
    const atPlate = batStrokes(-3.5, 0), atFirst = batStrokes(63.6, 63.6);

    assert.ok(atPlate > atFirst, `bat strokes at the plate ${atPlate}, on first base ${atFirst}`);
});

// ── Fielding, throwing, the bat and the ball ─────────────────────────────────────────────────────────────────

test('a fielder bent over a ground ball has his glove down at the grass; transferring and pitching from the stretch are drawn too', () => {
    const { figureParts } = load().api._internals;
    const fielder = { id: 'f', kind: 'fielder', team: 'home' };
    const pitcher = { id: 'p', kind: 'pitcher', team: 'home' };
    const glove = (actor, pose, view) => figureParts(actor, { pose, segT: 0 }, view, 0).filter(p => p.c === '#8b5a2b');
    const stand = glove(fielder, 'stand', 'front');

    for (const view of ['front', 'side']) {
        const low = glove(fielder, 'crouch', view), chest = glove(fielder, 'transfer', view);
        assert.ok(low.length >= 1 && low.every(p => p.y > -8), `glove low in a crouch (${view})`);
        assert.ok(chest.length >= 1 && chest.every(p => p.y < -9), `glove at the chest in a transfer (${view})`);
        assert.ok(glove(pitcher, 'stretch', view).length >= 1, `hands set in the stretch (${view})`);
    }
    assert.ok(stand.every(p => p.y > -9), 'a standing fielder\'s glove hangs at his side');
});

test('the ball is held in the hand the pose puts it in, for each new pose', () => {
    const { handOf } = load().api._internals;

    for (const view of ['front', 'side'])
        assert.ok(handOf('crouch', view).y > handOf('transfer', view).y, `${view}: low for a grounder, high for a transfer`);
});

test('a swing carries on after the hit: the arms come up in the follow-through', () => {
    const { figureParts } = load().api._internals;
    const arm = segT => figureParts({ id: 'b', kind: 'batter', team: 'home' }, { pose: 'swing', segT }, 'side', 0)
        .filter(p => p.w === 3 && p.h === 1.6).map(p => p.y)[0];

    assert.ok(arm(0.3) < arm(0.05), 'the arm is higher late in the swing');
});

test('a rolling ball reports its progress, so it can hop', () => {
    const { prepare, sample } = load().api._internals;
    const keys = prepare({ tracks: [{ actorId: 'b', keys: [
        { t: 0, x: 0, y: 0, z: 0.5 }, { t: 2, x: 0, y: 40, z: 0.5, ease: 'roll' },
    ] }] }).tracks.get('b');

    const mid = sample(keys, 1);
    assert.equal(mid.rolling, true);
    assert.equal(mid.f, 0.5);
    assert.equal(sample(prepare({ tracks: [{ actorId: 'b', keys: [{ t: 0, x: 0, y: 0, z: 0 }, { t: 2, x: 0, y: 40, z: 0 }] }] }).tracks.get('b'), 1).rolling, false);
});

const batStrokes = (script, t) => {
    const { env, canvas } = start();
    const before = canvas.ctx.styles.length;
    env.api.seek(script, park, t, '');
    return canvas.ctx.styles.slice(before).filter(c => c === '#d2a679').length;
};
const swungAndRan = outcome => ({
    ...scriptOf({
        actor: { id: 'batter', kind: 'batter', team: 'visitor', hand: 'right' },
        track: { actorId: 'batter', keys: [
            { t: 0, x: -3.5, y: 0, z: 0, pose: 'swing' }, { t: 0.5, x: -3.5, y: 0, z: 0, pose: 'run' }, { t: 2, x: 63.6, y: 63.6, z: 0, pose: 'run' },
        ] },
    }), outcome,
});

test('the bat is dropped when the batter leaves the box, and stays where it fell', () => {
    const script = swungAndRan('Single');

    assert.ok(batStrokes(script, 1.8) >= 1, 'a bat lying by the plate while he is running to first');
});

test('on a home run the bat is flipped away: a moment after he leaves the box it is somewhere else than where a dropped one is', () => {
    const where = outcome => {
        const { env, canvas } = start();
        env.api.seek(swungAndRan(outcome), park, 0.8, '');   // 0.3 s after he left the box
        return JSON.stringify(canvas.ctx.calls.filter(([n]) => n === 'moveTo').slice(-3).map(c => c[1]));
    };

    assert.notEqual(where('HomeRun'), where('Single'));
    assert.ok(batStrokes(swungAndRan('HomeRun'), 1.8) >= 1, 'and it lies on the ground once it lands');
});

test('the hitter flips the bat while watching a home-run drive before starting the trot', () => {
    const script = swungAndRan('HomeRun');
    script.tracks[0].keys.splice(1, 0, { t: 0.12, x: -3.5, y: 0, z: 0, pose: 'watch' });
    assert.ok(batStrokes(script, 0.3) >= 1, 'the bat is already in flight while he is still at home');
    const { figureParts } = load().api._internals;
    const hitter = script.actors[0];
    assert.notDeepEqual(figureParts(hitter, { pose: 'watch', distance: 0 }, 'side', 0),
        figureParts(hitter, { pose: 'stand', distance: 0 }, 'side', 0));
});

test('the wall crossing gets a brief visible mark above the fence', () => {
    const { env, canvas } = start();
    const script = scriptOf({ actor: { id: 'f', kind: 'fielder', team: 'home' },
        track: { actorId: 'f', keys: [{ t: 0, x: 0, y: 300, pose: 'stand' }] } });
    script.outcome = 'HomeRun';
    script.cues = [{ t: 1, kind: 'effect', value: 'wallclear', x: 0, y: 330 }];
    env.api.seek(script, park, 1.1, '');
    const at = env.api._internals.project(0, 330, 22);
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'arc' &&
        Math.abs(args[0] - at.x) < 0.01 && Math.abs(args[1] - at.y) < 0.01 && args[2] > 5));
});

test('a home run briefly enlarges the outfielder and ball at the fence', () => {
    const { env, canvas } = start();
    const script = { version: 1, outcome: 'HomeRun', duration: 3,
        actors: [{ id: 'ball', kind: 'ball' }, { id: 'fielder-Left', kind: 'fielder', team: 'home' }],
        tracks: [
            { actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 3 },
                { t: 1.4, x: -145, y: 310, z: 22 }, { t: 1.8, x: -160, y: 340, z: 10 }] },
            { actorId: 'fielder-Left', keys: [{ t: 0, x: -130, y: 250, pose: 'run' },
                { t: 1.3, x: -145, y: 306, pose: 'track', facing: 'back' }] },
        ], cues: [{ t: 1.4, kind: 'effect', value: 'wallclear', x: -145, y: 310 }] };
    env.api.seek(script, park, 1.4, '');
    assert.ok(canvas.ctx.calls.some(([name, args]) => name === 'fillText' && args[0] === 'OVER THE WALL'));
    const before = canvas.ctx.calls.length;
    env.api.seek(script, park, 2.3, '');
    assert.ok(!canvas.ctx.calls.slice(before).some(([name, args]) => name === 'fillText' && args[0] === 'OVER THE WALL'));
});

test('the home-run ball disappears into the seats before the trot ends', () => {
    const base = { version: 1, duration: 3, actors: [{ id: 'ball', kind: 'ball' }],
        tracks: [{ actorId: 'ball', keys: [{ t: 0, x: 0, y: 0, z: 3 },
            { t: 1, x: 50, y: 350, z: 10 }] }],
        cues: [{ t: 1, kind: 'effect', value: 'burst', x: 50, y: 350 }] };
    const shadowAt = load().api._internals.project(50, 350, 0);
    const shadow = outcome => {
        const { env, canvas } = start();
        env.api.seek({ ...base, outcome }, park, 2.1, '');
        return canvas.ctx.calls.some(([name, args]) => name === 'ellipse' &&
            Math.abs(args[0] - shadowAt.x) < 0.01 && Math.abs(args[1] - shadowAt.y) < 0.01);
    };
    assert.equal(shadow('Single'), true);
    assert.equal(shadow('HomeRun'), false);
});

test('a man who never swung (a walk) has no bat to drop', () => {
    const script = { ...swungAndRan('Walk') };
    script.tracks = script.tracks.map(tr => tr.actorId === 'batter' ? { ...tr, keys: tr.keys.map(k => k.pose === 'swing' ? { ...k, pose: 'stand' } : k) } : tr);

    assert.equal(batStrokes(script, 1.8), 0);
});

test('a pitch in flight is named beside the ball, and the name goes when the pitch is over', () => {
    const { env, overlay } = withOverlay();
    const script = {
        ...scriptOf({ actor: { id: 'pitcher', kind: 'pitcher', team: 'home' }, track: { actorId: 'pitcher', keys: [{ t: 0, x: 0, y: 60.5, z: 0, pose: 'stand' }] } }),
        cues: [{ t: 0.2, kind: 'effect', value: 'pitch:Curveball' }],
    };
    script.tracks = script.tracks.map(tr => tr.actorId === 'ball'
        ? { ...tr, keys: [{ t: 0.2, x: 0, y: 55, z: 6 }, { t: 1.0, x: 0, y: 0, z: 3 }] } : tr);

    env.api.seek(script, park, 0.6, '');
    assert.ok(fillTextsOf(overlay).includes('CURVE'));

    const later = withOverlay();
    later.env.api.seek(script, park, 1.6, '');
    assert.ok(!fillTextsOf(later.overlay).includes('CURVE'));
});
