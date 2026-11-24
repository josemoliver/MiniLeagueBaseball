# Animated Plays: Specification and Implementation Plan

Status: Phases A to F and the atmosphere scenes (section 8) are implemented on `feature/animated-plays`. Written against the code as of commit `443bf97` (1.0.0).

## 1. Goal

The 1984 original showed each play on a field with small animated figures: the ball's path, fielders running to it, throws, runners advancing, slides, steal attempts, and outfield depth. This feature recreates that experience in the Blazor WebAssembly app.

**It is an optional second field view.** The current field view (`FieldView`, referred to below as **Classic**) is preserved exactly as it is and stays available. Animated Plays is a new view, called **Detailed** in the UI. **Detailed is the default** (decided after the first six phases were built, when it had proved itself in a real browser); Classic is one click away. Players switch between the two with a setting, and the choice is remembered.

**Visual target.** Detailed follows the look of the 1984 game (reference screenshots supplied by the project owner): a view from behind home plate looking out over a foreshortened diamond, stands full of speckled crowd along the top, small sprite players, a ball with a shadow on the ground, and a black message box across the top that announces the play. It should feel like that game, but with a much cleaner presentation: a richer palette, larger and better-animated sprites, smooth motion and crisp scaling (see 3.9).

### Principles

0. **Classic is untouched.** Its look, timing, tests and behavior do not change. Detailed is additive. (It was first planned as opt-in, so that a player who never switched saw no difference; it is now the default, and a player who chose Classic keeps it.)
1. **Cosmetic only.** The simulation decides the outcome first. The animation illustrates that outcome and never changes it, so existing probabilities, tests and saved games are unaffected.
2. **Truthful.** Everything shown must agree with the `PlayResult`: who made the out, who scored, who was thrown out, which base each runner ended on, and the outs and runs totals.
3. **Data first.** The engine emits a *play script*, which is plain data and unit-testable. The browser only plays it back. No animation logic lives in the engine beyond choosing what happens.
4. **Skippable and fast.** Animation can be sped up, skipped, or turned off. Computer-vs-computer, simulate-to-end and resume-from-save never block on it.
5. **Static-hosting friendly.** No server, no new asset pipeline. It must work offline in the PWA.

### Non-goals (this spec)

- Removing or rewriting the Classic view.
- Changing how outcomes are rolled, or making the sim physics-based.
- Pitch-by-pitch simulation (balls and strikes). One animation covers one plate appearance or one steal attempt.
- 3D, broadcast cameras, or per-player faces and uniforms beyond team colors.
- Plays the engine does not generate today (see 3.3). They are listed as follow-ups.

## 2. What exists today

Relevant facts from the code, which shape the design:

| Area | Current state |
|---|---|
| `Components/Game/FieldView.razor` | Overhead SVG stadium (viewBox `600x400`, home plate `(300,350)`, bases `(400,250)`, `(300,150)`, `(200,250)`, wall arc radius 675 centered `(300,720)`). Draws fielders as labelled circles at **hard-coded** positions, and plays a single SMIL ball path chosen by outcome type from a fixed table (`HRPath`, `SinglePath`, ...). Runner tokens are derived by diffing base occupants before and after the play (`ComputeRunnerTokens`). Shows a banner such as "DOUBLE". |
| `PlayResult` | Outcome, description, runs, new base state, outs, pitches. It records **no** fielder, except `ErrorBy` and `ErrorPosition` on errors. It records nothing about individual runners (who was out, who was thrown out, who slid). |
| `AtBatResolver` | `DefenseModel.PickFielder` chooses a fielder for ground outs and fly outs, but only to test for an error. The chosen position is then discarded. Hit direction and depth do not exist. |
| `BaseRunningEngine` | Returns only the final base state, with no per-runner movement. A double play is `(null, null, second, ...)`, so which runner was erased is implicit. |
| Steals | `OffensiveStrategy.Steal` resolves only a steal of second from first (`ResolveStolenBase`). It pre-empts the at-bat. |
| Not resolved by the engine | Pickoffs, `WildPitch`, `PassedBall`, `Balk` and `TriplePlay`. Enum values exist, but the resolver never produces them. (Bunts and the hit and run were added later: see 3.3.) `DefensiveAlignment` affects only hit probabilities (singles, doubles and, for GuardLines, triples) and double-play odds. |
| `GameRoom.razor` | `ExecutePlay` is synchronous and returns the result. The page then calls `StateHasChanged`, plays sounds, and (for the computer) waits a fixed `600 ms` or `3 s` (computer vs. computer) before the next turn. Nothing waits for an animation. |
| Sound | `SoundService` plays a sound per outcome right after the play. Its sounds are synthesized by `wwwroot/js/sound.js`, so no audio assets. |
| Ballparks | `Stadium` has wall distances for LF, LC, CF, RC and RF, and wall heights. |
| Fielders | `Player.FieldingAt(pos)` gives Range, ErrorRating and Arm. `Player.SpeedRating` is 1-5. `GameState.DefenderAt(pos)` gives who plays each position. |

The current animation is therefore a good start but cannot show: where fielders actually go, throws, tag plays, slides, thrown-out runners, double plays, steal attempts, or alignment shifts.

## 3. Functional specification

### 3.1 What the player sees

For each plate appearance or steal attempt, in order:

1. **Setup.** Fielders stand at positions that reflect the current `DefensiveAlignment` (see 3.4). Runners stand on their bases. The batter stands in the box (left or right per `BatHand`, with switch hitters following the existing rule in `AtBatResolver`).
2. **Pitch.** The pitcher winds up and the ball travels to the plate. Skipped for walks of the intentional kind (a short toss-out motion instead).
3. **Result sequence.** Depends on the outcome (3.2).
4. **Resolution.** Runners settle on their final bases. Scoring runners cross the plate. Outs are marked briefly (a flash or fade on the retired runner). Then the message box announces the result and the page's base and out indicators update.

Rules:

- The animation is the **only** place the new state is "revealed" first. Scoreboard, bases and outs update at the end of the animation, not before. This avoids spoilers and matches the 1984 feel. (Open question O1.)
- While an animation runs, decision input is disabled. A **Skip** button, or any key (Space or Enter, consistent with existing `Shortcuts`), completes the animation instantly and applies the final state.
- **Field view setting: Classic / Detailed.** Detailed is the default for a visitor who has not chosen (and for a stored value that cannot be read). It is a per-device preference stored in `localStorage` through the existing `storage.js`, wrapped in a small `FieldViewPreferences` service (reads and writes guarded with try/catch, as for the other browser-storage uses). It can be changed at any time, including mid-game and between plays, and takes effect from the next play. A switch in the game toolbar, next to the existing sound toggle, changes it, and it is also offered on the game-setup screen.
- **Speed setting (Detailed only): Fast / Normal / Slow.** There is no "Off": players who want no animation choose Classic. Same storage as above.

### 3.2 Play catalogue

Each row is a *template*. The choreographer fills in names, positions and timings. "Fielder" means the position chosen by the engine or choreographer (3.5).

| Outcome | Sequence |
|---|---|
| **Strikeout** | Pitch, swing (or called strike), catcher holds ball. Batter walks off. |
| **Walk / HBP** | Pitch, batter jogs to 1B. Forced runners advance one base. IBW shows four wide tosses, with the catcher standing. |
| **Single** | Contact, ground ball or line drive to a landing zone in the infield gap or outfield, fielder gathers it, batter reaches 1B. Runners advance per `NewFirst/Second/Third`. A runner scoring from second shows a throw home that arrives late. |
| **Double** | Ball to the gap or down a line, outfielder retrieves it, relay throw to the infield. Batter ends on 2B, possibly with a slide. |
| **Triple** | Ball to the right-center or left-center alley, or the corner. Long retrieval, relay, batter slides into 3B. |
| **Home run** | Ball arcs over the wall at a point consistent with the park's dimensions. Outfielder runs to the wall (when the wall is close). All runners jog around. A burst effect at the landing spot (the existing `hr-burst`). |
| **Fly out** | High arc to an outfielder who settles under it and catches. If the engine scored a tag-up runner (`RunsScored > 0` on a fly out), the runner on third tags and runs, and the throw to the plate is late. |
| **Line out** | Hard line drive caught by an infielder or outfielder. |
| **Pop out** | High pop in the infield, fielder under it, catches. |
| **Ground out** | Ground ball, fielder fields and throws to 1B (or tags the base himself for the first baseman). Batter is out at first. Runners advance one base (existing engine rule). |
| **Double play** | Ground ball, fielder to the second baseman or shortstop covering 2B, then the relay to 1B. The retired runner is the one forced at second. The batter is out at first. |
| **Error** | Same as the ground out or fly out template for `ErrorPosition`, but the fielder misplays it (the ball squirts away, or a throw sails). Batter and runners advance per `NewFirst/Second/Third`. |
| **Stolen base** | Runner breaks on the pitch, catcher throws to 2B, the runner slides, the fielder's tag arrives late, "SAFE". |
| **Caught stealing** | The same, but the tag beats the runner, "OUT". With a pitchout, the pitch is visibly wide and high and the catcher stands to throw. |

Template choice is a pure function of the outcome and the facts in 4.2, so it is deterministic given a script seed.

### 3.3 Out of scope until the engine produces them

The sim does not generate these, so there is nothing to illustrate. They become a follow-up whenever the engine models them:

- Pickoffs, wild pitches, passed balls, balks and triple plays.
- Surprise bunts with a runner on third (a squeeze that fails), and bunt double plays.
- Steals of third or home, and double steals.
- Rundowns, and runners thrown out stretching a hit.

**Added since: bunts and the hit and run.** The engine now resolves both (`BuntModel`, `AtBatResolver.ResolveBunt`, `AtBatResolver.IsHitAndRun`), so they are staged (`Staging.Bunts.cs`):

- **Bunt.** `PlayResult.IsBunt` marks it and the outcome says how it ended. With a runner on and fewer than two out the call is a sacrifice: `SacrificeBunt` (batter out at first, runners up one, a runner on third scores), `FieldersChoice` (the lead runner is out at the next base and the batter is safe), or a pop-up, a bunt single (`Single`), or an error. With the bases empty or two out it is a bunt for a hit (`GroundOut`, `Single`, `PopOut` or `Error`). The batter squares around at the release (`Poses.Bunt`, the bat held level; banner "BUNT!"), the ball dies in front of the plate and rolls slowly (`BuntLanding`, `RollArrival`), and the pitcher, catcher or a corner man charges and throws. Corners In is the defense for it (fewer bunt hits, more lead runners).
- **Hit and run.** `PlayResult.HitAndRun` marks it. It is on only with a runner on first and second open. The runner breaks at the release and is part way to second when the ball reaches the plate (`RunnerBreaks`; banner "HIT AND RUN!"). The batter strikes out less, a grounder is rarely a double play, and the runner takes the extra base more often. The risks are staged too: a strikeout that is also a runner thrown out at second (the catcher's throw, as on a steal), and a line drive that doubles the runner off first (he turns back and is thrown out, `DoubleOffAtFirst`). A pitchout turns the call into a steal attempt. A runner who has to come back (a fly ball) is returned to his base at the end.

The animation system must degrade gracefully for any `OutcomeType` without a template: show the pitch, the static result, and the existing banner.

### 3.4 Defensive alignment and positioning

Fielders start at base positions defined for a standard alignment, then offset by `DefensiveAlignment`:

| Alignment | Visual change |
|---|---|
| Standard | Default positions. |
| CornersIn | 1B and 3B on the grass in front of the bag. |
| InfieldIn | All four infielders cut in toward the plate. |
| GuardLines | 1B and 3B hug the lines, and LF and RF edge toward the foul poles. |
| ShiftPull | Infielders and outfielders slide toward the pull side for the **current batter** (right-handed hitters pull to left, left-handed to right). |
| ShiftOpposite | The reverse. |

Outfield depth also varies by the batter's power, but only cosmetically. For example, a high home run rate puts the outfielders a few pixels deeper, and a slap hitter's outfielders play in. This is optional polish (Phase E).

Positions are data in one table (`FieldGeometry`, see 4.4) so they are easy to tune.

### 3.5 Picking the fielder and the landing spot

The animation needs to invent details that agree with the outcome. Rules:

- **Outs on balls in play.** The fielder is the position the engine already picks with `DefenseModel.PickFielder`. This spec moves that decision from "thrown away after the error check" to "recorded in the result", so the sim and the animation agree and box-score fielding credit can later use it.
- **Hits.** The landing zone is chosen from the hit type and the batter's handedness and pull tendency, within the park's dimensions:
  - Single: infield hole or shallow-to-medium outfield.
  - Double: gap (left-center or right-center) or down a line.
  - Triple: deep alley or corner.
  - Home run: lands beyond the wall. The distance is at least the wall distance at that angle (`Stadium` has five reference points, interpolated by angle). Tiny parks produce a visibly shorter drive.
- **Fielder for a hit** is the fielder nearest the landing zone, after alignment offsets.
- **Direction randomness** comes from a seeded `Random` owned by the choreographer, never the sim's RNG (see 4.3).

### 3.6 Timing

All durations are expressed in **ticks of the script clock** and scaled by the speed setting. Defaults at Normal speed:

| Phase | Approx. duration |
|---|---|
| Wind-up and pitch | 0.6 s |
| Ball in play (flight or roll) | 0.5 to 1.4 s depending on distance and type |
| Fielder move, throw, relay | 0.4 to 1.0 s per leg |
| Runner running | 0.45 s per base, scaled by `SpeedRating` (faster runners 15% quicker) |
| Slide | 0.25 s |
| Settle and banner | 0.5 s |

Total at Normal: about 2 to 5 s per play. Fast is half of that. Computer-vs-computer play should use Fast by default.

Runner and throw timing must make the story consistent. A runner is *safe* when his arrival time is earlier than the ball's, and *out* otherwise. Since the result is already decided, the choreographer **solves for the throw** (its speed, and when the fielder releases it) to produce that arrival order. It does not simulate it.

### 3.7 Sound sync

`SoundService.PlayForResultAsync` currently fires at play start. With animation it should fire at the matching *event* in the script, for example the bat crack on contact, the "out" sound when the ball reaches the base, and the crowd cheer when a run scores. Script events carry a `Sound` cue name. The existing sound names (`hit`, `homerun`, `out`, `steal`, `caught`, `cheer`, `error`, `doubleplay`, `walk`, `strikeout`) are reused. In Classic, the current behavior is unchanged: the sound fires when the play is applied.

### 3.8 Accessibility and preferences

- Honors `prefers-reduced-motion`: the view stays Detailed, but it starts at Fast (unless a speed was chosen) and drops the crowd sparkle, the ball trail and the growing burst.
- A text description (the existing `PlayResult.Description` and the play-by-play log) is always updated, and an `aria-live` region announces it, so the animation is never the only source of information.
- Colorblind-safe: out and safe markers use text and shape as well as color.
- Mute and sound preferences are unchanged.

### 3.9 Look and feel: the classic behind-home view

What the 1984 screenshots show, and how Detailed treats each element:

| 1984 element | Detailed |
|---|---|
| **Camera behind and above home plate**, looking toward center field. The diamond is foreshortened: home plate is wide and near at the bottom, second base is small and far, and the foul lines fan out. | The same perspective, produced by a simple projection of the ground plane (4.4). It is **not** the overhead view that Classic uses. |
| **Stands** across the top: a band of speckled, multicolored crowd above a red or pink wall and warning track. | A dithered crowd band in a richer palette, with a few animated "flicker" patches (a cheer when a run scores). The wall follows the park's real dimensions, so short porches and deep alleys are visible. |
| **Billboard on the outfield wall.** | A sign with the park's name (shrunk to fit if it is long) above the small words "MINILEAGUE BASEBALL", drawn as text; the app artwork is a tall poster that does not suit a wide sign. The original game's name and logo are not used. |
| **Infield dirt arc and basepaths** (tan), the grass circle around the mound, white bases, batter's boxes and the plate at the bottom. | Same layout, with mowing stripes (dithered), a shaded mound, chalk lines, and base and plate sprites sized by depth. |
| **Small humanoid sprites**: fielders in a ready stance, a batter, a pitcher, runners. | Larger, better-animated pixel sprites (4.6). Fielders face the batter, and runners face their direction of travel. Sprites are drawn back to front and scale with depth. |
| **The ball with a shadow** on the ground, so fly-ball height is readable. | Kept. The ball is drawn at its projected height, and a shadow stays on the ground below it. The shadow also shows where a fly ball will land before it comes down, which is how the player reads the play. |
| **Message box** across the top: "TOP OF INNING 1", "A CLEAN SINGLE.". | A black box with monospace pixel text, in the same place. It shows inning changes, who is up, and the result line from `PlayResult.Description`. This replaces Classic's banner **in Detailed only**. The text is also exposed to screen readers (3.8). |
| Chunky pixels, a limited palette, and dithering. | A fixed palette of about 32 colors, nearest-neighbor scaling, and dither patterns for grass, dirt and crowd. Unlike the original, motion is smooth, colors are richer and the picture is sharp at any window size. |

Camera notes:

- The camera is fixed during a play. Nothing pans or zooms.
- A very high fly ball or pop-up may rise above the top of the screen. The shadow stays on the field, so the player still sees where it will land. Tuning the camera height to keep most pops visible is a Phase E item.
- Aspect ratio is 16:10, the same shape as the original's 320x200 screen. `GameRoom`'s `g-field` container needs to accept this aspect (Classic's is 3:2), which is a small CSS change scoped to Detailed.

## 4. Technical design

### 4.1 Architecture

```
GameService.ExecutePlay ──► PlayResult (+ PlayFacts)
                                │
                  PlayChoreographer (Engine, pure C#)
                  inputs: pre-play snapshot + PlayResult + seed
                                │
                          PlayScript (data)
                                │  JSON via JS interop
                                ▼
                 playAnimator.js  (browser: pixel canvas)
                    plays script, raises sound cues, Skip,
                    returns a promise when finished
                                │
                                ▼
                  GameRoom: applies final UI state,
                  then continues (save, CPU turn)
```

**Two views, one game.** `GameRoom` hosts one field view at a time, chosen by the preference. Both read the same `PlayResult` and game state. Classic keeps using its current code path (`FieldView`). Detailed uses the new `DetailedFieldView`. Only the Detailed path awaits an animation.

### 4.2 Engine changes: record the facts the sim already computes

Add to `PlayResult` (additive, so nothing existing breaks):

```csharp
public Position? Fielder { get; set; }          // who handled the ball (outs, errors); null for K/BB/steals
public List<RunnerMove> RunnerMoves { get; set; } = [];   // every baserunner, incl. the batter

public record RunnerMove(Player Player, int FromBase, int ToBase, RunnerFate Fate);
// FromBase: 0 = batter's box, 1-3 = bases. ToBase: 1-3, or 4 = home (scored).
// For an out, ToBase is the base he was retired at (or the base he was heading for).
public enum RunnerFate { Safe, Scored, StayedPut, ForcedOut, TaggedOut, Retired }
// Retired = out with no base play (struck out, or a fly/line/pop caught). A batter retired this way has ToBase 0.
```

How to populate:

- `AtBatResolver.ResolveCore` keeps the `PickFielder` result instead of discarding it. The fielder is recorded for ground outs, fly outs, double plays and errors. The sim never produces line outs or pop outs today (`Roll` has no such outcome), so they get no fielder pick until the engine models them.
- **As built**, the moves are derived after the play by `RunnerMoveBuilder.Build` from the pre-play bases and the result, using no randomness. `BaseRunningEngine`'s signatures are unchanged, so Classic and the existing tests are untouched. A double play marks the runner on first `ForcedOut` at 2B and the batter `ForcedOut` at 1B. Runners left on base when the third out ends the inning are `StayedPut`.
- `ResolveStolenBase` records a single move (1 to 2, `Safe` or `TaggedOut`).
- **Invariant, enforced by a test:** applying `RunnerMoves` to the pre-play base state yields exactly `NewFirst/NewSecond/NewThird`, with `RunsScored` equal to the count of `Scored`, and `OutsRecorded` equal to the count of `*Out`. If this ever drifts, the animation would lie. This test is the main guard for the whole feature.

Detailed uses these moves. Classic keeps its own `ComputeRunnerTokens` diffing unchanged (it cannot tell who was retired on a double play or which runner scored on a fly out, which is acceptable for the simpler view). The new fields are additive, so Classic is unaffected.

`BoxScoreRecorder` is not changed in this feature. A later feature could use `Fielder` for putouts and assists.

### 4.3 The play script

New namespace `MiniLeague.Engine.Animation`, in the existing Engine project.

```csharp
public record PlayScript(int Version, string Outcome, double Duration,
                         IReadOnlyList<Actor> Actors, IReadOnlyList<Track> Tracks, IReadOnlyList<Cue> Cues);
```

A script is a set of **tracks**, one per moving thing, each a list of timed **keyframes** in **ground-plane feet** (4.4): `X` toward right field, `Y` toward center field, `Z` height above the ground. The script carries no pixel or screen coordinates, so the renderer is free to project it however it likes.

```csharp
public record Actor(string Id, ActorKind Kind, string? Label, string? Team /* home|visitor, null for the ball */);
// Kinds: Pitcher, Catcher, Batter, Runner, Fielder, Ball. Ids: pitcher, catcher, batter, ball,
// fielder-<Position>, runner-<playerId>. The batter keeps the id "batter" while he runs.

public record Track(string ActorId, IReadOnlyList<Key> Keys);

// Ease, Apex and Facing describe the move INTO this key. Pose null keeps the previous pose.
// Apex is the peak height of an Arc segment, in feet.
public record Key(double T, double X, double Y, double Z, string? Pose = null,
                  Ease Ease = Ease.Linear, double Apex = 0, string? Facing = null);
// Poses (string constants in Poses): stand, windup, throw, swing, bunt, run, slide, catch, dive, tag, safe, out, hit

// Kind is sound | banner | effect. Effects (such as the home run burst) carry a position.
public record Cue(double T, string Kind, string Value, double? X = null, double? Y = null);
```

Notes:

- Positions between keys are linearly interpolated, except the ball, which uses an arc (parabolic height `Z`) for fly balls and a decelerating roll for grounders. The interpolation kind is a key attribute (`Ease`: Linear, Arc, Roll), and an Arc segment carries its peak height as `Apex`.
- Scripts are **JSON-serializable and versioned**. They are never persisted in saves: the choreographer regenerates one on demand, and a resumed game starts with no animation.
- The choreographer takes `(PreState, PlayResult, int seed)` and is **pure**. It uses its own `Random(seed)`, never `AtBatResolver`'s RNG, because drawing from the sim RNG would change the game and break seeded tests. `GameRoom` supplies the seed (for example a play counter, so replays are repeatable).
- `PreState` is a small snapshot taken before `ExecutePlay`: base occupants, defensive alignment, batter handedness, the fielders at each position, and the `Stadium`. `GameService` already has these. **As built:** `PrePlaySnapshot.Capture(state)` builds it, and `GameService.LastPrePlay` holds the snapshot for the latest play (taken after substitutions, just before the play is resolved), so the UI does not need to snapshot by hand. It is cleared when a game starts or resumes.

### 4.4 Field geometry and the camera

**Coordinates.** The engine works in real feet on the ground plane, with home plate at `(0,0)`, `X` positive toward right field, `Y` positive toward center field, and `Z` as height. This is natural for baseball and removes the old pixel-scale conversion:

- Bases are 90 ft apart: first `(63.6, 63.6)`, second `(0, 127.3)`, third `(-63.6, 63.6)`. The mound is at `(0, 60.5)`.
- The foul lines run at 45 degrees. The wall is built from the five distances in `Stadium` (left, left-center, center, right-center, right), interpolated by angle, so each park has its real shape and a home run is a ball that lands beyond the wall at its angle.
- Default fielder spots and the per-alignment offsets from 3.4 are stored in feet in `FieldGeometry` (Engine, static data), which also provides the wall-at-angle helper.

**Camera and projection** (renderer only, `playAnimator.js`). A simple pinhole model, with the camera at height `H` and distance `B` behind the plate, looking toward `+Y`:

```
depth = Y + B
screenX = centerX + f * X / depth
screenY = horizonY + f * (H - Z) / depth        // Z = 0 is the ground
spriteScale = f / depth                          // nearer = bigger
```

`H`, `B`, `f` and `horizonY` are tuned once to match the proportions of the reference screenshots (home plate low and wide, second base small and high, outfield wall near the top). A ground point and its height are projected separately, which is how the ball's shadow (`Z = 0`) and the ball itself (`Z = h`) are placed. Anything near the plate is drawn larger than anything in the outfield, which gives the depth the original had.

Because the projection lives only in the renderer, changing the camera (a flatter or higher view) needs no engine or script change.

**Tests.** The geometry helpers are checked in xUnit: bases are 90 ft apart and form a right angle at the plate, the wall point at an angle lies on the interpolation of the park's distances, and the home run landing is at or beyond that point. Classic's `FieldView` is not involved.

### 4.5 Rendering: a pixel canvas, separate from Classic

Why not reuse `FieldView`'s SVG: it is an overhead layout with a different projection, so it cannot be shared. SMIL is also declarative and one-shot, which cannot be sequenced across many actors, skipped, sped up or finished on demand. A timeline player needs a clock.

Chosen approach:

- **Detailed has its own backdrop.** `FieldView` is not modified at all (no extraction, no shared markup), which is the safest way to keep Classic untouched. The earlier `FieldBackdrop` idea is dropped.
- `DetailedFieldView.razor` hosts a single `<canvas>` with a fixed **logical resolution of 640x400** (16:10). All drawing happens at that size. CSS scales the canvas up with `image-rendering: pixelated`, so edges stay crisp and chunky at any window size and the layout needs no resize handling beyond the CSS box.
- **The backdrop is pre-rendered once** per park into an offscreen canvas: sky/stands, crowd dither, wall to scale from `Stadium` (in the park's `wallColor` when it has one), the foul poles, grass with mowing stripes, infield dirt with its base paths and the grass diamond, chalk lines, on-deck circles and coaches' boxes, the mound and rubber, bases and plate, a bullpen along each foul line and a dugout at each side of home plate, and the billboard. Each frame blits it, then draws the dynamic layer on top (sprites sorted back to front, ball shadow, ball, effects, then the message box). That keeps frames cheap.
- `wwwroot/js/playAnimator.js` exposes `init(canvasId, parkJson)`, `start(scriptJson, speed, dotNetRef)` (returns a promise resolved when the script ends or is skipped), `skip()` and `setSpeed()`. It uses `requestAnimationFrame` with a script clock and stops drawing when idle. Sound cues call `window.mlbSound` directly or call back into `SoundService`.
- A thin component `PlayAnimator.razor` owns the canvas and the interop and raises `OnCompleted`. `DetailedFieldView` hosts it.

The backdrop and projection code is plain JavaScript, so it can be tested with the existing `tests/js` setup (for example, projected base positions land in the expected screen regions, and wall points follow park dimensions).

### 4.6 Sprites and art

Goal: the 1984 look, improved. Chunky pixel sprites, but larger and with more animation than the original, drawn in a consistent palette.

- **Size and detail.** Sprites are about 20x30 logical pixels at the plate and scale down with depth (a fielder in deep center is about 10x15). Smaller sizes are generated from the same art by nearest-neighbor scaling, or from two or three hand-authored scale steps if that looks better.
- **Authoring.** Each pose is a small text grid of palette letters, kept in source files (`wwwroot/sprites/` or inside `playAnimator.js`), and compiled to offscreen canvases at startup. No separate art tooling, no downloads of image sets, and nothing licensed. If hand-drawn sheets are preferred later, the pose names are the stable interface (see O4).
- **Team colors by palette swap.** Two marker colors in the grid (jersey and cap) are replaced at load time with the home or visitor colors, so every team gets correct uniforms from the same art. Home and visitor use different colors (the app already has team colors in `AppTheme`; if a team lacks one, use home white and visitor gray with an accent).
- **Poses and facings.** Stand/ready, run (4 frames), windup, pitch release, swing, catch, throw, slide, dive, tag, plus safe and out signals for the umpire or the runner. Facings: toward the camera, away from the camera and sideways (mirrored for left and right), which covers all directions of travel in a behind-home view.
- **Ball and shadow.** A small white ball with a dark outline, drawn at its projected height. Its shadow is a dark flattened oval on the ground at the same `X, Y`, shrinking and darkening as the ball rises, as in the original. A short fading trail is optional.
- **Effects.** Dust puff on slides, a small burst on a home run (the Classic game already uses one), and a crowd flicker on runs. All effects are simple sprites or particles on the same canvas.
- **Fonts.** The message box uses a monospace system font at integer sizes (a bundled pixel font was left out to avoid another file to cache).
- **Dither.** Grass stripes, dirt and the crowd use ordered dithering with a fixed 4x4 pattern, which is cheap and gives the same "textured" feel as the original's CGA output.
- Optional name tag over the active fielder or runner, only for the actors involved, to keep the screen clean.

### 4.7 Integration with `GameRoom.razor`

`GameRoom` picks the field component from the preference, `FieldView` or `DetailedFieldView`, passing the same parameters. In Classic, `HandleDecision`, the CPU turn, the sounds and the fixed `600 ms` and `3 s` pauses behave exactly as today.

When Detailed is selected, the sequence is:

1. `ExecutePlay` (sync). Take `PlayResult` and the `LastPrePlay` snapshot.
2. Build the script (`PlayChoreographer.Build`). If building the script fails, skip to step 4 (the play is still shown by the banner and the final state).
3. `await` the animator promise. During it: sound cues fire, decision input is disabled, and the scoreboard shows pre-play values.
4. Apply final UI state (`StateHasChanged`), then the existing continuation: `PlayResultSounds` (skipped for plays whose sounds already played as cues), `OfferInjuryReplacement`, `SaveGame`, `ScheduleComputerTurnIfNeeded`.

Details:

- **State to display during the animation.** `GameState` has already been mutated by `ExecutePlay`. The page keeps a `DisplayState` projection (bases, outs, score) that it holds at the pre-play values until the animation completes. This is the only awkward part. Options are in O1. The default is to reveal everything at the end.
- **Saves** move to *before* the animation. Today `SaveGame` runs after the sounds. If the save stayed after a multi-second animation, closing the tab mid-play would lose a play that has already been applied. Order: execute, save, animate, continue.
- **CPU pacing.** The fixed `600 ms` and `3 s` pauses are replaced by "animation duration plus a small gap". In Classic, the current constants apply unchanged.
- **Cancellation.** `Dispose` and the Skip action cancel the animator. The existing `_cpuCts` pattern is extended.
- **Resume.** A resumed game shows the field with no animation.
- **bUnit tests.** JS interop is mocked (the app's tests already use `SoundService` fakes). The default view is now Detailed, so the existing `GameRoomTests` store a Classic choice in their set-up and pass unchanged (they cover the game with the Classic field); `DetailedViewTests` cover Detailed and the default.

### 4.8 Performance

- A script is a few KB of JSON and is built in microseconds. No allocations per frame in the player beyond the draw.
- One canvas, redrawn per frame only while an animation is active, so idle cost is zero (important for battery on phones and for the PWA).
- The canvas, `playAnimator.js` and the choreographer are used only in Detailed. **As built**, `playAnimator.js` is a plain script tag loaded with the page like the other scripts (about 20 KB, so on-demand loading was not worth the extra moving part). Nothing in it runs until the Detailed view asks.

## 5. Testing strategy

**Engine (xUnit), the bulk of the testing:**

- `RunnerMoves` invariant (4.2) for every outcome type, across a large number of seeded random games (reuse the style in `GameFlowTests`): the moves reconcile with base state, runs and outs.
- Choreographer determinism: the same `(PreState, PlayResult, seed)` yields an identical script.
- Truthfulness: for each outcome, the script ends with the actor positions at the final bases, contains a `Scored` crossing of the plate for each run, and a retired runner for each out. For a ground out, the ball reaches 1B no later than the batter, and for a safe call the reverse.
- Geometry: bases are 90 ft apart with a right angle at the plate. The wall point at an angle follows the park's distances. Home run landing is at or beyond the wall point. Fielder choice matches `PickFielder` and the recorded `Fielder`.
- Alignment offsets: each alignment moves the expected fielders.
- Regression: with fixed seeds, existing sim outputs are unchanged. The choreographer must not consume the sim RNG.

**JS (existing `tests/js` harness):**

- `playAnimator` timeline: interpolation, arc and roll easing, `skip()` resolves immediately at the final frame, speed scaling, cue firing order.
- Projection: home plate projects near the bottom center, second base above it and smaller, a nearer sprite is larger than a farther one, and the ball's shadow stays on the ground while the ball rises.
- Palette swap produces the requested jersey colors.

**UI (bUnit):**

- Classic regression: `FieldView` is unchanged. With a Classic choice stored, `GameRoom` renders `FieldView` and no canvas. With nothing stored it renders the animated field.
- The preference service round-trips, tolerates unavailable storage, and switching views mid-game does not lose state.
- `GameRoom` with animation On and a fake animator: the final state is applied only after completion, Skip completes it, the CPU turn waits for completion, and Classic bypasses it entirely and behaves as before.

**Manual / browser check** (see the project's real-Chrome testing notes): each play type at Normal speed, on desktop and a phone-width viewport, in a dome and a small park, and offline in the PWA.

## 6. Implementation plan

Each phase is shippable on its own and keeps `dotnet build` clean with all tests passing.

### Phase A: Engine facts (no visible change) — done

1. Add `Fielder`, `RunnerMoves`, `RunnerMove`, `RunnerFate` to `PlayResult`.
2. Return per-runner moves from `BaseRunningEngine` and `AtBatResolver`. Record the picked fielder, adding fielder picks for line outs and pop outs.
3. Add `GameService.LastPrePlay` snapshot.
4. Tests: the `RunnerMoves` invariant across all outcomes, and fielder recorded.

*Exit:* the sim output is unchanged for fixed seeds and the invariant test passes.

### Phase B: Script model and geometry: done

1. Add `FieldGeometry`, with alignment offsets and the wall-point helper from `Stadium`.
2. Add the `PlayScript` types and JSON serialization (System.Text.Json, camel case, versioned).
3. Build `PlayChoreographer` with the simple plays first: strikeout, walk, home run, single.
4. Tests: determinism, geometry, truthfulness for those plays.

**As built** (`src/MiniLeague.Engine/Animation/`): `FieldPoint` and `FieldGeometry` (feet; bases, mound, wall points and heights from `Stadium`, fielder spots per alignment and batter hand, kept fair and inside the wall), the script types and `PlayScriptJson`, an internal `ScriptBuilder` that keeps tracks in time order, and `PlayChoreographer.Build(PrePlaySnapshot, PlayResult, seed)`. It has templates for strikeout, walk, hit by pitch, intentional walk, home run and single. Every other outcome gets a basic script (the pitch, each runner's move with out or safe at the end, and the result line) until Phase D adds its template. A throw is solved so it arrives after a safe runner, and only throws that must arrive late exist so far; throws that must beat a runner (outs) come in Phase D.

### Phase C: Player and a first visible animation: done

1. `playAnimator.js` with the timeline engine, canvas scaling, placeholder sprites (simple colored figures with the ball shadow, to be replaced in Phase E), skip and speed, and a promise on completion.
2. Pre-rendered backdrop from park data: stands and crowd, wall to scale, logo billboard, grass, dirt, chalk, bases. Projection with parameters tuned against the reference screenshots. Message box.
3. `DetailedFieldView.razor` (16:10 canvas) hosting `PlayAnimator.razor`. `FieldViewPreferences` service and the Classic / Detailed switch (toolbar and setup screen), the Fast / Normal / Slow speed setting, and a Skip button.
4. Wire `GameRoom`, Detailed only: execute, save, animate, continue. Defer the state reveal. CPU pacing from the animation duration.
5. JS tests for the timeline, and bUnit tests with a fake animator.

**As built:**

- `wwwroot/js/playAnimator.js` (`window.mlbAnimator`): `init`, `show` (the field between plays), `play` (returns a promise, true if skipped), `skip`, `stop`, and `seek` (draw one moment, for checking how a play looks). Camera `B=220, f=600, hy=-94.6`, tuned against the 1984 screenshots: home plate low, second base mid-screen, wall near the top. The backdrop is pre-rendered per park (stands with crowd, wall to scale, warning track, striped grass, infield dirt, base paths, chalk, mound, bases, a text billboard). Figures are simple team-colored sprites (placeholders until Phase E) drawn far to near, with a ball shadow. The message box is a black box with white monospace text. Clicking the picture skips. A failing sound never stops a play.
- `DetailedFieldView.razor` hosts the canvas, builds the between-plays picture from `PlayChoreographer.BuildSetup(PrePlaySnapshot.Capture(game))` (new in this phase: a zero-length script with the cast in place), and exposes `PlayAsync(script, speed)` and a Skip button. There is no separate `PlayAnimator.razor`: it was not needed.
- `FieldViewPreferences` (`Classic`/`Detailed`, `Fast`/`Normal`/`Slow`) is stored in browser storage under `mlb-fieldview` and `mlb-animspeed`.
- `GameRoom` chooses `DetailedFieldView` (default) or `FieldView`. A switch sits next to Box score and Recap during a game (with the speed buttons in Detailed), and a "Field view" select is on the setup screen. **Holding the old situation:** while a play is shown, `GameRoom.ShouldRender()` returns false, so the scoreboard, bases and last-play card keep the situation from before the play (the game state has already moved on), and one redraw follows when the play ends. The play is saved before the animation, a second decision during it is ignored, and the computer's gap between plays is 400 ms instead of 0.6 s or 3 s. A play whose script has sound cues plays them itself; otherwise the usual sound plays when it starts. In Classic none of this runs.
- Tests: JS (`tests/js/animator.test.js`, added to the CI workflows), bUnit (`DetailedViewTests.cs`, using bUnit's pending-JS calls instead of a fake animator), engine (`SetupScriptTests`, and the JSON field names the player reads).
- Checked by eye in headless Chrome against the 1984 screenshots (setup, single, home run). Not yet checked: a full run in the real app in a browser, phone width, and a dome or very small park.

*Exit:* with Detailed selected, the four simple plays animate end to end. With Classic selected, the game behaves and renders exactly as before.

### Phase D: The full play catalogue: done

1. Add templates: fly, line and pop outs, ground out, double play, double, triple, error, stolen base, caught stealing.
2. Throw-solving, so safe and out calls come out consistent with the recorded result.
3. Tag-up on fly outs, relay throws on extra-base hits, runners thrown out at the plate.
4. Tests for each template.

**As built** (`Staging.Hits.cs`, `Staging.Outs.cs`, `Staging.Steals.cs`, with the shared pieces in `PlayChoreographer.cs`): templates for double, triple, ground out, double play, fly out (with tag-up on a sacrifice fly), line out, pop out, error and stolen base / caught stealing. Every outcome the simulation produces now has its own template; only outcomes it never produces (wild pitch, passed ball, balk, triple play) get the basic script.

- **Making the picture agree with the result.** A throw that must *beat* a runner (an out) is thrown at fastest arcade speed and the runner is slowed up to 2.2 times, then held back at his base a moment if that is still not enough. A throw that must arrive *after* a runner (he is safe) is thrown slower and, if need be, held. A runner who is out, or who is safe on a close play, slides.
- **Double and triple:** the ball goes to a gap or a line (the corner outfielders only for the lines), rolls to the wall, and the outfielder runs it down within about 95 feet. It is relayed through the shortstop (left side) or second baseman (right side) to home, or to the base the lead runner took. The batter slides into the base he takes.
- **Ground out:** fielded where the result says (any of the six ground-ball fielders), thrown to first, or the first baseman steps on the bag himself. **Double play:** to second (the other middle infielder takes it), then to first; both runners are beaten by the ball and the forced runner slides.
- **Fly, line and pop out:** the fielder gets under it and the ball comes down when he can be there. On a sacrifice fly the runner leaves third only after the catch and the throw home arrives late.
- **Error:** the fielder dives, the ball gets away and he chases it down; his throw arrives after the runner.
- **Steal:** the runner breaks as the pitcher delivers, the catcher throws to second, and a middle infielder covers and tags.
- Line outs and pop outs have templates and tests, but the simulation does not produce them yet. Runners thrown out at the plate are not produced either.
- **Durations at Normal speed (median / 95th percentile, over about 150 plays of each):** walk 1.9 s, strikeout 2.2, ground out 2.6 / 2.8, double play 2.9 / 3.1, steal 2.1, single 4.1 / 4.5, fly out 4.3 / 4.4, home run 3.8 / 4.4, error 3.1 / 6.0, double 5.5 / 6.1, triple 5.8 / 6.3. At Fast, halve them. Doubles and triples run a little over the 2 to 5 second goal because of the relay.
- Tests (`PlayTemplateTests.cs`) check over 60 simulated games that: every ball in play ends in someone's hands; wherever a throw goes to a base, an out runner is beaten by it and a safe runner beats it (over 300 outs and 100 safe plays); no runner is slowed to an absurd pace and no play runs long; and each template's details (who fields it, the relay, the slide, the tag-up, the dive, the cover man and the tag).
### Phase E: Sprites, polish and sound sync: done

1. Replace the placeholders with the full pixel sprite set and poses (run, throw, swing, catch, slide, dive), team palette swap, and dither.
2. Ball shadow and arc, optional trail, dust puffs and HR burst, crowd flicker, out and safe signals, and camera tuning so pop-ups stay mostly visible.
3. Sound cues from script events. Remove the duplicate start-of-play sound when animation is on.
4. Outfield depth tweaks by batter power, name tags for active actors, `prefers-reduced-motion`, and the `aria-live` announcement.

**As built:**

- **Sprites.** Not text grids after all: each figure is built from a small set of rectangles (a "rig" in `figureParts`) in a 16-unit-tall space, for three views (front, back, side, mirrored for the left) and every pose in the script (stand, run with a stride that changes frame to frame, windup, throw, swing with the bat sweeping through, catch, dive, tag, safe, out, slide, hit). Every edge is snapped to whole pixels and each piece gets a one-pixel outline, so a figure stays crisp and readable at any size from the plate to the wall, which a fixed grid scaled by nearest-neighbor would not. Uniforms have a shade on one side and come from the team's two palettes (home white with a red cap, visitors blue-gray with a navy cap). The catcher crouches, and wears a mask when facing the camera and leg pads. The batter and runners wear a helmet. Which way a figure faces comes from the way he is moving, or from his job when still (catcher away from us, batter toward the plate).
- **Ball.** A shadow that shrinks and fades as the ball rises, a short fading trail while it moves, and a small arrow at the top edge when it is too high to see (its shadow still shows where it will land).
- **Effects.** Dust behind a sliding runner, the home run burst, and the crowd sparkling for 1.4 seconds after a cheer or a home run. A flag reading OUT or SAFE appears over a runner for about a second after he is called. A name tag (surname) appears over up to three people who are handling the ball (catching, throwing, tagging, diving, sliding or swinging).
- **Dither.** The grass, the stripe edges, the dirt, the warning track and the infield grass use 4x4 Bayer dither patterns.
- **Reduced motion.** With `prefers-reduced-motion`, the crowd does not sparkle, the ball has no trail and the burst does not grow. `FieldViewPreferences` also starts the speed at Fast when the device asks for less motion and the visitor has not chosen a speed.
- **Outfield depth.** For show only: `FieldGeometry.OutfieldDepthFor` plays the outfield up to 12 feet deeper for a power hitter (isolated power well above .150) and up to 10 feet shallower for a slap hitter, never outside the wall. It applies to the plays and to the between-plays picture.
- **Announcements.** The screen-reader region says who is up (until the first play) and then the result of each play.
- **Sound.** Done in Phase D: every template carries its own sound cues, so the page adds no start-of-play sound.
- **Not done, and why:** a pixel web font (the message box keeps a monospace system font, which avoids another file to cache); an umpire figure; per-park camera changes. Pop-ups were handled with the arrow instead of changing the camera.

### Phase F: Hardening: done

1. Real-Chrome pass across play types, parks and viewports. Offline PWA check, and add `playAnimator.js` to the service worker's asset list.
2. Performance check on a low-end phone.
3. Update `README`/docs. Classic's SMIL ball paths and runner token diffing stay as they are.

**As done.** The published app (`dotnet publish`, served like the static web app) was driven in real Chrome through the DevTools protocol, with the computer playing both sides and with a human at the controls. Results:

- **Performance.** A steady 60 frames per second while a play runs. With the CPU slowed 6 times (to stand in for a low-end phone), 0.3% of frames were late (over 34 ms), the browser was busy about 60% of the time (about 10% at full speed), and plays took as long in real time as their scripts say. Drawing one frame costs about 0.1 ms (the backdrop is drawn once per park). The JavaScript heap stayed flat at about 5.5 MB over minutes of play. Headless Chrome draws with the CPU, so a real device with a graphics chip should do better.
- **Real app, no errors.** No console errors, exceptions or failed requests in any scenario.
- **Human flow.** Eight plays with a human: a pitch is shown, a click on Skip ends it in about half a second, the page then shows the new situation and the decision panel returns, and a second click during a play is ignored.
- **Offline.** After one visit the service worker cached 93 files (including `js/playAnimator.js`), and with the network off the setup screen loads and the Detailed view plays.
- **Phone (390 wide, 3x).** No sideways scroll and the field fits the width. Found and fixed: text was drawn at 7 px and 4 px on screen because the 640-wide picture is shown at about 56% size, so the message box and tags are now sized from the displayed width (readable on a phone, unchanged on a laptop), and the Skip button is 44 px tall on touch screens (it was 29 px). The picture also redraws when it is resized or a phone turned.
- **Odd parks.** Fenway Park (the smallest, with the 37 foot left-field wall) and Tiger Stadium (the deepest) draw their real shapes, walls and distances. There is no dome in the park data, so no roof is drawn.
- **Found and fixed: a hidden tab.** A background tab draws no frames, so a play never finished and the game stalled. A play now finishes at once (quietly) when the tab is hidden or becomes hidden, and a watchdog finishes any play that is 3 seconds overdue, so a game can never hang on an animation. With a tab hidden for 15 seconds, 34 plays went by instead of none.
- **Found by hand, then fixed: sparkle spilling onto the field.** The crowd sparkle picked seats from the stands' geometry, but the field, wall and sign are drawn over part of that area, so about 40% of the sparkle landed on the grass and dirt. The backdrop is now read back once per park and only seats where the stands' own colour shows are used (and if it cannot be read there is no sparkle). Checked in real Chrome for all 15 ballparks by comparing a frame with and without a cheer: 56 to 78 sparkle pixels per park on the field before, none after. This is covered by JS tests that give the fake canvas different pixels.
- **Found by eye, then fixed: jagged text.** The picture is 640x400 and is scaled up with hard pixel edges, which suits the sprites but made the message box, name tags, OUT/SAFE flags and the sign's lettering look stair-stepped. All text is now drawn on a second canvas laid over the picture (`DetailedFieldView` has two canvases, and `mlbAnimator.init(canvas, overlay)` takes both). The text canvas has as many pixels as the screen actually shows, including on a high-density display, while the drawing code still uses the picture's own 640x400 units, so the text is smooth and the field and players keep their pixel look. It is cleared and redrawn with every frame, follows resizing, ignores clicks (they still skip the play), and is skipped from the accessibility tree (the announcement region does that job). If there is no second canvas the text is drawn on the picture as before. Compared before and after in real Chrome on a 2x screen: the letters changed from stair-stepped to clean.
- **Layout.** The field is capped to the screen height, as Classic's is.
- **README** now describes the two views, the speed and skip controls, and the new tests.
- The browser scripts used for this (a small static server and a DevTools client) are not in the repository.

### Suggested sizing

| Phase | Effort |
|---|---|
| A | Small (about 1 day) |
| B | Small to medium (about 1 to 2 days) |
| C | Medium (about 2 to 3 days) |
| D | Medium to large (about 3 to 4 days) |
| E | Medium (art and feel take iteration; about 2 to 4 days) |
| F | Small (about 1 day) |

A first visible version (end of Phase C) is a few days of work. Full fidelity with the whole catalogue and sprites is roughly two to three weeks. These are rough estimates, and the largest uncertainty is tuning how the animation looks and feels.

## 7. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Animation disagrees with the result, such as the wrong runner shown out. | The `RunnerMoves` invariant test, and the choreographer reads only from `PlayResult`, never re-deriving outcomes. |
| Animation consumes the sim RNG and changes seeded results. | A separate seeded RNG. A regression test with fixed seeds. |
| Long games feel slow. | Detailed has Fast and Skip, a short gap between computer plays, and a play never takes longer than its script. Classic is one click away. Never animate simulate-to-end. |
| Classic regresses. | `FieldView` is not modified. Classic's existing tests are the guard, and a test asserts that a stored Classic choice renders `FieldView` with no canvas. |
| Invented hit locations look wrong in odd parks. | The wall-point helper uses `Stadium` data, with clamping. Review the smallest and largest parks in `wwwroot/data`. |
| The art does not look right (too flat, too modern, or too crude). | Tune against the reference screenshots at the end of Phase C, with placeholder art, before the sprite set is drawn. The script, projection and sprites are independent, so any one can be redone. |
| Perspective projection looks wrong for unusual parks. | Wall and landing points come from `Stadium` data and are clamped. Check the smallest and largest parks in `wwwroot/data`. |
| Pixel scaling looks soft on some displays. | A fixed 640x400 logical size, `image-rendering: pixelated`, and integer-friendly layout widths where possible. |
| Two sources of truth for field geometry. | Detailed has one: `FieldGeometry` in feet. It never reads Classic's pixel constants. |
| Mid-animation state (the `GameState` is already mutated). | Save before animating. Display a held pre-play projection until completion. |

## 8. Future animations

Not part of Phases A to F. Each is a new template or effect in the same script model, so none changes the architecture. They are grouped by what they depend on, and listed roughly in the order recommended.

**Engine already supports them (can follow Phase E directly)**

- **Atmosphere: done.** Inning changes (teams run on and off the field), pitcher entrances from the bullpen, pinch hitters and pinch runners walking out, mound visits, starting-lineup and "Play ball!" intro. Home run trots with a high-five at the plate, walk-off celebrations, a crowd that stands and flickers after runs, and a final-score card. See "Atmosphere, as built" below.
- **Random events.** Injury (player down, trainers, replacement jogs in), ejections (the manager argues at the plate), and rain delays (tarp, clouds). These use the existing `GameOptions` and `InjuryNotice`.
- **Pitching and batting detail: done.** Different wind-ups per `PitchType`, and left, right and switch-hitter stances. **Umpires: decided against.** An umpire figure was tried (gestures for strike three, a walk, an out and a stolen base) and removed at the owner's request: it added little, and the only place a single figure could stand without hiding the batter or the catcher was off to one side of the plate, where he did not look like the plate umpire.
- **Presentation.** The batter's name and average in the message box when he steps up, a pitch-count readout, a day/night palette, a closing dome roof (`IsDome`), and wind-blown outfield flags.
- **Error variety: done.** See "Plays, as built" below.

**Need small engine additions**

- **Diving catches: done**, without an engine flag: the chance follows the fielder's range rating (see below).
- **Relays and cutoffs: done** (a double or triple goes through a cutoff man since Phase D). **Tag at the plate: done.** Plate collisions: not done.
- **Runner leads and jumps: done** (by `SpeedRating`). Not done: a lead that depends on `HoldRunners`, and hit-by-pitch reactions.

**Wait for engine features listed in 3.3**

- Bunts, hit-and-run, pickoffs, wild pitches, passed balls, balks, double steals, steals of third or home, rundowns and triple plays. Each becomes a template once the sim produces it.

**Beyond single plays**

- **Replay: done for the last play** (see below). **Highlights** (the key plays from the game recap) are not: that needs the plays' scripts kept for the whole game, or rebuilt from stored play data. Scripts are never saved with the game.
- **Optional camera cuts** (a zoom on a home run or the batter). Off by default, to keep the fixed 1984-style camera.

### Atmosphere, as built

The scenes use the same script model as the plays (same JSON, same player), so the engine side is `SceneChoreographer` (`Animation/SceneChoreographer.cs`) beside `PlayChoreographer`, and the browser side is a few additions to `playAnimator.js`. Scenes are display only and never touch the game.

- **Game start.** `PlayBall(game)`: both batting orders come out and stand along their foul lines (home on the third-base side, visitors on the first), the banner announces the teams and the park, the lineups go back, the home team takes the field and the first batter steps in. It ends on "PLAY BALL!" with the usual sound as a cue in the script, so the page no longer plays that sound itself in Detailed. The game waits for it: the first computer turn is scheduled once the introduction has finished (`DetailedFieldView.OnReady` starts it).
- **New half-inning.** The side that was in the field runs off and the other side runs on, from and to a point past the edge of the picture so nobody pops in or out. The new batter walks in last.
- **New pitcher.** The manager comes out of the dugout, the reliever jogs in from the bullpen (behind the wall, past the edge of the picture, by the gate behind home plate), the ball changes hands, and the old pitcher leaves with the manager. A pitcher changed after a play (by the computer) is shown after that play; one changed before a pitch (by a human, in the decision or the Subs window) is shown before it.
- **Pinch hitter, pinch runner, new fielder.** The new man walks out of the dugout while the man he replaces walks off, with a banner line for each. A swap of positions between two men already in the game is not a substitution and shows nothing.
- **Mound visit.** After the third run of a half-inning (the `Rally` moment, unless a new pitcher comes in), the catcher and the infielders gather round the pitcher, talk, and go back.
- **Home run.** Everyone who scored finishes at the plate with both arms up (new pose `cheer`), and three teammates come out of the dugout to meet them. The crowd gets to its feet (new effect cue `stand`: fans rise over half a second and sparkle).
- **Walk-off.** When the home team takes the lead in its last at-bat the whole team runs to the plate and jumps together, the crowd stands, and the visitors trudge off. `IsWalkOff(game)` decides.
- **Final card.** A score card (new cue `card`, lines separated by newlines: the result, the two scores and the pitchers' decisions) is drawn over the field for as long as the game is over, including after the page redraws.

Detailed in the code: the scenes that follow a play use only what can be told apart after it (the half-inning and the pitcher), because the next batter and the runners always differ from those in the play. A scene before a pitch compares the field as last shown with the field about to be shown (`GameRoom._shown`). Anyone who left the game is gone for good, as in the rules. Reduced motion: the standing crowd is drawn already up, with no rise or sparkle.

**Random events and presentation.** The game now reports what interrupted it (`GameService.TakeEvents()`, a list of `GameEvent`s, display only and never saved, like the sound moments), because a computer-managed injury is dealt with on the spot and leaves nothing in the final state to show.

- **Injury.** The man goes down where he was (pitcher on the mound, fielder at his spot, a runner on his base, a hitter at the plate; new pose `down`), two trainers run out from his dugout, tend him, and walk him off. If the game already has his replacement (a computer-managed team) he jogs on to the spot. If a human must choose, the replacement window follows and the replacement simply appears.
- **Ejection.** The manager comes out of his dugout and argues at the plate, arms up and jumping, then is thrown out and leaves. There is no umpire figure. The organ tune for it already existed.
- **Rain delay.** After the new half-inning is shown, grey light and rain fall over the picture, everyone runs into the dugouts, the banner gives the length of the delay, and when the rain stops (cue `clear`) they come back to their places. Reduced motion keeps the grey light and drops the streaks.
- **Presentation.** The message box between plays names the hitter with his average and counts the pitcher's pitches (`E. Combs AT BAT (.325)`, `NOLAN: 54 PITCHES`). A dome park gets a steel roof across the top of the picture.

Order after a play: ejection, injury, new half-inning or pitcher, rain delay.

**Pitch wind-ups.** `PlayResult.Pitch` (display only, set by `GameService` from the defensive decision) says which pitch was called, and `PitchStyle` (`Animation/PitchStyle.cs`) turns that into timing, wind-up and flight. Every play's pitch uses it, so the time the ball takes to reach the plate, and so the whole play after it, depends on the pitch:

| Pitch | Wind-up | Ball | Reaches the plate |
|---|---|---|---|
| Fastball | arms up, short kick, overhand | straight | 0.60 s (the speed everything was tuned against) |
| Curveball | slow, high leg kick, overhand | loops in | 0.98 s |
| Slider | quick kick, arm out to the side (`sidearm`) | straight, then breaks late | 0.66 s |
| Changeup | exactly the fastball's wind-up | slow, hangs | 0.88 s |
| Pitchout | none: out of the stretch (`kick` only) | quick; the catcher steps out of the box | 0.48 s |

Pitchers are marked `hand` left or right (new optional field on `Actor`, in every scene too) and a left-hander is drawn mirrored, so he throws with the other arm and his slider breaks the other way. New poses: `kick` (front knee up, hands together at the chest) and `sidearm`.

**Batter stances.** Every batter actor carries the side he stands on (`hand`: a left-hander in the first-base box, a right-hander in the third-base box, as before) and a `stance` from `Stances.Of(batter, pitcher's arm)`, which uses the same power-or-slap measure as the outfield's depth:

- **power** (isolated power well above .150): feet wide apart, bat held high and back, a bigger waggle;
- **slap** (well below): crouched, bat choked up short and held level;
- **balanced**: the original stance.

The bat waggles a little while he waits (not under reduced motion), and the swing starts from the bat's resting angle. Running ends the stance. A **switch hitter** bats from the side opposite the pitcher's arm; when a new pitcher of the other arm comes in while he is at the plate, he walks out of his box, round behind the catcher and into the other one ("X SWITCHES TO BAT RIGHT" in the pitching-change scene).

**Routes.** The wall along each foul line stands 31 feet outside it and stops short of home plate, so the dugouts and the bullpens are behind it, past the edge of the picture and low in it (`x` = ±150 and ±200, `y` of 4 to 22). Everyone comes and goes between them and the field by the open ground behind home plate (the gate, `(±30, 0)`): `Scenes.Go` adds it as a waypoint whenever either end is behind the wall (`BehindTheWall`). Nobody walks over the wall or through the stands. `RouteTests` checks every scene of whole games for that (`WallRule`).

**The introduction hands over.** A home player on the line who also has a place in the first pitch (a fielder, the pitcher) runs from the line straight to it, and the visitors' first batter straight to the box. The walker and the field figure are the same man: the walker steps out of the picture and the field figure steps in at the same instant (two keys at the same time), so nothing pops. Only the men with no place go back to the dugout.

**The ball in a hand.** While the ball sits still beside a pitcher, catcher or fielder the player draws it in his hand (`holdingHand`, with `handOf` giving the hand for each pose), not at the point the script gives, which is only near him: at the pitcher's side at rest, over his head in the wind-up, at his chest in the kick, up in the throw, and in the catcher's glove. The script's pitch starts from the pitching hand (`PitchStyle.HandSide`, `HandHeight`) so the ball does not jump at the release. The score card sizes itself to its longest line (`cardLayout`).

**Plays, as built.** All of this is display only: the result is decided first, and the picture agrees with it.

- **Errors.** `ChooseMisplay` picks one of five, from the script's seed. An infielder: *booted* (it gets away from him and he chases it), *under the glove* (glove down at the grass, it carries on 16 to 24 feet behind him), or, for the shortstop, second baseman and third baseman only, a *wild throw* (he fields it cleanly and throws it over the first baseman and 50 feet past the bag, into the stands; the first baseman leaps for it; the batter and runners still end where the result puts them, which is how an extra base looks). An outfielder: *booted* (as before), *dropped* (it pops out of the glove and he finds it at his feet) or *misjudged* (he breaks in, it lands behind him, and he chases it toward the wall). After any of them the throw is late, as before.
- **Diving catches.** A fly out or line out becomes a dive with a chance of 10% for a fielder of average range (5 of 9), 7 points more per point of range, between 4% and 45% (`DiveChance`, from `Player.FieldingAt(pos).Range`). The catch is made 9 to 14 feet to the side for an infielder and 20 to 32 for an outfielder (never in the stands, never past the wall), low (1.5 feet) in the new `dive` pose; the banner says "DIVING CATCH!", the crowd cheers, the description follows, and he gets up (0.75 s) before any throw. Pop-ups, and the pitcher and catcher, never dive.
- **Tag at the plate.** When a throw reaches the catcher at the plate he catches it, sweeps a tag a beat later (`tag` pose), and stands up again.
- **Leads.** As the pitcher winds up every runner steps off his base toward the next (4 feet for a speed rating of 1, 10 for a 5, `Staging.LeadFrom`) in the standing pose: nothing runs before the ball is hit. A runner who goes breaks from the lead; one who does not goes back to the bag at the end, so the end of a play is still the bases and the next picture is unchanged.
- **Reactions.** New `slump` pose (the head sinks into the shoulders): the pitcher after a home run, the batter after a strikeout (his bat is gone and he walks off after half a second). The pitcher pumps both arms after a strikeout. The line score flashes the new total and this inning's cell for a second and a half when a run scores (a changed `@key` restarts a CSS animation; none under reduced motion).

**Replay.** `GameRoom` keeps the script of the last play as it was shown (`_replay`, in memory only: not saved, and gone when a game starts or is resumed). The **Replay** button is on the field, in the bottom-right corner where **Skip** shows while a play runs (`DetailedFieldView`, `CanReplay`/`OnReplay`), so it is there only between plays, once a play has been shown, and never when it would do nothing. (It was first a toolbar button, but the page deliberately does not redraw while a play is shown, so a toolbar button kept looking enabled during plays; the field redraws itself.) It plays that same script again at the chosen speed with a "REPLAY" line in front of its banners, so the play is move for move what happened (the same seed, nothing rebuilt). Nothing in the game changes and nothing is saved; when it ends the field is drawn again as it stands. It is available after the last play of a game too, and the final card comes back afterwards. Decisions are ignored while it runs. A computer turn that was waiting is cancelled when it starts and scheduled again when it ends, and `ScheduleComputerTurnIfNeeded` does nothing while something is being shown, so a replay never overlaps the next play.

Not done from the original list: a day/night palette and wind-blown flags.

## 9. Decisions on the open questions

These were the open questions in the first draft. Each now carries a recommended answer, which this spec assumes unless changed.

- **O1. Reveal timing: reveal at the end.** The scoreboard, bases and outs update when the animation finishes, not as each runner arrives. It matches the 1984 feel, avoids spoiling the play, and needs no per-event state projection. `GameRoom` holds a pre-play display state until the animation completes or is skipped. Live updating can be added later without changing the script format, because cues already carry timestamps.
- **O2. Detailed speed defaults: Normal with a human, Fast for computer vs. computer.** In computer vs. computer, plays should keep pace without a 3-second wait each, so Fast plus a short gap replaces the fixed pause. The player's own choice always overrides the default.
- **O3. Park wall scale: yes, in Detailed from the start.** Detailed draws its own backdrop from `Stadium` data in real feet (4.4, 4.5), so the wall to scale costs almost nothing extra and short porches and deep alleys are visible. Classic's wall is unchanged. (This replaces the earlier recommendation to defer it, which assumed a shared backdrop.)
- **O4. Sprite style: pixel sprites authored as text grids, in the 1984 style but larger and better animated (4.6).** No art tooling or licensed assets, team colors come from a palette swap, and it fits the reference screenshots. The pose names in the script are the stable interface, so hand-drawn sprite sheets can replace the grids later without touching the engine.
- **O5. Bunts and other unresolved plays: a separate engine feature first, after this one.** Do not block animation on it. Bunts, pickoffs, wild pitches and similar plays change odds and results, so they need their own design and tests. Once the engine produces them, adding a template is a small change. Until then, unsupported outcomes fall back to the banner and final state (3.3).
- **O6. Fielding credit: record `Fielder` now, use it later.** Phase A adds the field at almost no cost. Putouts and assists in the box score are a separate small follow-up, so this feature does not change box-score output.
- **O7. Where the switch lives: game toolbar plus the setup screen, stored per device.** It is a viewing preference rather than part of the game, so it should not go into saves or be shared between devices, and a global settings page would be a heavier change. Default is Detailed.


### Ballpark details (added after the first release)

- **Park identity.** `stadiums.json` may give a park a `wallColor` (`#rrggbb`); the field paints the wall, its lighter top line and the walls along the foul lines in it. Only two parks have one, because only two have a source (see `wwwroot/data/ATTRIBUTION.md`); every other park keeps the default wall. The center-field sign shows the park's name.
- **Distances and poles.** The five posted distances are drawn on the wall on the sharp text layer (so they stay readable on a TV), and a yellow foul pole stands at each end of the wall, 22 feet above it.
- **Foul ground.** The bullpens are strips along each foul line between the line and the wall. They are scenery only: a reliever in a scene still walks in from the dugout side, because `Scenes.Bullpen` is off the edge of the picture. No dugout is drawn: a sunken pit was tried and did not read as a dugout, so players who leave the picture simply walk off its edge (where `Scenes.Dugout` sends them).
- **Numbers on the wall follow the wall.** Each posted distance is drawn with its own transform built from where the wall's tangent and its vertical land on the picture, so near the poles, where the wall runs away from us, the digits slant and narrow with it.
- **Players.** The picture is drawn at twice its size (`RES = 2`; the field itself is still scaled up with hard pixels), so the square players can carry detail: eyes with whites and brows, ears, a mouth, hands with a thumb, a belt and buckle, and a band of the team's colour on the stockings. Pitchers, catchers and fielders wear a brown glove (a darker pocket) on the hand that is not throwing; batters, runners and the team in the dugout have bare hands.

### Fielding, the bat and the ball (added after the first release)

- **New poses** (`Poses` in `PlayScript.cs`): `crouch` (bent low, glove at the grass, for a ground ball), `transfer` (the ball goes from the glove to the throwing hand, put in by `ThrowTo` just before every throw when there is time for it) and `stretch` (the pitcher's set position).
- **Ground balls and fly balls.** A ball that rolls is fielded in a `crouch` (`Fetch` is given the pose); under a ball in the air the fielder has his glove up (`catch`) 0.3 s before it arrives, not only at the catch.
- **Pitching.** With a runner on, the pitcher pitches from the `stretch`; with the bases empty he winds up. Timings are the same. Each pitch also carries a cue `effect` `pitch:<type>` from the release, and the picture names the pitch beside the ball while it is in flight. The pitch types already differed in the ball's flight (a curve loops and is slow, a slider breaks late, a change-up hangs); no speed is shown, because the game has no pitch speeds to show.
- **The swing.** The bat carries on after the hit and the hands come up (follow-through). Once the batter leaves the box a man who swung drops his bat by the plate, and on a home run he flips it away: a short spinning arc (JS only, from the script's `outcome`). There is no bunt in the animation: a bunt is a strategy chosen before the play, and the play result does not say which strategy it was.
- **The ball.** A ball in flight has a seam that turns and a streak of blur behind it when it moves fast; a rolling ball hops, smaller each time. On a double or a triple the ball now runs on to the wall, hits it and comes back off it to where the outfielder picks it up (a key at the wall, then a short rebound to the resting point). Reduced motion drops the blur, the hops and the spin, and the bat is dropped without the flip.
