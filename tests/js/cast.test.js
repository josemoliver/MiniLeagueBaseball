// Tests for casting the game to a TV, run with: node --test tests/js/cast.test.js
// Each "page" is its own JavaScript world running the real js/cast.js; a shared fake BroadcastChannel joins them.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const CAST_JS = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'MiniLeague.Web', 'wwwroot', 'js', 'cast.js'), 'utf8');

function world() {
    const members = new Set();
    // `conn` is the presentation connection the page uses: the one it gets from start() (the player's page), or
    // the one it is handed as the receiver (the TV page).
    return function page({ conn = null, receiver = false } = {}) {
        class FakeChannel {
            constructor() { members.add(this); this.onmessage = null; }
            postMessage(data) { for (const m of members) if (m !== this && m.onmessage) m.onmessage({ data }); }
        }
        class FakeRequest {
            getAvailability() { return Promise.resolve({ value: true }); }
            start() { return Promise.resolve(conn); }
        }
        const log = [];   // what the page's animator and sound were asked to do
        const state = { running: false, silenced: [] };
        const sandbox = {
            BroadcastChannel: FakeChannel, PresentationRequest: FakeRequest, JSON, Promise, URL, setTimeout, clearTimeout, console,
            navigator: {
                presentation: receiver ? { receiver: { connectionList: Promise.resolve({ connections: [conn] }) } } : {},
            },
            document: { baseURI: 'http://localhost/' },
            mlbAnimator: {
                play(script) { log.push(['play', script]); return Promise.resolve(false); },
                show() { log.push(['show']); },
                skip() { log.push(['skip']); },
            },
            mlbSound: {
                play(name) { log.push(['sound', name]); return 2; },
                isMuted: () => false,
                setSilenced(on) { state.silenced.push(on); },
                running: () => state.running,
            },
        };
        sandbox.window = sandbox;
        vm.createContext(sandbox);
        vm.runInContext(CAST_JS, sandbox);
        return { cast: sandbox.mlbCast, animator: sandbox.mlbAnimator, sound: sandbox.mlbSound, log, state };
    };
}

function fakeConnection() {
    return {
        state: 'connected', sent: [],
        send(text) { this.sent.push(text); },
        got(msg) { this.onmessage({ data: typeof msg === 'string' ? msg : JSON.stringify(msg) }); },
        sentMessages() { return this.sent.filter(t => t !== 'hello').map(t => JSON.parse(t)); },
    };
}

// A .NET stand-in for the TV page: records what it was told.
function host() {
    const calls = [];
    return { calls, invokeMethodAsync: (name, ...args) => { calls.push([name, ...args]); return Promise.resolve(); } };
}
const tick = () => new Promise(r => setTimeout(r, 5));

test('the Classic view sends every state to the TV at once', async () => {
    const page = world();
    const sender = page(), tv = page(), h = host();
    tv.cast.listen(h);

    sender.cast.send('{"turn":1}', false);
    sender.cast.send('{"turn":2}', false);
    await tick();

    assert.deepEqual(h.calls, [['OnState', '{"turn":1}', false], ['OnState', '{"turn":2}', false]]);
});

test('the Detailed view tells the TV once, then sends each state when the field is drawn again after a play', async () => {
    const page = world();
    const sender = page(), tv = page(), h = host();
    tv.cast.listen(h);

    sender.cast.send('{"turn":1}', true);   // the view was switched on
    sender.cast.send('{"turn":2}', true);   // a play was made: held back until the animation is over
    await tick();
    assert.deepEqual(h.calls, [['OnState', '{"turn":1}', true]]);

    sender.animator.show();                 // the field is drawn again after the play
    await tick();
    assert.deepEqual(h.calls.at(-1), ['OnState', '{"turn":2}', true]);
});

test('plays reach the TV in order, wait for its canvas, and the new score comes after them', async () => {
    const page = world();
    const sender = page(), tv = page(), h = host();
    tv.cast.listen(h);

    sender.cast.send('{"turn":1}', true);
    await tick();
    sender.animator.play('SCRIPT-1', { name: 'park' }, 1);
    sender.cast.send('{"turn":2}', true);
    sender.animator.show();
    await tick();

    // the canvas is not up yet: the play has not started, and the score has not changed
    assert.deepEqual(tv.log.filter(e => e[0] === 'play'), []);
    assert.equal(h.calls.length, 1);

    tv.cast.animatorUp();
    await tick();
    assert.deepEqual(tv.log.filter(e => e[0] === 'play'), [['play', 'SCRIPT-1']]);
    assert.deepEqual(h.calls.at(-1), ['OnState', '{"turn":2}', true]);
});

test('the sender plays its own animation too, and a skip is passed on', async () => {
    const page = world();
    const sender = page(), tv = page(), h = host();
    tv.cast.listen(h);
    tv.cast.animatorUp();

    sender.animator.play('SCRIPT-1', {}, 1);
    sender.animator.skip();
    await tick();

    assert.deepEqual(sender.log, [['play', 'SCRIPT-1'], ['skip']]);
    assert.deepEqual(tv.log, [['play', 'SCRIPT-1'], ['skip']]);
});

test('the TV never passes on what it plays, and its sound stays off', async () => {
    const page = world();
    const sender = page(), tv = page(), other = page(), h = host(), h2 = host();
    tv.cast.listen(h);
    other.cast.listen(h2);
    tv.cast.animatorUp();
    other.cast.animatorUp();

    sender.animator.play('SCRIPT-1', {}, 1);
    await tick();

    assert.equal(tv.log.filter(e => e[0] === 'play').length, 1);   // one play, not an echo of it
    assert.equal(other.log.filter(e => e[0] === 'play').length, 1);
    tv.sound.play('cheer');
    assert.equal(tv.log.filter(e => e[0] === 'sound').length, 0);
});

test('a TV that joins late is brought up to date', async () => {
    const page = world();
    const sender = page(), tv = page(), h = host();
    sender.cast.send('{"turn":7}', true);

    tv.cast.listen(h);   // asks for the game when it opens
    await tick();

    assert.deepEqual(h.calls, [['OnState', '{"turn":7}', true]]);
});

test('messages that are not for the TV are ignored', async () => {
    const page = world();
    const tv = page(), other = page(), h = host();
    tv.cast.listen(h);

    other.cast.send('not json at all', false);   // arrives wrapped, so it is fine
    await tick();
    assert.equal(h.calls.length, 1);

    tv.cast.unlisten();
    other.cast.send('{"turn":2}', false);
    await tick();
    assert.equal(h.calls.length, 1);
});

// ── Sound ────────────────────────────────────────────────────────────────────

async function castingPlayer() {
    const page = world();
    const conn = fakeConnection();
    const sender = page({ conn });
    sender.cast.init(host());
    await sender.cast.start();
    return { sender, conn, page };
}

test('sound stays on the player\'s device until the TV says it is there', async () => {
    const { sender, conn } = await castingPlayer();

    sender.sound.play('walk');

    assert.deepEqual(conn.sentMessages(), []);
    assert.deepEqual(sender.log.filter(e => e[0] === 'sound'), [['sound', 'walk']]);
    assert.deepEqual(sender.state.silenced, []);
});

test('once the TV is there it plays the sounds, and this device goes quiet but keeps the timing', async () => {
    const { sender, conn } = await castingPlayer();
    conn.got('hello');
    assert.deepEqual(sender.state.silenced, [true]);

    const seconds = sender.sound.play('cheer', 0.5);

    assert.equal(seconds, 2);   // the length still comes back: the game waits for tunes to end
    assert.deepEqual(conn.sentMessages(), [{ k: 'sound', name: 'cheer', delay: 0.5 }]);
});

test('a muted game sends no sound to the TV', async () => {
    const { sender, conn } = await castingPlayer();
    conn.got('hello');
    sender.sound.isMuted = () => true;

    sender.sound.play('cheer');

    assert.deepEqual(conn.sentMessages(), []);
});

test('if the TV cannot make sound, or the cast ends, the sound comes back to this device', async () => {
    const { sender, conn } = await castingPlayer();
    conn.got('hello');
    conn.got({ k: 'nosound' });
    assert.deepEqual(sender.state.silenced, [true, false]);

    sender.sound.play('cheer');
    assert.deepEqual(conn.sentMessages(), []);

    conn.got('hello');                 // (it came back later)
    conn.state = 'terminated';
    conn.onterminate();
    assert.deepEqual(sender.state.silenced, [true, false, true, false]);
});

test('the TV plays the sounds it is sent, but not the animation\'s own cues', async () => {
    const page = world();
    const conn = fakeConnection();
    const tv = page({ conn, receiver: true });
    tv.cast.listen(host());
    await tick();
    assert.deepEqual(conn.sent, ['hello']);

    tv.sound.play('crowd');                       // an animation cue on the TV: the player's page sends it as a message
    assert.deepEqual(tv.log.filter(e => e[0] === 'sound'), []);

    conn.got({ k: 'sound', name: 'cheer', delay: 0 });
    assert.deepEqual(tv.log.filter(e => e[0] === 'sound'), [['sound', 'cheer']]);
});

test('the TV tells the player\'s page when the browser will not let it make sound', async () => {
    const page = world();
    const blocked = fakeConnection(), allowed = fakeConnection();
    const tvBlocked = page({ conn: blocked, receiver: true }), tvAllowed = page({ conn: allowed, receiver: true });
    tvBlocked.cast.listen(host());
    tvAllowed.cast.listen(host());
    tvAllowed.state.running = true;
    await tick();

    blocked.got({ k: 'sound', name: 'cheer' });
    allowed.got({ k: 'sound', name: 'cheer' });
    await new Promise(r => setTimeout(r, 500));

    assert.deepEqual(blocked.sentMessages(), [{ k: 'nosound' }]);
    assert.deepEqual(allowed.sentMessages(), []);
});
