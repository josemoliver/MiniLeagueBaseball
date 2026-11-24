# Computer managers

The computer can manage either side of a game, or both. A player chooses **which** computer manager runs each side
on the setup screen. This document specifies how that works: the managers, the personas the player meets, the dials
that make them differ, how a choice is saved, and how to add another.

The five personas are fictional. Any resemblance to a real manager is accidental.

## Contents

1. [Overview](#1-overview)
2. [The personas](#2-the-personas)
3. [Architecture](#3-architecture)
4. [How the expected-run managers decide](#4-how-the-expected-run-managers-decide)
5. [The tuning dials](#5-the-tuning-dials)
6. [The old-school manager](#6-the-old-school-manager)
7. [Choosing a manager: UI and saved games](#7-choosing-a-manager-ui-and-saved-games)
8. [Adding a manager](#8-adding-a-manager)
9. [Measured results](#9-measured-results)
10. [Known limits and engine quirks](#10-known-limits-and-engine-quirks)
11. [Tests](#11-tests)
12. [File map](#12-file-map)

---

## 1. Overview

A computer manager answers four questions for the side it runs:

| Question | When | Method |
|---|---|---|
| What does the **batting** side do? (swing away, steal, bunt, hit and run, pinch hit, pinch run) | Before each plate appearance | `MakeOffensiveDecision` |
| What does the **fielding** side do? (pitch type, defensive alignment, intentional walk) | Before each plate appearance | `MakeDefensiveDecision` |
| Who is **pitching**? (start a reliever warming, or make a change) | After each play, for the side that fields next | `DecidePitching` |
| Who **replaces** an injured player, and how are fielders re-positioned after a substitution? | When it happens | `ChooseInjuryReplacement`, `BestPositionSwaps` |

Every side of every game has its own manager. A game between two computer-managed sides can pit two different
personas against each other, and the same persona can manage both sides.

There are two kinds of manager:

* **Expected-run managers** (`ComputerManager`). They price each legal call in expected runs, using the same outcome
  table that resolves the play, and make a call only when it beats the ordinary one by a margin. One class, many
  temperaments: the personas differ only by their [tuning dials](#5-the-tuning-dials).
* **The old-school manager** (`OldSchoolManager`). The original rule-based manager, kept as a choice of its own.

## 2. The personas

| Id | Name | Nickname | Style | Manager |
|---|---|---|---|---|
| `analyst` (default) | Ellie Whitcombe | The Actuary | By the numbers | `ComputerManager`, default tuning |
| `oldschool` | Gus Halloran | Ironhide | The book | `OldSchoolManager` |
| `smallball` | Rosa Delgado | Spark | One run at a time | `ComputerManager`, small-ball tuning |
| `slugger` | Duke Pruitt | Big Sky | Let them swing | `ComputerManager`, hands-off tuning |
| `tinkerer` | Marcus Delacroix | The Tinkerer | Matchups, always | `ComputerManager`, busy-bench tuning |

The id is what a saved game stores. It must never change once released.

### Profile ratings

Each persona carries 1 to 5 ratings, shown on the setup screen as pips. They describe how much the manager leans on
each part of the game. They are written by hand, not computed, so a test keeps them honest about the direction of the
differences (see [Tests](#11-tests)).

| Persona | Aggression on the bases | Small ball | Bench use | Quick with the bullpen | Intentional walks |
|---|:-:|:-:|:-:|:-:|:-:|
| Ellie Whitcombe | 3 | 2 | 3 | 3 | 2 |
| Gus Halloran | 3 | 4 | 3 | 3 | 5 |
| Rosa Delgado | 5 | 5 | 4 | 3 | 2 |
| Duke Pruitt | 2 | 1 | 1 | 1 | 1 |
| Marcus Delacroix | 3 | 2 | 5 | 5 | 4 |

### Ellie Whitcombe, "The Actuary" (`analyst`)

> Prices every call in expected runs and only makes the ones that clearly pay.

A former insurance actuary who took over a last-place club with a spreadsheet and a stopwatch. She will not bunt,
steal or walk anyone unless the arithmetic says so, and the arithmetic usually says no.

**In play.** The reference manager and the default for any side that has not been given a choice. Every dial is at its
default. Per game she makes few bunts (about 0.2), almost no steals, some hit and runs, and almost never an
intentional walk.

### Gus Halloran, "Ironhide" (`oldschool`)

> Plays it the way it has always been played: set rules, no lookahead, a bunt when the book says bunt.

Thirty-one years in the game, nineteen of them in the dugout, and he has never read a spreadsheet. He walks the big
bat with first base open, bunts with the weak hitter late, and sends a fast runner on a hunch.

**In play.** Follows the fixed rules in [section 6](#6-the-old-school-manager). Walks power hitters often (about 1.7
intentional walks a game), steals more than the others, and picks pitches at random. He is the weakest of the five by
a wide margin, which makes him the natural "easy" opponent.

### Rosa Delgado, "Spark" (`smallball`)

> Bunts, steals and hit-and-runs to manufacture runs, and sends in the fast pinch runner early.

A scrappy utility infielder who never hit over .240 and made a career of moving runners. She manages the way she
played: bother the pitcher, take the extra base, and play for the one run.

**In play.** Low bars for steals (0.010), hit and runs (0.010) and sacrifice bunts (0.0), and for aggressive running
(0.0), so she makes those calls whenever they look even slightly better. A low pinch-run bar (0.02) puts a fast runner
in early. On defense she calls a pitchout 45% of the time when a runner is likely to go.

### Duke Pruitt, "Big Sky" (`slugger`)

> Never gives up an out on purpose, trusts his starter and lets his hitters hit.

A power-hitting first baseman who watched managers waste his at-bats on bunts. He keeps the bench for emergencies,
leaves a good starter in, and would rather lose swinging than win bunting.

**In play.** Steals, hit and runs and bunts are switched off (`ManagerTuning.Never`, a margin no call can reach). He
pinch hits only from the seventh inning and only for a large gain, charges a high price for spending a bench player
(1.6 times the normal cost), and walks a batter only on a large edge (0.10). He keeps a starter longer than anyone
(bullpen margins of 0.07 and 0.14, warm-up not before the eighth inning or 90 pitches).

### Marcus Delacroix, "The Tinkerer" (`tinkerer`)

> Changes pitchers and pinch hitters at the first sign of a better matchup and walks hitters to get one.

A pitching coach turned manager who keeps a binder on every hitter in the league. His bullpen door never stops
swinging, his bench plays every night, and he will walk a star to face a lesser bat.

**In play.** Pinch hitting from the fifth inning for a small gain (0.012) with a half-price bench. Walks a batter on a
small edge (0.01). Starts a reliever warming from the fourth inning at 45 pitches, and from 60 pitches will swap a
still-fresh starter for a warm reliever who looks at least as good (a margin of -0.02 in a close late game, 0.0
otherwise). The swap needs a warm reliever who is about as good, which an ace starter usually rules out, so this
manager's signature is the matchup change, not a high count of pitchers used.

**Limit today.** The team data has no left/right splits and the engine applies no platoon effect (see
[Known limits](#10-known-limits-and-engine-quirks)). His "matchups" therefore compare only overall season quality,
and in measured play he behaves much like the Analyst. His style needs a platoon effect to show.

## 3. Architecture

```
ManagerCatalog ──holds──► ManagerPersona ──Create(rng)──► ComputerManager
 (the choices)             (id, name, profile)              ├─ ManagerTuning  (dials)
                                                            └─ OldSchoolManager (override)

GameState.VisitorManagerId / HomeManagerId   (saved ids)
GameService.ManagerFor(visitor)              (one manager per side, built from the id)
```

* **`ManagerTuning`** (`AI/ManagerTuning.cs`). An immutable record of the dials. Defaults are the Analyst's.
* **`ManagerPersona`** (`AI/ManagerPersona.cs`). Everything a player sees (name, nickname, style, tagline, biography,
  trait ratings) plus `Create`, the function that builds the manager from a `Random`.
* **`ManagerCatalog`** (`AI/ManagerCatalog.cs`). The list of personas. `BuiltIn` holds the five that ship, `Default` is
  the instance the app uses, `Find`, `Get` (falls back to the default for an unknown id), `Create`, and `Register`
  (rejects a duplicate id).
* **`ComputerManager`** (`AI/ComputerManager.cs`). The expected-run manager. `MakeOffensiveDecision`,
  `MakeDefensiveDecision` and `DecidePitching` are `virtual`; injury replacement and position swaps are shared by every
  manager.
* **`OldSchoolManager`** (`AI/OldSchoolManager.cs`). Overrides the three decision methods with the original rules.
* **`ManagerDecisionModel`** (`AI/ManagerDecisionModel.cs`). The internal valuation used by the expected-run managers.
* **`GameService`**. `ManagerFor(bool visitor)` returns the manager for a side, building it from the state's id on
  first use and rebuilding it if the id changes (a new or resumed game). With no id, or no catalog, it falls back to the
  single manager the service was constructed with, so existing callers and tests behave as before.
  `GetComputerDefense(plannedBatter)` and `PersonaFor(visitor)` expose the fielding decision and the persona to the UI.

The engine stays a plain class library: nothing here references the UI, the browser or a package.

### Routing of decisions

| Call | Manager used |
|---|---|
| Offensive decision | The **batting** side's manager |
| Defensive decision | The **fielding** side's manager |
| Pitching change | The manager of the side that fields next (the one whose pitcher is asked about) |
| Injury replacement | The manager of the side with the injury |
| Position swaps after a substitution | The manager of the side that substituted |

When a human batting manager announces a pinch hitter, the computer defense answers **that** batter, not the one he
replaces (`plannedBatter`).

## 4. How the expected-run managers decide

### 4.1 One-play valuation

`ManagerDecisionModel.AtBat` builds the outcome weights for a matchup with `ProbabilityTable.Build` (the same builder
the resolver rolls on), so a choice depends on this batter's and this pitcher's season statistics, the pitcher's fatigue,
the ballpark and the alignment. The code asks for each player's statistics against the other's throwing or batting hand,
but the team data has no splits, so the overall season line is used for both hands. The batter's side changes only the
ballpark's home-run factor. It then turns each outcome into the value of the situation that follows, using a run-expectancy table
(`WinProbability.ExpectedRuns`) for the base and out state. The value is expected runs for the **batting** side: the
batting manager maximizes it, the fielding manager minimizes it.

A late tied or one-run-behind situation (the eighth inning or later) blends run expectancy with the chance of scoring at
least once: `0.35 * expected + 0.65 * (1 - e^-expected)`. A single run is worth more than the second one there.

The model never rolls dice and never changes the game. Its base-running transitions mirror the resolver's rules in
expectation; they do not need to predict a single roll, only to compare two calls fairly.

### 4.2 Batting side

In order:

1. **Pinch runner.** From the eighth inning, in a one-run-or-tied game, with fewer than two out, for the lead runner
   (the one on second, else first). Gain is the speed difference times 0.055 (runner on second) or 0.043 (on first),
   minus the bat lost, the fielding lost and a reserve cost. Made only if the net gain beats `PinchRunGain`.
2. **Strategy** for the batter due up:
   * Compare **aggressive running** with a plain swing; take it if it beats it by `AggressiveRunMargin`.
   * With a runner on first only, fewer than two out, and not already ahead late: a **steal** if it beats staying put by
     `StealMargin` (and it is the sixth inning or later, or the batting side is not ahead); otherwise a **hit and run**
     for a contact hitter if it beats the best swing by `HitAndRunMargin`.
   * With a runner on first or second, no one on third, and nobody out: a **sacrifice bunt** if it beats the best swing
     by `BuntMargin`, except when the side is down two or more from the seventh inning on.
3. **Pinch hitter.** From `PinchHitFromInning`, when the batting side leads by at most 2 or trails by at most 4. Each
   bench bat with 100 or more at-bats is valued against the man due up: the matchup gain, plus 20% of the OPS gap, minus
   the fielding lost (at the position he would take), a reserve cost (more if the bench is thin) and a cost for losing a
   bat for later, the last two scaled by `BenchReserveScale`. A catcher is replaced only by a natural catcher. Made only
   if the net gain beats `PinchHitGain`.
4. **Re-evaluation.** A steal is decided before a pinch hitter so a pinch hitter is not spent on a play that has no
   plate appearance. After a pinch hitter is chosen, the strategy is reconsidered for the new batter.

### 4.3 Fielding side

1. **Intentional walk.** Only with first base open, fewer than two out, a runner in scoring position, the seventh inning
   or later, within two runs, a batter with OPS of at least .800 who is at least .150 better than the next hitter. It is
   priced as a forced runner plus a bounded credit for the weaker on-deck bat (at most 0.35), and made if it beats
   facing the batter by `IntentionalWalkMargin`.
2. **Pitch and alignment.** Every available alignment is evaluated with each of the four ordinary pitches and the lowest
   value wins. Ties within 0.004 are broken at random (20% chance to switch) so the choice is not predictable.
   * Always available: Standard.
   * Infield In: runner on third, fewer than two out.
   * Corners In: runner on first or second, fewer than two out, a batter who bunts well (bunting or sacrifice rated 4
     or more). Today every player has the default rating of 3, so Corners In is never offered.
   * Guard Lines: the seventh inning or later, within two runs.
   * Shift Pull when the batter's power rating is 3 or more, Shift Opposite when it is 2 or less. There are no spray
     charts, so power is only a rough clue and Standard stays available and wins ties.
3. **Pitchout.** Considered only with a runner on first, second empty, fewer than two out, and within two runs. A pitchout
   is **priced as a wasted ball** (`PitchoutBallCost` = 0.05 runs), not as a plate appearance. The runner is assumed to
   go with probability 0.5 if the computer offense's own steal rule would send him, else 0.05
   (`ManagerDecisionModel.RunnerGoChance`). It is called only when it beats the normal pitch by 0.02, and then only
   `PitchoutShare` of the time, so a human or computer runner can still succeed.

### 4.4 Bullpen

Run after each play for the side that fields next.

* **Closer.** A closer is held for a save (the ninth inning with a lead of 1 to 3) and valued with a 0.20 penalty outside
  one, and a 0.08 bonus inside one.
* **Candidates.** Each warm reliever is valued against the next three hitters, using his best pitch against each, with
  his warm-up modifier. The same value is computed for the current pitcher with his fatigue.
* **A change** is made when there is a warm reliever and any of these holds: a closer is ready in a save spot; the
  pitcher is spent (Tired or worse once settled) or **shelled** (at least five batters faced, runs allowed at least 5 for
  a starter or 3 for a reliever, and at least 0.18 runs per batter); he is Tiring and the reliever is not worse by more
  than 0.08; or he is Ok (or past `QuickHookPitchCount`) and the reliever beats him by `ReliefEdgeClose` in a close late
  game or `ReliefEdgeOther` otherwise.
* **Emergency.** A spent or shelled pitcher with nobody warm is replaced by the best arm available, cold or not.
* **Warming.** If nobody is already warming (an over-warmed arm does not count), start the closer in the eighth inning
  with a save in sight, or start the best other reliever once the pitcher is no longer fresh, is shelled, or (in a close
  game from `WarmUpFromInning`) has thrown `WarmUpPitchCount` pitches.

## 5. The tuning dials

`ManagerTuning` (`AI/ManagerTuning.cs`). Every margin is in **expected runs**: how much better than the ordinary call a
move must look before the manager makes it. A **lower** number means the manager acts more readily. `Never` (9.0) means
no call can clear the bar.

| Dial | Default (Analyst) | What it controls |
|---|--:|---|
| `StealMargin` | 0.035 | Edge needed over staying put to call a steal |
| `HitAndRunMargin` | 0.035 | Edge needed over a plain swing to call a hit and run (contact hitters only) |
| `BuntMargin` | 0.025 | Edge needed over swinging to call a sacrifice bunt |
| `AggressiveRunMargin` | 0.005 | Edge needed over ordinary running to run aggressively |
| `PinchHitFromInning` | 6 | First inning a pinch hitter is considered |
| `PinchHitGain` | 0.025 | Net gain a pinch hitter must show |
| `BenchReserveScale` | 1.0 | Multiplier on the cost of spending a bench player; above 1 hoards the bench |
| `PinchRunGain` | 0.05 | Net gain a pinch runner must show |
| `IntentionalWalkMargin` | 0.04 | Edge needed over facing the batter for an intentional walk |
| `ReliefEdgeClose` | 0.035 | Edge a reliever needs over a tiring pitcher in a close late game |
| `ReliefEdgeOther` | 0.085 | The same bar earlier, or when the game is not close |
| `WarmUpPitchCount` | 65 | Pitches after which a reliever is warmed early in a close game |
| `WarmUpFromInning` | 7 | First inning that early warm-up applies |
| `QuickHookPitchCount` | `int.MaxValue` | Pitches after which a still-fresh pitcher may be swapped for a warm reliever |
| `PitchoutShare` | 0.30 | Share of the time a favorable pitchout is actually called |

### Values per persona

Blank means the default. `OldSchoolManager` has no dials; see [section 6](#6-the-old-school-manager).

| Dial | Analyst | Small ball | Slugger | Tinkerer |
|---|--:|--:|--:|--:|
| `StealMargin` | 0.035 | **0.010** | **Never** | 0.035 |
| `HitAndRunMargin` | 0.035 | **0.010** | **Never** | 0.035 |
| `BuntMargin` | 0.025 | **0.0** | **Never** | 0.025 |
| `AggressiveRunMargin` | 0.005 | **0.0** | **0.012** | 0.005 |
| `PinchHitFromInning` | 6 | 6 | **7** | **5** |
| `PinchHitGain` | 0.025 | 0.025 | **0.05** | **0.012** |
| `BenchReserveScale` | 1.0 | 1.0 | **1.6** | **0.5** |
| `PinchRunGain` | 0.05 | **0.02** | **0.10** | 0.05 |
| `IntentionalWalkMargin` | 0.04 | 0.04 | **0.10** | **0.01** |
| `ReliefEdgeClose` | 0.035 | 0.035 | **0.07** | **-0.02** |
| `ReliefEdgeOther` | 0.085 | 0.085 | **0.14** | **0.0** |
| `WarmUpPitchCount` | 65 | 65 | **90** | **45** |
| `WarmUpFromInning` | 7 | 7 | **8** | **4** |
| `QuickHookPitchCount` | off | off | off | **60** |
| `PitchoutShare` | 0.30 | **0.45** | **0.15** | 0.30 |

## 6. The old-school manager

`OldSchoolManager` is the original rule-based manager. It never prices a call and never looks at the matchup. It decides
from fixed rules. Injury replacement and position swaps are inherited from `ComputerManager`.

**Batting**

1. Sacrifice bunt: no designated hitter, the pitcher batting, a runner on first or second, nobody out. (This rule never
   fires today: lineups hold nine position players and pitchers never bat.)
2. Sacrifice bunt: within one run, the seventh inning or later, nobody out, a runner on first or second and none on
   third, and a weak hitter (fewer than 8 home runs and a batting average under .240).
3. Steal: a runner of speed 4 or more on first, second empty, within one run, fewer than two out, 40% of the time.
4. Hit and run: a runner on first, second empty, fewer than two out, a contact hitter, the seventh inning or later,
   within one run, 25% of the time.
5. Otherwise aggressive running in a close late game, ordinary swinging at any other time.
6. Pinch runner: the eighth inning or later, tied or one behind, fewer than two out, for a runner of speed 2 or less,
   with the fastest bench player who is at least 2 faster.
7. Pinch hitter: the seventh inning or later, within one run ahead or three behind, for a bench bat (100 or more
   at-bats) whose OPS is at least .080 better; never for a catcher unless the bench has another catcher.

**Fielding**

* Intentional walk: first base open, fewer than two out, a batter with 25 or more home runs, 50% of the time.
* Infield in: a runner on third, fewer than two out, within two runs. Corners in: a runner on first, nobody out, a batter
  who bunts well (never true today, since every player has the default bunting rating). Otherwise Standard. No shifts.
* Pitch: random, weighted toward the fastball (fastball twice, then curveball, slider, changeup once each).

**Pitching**

Fatigue-driven. Warm a reliever once the pitcher is no longer fresh; change when he is Tiring or worse once settled, or
shelled (three batters faced, 5 runs for a starter or 3 for a reliever); hand a save to the closer in the ninth with a
lead of 1 to 3. Reliever choice is the warm arm with the lowest ERA, and the closer only in a save spot.

When a human announces a pinch hitter, his defense also answers the incoming batter.

## 7. Choosing a manager: UI and saved games

### Setup screen

For each side the **computer** manages, the setup card shows a chooser below "Game options": a select listing every
persona by "Name, Nickname", and under it the chosen persona's style, tagline, the five profile ratings as pips (with an
accessible label such as "3 out of 5") and a collapsible **Background**. The choosers appear only for computer sides:

| "You manage" | Choosers shown |
|---|---|
| Visitors | Home |
| Home team | Visitors |
| Both teams | None |
| Neither | Visitors and Home |

`ManagerChooser.razor` is the component; `GameRoom.razor` holds the selection (`_visitorManagerId`,
`_homeManagerId`, both defaulting to `ManagerCatalog.DefaultId`) and passes it to `GameService.StartGame`.

### In the game

While the game waits for a computer decision, the message names the manager, for example "Ellie Whitcombe is deciding…".
When both sides are computer-managed it names the batting side's manager.

### Saved games

The choice is saved with the game (`GameSnapshot.VisitorManagerId` and `HomeManagerId`, copied to and from
`GameState`), so a resumed game keeps the same personalities.

* A save from before the choice existed has no ids. Both sides then use the default manager.
* An id the catalog no longer knows (a save from a newer version, say) falls back to the default instead of failing.
* If a human loses control of a side (an ejection hands it to the computer), that side's saved persona takes over.

## 8. Adding a manager

Nothing outside the catalog has to change; the setup screen lists whatever the catalog holds.

**A new temperament.** Add a `ManagerPersona` to `ManagerCatalog.BuiltIn` and build a `ComputerManager` with your own
tuning:

```csharp
new ManagerPersona(
    Id: "gambler",                       // saved with games: never change it once released
    Name: "Jo Quill",
    Nickname: "The Gambler",
    Style: "Roll the dice",
    Tagline: "Runs on everything and bunts on nothing.",
    Biography: "A short fictional background that explains the style.",
    Traits:
    [
        new("Aggression on the bases", 5), new("Small ball", 2), new("Bench use", 3),
        new("Quick with the bullpen", 3), new("Intentional walks", 1),
    ],
    Create: rng => new ComputerManager(rng, new ManagerTuning
    {
        StealMargin = 0.0, AggressiveRunMargin = 0.0, BuntMargin = ManagerTuning.Never,
    }))
```

**Different logic.** Subclass `ComputerManager` and override `MakeOffensiveDecision`, `MakeDefensiveDecision` and/or
`DecidePitching`, as `OldSchoolManager` does, then return it from `Create`.

**At run time.** `ManagerCatalog.Register(persona)` adds one to a catalog instance and throws if the id is taken.

Checklist for a new persona:

* A unique id and a unique name. A test checks both.
* A biography of more than 40 characters, at least three trait ratings between 1 and 5.
* Trait ratings that match the dials. If you say a manager hoards his bench, the dials should.
* A test that shows the temperament, for example that his side attempts more or fewer of a kind of play than the Analyst.
* Run the round robin (see [section 9](#9-measured-results)) to check that he is neither broken nor dominant.

## 9. Measured results

Measured on 2026-11-24 over whole games between computer-managed sides, all 15 teams (seven fixed pairings each), both
home and visitor seats.

### Expected-run manager against the old rules

3,000 games. Both sides ran on the same engine.

| Setup | New win % (95% CI) | Run margin per game |
|---|---|---|
| Full comparison | 62.6% (60.8 to 64.3) | +1.33 ± 0.15 |
| New manager's shifts switched off | 62.4% (60.7 to 64.2) | +1.29 |
| Old manager's intentional walks off | 58.4% (56.7 to 60.2) | +0.84 |
| New manager's aggressive running off | 59.4% (57.7 to 61.2) | +0.92 |
| Both of the last two off | 55.1% (53.3 to 56.9) | +0.49 ± 0.14 |

Shifts and Guard Lines add nothing measurable. The old manager's intentional walks cost it a lot, and aggressive running
is worth about 0.45 runs a game to the new manager (see [limits](#10-known-limits-and-engine-quirks)). What remains is a
real edge of about 55% from the bunt, hit-and-run, pinch-hitting, bullpen and pitch-choice rules.

### The five personas against each other

About 2,400 games per manager.

| Manager | Overall win rate |
|---|---|
| Rosa Delgado (small ball) | 54.8% |
| Marcus Delacroix (tinkerer) | 53.9% |
| Ellie Whitcombe (analyst) | 53.6% |
| Duke Pruitt (slugger) | 52.2% |
| Gus Halloran (old school) | 35.5% |

The four expected-run personas are within three points of each other, so none dominates. Gus loses to each of them by
about 1.5 runs a game.

### Typical behavior per game, new against old (3,000 games)

| | New (Analyst) | Old school |
|---|--:|--:|
| Sacrifice bunts | 0.17 | 0.02 |
| Hit and runs | 0.29 | 0.07 |
| Steals | 0.00 | 0.30 |
| Aggressive running calls | 17.0 | 5.0 |
| Intentional walks | 0.00 | 1.66 |
| Pitching changes | 1.17 | 1.35 |

## 10. Known limits and engine quirks

* **Aggressive running is free.** In this engine it can take an extra base but cannot be thrown out, so every
  expected-run manager calls it constantly. It accounts for a large share of their edge over Gus. It is a rules flaw,
  not good managing, and a human can use it too. A downside (a chance to be thrown out) would fix it.
* **The Analyst almost never steals.** With ordinary rosters the steal estimate rarely clears the 0.035 bar. The
  Small-ball dials do steal; the steal bar may be worth lowering for the default.
* **A pitchout is a poor ordinary pitch in the pitch table.** The engine resolves a pitchout against a batter who is not
  running as a plate appearance with hits cut to 10% and walks doubled, which makes it look like the best pitch in the
  game. The managers therefore price it as a wasted ball instead (section 4.3), and a human fielder can still exploit the
  table.
* **Guard Lines is never worse for the defense.** It trades singles for extra-base hits at weights that reduce total
  bases, so a manager that evaluates it will choose it whenever it is available (late and close). The data has no spray
  charts, so shifts are only rough clues.
* **Tired pitchers.** A tired pitcher now allows **more** hits (the hit rate is divided by his effectiveness). Before
  this change it was multiplied, which made a tired pitcher harder to hit. This is an engine change that affects every
  game, not only the managers.
* **No platoon effect.** `TeamLoader` gives each player the same statistics against left- and right-handers, and the
  probability table uses the batter's side only for the ballpark's home-run factor. Same-hand and opposite-hand
  matchups are therefore identical, which leaves the Tinkerer's style and reliever matchups with little to work on.
* **Ratings with no data behind them.** Bunting, sacrifice, hold-runners and ground-ball rate are not in the team data,
  so every player has the default. Bunt and steal odds vary only through speed and the steal record, and Corners In is
  never offered.
* **Pitchers never bat.** Lineups are the first nine position players in every era, which amounts to a universal
  designated hitter, and `GameState.DHRule` is never set. Pinch-hitting for the pitcher, double switches and pitcher
  bunts cannot happen, although 11 of the 15 teams played without a DH.
* **Infield In and Corners In.** Infield In cuts singles by 15% and removes the double play, but the runner on third
  still scores on every ground out with fewer than two out. Corners In cuts singles by 20% on every swing, not only on
  bunts. Both are close to free for the defense.
* **Pitch types.** Each pitch type is a fixed multiplier for every pitcher (a changeup, for example, cuts home runs by
  20%), with no repertoire. The fielding manager therefore throws the same pitch to the same kind of batter.
* **Model estimates.** The valuation uses average transitions, not rolls, and a run-expectancy table from the modern
  game for every era. The base-running constants it uses are copies of the resolver's.

Recommendations for all of these are in [ComputerManagers-Review.md](ComputerManagers-Review.md).
* **The per-game `Random`.** A side's manager uses `Random.Shared` unless a test builds it itself, so computer-versus-
  computer games are not repeatable from the game's own seed.

## 11. Tests

* `tests/MiniLeague.Engine.Tests/ManagerCatalogTests.cs`
  * the catalog: unique ids and names, a complete profile for each persona, unknown ids fall back to the default,
    `Register` rejects a duplicate, the built-in list is not empty (a regression for an initialization-order bug)
  * the old rules are a choice, and an extra manager can be added
  * each side uses its own manager (a spy on each side records which side of the ball it was asked about)
  * the personas and ids are saved with a game, and an older save with none still opens
  * the temperaments: small ball attempts more small-ball plays than the Analyst, and the Slugger attempts none; the
    Tinkerer changes pitchers on smaller edges than the Analyst, and the Analyst than the Slugger; the old-school manager
    walks the big bat about half the time and the Analyst does not
* `tests/MiniLeague.Engine.Tests/ComputerManagerDecisionTests.cs`: the Analyst's decisions (steals depend on the runner
  and the battery, bunts, intentional walks, pitch choice, reliever matchups) and the pitchout regression (never against
  a runner who will not go, mixed in rather than constant, rare over whole games).
* `tests/MiniLeague.Web.Tests/GameRoomTests.cs`: the choosers appear only for computer sides, each shows its persona's
  profile, and the chosen managers are saved with the game.

## 12. File map

| File | Role |
|---|---|
| `src/MiniLeague.Engine/AI/ComputerManager.cs` | The expected-run manager; virtual decision methods; shared injury and position logic |
| `src/MiniLeague.Engine/AI/ManagerDecisionModel.cs` | One-play valuation, steal and bunt estimates, pitchout pricing |
| `src/MiniLeague.Engine/AI/ManagerTuning.cs` | The dials |
| `src/MiniLeague.Engine/AI/OldSchoolManager.cs` | The original rule-based manager |
| `src/MiniLeague.Engine/AI/ManagerPersona.cs` | A persona and its profile ratings |
| `src/MiniLeague.Engine/AI/ManagerCatalog.cs` | The five personas and the catalog |
| `src/MiniLeague.Engine/Services/GameService.cs` | Per-side manager lookup (`ManagerFor`), `GetComputerDefense`, `PersonaFor` |
| `src/MiniLeague.Engine/Models/GameState.cs`, `Services/GameSnapshot.cs` | The saved manager ids |
| `src/MiniLeague.Web/Components/Game/ManagerChooser.razor` (+ `.css`) | The setup chooser and persona card |
| `src/MiniLeague.Web/Pages/GameRoom.razor` | Selection state, passing the ids on, the "is deciding" message |
