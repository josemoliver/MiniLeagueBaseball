// Plays an animated play on a canvas: the "Detailed" field view.
//
// The engine sends a play script (see PlayScript.cs): actors and timed keyframes in ground-plane feet
// (X toward right field, Y toward center field, Z height). This file draws that script from a camera behind
// and above home plate, in the style of the 1984 game: a fixed 640x400 pixel picture that the page scales up.
//
// Nothing here decides anything about the game. It only draws what the script says.
window.mlbAnimator = (() => {
    const W = 640, H = 400;

    // ── Camera ───────────────────────────────────────────────────────────────
    // A pinhole camera behind home plate looking toward center field:
    //   depth = y + B; screenX = cx + f*x/depth; screenY = hy + (K - f*z)/depth   (K = f * camera height)
    // Tuned so home plate sits low and wide, second base about mid-screen and the wall near the top.
    const CAM = { cx: 320, hy: -94.6, f: 600, k: 102220, b: 220 };

    function project(x, y, z = 0) {
        const d = y + CAM.b;
        return { x: CAM.cx + CAM.f * x / d, y: CAM.hy + (CAM.k - CAM.f * z) / d, s: CAM.f / d };
    }

    // ── Park ─────────────────────────────────────────────────────────────────

    const DEFAULT_PARK = {
        name: '', leftField: 330, leftCenter: 375, center: 400, rightCenter: 375, rightField: 330,
        leftWallHeight: 8, rightWallHeight: 8, isDome: false,
    };

    const POLE_EXTRA_FEET = 22;   // how far a foul pole stands above the top of the wall

    // A point beside a foul line: `d` feet along it from home plate and `off` feet outside it. side -1 is the
    // third-base (left) line and 1 the first-base (right) line.
    const alongLine = (side, d, off) => ({ x: side * 0.7071 * (d + off), y: 0.7071 * (d - off) });

    const clampAngle = a => Math.max(-45, Math.min(45, a));

    function wallDistance(park, angle) {
        const feet = [park.leftField, park.leftCenter, park.center, park.rightCenter, park.rightField];
        const seg = (clampAngle(angle) + 45) / 22.5;
        const i = Math.min(3, Math.floor(seg));
        return feet[i] + (feet[i + 1] - feet[i]) * (seg - i);
    }

    function wallHeight(park, angle) {
        return park.leftWallHeight + (park.rightWallHeight - park.leftWallHeight) * (clampAngle(angle) + 45) / 90;
    }

    const polar = (angle, dist) => {
        const r = angle * Math.PI / 180;
        return { x: dist * Math.sin(r), y: dist * Math.cos(r) };
    };

    // ── Scripts ──────────────────────────────────────────────────────────────

    // Poses carry forward: a key with no pose keeps the one before it.
    function prepare(script) {
        const tracks = new Map();
        for (const track of script.tracks) {
            let pose = 'stand', previousPose = 'stand', poseStart = 0, facing = null;
            let distance = 0, previous = null, direction = 1;
            tracks.set(track.actorId, track.keys.map(k => {
                if (k.pose && k.pose !== pose) { previousPose = pose; pose = k.pose; poseStart = k.t; }
                if (k.facing) facing = k.facing;
                if (previous) {
                    distance += Math.hypot(k.x - previous.x, k.y - previous.y);
                    if (Math.abs(k.x - previous.x) > 0.01) direction = Math.sign(k.x - previous.x);
                }
                previous = k;
                return { t: k.t, x: k.x, y: k.y, z: k.z || 0, pose, previousPose, poseStart, direction, facing,
                    ease: k.ease || 'linear', apex: k.apex || 0, distance };
            }));
        }
        const cues = [...(script.cues || [])].sort((a, b) => a.t - b.t);
        const error = cues.find(c => c.kind === 'effect' && c.value.startsWith('error:'));
        const errorFielder = error && errorActor(script.actors || [], tracks, error);
        const looseUntil = error && errorFielder && error.value !== 'error:wildthrow'
            ? tracks.get(errorFielder.id).find(k => k.t > error.t && k.pose === 'crouch')?.t : null;
        return { script, tracks, cues, error, looseUntil,
            closeups: [homeCall(script, tracks, cues), hitOutAtThird(script, tracks, cues), doublePlayPivot(script, tracks),
                barehandPlay(script, tracks, cues), overShoulderCatch(script, tracks, cues),
                distantDive(script.actors || [], tracks, cues),
                script.outcome === 'Error' || script.outcome === 'DoublePlay' || cues.some(c => c.value === 'barehand')
                    ? null : relayTurn(script.actors || [], tracks),
                stealCall(script, tracks, cues), groundCall(script, cues),
                errorCloseup(script, tracks, error, errorFielder), homeRunCloseup(script, tracks, cues),
                wallCatchCloseup(script, tracks, cues)].filter(Boolean) };
    }

    const lerp = (a, b, f) => a + (b - a) * f;

    // Where an actor is at time t, the pose he is in (the pose of the key he has most recently left), how fast
    // he is going (feet per second), how long he has been in that pose, and whether he has reached his last key
    // (and for how long).
    function sample(keys, t) {
        const first = keys[0], last = keys[keys.length - 1];
        if (t <= first.t) return { x: first.x, y: first.y, z: first.z, pose: first.pose, previousPose: first.previousPose, facing: first.facing,
            poseAge: 0, direction: first.direction, vx: 0, vy: 0, distance: first.distance, segT: 0, since: 0, ended: false };
        if (t >= last.t) return { x: last.x, y: last.y, z: last.z, pose: last.pose, previousPose: last.previousPose, facing: last.facing,
            poseAge: t - last.poseStart, direction: last.direction, vx: 0, vy: 0, distance: last.distance,
            segT: t - last.t, since: t - last.t, ended: true };
        let i = 0;
        while (keys[i + 1].t <= t) i++;
        const a = keys[i], b = keys[i + 1];
        const span = b.t - a.t;
        if (span <= 0) return { x: b.x, y: b.y, z: b.z, pose: b.pose, previousPose: b.previousPose, facing: b.facing,
            poseAge: Math.max(0, t - b.poseStart), direction: b.direction, vx: 0, vy: 0, distance: b.distance,
            segT: 0, since: 0, ended: false };
        const f = (t - a.t) / span;
        // A rolling ball slows as it goes; everything else moves at a steady speed.
        const g = b.ease === 'roll' ? 1 - (1 - f) * (1 - f) : f;
        let z = lerp(a.z, b.z, g);
        if (b.ease === 'arc') z += 4 * b.apex * f * (1 - f);
        return {
            x: lerp(a.x, b.x, g), y: lerp(a.y, b.y, g), z: Math.max(0, z), pose: a.pose,
            previousPose: a.previousPose, poseAge: Math.max(0, t - a.poseStart), direction: b.direction, facing: a.facing,
            vx: (b.x - a.x) / span, vy: (b.y - a.y) / span, distance: lerp(a.distance, b.distance, g), segT: t - a.t, since: 0, ended: false,
            f, rolling: b.ease === 'roll',   // how far along the move he or it is, and whether a ball is rolling on the ground
        };
    }

    // WHY: Only a distant diving catch needs the inset; ordinary catches are already legible in the fixed view.
    function distantDive(actors, tracks, cues) {
        for (const cue of cues) {
            if (cue.kind !== 'effect' || cue.value !== 'glove' || Math.hypot(cue.x || 0, cue.y || 0) < 180) continue;
            for (const actor of actors) {
                if (actor.kind !== 'fielder' || !/^fielder-(Left|Center|Right)$/.test(actor.id)) continue;
                const keys = tracks.get(actor.id);
                if (keys && sample(keys, cue.t).pose === 'dive')
                    return { actor, start: cue.t - 0.42, end: cue.t + 0.82, x: cue.x, y: cue.y, title: 'CATCH CLOSE-UP' };
            }
        }
        return null;
    }

    function relayTurn(actors, tracks) {
        for (const actor of actors) {
            if (actor.kind !== 'fielder') continue;
            const keys = tracks.get(actor.id);
            const turn = keys && keys.find(k => k.pose === 'turn');
            const release = keys && keys.find(k => k.pose === 'throw' && turn && k.t > turn.t);
            if (turn && release)
                return { actor, start: turn.t - 0.12, end: release.t + 0.18, x: turn.x, y: turn.y, title: 'RELAY CLOSE-UP' };
        }
        return null;
    }

    function doublePlayPivot(script, tracks) {
        if (script.outcome !== 'DoublePlay') return null;
        const pivot = script.actors.find(a => ['fielder', 'pitcher'].includes(a.kind) && tracks.get(a.id)?.some(k =>
            k.pose === 'tag' && Math.abs(k.x) < 4 && Math.abs(k.y - 127.28) < 4 &&
            tracks.get(a.id).some(next => next.pose === 'turn' && next.t > k.t) &&
            tracks.get(a.id).some(next => next.pose === 'throw' && next.t > k.t)));
        if (!pivot) return null;
        const keys = tracks.get(pivot.id);
        const force = keys.find(k => k.pose === 'tag' && Math.abs(k.x) < 4 && Math.abs(k.y - 127.28) < 4);
        const release = keys.find(k => k.pose === 'throw' && k.t > force.t);
        const runner = script.actors.find(a => a.kind === 'runner' && tracks.get(a.id)?.some(k =>
            k.pose === 'out' && k.t >= force.t && k.t < release.t &&
            Math.abs(k.x) < 4 && Math.abs(k.y - 127.28) < 4));
        if (!runner) return null;
        const out = tracks.get(runner.id).find(k => k.pose === 'out' && k.t >= force.t && k.t < release.t);
        return { actor: pivot, other: runner, start: force.t - 0.25, end: release.t + 0.18,
            callT: out.t, callLabel: 'OUT', callColor: '#ff9393', slideThroughCall: true,
            x: force.x, y: force.y, otherOffset: 20, scale: 2.8, title: 'DOUBLE PLAY TURN' };
    }

    function barehandPlay(script, tracks, cues) {
        if (script.outcome !== 'GroundOut') return null;
        const cue = cues.find(c => c.kind === 'effect' && c.value === 'barehand');
        if (!cue) return null;
        const actor = script.actors.find(a => a.kind === 'fielder' && tracks.get(a.id)?.some(k =>
            k.pose === 'barehand' && Math.abs(k.t - cue.t) < 0.005));
        if (!actor) return null;
        const release = tracks.get(actor.id).find(k => k.pose === 'throw' && k.t > cue.t);
        return { actor, start: cue.t - 0.35, end: (release?.t || cue.t + 0.4) + 0.12,
            x: cue.x, y: cue.y, scale: 2.8, title: 'BAREHAND PLAY' };
    }

    function overShoulderCatch(script, tracks, cues) {
        if (script.outcome !== 'FlyOut') return null;
        const cue = cues.find(c => c.kind === 'effect' && c.value === 'shouldercatch');
        if (!cue) return null;
        const actor = script.actors.find(a => a.kind === 'fielder' && tracks.get(a.id)?.some(k =>
            k.pose === 'overShoulder' && Math.abs(k.t - cue.t) < 0.005));
        if (!actor) return null;
        return { actor, start: cue.t - 0.4, end: cue.t + 0.65,
            x: cue.x, y: cue.y, scale: 2.8, title: 'OVER THE SHOULDER' };
    }

    function stealCall(script, tracks, cues) {
        if (script.outcome !== 'StolenBase' && script.outcome !== 'CaughtStealing') return null;
        for (const call of cues.filter(c => c.kind === 'effect' && c.value === 'call')) {
            const third = Math.abs(call.x + 63.64) < 4 && Math.abs(call.y - 63.64) < 4;
            const second = Math.abs(call.x) < 4 && Math.abs(call.y - 127.28) < 4;
            if (!second && !third) continue;
            const cover = script.actors.find(a => ['fielder', 'pitcher'].includes(a.kind) && tracks.get(a.id)?.some(k =>
                k.pose === 'tag' && Math.hypot(k.x - call.x, k.y - call.y) < 6));
            const runner = script.actors.find(a => a.kind === 'runner' && tracks.get(a.id)?.some(k =>
                Math.abs(k.t - call.t) < 0.005 && Math.hypot(k.x - call.x, k.y - call.y) < 4));
            if (!cover || !runner) continue;
            const callPose = tracks.get(runner.id).find(k => Math.abs(k.t - call.t) < 0.005 &&
                Math.hypot(k.x - call.x, k.y - call.y) < 4)?.pose;
            return { actor: cover, other: runner, start: call.t - 0.42, end: call.t + 0.55,
                callT: call.t, callLabel: callPose === 'out' ? 'OUT' : 'SAFE',
                callColor: callPose === 'out' ? '#ff9393' : '#8ee79d',
                otherOffset: 20, slideThroughCall: true,
                x: call.x, y: call.y, title: third ? 'STEAL AT THIRD' : 'STEAL AT SECOND' };
        }
        return null;
    }

    function groundCall(script, cues) {
        if (script.outcome !== 'GroundOut') return null;
        const call = cues.find(c => c.kind === 'effect' && c.value === 'call' && c.x > 50);
        const first = script.actors.find(a => a.id === 'fielder-First');
        const batter = script.actors.find(a => a.kind === 'batter');
        if (!call || !first || !batter) return null;
        return { actor: first, other: batter, start: call.t - 0.42, end: call.t + 0.55,
            x: call.x, y: call.y, title: 'PLAY AT FIRST' };
    }

    function hitOutAtThird(script, tracks, cues) {
        if (!['Single', 'Double', 'Triple'].includes(script.outcome)) return null;
        const call = cues.find(c => c.kind === 'effect' && c.value === 'call' &&
            Math.abs((c.x || 0) + 63.64) < 4 && Math.abs((c.y || 0) - 63.64) < 4);
        if (!call) return null;
        const runner = script.actors.find(a => a.kind === 'runner' && tracks.get(a.id)?.some(k =>
            k.pose === 'out' && Math.abs(k.t - call.t) < 0.005 &&
            Math.hypot(k.x - call.x, k.y - call.y) < 4));
        const cover = script.actors.find(a => ['fielder', 'pitcher'].includes(a.kind) && tracks.get(a.id)?.some(k =>
            k.pose === 'tag' && k.t >= call.t && k.t <= call.t + 0.2 &&
            Math.hypot(k.x - call.x, k.y - call.y) < 6));
        if (!runner || !cover) return null;
        return { actor: cover, other: runner, start: call.t - 0.45, end: call.t + 0.6,
            callT: call.t + 0.05, callLabel: 'OUT', callColor: '#ff9393',
            x: call.x, y: call.y, otherOffset: 20, slideThroughCall: true, title: 'PLAY AT THIRD' };
    }

    function homeCall(script, tracks, cues) {
        if (!['Single', 'Double', 'Triple'].includes(script.outcome)) return null;
        const crossing = cues.filter(c => c.kind === 'effect' && c.value === 'call' &&
            Math.abs(c.x || 0) < 4 && Math.abs(c.y || 0) < 4).at(-1);
        const plateDecision = cues.find(c => c.kind === 'effect' && c.value === 'platecall');
        const call = plateDecision || crossing;
        const catcher = script.actors.find(a => a.id === 'catcher');
        const ball = tracks.get('ball');
        if (!crossing || !call || !catcher || !ball) return null;
        const receive = ball.find(k => k.t >= crossing.t && Math.abs(k.x) < 4 && k.y < 0 && k.y > -4);
        if (!receive || receive.t - crossing.t > 0.9 || (plateDecision && call.t < receive.t)) return null;
        const runner = script.actors.find(a => (a.kind === 'runner' || a.kind === 'batter') &&
            tracks.get(a.id)?.some(k => Math.abs(k.t - crossing.t) < 0.005 && k.pose === 'safe' &&
                Math.abs(k.x) < 4 && Math.abs(k.y) < 4));
        if (!runner) return null;
        return { actor: catcher, other: runner, start: crossing.t - 0.38,
            end: (plateDecision ? call.t : receive.t) + 0.55,
            callT: call.t, callLabel: 'SAFE', decision: !!plateDecision,
            x: 0, y: 0, scale: 1.55, centerY: 95, otherOffset: 25, title: 'PLAY AT HOME' };
    }

    function errorActor(actors, tracks, cue) {
        const fielders = actors.filter(a => (a.kind === 'fielder' || a.kind === 'pitcher' || a.kind === 'catcher') && tracks.has(a.id));
        if (cue.value === 'error:misjudged') {
            const turned = fielders.find(a => tracks.get(a.id).some(k => k.pose === 'turn' && k.t < cue.t));
            if (turned) return turned;
        }
        return fielders.sort((a, b) => {
            const one = sample(tracks.get(a.id), cue.t), two = sample(tracks.get(b.id), cue.t);
            return Math.hypot(one.x - cue.x, one.y - cue.y) - Math.hypot(two.x - cue.x, two.y - cue.y);
        })[0];
    }

    function errorCloseup(script, tracks, cue, actor) {
        if (script.outcome !== 'Error') return null;
        if (!cue || (Math.hypot(cue.x || 0, cue.y || 0) < 160 && cue.value !== 'error:wildthrow')) return null;
        if (!actor) return null;
        const titles = { booted: 'BOOTED BALL', underglove: 'THROUGH THE GLOVE',
            dropped: 'DROPPED FLY', misjudged: 'MISJUDGED FLY', wildthrow: 'WILD THROW' };
        const at = sample(tracks.get(actor.id), cue.t);
        const focus = cue.value === 'error:misjudged' ? { x: (at.x + cue.x) / 2, y: (at.y + cue.y) / 2 } : cue;
        const pickup = tracks.get(actor.id).find(k => k.t > cue.t && k.pose === 'crouch');
        return { actor, start: cue.t - 0.35, end: Math.max(cue.t + 1.15, (pickup?.t || cue.t) + 0.2), x: focus.x, y: focus.y,
            title: titles[cue.value.slice(6)] || 'FIELDING ERROR' };
    }

    function homeRunCloseup(script, tracks, cues) {
        if (script.outcome !== 'HomeRun') return null;
        const cue = cues.find(c => c.kind === 'effect' && c.value === 'wallclear');
        if (!cue) return null;
        const fielders = script.actors.filter(a => /^fielder-(Left|Center|Right)$/.test(a.id) && tracks.has(a.id));
        const actor = fielders.sort((a, b) => {
            const one = sample(tracks.get(a.id), cue.t), two = sample(tracks.get(b.id), cue.t);
            return Math.hypot(one.x - cue.x, one.y - cue.y) - Math.hypot(two.x - cue.x, two.y - cue.y);
        })[0];
        if (!actor) return null;
        const ball = tracks.get('ball');
        return { actor, start: cue.t - 0.45, end: cue.t + 0.65, x: cue.x, y: cue.y,
            z: ball ? sample(ball, cue.t).z * 0.45 : 0, scale: 2.6, centerY: 64, title: 'OVER THE WALL' };
    }

    function wallCatchCloseup(script, tracks, cues) {
        const cue = cues.find(c => c.kind === 'effect' && c.value === 'wallcatch');
        if (!cue) return null;
        const actor = script.actors.find(a => /^fielder-(Left|Center|Right)$/.test(a.id)
            && tracks.get(a.id)?.some(k => k.pose === 'leap'));
        if (!actor) return null;
        return { actor, start: cue.t - 0.45, end: cue.t + 0.65, x: cue.x, y: cue.y,
            z: 2, scale: 2.6, centerY: 64, title: 'CATCH AT THE WALL' };
    }

    // The newest banner at or before t (the message box line), or the starting message.
    function activeBanner(cues, t, fallback) {
        let text = fallback;
        for (const c of cues) if (c.kind === 'banner' && c.t <= t) text = c.value;
        return text;
    }

    const EFFECT_SECONDS = 0.8;
    function activeEffects(cues, t) {
        return cues.filter(c => c.kind === 'effect' && c.t <= t && t < c.t + EFFECT_SECONDS);
    }

    // Holds use wall-clock seconds, so a close call remains readable even when the rest of the play is sped up.
    function scriptTime(elapsed, rate, holds) {
        let paused = 0;
        for (const hold of holds) {
            const start = hold.t / rate + paused;
            if (elapsed < start) break;
            if (elapsed < start + hold.seconds) return hold.t;
            paused += hold.seconds;
        }
        return Math.max(0, elapsed - paused) * rate;
    }

    // ── Drawing helpers ──────────────────────────────────────────────────────

    const COLORS = {
        stands: '#262630', wall: '#b84a4a', wallTop: '#e9a0a0', track: '#a9794f',
        grassFoul: '#245018', grassFair: '#2e6a1c', grassStripe: '#35761f', dirt: '#9a6b44', infield: '#2f6f1e',
        chalk: '#f2f2f2', mound: '#a9794f', crowd: ['#cfd3da', '#aab1bd', '#8d949f', '#d9c6a5', '#b85c5c', '#5c84b8', '#c9a44e', '#7fa66a'],
    };

    // WHY: a park can give its wall a colour (stadiums.json "wallColor"). The lighter line along the top and the
    // other shades are worked out from it, so one value is all a park has to say. An unusable value is ignored, and
    // the park keeps the default wall.
    const hexRgb = hex => {
        const m = /^#([0-9a-f]{6})$/i.exec(String(hex || ''));
        if (!m) return null;
        const n = parseInt(m[1], 16);
        return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
    };
    const toHex = rgb => '#' + rgb.map(c => Math.max(0, Math.min(255, Math.round(c))).toString(16).padStart(2, '0')).join('');
    const mixHex = (hex, target, amount) => {
        const a = hexRgb(hex);
        return toHex(a.map((c, i) => c + (target[i] - c) * amount));
    };

    // Uniform colours. The home team is in white and the visitors in blue-gray.
    const TEAMS = {
        home: { jersey: '#f2f2f2', jerseyShade: '#c9ccd3', pants: '#dcdcdc', pantsShade: '#b4b7bf', cap: '#c62828', capShade: '#8f1c1c' },
        visitor: { jersey: '#8fa8cf', jerseyShade: '#6a84ad', pants: '#7189b3', pantsShade: '#566d94', cap: '#1a237e', capShade: '#101a5e' },
    };
    const SKIN = '#e0a96d', SKIN_SHADE = '#c28a52', HAIR = '#3a2a1a', OUTLINE = '#15151b';
    const SHOE = '#262626', GLOVE = '#8b5a2b', GLOVE_DARK = '#5e3a17', GLOVE_LACE = '#c99a5f', BAT = '#d2a679', PADS = '#cfd3da';
    const BELT = '#3a2a1a', EYE_WHITE = '#ffffff';
    // The picture is drawn at twice its logical size (RES device pixels to a logical pixel) so the players can carry
    // finer details (eyes, hands, gloves) while the field itself keeps its chunky pixels.
    const RES = 2;
    const FLICKER = ['#ffffff', '#ffd54f', '#ff8a80', '#80d8ff'];
    const FIGURE_FEET = 10;  // drawn taller than life so the players read at this size

    function poly(ctx, points, fill) {
        ctx.beginPath();
        points.forEach((p, i) => (i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y)));
        ctx.closePath();
        ctx.fillStyle = fill;
        ctx.fill();
    }

    function line(ctx, a, b, color, width) {
        ctx.beginPath();
        ctx.moveTo(a.x, a.y);
        ctx.lineTo(b.x, b.y);
        ctx.strokeStyle = color;
        ctx.lineWidth = width;
        ctx.stroke();
    }

    const ground = (pt, z = 0) => project(pt.x, pt.y, z);

    // The screen points of a flat disc of ground (a mound, a circle) of radius `r` feet around `center`.
    const discAt = (center, r, steps = 14) => Array.from({ length: steps }, (_, i) => {
        const a = i / steps * 2 * Math.PI;
        return ground({ x: center.x + r * Math.sin(a), y: center.y + r * Math.cos(a) });
    });

    // A repeatable little random sequence, so the crowd is the same every time.
    function seeded(seed) {
        let s = seed >>> 0;
        return () => {
            s = (s * 1664525 + 1013904223) >>> 0;
            return s / 4294967296;
        };
    }

    // Fill a band whose near edge and far edge are lists of screen points, with crowd dots.
    function crowdBand(ctx, near, far, seed) {
        const pts = [...near, ...far.slice().reverse()];
        poly(ctx, pts, COLORS.stands);
        const rnd = seeded(seed);
        const n = near.length - 1;
        for (let k = 0; k < 700; k++) {
            const u = rnd() * n, i = Math.min(n - 1, Math.floor(u)), f = u - i;
            const a = { x: lerp(near[i].x, near[i + 1].x, f), y: lerp(near[i].y, near[i + 1].y, f) };
            const b = { x: lerp(far[i].x, far[i + 1].x, f), y: lerp(far[i].y, far[i + 1].y, f) };
            const v = rnd();
            ctx.fillStyle = COLORS.crowd[Math.floor(rnd() * COLORS.crowd.length)];
            ctx.globalAlpha = 0.85;
            ctx.fillRect(Math.round(lerp(a.x, b.x, v)), Math.round(lerp(a.y, b.y, v)), 2, 2);
        }
        ctx.globalAlpha = 1;
        // For the crowd to flicker later: a way to pick a seat at random.
        return r => {
            const u = r() * n, i = Math.min(n - 1, Math.floor(u)), f = u - i, v = r();
            return {
                x: lerp(lerp(near[i].x, near[i + 1].x, f), lerp(far[i].x, far[i + 1].x, f), v),
                y: lerp(lerp(near[i].y, near[i + 1].y, f), lerp(far[i].y, far[i + 1].y, f), v),
            };
        };
    }

    // Ordered (Bayer) dither: a 4x4 pattern in which `level` of the 16 pixels are colour b and the rest colour a.
    const BAYER = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];
    function dither(ctx, a, b, level) {
        const tile = document.createElement('canvas');
        tile.width = 4; tile.height = 4;
        const g = tile.getContext('2d');
        for (let i = 0; i < 16; i++) {
            g.fillStyle = BAYER[i] < level ? b : a;
            g.fillRect(i % 4, Math.floor(i / 4), 1, 1);
        }
        return ctx.createPattern(tile, 'repeat') || a;
    }

    // The colour of the stands themselves (the dark seats the crowd sits on). Nothing else in the backdrop is this colour.
    const STANDS_RGB = [38, 38, 48];

    // Picks seats that can really be seen. The stands are drawn first and the field, wall and sign are drawn over
    // them, so part of the area the stands cover on paper is not stands on the screen. A seat counts only if the
    // finished backdrop shows the stands' own colour around it. If the backdrop cannot be read there are no seats,
    // and so no sparkle (better that than sparkle over the field).
    function visibleSeats(backdropCtx, samplers) {
        let data = null;
        try { data = backdropCtx.getImageData(0, 0, W, H).data; } catch { data = null; }
        if (!data || !data.length || !samplers.length) return [];
        const rnd = seeded(99);
        const seats = [];
        for (let k = 0; k < 3000 && seats.length < 700; k++) {
            const pt = samplers[Math.floor(rnd() * samplers.length)](rnd);
            const x = Math.round(pt.x), y = Math.round(pt.y);
            if (x < 1 || y < 1 || x >= W - 1 || y >= H - 1) continue;
            let stands = 0;
            for (let dy = -1; dy <= 1; dy++)
                for (let dx = -1; dx <= 1; dx++) {
                    const i = ((y + dy) * W + (x + dx)) * 4;
                    if (Math.abs(data[i] - STANDS_RGB[0]) <= 6 && Math.abs(data[i + 1] - STANDS_RGB[1]) <= 6 && Math.abs(data[i + 2] - STANDS_RGB[2]) <= 6) stands++;
                }
            if (stands >= 5) seats.push({ x, y });
        }
        return seats;
    }

    function wrapText(ctx, text, maxWidth) {
        const lines = [];
        for (const para of String(text).split('\n')) {
            let current = '';
            for (const word of para.split(' ')) {
                const trial = current ? current + ' ' + word : word;
                if (current && ctx.measureText(trial).width > maxWidth) {
                    lines.push(current);
                    current = word;
                } else current = trial;
            }
            lines.push(current);
        }
        return lines.slice(0, 3);
    }

    // ── The park backdrop: drawn once per park, then copied every frame ─────

    function buildBackdrop(ctx, park) {
        // NOTE: without a usable wallColor the wall is drawn exactly as it always was (COLORS.wall and COLORS.wallTop).
        const ownWall = hexRgb(park.wallColor) !== null;
        const wallFill = ownWall ? park.wallColor : COLORS.wall;
        const wallCap = ownWall ? mixHex(park.wallColor, [255, 255, 255], 0.35) : COLORS.wallTop;
        ctx.clearRect(0, 0, W, H);
        ctx.fillStyle = dither(ctx, COLORS.grassFoul, '#1f4814', 3);
        ctx.fillRect(0, 0, W, H);
        const seats = [];   // ways to pick a seat in each part of the stands

        // WHY: a bullpen in foul ground along each line, between the foul line and the wall. It is drawn first, before
        // the wall, so the wall hides the part of it that is behind it (the camera sits low behind home plate).
        // NOTE: this is scenery only. A reliever in a scene still walks in from the off-screen dugout side (Scenes.Bullpen in
        // SceneChoreographer.cs is off the edge of the picture), not from this strip.
        for (const side of [-1, 1]) {
            const at = (d, off) => ground(alongLine(side, d, off));
            poly(ctx, [at(195, 6), at(265, 6), at(265, 19), at(195, 19)], dither(ctx, '#a98458', '#9a7650', 3));
            for (const d of [208, 252]) {
                poly(ctx, discAt(alongLine(side, d, 12.5), 3.4), COLORS.mound);
                poly(ctx, discAt(alongLine(side, d, 12.5), 1.1), '#ffffff');   // the rubber, larger than life so it shows
            }
            poly(ctx, [at(203, 17.2), at(257, 17.2), at(257, 18.8), at(203, 18.8)], '#5a3a22');   // the bench along the wall
        }

        const angles = [];
        for (let a = -45; a <= 45; a += 3) angles.push(a);

        // Stands behind the outfield wall, then along each foul line.
        const wallBottom = angles.map(a => ground(polar(a, wallDistance(park, a))));
        const wallTop = angles.map(a => ground(polar(a, wallDistance(park, a)), wallHeight(park, a)));
        const standsFar = angles.map(a => ground(polar(a, wallDistance(park, a) + 55), wallHeight(park, a) + 32));
        seats.push(crowdBand(ctx, wallTop, standsFar, 11));

        for (const side of [-1, 1]) {
            const poleDist = wallDistance(park, 45 * side);
            const pole = polar(45 * side, poleDist);
            const out = { x: 0.7071 * side, y: -0.7071 };
            const h = wallHeight(park, 45 * side);
            const steps = [0, 0.2, 0.4, 0.6, 0.8, 1];
            const frontGround = steps.map(s => ({ x: pole.x * (1 - 0.85 * s) + out.x * 22, y: pole.y * (1 - 0.85 * s) + out.y * 22 }));
            const near = frontGround.map(p => ground(p, h));
            const far = frontGround.map(p => ground({ x: p.x + out.x * 45, y: p.y + out.y * 45 }, h + 32));
            seats.push(crowdBand(ctx, near, far, 17 + side));
            poly(ctx, [...frontGround.map(p => ground(p, 0)), ...near.slice().reverse()], wallFill);
        }

        // The outfield wall and the warning track in front of it.
        poly(ctx, [...wallBottom, ...wallTop.slice().reverse()], wallFill);
        ctx.beginPath();
        wallTop.forEach((p, i) => (i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y)));
        ctx.strokeStyle = wallCap;
        ctx.lineWidth = 1;
        ctx.stroke();

        // WHY: the foul poles stand at the ends of the wall, taller than it, so a ball can be called fair or foul
        // against them. The dark edge keeps the yellow readable against the crowd behind.
        for (const side of [-1, 1]) {
            const at = polar(45 * side, wallDistance(park, 45 * side));
            const foot = ground(at, 0), tip = ground(at, wallHeight(park, 45 * side) + POLE_EXTRA_FEET);
            line(ctx, foot, tip, '#15151b', 3);
            line(ctx, foot, tip, '#ffd54f', 2);
        }

        const trackInner = angles.map(a => ground(polar(a, wallDistance(park, a) - 15)));
        poly(ctx, [...wallBottom, ...trackInner.slice().reverse()], dither(ctx, COLORS.track, '#98694a', 4));

        // Fair territory: the grass, in mowing stripes.
        const fairOuter = trackInner;
        const fair = [ground({ x: 0, y: 0 }), ...fairOuter];
        poly(ctx, fair, dither(ctx, COLORS.grassFair, '#2a6019', 3));
        ctx.save();
        ctx.beginPath();
        fair.forEach((p, i) => (i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y)));
        ctx.closePath();
        ctx.clip();
        for (let r = 30, band = 0; r < 520; r += 30, band++) {
            if (band % 2) continue;
            const outer = angles.map(a => ground(polar(a, r + 30)));
            const inner = angles.map(a => ground(polar(a, r)));
            poly(ctx, [...outer, ...inner.slice().reverse()], COLORS.grassStripe);
            // The edge of each stripe is dithered into the lawn beside it.
            const edgeOut = angles.map(a => ground(polar(a, r + 38)));
            const edgeIn = angles.map(a => ground(polar(a, r + 30)));
            poly(ctx, [...edgeOut, ...edgeIn.slice().reverse()], dither(ctx, COLORS.grassFair, COLORS.grassStripe, 8));
        }
        ctx.restore();

        // Infield dirt, the grass inside the base paths, home plate's circle.
        const dirt = [];
        for (let a = 0; a < 360; a += 10) {
            const r = a * Math.PI / 180;
            dirt.push(ground({ x: 95 * Math.sin(r), y: 60.5 + 95 * Math.cos(r) }));
        }
        // The dirt stays near the base paths: it is cut off a little outside each foul line.
        ctx.save();
        ctx.beginPath();
        [{ x: -26, y: -18 }, { x: 26, y: -18 }, polar(55, 170), { x: 0, y: 240 }, polar(-55, 170)]
            .map(p => ground(p)).forEach((p, i) => (i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y)));
        ctx.closePath();
        ctx.clip();
        poly(ctx, dirt, dither(ctx, COLORS.dirt, '#8c5f3a', 3));
        ctx.restore();
        // WHY: a lighter six-foot strip of dirt along each side of the diamond is the base path. Without it the infield
        // is one patch of dirt with a grass diamond in it; with it the bases are visibly joined.
        const baseCorners = [{ x: 0, y: 0 }, { x: 63.6, y: 63.6 }, { x: 0, y: 127.3 }, { x: -63.6, y: 63.6 }];
        for (let i = 0; i < 4; i++) {
            const a = baseCorners[i], b = baseCorners[(i + 1) % 4];
            const len = Math.hypot(b.x - a.x, b.y - a.y), nx = -(b.y - a.y) / len * 3, ny = (b.x - a.x) / len * 3;
            poly(ctx, [{ x: a.x + nx, y: a.y + ny }, { x: b.x + nx, y: b.y + ny }, { x: b.x - nx, y: b.y - ny }, { x: a.x - nx, y: a.y - ny }].map(p => ground(p)),
                dither(ctx, '#a97c52', '#9f7249', 3));
        }
        const homeCircle = [];
        for (let a = 0; a < 360; a += 20) {
            const r = a * Math.PI / 180;
            homeCircle.push(ground({ x: 13 * Math.sin(r), y: 13 * Math.cos(r) }));
        }
        poly(ctx, homeCircle, COLORS.dirt);
        poly(ctx, [{ x: 0, y: 19.8 }, { x: 49.6, y: 63.6 }, { x: 0, y: 113.3 }, { x: -49.6, y: 63.6 }].map(p => ground(p)), dither(ctx, COLORS.infield, '#29651a', 3));

        // Chalk foul lines and batter's boxes.
        for (const side of [-1, 1]) {
            const pole = polar(45 * side, wallDistance(park, 45 * side));
            line(ctx, ground({ x: 0, y: 0 }), ground(pole), COLORS.chalk, 1.5);
        }
        for (const side of [-1, 1]) {
            const box = [{ x: side * 2, y: -3.5 }, { x: side * 6, y: -3.5 }, { x: side * 6, y: 3.5 }, { x: side * 2, y: 3.5 }].map(p => ground(p));
            ctx.beginPath();
            box.forEach((p, i) => (i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y)));
            ctx.closePath();
            ctx.strokeStyle = COLORS.chalk;
            ctx.lineWidth = 1;
            ctx.stroke();
        }

        // WHY: the on-deck circles and the coaches' boxes: small things that tell a baseball field from a lawn with a
        // diamond on it. The circles are 38 feet from the plate beside each dugout, the boxes are beside first and
        // third base, outside the foul line.
        for (const side of [-1, 1]) {
            poly(ctx, discAt({ x: side * 38, y: 6 }, 4), COLORS.chalk);
            poly(ctx, discAt({ x: side * 38, y: 6 }, 3.2), COLORS.dirt);
            const box = [[84, 3], [100, 3], [100, 9], [84, 9]].map(([d, off]) => ground(alongLine(side, d, off)));
            ctx.beginPath();
            box.forEach((p, i) => (i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y)));
            ctx.closePath();
            ctx.strokeStyle = COLORS.chalk;
            ctx.lineWidth = 1;
            ctx.stroke();
        }

        // Mound, pitcher's rubber, plate and bases (drawn larger than life so they show at this size).
        poly(ctx, discAt({ x: 0, y: 60.5 }, 8, 12), COLORS.mound);
        poly(ctx, [{ x: -1.9, y: 60 }, { x: 1.9, y: 60 }, { x: 1.9, y: 61.3 }, { x: -1.9, y: 61.3 }].map(p => ground(p)), '#ffffff');
        // NOTE: each base sits on a slightly larger dark diamond: a white base on tan dirt is only a few pixels wide
        // at this size, and the rim keeps it from washing out (especially on a TV).
        const base = (x, y, size) => {
            const diamond = s => [{ x: x - s, y }, { x, y: y + s }, { x: x + s, y }, { x, y: y - s }].map(p => ground(p));
            poly(ctx, diamond(size + 1), '#6b4528');
            poly(ctx, diamond(size), '#ffffff');
        };
        base(63.6, 63.6, 3.4);
        base(0, 127.3, 3.4);
        base(-63.6, 63.6, 3.4);
        poly(ctx, [{ x: -2.1, y: 1 }, { x: 2.1, y: 1 }, { x: 2.1, y: -0.8 }, { x: 0, y: -2.6 }, { x: -2.1, y: -0.8 }].map(p => ground(p)), '#ffffff');

        if (park.isDome) drawRoof(ctx);

        // The sign on the center-field wall.
        const top = ground({ x: 0, y: wallDistance(park, 0) }, wallHeight(park, 0));
        const sign = { x: 320 - 100, y: Math.max(6, top.y - 40), w: 200, h: 32 };
        ctx.fillStyle = '#0d2a4a';
        ctx.fillRect(sign.x, sign.y, sign.w, sign.h);
        ctx.strokeStyle = '#d9c6a5';
        ctx.lineWidth = 1;
        ctx.strokeRect(sign.x + 0.5, sign.y + 0.5, sign.w - 1, sign.h - 1);
        ctx.fillStyle = '#c8323a';
        ctx.fillRect(sign.x + 8, sign.y + sign.h - 6, sign.w - 16, 2);
        return { seats, signY: sign.y };
    }

    // ── Figures and the ball ─────────────────────────────────────────────────

    const hashOf = text => { let h = 7; for (let i = 0; i < text.length; i++) h = (h * 31 + text.charCodeAt(i)) >>> 0; return h; };

    // Which way a figure faces: the way he is going when he is moving, otherwise by his job. From behind home
    // plate, someone going toward the plate comes toward us ('front') and going away shows his back.
    function facingOf(actor, st) {
        if (st.facing) {
            if (st.facing === 'left' || st.facing === 'right') return { view: 'side', dir: st.facing === 'left' ? -1 : 1 };
            return { view: st.facing === 'back' ? 'back' : 'front', dir: 1 };
        }
        if (Math.hypot(st.vx, st.vy) > 3) {
            if (Math.abs(st.vx) > Math.abs(st.vy) * 0.8) return { view: 'side', dir: st.vx > 0 ? 1 : -1 };
            return { view: st.vy < 0 ? 'front' : 'back', dir: 1 };
        }
        if (actor.kind === 'catcher') return { view: 'back', dir: 1 };
        if (actor.kind === 'batter') return { view: 'side', dir: st.x < 0 ? 1 : -1 };   // faces the plate
        return { view: 'front', dir: 1 };
    }

    // How a batter stands: where the bat is held (an angle from straight up, toward the pitcher), how long it looks
    // (a slap hitter chokes up), and how much it waggles while he waits.
    const STANCES = {
        power: { angle: -0.7, length: 9, wobble: 0.14, spread: 2.2, crouch: 1 },
        slap: { angle: -0.05, length: 6.5, wobble: 0.06, spread: 0, crouch: 0.88 },
        balanced: { angle: -0.3, length: 8.5, wobble: 0.1, spread: 0, crouch: 1 },
    };
    // WHY: the batter keeps his actor kind ('batter') after he reaches base, and a man standing on first base is still
    // in the 'stand' pose, so the kind and pose alone would give him a bat and a hitter's crouch there. He is only
    // set to hit while he is in the batter's box (a few feet from the plate).
    const inBatterBox = st => Math.abs(st.x) < 8 && st.y < 8;
    const stanceOf = actor => (actor.kind === 'batter' && STANCES[actor.stance]) || STANCES.balanced;

    // The pieces a figure is made of, as rectangles in "figure units" (a figure is 16 units tall, standing on
    // y = 0, up is negative), for one view and pose. A side view faces +x; the caller mirrors it for the left.
    function figureParts(actor, st, view, time) {
        const team = TEAMS[actor.team] || TEAMS.home;
        const pose = st.pose;
        const parts = [];
        const add = (x, y, w, h, c) => parts.push({ x, y, w, h, c });
        // WHY: Small uniform and gear marks need square edges without a one-pixel outline swallowing them.
        const detail = (x, y, w, h, c) => parts.push({ x, y, w, h, c, outline: false });
        // WHY: the stride follows ground covered, so a runner whose arrival is delayed does not keep sprinting in place.
        const strideRate = actor.kind === 'batter' || actor.kind === 'runner' ? 0.10 : 0.30;
        const ph = pose === 'run' ? Math.sin((st.distance || 0) * strideRate + (hashOf(actor.id) % 7)) : 0;
        const kick = pose === 'kick';   // the pitcher's front knee comes up
        const lift = Math.max(0, ph) * 1.5, lift2 = kick ? 4 : Math.max(0, -ph) * 1.5;
        const helmet = actor.kind === 'batter' || actor.kind === 'runner';
        const catcher = actor.kind === 'catcher';
        // WHY: the men in the field wear a brown glove on one hand (the one that is not throwing). Batters, runners and
        // the rest of the team have bare hands.
        const fielding = actor.kind === 'fielder' || actor.kind === 'pitcher' || actor.kind === 'catcher';
        const mitt = (x, y, w = 3, h = 3) => {
            add(x, y, w, h, GLOVE);
            detail(x + 0.8, y + 0.8, w - 1.6, h - 1.6, GLOVE_DARK);
            detail(x + 0.35, y + 0.3, 0.4, 0.8, GLOVE_LACE);
            detail(x + w - 0.75, y + 0.3, 0.4, 0.8, GLOVE_LACE);
        };
        // A hand at (x, y), 1.6 wide: a fist, or a glove with a darker pocket when this man is fielding.
        const hand = (x, y) => {
            if (fielding) mitt(x - 0.7, y - 0.6);
            else add(x, y, 1.6, 1.6, SKIN);
        };
        // A bare hand (the throwing one, or any man's other hand): a little wider than the sleeve, with a thumb.
        const fist = (x, y) => { add(x - 0.1, y, 1.8, 1.8, SKIN); add(x - 0.1, y + 1.2, 0.6, 0.6, SKIN_SHADE); };

        if (view !== 'side') {
            // Legs (a lifted leg is shorter) and shoes.
            const wide = pose === 'reach' ? 3 : pose === 'plant' ? 2 : pose === 'pop' ? 1.2 : pose === 'crouch' ? 0.9 : 0;
            add(-2.7 - wide, -5, 2.3, 5 - lift, catcher ? PADS : team.pants);
            add(0.4 + wide, -5, 2.3, 5 - lift2, catcher ? PADS : team.pantsShade);
            add(-2.9 - wide, -lift - 1, 2.7, 1, SHOE);
            add(0.2 + wide, -lift2 - 1, 2.7, 1, SHOE);
            detail(-2.9 - wide, -lift - 0.8, 0.8, 0.35, PADS);
            detail(0.2 + wide, -lift2 - 0.8, 0.8, 0.35, PADS);
            if (!catcher) {   // the stockings show a band of the team's colour above the shoe
                add(-2.7 - wide, -lift - 2.6, 2.3, 0.9, team.cap);
                add(0.4 + wide, -lift2 - 2.6, 2.3, 0.9, team.cap);
                detail(-2.5 - wide, -5, 0.5, 2.3, team.capShade);
                detail(2.0 + wide, -5, 0.5, 2.3, team.capShade);
            } else {
                detail(-2.3 - wide, -4.4, 1.5, 0.55, team.capShade);
                detail(0.8 + wide, -4.4, 1.5, 0.55, team.capShade);
                detail(-2.3 - wide, -2.7, 1.5, 0.55, team.capShade);
                detail(0.8 + wide, -2.7, 1.5, 0.55, team.capShade);
            }
            // Body.
            add(-3, -11, 6, 6, team.jersey);
            add(1.8, -11, 1.2, 6, team.jerseyShade);
            add(-3, -5.8, 6, 0.8, BELT);                                  // the belt
            add(-0.5, -5.8, 1, 0.8, PADS);                                // and its buckle
            if (view === 'front') add(-1.4, -11, 2.8, 0.7, team.jerseyShade);   // the collar
            if (view === 'front') {
                detail(-0.25, -10.1, 0.5, 4.1, team.jerseyShade);
                detail(0.4, -9.2, 0.55, 0.55, team.capShade);
                detail(0.4, -7.3, 0.55, 0.55, team.capShade);
                detail(-2.5, -10, 0.65, 3, team.cap);
            } else {
                detail(-2.5, -10.7, 5, 0.65, team.jerseyShade);
                detail(-2.2, -9.4, 0.65, 3.2, team.capShade);
                detail(1.55, -9.4, 0.65, 3.2, team.capShade);
            }
            if (catcher) {
                detail(-2.55, -10.4, 5.1, 4.5, team.capShade);
                detail(-2.1, -9.8, 4.2, 0.7, PADS);
                detail(-0.35, -9.1, 0.7, 2.8, PADS);
                detail(-1.9, -7.1, 3.8, 0.55, PADS);
            }
            // Arms.
            const sw = ph * 0.9;
            if (pose === 'windup') {
                add(-2, -18, 4, 6, team.jersey);
                add(-1, -19, 2, 1.4, SKIN);
            } else if (pose === 'kick') {
                add(-1.6, -12, 3.2, 2.4, GLOVE);                       // the hands together at the chest
                add(-4.6, -11, 1.6, 3.2, team.jersey); add(3, -11, 1.6, 3.2, team.jersey);
            } else if (pose === 'sidearm') {
                add(3, -13, 6, 1.6, team.jersey); fist(9, -13);   // the arm out to the side
                add(-4.6, -11, 1.6, 3.2, team.jersey); hand(-4.6, -7.6);
            } else if (pose === 'throw') {
                add(3, -18, 1.6, 7, team.jersey); fist(3, -19.4);
                add(-4.6, -11, 1.6, 3.2, team.jersey); hand(-4.6, -7.6);
            } else if (pose === 'turn') {
                add(-4.6, -12, 2.2, 3.8, team.jersey); hand(-3.6, -11.4);
                add(2.8, -12, 2, 3.6, team.jerseyShade); fist(2.8, -9.2);
            } else if (pose === 'plant') {
                add(-4.6, -12, 2.2, 3.8, team.jersey); hand(-3.6, -11.4);
                add(3, -17, 2, 6, team.jerseyShade); fist(3, -17.8);
            } else if (pose === 'pop') {
                add(-3.5, -11.5, 2.5, 3, team.jersey); hand(-1.2, -10.5);
                add(3, -14, 2, 4, team.jerseyShade); fist(3, -14.5);
            } else if (pose === 'maskoff') {
                add(-4.6, -11, 1.6, 3.2, team.jersey); hand(-4.6, -7.6);
                add(2.4, -17, 1.8, 6, team.jerseyShade); fist(2.4, -18);
            } else if (pose === 'track') {
                add(-4.6, -14, 1.8, 4, team.jersey); hand(-4.6, -14);
                add(3, -15.5, 1.6, 4.5, team.jerseyShade); fist(3, -16);
            } else if (pose === 'watch') {
                add(-4.6, -13, 1.8, 3, team.jersey); hand(-4.6, -11);
                add(3, -15, 1.8, 4, team.jerseyShade); fist(3, -16);
            } else if (pose === 'reach') {
                add(-4.6, -15, 2.2, 4.5, team.jersey); hand(-4.6, -15.5);
                add(3, -11, 2, 3.2, team.jerseyShade); fist(3, -8);
            } else if (pose === 'overShoulder') {
                // Looking back while running: the glove is above and just behind one shoulder.
                add(-4.6, -13, 1.8, 4, team.jersey); fist(-4.6, -9.5);
                add(3, -18, 2, 7, team.jerseyShade); mitt(2.4, -20.8, 3.5, 3.5);
            } else if (pose === 'catch' || pose === 'dive' || pose === 'leap') {
                add(-4.6, -17.5, 1.6, 6.5, team.jersey); add(3, -17.5, 1.6, 6.5, team.jersey);
                detail(-4.6, -12.6, 1.6, 0.65, team.capShade);
                detail(3, -12.6, 1.6, 0.65, team.capShade);
                mitt(-5.6, -20, 3.2, 3);
                if (!catcher) fist(3, -19.4);
            } else if (pose === 'tag') {
                add(-4.6, -8, 1.6, 5, team.jersey); mitt(-5.2, -3.4, 2.8, 2.8);
                detail(-4.6, -7.6, 1.6, 0.7, team.capShade);
                add(3, -11, 1.6, 3.2, team.jersey); fist(3, -7.6);
            } else if (pose === 'recover') {
                add(-6, -13, 1.6, 5, team.jerseyShade); hand(-6, -8);
                add(4.5, -14, 1.6, 5, team.jersey); fist(4.5, -9);
            } else if (pose === 'crouch') {
                // Bent low, both hands down by the grass: the glove open in front of the feet.
                add(-4.6, -11, 1.6, 5.4, team.jersey); hand(-4.4, -5.6);
                add(3, -11, 1.6, 5, team.jersey); fist(2.8, -6.2);
            } else if (pose === 'barehand') {
                // The glove stays at the hip while the throwing hand scoops the roller off the grass.
                add(-4.6, -11, 1.8, 3.3, team.jersey); mitt(-5.1, -9, 3, 3);
                add(3, -10, 2, 7, team.jerseyShade); fist(3.5, -3.5);
            } else if (pose === 'transfer') {
                // The ball goes from the glove (left) across to the bare hand (right), at the chest.
                add(-4.6, -11, 1.6, 3.2, team.jersey); hand(-3.6, -11.4);
                add(3, -11, 1.6, 3.2, team.jersey); fist(0.6, -11.6);
            } else if (pose === 'stretch') {
                // Set: the hands together at the belt, no wind-up.
                add(-4.6, -11, 1.6, 3.4, team.jersey); add(3, -11, 1.6, 3.4, team.jersey);
                add(-1.8, -9.4, 3.6, 2.4, GLOVE); add(-0.6, -9.2, 1.2, 1.2, GLOVE_DARK);
            } else if (pose === 'cheer') {
                add(-4.8, -19, 1.6, 8, team.jersey); fist(-4.8, -20.4);
                add(3.2, -19, 1.6, 8, team.jersey); fist(3.2, -20.4);
            } else if (pose === 'safe') {
                add(-8.5, -11, 17, 1.6, team.jersey);
                detail(-6.2, -11, 0.65, 1.6, team.capShade);
                detail(5.5, -11, 0.65, 1.6, team.capShade);
                fist(-9.8, -11); fist(8.4, -11);
            } else if (pose === 'out') {
                add(3, -18, 1.6, 7, team.jersey); fist(2.8, -20);
                add(-4.6, -11, 1.6, 3.2, team.jersey); hand(-4.6, -7.6);
            } else {
                add(-4.6, -11 + sw, 1.6, 3.2, team.jersey); hand(-4.6, -7.6 + sw);
                add(3, -11 - sw, 1.6, 3.2, team.jersey); fist(3, -7.6 - sw);
            }
            // Head, face, cap. A slumped head sinks into the shoulders.
            const drop = pose === 'slump' ? 2.4 : 0;
            add(-2, -15 + drop, 4, 4, view === 'back' ? HAIR : SKIN);
            if (view === 'front') {
                // Eyes: a white with a dark pupil, under a brow, and a small mouth.
                add(-1.7, -13.5 + drop, 1.3, 1, EYE_WHITE); add(-1.1, -13.4 + drop, 0.6, 0.8, OUTLINE);
                add(0.4, -13.5 + drop, 1.3, 1, EYE_WHITE); add(0.6, -13.4 + drop, 0.6, 0.8, OUTLINE);
                add(-1.8, -14.2 + drop, 1.5, 0.4, HAIR); add(0.3, -14.2 + drop, 1.5, 0.4, HAIR);
                add(-0.6, -11.9 + drop, 1.2, 0.4, SKIN_SHADE);
                add(-0.3, -12.8 + drop, 0.6, 0.6, SKIN_SHADE);   // the nose
                add(-2.4, -13.4 + drop, 0.5, 1.3, SKIN_SHADE); add(1.9, -13.4 + drop, 0.5, 1.3, SKIN_SHADE);   // the ears
            }
            if (helmet) {
                add(-2.6, -16.6 + drop, 5.2, 3.6, team.cap);
                detail(-1.8, -16.25 + drop, 3.6, 0.55, team.capShade);
                detail(-2.6, -14.2 + drop, 1.15, 1.4, team.capShade);
            } else {
                add(-2.4, -16.2 + drop, 4.8, 2.4, team.cap);
                detail(-1.8, -15.8 + drop, 3.6, 0.5, team.capShade);
            }
            if (view === 'front') add(-2.6, -14 + drop, 5.2, 0.8, team.capShade);   // the brim
            if (catcher && view === 'front' && !actor.maskOff) {
                add(-2.2, -14 + drop, 4.4, 2.6, OUTLINE);   // the mask
                detail(-1.5, -13.55 + drop, 3, 0.55, PADS);
                detail(-1.5, -12.35 + drop, 3, 0.45, PADS);
                detail(-0.25, -13.55 + drop, 0.5, 1.65, PADS);
            }
            return parts;
        }

        // Side view, facing right.
        // A batter waiting for the pitch (or swinging) stands as his stance says: a power hitter with his feet apart.
        const set = actor.kind === 'batter' && (pose === 'stand' || pose === 'swing');
        const s1 = ph * 1.8 + (set ? stanceOf(actor).spread : 0) + (pose === 'crouch' ? 2.2 : 0) + (pose === 'plant' ? 2.4 : 0) + (pose === 'reach' ? 3 : 0) + (pose === 'recover' ? 2 : 0);
        if (pose === 'dive') {
            // WHY: Trailing, separated legs and an extended glove arm make the inset read as a dive, not a tipped-over stand.
            add(-4.2, -5, 3.6, 1.7, team.pantsShade); add(-6.6, -4.8, 2.8, 1.2, SHOE);
            add(-2.7, -3.4, 3.5, 1.6, team.pants); add(-5, -2.5, 2.8, 1.2, SHOE);
            detail(-6.35, -4.6, 0.95, 0.4, PADS);
            detail(-4.75, -2.3, 0.95, 0.4, PADS);
            detail(-3.75, -4.8, 0.7, 1.2, team.capShade);
            detail(-2.25, -3.2, 0.7, 1.1, team.capShade);
            if (!catcher) { add(-4.2, -5, 2.1, 0.8, team.cap); add(-2.7, -3.4, 2.1, 0.8, team.cap); }
        } else {
            add(-1.2 - s1, -5, 2.3, 5 - lift, team.pantsShade);
            add(-1.0 + s1, -5, 2.3, 5 - lift2, team.pants);
            add(-1.2 - s1, -lift - 1, 3, 1, SHOE);
            add(-1.0 + s1, -lift2 - 1, 3, 1, SHOE);
            detail(-1.2 - s1, -lift - 0.8, 0.85, 0.35, PADS);
            detail(-1.0 + s1, -lift2 - 0.8, 0.85, 0.35, PADS);
            if (!catcher) {
                add(-1.2 - s1, -lift - 2.6, 2.3, 0.9, team.cap);
                add(-1.0 + s1, -lift2 - 2.6, 2.3, 0.9, team.cap);
                detail(-1.1 - s1, -5, 0.55, 2.3, team.capShade);
                detail(-0.9 + s1, -5, 0.55, 2.3, team.capShade);
            } else {
                detail(-1.0 - s1, -4.5, 1.7, 0.65, team.capShade);
                detail(-0.8 + s1, -4.5, 1.7, 0.65, team.capShade);
                detail(-1.0 - s1, -2.8, 1.7, 0.55, team.capShade);
                detail(-0.8 + s1, -2.8, 1.7, 0.55, team.capShade);
            }
        }
        add(-2, -11, 4, 6, team.jersey);
        add(1, -11, 1, 6, team.jerseyShade);
        add(-2, -5.8, 4, 0.8, BELT);
        detail(-1.45, -10.3, 0.55, 4.2, team.jerseyShade);
        detail(0.6, -9.3, 0.5, 0.55, team.capShade);
        detail(0.6, -7.5, 0.5, 0.55, team.capShade);
        if (catcher) {
            detail(-1.65, -10.3, 3.3, 4.45, team.capShade);
            detail(-1.25, -9.5, 2.5, 0.65, PADS);
            detail(-1.25, -7.1, 2.5, 0.55, PADS);
        }
        const sw = -ph * 1.6;
        if (pose === 'throw' || pose === 'windup') {
            add(0.4, -18, 1.6, 7, team.jersey); fist(0.4, -19.4);
        } else if (pose === 'turn') {
            add(0.2, -12, 3, 2, team.jersey); hand(2, -10.4);
            add(-3.2, -11.8, 2, 3.2, team.jerseyShade); fist(-3.2, -9.2);
        } else if (pose === 'plant') {
            add(0.2, -12, 3, 2, team.jersey); hand(2, -10.4);
            add(-3.2, -17.5, 2, 6.5, team.jerseyShade); fist(-3.2, -18);
        } else if (pose === 'pop') {
            add(0.2, -11.5, 3, 2, team.jersey); hand(2, -10.4);
            add(-2.6, -14, 2, 4, team.jerseyShade); fist(-2.6, -14.5);
        } else if (pose === 'maskoff') {
            add(0.4, -12, 3, 2, team.jersey); hand(2.2, -11);
            add(-2.4, -17, 2, 6, team.jerseyShade); fist(-2.4, -18);
        } else if (pose === 'track') {
            add(0.4, -14, 4, 1.6, team.jersey); hand(4, -14);
            add(-3, -15, 2, 4, team.jerseyShade); fist(-3, -16);
        } else if (pose === 'watch') {
            add(0.5, -13, 3.5, 1.6, team.jersey); hand(4, -12.5);
            add(-3, -16, 2, 5, team.jerseyShade); fist(-3, -17);
        } else if (pose === 'reach') {
            add(0.4, -14.5, 5.6, 1.6, team.jersey); hand(5.6, -14.4);
            add(-3, -11, 2, 3.2, team.jerseyShade); fist(-3, -8.3);
        } else if (pose === 'overShoulder') {
            add(-2.8, -12, 1.8, 3.5, team.jersey); fist(-2.8, -9);
            add(1, -18, 2, 7, team.jerseyShade); mitt(1.2, -20.5, 3.2, 3.2);
        } else if (pose === 'dive') {
            // The arm points along the body's axis; after rotation the glove leads the horizontal dive.
            add(0.2, -20, 1.7, 9, team.jersey); mitt(-0.4, -22, 3, 3);
            detail(0.2, -13, 1.7, 0.7, team.capShade);
            add(0.8, -11, 5, 1.2, team.jerseyShade); fist(5.4, -11);
        } else if (pose === 'leap') {
            add(-1.5, -20, 2, 9, team.jersey); mitt(-2, -22, 3, 3);
            add(1, -18, 1.7, 7, team.jerseyShade); fist(1, -19);
        } else if (pose === 'catch') {
            add(0.8, -14, 4, 1.6, team.jersey); mitt(4.6, -15.4, 2.8, 3);
            detail(2.4, -14, 0.65, 1.6, team.capShade);
        } else if (pose === 'tag') {
            add(0.8, -8, 4, 1.6, team.jersey); mitt(4.6, -9.2, 2.6, 2.6);
            detail(2.4, -8, 0.65, 1.6, team.capShade);
        } else if (pose === 'recover') {
            add(1, -12, 1.6, 5, team.jersey); hand(1.5, -7);
            add(-4.5, -16, 1.6, 5, team.jerseyShade); fist(-4.5, -12);
        } else if (pose === 'crouch') {
            add(0.6, -9.6, 3.6, 1.6, team.jersey); add(3.2, -8.6, 1.6, 4, team.jersey); hand(3.6, -4.9);   // glove down at the grass
        } else if (pose === 'barehand') {
            add(-3.3, -11, 2, 3.5, team.jersey); mitt(-3.9, -9.5, 3, 3);
            add(1, -9.5, 3.2, 2, team.jerseyShade); add(3.6, -8, 2, 4.3, team.jerseyShade); fist(5, -3.8);
        } else if (pose === 'transfer') {
            add(0.6, -10.6, 3, 1.6, team.jersey); hand(3.2, -11.3); fist(1.2, -11.2);   // the ball moving glove to hand
        } else if (pose === 'stretch') {
            add(0.6, -10, 2.6, 1.6, team.jersey); hand(3, -10.3);                       // set, the hands together
        } else if (pose === 'bunt') {
            // Squared around to face the pitcher: the hands are out in front at the belt, holding the bat level over the plate.
            add(0.6, -9.8, 4.2, 1.6, team.jersey); fist(5, -9.8);
        } else if (pose === 'swing') {
            // The swing sweeps through, then the arms wrap up and around: the follow-through.
            const wrap = st.segT > 0.16 ? 2 : 0;
            add(0.4, -11 - wrap, 3, 1.6, team.jersey); fist(3.2, -11 - wrap);
        } else if (pose === 'cheer') {
            add(0.4, -19, 1.6, 8, team.jersey); fist(0.4, -20.4);
            add(-1.8, -19, 1.6, 8, team.jerseyShade); fist(-1.8, -20.4);
        } else if (pose === 'safe') {
            add(0.4, -12, 7, 1.6, team.jersey); fist(7.2, -12);
            detail(2.1, -12, 0.65, 1.6, team.capShade);
        } else if (pose === 'out') {
            add(0.4, -18, 1.6, 7, team.jersey); fist(0.2, -20);
        } else {
            add(-0.8 + sw, -11, 1.6, 3.2, team.jersey); hand(-0.8 + sw, -7.6);
        }
        const drop = pose === 'slump' ? 2.4 : 0;
        add(-1.6, -15 + drop, 3.6, 4, SKIN);
        add(-1.8, -14.2 + drop, 0.8, 2, HAIR);
        add(2, -13 + drop, 0.8, 0.9, SKIN_SHADE);   // the nose
        add(0.5, -13.6 + drop, 1.2, 1, EYE_WHITE);  // the eye: a white, with a pupil toward the way he looks
        add(1.0, -13.5 + drop, 0.7, 0.8, OUTLINE);
        add(0.3, -14.3 + drop, 1.5, 0.4, HAIR);     // the brow
        add(1.7, -11.9 + drop, 0.9, 0.4, SKIN_SHADE);   // the mouth
        add(-0.8, -13.4 + drop, 0.6, 1.2, SKIN_SHADE);  // the ear
        if (helmet) {
            add(-2.2, -16.6 + drop, 4.6, 3.6, team.cap);
            detail(-1.5, -16.2 + drop, 3.3, 0.55, team.capShade);
            add(-0.2, -14.2 + drop, 1.4, 1.8, team.capShade);   // the ear flap
        } else {
            add(-2, -16.2 + drop, 4.2, 2.4, team.cap);
            detail(-1.25, -15.8 + drop, 2.7, 0.5, team.capShade);
        }
        add(2, -14.2 + drop, 2.2, 0.8, team.capShade);          // the brim
        if (catcher && !actor.maskOff) {
            detail(1.8, -13.9 + drop, 0.55, 3.05, OUTLINE);
            detail(0.8, -12.5 + drop, 2.15, 0.45, OUTLINE);
        }
        return parts;
    }

    // One figure: an outline of the pieces, then the pieces, with every edge snapped to whole pixels.
    const DIVE_ROTATION = 1.4;
    // WHY: In a front view the figure's left side of the screen is his right hand; from behind it is his left.
    // The base sprite has its glove on screen-left, so a right-handed defender flips only when facing us.
    function mirrorFigure(actor, view, dir) {
        if (view === 'side') return dir < 0;
        if (actor.kind === 'fielder' || actor.kind === 'pitcher' || actor.kind === 'catcher')
            return view === 'front' ? actor.hand !== 'left' : actor.hand === 'left';
        return actor.hand === 'left';
    }
    function drawFigure(ctx, actor, st, time) {
        const p = project(st.x, st.y, 0);
        const u = Math.max(0.5, FIGURE_FEET * p.s / 16);   // pixels per figure unit
        const lying = st.pose === 'slide' || st.pose === 'dive' || st.pose === 'down';
        const f = lying ? { view: 'side', dir: st.direction || (st.vx < 0 ? -1 : 1) } : facingOf(actor, st);
        const parts = figureParts(actor, st, f.view, time);
        // WHY: the canvas has RES device pixels to a logical pixel, so a figure's edges snap to whole device pixels
        // (1/RES of a logical one). That is what lets a figure show an eye or a hand and still look pixel-square.
        const px = v => Math.round(v * u * RES) / RES;
        const hair = 1 / RES;   // one device pixel
        const box = q => {
            const x0 = px(q.x), y0 = px(q.y), x1 = px(q.x + q.w), y1 = px(q.y + q.h);
            return [x0, y0, Math.max(hair, x1 - x0), Math.max(hair, y1 - y0)];
        };

        ctx.save();
        ctx.translate(Math.round(p.x * RES) / RES, Math.round(p.y * RES) / RES);

        ctx.globalAlpha = 0.32;
        ctx.fillStyle = '#000';
        ctx.beginPath();
        ctx.ellipse(0, 0, 4.2 * u, 1.5 * u, 0, 0, Math.PI * 2);
        ctx.fill();
        ctx.globalAlpha = 1;
        if (st.z > 0) ctx.translate(0, -Math.round(st.z * p.s * 0.6));   // a jump: the figure leaves its shadow behind

        if (lying) ctx.rotate(f.dir * (st.pose === 'dive' ? DIVE_ROTATION * Math.min(1, (st.poseAge || 0) / 0.2) : -1.25));
        else if (mirrorFigure(actor, f.view, f.dir)) ctx.scale(-1, 1);
        if (actor.kind === 'catcher' && !actor.maskOff && (st.pose === 'stand' || st.pose === 'catch')) ctx.scale(1, 0.82);   // crouching
        if (st.pose === 'crouch' || st.pose === 'barehand') ctx.scale(1, 0.78);                                // a fielder bent over a ground ball
        if (actor.kind === 'batter' && inBatterBox(st) && (st.pose === 'stand' || st.pose === 'swing' || st.pose === 'bunt')) ctx.scale(1, stanceOf(actor).crouch);   // a slap hitter crouches

        ctx.fillStyle = OUTLINE;
        for (const q of parts) {
            if (q.outline === false) continue;
            const [x, y, w, h] = box(q);
            ctx.fillRect(x - hair, y - hair, w + 2 * hair, h + 2 * hair);
        }
        for (const q of parts) {
            const [x, y, w, h] = box(q);
            ctx.fillStyle = q.c;
            ctx.fillRect(x, y, w, h);
        }

        // The bat, for the batter: over the shoulder, then swept through.
        if (actor.kind === 'batter' && inBatterBox(st) && f.view === 'side' && (st.pose === 'stand' || st.pose === 'swing' || st.pose === 'bunt')) {
            const stance = stanceOf(actor);
            const waggle = st.pose === 'stand' && !calmRequested() ? Math.sin(time * 9 + (hashOf(actor.id) % 5)) * stance.wobble : 0;
            // The bat sweeps through (0.16 s), then carries on up and over the shoulder: the follow-through.
            // A bunter holds it level (a little above horizontal) out in front of him, over the plate, and does not swing.
            const angle = st.pose === 'bunt' ? 1.4
                : st.pose === 'swing'
                ? (st.segT <= 0.16 ? lerp(stance.angle - 0.3, 2.2, st.segT / 0.16) : lerp(2.2, 3.0, Math.min(1, (st.segT - 0.16) / 0.2)))
                : stance.angle + waggle;   // from straight up, toward the pitcher
            const hand = st.pose === 'bunt'
                ? { x: px(5), y: px(-9.8) }
                : { x: px(2.2), y: px(st.pose === 'swing' && st.segT > 0.16 ? -12.5 : -10.5) };   // the hands come up with it
            const tip = { x: hand.x + Math.sin(angle) * px(stance.length), y: hand.y - Math.cos(angle) * px(stance.length) };
            ctx.lineCap = 'butt';
            ctx.strokeStyle = OUTLINE;
            ctx.lineWidth = Math.max(3, px(1.4) + 2);
            ctx.beginPath(); ctx.moveTo(hand.x, hand.y); ctx.lineTo(tip.x, tip.y); ctx.stroke();
            ctx.strokeStyle = BAT;
            ctx.lineWidth = Math.max(1, px(1.4));
            ctx.beginPath(); ctx.moveTo(hand.x, hand.y); ctx.lineTo(tip.x, tip.y); ctx.stroke();
        }
        ctx.restore();

        // Dust lingers after the slide has reached the bag, then settles around the call.
        const slideLanding = (st.pose === 'safe' || st.pose === 'out') && st.previousPose === 'slide' && st.poseAge < 0.35;
        if (st.pose === 'slide' || slideLanding) {
            const age = st.pose === 'slide' ? Math.min(1, (st.poseAge || 0) / 0.3) : 1 + st.poseAge / 0.35;
            ctx.fillStyle = '#d2b48c';
            ctx.globalAlpha = Math.max(0, 0.6 * (1 - age * 0.7));
            for (let k = 0; k < 3; k++) {
                const r = Math.max(1, (1.2 + k * 0.9 + age * 3) * u * 0.6);
                ctx.fillRect(Math.round(p.x - f.dir * (k * 3 + 2) * u - r), Math.round(p.y - r * (0.6 + k * 0.25)), Math.round(r * 2), Math.max(1, Math.round(r)));
            }
            ctx.globalAlpha = 1;
        }
        if (st.pose === 'dive' && !calmRequested() && st.poseAge >= 0.2 && st.poseAge < 0.55) {
            const age = (st.poseAge - 0.2) / 0.35;
            ctx.fillStyle = '#b6c58e';
            ctx.globalAlpha = 0.55 * (1 - age);
            for (let k = 0; k < 3; k++) {
                const size = Math.max(1, Math.round((1 + k + age * 2) * u * 0.5));
                ctx.fillRect(Math.round(p.x - f.dir * (k * 3 + 3) * u), Math.round(p.y - (k % 2 + 1) * size), size, size);
            }
            ctx.globalAlpha = 1;
        }
    }

    // The picture is a fixed 640 wide but is shown at whatever size the page gives it (a phone shows it at about
    // half). Text is sized so that it comes out at least `screenPx` tall on the screen, and never below `minLogical`.
    const shownWidth = () => (state.canvas && state.canvas.clientWidth > 0 ? state.canvas.clientWidth : W);
    const textSize = (screenPx, minLogical) => Math.max(minLogical, Math.round(screenPx * W / shownWidth()));

    // A small label with a coloured background, centered on x and sitting just above y.
    function drawTag(ctx, text, x, y, bg) {
        const size = textSize(9, 8);
        ctx.font = `bold ${size}px "Courier New", monospace`;
        ctx.textAlign = 'center';
        const w = Math.ceil(ctx.measureText(text).width) + 6;
        const h = size + 3;
        const left = Math.max(1, Math.min(W - w - 1, Math.round(x - w / 2)));
        ctx.fillStyle = bg;
        ctx.fillRect(left, Math.round(y - h), w, h);
        ctx.fillStyle = '#fff';
        ctx.fillText(text, left + w / 2, Math.round(y - 2));
    }

    const surname = label => String(label || '').trim().split(/\s+/).pop().toUpperCase().slice(0, 12);

    // Where the hand that has the ball is, in figure units (before mirroring), as figureParts draws each pose.
    function handOf(pose, view, kind) {
        if (view === 'side') {
            if (pose === 'throw' || pose === 'windup') return { x: 1.2, y: -20 };
            if (pose === 'dive') return { x: 1.1, y: -20.5 };
            if (pose === 'catch') return { x: 6, y: -13.9 };
            if (pose === 'overShoulder') return { x: 2.8, y: -19 };
            if (pose === 'tag') return { x: 5.9, y: -7.9 };
            if (pose === 'crouch') return { x: 4.4, y: -4 };
            if (pose === 'barehand') return { x: 5.9, y: -3 };
            if (pose === 'transfer') return { x: 2, y: -10.4 };
            if (pose === 'turn' || pose === 'plant') return { x: 3.5, y: -9.8 };
            if (pose === 'pop') return { x: 3.5, y: -9.8 };
            if (pose === 'reach') return { x: 7.1, y: -13.8 };
            if (pose === 'stretch') return { x: 3.8, y: -9.4 };
            return { x: 0, y: -6.7 };
        }
        if (pose === 'windup') return { x: 0, y: -20 };
        if (pose === 'kick') return { x: 0, y: -10.8 };
        if (pose === 'sidearm') return { x: 9.8, y: -12.2 };
        if (pose === 'throw') return { x: 3.8, y: -19.8 };
        if (pose === 'catch' || pose === 'dive') return { x: -4, y: -18.5 };
        if (pose === 'overShoulder') return { x: 4, y: -19.4 };
        if (pose === 'tag') return { x: -3.8, y: -2 };
        if (pose === 'crouch') return { x: -3.6, y: -4.7 };
        if (pose === 'barehand') return { x: 4.4, y: -3 };
        if (pose === 'transfer') return { x: 1.5, y: -10.7 };
        if (pose === 'turn' || pose === 'plant') return { x: -3.6, y: -11.4 };
        if (pose === 'pop') return { x: -1.2, y: -10.5 };
        if (pose === 'reach') return { x: -4.6, y: -15.5 };
        if (pose === 'stretch') return { x: 0, y: -8.2 };
        return kind === 'pitcher' ? { x: 3.8, y: -6.7 } : { x: -3.6, y: -7.6 };
    }

    // Where that hand is on the picture, drawn the way drawFigure draws the figure.
    function handScreen(actor, st) {
        if (st.pose === 'slide' || st.pose === 'down') return null;
        if (st.pose === 'dive') {
            const p = project(st.x, st.y, 0);
            const u = Math.max(0.5, FIGURE_FEET * p.s / 16);
            const glove = handOf('dive', 'side');
            // WHY: The diving sprite rotates around its feet; its glove must follow that same rotation.
            const angle = (st.direction || (st.vx < 0 ? -1 : 1)) * DIVE_ROTATION * Math.min(1, (st.poseAge || 0) / 0.2);
            const gx = glove.x * u, gy = glove.y * u;
            const jump = st.z > 0 ? Math.round(st.z * p.s * 0.6) : 0;
            return { x: Math.round((p.x + gx * Math.cos(angle) - gy * Math.sin(angle)) * RES) / RES,
                y: Math.round((p.y - jump + gx * Math.sin(angle) + gy * Math.cos(angle)) * RES) / RES };
        }
        const f = facingOf(actor, st);
        const p = project(st.x, st.y, 0);
        const u = Math.max(0.5, FIGURE_FEET * p.s / 16);
        const h = handOf(st.pose, f.view, actor.kind);
        const flip = mirrorFigure(actor, f.view, f.dir) ? -1 : 1;
        const crouch = actor.kind === 'catcher' && !actor.maskOff && (st.pose === 'stand' || st.pose === 'catch')
            ? 0.82 : st.pose === 'crouch' || st.pose === 'barehand' ? 0.78 : 1;
        const jump = st.z > 0 ? Math.round(st.z * p.s * 0.6) : 0;
        return { x: Math.round(p.x) + flip * Math.round(h.x * u), y: Math.round(p.y) - jump + Math.round(h.y * crouch * u) };
    }

    // The ball is in a hand while it sits still beside the pitcher, the catcher or a fielder (the script only
    // puts it near him). Returns where that hand is on the picture, or null if nobody has it.
    const HOLDERS = new Set(['pitcher', 'catcher', 'fielder']);
    function holdingHand(drawn, ballSt) {
        if (ballSt.z > 14 || Math.hypot(ballSt.vx, ballSt.vy) > 8) return null;
        let best = null, bestDist = Infinity;
        for (const d of drawn) {
            if (!HOLDERS.has(d.actor.kind)) continue;
            const dist = Math.hypot(d.st.x - ballSt.x, d.st.y - ballSt.y);
            const reach = d.actor.kind === 'pitcher' && ballSt.z >= 6 ? 7 : 3.2;   // a pitcher's hand is out to his side, up high
            if (dist <= reach && dist < bestDist) { best = d; bestDist = dist; }
        }
        return best ? handScreen(best.actor, best.st) : null;
    }

    // `spin` (radians) turns the red seam: a ball in flight has one stitch mark that goes round; a ball in a hand has none.
    function ballDot(ctx, x, y, r, spin) {
        ctx.fillStyle = '#ffffff';
        ctx.strokeStyle = '#555';
        ctx.lineWidth = 0.8;
        ctx.beginPath();
        ctx.arc(x, y, r, 0, Math.PI * 2);
        ctx.fill();
        ctx.stroke();
        if (spin !== undefined) {
            const s = Math.max(1, r * 0.5);
            ctx.fillStyle = '#c0392b';
            ctx.fillRect(x + Math.cos(spin) * r * 0.5 - s / 2, y + Math.sin(spin) * r * 0.5 - s / 2, s, s);
            ctx.fillRect(x - Math.cos(spin) * r * 0.5 - s / 2, y - Math.sin(spin) * r * 0.5 - s / 2, s, s);
        }
    }

    // WHY: the bat does not stay in the batter's hands once he runs: it is dropped by the plate, and on a home run it is
    // flipped away (a short arc, spinning). It lies where it fell until the play ends. Only a man who swung had one.
    function drawDroppedBat(ctx, actor, keys, st, t, outcome, calm) {
        const watch = outcome === 'HomeRun' && keys.find(k => k.pose === 'watch' && k.t > 0);
        if (actor.kind !== 'batter' || (inBatterBox(st) && !watch) || !keys.some(k => k.pose === 'swing' || k.pose === 'bunt')) return;
        const left = watch || keys.find(k => k.pose === 'run' && k.t > 0);   // the flip begins while he watches the drive
        if (!left || t < left.t) return;
        const flip = outcome === 'HomeRun' && !calm;
        const dir = keys[0].x < 0 ? -1 : 1;                          // toward his own side of the plate
        const flight = flip ? 0.6 : 0.25, reach = flip ? 12 : 4;
        const age = t - left.t, k = Math.min(1, age / flight);
        const bx = keys[0].x + dir * reach * k, by = keys[0].y - 1 - (flip ? 2 * k : 0);
        const bz = k < 1 ? (flip ? 14 : 3) * Math.sin(Math.PI * k) + 0.6 : 0.6;
        const turn = k < 1 ? age * (flip ? 14 : 8) : 0;              // spinning in the air, flat once it has landed
        const a = project(bx - 3 * Math.cos(turn), by, bz - 3 * Math.sin(turn));
        const b = project(bx + 3 * Math.cos(turn), by, bz + 3 * Math.sin(turn));
        const width = Math.max(1.5, 0.5 * a.s);
        ctx.lineCap = 'butt';
        ctx.strokeStyle = OUTLINE; ctx.lineWidth = width + 1.5;
        ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
        ctx.strokeStyle = BAT; ctx.lineWidth = width;
        ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
    }

    // The pitch in flight is named beside the ball (a cue "pitch:<name>" from the release until it is across the plate).
    const PITCH_LABELS = { Fastball: 'FASTBALL', Curveball: 'CURVE', Slider: 'SLIDER', Changeup: 'CHANGE-UP', Pitchout: 'PITCHOUT' };
    const PITCH_SECONDS = 0.9;
    function drawPitchTag(ctx, cues, ballKeys, t) {
        const cue = cues.find(c => c.kind === 'effect' && c.value.startsWith('pitch:') && c.t <= t && t < c.t + PITCH_SECONDS);
        if (!cue) return;
        const name = cue.value.slice(6);
        const st = sample(ballKeys, t);
        if (st.z > 14 || Math.hypot(st.vx, st.vy) < 8) return;   // in a hand, or gone up out of the picture
        const at = project(st.x, st.y, st.z);
        const size = textSize(8, 6);
        ctx.font = `bold ${size}px "Courier New", monospace`;
        ctx.textAlign = 'left';
        ctx.lineJoin = 'round'; ctx.lineWidth = 2;
        const text = PITCH_LABELS[name] || name.toUpperCase();
        const x = Math.min(W - 6 - ctx.measureText(text).width, at.x + 9), y = at.y - 7;
        ctx.strokeStyle = 'rgba(0,0,0,0.7)'; ctx.strokeText(text, x, y);
        ctx.fillStyle = '#ffffff'; ctx.fillText(text, x, y);
    }

    function drawBall(ctx, keys, t, calm, hand) {
        const st = sample(keys, t);
        if (hand) {
            ballDot(ctx, hand.x, hand.y, Math.max(2, 0.7 * project(st.x, st.y, 0).s));
            return;
        }
        const speed = Math.hypot(st.vx, st.vy);
        // WHY: a ball rolling along the ground skips: small hops that die away as it slows (the shadow stays on the grass).
        const hop = !calm && st.rolling && speed > 20
            ? Math.abs(Math.sin(st.f * Math.PI * 5)) * 3.2 * (1 - st.f) * Math.min(1, speed / 80) : 0;
        const shadow = project(st.x, st.y, 0);
        const ball = project(st.x, st.y, st.z + hop);
        const lift = Math.min(1, st.z / 60);
        ctx.globalAlpha = 0.55 - 0.15 * lift;
        ctx.fillStyle = '#000';
        ctx.beginPath();
        ctx.ellipse(shadow.x, shadow.y, Math.max(2.6, 1.9 * shadow.s) * (1 - 0.3 * lift), Math.max(1.2, 0.75 * shadow.s) * (1 - 0.3 * lift), 0, 0, Math.PI * 2);
        ctx.fill();

        // A short fading trail behind a ball that is moving, and a streak of blur when it is moving fast (a pitch, a throw).
        if (!calm) {
            const q = sample(keys, t - 0.045);
            const qp = project(q.x, q.y, q.z);
            if (Math.hypot(qp.x - ball.x, qp.y - ball.y) > 5) {
                ctx.globalAlpha = 0.3;
                ctx.strokeStyle = '#ffffff';
                ctx.lineCap = 'round';
                ctx.lineWidth = Math.max(2, 1.3 * ball.s);
                ctx.beginPath(); ctx.moveTo(qp.x, qp.y); ctx.lineTo(ball.x, ball.y); ctx.stroke();
                ctx.lineCap = 'butt';
            }
            for (let i = 4; i >= 1; i--) {
                const q = sample(keys, t - i * 0.03);
                const qp = project(q.x, q.y, q.z);
                if (Math.hypot(qp.x - ball.x, qp.y - ball.y) < 2) continue;
                ctx.globalAlpha = 0.45 - 0.09 * i;
                ctx.fillStyle = '#ffffff';
                const r = Math.max(1, 0.55 * qp.s * (1 - 0.12 * i));
                ctx.fillRect(Math.round(qp.x - r), Math.round(qp.y - r), Math.round(r * 2), Math.round(r * 2));
            }
        }
        ctx.globalAlpha = 1;

        const r = Math.max(2, 0.7 * ball.s);
        if (ball.y < 14) {
            // Too high to see: a little arrow at the top edge, over the shadow that shows where it will land.
            ctx.fillStyle = '#ffffff';
            ctx.beginPath();
            const x = Math.max(6, Math.min(W - 6, ball.x));
            ctx.moveTo(x, 4); ctx.lineTo(x - 5, 12); ctx.lineTo(x + 5, 12);
            ctx.closePath();
            ctx.fill();
            return;
        }
        ballDot(ctx, ball.x, ball.y, r, calm ? undefined : t * 38);
    }

    function drawBurst(ctx, cue, t, calm) {
        const p = project(cue.x ?? 0, cue.y ?? 0, 14);
        const age = calm ? 0.3 : (t - cue.t) / EFFECT_SECONDS;   // for someone who prefers less motion it stays still
        const r = 6 + 22 * age;
        ctx.globalAlpha = calm ? 0.7 : 1 - age;
        ctx.fillStyle = '#ffd54f';
        ctx.beginPath();
        for (let i = 0; i < 8; i++) {
            const a = i * Math.PI / 4, rr = i % 2 ? r * 0.45 : r;
            ctx.lineTo(p.x + rr * Math.cos(a), p.y + rr * Math.sin(a));
        }
        ctx.closePath();
        ctx.fill();
        ctx.globalAlpha = 1;
    }

    function drawFocus(ctx, cue, t, calm) {
        const at = project(cue.x ?? 0, cue.y ?? 0, 0);
        const age = Math.min(1, (t - cue.t) / EFFECT_SECONDS);
        const radius = Math.max(7, 9 * at.s) * (calm ? 1 : 0.9 + age * 0.15);
        ctx.save();
        ctx.globalAlpha = calm ? 0.4 : 0.45 * (1 - age);
        ctx.strokeStyle = '#ffe08a';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.ellipse(at.x, at.y, radius, radius * 0.38, 0, 0, Math.PI * 2);
        ctx.stroke();
        ctx.restore();
    }

    function drawBounce(ctx, cue, t, calm) {
        const age = Math.min(1, (t - cue.t) / 0.38);
        if (age < 0 || age >= 1) return;
        const at = project(cue.x ?? 0, cue.y ?? 0, 0);
        ctx.save();
        ctx.globalAlpha = 0.65 * (1 - age);
        ctx.fillStyle = '#d2b485';
        for (const side of [-1, 0, 1]) {
            const spread = calm ? 0 : age * 9;
            const x = at.x + side * (3 + spread);
            const y = at.y - (calm ? 0 : age * (2 + Math.abs(side) * 3));
            ctx.fillRect(Math.round(x), Math.round(y), side === 0 ? 3 : 2, 2);
        }
        ctx.restore();
    }

    function drawMisplay(ctx, cue, t, calm) {
        const at = project(cue.x ?? 0, cue.y ?? 0, 0);
        const age = calm ? 0.25 : Math.min(1, (t - cue.t) / EFFECT_SECONDS);
        ctx.save();
        ctx.globalAlpha = calm ? 0.7 : 0.85 * (1 - age);
        ctx.strokeStyle = '#ff8b69';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.ellipse(at.x, at.y, 7 + age * 9, 4 + age * 4, 0, 0, Math.PI * 2);
        ctx.stroke();
        ctx.restore();
    }

    function drawWallClear(ctx, cue, t, calm) {
        const angle = Math.atan2(cue.x || 0, cue.y || 1) * 180 / Math.PI;
        const at = project(cue.x ?? 0, cue.y ?? 0, wallHeight(state.park, angle) + 14);
        const age = calm ? 0.25 : Math.min(1, (t - cue.t) / EFFECT_SECONDS);
        ctx.save();
        ctx.globalAlpha = calm ? 0.65 : 0.85 * (1 - age);
        ctx.strokeStyle = '#ffe08a';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(at.x, at.y, 5 + age * 14, Math.PI * 0.2, Math.PI * 0.8);
        ctx.stroke();
        ctx.beginPath();
        ctx.arc(at.x, at.y, 5 + age * 14, Math.PI * 1.2, Math.PI * 1.8);
        ctx.stroke();
        ctx.restore();
    }

    function drawDiscardedMask(ctx, cue) {
        const at = project(cue.x ?? 0, cue.y ?? 0);
        ctx.save();
        ctx.fillStyle = '#172028';
        ctx.beginPath();
        ctx.ellipse(at.x, at.y - 1, 5, 3, 0.3, 0, Math.PI * 2);
        ctx.fill();
        ctx.strokeStyle = '#aab5bd';
        ctx.lineWidth = 1;
        for (let i = -2; i <= 2; i += 2) {
            ctx.beginPath(); ctx.moveTo(at.x + i - 1, at.y - 3); ctx.lineTo(at.x + i + 1, at.y + 1); ctx.stroke();
        }
        ctx.restore();
    }

    // WHY: Runners show a brief burst of urgency when the misplay opens up an extra base.
    function drawErrorRunnerSurge(ctx, drawn, cue, t, calm) {
        if (!cue || calm || t < cue.t || t >= cue.t + 0.7) return;
        ctx.save();
        ctx.strokeStyle = '#ffe08a';
        ctx.lineWidth = 1.5;
        ctx.globalAlpha = 0.65 * (1 - (t - cue.t) / 0.7);
        for (const { actor, st } of drawn) {
            if ((actor.kind !== 'runner' && actor.kind !== 'batter') || st.pose !== 'run' || Math.hypot(st.vx, st.vy) < 1) continue;
            const now = project(st.x, st.y), prior = project(st.x - st.vx * 0.1, st.y - st.vy * 0.1);
            const dx = now.x - prior.x, dy = now.y - prior.y;
            const length = Math.hypot(dx, dy) || 1;
            for (let i = 0; i < 3; i++) {
                const side = (i - 1) * 3;
                const x = now.x - dx / length * (6 + i * 3) - dy / length * side;
                const y = now.y - dy / length * (6 + i * 3) + dx / length * side - 8;
                ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x - dx / length * 5, y - dy / length * 5); ctx.stroke();
            }
        }
        ctx.restore();
    }

    const IMPACT_SECONDS = 0.24;
    function drawImpact(ctx, cue, t, calm) {
        const elapsed = t - cue.t;
        if (elapsed >= IMPACT_SECONDS) return;
        const at = project(cue.x ?? 0, cue.y ?? 0, cue.value === 'contact' ? 3 : cue.value === 'glove' ? 4 : 0);
        const age = calm ? 0.25 : elapsed / IMPACT_SECONDS;
        const radius = (cue.value === 'contact' ? 5 : 3) + (calm ? 0 : 7 * age);
        ctx.save();
        ctx.globalAlpha = calm ? 0.55 : 0.8 * (1 - age);
        ctx.strokeStyle = cue.value === 'call' ? '#ffe08a' : '#ffffff';
        ctx.lineWidth = cue.value === 'contact' ? 2 : 1.5;
        ctx.beginPath();
        if (cue.value === 'call') ctx.ellipse(at.x, at.y, radius * 1.5, radius * 0.6, 0, 0, Math.PI * 2);
        else for (let i = 0; i < 4; i++) {
            const angle = i * Math.PI / 2;
            ctx.moveTo(at.x + Math.cos(angle) * radius * 0.5, at.y + Math.sin(angle) * radius * 0.5);
            ctx.lineTo(at.x + Math.cos(angle) * radius, at.y + Math.sin(angle) * radius);
        }
        ctx.stroke();
        ctx.restore();
    }

    // The crowd sparkles for a moment after a cheer or a home run.
    const FLICKER_SECONDS = 1.4;
    function cheering(cues, t) {
        return cues.some(c => ((c.kind === 'sound' && c.value === 'cheer') || (c.kind === 'effect' && c.value === 'burst'))
            && c.t <= t && t < c.t + FLICKER_SECONDS) || standing(cues, t);
    }

    // The crowd gets to its feet (after a home run, or when the home team wins at the last): from the cue to the end of the scene.
    const STAND_RISE = 0.5;
    function standing(cues, t) {
        return cues.some(c => c.kind === 'effect' && c.value === 'stand' && c.t <= t);
    }

    // Fans on their feet: a little light figure with its arms up over each seat, rising over half a second.
    function drawStanding(ctx, seats, cues, t) {
        let since = 0;
        for (const c of cues) if (c.kind === 'effect' && c.value === 'stand' && c.t <= t) since = Math.max(since, t - c.t);
        const rise = Math.min(1, since / STAND_RISE);
        const rnd = seeded(5);
        for (let k = 0; k < seats.length; k += 2) {
            const seat = seats[k];
            const h = Math.round((3 + 2 * rnd()) * rise);
            if (h < 1) continue;
            ctx.fillStyle = FLICKER[Math.floor(rnd() * FLICKER.length)];
            ctx.fillRect(Math.round(seat.x), Math.round(seat.y) - h, 2, h + 2);
        }
    }

    // Rain falls from a "rain" cue until the next "clear" cue.
    function raining(cues, t) {
        let wet = false;
        for (const c of [...cues].sort((a, b) => a.t - b.t))
            if (c.kind === 'effect' && c.t <= t && (c.value === 'rain' || c.value === 'clear')) wet = c.value === 'rain';
        return wet;
    }

    // Grey light over the picture, and slanting streaks that fall a little further every frame.
    function drawRain(ctx, cues, t, calm) {
        let since = 0;
        for (const c of cues) if (c.kind === 'effect' && c.value === 'rain' && c.t <= t) since = Math.max(since, t - c.t);
        ctx.globalAlpha = Math.min(0.38, 0.1 + since * 0.3);
        ctx.fillStyle = '#1b2433';
        ctx.fillRect(0, 0, W, H);
        ctx.globalAlpha = 0.55;
        ctx.fillStyle = '#c9d6e8';
        const rnd = seeded(31);
        const drops = calm ? 0 : 140;
        for (let k = 0; k < drops; k++) {
            const x0 = rnd() * W, y0 = rnd() * H, speed = 380 + rnd() * 220;
            const y = (y0 + t * speed) % H;
            const x = (((x0 - t * speed * 0.18) % W) + W) % W;
            ctx.fillRect(Math.round(x), Math.round(y), 1, 6);
        }
        ctx.globalAlpha = 1;
    }

    // A dome's roof: steel trusses and lights across the top of the picture.
    function drawRoof(ctx) {
        ctx.fillStyle = '#343842';
        ctx.fillRect(0, 0, W, 34);
        ctx.fillStyle = '#23262e';
        ctx.fillRect(0, 34, W, 3);
        ctx.strokeStyle = '#5a6070';
        ctx.lineWidth = 1;
        for (let x = -20; x < W + 40; x += 40) {
            ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x + 20, 34); ctx.lineTo(x + 40, 0); ctx.stroke();
        }
        ctx.fillStyle = '#fff3b0';
        for (let x = 20; x < W; x += 40) ctx.fillRect(x - 1, 30, 3, 3);
    }

    // The newest score card cue at or before t: its lines, or null.
    function activeCard(cues, t) {
        let card = null;
        for (const c of cues) if (c.kind === 'card' && c.t <= t) card = String(c.value).split('\n');
        return card;
    }

    // A score card in the middle of the picture: the first line is the heading.
    // The score card's type size and width: wide enough for its longest line, and the type made smaller if a line
    // would not fit across the picture at all.
    const cardFont = (i, size) => `bold ${i === 0 ? size + 3 : size}px "Courier New", monospace`;
    function cardLayout(ctx, lines) {
        const widest = size => Math.max(...lines.map((l, i) => { ctx.font = cardFont(i, size); return ctx.measureText(l).width; }));
        const room = W - 40;
        let size = textSize(12, 15);
        while (size > 8 && widest(size) + 36 > room) size--;
        return { size, w: Math.min(room, Math.max(300, Math.ceil(widest(size)) + 36)) };
    }

    function drawCard(ctx, lines) {
        const { size, w } = cardLayout(ctx, lines);
        const lineHeight = size + 5;
        const h = 24 + lines.length * lineHeight;
        const x = 320 - w / 2, y = 150 - h / 2;
        ctx.fillStyle = '#000';
        ctx.fillRect(x, y, w, h);
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 2;
        ctx.strokeRect(x + 1, y + 1, w - 2, h - 2);
        ctx.strokeStyle = '#ffd54f';
        ctx.lineWidth = 1;
        ctx.strokeRect(x + 6.5, y + 6.5, w - 13, h - 13);
        ctx.fillStyle = '#fff';
        ctx.textAlign = 'center';
        lines.forEach((l, i) => {
            ctx.font = cardFont(i, size);
            ctx.fillStyle = i === 0 ? '#ffd54f' : '#fff';
            ctx.fillText(l, 320, y + 12 + size + i * lineHeight);
        });
    }

    function drawFlicker(ctx, seats, t) {
        if (!seats.length) return;
        const rnd = seeded(Math.floor(t * 10) + 7);
        for (let k = 0; k < 60; k++) {
            const seat = seats[Math.floor(rnd() * seats.length)];
            ctx.fillStyle = FLICKER[Math.floor(rnd() * FLICKER.length)];
            ctx.fillRect(Math.round(seat.x), Math.round(seat.y), 2, 2);
        }
    }

    function drawMessage(ctx, text) {
        if (!text) return;
        const size = textSize(11, 13);
        const lineHeight = size + 3;
        ctx.font = `bold ${size}px "Courier New", monospace`;
        ctx.textAlign = 'center';
        const lines = wrapText(ctx, String(text).toUpperCase(), 408);
        const h = 12 + lines.length * lineHeight;
        ctx.fillStyle = '#000';
        ctx.fillRect(96, 8, 448, h);
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 2;
        ctx.strokeRect(97, 9, 446, h - 2);
        ctx.fillStyle = '#fff';
        lines.forEach((l, i) => ctx.fillText(l, 320, 14 + size + i * lineHeight));
    }

    // ── The player ───────────────────────────────────────────────────────────

    const state = {
        canvas: null, ctx: null, backdrop: null, parkKey: '', park: DEFAULT_PARK,
        prepared: null, message: '', raf: 0, resolve: null, seats: [], guard: 0, lastT: 0, lastNames: false,
        overlay: null, octx: null, signY: 6,
    };

    // A play that has not finished this many seconds after it should have is finished for it (a browser can stop
    // calling for frames, for example in a tab nobody is looking at, and the game must not wait for ever).
    const config = { guardSlack: 3 };

    const hidden = () => typeof document !== 'undefined' && document.hidden === true;

    const now = () => (typeof performance !== 'undefined' ? performance.now() : Date.now());
    const requestFrame = fn => (typeof window !== 'undefined' && window.requestAnimationFrame
        ? window.requestAnimationFrame(fn) : setTimeout(() => fn(now()), 16));
    const cancelFrame = id => (typeof window !== 'undefined' && window.cancelAnimationFrame
        ? window.cancelAnimationFrame(id) : clearTimeout(id));

    const asObject = v => (typeof v === 'string' ? JSON.parse(v) : v);

    function setPark(parkInput) {
        const park = { ...DEFAULT_PARK, ...(asObject(parkInput) || {}) };
        const key = JSON.stringify(park);
        if (key === state.parkKey && state.backdrop) return;
        state.park = park;
        state.parkKey = key;
        rebuildBackdrop();
    }

    function rebuildBackdrop() {
        if (!state.canvas) return;
        const off = state.backdrop || document.createElement('canvas');
        off.width = W; off.height = H;
        const octx = off.getContext('2d');
        const built = buildBackdrop(octx, state.park);
        state.seats = visibleSeats(octx, built.seats);
        state.signY = built.signY;
        state.backdrop = off;
    }

    // Someone who prefers less motion gets no sparkle, trail or growing burst.
    function calmRequested() {
        try {
            return !!(typeof window !== 'undefined' && window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
        } catch { return false; }
    }

    const NAMED_POSES = new Set(['catch', 'throw', 'tag', 'dive', 'slide', 'swing', 'barehand', 'overShoulder']);

    function drawActionCloseup(ctx, closeup, drawn, ballKeys, t, calm, ballLoose) {
        if (!closeup) return;
        const fielder = drawn.find(d => d.actor.id === closeup.actor.id);
        if (!fielder) return;

        const x = closeup.x < 0 ? 442 : 18, y = 88, w = 180, h = 122;
        const focus = project(closeup.x, closeup.y, closeup.z || 0);
        ctx.save();
        ctx.fillStyle = '#101822';
        ctx.fillRect(x, y, w, h);
        ctx.strokeStyle = '#f5d78a';
        ctx.lineWidth = 2;
        ctx.strokeRect(x + 1, y + 1, w - 2, h - 2);
        ctx.beginPath();
        ctx.rect(x + 4, y + 22, w - 8, h - 26);
        ctx.clip();
        // WHY: Redraw the field and actors through a scaled canvas transform so the inset retains sprite detail.
        ctx.translate(x + w / 2, y + (closeup.centerY || (closeup.title === 'CATCH CLOSE-UP' ? 76 : 88)));
        ctx.scale(closeup.scale || 3.2, closeup.scale || 3.2);
        ctx.translate(-focus.x, -focus.y);
        if (state.backdrop) ctx.drawImage(state.backdrop, 0, 0, W, H);
        const other = closeup.other && drawn.find(d => d.actor.id === closeup.other.id);
        const actors = [fielder, other].filter(Boolean);
        if (!closeup.otherOffset) actors.sort((a, b) => b.st.y - a.st.y);
        for (const d of actors) {
            ctx.save();
            if (d === other && closeup.otherOffset) ctx.translate(closeup.otherOffset / (closeup.scale || 3.2), 0);
            // WHY: Keep the arriving runner down briefly after the call so the slide and tag read as one play.
            const arriving = d === other && d.st.previousPose === 'slide' &&
                (closeup.title === 'PLAY AT HOME' && d.st.pose === 'safe' &&
                    (d.st.poseAge < 0.24 || (closeup.decision && t < closeup.callT + 0.2)) ||
                 closeup.slideThroughCall && d.st.pose === 'out' && d.st.poseAge < 0.12);
            drawFigure(ctx, d.actor, arriving ? { ...d.st, pose: 'slide' } : d.st, t);
            ctx.restore();
        }
        if (ballKeys) {
            const hand = ballLoose ? null : holdingHand([fielder], sample(ballKeys, t));
            if (hand) ballDot(ctx, hand.x, hand.y, 1.2);
            else drawBall(ctx, ballKeys, t, calm, null);
        }
        ctx.restore();

        ctx.save();
        ctx.fillStyle = '#f5d78a';
        ctx.font = 'bold 11px "Courier New", monospace';
        ctx.textAlign = 'left';
        ctx.fillText(closeup.title, x + 10, y + 16);
        if (closeup.callLabel && t >= closeup.callT) {
            ctx.fillStyle = closeup.callColor || '#8ee79d';
            ctx.fillText(closeup.callLabel, x + w - 40, y + 16);
        }
        ctx.restore();
    }

    // The figures on the field at script time t, the ball and the message. With `names`, the people in the
    // middle of the action get a name tag.
    function drawScene(t, names) {
        const ctx = state.ctx;
        if (!ctx) return;
        state.lastT = t;
        state.lastNames = !!names;
        const calm = calmRequested();
        ctx.setTransform(RES, 0, 0, RES, 0, 0);   // everything below is in logical (640x400) units
        ctx.imageSmoothingEnabled = false;
        if (state.backdrop) ctx.drawImage(state.backdrop, 0, 0, W, H);   // the backdrop keeps its chunky pixels
        else { ctx.fillStyle = COLORS.grassFoul; ctx.fillRect(0, 0, W, H); }
        drawDistances(ctx);

        const text = textLayer();
        const p = state.prepared;
        if (!p) { drawSign(text); return; }
        if (!calm && cheering(p.cues, t)) drawFlicker(ctx, state.seats, t);
        if (standing(p.cues, t)) drawStanding(ctx, state.seats, p.cues, calm ? 1e9 : t);

        const maskCue = p.cues.find(c => c.kind === 'effect' && c.value === 'mask' && c.t <= t);
        const drawn = [];
        let ballKeys = null;
        for (const actor of p.script.actors) {
            const keys = p.tracks.get(actor.id);
            if (actor.kind === 'ball') ballKeys = keys;
            else drawn.push({ actor: actor.kind === 'catcher' && maskCue ? { ...actor, maskOff: true } : actor,
                st: sample(keys, t) });
        }
        drawn.sort((a, b) => b.st.y - a.st.y);   // far figures first, so near ones are in front
        const effects = activeEffects(p.cues, t);
        for (const e of effects) if (e.value === 'focus') drawFocus(ctx, e, t, calm);
        if (maskCue) drawDiscardedMask(ctx, maskCue);
        drawErrorRunnerSurge(ctx, drawn, p.cues.find(c => c.kind === 'effect' && c.value.startsWith('error:')), t, calm);
        for (const d of drawn) drawDroppedBat(ctx, d.actor, p.tracks.get(d.actor.id), d.st, t, p.script.outcome, calm);
        for (const d of drawn) drawFigure(ctx, d.actor, d.st, t);
        const ballLoose = !!(p.error && p.looseUntil && p.error.t <= t && t < p.looseUntil);
        const seats = p.script.outcome === 'HomeRun' && p.cues.find(c => c.kind === 'effect' && c.value === 'burst');
        if (ballKeys && (!seats || t < seats.t + 0.45))
            drawBall(ctx, ballKeys, t, calm, ballLoose ? null : holdingHand(drawn, sample(ballKeys, t)));
        if (ballKeys) drawPitchTag(text, p.cues, ballKeys, t);
        for (const e of effects) {
            if (e.value === 'burst') drawBurst(ctx, e, t, calm);
            else if (e.value === 'bounce') drawBounce(ctx, e, t, calm);
            else if (e.value === 'contact' || e.value === 'glove' || e.value === 'call') drawImpact(ctx, e, t, calm);
            else if (e.value.startsWith('error:')) drawMisplay(ctx, e, t, calm);
            else if (e.value === 'wallclear') drawWallClear(ctx, e, t, calm);
        }
        if (raining(p.cues, t)) drawRain(ctx, p.cues, t, calm);

        drawSign(text);
        drawActionCloseup(ctx, p.closeups.find(c => c.start <= t && t <= c.end), drawn, ballKeys, t, calm, ballLoose);

        // Flags for a runner who has just been called out or safe, and names for the people handling the ball.
        let tags = 0;
        const plateDecision = p.cues.find(c => c.kind === 'effect' && c.value === 'platecall');
        for (const { actor, st } of drawn) {
            const top = project(st.x, st.y, 0);
            const head = top.y - FIGURE_FEET * top.s - 3;
            if (st.ended && st.since < 1.3 && st.pose === 'out' && actor.kind !== 'ball') drawTag(text, 'OUT', top.x, head, '#b71c1c');
            else if (st.ended && st.since < 1.3 && st.pose === 'safe' &&
                !(plateDecision && t < plateDecision.t && Math.hypot(st.x, st.y) < 4))
                drawTag(text, 'SAFE', top.x, head, '#2e7d32');
            else if (names && tags < 3 && actor.label && NAMED_POSES.has(st.pose) && !st.ended) {
                drawTag(text, surname(actor.label), top.x, head, 'rgba(0,0,0,0.78)');
                tags++;
            }
        }
        const card = activeCard(p.cues, t);
        if (card) drawCard(text, card);
        else drawMessage(text, activeBanner(p.cues, t, state.message));
    }

    // Text is drawn on a second canvas laid over the picture, at the real resolution of the screen (and of a
    // high-density display), so that it is sharp while the field and the players keep their hard pixel edges.
    // Drawing still uses the picture's own 640x400 units. With no second canvas the text goes on the picture.
    // This also clears the text drawn last time.
    function textLayer() {
        if (!state.overlay || !state.octx) return state.ctx;
        const dpr = (typeof window !== 'undefined' && window.devicePixelRatio) || 1;
        const cw = shownWidth();
        const ch = state.canvas.clientHeight > 0 ? state.canvas.clientHeight : cw * H / W;
        const w = Math.max(1, Math.round(cw * dpr)), h = Math.max(1, Math.round(ch * dpr));
        if (state.overlay.width !== w || state.overlay.height !== h) { state.overlay.width = w; state.overlay.height = h; }
        const c = state.octx;
        c.setTransform(1, 0, 0, 1, 0, 0);
        c.clearRect(0, 0, w, h);
        c.setTransform(w / W, 0, 0, h / H, 0, 0);
        return c;
    }

    // The lettering on the sign on the center-field wall: the park's name, with the game's name under it. A park with
    // no name (the generic one) shows only the game's name.
    function drawSign(ctx) {
        ctx.textAlign = 'center';
        const name = String(state.park.name || '').trim().toUpperCase();
        if (!name) {
            ctx.fillStyle = '#ffffff';
            ctx.font = 'bold 14px "Courier New", monospace';
            ctx.fillText('MINILEAGUE BASEBALL', 320, state.signY + 19);
            return;
        }
        // WHY: a long name ("OAKLAND-ALAMEDA COUNTY COLISEUM") is shrunk until it fits the sign instead of running off it.
        let size = 13;
        ctx.font = `bold ${size}px "Courier New", monospace`;
        while (size > 7 && ctx.measureText(name).width > 184) {
            size--;
            ctx.font = `bold ${size}px "Courier New", monospace`;
        }
        ctx.fillStyle = '#ffffff';
        ctx.fillText(name, 320, state.signY + 14);
        ctx.fillStyle = '#d9c6a5';
        ctx.font = 'bold 7px "Courier New", monospace';
        ctx.fillText('MINILEAGUE BASEBALL', 320, state.signY + 23);
    }

    // WHY: the posted distances, painted on the wall where a park posts them: the foul poles, the alleys and center. The
    // wall is a curve, so each number is drawn in the wall's own plane: one affine transform per number, built from where
    // the wall's tangent and its vertical go on the picture. Near the poles the wall runs away from us, so the digits
    // squeeze and slant with it; at center it faces us and they are square. Draw on the picture before the players,
    // so a fielder standing near the wall occludes the painted numbers.
    // NOTE: the two pole numbers sit a little way in from the pole (at 40 degrees, not 45) so they are not cut off by the
    // edge of the picture; they still show the distance at the pole.
    function drawDistances(ctx) {
        const park = state.park;
        const posts = [[-40, park.leftField], [-22.5, park.leftCenter], [0, park.center], [22.5, park.rightCenter], [40, park.rightField]];
        // The smallest a digit may be on the screen, in logical pixels, so a short wall or a small phone stays legible.
        const minPx = textSize(8, 6);
        for (const [angle, feet] of posts) {
            if (!feet) continue;
            const wall = a => polar(a, wallDistance(park, a));
            const h = wallHeight(park, angle);
            const at = ground(wall(angle), h * 0.45);
            // Screen vectors for one foot along the wall and one foot up it.
            const p0 = ground(wall(angle - 2)), p1 = ground(wall(angle + 2));
            const w0 = wall(angle - 2), w1 = wall(angle + 2);
            const run = Math.hypot(w1.x - w0.x, w1.y - w0.y);
            const ex = { x: (p1.x - p0.x) / run, y: (p1.y - p0.y) / run };
            const ey = { x: 0, y: ground(wall(angle), 1).y - ground(wall(angle), 0).y };   // up the wall (screen y falls as z rises)
            const up = Math.abs(ey.y);
            // Digit height in feet: most of the wall, but never less than the legible minimum on the screen.
            const tall = Math.max(Math.min(h * 0.6, 6), minPx / up);
            ctx.save();
            ctx.transform(ex.x, ex.y, -ey.x, -ey.y, at.x, at.y);   // text's down is the wall's down
            ctx.font = `bold ${tall}px "Courier New", monospace`;
            ctx.textAlign = 'center';
            ctx.lineJoin = 'round';
            ctx.lineWidth = tall * 0.22;
            ctx.strokeStyle = 'rgba(0,0,0,0.65)';
            ctx.strokeText(String(feet), 0, tall * 0.35);
            ctx.fillStyle = '#ffffff';
            ctx.fillText(String(feet), 0, tall * 0.35);
            ctx.restore();
        }
    }

    // The picture was resized (the window changed, or a phone turned): draw again at the new size.
    function redraw() {
        if (!state.resolve) drawScene(state.lastT, state.lastNames);
    }

    function playSound(name) {
        try {
            const sound = typeof window !== 'undefined' ? window.mlbSound : undefined;
            if (sound && typeof sound.play === 'function') sound.play(name);
        } catch { /* sound is best-effort */ }
    }

    function finish(skipped) {
        if (!state.resolve) return;
        cancelFrame(state.raf);
        clearTimeout(state.guard);
        const p = state.prepared;
        const end = p ? p.script.duration : 0;
        // Skipping jumps to the end quietly: the sounds that were still to come are dropped.
        const done = state.resolve;
        state.resolve = null;
        state.raf = 0;
        drawScene(end, true);
        done(skipped);
    }

    const api = {
        /** Attach to the canvas. Clicking the picture skips the play being shown. */
        init(canvas, overlay) {
            state.overlay = overlay || null;
            state.octx = overlay && overlay.getContext ? overlay.getContext('2d') : null;
            if (state.canvas !== canvas) {
                state.canvas = canvas;
                canvas.width = W * RES;
                canvas.height = H * RES;
                state.ctx = canvas.getContext('2d');
                state.backdrop = null;
                state.parkKey = '';
                if (canvas.addEventListener) canvas.addEventListener('click', () => api.skip());
                if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => redraw()).observe(canvas);
                // Going to another tab ends the play being shown: the browser stops drawing frames for it.
                if (typeof document !== 'undefined' && document.addEventListener && !state.watching) {
                    state.watching = true;
                    document.addEventListener('visibilitychange', () => { if (hidden()) api.skip(); });
                }
            }
            setPark(state.park);
            drawScene(0);
        },

        /** Draw the field as it stands between plays: the script's starting positions and a message. */
        show(scriptJson, park, message) {
            finish(true);
            setPark(park);
            state.prepared = prepare(asObject(scriptJson));
            state.message = message || '';
            drawScene(0);
        },

        /** Draw a script at one moment in time (for checking how a play looks). */
        seek(scriptJson, park, t, message) {
            finish(true);
            setPark(park);
            state.prepared = prepare(asObject(scriptJson));
            state.message = message || '';
            drawScene(t, true);
        },

        /** Play a script. The promise settles when it has finished (true if it was skipped). */
        play(scriptJson, park, speed) {
            finish(true);
            setPark(park);
            state.prepared = prepare(asObject(scriptJson));
            state.message = '';
            const p = state.prepared;
            const rate = speed > 0 ? speed : 1;
            const holds = p.cues.filter(c => c.kind === 'hold')
                .map(c => ({ t: c.t, seconds: Number(c.value) }))
                .filter(h => h.t < p.script.duration && Number.isFinite(h.seconds) && h.seconds > 0);
            const holdSeconds = holds.reduce((sum, h) => sum + h.seconds, 0);
            if (hidden()) {
                // Nobody is watching (a background tab draws no frames): show the result at once.
                drawScene(p.script.duration, true);
                return Promise.resolve(true);
            }
            return new Promise(resolve => {
                state.resolve = resolve;
                state.guard = setTimeout(() => finish(true), (p.script.duration / rate + holdSeconds + config.guardSlack) * 1000);
                let fired = 0;
                const start = now();
                const tick = () => {
                    const t = scriptTime((now() - start) / 1000, rate, holds);
                    while (fired < p.cues.length && p.cues[fired].t <= t) {
                        if (p.cues[fired].kind === 'sound') playSound(p.cues[fired].value);
                        fired++;
                    }
                    if (t >= p.script.duration) { finish(false); return; }
                    drawScene(t, true);
                    state.raf = requestFrame(tick);
                };
                tick();
            });
        },

        /** Jump to the end of the play being shown (quietly). */
        skip() { finish(true); },

        /** Stop for good, for example when the page goes away. */
        stop() { finish(true); },

        get playing() { return !!state.resolve; },

        /** True if the visitor has asked their device for less motion. */
        prefersReducedMotion: calmRequested,

        _internals: { project, sample, prepare, scriptTime, wallDistance, wallHeight, activeBanner, activeEffects, wrapText, facingOf, figureParts, cheering, standing, activeCard, raining, stanceOf, STANCES, surname, textSize, config, CAM, STANDS_RGB, handOf, handScreen, holdingHand, cardLayout },
    };
    return api;
})();
