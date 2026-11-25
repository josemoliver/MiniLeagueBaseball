# The simulation engine

The engine in `src/MiniLeague.Engine` decides what happens in a game. A manager (a person or a computer manager) makes
calls; the engine rolls the plate appearance, moves the runners, tires the pitcher, keeps the box score, and says what
happened. It knows nothing about the screen. This document specifies how it works: the game loop, the probability model
behind every outcome, base running, defense, pitching and fatigue, substitutions, random events, the records it keeps, how a
game is saved, and how a play is turned into an animation script.

It is the companion of [ComputerManagers.md](ComputerManagers.md) (who makes the calls) and of
[ModelingStatisticsProbability.md](ModelingStatisticsProbability.md) (how the numbers here were checked against real
seasons, what was found, and what does not work). Where a number below has a history, that document has it.

## Contents

1. [Overview](#1-overview)
2. [Architecture](#2-architecture)
3. [The data the engine reads](#3-the-data-the-engine-reads)
4. [The game loop](#4-the-game-loop)
5. [One plate appearance](#5-one-plate-appearance)
6. [The outcome table](#6-the-outcome-table)
7. [Base running](#7-base-running)
8. [Bunts, squeezes, steals and the hit and run](#8-bunts-squeezes-steals-and-the-hit-and-run)
9. [Defense](#9-defense)
10. [Pitching, fatigue and the bullpen](#10-pitching-fatigue-and-the-bullpen)
11. [Lineups, the designated hitter and substitutions](#11-lineups-the-designated-hitter-and-substitutions)
12. [Random events](#12-random-events)
13. [Winning, ending and the records](#13-winning-ending-and-the-records)
14. [Win probability and the recap](#14-win-probability-and-the-recap)
15. [Saving and resuming](#15-saving-and-resuming)
16. [Determinism and random numbers](#16-determinism-and-random-numbers)
17. [From a result to an animation](#17-from-a-result-to-an-animation)
18. [What the engine does not model](#18-what-the-engine-does-not-model)
19. [Calibration status](#19-calibration-status)
20. [Tests](#20-tests)
21. [Changing the engine](#21-changing-the-engine)
22. [File map](#22-file-map)

---

## 1. Overview

The unit of play is the **plate appearance**. One call to the engine resolves one batter's turn (or one steal attempt, which is
not a plate appearance) from the situation, the two managers' calls, and a seeded random number generator. There are no
individual pitches: the engine estimates a pitch count for each play (section 5) so that fatigue works, but it never rolls a
ball or a strike.

```
situation  +  batting call  +  fielding call
   (GameState)   (OffensiveDecision)  (DefensiveDecision)
                    │
                    ▼
        AtBatResolver.Resolve ──► PlayResult  (outcome, runs, outs, new bases, who was thrown out, pitches, text)
                    │
                    ▼
        GameService.ApplyPlay ──► bases, runs, outs, pitcher fatigue, batting order, box score, play-by-play,
                                  win probability, end of inning / game
                    │
                    ▼
        the computer sides then act: warm-ups, pitching changes, defensive replacements
```

The shape of the model:

* **The batter's own season line** (his rate of each result per plate appearance) is the starting point. It already includes
  the league he played in.
* **The pitcher** scales those rates by how much better or worse than his own league's average he was at giving up hits,
  home runs, walks and strikeouts (section 6).
* **Modifiers** adjust the table: platoon, ballpark, defense, the pitch called, the defensive alignment, and the pitcher's
  fatigue.
* **One roll** against the table picks the result. Runner movement, errors and double plays then follow from the result and
  from further rolls whose odds come from the runners' speed, the fielders and the pitcher.
* **Everything is a weight, never a guarantee.** The rules are set so that an average player in an average situation gets
  his real season rates, and so that no call is free (a call that helps somewhere costs somewhere else).

Two properties are guaranteed by design and by tests:

* The engine is **plain**: a class library with no package or project references and nothing about the user interface, the
  browser or JavaScript.
* It is **deterministic given its random generators**: the same seeds and the same calls give the same game, byte for byte
  (section 16).

## 2. Architecture

```
src/MiniLeague.Engine
├── Models/        the data: Team, Player, stats, GameState, decisions, results, options, box score
├── Simulation/    the rules: probability table, base running, bunts, defense, fatigue, win probability
├── Services/      the orchestration: GameService (the loop), box score, recap, save format, team loading
├── AI/            the computer managers (see ComputerManagers.md)
└── Animation/     result → animation script (display only)
```

| Piece | Responsibility |
|---|---|
| `GameService` | Owns the `GameState`. Runs the loop, applies results, advances innings, handles substitutions, random events, computer managers' moves. The only thing the app calls to play. |
| `AtBatResolver` | Pure rules for one play: takes a state and two decisions, returns a `PlayResult`. Never changes the state. |
| `ProbabilityTable` | Turns a batter, a pitcher and the situation into outcome weights. |
| `BaseRunningEngine`, `RunningRules` | Where each runner ends up; the shared constants (hit and run, double plays, steals, extra bases). |
| `BuntModel`, `DefenseModel`, `FatigueModel` | Bunt and squeeze odds; who fields and errors; pitch counts and tiring. |
| `WinProbability` | The home team's chance of winning from any situation; run expectancy. |
| `BoxScoreRecorder`, `GameRecaps` | Statistics as the game goes; highlights and a written recap. |
| `GameSnapshots` | Save and restore. |
| `TeamLoader`, `StadiumLoader` | Read the team and ballpark JSON. |
| `PlayChoreographer`, `SceneChoreographer` | Turn a finished play into an animation script. They never change the game. |

**Who calls what.** The Blazor app (`src/MiniLeague.Web`, `GameRoom`) calls `GameService`:
`StartGame` → repeated `ExecutePlay` (or `ExecuteAtBat` with the human's decisions) → substitutions and pitching moves between
plays. The app never touches the state except through the service, and shows the state, the `PlayResult` and the animation
script. The benchmark tool (`tools/MiniLeague.ManagerBench`) plays computer-versus-computer games through the same calls.

**The shared-rules principle.** Anything the resolver rolls and a computer manager prices uses the same code or constant
(`RunningRules`, `BaseRunningEngine.StealSuccessChance`, `ProbabilityTable.Build`). A number copied into both would drift apart
and the manager would misjudge the play it is choosing.

## 3. The data the engine reads

Teams are JSON files in `src/MiniLeague.Web/wwwroot/data/teams`, generated from the Lahman Baseball Database by
`tools/generate_teams.py` (CC BY-SA; see `wwwroot/data/ATTRIBUTION.md`). Fifteen teams from 1927 to 1982. A file carries:

| Field | Used for |
|---|---|
| `season`, `league` | the designated hitter rule; league figures below |
| `batters[]`: `ab h d2 d3 hr bb so hbp sb cs sh speed bats throws pos field` | the batter's rates, steals, speed rating, bunting rating (from `sh`), fielding by position |
| `pitchers[]`: `era ip h hr bb so cg gs role tiringFactor throws` (and `bat`, `bats`) | the pitcher's rates, stamina, role, his own batting line |
| `leaguePitching` | what an average pitcher did in the team's league that season, per nine: ERA, non-home-run hits, home runs, strikeouts, walks |
| `leagueStealRate` | the league's steal success that season |
| `eraErrorsPerGame`, `eraStrikeoutsPerGame` | the league's errors and strikeouts per team game: how often a ball in play is booted |
| `tendencies` | the club's real habits (steal attempts, sacrifices, complete games, saves per game); not yet used in play |
| `stadium` | the ballpark id (`stadiums.json`: dimensions and park factors) |

**What is derived, not recorded** (so it is approximate): the speed rating (1 to 5) from steals and triples per 600 at-bats;
fielding range, error and arm ratings from putouts, assists, errors and the catcher's caught-stealing against the league at
the position; the bunting and sacrifice ratings (1 to 6) from sacrifice hits per plate appearance; the power rating from
home runs.

**What no data supplies, so every player gets the default:** a pitcher's ground-ball rate (0.45), his ability to hold runners
(3 of 5), platoon splits (a standard modifier applies instead), and ballpark dimensions are hand-entered and approximate.
Lahman has no play-by-play and no pitch data.

A team file without a newer field still loads and falls back to a default (`TeamLoader`): no `leaguePitching` means the old
fixed pitcher references (a 4.00 ERA, 7.0 strikeouts and 3.0 walks per nine); no `leagueStealRate` means 0.67; no `gs` means
the tiring factor; no `sh` means an average bunter (3); no pitcher `bat` means an ordinary pitcher's line (about .150).

## 4. The game loop

### The state

`GameState` holds everything about a game in progress: both teams, the ballpark, the inning (`CurrentInning`, `IsTopOfInning`),
the outs, the three base runners, each side's batting order and positions, batter index, the active pitcher
(`ActivePitcher`: pitch count, batters faced, runs allowed, condition), the bullpens (`WarmingPitcher`), who has been used, the
alignment, the score by inning, the box score, the play-by-play and the win-probability track. Substitutions work on copies
(`VisitorLineup`, `VisitorPositions`), so a team is never changed.

### Starting

`StartGame` takes two teams and their control (human or computer), optional lineups and starters, the options, and a manager id
for each side. It decides the designated hitter first (section 11), chooses the starters and lineups, builds the bullpens
(everyone on the pitching staff except the starter, resting), and opens the box score. The game starts in the top of the
first with the visitors batting.

### One turn

`ExecutePlay(humanOffense?, humanDefense?)` plays the next plate appearance. A side with no human decision gets its computer
manager's. Both managers decide inside one `ManagerDecisionModel.Decision` scope, so what one works out the other can reuse. The
computer defense decides **after** a declared pinch hitter, for the batter who will actually bat. Then `ExecuteAtBat`:

1. **Offensive substitutions** are applied (pinch runner, then pinch hitter).
2. A **reliever** named in the defensive decision is brought in.
3. The chosen **alignment** is stored on the fielding side (the base-running rules read it for double plays).
4. The **pre-play snapshot** is captured (`PrePlaySnapshot`): everything the animation needs about the moment before the play.
5. `AtBatResolver.Resolve` produces the `PlayResult` (sections 5 to 9).
6. `ApplyPlay` applies it (below).
7. If the game goes on: the random events (ejection, injury) are rolled; if the half-inning changed, a computer side may make a
   defensive replacement (and a pitcher out of the order after a pinch hit is replaced); then the fielding computer manager
   may start a reliever warming or change pitchers (with a double switch where the pitcher bats).

### Applying a result

`ApplyResult`, in order:

1. The box score credits the batter, runners and pitchers, while the bases still show who was where.
2. Runners who reached are charged to the pitcher who let them on (`ResponsiblePitcherId`).
3. The bases, runs (by inning), and outs are updated; a rally (a side's third run of a half-inning) and an error in the inning
   are noted (runs after an error are unearned).
4. The pitcher's **pitch count** grows by the play's pitches (at least 1), his runs allowed by the runs scored. If it was a
   plate appearance (a steal attempt is not), his **batters faced** grows, the bullpen warms by one batter (section 10), and
   the batting order advances (it wraps).
5. A **walk-off** (the home team leads in the bottom of the ninth or later) ends the game on the spot.
6. Three outs end the half-inning (below).

### Innings and the end

At the third out: stranded runners are counted, the outs and bases clear, and the error flag resets. After the **top** half, the
home team wins without batting if it leads in the ninth or later; otherwise the bottom half starts (and the seventh-inning
stretch is announced as it begins). After the **bottom** half, from the ninth on, anything but a tie ends the game; a tie goes to
another inning. Extra innings continue until the tie is broken: there is no ghost runner and no limit. A rain delay may start any
half-inning after the first (section 12).

### Human turns

`OffenseIsHuman` and `DefenseIsHuman` say whether a human must decide; a human-managed side that has not supplied its call is
an error (`InvalidOperationException`), so the app can never skip it. `ExecuteComputerTurn` plays a turn only if no human is
needed and no injury is waiting to be replaced. A pending injury (section 12) blocks play until it is resolved.

## 5. One plate appearance

`AtBatResolver.ResolveCore` is a short sequence of special cases ending in a roll:

1. **Intentional walk:** if the defense called it, nobody swings. The batter takes first; forced runners move; the bases-loaded
   run scores.
2. **Bunt** (`SacrificeBunt`): its own model (section 8); the at-bat table is not used.
3. **Squeeze** (`Squeeze`, `SafetySqueeze`): a bunt with the runner on third coming home; with no runner on third or with two out
   it is only a bunt.
4. Otherwise the batter **swings**. The pitcher's and batter's stats are chosen by handedness (a switch hitter bats from the side
   opposite the pitcher's arm), the pitcher's **fatigue modifier** is read, the fielding side's **ratings** are measured
   (section 9), and `ProbabilityTable.Build` makes the weights (section 6).
5. A **steal** call that has a base to steal (section 8) is resolved first and never uses the table. With a hit and run, a
   *pitchout* turns the play into a straight steal attempt; otherwise the hit-and-run weights are applied (fewer strikeouts,
   a few more singles).
6. **Roll.** One uniform random number picks the outcome in this order: home run, triple, double, single, walk, hit by pitch,
   strikeout, ground out, fly out. The weights need not add to 1; the roll is scaled by their total.
7. **Error check.** A ground out or fly out goes to a fielder (shares in section 9); that fielder may boot it, which makes the
   outcome an **error**.
8. **Hit-and-run line drive.** On a hit and run, 35% of the fly outs become line outs (the runner who has already gone is then
   often doubled off).
9. **Base running** (section 7) decides where everyone ends up. A ground out that turned two outs is relabelled a **double play**
   afterwards, so the log, the box score and the out count agree.
10. The **description**, **runner moves** (`RunnerMoveBuilder`, display only) and a **pitch estimate** are added to the result.

**Pitch counts are an estimate, not a model.** A strikeout takes 3 to 7 pitches, a walk 4 to 7, an intentional walk 4, a hit by
pitch 1 to 5, a steal 1, and anything else 1 to 5: about 3.5 a plate appearance and 4.9 an out. They drive fatigue and the
managers' pitch counts, nothing else.

**Strategies the resolver treats specially:** `SwingAgressive` (the runners are sent hard on hits, section 7), `Steal`,
`HitAndRun`, `SacrificeBunt`, `Squeeze`, `SafetySqueeze`. `SwingAway` and `SwingSafe` are the same plain swing (`SwingSafe` has
no separate effect in the engine). `SurpriseBunt` exists in the enumeration and is not produced or resolved separately: a bunt
with no one on is a `SacrificeBunt` call that the engine treats as a bunt for a hit.

## 6. The outcome table

`ProbabilityTable.Build(batter, pitcher, stadium, hand, pitch, alignment, fatigue, defense, pitcherHand)` returns the weights.

### Batter rates

Per plate appearance (`pa = AB + BB + HBP`): `HR, 3B, 2B, 1B (hits − the rest), BB, HBP, SO`, each divided by `pa`. A batter with no
plate appearances gets league-typical defaults.

### The pitcher, against his own league

Each rate is scaled by the pitcher's rate divided by his **league's** rate that season (`LeaguePitching`, from the team file),
so an average pitcher of any era leaves the batter's line alone:

| Weight | Scaled by |
|---|---|
| Single, double, triple | pitcher's non-home-run hits per nine ÷ the league's, clamped to 0.6 to 1.5 |
| Home run | `0.5 + 0.5 ×` (his home runs per nine ÷ the league's), clamped 0.4 to 2.0: pulled halfway to the league rate, because one pitcher's season total is noisy |
| Walk | his walks per nine ÷ the league's, clamped 0.4 to 2.5 |
| Strikeout | his strikeouts per nine ÷ the league's, clamped 0.4 to 2.5 |
| Hit by pitch | not scaled |

All the hit weights are also **divided by the pitcher's fatigue effectiveness** (clamped 0.5 to 1.0): a tired pitcher gives up more
hits. Hits and home runs use their own rates because ERA moves far more than hits do (strikeouts, walks, home runs and
stranding all feed it), so scaling every hit by ERA made a good staff much too hard to hit. A team file without league figures
falls back to scaling every hit by `ERA ÷ league ERA` with the old fixed references. See
[ModelingStatisticsProbability.md](ModelingStatisticsProbability.md) section 5.

### Platoon

A standard modifier by the batter's side against the pitcher's arm (there is no per-player split in the data):

| Matchup | Contact (1B, 2B, 3B) | Power (HR) | Walks | Strikeouts |
|---|--:|--:|--:|--:|
| Left-handed batter against a left-hander | ×0.94 | ×0.88 | ×0.92 | ×1.12 |
| Left-handed batter against a right-hander | ×1.02 | ×1.04 | ×1.03 | ×0.96 |
| Right-handed batter against a right-hander | ×0.98 | ×0.96 | ×0.97 | ×1.04 |
| Right-handed batter against a left-hander | ×1.03 | ×1.06 | ×1.04 | ×0.95 |

### Ballpark

Hitters' own lines already carry some of their home park, so the overall run environment is applied at **half strength**
(`√RunsFactor`, clamped 0.7 to 1.4) to every hit weight; the fence-specific factors apply in full: the home-run factor times the
side's own factor (left- or right-handed batters pull to different fences), the doubles-and-triples factor, and the singles factor
(turf plays faster).

### Defense

The fielding side's average infield and outfield **range** (1 to 9, 5 average) shift the hit weights: infield effect
`1 − 0.015 × (range − 5)` and outfield effect `1 − 0.020 × (range − 5)`; singles use 60% infield and 40% outfield, doubles and
triples the outfield effect; each clamped to 0.88 to 1.12.

### The pitch called

Each pitch trades one thing for another. The multipliers are balanced so that, against an ordinary hitter, no pitch is worth
more runs than another (calibration found two pitches that were; section 19).

| Pitch | Home runs | Strikeouts | Walks | Singles |
|---|--:|--:|--:|--:|
| Fastball | ×1.10 | ×1.10 | ×0.90 | ×0.99 |
| Curveball | ×0.85 | ×1.05 | ×1.10 | ×1.05 |
| Slider | ×0.90 | ×1.15 | ×1.05 | ×1.045 |
| Changeup | ×0.80 | ×0.90 | ×0.95 | ×1.125 |
| Pitchout | ×1.00 | ×0.90 | ×1.25 | ×1.00 |

A pitchout against a runner who does not go only puts the pitcher behind in the count; against a runner who does, it is a steal
attempt against a ready catcher (success ×0.15).

### The defensive alignment

Each alignment buys outs in one place and gives them up in another; none is better than Standard for an ordinary hitter with the
bases empty (`NoFreeOptionTests`). Their gains are situational.

| Alignment | Singles | Doubles | Triples | Also |
|---|--:|--:|--:|---|
| Standard | ×1.00 | ×1.00 | ×1.00 | |
| Corners In | ×1.06 | ×1.04 | ×1.00 | bunts: hits ×0.55, fielder's choices ×1.6 |
| Infield In | ×1.12 | ×1.00 | ×1.00 | bunts: hits ×0.80, choices ×1.2; a runner on third usually holds on a ground ball (section 7); never a double play |
| Guard Lines | ×1.12 | ×0.85 | ×0.85 | |
| Shift (pull) | ×1.20 | ×0.90 | ×1.00 | |
| Shift (opposite) | ×0.95 | ×1.20 | ×1.00 | |

### The remainder

What is left of the probability goes to balls in play that are outs: `out = 1 − (hits + walks + hit by pitch) − strikeouts`
(at least 0.05), split by the pitcher's **ground-ball rate** into ground outs and fly outs. (The rate is 0.45 for every pitcher: the
data has none.)

## 7. Base running

`BaseRunningEngine.Advance` moves runners on a hit or a walk; `GroundOut` on a ground out; `OnError` on a booted ball. The rules
are fixed by the outcome, except where noted, and all random draws come from the resolver's generator.

| Result | What happens |
|---|---|
| Home run | everyone scores |
| Triple | runners score; the batter is on third |
| Double | runners on second and third score; the runner on first goes to third, unless sent (below) |
| Single | the runner on third scores; the runner on second scores or stops at third (below); the runner on first goes to second, or to third (below) |
| Walk, hit by pitch, intentional walk | only forced runners move; the run scores only with the bases loaded; no randomness |
| Error | the batter is on, every runner moves up one base; no RBI |

### Extra bases: the speed check

Where a runner might take an extra base, the engine rolls once: success when `random(0..99) < base + 8 × (speed − 3) − arm penalty`, with
speed from 1 to 5. The outfield-arm penalty is `6 × (arm − 3)` points (arm 1 to 5). The bases (`RunningRules`):

| Situation | Base chance |
|---|--:|
| Runner on second scores on a single | 30% |
| ... sent hard (`SwingAgressive`) | 55% |
| Runner on first takes third on a single, sent hard | 50% |
| ... on a hit and run | 65% |
| Runner on first scores on a double, sent hard | 60% |
| ... on a hit and run | 72% |
| Runner on third scores on a fly out (a tag-up), fewer than two out | 65% |

Only an **outfielder's** catch is deep enough to tag up; a pop-up in the infield or to the catcher holds the runner. With two out
the catch is the third out and no run counts.

### Sending a runner hard

An aggressive send (`SwingAgressive`) that fails is not free: **60%** of the time the runner is **thrown out** instead of held
(`SentRunnerThrownOut`), at most one runner per play, and the runner behind does not try once the one ahead was thrown out. The
out is in `PlayResult.ThrownOutFrom/ThrownOutAt` (where he started, and where he was tagged: 3 or 4 for home).

### Ground outs and double plays

With a runner on first and fewer than two out, a ground out is a double play with probability
`RunningRules.DoublePlayChanceFor(batter speed, pitcher ground-ball rate, runnerGoing)`:

```
chance = base + (3 − batter speed) × 0.05 + (ground-ball rate − 0.45) × 0.5     clamped to 0.04 .. 0.70
base   = 0.52     (0.12 when the runner broke with the pitch: he is most of the way to second)
```

An Infield In alignment prevents the double play. A double play takes the batter and the runner on first; the others move up, and
a run from third counts only if the inning does not end on it. With the **infield in** and an unforced runner on third, he
usually has to hold: he scores only `0.25 + 0.05 × (speed − 3)` of the time (10% to 45%), at the cost of the extra singles the
alignment gives up. Any other ground out retires the batter and moves each runner up a base; a run from third counts unless it
is the third out. **There is no fielder's choice on an ordinary swing**: the double-play share of ground outs carries that whole
rate (calibrated at about 10.4% of the chances with a runner on first and fewer than two out).

## 8. Bunts, squeezes, steals and the hit and run

### Bunts (`BuntModel`)

A bunt is its own play. With a runner on and fewer than two out it is a **sacrifice**; otherwise the batter is bunting for a hit.
The ball goes to the pitcher (32%), third base (25%), first base (25%) or the catcher (18%), and the fielder may boot it
(1.4 times a routine ground ball's error chance). In order, one roll decides:

1. **Error:** the batter is on, every runner moves up.
2. **Pop-up** (also a missed bunt): the batter is out, nobody moves. 10% at average skill (skill is the mean of the bunting and
   sacrifice ratings, 1 to 6, average 3), `−1.5%` per point of skill, clamped 4% to 16%.
3. **Bunt single:** a sacrifice `4% + 3% × (speed − 3)`; a bunt for a hit `8% + 4.5% × (speed − 3) + 1% × (skill − 3)`; clamped 1% to
   30%.
4. **Fielder's choice** (a sacrifice with a runner the defense can take): the lead runner is out at the next base, the batter is on
   first. About 14%, reduced by skill and by a fast runner. Which runner is the target depends on who is on (`FieldersChoiceFrom`):
   the runner on first (alone) or second (with or without first); never a lone runner on third or runners on second and third.
5. Otherwise a **sacrifice bunt** (batter out, every runner moves up, a runner on third scores) or a **groundout** for a bunt with
   nobody to move.

Corners In takes bunt hits down to 55% and raises the fielder's choice by 60%; Infield In does a little of the same.

### Squeezes

* A **suicide squeeze** (`Squeeze`): the runner on third breaks with the pitch, so the batter must bunt whatever is thrown. Pop-ups
  rise (`1.4 ×` plus 4%, 8% to 30%) and a popped-up or missed bunt **doubles the runner off third**; the defense can field the ball
  and throw home (the "fielder's choice" is the runner **out at the plate**, the batter safe, about 20% reduced by a fast runner
  and a good bunter, 6% to 40%, raised by Corners and Infield In); otherwise the bunt is down and the run scores.
* A **safety squeeze** (`SafetySqueeze`): the runner goes only once the bunt is down. It never loses him; the run scores with
  probability `0.62 + 0.07 × (runner speed − 3) + 0.03 × (skill − 3)` (30% to 90%, ×0.8 against Corners In), otherwise he holds at
  third and the batter is out.

### Steals (`StealPlan`)

The steal call sends:

| Situation | Plan |
|---|---|
| Runner on first, second open (even with a runner on third) | steal of second |
| Runner on second, first and third open | steal of third |
| Runners on first and second, third open | double steal |
| Anything else | nothing to steal (the call is a plain swing) |

**Success** of one runner (`StealSuccessChance`):

```
record  = (his SB + leagueRate × 5) / (his SB + CS + 5)      steadied toward his league's rate by five attempts
chance  = record × (1 + (3 − pitcher hold) × 0.07) × (1 + (3 − catcher arm) × 0.07) × (1 + (speed − 3) × 0.015)
clamped 0.05 .. 0.95;   × 0.15 against a pitchout
```

A steal of third is harder: success is multiplied by 0.93. On a **double steal** the catcher picks a base: to third 65% of the time (the lead
runner is out if he fails; the trailing runner takes second), otherwise to second (the trailing runner is out if he fails; the lead
runner is safe at third). A caught stealing is one out. A steal is not a plate appearance: the batter stays up, nobody is "faced".

### The hit and run (`RunningRules`)

With a runner on first, second open and no intentional walk, the runner breaks with the pitch. The table is adjusted: strikeouts
×0.75 (the batter fights off strikes), singles ×1.08 (the second baseman covers the bag). Then:

* On a **strikeout**, the runner is on a pitch the batter missed: the catcher gets a steal-style throw; if he is out it is a double
  play (the log says so).
* A **fly out** becomes a **line out** 35% of the time, and on a line out the runner is **doubled off** with probability
  `0.55 − 0.08 × (speed − 3)` (25% to 75%).
* A **ground out** rarely turns two (double-play base 0.12).
* On a **hit** the runner takes the extra base more often (the hit-and-run rows of section 7).
* A **pitchout** turns the whole play into a steal attempt against a ready catcher.

## 9. Defense

* **Who fields a ball in play.** A ground out goes to short 30%, second 21%, third 20%, first 17%, the pitcher 8%, the catcher 4%; a
  fly out to center 30%, left 24%, right 24%, third 6%, short 6%, second 4%, first 4%, the catcher 2%
  (`DefenseModel.PickFielder`). The fielder is recorded on the result (display, and who made an error).
* **Errors.** Only a ball that would have been an out can be booted. The chance is
  `league errors ÷ batted-ball chances × position propensity × fielder multiplier ÷ normaliser`, capped at 0.35. The league's
  errors and strikeouts per game come from the team file (`eraErrorsPerGame`, `eraStrikeoutsPerGame`); batted-ball chances are
  `27 − strikeouts + errors`. The position propensity (short and third 1.5, second 1.1, pitcher and catcher 0.8, left 0.7, right 0.6,
  center 0.5, first 0.5) and the fielder's error rating (3: ×0.45; 2: ×0.9; 1: ×1.45; 0: ×2.1) are scaled so a league-average defense
  errs at exactly the league's rate. A player at a position he has not played gets a poor profile (`FieldingProfile.OutOfPosition`).
* **Ratings of the whole defense.** `DefenseModel.RatingsFor` averages the infielders' range, the outfielders' range, and the
  outfielders' arm; they feed the hit weights (section 6) and the extra-base chance (section 7).
* **The catcher and pitcher against the running game** are the catcher's arm (stopping steals) and the pitcher's hold, both 1 to 5
  with 3 average; all pitchers carry 3 (no data).
* **Defensive value** (`DefensiveValue`, `PositionValue`) rates a glove by position (short, center and catcher count 1.3, second
  and third 1.1, others 0.8). The computer managers use it to place players after a substitution.

## 10. Pitching, fatigue and the bullpen

### Effectiveness

`FatigueModel.GetModifier(ActivePitcher)` returns a multiplier (1.0 is full strength) that divides the hit weights (section 6):

* **Pitch count.** Above his first threshold ×0.92, above the second ×0.80, above the third ×0.65. The thresholds are his
  **stamina**, +20 and +35 pitches.
* **Settling in.** A reliever who came in cold or overwarmed pitches below par for his first 3 batters (his entry modifier below).
* The result sets his displayed **condition**: ≥0.95 Fresh, ≥0.88 Ok, ≥0.78 Tiring, ≥0.65 Tired, otherwise Exhausted.

### Stamina

For a starter whose games started are known:

```
stamina = clamp( outs per start × 4.9 + 15 × (share of his starts he finished), 70, 140 )   pitches
```

where outs per start is three times his innings per start and 4.9 is the engine's pitches per out. Anyone else uses the tiring
factor (1 durable to 9 fragile; relievers 5): thresholds `80, 100, 115` shifted by `5 × (5 − factor)`. See
[ModelingStatisticsProbability.md](ModelingStatisticsProbability.md) section 6 for why the tiring factor alone could not tell
starters apart.

### The bullpen

Relievers and the closer start resting. A manager tells one to **warm up**; he warms by one batter for each batter his team's pitcher
faces. His readiness by batters warmed: 0 → ×0.70, 1 → ×0.78, 2 → ×0.86, 3 → ×0.93, **4 to 10 → ×1.00 (ready)**, 11 → ×0.97, 12 → ×0.93,
13 → ×0.87, 14 → ×0.80, more → ×0.72 (overwarmed). That value is his **entry modifier** for his first three batters. A pitcher who
leaves is out of the game for good. A **rain delay** cools every arm (warm-ups lost, an entry modifier of 0.88 for the next three
batters).

### Pitching changes

`ChangePitcher` replaces the pitcher (and, without a designated hitter, puts the reliever into the batting order in his spot). A
`DoubleSwitch` (no designated hitter only) brings in the reliever in a fielder's spot and a bench player into the pitcher's old
spot. `EnforcePitcherAfterPinchHit` replaces a pitcher whose spot a pinch hitter took.

## 11. Lineups, the designated hitter and substitutions

* **Designated hitter** (`GameOptions.DesignatedHitter`): `DesignatedHitter` (every game, the default), `Historical` (the home team's
  league and year decide: none in the National League before 2022 or the American League before 1973) or `PitchersBat`. Both teams
  play by the one rule. With a designated hitter a lineup needs one of each fielding position plus DH; without one the starting
  pitcher bats in the DH's place.
* **Lineup rules** (`LineupRules`): nine slots, each required position exactly once; the default order is repaired if a position is
  missing or duplicated; `Validate` returns the problems for a hand-built lineup.
* **Substitutions** (all throw `InvalidOperationException` with a message the player can read when illegal): `PinchHit` (batting
  side), `PinchRun` (a base 1 to 3), `Replace` (a fielder, not the batter or a runner), `SwapPositions` (two lineup players trade
  positions; never the pitcher), `ChangePitcher`, `DoubleSwitch`. **A player who leaves cannot return.** A replacement takes the
  batting slot and position of the player he replaces; a computer side then moves players to where they field best
  (`BestPositionSwaps`). A pinch hitter for the pitcher forces a reliever, and is refused if the bullpen is empty.
* **A pitcher batting** uses his own batting line (a pitcher bats .150 by default and can bunt: bunting rating at least 4).

## 12. Random events

Three optional events (`GameOptions`: injuries, rain delays, ejections), each switchable, rolled from a separate seeded generator
(`eventRng`) so they never disturb the rolls of the game. Frequencies are set so each appears in roughly one game in ten
(`RandomEvents`):

| Event | Chance | What happens |
|---|---|---|
| Injury | 0.12% after any play | one player on the field is hurt: batter, a runner, the pitcher (more likely when tired: weight `1 + pitches/60`), or a fielder (weight 0.35 each). The side must replace him before play continues (`PendingInjury`); a computer side does so at once. A pitcher is replaced by a reliever, others by a bench player; a hurt runner or batter is replaced in place. |
| Rain delay | 0.6% at the start of each half-inning after the first | 20 to 99 minutes. In an official game (five innings, or 4½ if the home team leads) with a leader, 30% end the game. A tie is never called. Otherwise play resumes with cold arms (section 10). |
| Ejection | 0.35% on a strikeout, caught stealing or walk | the manager of the side that argued is ejected (batting side on a strikeout or caught stealing, fielding side on a walk). A human manager is replaced by the computer for the rest of the game. |

The events a game produces are available through `TakeEvents` (display only; nothing about them is saved), and moments for the
ballpark organ (the seventh-inning stretch, a rally, a pitching change, an ejection) through `TakeMoments`.

## 13. Winning, ending and the records

* **A game ends** on a walk-off, after the top of the ninth or later with the home team ahead, after any complete inning from the
  ninth with the score not tied, or when a rain delay ends it. It never ends tied.
* **The box score** (`BoxScoreRecorder`) is kept as the game is played. Batters: AB, R, H, 2B, 3B, HR, RBI, BB, SO, SB, CS, HBP, SH.
  A sacrifice is a plate appearance but not an at-bat. A fielder's choice or an error is an at-bat, not a hit; an error and a
  double play carry no RBI. Pitchers: outs, H, R, ER, BB, SO, HR, batters faced, pitches. Teams: errors (with who), left on base,
  double plays (any play with two or more outs, a strikeout that is also a caught stealing included). Steals are credited from the
  runner moves, so a double steal credits each runner.
* **Runs and earned runs.** Runs go to the lead runners first. A runner is charged to the pitcher who let him on. A run is
  **unearned** after an error in the half-inning (a simplification: no reconstruction of the inning).
* **Decisions.** The win goes to the pitcher of the side that took the lead it never gave up (`GoAhead`); a starter needs five innings
  (15 outs) to get it, otherwise the best reliever after him does. The loss goes to the pitcher who gave up the winning lead. A
  **save** goes to the finisher who is not the winner or starter, pitched at least an inning, and entered with a lead of one to
  three runs (or any larger lead for three or more innings). A game ended by rain with the score tied has no decisions.

## 14. Win probability and the recap

`WinProbability.HomeWins(inning, top, outs, bases, margin)` is the home team's chance of winning from any situation: it works out
the odds of every possible score for the rest of the game. It uses **league-average** scoring, not the two teams' strengths, so it
measures how much a play changed the game rather than who is better.

* A half-inning's runs follow a distribution built from the base and out state: the standard run expectancy table (RE24, bases
  empty with no out 0.50 runs up to bases loaded 2.30) for the average, and the chance of scoring at least once for the
  scoreless part, with a geometric tail; a fresh half-inning averages 0.49 runs.
* The game is played out to the ninth plus four extra innings; the home team wins without batting if it leads after the top of the
  ninth or later. Anything still level is called even. A table is filled on first use (`HomeWinsCached`) for the managers, which
  price many situations a call.

`GameRecaps` records the home team's win probability after every play (`RecapPlay`: the score, the probability, the swing) and
builds the recap: a headline, the track, the **key plays** (the five largest swings of at least 4 points), a written story, and the
performers. The track starts at 0.5 before the first pitch.

## 15. Saving and resuming

`GameSnapshots.Capture(state)` turns a game into a `GameSnapshot`: plain data (player ids and numbers, no objects) small enough
for browser storage, with a description for the resume prompt. Teams and ballparks are referenced by id and must be loaded again;
`Restore` rebuilds the state and throws `InvalidOperationException` if a snapshot no longer fits the rosters. The snapshot holds
the score by inning, inning, outs, runners (with their batting slot and the pitcher charged), lineups and positions, batter
indices, both pitchers (pitch count, batters faced, settle point, runs, entry modifier, condition), bullpens (warming and batters
warmed), used players, alignments, the newest 200 play-by-play entries, the box score, the recap, the options, the manager ids, and
the flags (an error in the inning, ejections, rain, pending injury). The designated hitter rule is not stored: it is worked out
again from the options and the home team. The box score in a captured snapshot is shared with the live game, so a snapshot is
serialized (`ToJson`) as soon as it is taken.

* The format has a **version** (`GameSnapshot.CurrentVersion`, now 2). A snapshot of another version is rejected (read as no save),
  not migrated.
* Persona ids are stored as the game's manager ids and must never change once released (`ManagerCatalog`).
* The game does not save the random generators: a resumed game continues with fresh randomness, so it is not a replay.

## 16. Determinism and random numbers

| Generator | Seeded by | Used for |
|---|---|---|
| The resolver's (`AtBatResolver(new Random(seed))`) | the caller | every roll of a play: outcome, error, extra bases, steals, bunts, double plays, pitch counts |
| `eventRng` (`GameService` constructor) | the caller | injuries, rain, ejections |
| Each side's persona manager | `managerSeed` (+0 visitor, +1 home) | the computer managers' tie-breaks and mixed choices |
| The animation | a seed per play | where the ball lands, how a throw goes; never touches the above |

With every generator seeded the game is exactly repeatable, which is what the benchmark and many tests depend on. Without seeds
the shared generator is used, as a real game should. **Order of rolls matters:** a change that adds a roll to a play (even one that
usually does nothing) shifts every later roll of a seeded game; the engine takes care to roll only when a roll is needed
(`Advance`'s ternaries, `GroundOut`'s infield-in roll), and tests that seed the generator depend on it. One known exception:
`BuildWalkResult` (an intentional walk) passes `Random.Shared` into `Advance`, which uses no randomness for a walk.

## 17. From a result to an animation

The animation is **display only**: the result is decided first, and the animation illustrates it. It never feeds back into the
game or into the simulation's random numbers.

`PlayChoreographer.Build(PrePlaySnapshot before, PlayResult result, int seed)` returns a `PlayScript`: plain data in ground-plane
feet (`FieldGeometry`), with **tracks** (an actor's keyframes: position, height, pose, easing) and **cues** (sounds, the banner,
the camera). It is pure: the same situation, result and seed give the same script. The `Staging` partial classes
(`Staging.Hits`, `.Outs`, `.Bunts`, `.Steals` and the common pieces in `PlayChoreographer`) choose the plausible details the result
does not fix (where the ball lands, who fields it, how the throw goes) so that they **agree with the result**: who made the out,
who scored, who was thrown out, which base each runner ended on. `SceneChoreographer` builds the scenes around the plays (the start of
the game, a new half-inning, a new pitcher or pinch hitter, a mound visit, a walk-off, the final score) from two snapshots.
`RunnerMoveBuilder` derives each runner's move from the pre-play position and the result (no randomness) so that the picture, the
box score's steals and the result's bases cannot disagree.

The browser's canvas animator (`wwwroot/js/animator.js`) plays a script back; the spec is [AnimatedPlays.md](AnimatedPlays.md).

## 18. What the engine does not model

These are deliberate gaps, not bugs. Where one affects a calibration result it is noted in
[ModelingStatisticsProbability.md](ModelingStatisticsProbability.md).

* **Individual pitches.** No balls, strikes or counts; a pitch estimate only drives fatigue.
* **Wild pitches, passed balls, balks, pickoffs, errors on throws, stolen bases that advance on an overthrow.** The enumeration lists
  `WildPitch`, `PassedBall`, `Balk` and `TriplePlay`, but no rule produces them.
* **Fielder's choices on an ordinary swing** (only bunts have them); a ground out always retires the batter, unless it is a double play.
* **Sacrifice flies as such** (a fly out with a run is a fly out that scores; the box score counts no SF), **hit-by-pitch effects**,
  **ground-rule doubles**, **infield hits**, **errors on a throw to a base**.
* **Extra-inning rules** such as a runner placed on second: extra innings are played straight until the tie is broken.
* **Weather, wind, day and night, fatigue of fielders and hitters, and streaks.**
* **Per-pitcher ground-ball rates, hold, and per-hitter platoon splits:** no data. Everyone gets the default or the standard modifier.
* **Scoring a runner from second on a single or from first on a double by a fielder's play** beyond the speed check and the arm
  penalty; no relay model.
* **Park effects on anything but the batting table** (the animation uses the dimensions).
* **Manager habits of a real era** (open problem in the modeling document): the managers are modern and expected-run driven.

## 19. Calibration status

The engine has been checked against each team's real season (runs, hits, home runs, walks, strikeouts, steals, double plays,
sacrifices, complete games, starter length), by playing every team against every other and comparing per game. The method, the
findings and what works are in [ModelingStatisticsProbability.md](ModelingStatisticsProbability.md); the current picture
(2026-11-25, analyst manager, Historical designated-hitter rule, starters in rotation):

| Measure (simulated ÷ real) | Ratio |
|---|--:|
| Runs | 0.91 (about what a perfect model scores here: every opponent is a pennant winner) |
| Hits, doubles, home runs | 1.02, 1.02, 1.05 |
| Strikeouts, walks | 1.05, 0.83 |
| Errors | 0.96 |
| Starter outs per start, pitchers used a game | 0.94, 0.96 |
| Double plays | 0.89 |
| Steals, caught stealing | 1.25, 0.97 |
| Sacrifices, complete games | 0.73, 0.77 |

The corrections that made this possible, in the engine: pitchers scaled against their own league; hits and home runs by their own
rates; each pitch balanced by run value; the double-play base 0.45 → 0.52; the steal success steadied toward the league's rate
with a lighter record weight; stamina from a starter's real starts. What is left is mostly **manager behavior** (steal attempts,
sacrifices, complete games follow the managers' habits, not the engine's) and the roster the team files carry (walks; the files hold only each staff's best arms).

## 20. Tests

`tests/MiniLeague.Engine.Tests` (xUnit) covers the engine; `tests/MiniLeague.Web.Tests` (bUnit) the screens that use it. The files
that matter for the rules above:

| Area | Files |
|---|---|
| The loop: innings, walk-offs, extra innings, control, resume | `GameFlowTests`, `StartingPitcherTests` |
| One play: outcomes, base running, tag-ups, errors | `AtBatResolverTests`, `FieldingAndParkTests`, `OutfieldDepthTests` |
| Bunts, squeezes, hit and run, steals of third, double steals | `BuntAndHitAndRunTests`, `PlayFactsTests` |
| No free option: alignments, pitchout, pitches | `NoFreeOptionTests`, `PitchBalanceTests` |
| Pitcher scaling, pitch estimate, stamina, steal calibration | `LeaguePitchingTests`, `PitchEstimateTests`, `StaminaTests`, `StealCalibrationTests`, `CalibrationRotationTests` |
| Lineups, substitutions, the designated hitter, a pitcher batting | `LineupAndSubstitutionTests`, `PitchersBatTests` |
| Random events | `RandomEventTests` |
| The box score and decisions | `BoxScoreTests` |
| Win probability and the recap | `WinProbabilityAndRecapTests`, `GameMomentTests` |
| Team data | `TeamLoaderTests` |
| Animation: scripts agree with results | `AnimationTests`, `PlayTemplateTests`, `RouteTests`, `SceneTests`, `EventSceneTests`, `PitchStyleTests`, `StanceTests` |
| Computer managers and the benchmark | `ManagerModelTests`, `ManagerCatalogTests`, `ManagerDialsTests`, `ComputerManagerDecisionTests`, `ManagerBenchmarkTests` |

Beyond unit tests: the **manager benchmark** (`dotnet run -c Release --project tools/MiniLeague.ManagerBench -- --games 20 --check`)
guards persona behavior, and **calibration** (`-- --calibrate`) compares the engine with real seasons; both are seeded and exact.
Run them after any change to the rules in sections 6 to 10.

## 21. Changing the engine

* **A rule or a number** that both the resolver and the managers use belongs in `RunningRules` (or the shared function), not in two
  places. Change it there and both follow.
* **Keep an average player average.** A change must leave a neutral batter against a neutral pitcher with his own season line;
  `LeaguePitchingTests` checks it for the pitcher scaling.
* **No free options.** A new call (an alignment, a pitch, a strategy) must cost something where it helps; add it to the no-free-option
  tests.
* **Mind the order of rolls** (section 16). If a new rule rolls, roll only when it applies.
* **Display-only fields stay display-only** (`RunnerMoves`, `Fielder`, `Pitch`, the animation); never let a rule read them.
* **The engine stays plain:** no package or project references, nothing about the UI, the browser or JavaScript.
* **After a change to the table, base running, fatigue or the team data**, re-run the benchmark and the calibration, update
  [ModelingStatisticsProbability.md](ModelingStatisticsProbability.md) and this document, and re-baseline `expected-ranges.json`
  only for a change that is meant to move it.
* **A new team-file field** needs a default in `TeamLoader` so older files still load, a line in `tools/generate_teams.py`, and a
  test that every shipped team carries it.
* **A new save-file field** needs a snapshot version decision (section 15).

## 22. File map

```
src/MiniLeague.Engine/
  Models/
    GameState.cs         state, enums (PitchType, alignment, strategy), ActivePitcher, WarmingPitcher, BaseRunner
    AtBatDecision.cs     OffensiveDecision, DefensiveDecision, StealPlan, SqueezeKind, DecisionExplanation
    PlayResult.cs        OutcomeType, PlayResult, RunnerMove, RunnerFate
    Player.cs, BatterStats.cs, PitcherStats.cs, Fielding.cs, Team.cs, LeaguePitching.cs, Stadium.cs
    Lineup.cs            lineup rules; GameOptions.cs: options, designated hitter rule
    BoxScore.cs, GameEvent.cs, GameMoment.cs, PrePlaySnapshot.cs, RecapPlay.cs
  Simulation/
    AtBatResolver.cs     one play (sections 5 to 8)
    ProbabilityTable.cs  the outcome table (section 6)
    BaseRunningEngine.cs, RunningRules.cs     runners, steals, double plays, shared constants
    BuntModel.cs, DefenseModel.cs, FatigueModel.cs, WinProbability.cs, RandomEvents.cs, RunnerMoveBuilder.cs
  Services/
    GameService.cs       the loop, substitutions, computer sides, random events
    BoxScoreRecorder.cs, BoxScoreFormatter.cs, GameRecap.cs, GameSnapshot.cs
    TeamLoader.cs, StadiumLoader.cs
  AI/                    the computer managers (ComputerManagers.md)
  Animation/             PlayChoreographer, Staging.*.cs, SceneChoreographer, PlayScript, FieldGeometry, Stances, PitchStyle
tools/
  MiniLeague.ManagerBench/   benchmark and calibration; lahman-reference.json, expected-ranges.json
  generate_teams.py, export_lahman_reference.py
```
