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
each part of the game. They are written by hand, not computed, so the benchmark (section 9) keeps them honest: where
two personas' ratings for a trait differ by two or more, the higher-rated one must measurably do more of it.

| Persona | Aggression on the bases | Small ball | Bench use | Quick with the bullpen | Intentional walks | Late defensive subs |
|---|:-:|:-:|:-:|:-:|:-:|:-:|
| Ellie Whitcombe | 3 | 3 | 3 | 3 | 2 | 1 |
| Gus Halloran | 3 | 2 | 3 | 3 | 5 | 3 |
| Rosa Delgado | 5 | 5 | 4 | 3 | 2 | 4 |
| Duke Pruitt | 2 | 1 | 1 | 1 | 1 | 1 |
| Marcus Delacroix | 3 | 2 | 5 | 5 | 4 | 4 |

### Ellie Whitcombe, "The Actuary" (`analyst`)

> Prices every call in expected runs and only makes the ones that clearly pay.

A former insurance actuary who took over a last-place club with a spreadsheet and a stopwatch. She will not bunt,
steal or walk anyone unless the arithmetic says so, and the arithmetic usually says no.

**In play.** The reference manager and the default for any side that has not been given a choice. Every dial is at its
default. She steals on about 8% of chances, bunts on about 11%, uses the hit and run often with a contact hitter up,
and almost never walks anyone on purpose.

### Gus Halloran, "Ironhide" (`oldschool`)

> Plays it the way it has always been played: set rules, no lookahead, a bunt when the book says bunt.

Thirty-one years in the game, nineteen of them in the dugout, and he has never read a spreadsheet. He walks the big
bat with first base open, bunts with the weak hitter late, and sends a fast runner on a hunch.

**In play.** Follows the fixed rules in [section 6](#6-the-old-school-manager). Walks power hitters often (about 1.7
intentional walks a game) and picks pitches at random. His bunting rules rarely fire, because pitchers never bat. He is
the weakest of the five (about 41% in the round robin), which makes him the natural "easy" opponent.

### Rosa Delgado, "Spark" (`smallball`)

> Bunts, steals and hit-and-runs to manufacture runs, and sends in the fast pinch runner early.

A scrappy utility infielder who never hit over .240 and made a career of moving runners. She manages the way she
played: bother the pitcher, take the extra base, and play for the one run.

**In play.** Negative bars for steals (-0.015), hit and runs (-0.010), sacrifice bunts (-0.015) and aggressive running
(-0.005): she makes those calls not only when they pay but when they cost a little, because playing for one run is
how she manages. She steals on about 19% of chances and runs aggressively on about 40% of plate appearances with a
runner on. A low pinch-run bar (0.02) puts a fast runner in early. On defense she calls a pitchout 45% of the time
when a runner is likely to go. **Philosophy:** she presses when behind late (`SituationalRisk` 0.5, so her gambles cost 0.02
less when trailing and 0.02 more when ahead), acts a little more readily in the big moments (`LeverageSensitivity` 0.3),
keeps hitters guessing (`PitchMix` 0.02) and puts a better glove in to protect a lead from the eighth.

### Duke Pruitt, "Big Sky" (`slugger`)

> Never gives up an out on purpose, trusts his starter and lets his hitters hit.

A power-hitting first baseman who watched managers waste his at-bats on bunts. He keeps the bench for emergencies,
leaves a good starter in, and would rather lose swinging than win bunting.

**In play.** Steals, hit and runs and bunts are switched off (`ManagerTuning.Never`, a margin no call can reach). He
pinch hits only from the seventh inning and only for a large gain, charges a high price for spending a bench player
(1.6 times the normal cost), and walks a batter only on a large edge (0.10). He keeps a starter longer than anyone:
bullpen margins of 0.07 and 0.14, warm-up not before the eighth inning or 79 pitches, a tiring starter stays unless
the reliever is clearly better (+0.06), and a starter is not "shelled" until he has allowed 6 runs. His starters
finish about 20% of games, against about 1% for the Tinkerer's. **Philosophy:** he is stubborn, not changing his plan
for the moment (`LeverageSensitivity` -0.3) and trusting his hitters over the platoon book (`PlatoonWeight` 0.3).

### Marcus Delacroix, "The Tinkerer" (`tinkerer`)

> Changes pitchers and pinch hitters at the first sign of a better matchup and walks hitters to get one.

A pitching coach turned manager who keeps a binder on every hitter in the league. His bullpen door never stops
swinging, his bench plays every night, and he will walk a star to face a lesser bat.

**In play.** Pinch hitting from the fifth inning for a small gain (0.012) with a half-price bench. Considers an
intentional walk from the sixth inning, for a smaller gap between the batter and the next hitter (0.08 of OPS), and
makes it on any edge. Starts a reliever warming from the fourth inning at 39 pitches, and from 52 pitches will swap a
still-fresh starter for a warm reliever who looks at least as good (a margin of -0.02 in a close late game, 0.0
otherwise). A tiring pitcher goes even for a somewhat worse arm (-0.20), and a starter is "shelled" after 4 runs. The
platoon effect (section 4.1) gives his matchup changes something real to work on: he uses the most pitchers and pinch
hitters of the five, and his starters finish the fewest games. **Philosophy:** the platoon book matters twice as much to
him as it really does (`PlatoonWeight` 2.0), the big moments matter most (`LeverageSensitivity` 0.6), a starter is done at
88 pitches (`StarterPitchLimit`), the closer comes in from the eighth (`CloserFromInning` 8), a glove goes in for a lead
from the seventh, and he varies his calls a little (`PitchMix` 0.015).

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
the **platoon** matchup between them, the ballpark and the alignment.

**Platoon.** The team data has only overall season lines (the Lahman database has no splits), so the matchup comes
from a standard modifier around that line (`ProbabilityTable.Platoon`): a little better against a pitcher of the other
hand, clearly worse against his own. Left-handed batters have the larger split. A switch hitter always bats from the far
side. The resolver applies the same modifier, so it is part of the game, not only of the managers' estimates.

| Matchup | Hits | Home runs | Walks | Strikeouts |
|---|--:|--:|--:|--:|
| Left-handed batter against a left-hander | ×0.94 | ×0.88 | ×0.92 | ×1.12 |
| Left-handed batter against a right-hander | ×1.02 | ×1.04 | ×1.03 | ×0.96 |
| Right-handed batter against a right-hander | ×0.98 | ×0.96 | ×0.97 | ×1.04 |
| Right-handed batter against a left-hander | ×1.03 | ×1.06 | ×1.04 | ×0.95 |

**Win probability.** Each outcome becomes the situation that follows (bases, outs, runs scored), and that situation is
valued by the **batting side's chance of winning** from it (`WinProbability.HomeWinsCached`, a table filled on first
use), divided by what one more run is worth at the moment of the decision. Dividing keeps every value in runs, so the
dials in section 5 keep their meaning, while the shape follows the game: early on a run is a run; late and close, the
first run is worth far more than the second, a lead protects itself, and a walk-off ends it. In a game already decided
(one more run changes the win chance by less than 1%), plain run expectancy is used instead. The value is for the
**batting** side: the batting manager maximizes it, the fielding manager minimizes it.

The win chances come from the run expectancy and the **chance of scoring at least once** for each base and out state
(`WinProbability.ChanceToScore`). The second table matters for one-run decisions: a runner on third with one out scores
more often than a runner on second with nobody out (0.66 against 0.62), though fewer runs on average, which is exactly
what makes a late sacrifice bunt worth it.

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
   * With a runner on second and third open (a **steal of third**) or runners on first and second (a **double steal**): valued
     like a steal of second, with the steal of third made harder (`RunningRules.StealOfThirdFactor`). It takes a strong
     runner, a weak battery and a close game.
   * With a runner on third and fewer than two out: a **squeeze** (the runner breaks with the pitch; a popped-up bunt
     doubles him off, a read play puts him out at the plate) or a **safety squeeze** (he waits for the bunt; it scores less
     often and never loses him). Each is valued by what the on-deck hitter makes of the situation it leaves, against a
     swing, and made if it wins by `SqueezeMargin`.
   * Bunts, squeezes and intentional walks look one hitter ahead: the on-deck man bats in the state they leave.
3. **Pinch hitter.** From `PinchHitFromInning`, when the batting side leads by at most 2 or trails by at most 4. Each
   bench bat with 100 or more at-bats is valued against the man due up: the matchup gain, plus 20% of the OPS gap, minus
   the fielding lost (at the position he would take), a reserve cost (more if the bench is thin) and a cost for losing a
   bat for later, the last two scaled by `BenchReserveScale`. A catcher is replaced only by a natural catcher. Made only
   if the net gain beats `PinchHitGain`.
4. **Re-evaluation.** A steal is decided before a pinch hitter so a pinch hitter is not spent on a play that has no
   plate appearance. After a pinch hitter is chosen, the strategy is reconsidered for the new batter.

### 4.3 Fielding side

1. **Intentional walk.** Only with first base open, fewer than two out, a runner in scoring position, from
   `IntentionalWalkFromInning` (the seventh by default), within two runs, a batter with OPS of at least .800 who is at
   least `IntentionalWalkOpsGap` (.150 by default) better than the next hitter. It is
   priced as a forced runner plus a bounded credit for the weaker on-deck bat (at most 0.35), and made if it beats
   facing the batter by `IntentionalWalkMargin`.
2. **Pitch and alignment.** Every available alignment is evaluated with each of the four ordinary pitches and the lowest
   value wins. Ties within 0.004 are broken at random (20% chance to switch) so the choice is not predictable. A manager
   with `PitchMix` above zero instead picks among all the options at random, each weighted by `exp(-(worse by)/PitchMix)`
   (a softmax whose temperature is in runs): the best stays the most likely and a clearly bad one almost never comes up.
   * Always available: Standard.
   * Infield In: runner on third, fewer than two out. It lets more singles through but holds an unforced runner on
     third on most ground balls (section 10).
   * Corners In: runner on first or second, fewer than two out, a batter who bunts well (bunting or sacrifice rated 4
     or more). The ratings come from sacrifice hits (section 10); the team files in the repository predate that field,
     so every player has the default of 3 and Corners In is not offered until they are regenerated.
   * Guard Lines: the seventh inning or later, within two runs.
   * Shift Pull when the batter's power rating is 3 or more, Shift Opposite when it is 2 or less. There are no spray
     charts, so power is only a rough clue and Standard stays available and wins ties.
3. **Pitchout.** Considered only with a runner on first, second empty, fewer than two out, and within two runs. If the
   runner goes, it is a steal attempt the catcher is ready for. If he stays, it is a plate appearance with the pitcher
   behind in the count: the pitch table's Pitchout row (a few more walks, nothing else changed), so a pitchout is a
   **wasted ball**. The runner is assumed to go with probability 0.5 if the computer offense's own steal rule would send
   him, else 0.05 (`ManagerDecisionModel.RunnerGoChance`). It is called only when it beats the normal pitch by 0.02, and
   then only `PitchoutShare` of the time, so a human or computer runner can still succeed.

### 4.4 Bullpen

Run after each play for the side that fields next.

* **Closer.** A closer is held for a save (a lead of 1 to 3 from `CloserFromInning`, the ninth by default) and valued with
  a 0.20 penalty outside one, and a 0.08 bonus inside one. A fireman has an earlier `CloserFromInning`.
* **Candidates.** Each warm reliever is valued against the next three hitters, using his best pitch against each, with
  his warm-up modifier. The same value is computed for the current pitcher with his fatigue.
* **A change** is made when there is a warm reliever and any of these holds: a closer is ready in a save spot; the
  pitcher is spent (Tired or worse once settled) or **shelled** (at least five batters faced, runs allowed at least
  `ShelledRuns` for a starter or 3 for a reliever, and at least 0.18 runs per batter); he is Tiring and the reliever
  beats him by `TiringSwapMargin` (by default he goes unless the reliever is worse by more than 0.08); or he is Ok (or
  past `QuickHookPitchCount`) and the reliever beats him by `ReliefEdgeClose` in a close late game or `ReliefEdgeOther`
  otherwise. Each move carries its reason (section 4.5).
* **Pitch limit.** A starter who has thrown `StarterPitchLimit` pitches is replaced if a reliever is ready, however well he
  is pitching, and a reliever starts warming 15 pitches before. Unlimited by default.
* **Emergency.** A spent or shelled pitcher with nobody warm is replaced by the best arm available, cold or not.
* **Warming.** If nobody is already warming (an over-warmed arm does not count), start the closer in the eighth inning
  with a save in sight, or start the best other reliever once the pitcher is no longer fresh, is shelled, or (in a close
  game from `WarmUpFromInning`) has thrown `WarmUpPitchCount` pitches.

### 4.5 Explanations

Every computer call carries a `DecisionExplanation` (`Why` on the offensive and defensive decision): a sentence for the
player and the options weighed, each with its gain in runs over the ordinary call. For example, "Sacrifice bunt: worth
+0.04 runs over swinging away with T. Lazzeri up." A pinch hitter or runner adds a sentence of its own. A pitching move
carries a reason (`PitchingMove.Why`). Gus Halloran's explanations name the rule he followed ("By the book: ...").
`GameService.LastOffense` and `LastDefense` hold the calls behind the latest play. Nothing shows them in the game yet;
they are the basis for manager feedback.

### 4.6 Late defensive replacements

As a computer-managed side takes the field (the first play of each half-inning in which it fields), its manager may swap
a weak glove for a better one (`ComputerManager.ChooseDefensiveReplacement`, applied through `GameService.Replace`, at
most one a half-inning). It happens only from `DefensiveReplaceFromInning`, only with a lead of 1 to
`DefensiveReplaceMaxLead` (3), and only to a bench player who is a natural at the position. The gain is the better glove
(`DefenseModel.PositionValue`, the fielding value weighted by the position) minus the bat he gives up (the OPS difference
times 20, or 8 in the ninth, when the spot rarely bats again); it must beat `DefensiveReplaceGain` (1.0 points). Off by
default. Gus Halloran makes this one book move, in the ninth only.

### 4.7 When the pitcher bats

With the **Designated hitter** option off (or *Historical* for a year and league without one) the starting pitcher is in the
batting order, ninth by default, at position P, and the order has no DH. `GameState.DHRule` is false for the game, and a
saved game works the rule out again from its options and the home team. The pitcher is a real hitter: his batting line
comes from the team file (`bat`, which the generator writes from the Lahman batting rows), or an ordinary pitcher's
(about .147, little power) when the file has none, with bunting and sacrifice ratings of at least 4.

* **The order follows the pitcher.** A pitching change puts the new pitcher in the old one's spot and position, and on
  base if he was running. The pitcher is never a fielder to replace or swap, and the computer's position swaps skip him.
  An injured pitcher at bat is replaced by a reliever in his spot, not a bench player.
* **Pinch hitting for the pitcher** takes him out of the game: the pinch hitter holds the pitcher's spot and position, and
  when the side next takes the field a reliever is brought in by the manager who made the move (a human's by the default
  manager, who prefers a reliever who is already warm). It is refused when the bullpen is empty.
  A computer manager does it only when it is behind or tied from the sixth inning, or the pitcher has thrown 79 pitches
  or is tiring, and only with a reliever to bring in; the pitcher's poor bat would otherwise make it worth doing every
  time. Gus Halloran does it only from the seventh, behind or tied.
* **The double switch** (`GameService.DoubleSwitch`, offered in the Substitutions dialog's Pitching tab): the new pitcher
  bats in the spot of a fielder who leaves, and a bench player takes that fielder's position and bats in the pitcher's
  old spot, which comes up sooner. A computer manager makes one with a pitching change when the pitcher's spot is due
  within three batters, a bench bat who is a natural at the fielder's position is worth more than the fielder's bat lost
  (a far-off spot rarely bats) and the bench player used (`ChooseDoubleSwitch`, `DoubleSwitchGain`).
* **Pitcher bunts** need no new rule: the pitcher's bunting rating and weak bat make the sacrifice bunt the best call
  for the expected-run managers in many situations, and Gus Halloran's book (bunt with the pitcher, runner on, nobody
  out) now fires.

Measured with `--dh pitchers` (1,800 games): pinch hitters rise from about 0.5 to about 1.6 a game, pitching changes from
about 1.2 to 1.6–1.9, bunts from about 10% to 14% of chances for the Analyst, and starters finish about 4% of games
instead of 11%, with runs about the same. The personas keep their order.

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
| `IntentionalWalkFromInning` | 7 | First inning an intentional walk is considered |
| `IntentionalWalkOpsGap` | 0.15 | OPS by which the batter must beat the next hitter before a walk is considered |
| `TiringSwapMargin` | -0.08 | Edge a reliever needs over a tiring pitcher (negative: a slightly worse arm is taken) |
| `ShelledRuns` | 5 | Runs a starter may allow before he is pulled as shelled |
| `ReliefEdgeClose` | 0.035 | Edge a reliever needs over a tiring pitcher in a close late game |
| `ReliefEdgeOther` | 0.085 | The same bar earlier, or when the game is not close |
| `WarmUpPitchCount` | 57 | Pitches after which a reliever is warmed early in a close game |
| `WarmUpFromInning` | 7 | First inning that early warm-up applies |
| `QuickHookPitchCount` | `int.MaxValue` | Pitches after which a still-fresh pitcher may be swapped for a warm reliever |
| `PitchoutShare` | 0.30 | Share of the time a favorable pitchout is actually called |
| `SituationalRisk` | 0 | From the seventh inning, gambles (steal, hit and run, run hard) cost 0.04 × this less when behind and more when ahead; negative reverses it |
| `LeverageSensitivity` | 0 | Positive: acts on smaller edges in important moments (late, close), larger in blowouts and early; negative: stubborn when it matters |
| `PitchMix` | 0 | Temperature (runs) of a random pick among pitch and alignment options; 0 always takes the best |
| `StarterPitchLimit` | `int.MaxValue` | Pitches after which a ready reliever replaces a starter regardless of how he pitches |
| `CloserFromInning` | 9 | First inning a lead of 1 to 3 is a save spot (earlier means a fireman) |
| `PlatoonWeight` | 1.0 | Weight on the platoon matchup in pinch-hit and bullpen choices (0 ignores it, 2 doubles it) |
| `DefensiveReplaceFromInning` | `int.MaxValue` | First inning he swaps a weak glove for a better one with a lead (never, by default) |
| `DefensiveReplaceMaxLead` | 3 | Largest lead at which he does |
| `DefensiveReplaceGain` | 1.0 | Net fielding points a replacement must add after the bat he loses |
| `DoubleSwitchGain` | 0.02 | Net runs a double switch must show (only when the pitcher bats); a huge value means never |

**How leverage and risk change a margin.** Leverage is how much more the moment matters than the first inning of a tied game
(`ManagerDecisionModel.Leverage`: about 1 at the start, about 3 for a tight ninth, near 0 in a blowout). A manager with
`LeverageSensitivity` s multiplies a positive margin by `leverage^-s` (and divides a negative one by it), so he acts more readily
when it matters. The scaled margin applies to steals, hit and runs, bunts, running hard, pinch hitters and runners,
intentional walks and bullpen changes. `SituationalRisk` is added separately, to the gambles only.

### Values per persona

Blank means the default. `OldSchoolManager` has no dials; see [section 6](#6-the-old-school-manager).

| Dial | Analyst | Small ball | Slugger | Tinkerer |
|---|--:|--:|--:|--:|
| `StealMargin` | 0.035 | **-0.015** | **Never** | 0.035 |
| `HitAndRunMargin` | 0.035 | **-0.010** | **Never** | 0.035 |
| `BuntMargin` | 0.025 | **-0.015** | **Never** | 0.025 |
| `AggressiveRunMargin` | 0.005 | **-0.005** | **0.012** | 0.005 |
| `PinchHitFromInning` | 6 | 6 | **7** | **5** |
| `PinchHitGain` | 0.025 | 0.025 | **0.05** | **0.012** |
| `BenchReserveScale` | 1.0 | 1.0 | **1.6** | **0.5** |
| `PinchRunGain` | 0.05 | **0.02** | **0.10** | 0.05 |
| `IntentionalWalkMargin` | 0.04 | 0.04 | **0.10** | **0.0** |
| `IntentionalWalkFromInning` | 7 | 7 | 7 | **6** |
| `IntentionalWalkOpsGap` | 0.15 | 0.15 | 0.15 | **0.08** |
| `TiringSwapMargin` | -0.08 | -0.08 | **0.06** | **-0.20** |
| `ShelledRuns` | 5 | 5 | **7** | **4** |
| `SituationalRisk` | 0 | **0.5** | 0 | 0 |
| `LeverageSensitivity` | 0 | **0.3** | **-0.3** | **0.6** |
| `PitchMix` | 0 | **0.02** | 0 | **0.015** |
| `StarterPitchLimit` | off | off | off | **88** |
| `CloserFromInning` | 9 | 9 | 9 | **8** |
| `PlatoonWeight` | 1.0 | 1.0 | **0.3** | **2.0** |
| `DefensiveReplaceFromInning` | off | **8** | off | **7** |
| `DoubleSwitchGain` | 0.02 | 0.02 | **Never** | **0.0** |
| `ReliefEdgeClose` | 0.035 | 0.035 | **0.07** | **-0.02** |
| `ReliefEdgeOther` | 0.085 | 0.085 | **0.14** | **0.0** |
| `WarmUpPitchCount` | 57 | 57 | **79** | **39** |
| `WarmUpFromInning` | 7 | 7 | **8** | **4** |
| `QuickHookPitchCount` | off | off | off | **52** |
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

**Defensive replacement**

In the ninth inning, leading by one to three runs, a better glove for the weakest fielder, using the same rule as in
section 4.6. It is the only decision of his that is not overridden.

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

### The benchmark

`tools/MiniLeague.ManagerBench` plays seeded computer-versus-computer games between every pair of personas, over every
team (each against the team seven places along the list), with each persona in both seats. Every random source is seeded
(`GameService`'s `managerSeed` seeds each side's persona), so the same options give the same numbers on every run.

```
dotnet run -c Release --project tools/MiniLeague.ManagerBench -- --games 20 --check
```

It prints each persona's win rate with a 95% interval, runs for and against, and how often it makes each call: steals,
bunts and hit and runs per chance, aggressive running per plate appearance with a runner on, runners thrown out,
intentional walks per chance, pitchouts, pinch hitters and runners, pitching changes and complete games. With
`--check` it compares them with `expected-ranges.json` and with the profile ratings (section 2), and exits with 1 on any
mismatch. `--dh designated|historical|pitchers` sets the designated hitter rule for every game (the default is the
designated hitter); the ranges are for that default, so `--check` refuses another rule. With `--summary FILE` it also writes the report and any failures to FILE as Markdown (CI passes
`$GITHUB_STEP_SUMMARY`). It is not in the solution and takes a minute or two; run it after any change to the managers or
the engine's rules, and update the ranges only for a change that is meant to move them.

The tool's logic lives in `ManagerBenchmark.cs` (`Run`, `Report`, `CheckRanges`, `CheckProfiles`, `Metric`), with
`Program.cs` as the command line, so that tests can call it.

**Two safety nets.**

* *Every build:* `ManagerBenchmarkTests` (in the engine test project, which references the tool) runs a tiny benchmark
  (two personas, 30 games, a few seconds) and checks the tool itself: every pairing is played in both seats, the counts
  add up (one winner per game, one side's runs are the other's runs allowed), the same seed gives byte-identical output
  and a different seed does not, the Wilson interval and every metric are right, and the range and profile checks pass
  and fail when they should. It does not check the persona results, which need thousands of games.
* *In CI:* the **Manager benchmark** workflow (`.github/workflows/manager-benchmark.yml`) runs the full 6,000-game
  benchmark with `--check` and writes the report to the run's summary page. It runs when the engine, the team data or
  the tool changes (on a pull request to `main`), every Monday, and on demand from the Actions tab (with a choice of
  games per pairing). Like the browser tests it reports on its own and holds up nothing. Because every random source is
  seeded the numbers are exact, so a failure is a real change in behavior, not noise.

### Current results

Measured on 2026-11-25: 6,000 games (`--games 20`), after pitchers were scaled against their own league and
starters were given a stamina from their real starts (the calibration below). Scoring is now about 4.4 runs a team a game, up from 3.2. Also since the first measurement: the pitch types were balanced
(no free pitch), the double-play chance raised to 0.52, and the team files gained sacrifice hits and pitchers' batting lines
(see [ModelingStatisticsProbability.md](ModelingStatisticsProbability.md)).

| Manager | Win rate (95% CI) | Runs | Allowed | Steal % | Bunt % | H&R % | Aggressive % | IBB % | PH a game | Pitching changes | Complete games | Defensive subs a game |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Ellie Whitcombe (analyst) | 55.5% (53.5-57.4) | 4.50 | 4.00 | 6.6 | 5.8 | 31.1 | 20.0 | 0.1 | 0.53 | 0.64 | 52.2% | 0.00 |
| Marcus Delacroix (tinkerer) | 52.3% (50.3-54.3) | 4.50 | 4.35 | 7.5 | 5.5 | 29.4 | 17.7 | 1.3 | 0.78 | 1.73 | 0.2% | 0.21 |
| Rosa Delgado (small ball) | 53.6% (51.6-55.6) | 4.41 | 4.19 | 15.1 | 9.8 | 29.4 | 34.4 | 0.3 | 0.53 | 0.70 | 49.5% | 0.24 |
| Duke Pruitt (slugger) | 48.2% (46.2-50.2) | 4.30 | 4.14 | 0.0 | 0.0 | 0.0 | 10.5 | 0.2 | 0.28 | 0.60 | 49.3% | 0.00 |
| Gus Halloran (old school) | 40.4% (38.5-42.4) | 4.02 | 5.04 | 4.9 | 0.4 | 1.5 | 10.4 | 11.2 | 0.54 | 1.05 | 25.8% | 0.15 |

Steal and hit-and-run percentages are of chances (a runner on first, second open, fewer than two out); bunt, of
runner on first or second, third open, nobody out; aggressive, of plate appearances with a runner on; IBB, of first base
open with a runner in scoring position and fewer than two out.

The four expected-run personas are within about seven points of each other (48.2% to 55.5%), and the analyst now sits just
outside five points of even. Spark runs and bunts the most, Duke never does, and Marcus uses the most bench players and
pitchers (his starters almost never finish). Complete games do not set Duke apart: he, the analyst and Spark all finish 49%
to 52% of the games their aces start. Spark, Marcus and Gus are the ones who make defensive replacements, Gus only in the
ninth. Gus is the easy opponent.

### Calibration against the real seasons

`--calibrate` (in `Calibration.cs`) plays every team against each of the others, home and away, with one manager on both
sides, and compares each team's per-game numbers with the real season's (`lahman-reference.json`, written by
`tools/export_lahman_reference.py` from the Lahman database; the file is committed, so the check runs offline).
`--games` sets games per matchup (default 10) and `--persona` the manager. It reports ratios (simulated over real); it
does not pass or fail.

The first run found the engine scoring only 62% of the real runs. The cause was the pitcher scaling in
`ProbabilityTable`: it divided a pitcher's ERA by a fixed 4.00, his strikeouts by 7.0 per nine and his walks by 3.0, but
a batter's line already includes the pitching of his own league (league ERA ran from 2.98 in 1968 to 4.14 in 1927, and
league strikeouts from 2.8 to 6.1 per nine), so every hitter was penalized a second time. Pitchers are now scaled by
their own rate over their league's (`LeaguePitching`, written by the generator into each team file as `leaguePitching`),
with hits and home runs scaled by their own rates rather than by ERA, which moves far more than hits do. An average
pitcher of any era leaves a batter's line alone (`LeaguePitchingTests`).

| Measure (simulated / real) | Before | After |
|---|--:|--:|
| Runs | 0.62 | 0.84 |
| Hits | 0.74 | 0.93 |
| Home runs | 0.78 | 1.03 |
| Strikeouts | 0.75 | 1.06 |
| Walks | 0.84 | 0.84 |

A perfect model would score about 0.90 here, not 1.00: every opponent is a pennant winner whose staff allowed about 10%
fewer runs than its league. Still open: steals are about 1.5 times the real rate, and sacrifices and double plays are low (about 0.5 and 0.7).

**Starter fatigue.** The first calibration also showed starters leaving too early (complete games about 0.1 of real).
The tiring factor, whole innings per start taken from 10, gave nearly every starter a 2, 3 or 4: Cuellar of the 1970
Orioles, who finished more than half his starts, and Nolan of the 1975 Reds, who finished five of 32, differed by one step
(15 pitches on the threshold). A starter's stamina is now worked out from his real starts (`FatigueModel.Stamina`): his
outs per start (the team files carry games started, `gs`) times the engine's 4.9 pitches an out, plus 15 for the share of
his starts he finished. His effectiveness drops 8% at that pitch count, 20% twenty pitches later and 35% fifteen after
that. Relievers and files without `gs` keep the tiring factor. With a rotation and one manager on both sides, starter outs
are 0.94 of real and complete games 0.77. The pitch estimate was lowered at the same time to about 3.5 a plate appearance (it was about 4.0, and a start could run past 180 pitches), and the managers' pitch thresholds were scaled to match. Managers pull starters by their condition, so the same stamina gives each persona
its own completion rate (section 9, above); the benchmark starts each team's ace, so its rates run higher than a season's.
The ratios in the table above predate the later calibration work; the current ones are in
[ModelingStatisticsProbability.md](ModelingStatisticsProbability.md).

### Before: expected-run manager against the old rules

Measured before the free options were removed, so aggressive running cost nothing. 3,000 games, both sides on the same
engine.

| Setup | New win % (95% CI) | Run margin per game |
|---|---|---|
| Full comparison | 62.6% (60.8 to 64.3) | +1.33 ± 0.15 |
| New manager's shifts switched off | 62.4% (60.7 to 64.2) | +1.29 |
| Old manager's intentional walks off | 58.4% (56.7 to 60.2) | +0.84 |
| New manager's aggressive running off | 59.4% (57.7 to 61.2) | +0.92 |
| Both of the last two off | 55.1% (53.3 to 56.9) | +0.49 ± 0.14 |

At that point the old manager's intentional walks cost it a lot, and free aggressive running was worth about 0.45 runs a
game to the new manager. The round robin then had the four expected-run personas at 52–55% and Gus at 35.5%.

## 10. Known limits and engine quirks

* **Fixed: free options.** Aggressive running could take an extra base but never be thrown out; the infield in cut
  singles and never held the runner on third; Corners In cut singles on every swing; Guard Lines and Shift Opposite beat
  Normal on average; and a pitchout to a batter whose runner stayed cut hits to 10%. Now a failed aggressive send is
  thrown out 60% of the time (`BaseRunningEngine.SentRunnerThrownOut`, at most one runner a play); with the infield in
  an unforced runner on third scores on a ground out only about 25% of the time (`InfieldInScoreChance`, by speed) while
  12% more singles get through; Corners In charges the bunt and gives up a few more hits on a swing; Guard Lines and
  Shift Opposite were re-weighted; and a pitchout without a runner going only adds walks. A test
  (`NoFreeOptionTests`) checks that no alignment beats Normal against an ordinary hitter with the bases empty.
* **Fixed: steal odds.** The success chance once multiplied the runner's record by 0.6 twice for an average battery, so
  an average runner made it about one time in four and stealing never paid. It now starts from his record, steadied
  toward the league rate of 67% when the record is short, and adjusts for the pitcher's hold and the catcher's arm
  around the average (`BaseRunningEngine.StealSuccessChance`, shared by the engine and the managers).
* **Fixed: tag-ups.** Any caught fly ball used to let the runner on third tag up and score, a pop-up to the catcher
  included. Now only an outfielder's catch can score him.
* **Fixed: platoon matchups.** There were none; a standard platoon modifier now applies (section 4.1).
* **Shifts are rough.** The data has no spray charts, so the shifts are uniform trades chosen by power alone.
* **Philosophy dials are blunt.** Risk by the score is a fixed shift for the gambles only, leverage scales margins by one
  formula, and the pitch mix is a temperature over the same values the manager already computes. A defensive replacement
  weighs one glove against one bat; it ignores who is due up and whether the lineup spot comes round again.
* **Platoon is generic.** Every hitter gets the same modifier for his side; a player's own split would need a source
  beyond the Lahman database.
* **Tired pitchers.** A tired pitcher now allows **more** hits (the hit rate is divided by his effectiveness). Before
  this change it was multiplied, which made a tired pitcher harder to hit. This is an engine change that affects every
  game, not only the managers.
* **Ratings waiting for regenerated data.** The generator now writes each batter's sacrifice hits (`sh`), from which
  the loader sets the bunting and sacrifice ratings (`TeamLoader.BuntRating`), and each team's tendencies (steal attempts,
  sacrifices, complete games and saves per game, `Team.Tendencies`). The team files in the repository were generated
  before these fields, and the Lahman files are needed to regenerate them, so until then every player bunts at the
  default 3 and Corners In is not offered. The generator now also writes each pitcher's own batting line and handedness
  (`bat`, `bats`), which the loader reads for games where the pitcher bats; until the files are regenerated every pitcher
  bats as an ordinary .147 hitter. Hold-runners and ground-ball rate have no Lahman source; a team file may set
  `holdRunners` by hand.
* **Pitchers bat only if the game is set that way.** The **Designated hitter** game option decides: *Designated hitter*
  (the default, and what every game was before the option) gives both teams one; *Historical* follows the home team's
  league and year (none in the National League before 2022 or the American League before 1973); *Pitchers bat* removes
  it. 11 of the 15 teams played without one. The default has not been switched to *Historical* yet: that waits for the
  team files to be regenerated with each pitcher's own batting line (see "Ratings waiting for regenerated data"), until
  which every pitcher bats as an ordinary .147 hitter. See section 4.7.
* **Infield In with the bases loaded.** A forced runner on third cannot hold, and there is no force play at the plate in
  the engine, so with the bases loaded the run still scores on a ground out and the batter is out.
* **Pitch types.** Each pitch type is a fixed multiplier for every pitcher (a changeup, for example, cuts home runs by
  20%), with no repertoire. The fielding manager therefore throws the same pitch to the same kind of batter.
* **Model estimates.** The valuation uses average transitions, not rolls, and run-expectancy and scoring tables from the
  modern game for every era. Some base-running constants it uses are still copies of the resolver's.

* **Repeatable games.** In the app each side's manager uses shared randomness, as a real game should. Pass
  `managerSeed` to `GameService` (the benchmark and the tests do) to replay a computer-versus-computer game exactly.
* **Few pitching changes.** About 0.24 changes per team per game in the long computer-game test. With fewer runs
  scoring since the fixes above, fewer starters are shelled, and that test's floor was lowered from 0.25 to 0.2.

Recommendations for all of these are in [ComputerManagers-Review.md](ComputerManagers-Review.md).

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
* `tests/MiniLeague.Engine.Tests/ManagerModelTests.cs`: the platoon effect (other hand better, lefties the larger split,
  switch hitters from the far side); steal odds near the runner's record and moved by the battery; only an outfield fly
  scores a tag-up; the first run is worth more late and close, so a ninth-inning bunt looks better than a first-inning
  one; a decided game falls back to run expectancy; every computer call explains itself; a seeded game replays
  exactly; the bunting rating from sacrifice hits, and team files with and without the new fields.
* `tests/MiniLeague.Engine.Tests/ManagerDialsTests.cs`: one test per philosophy dial, each changing only the dial in a fixed
  situation: leverage (a tight ninth is worth more than the opening and a blowout almost nothing; a leverage-sensitive
  manager changes pitchers on a smaller edge); risk by the score (steals when behind late, not when tied); pitch mix
  (spreads calls but still favors the better ones); the pitch limit and early warm-up; a fireman against a classic closer;
  platoon weight (a platoon manager pinch hits for a bad matchup, one who ignores it does not); defensive replacements
  (needs a lead, a late inning, a natural at the position, and a glove worth more than the bat, made once a half-inning
  by a computer side); and Gus Halloran's ninth-inning-only rule.
* `tests/MiniLeague.Engine.Tests/ManagerBenchmarkTests.cs`: the benchmark tool's own smoke tests (section 9).
* `tests/MiniLeague.Engine.Tests/PitchersBatTests.cs`: the setting (every choice for every league and year, the default
  unchanged, Historical following the home team for both teams); the batting order and its rules, editing and fitting
  to the rule; pitchers' batting lines; whole computer games under the rule (the order stays sound after every play);
  a pinch hitter for the pitcher forcing a reliever, and refused with an empty bullpen; a pitching change and an injury
  moving the order; the double switch (and when it is refused); the pitcher not being replaceable or swappable; when the
  managers pinch hit, bunt and double switch; and a saved game keeping its rule.
* `tests/MiniLeague.Web.Tests/GameRoomTests.cs` (the last tests): the Game Options choice and its help, the lineup
  editor and card with the pitcher in the order, and the Substitutions dialog offering a double switch only without a
  designated hitter.
* `tools/MiniLeague.ManagerBench`: the benchmark and its `--check` (section 9).
* `tests/MiniLeague.Engine.Tests/NoFreeOptionTests.cs`: an aggressive send is thrown out at about the expected rate and
  a runner who is not sent never is; only one runner is thrown out on a hit; such a play is an out in the result, a tag
  in the runner moves and a throw that beats him in the animation; the infield in holds the runner on third and cuts runs
  with one there but costs runs with the bases empty; no alignment beats Normal against an ordinary hitter with the bases
  empty; a pitchout to a batter whose runner stays does not suppress hits.
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
| `src/MiniLeague.Engine/Models/AtBatDecision.cs` | `DecisionExplanation` and `DecisionOption` |
| `src/MiniLeague.Engine/Simulation/WinProbability.cs` | Run expectancy, chance to score, win probability and its table |
| `tools/MiniLeague.ManagerBench/` | The benchmark (`ManagerBenchmark.cs`, `Program.cs`) and `expected-ranges.json` |
| `.github/workflows/manager-benchmark.yml` | Runs the full benchmark with `--check` in CI (non-blocking) |
| `tools/generate_teams.py`, `src/MiniLeague.Engine/Services/TeamLoader.cs` | Sacrifice hits, bunting ratings, team tendencies |
