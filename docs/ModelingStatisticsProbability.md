# Modeling, statistics and probability

How the simulation is checked against real baseball, what was found, what works and what does not. Read this before
changing the at-bat probability table, base running, fatigue, the era data, or anything the benchmarks measure; update it
when you change them or learn something new (see [Keeping this document current](#17-keeping-this-document-current)).

Last updated: 2026-11-25 (observations on adding more teams and eras).

## Contents

1. [Why this exists](#1-why-this-exists)
2. [The tools](#2-the-tools)
3. [The reference data (Lahman)](#3-the-reference-data-lahman)
4. [Calibration methodology](#4-calibration-methodology)
5. [Finding 1: scoring was 38% too low](#5-finding-1-scoring-was-38-too-low)
6. [Finding 2: starters tired in the wrong way](#6-finding-2-starters-tired-in-the-wrong-way)
7. [Finding 3: steals](#7-finding-3-steals)
8. [Finding 4: the free pitch, the double play and the sacrifice](#8-finding-4-the-free-pitch-the-double-play-and-the-sacrifice)
9. [Finding 5: the ace started every game](#9-finding-5-the-ace-started-every-game)
10. [Finding 6: too many pitches](#10-finding-6-too-many-pitches)
11. [Current state](#11-current-state)
12. [What works, and what does not](#12-what-works-and-what-does-not)
13. [The tests that guard this](#13-the-tests-that-guard-this)
14. [Open problems, in order](#14-open-problems-in-order)
15. [Adding more teams or other eras](#15-adding-more-teams-or-other-eras)
16. [Pitfalls and gotchas](#16-pitfalls-and-gotchas)
17. [Keeping this document current](#17-keeping-this-document-current)

## 1. Why this exists

Two questions are different and need different tools:

* **Which computer manager is better?** Answered by the *manager benchmark*: computer against computer on the same engine.
  It is relative; a wrong engine that is wrong the same way for everyone can still rank managers sensibly.
* **Does the engine reproduce real baseball?** Answered by *calibration*: each simulated team against the real season it
  represents. This is absolute, and it caught a defect that the manager benchmark could never show: the engine scored
  only 62% of real runs, and every manager result had been measured in a game that was too hard to hit in.

Both are needed. The manager benchmark guards persona behavior ([ComputerManagers.md](ComputerManagers.md) section 9);
calibration guards the engine's realism (this document).

## 2. The tools

| Tool | What it does | Command |
|---|---|---|
| Manager benchmark | Plays seeded games between every pair of personas over every team, both seats; checks the results against `expected-ranges.json` and the profile ratings | `dotnet run -c Release --project tools/MiniLeague.ManagerBench -- --games 20 --check` |
| Calibration | Plays every team against each other team, home and away, with one manager on both sides; prints simulated ÷ real per measure and per team. Reports, never fails | `... -- --calibrate --games 10 [--persona analyst] [--dh historical]` |
| Reference export | Reads the Lahman database and writes `tools/MiniLeague.ManagerBench/lahman-reference.json` | `python tools/export_lahman_reference.py` |
| Team data generator | Builds the team and ballpark JSON from the Lahman CSVs | `python tools/generate_teams.py <Lahman CSV folder>` |

Where the code is: `tools/MiniLeague.ManagerBench/Calibration.cs` (`Run`, `Compare`, `Report`), `ManagerBenchmark.cs`,
`Program.cs` (the command line).

Calibration defaults: 10 games per ordered pair of teams (15 × 14 × 10 = 2,100 games, about 280 per team and seat mix,
under a minute), the analyst manager on both sides, the **Historical** designated-hitter rule (so each real league's rule
applies), **starters in rotation** (each in proportion to his real games started), seed 1. Every random source is seeded, so equal options give equal numbers. `--raw "<measure>"` lists a measure's
real and simulated values team by team.

> **Correction (2026-11-25).** Until finding 4, `--calibrate` said it used the Historical rule but silently used the
> designated hitter in every game (it took the benchmark's default). So findings 1 to 3 were measured with no pitchers
> batting; their conclusions hold, and the numbers in section 11 are re-measured with the rule actually applied. With
> pitchers batting the starters leave sooner (a pinch hitter for the pitcher ends his day), so the stamina offset was
> re-tuned (section 6).

## 3. The reference data (Lahman)

* **Source:** the Lahman Baseball Database, CC BY-SA, held in a SQL Server database named `Lahman` (27 tables, 1871 to
  2025). The same data is in the Lahman CSVs the generator reads.
* **Connection:** `export_lahman_reference.py` takes `LAHMAN_SQL_SERVER` (default `192.168.1.210`), `LAHMAN_SQL_USER`
  (default `sa`) and `LAHMAN_SQL_PASSWORD` (required) from the environment, and calls `sqlcmd`. It only reads. **Never put
  the password in a file, a test or a commit.** Tests and the benchmark use the committed JSON, so they run offline.
* **Output:** `lahman-reference.json`, committed next to `expected-ranges.json`:
  * per team (15): season totals from `Teams`, `Batting`, `Pitching` and the catchers' `Fielding` lines; the manager of
    record; park factors; ready-made per-game rates (runs, steals, double plays, complete games, pitchers used,
    sacrifices, outs per start, and so on);
  * per league-season (15): league totals and per-team-game rates, and `pitchingPerNine` (ERA, non-home-run hits, home
    runs, strikeouts and walks per nine), which is also what the team files carry as `leaguePitching`.
* **Team codes:** Lahman's differ from the file names: NYA (Yankees), LAN (Dodgers), SLN (Cardinals), KCA (Royals), ML4
  (Brewers), BRO, NYN, OAK, BOS, CIN, PIT, PHI, DET, BAL. The mapping is in the script, which checks each team-season
  exists exactly once.
* **Gaps in the data.** The 1927 `Batting` rows have no IBB, SF or GIDP (and many CS). The reference lists such stats under
  `notRecorded`; do not compare them. `Teams.CS` for 1927 is complete (64) but the player rows are partial, so never
  compare player sums with team totals for that year.
* **What Lahman cannot do:** it has no play-by-play and no base-out states, so it cannot say how often a team stole in a
  given situation or bunted with a runner on first; that needs Retrosheet. It has no platoon splits and no ballpark
  dimensions. It cannot validate decision quality (real managers are not optimal) or the win-probability tables.

## 4. Calibration methodology

**Ratio.** For each team and measure: simulated per game ÷ real per game (real total ÷ the team's games that season).
A median over the 15 teams says what the *engine* does; a team that departs from the median says something about that
team's data.

**What a perfect model scores.** Every team in the game is a strong club, and its staff allowed about 10% fewer runs than
its league average (mean of `RA ÷ league runs` over the 15: 0.90). In a tournament of these teams each faces a better
staff than the average one its real runs came against, so **a perfect engine scores about 0.90 of real runs, not 1.00**.
Judge scoring against 0.90.

**Noise.** Runs per game has a standard deviation near 3, so with about 280 games per team the standard error is about
0.18 runs, roughly 4%. Counts such as steals, caught stealing and sacrifices are noisier, and complete games more so
(a team with a 14% real rate has about 40 complete games in 280). A difference under about 8% on runs, or 25% on
steals, is not worth chasing at 10 games per matchup. Use `--games 20` or more before concluding anything about a rare
event, and compare the same seed before and after a change (seeded runs are exact, so a change in the numbers is a change
in behavior, not noise).

**Isolating a cause: experiments by switching.** Do not fix first and measure later. Switch the suspected input off, or
make it neutral, and see how much of the gap closes. Use a temporary override (an environment variable read in the code
under test) and **remove it afterwards**; the experiment is not the fix.

**Metrics that mislead.**
* *Outs per start* from the Lahman team totals is always about 27 (it is the whole team's outs per game, not a starter's).
  The reference now uses only pitchers who mostly started (`GS * 2 > G`): outs ÷ starts. Their relief outs are few and
  counted, so it runs slightly high.
* *Bunt rate* and *steal rate* in the benchmark are per **chance** (a situation), which Lahman cannot give; calibration can
  only compare per game (`SB`, `SH`).
* *Runs allowed* in a mixed-era tournament follows the opposing lineups, not only the staff.

## 5. Finding 1: scoring was 38% too low

First calibration run (analyst manager, 4,200 team-games), simulated ÷ real, median over 15 teams:

| Measure | Ratio |
|---|--:|
| Runs | 0.62 (0.54 to 0.68) |
| Hits | 0.74 |
| Home runs | 0.78 |
| Walks | 0.84 |
| Strikeouts | 0.75 |
| At-bats | 0.98 (so the measuring was right) |
| Errors | 1.00 |
| Steals | 1.60 (up to 4.35) |
| Double plays | 0.67 |
| Complete games | 0.36 |

### Cause

`ProbabilityTable.Build` scaled a batter's per-plate-appearance rates by the pitcher's ERA ÷ **4.00**, his strikeouts per
nine ÷ **7.0** and his walks per nine ÷ **3.0**. A batter's line already includes the pitching of his own league, so
a fixed reference double-counts it. League figures swing a great deal: ERA 2.98 (1968 American League) to 4.14 (1927),
strikeouts 2.8 per nine (1927) to 6.1 (1967), walks 2.6 to 3.9. A 1968 hitter was penalized for a league that was hard to
hit in, and again by the pitcher he faced.

### Experiments (all temporary, reverted)

| Experiment | Runs | Hits | Walks | Strikeouts |
|---|--:|--:|--:|--:|
| As it was | 0.62 | 0.74 | 0.84 | 0.75 |
| No pitcher scaling at all | **0.98** | 1.01 | 1.12 | 1.04 |
| Hit scaling only (walks, strikeouts neutral) | 0.67 | 0.76 | 1.05 | 0.96 |
| ERA, K/9, BB/9 each relative to the pitcher's league | 0.66 | 0.79 | 0.79 | 1.05 |
| Hits and home runs by their own rates, K/9 and BB/9 relative to the league | **0.83** | 0.93 | 0.85 | 1.06 |

Reading them:

* With no pitcher scaling the runs come back (0.98), so the pitcher modifier is the whole gap. Runs allowed then rises to
  1.22: without it every pitcher is average, which is also wrong. The answer is the *right* scaling, not none.
* **Making ERA relative to the league barely helped (0.66).** ERA is too strong a lever on hits: a staff 15% better in ERA
  allows only a few percent fewer hits, because strikeouts, walks, home runs and stranding all feed ERA. Scaling every hit
  by ERA made a good staff far too hard to hit, and every team here has a good staff.
* The fix that worked scales **non-home-run hits by the pitcher's own non-HR hits per nine ÷ the league's**, and **home
  runs by his HR per nine ÷ the league's** (see the shrinkage below).

### The fix (in the game)

* `LeaguePitching` (`Models/LeaguePitching.cs`): a league and season's ERA, non-HR hits, HR, strikeouts and walks per nine.
  `Team.LeaguePitching` and `PitcherStats.League` carry it; `TeamLoader` reads the `leaguePitching` block of each team
  file; `generate_teams.py` writes it (`league_pitching`).
* `ProbabilityTable.Build`: each modifier is the pitcher's rate ÷ his league's, so **an average pitcher of any era
  leaves the batter's line alone** (modifier 1.0). A file without `leaguePitching` falls back to the old fixed references
  (`LeaguePitching.Default`: 4.00, 7.0, 3.0), with ERA scaling every hit.
* **Home runs are pulled halfway toward the league rate** (`0.5 + 0.5 × ratio`): one pitcher's HR rate over a season is
  noisy, and unshrunk it gave home runs 1.07 of real. Non-HR hits are clamped to 0.6 to 1.5, strikeouts and walks to
  0.4 to 2.5, and the fatigue effectiveness divides all of them (a tired pitcher allows more).
* **Consequence:** when league hit rates are known, a pitcher's **ERA no longer changes the table** (his hits and home
  runs do). ERA still drives the managers' estimates and the display. Tests that step ERA to move a pitcher's quality
  (`ManagerDialsTests`, `ManagerCatalogTests`) give their pitchers `LeaguePitching.Default`.

### Result

| Measure | Before | After |
|---|--:|--:|
| Runs | 0.62 | 0.84 (expected 0.90 for a perfect model) |
| Hits | 0.74 | 0.93 |
| Home runs | 0.78 | 1.03 |
| Strikeouts | 0.75 | 1.06 |
| Walks | 0.84 | 0.84 |

Scoring went from about 3.2 to about 4.2 runs per team per game. The benchmark ranges that moved with it were re-baselined
(section 13).

## 6. Finding 2: starters tired in the wrong way

After finding 1 the starters were pulled far too early (complete games 0.12 of real).

### Causes

* **More hits means more pitches and more pulled starters.** Fixing scoring shortened every start.
* **The tiring factor could not tell starters apart.** `tf = 10 − innings per start`, rounded, so nearly every starter of
  every era came out as a 2, 3 or 4. Cuellar (1970 Orioles, 21 complete games in 40 starts) was a 3; Nolan (1975 Reds, 5
  in 32) was a 3. The thresholds moved only 5 pitches per step.

### The model

`FatigueModel.Stamina(PitcherStats)`, used when the starter's games started are known (the team files now carry `gs`):

```
stamina = clamp( outsPerStart × 4.9 + 15 × completeGameRate , 70, 140 )   // pitches
effectiveness: × 0.92 above stamina, × 0.80 above stamina + 20, × 0.65 above stamina + 35
```

`4.9` is the engine's measured pitches per out (calibration prints it; finding 6). Relievers, and files without `gs`, keep the tiring
factor. The managers pull by the pitcher's *condition*, so the same stamina gives each persona its own completion rate.

### What was tried

| Offset | Complete games | Outs per start | Pitchers per game | Verdict |
|---|--:|--:|--:|---|
| Tiring factor (before) | 0.12 | 0.92 | 0.99 | starters pulled too soon, no spread |
| `outs × 5.6 + 15 × CG rate` (no offset) | 1.95 | 1.09 | 0.68 | starters go too far |
| `outs × 5.6 − 18 + 15 × CG rate` (measured with a DH in every game) | 0.94 | 1.02 | 0.84 | chosen on wrong data |
| `outs × 5.6 − 18 + 15 × CG rate`, Historical rule | 0.51 | 0.94 | 1.02 | pitchers batting end starts sooner |
| `outs × 5.6 − 12 + 15 × CG rate`, Historical rule | 0.67 | 0.96 | 1.00 | chosen with aces starting (finding 5) |
| `outs × 5.6 + 15 × CG rate`, Historical rule, **rotation** | **0.74** | **0.94** | **0.97** | kept |
| `outs × 5.6 − 6 + 15 × CG rate`, Historical rule | 0.71 | 0.96 | 0.96 | fewer pitchers than real |

Pitchers per game fell to 0.84 of real, because the starters last longer; a more detailed bullpen-use model would be the
next lever.

### Result per team (analyst)

Outs per start run from 0.82 to 1.17 of real across the 15 teams, centered on 1.02. The two short ones are the 1979
Pirates and the 1967 Cardinals, whose staffs are hit hard by the strong lineups they face (runs allowed 1.18 and 1.30 of
real). Their real complete-game rates were 15% and 27%.

## 7. Finding 3: steals

Steals were 1.55 of real (1.04 to 4.82 by team). A steal is two things that fail for different reasons, so the calibration
now reports **attempts** (`SB + CS` per game) and **success** (`SB ÷ attempts`) beside steals and caught stealing, and
`--raw "steal attempts"` lists real and simulated per-game values team by team.

### What the first look showed

| | Ratio (median) | Range |
|---|--:|--|
| Attempts | 1.34 | 0.85 to 3.95 |
| Success | 1.11 | 0.88 to 1.71 |

Simulated success sat at 0.67 to 0.77 for every team, while real success ran 0.45 (1968 Tigers) to 0.82 (1975 Reds).

### Separating the engine from the manager

Two different things can be wrong, and only one is the engine's.

* **The success formula** (`BaseRunningEngine.StealSuccessChance`): a runner's own record steadied toward a league rate,
  adjusted for the pitcher's hold, the catcher's arm and his speed. **Check it without a manager:** weight each runner's model
  chance by how often he really stole (his `SB + CS`), against an average battery, and compare with the team's real
  success. That is `WeightedByAttempts_TheModelReproducesEachRealTeamsSuccessRate`. It showed the formula ran **4 points high
  on average** (up to 15 for poor teams), for two reasons:
  * the record was steadied toward a fixed 67% with ten attempts of weight, but real leagues stole at 57% to 69% (1927:
    61%, 1963: 58%, 1980 National: 69%);
  * the speed rating, which comes from his steals, was applied again (3% a step), counting speed twice.
* **Which runners go, and how often:** the manager's choice. The sim's attempt counts follow *runner quality* (the managers
  steal when the odds pay) far more than real teams did: the 1975 Reds and 1980 Royals, with 82% success, attempt 2.1 times
  their real rate, and the 1968 Tigers and 1975 Red Sox, with poor records, attempt half. Real teams attempted much less
  by success than an expected-run manager does, and the 1961 Yankees (0.28 attempts a game, 4.0 times in the sim) simply
  did not run. The team files already carry each club's real attempts per game (`Tendencies.StealAttemptsPerGame`), unused.

### The fix (engine side)

* **League steal rate by league and season**: `Team.LeagueStealRate` and `Player.LeagueStealRate`, read from the team file's
  `leagueStealRate`, which the generator writes (`league_steal_rate`: league SB ÷ (SB + CS)); the default stays 0.67.
* **`RunningRules.StealRecordWeight` 10 → 5**, so the runner's own record counts for more.
* **`RunningRules.StealPercentPerSpeedStep` 3% → 1.5%**, so speed is not counted twice.

| Measure | Before | After |
|---|--:|--:|
| Formula error against real team success (mean, attempt-weighted) | +4.3 points | +2.1 points |
| Steals (simulated ÷ real) | 1.55 | 1.13 |
| Steal attempts | 1.34 | 1.06 |
| Steal success | 1.11 | about 1.08 |
| Caught stealing | 1.20 | 0.92 |

Dropping the speed term entirely and weighting 5 gave a 1.5-point mean error (2.1 mean absolute), but the speed rating is the
only signal for a runner with no record; 1.5% was kept.

### What is still wrong

* **Attempts follow the wrong thing.** Median 1.06 hides a spread from 0.51 to 4.00. The managers steal when the odds pay;
  real clubs ran according to their manager's habits. Elite stealers (Reds 2.1, Royals 2.1, Phillies 1.6, Orioles 1.7) are
  sent too often, poor ones (Tigers 0.5, Red Sox 0.6, Dodgers 0.8) too seldom, and the dead-ball-style 1961 Yankees at 4.0 times.
  The engine cannot fix this: it needs managers that follow their club's real tendencies (an era-aware or "historical"
  persona), not a different success formula.
* **Success is compressed** (0.62 to 0.74 simulated, against 0.45 to 0.82 real). The manager picks the best spots, and the
  opposing batteries are strong and not those the runners really faced.
* **The runs ratio moved from 0.83 to 0.80** with the change (fewer successful steals): inside the noise of 10 games per
  matchup, but worth watching.

## 8. Finding 4: the free pitch, the double play and the sacrifice

After the designated-hitter correction (section 2) the engine scored 0.78 of real runs. The measures that were off:

| Measure | Ratio | Expected for a perfect model |
|---|--:|--:|
| Runs | 0.78 | 0.90 |
| Walks | 0.79 | about 0.91 (the opposing staffs' walk rate) |
| Strikeouts | 1.14 | about 1.00 |
| Hits | 0.91 | about 0.96 |
| Double plays | 0.76 | about 0.9 |
| Sacrifices | 0.77 | persona-driven |

"Expected" is each measure's mean over the 15 teams of their staff's rate against its league (walks 0.91, strikeouts 1.00,
non-home-run hits 0.96, home runs 0.98), the same adjustment as the 0.90 for runs (section 4).

### The free pitch (8% of scoring)

Neutralizing the pitch-type modifiers (an experiment, reverted) raised runs from 0.78 to 0.86 and moved strikeouts to 1.08
and hits to 0.97. The manager chooses the pitch that minimizes value on every plate appearance, and two of the four pitches
cost the pitcher nothing: the slider (−10% home runs, −10% singles, +15% strikeouts) and the curve (−15% home runs, −5%
singles) were worth about 0.008 and 0.012 runs per plate appearance against an ordinary hitter, about 0.4 runs a game. It is
the same defect as the free alignments (`NoFreeOptionTests`, ComputerManagers.md section 10).

**Fix (`ProbabilityTable.ApplyPitchType`).** The pitch character stays (home runs, walks, strikeouts); each pitch's single
multiplier now balances its trade by linear weights for a .290 hitter (single .47, double .77, triple 1.04, home run 1.40, walk
.33, strikeout −.03 against an out in play):

| Pitch | Singles before | Singles after |
|---|--:|--:|
| Fastball | ×1.00 | ×0.99 |
| Curveball | ×0.95 | ×1.05 |
| Slider | ×0.90 | ×1.045 |
| Changeup | ×1.10 | ×1.125 |

Runs went to 0.86 and hits to 0.99. The pitches still differ by batter: a slugger is best pitched with the changeup, a slap
hitter with the fastball (`PitchBalanceTests`). The pitchout test compares with the average of the four ordinary pitches now,
since they trade hits for other things.

### Double plays (0.76 to 0.85)

Calibration now counts **chances**: plate appearances with a runner on first and fewer than two out. Before the change the
engine turned two on 9.3% of them, against about 10.5% in real baseball (Retrosheet-era league rate; Lahman cannot give it),
and 42.9% of the ground balls there. The engine has no fielder's choice (a ground out retires the batter and moves the
runners), so the double-play share of ground outs has to carry the whole rate: `RunningRules.DoublePlayChance` **0.45 →
0.52** gives 10.4% of the chances and 47.9% of ground balls. The remainder of the gap (0.85) is fewer chances, because the
engine walks 0.73 of the real rate.

### Walks (0.73)

First explained here as the roster and missing intentional walks. **That explanation was wrong** (see finding 5): a batter's
walk rate already includes his intentional walks, and the roster accounts for only part of the gap.

### Sacrifices (0.41 → 0.86)

* **The data.** The team files predated sacrifice hits, so every hitter had the average bunting rating, and pitchers had no
  batting line. They now carry `sh` per batter, each pitcher's real batting line (`bat`, `bats`) where he batted, and the
  club's real habits (`tendencies`: steal attempts, sacrifices, complete games and saves per game). The patch added them from
  the server by matching names and checking at-bats, and `generate_teams.py` already writes all of them.
* **The rule.** Most of the jump was the designated hitter: with pitchers batting they bunt, at 6.5% of their plate
  appearances (real: 7.2%, 7% in the data's pitchers' lines), which is 0.147 sacrifices a team game.
* **What is left is the manager.** The expected-run manager bunts when the run values say so and real managers bunted more
  (the 1927 Yankees 1.3 a game, 0.42 simulated). Small ball is nearer to real. Nothing in the engine is wrong here; an era-aware
  manager (open problem 1) is the fix.

## 9. Finding 5: the ace started every game

Walks stood at 0.73 of real with about 0.91 expected, and strikeouts at 1.08 to 1.10 with about 1.00 expected.

### Splitting the gap (temporary experiments, reverted)

| Neutralized | Runs | Walks | Strikeouts |
|---|--:|--:|--:|
| Nothing | 0.84 | 0.73 | 1.08 |
| Pitcher walk modifier | 0.95 | **0.98** | 1.13 |
| Pitch-type walk multipliers | 0.82 | 0.77 | 1.14 |

* **The batters were not the cause:** the eight regulars' walk rate is 1.06 of their team's.
* **Pitch choice was not the cause** (4 points).
* **The pitcher modifier acted like 0.745** (0.73 ÷ 0.98), although the files' pitchers average 0.85 of their league's walk
  rate. So the pitchers on the mound were not the average of the file.

### Cause

`--calibrate` never chose a starter, so every game used the default: **the ace**. A real season was pitched by a rotation.

| Mean over the 15 teams, relative to the league | Ace | Rotation |
|---|--:|--:|
| Walks per nine | 0.69 | 0.80 |
| Strikeouts per nine | 1.11 | 1.01 |
| Non-home-run hits per nine | 0.92 | 0.95 |
| Share of starts finished | 41% | 33% |

### Fix (the tool, not the engine)

`Calibration.NextStarter` gives each start to the starter furthest behind his share of the team's real games started.

| Measure | Ace every game | Rotation |
|---|--:|--:|
| Runs | 0.84 | **0.90** |
| Runs allowed | 1.03 | 1.13 |
| Hits | 0.98 | 1.02 |
| Walks | 0.73 | **0.83** |
| Strikeouts | 1.10 | **1.04** |
| Double plays | 0.85 | 0.89 |
| Complete games | 0.67 | 0.46 |
| Starter outs per start | 0.96 | 0.92 |
| Sacrifices | 0.86 | 0.77 |
| Steals | 1.18 | 1.26 |

**Every earlier finding was measured with aces starting.** Their causes and fixes stand (each was shown by switching it off or
checked without a manager), but their ratios are not comparable with section 11's.

### What is left of the walk gap (0.83)

The engine with no pitcher walk modifier walks 0.98, and the pitchers the files carry walk about 0.85 of their league's rate
where the real staffs walked 0.91 (the files hold 83% of each staff's innings: the best of them). So about 0.89 is reachable
with these rosters, and the rest is the missing tail of each staff. Adding more pitchers to the team files would be the fix;
bending the modifier to compensate would not.

### Tried and rejected: modifiers per batter faced

The table is per plate appearance, while the pitcher modifiers compare rates per nine innings, and a good pitcher faces
fewer batters per nine. Converting them (batters faced estimated as outs, hits and walks):

| | Runs | Hits | Walks | Strikeouts |
|---|--:|--:|--:|--:|
| Per nine innings (kept) | 0.90 | 1.02 | 0.83 | 1.04 |
| Walks and strikeouts per batter faced | 0.91 | 1.03 | 0.84 | 1.07 |
| Walks, strikeouts and non-home-run hits per batter faced | 0.94 | 1.05 | 0.84 | 1.08 |

One point of walks for worse hits and strikeouts: not adopted.

### The stamina offset, re-tuned against a rotation

The offset had been tuned while aces started every game. Re-measured with a rotation:

| Stamina offset | Complete games | Outs per start | Pitchers a game |
|---|--:|--:|--:|
| −12 | 0.46 | 0.92 | 1.03 |
| −6 | 0.62 | 0.93 | 1.00 |
| **0 (adopted 2026-11-25)** | **0.74** | **0.94** | **0.97** |
| +6 | 0.79 | 0.95 | 0.95 |

In a game as played the default starter is still the ace, and an ace finishes more often than his rotation (41% against 33% of
starts in the real data): the benchmark, which starts aces, now has the analyst's starter finishing 48% of games.

## 10. Finding 6: too many pitches

Browser games after the offset was set to 0 showed five of six starts going nine innings, and 2,000 headless games of the
default matchup showed 22% of starts at 150 pitches or more (longest 186).

### Causes

* **The pitch estimate ran high:** 3.98 pitches a batter and 5.6 an out, about 150 for nine innings. Strikeouts drew 4 to 7,
  walks 5 to 7, balls in play 1 to 6. The game of 1927 to 1982 is believed to have run near 3.6 (pitch counts were not
  recorded and Lahman has none, so this figure is background knowledge, not data).
* **Stamina could reach 150**, so a finisher did not begin to tire until then.

### Fix

* `AtBatResolver.EstimatePitches`: strikeout 3 to 7, walk 4 to 7, hit by pitch 1 to 5, in play 1 to 5: **3.48 a batter, 4.94
  an out**. One draw per play, as before.
* `FatigueModel.PitchesPerOut` 5.6 → 4.9 and the stamina cap 150 → 140 (offset stays 0).
* The managers' pitch thresholds scaled by the same 0.875, so each acts at the same point of an outing: `WarmUpPitchCount`
  65 → 57 (slugger 90 → 79, tinkerer 45 → 39), the tinkerer's `QuickHookPitchCount` 60 → 52 and `StarterPitchLimit`
  100 → 88, and the warm-arm limit 90 → 79.
* The slugger's `ShelledRuns` 6 → 7: he and the analyst had become indistinguishable in pitching changes, which his
  profile (less quick with the bullpen) forbids.

### Sweep (rotation, cap 140, 20 games per matchup)

| Offset | Complete games | Outs per start | Pitchers a game | Starts of 130+ | Starts of 150+ |
|---|--:|--:|--:|--:|--:|
| −6 | 0.71 | 0.94 | 0.97 | 6.6% | 0.1% |
| **0 (kept)** | **0.79** | **0.95** | **0.94** | **9.0%** | **0.4%** |
| +6 | 0.84 | 0.96 | 0.92 | 11.7% | 0.7% |
| +18 | 1.00 | 0.97 | 0.87 | 15.2% | 2.2% |

Higher offsets reach real complete games only by working starters harder and using too few pitchers. The cap barely matters
(130 and 140 give the same results). (After the threshold scaling the kept setting measures complete games 0.77.)

### Result, and what is still long

| 2,000 games, 1927 Yankees at 1955 Dodgers, analyst | Before | After |
|---|--:|--:|
| Yankees starter, 150+ pitches | 22.9% | 4.8% |
| Dodgers starter, 150+ pitches | 22.1% | 1.3% |
| Longest start | 186 | 167 |

This matchup is the extreme: both aces finished most of their real starts. Still, 24% of its starts reach 130 pitches. A
hard pitch limit for the managers would be the next lever if that is too many.

`--calibrate` now prints a `workload:` line (pitches a batter, share of starts of 130+ and 150+, the longest) so this is
visible without a browser.

## 11. Current state

Calibration (2026-11-25), analyst on both sides, Historical designated-hitter rule, **starters in rotation**, 20 games per
matchup (8,400 team-games), simulated ÷ real, median over the 15 teams:

| Measure | Ratio | Judgment |
|---|--:|---|
| Runs | 0.91 | on target (0.90 is perfect here) |
| Runs allowed | 1.13 | high: the opposing lineups are all strong |
| At-bats | 1.03 | good |
| Hits, doubles, home runs | 1.02, 1.02, 1.05 | good |
| Triples | 0.95 | fine (small counts) |
| Strikeouts | 1.05 | good |
| Walks | 0.83 | a little low (about 0.89 reachable with these rosters) |
| Errors | 0.96 | good |
| Double plays | 0.89 | a little low |
| Pitchers used per game | 0.96 | good |
| Starter outs per start | 0.94 | good |
| Complete games | 0.77 | a little low (manager behavior) |
| Steals, caught stealing | 1.25, 0.97 | a little high; noisy |
| Steal attempts | 1.13 | **wrong by team** (0.58 to 5.11) |
| Steal success | 1.05 | good |
| Sacrifices | 0.73 | low (manager behavior) |
| Pitches a batter faced | 3.48 | about 3.6 believed real (not in Lahman) |
| Starts of 150+ pitches | 0.6% or less | plausible |

Benchmark (designated hitter, default starters, 6,000 games, `--check` passes): about 4.4 runs a team a game; see
[ComputerManagers.md](ComputerManagers.md) section 9. It still starts each team's ace, which is fine for comparing managers
with each other.

## 12. What works, and what does not

### Works

* **Calibration as a method.** It found a 38% scoring defect that every earlier test and benchmark missed.
* **Switch-it-off experiments** to isolate the cause before fixing (section 5): each experiment moved one input and the
  result pointed straight to the pitcher scaling.
* **League-relative scaling with separate hit and home-run rates.** It keeps an average pitcher exactly neutral in every
  era and is the only variant that closed the gap.
* **Balancing each pitch by linear weights** (no free pitch), and **counting double plays per chance** rather than per
  game: each exposed a defect the per-game totals blurred.
* **Checking a formula without the manager** (attempt-weighted success against the real team's): it separates what the engine
  gets wrong from what a manager chooses.
* **Per-starter stamina from real starts.** It makes outs per start and complete games follow the real team, with real
  spread between starters.
* **Seeded, committed reference data.** Calibration and the benchmark run offline and give exact, repeatable numbers; a
  change in the numbers is a change in behavior.
* **The opponent-strength adjustment (0.90).** Without it the engine would look 10% worse than it is and invite a wrong fix.
* **Byte-identical benchmark output** as a refactoring check (constants moved to `RunningRules`, the decision cache): equal
  text before and after proves no behavior moved.

### Does not work

* **A calibration option the code did not apply** (the Historical rule that was not used): read the printed options back, and
  check `pitchers batting` in the report is not zero before trusting a run.
* **Pitch modifiers that are not zero-sum** (the slider and the curve): a manager who picks the best pitch each time takes them.
* **A fixed league steal rate (67%) and a double-counted speed rating**: they put every poor-stealing team's success 4 to 15
  points above its real rate.
* **Judging steals by the count alone**: it hides whether the attempts or the success is wrong, and they have different causes.
* **A calibration that starts the ace every game.** It made every opponent far better than the real one and sent us looking
  for an engine defect (walks). Check *who played* before blaming a rule.
* **Judging pitcher use by outs and complete games alone:** both looked right while one start in five ran past 150 pitches.
  Check the pitch counts too.
* **Per-batter-faced pitcher modifiers**: one point of walks, worse hits and strikeouts (finding 5).
* **Fixed scaling constants** (ERA ÷ 4.00, K/9 ÷ 7.0, BB/9 ÷ 3.0). They ignore the era and double-count the league.
* **ERA relative to the league** as the hit scale (0.66 runs): ERA is a much stronger lever than hits.
* **Unshrunk home-run rates:** one pitcher's season HR rate is too noisy (home runs 1.07, with extremes of 0.80 to 1.19).
* **The whole-inning tiring factor** for starters: no spread, and it cannot express complete-game ability.
* **Comparing player sums with team totals for 1927**, and any use of the 1927 IBB, SF or GIDP.
* **Team outs per game as "outs per start".**
* **Era-agnostic managers against a real era's usage.** A modern analyst pulls starters when they tire; a 1927 manager
  left them in. Pitching usage in calibration is partly manager behavior, not only fatigue; compare with the persona that
  matches the era (slugger and old school leave starters in longest), or build the era-aware persona (review item 11 in
  [ComputerManagers-Review.md](ComputerManagers-Review.md)).
* **Small samples for rare events.** Steals by team (1.0 to 5.07), complete games and sacrifices swing too much at 10 games
  per matchup.

## 13. The tests that guard this

| Test | File | What it guards |
|---|---|---|
| `AnAveragePitcherOfAnyEra_LeavesTheBattersLineAlone` (3 eras) | `LeaguePitchingTests.cs` | an average pitcher gives modifier 1.0 against the old neutral baseline |
| `AGoodPitcherAllowsFewerHitsThanAnAverageOne_ButNotInProportionToHisEra` | same | hits do not follow ERA one for one |
| `WithLeagueFigures_HitsFollowTheHitRatesNotTheEra` | same | ERA is ignored when hit rates are known |
| `WithoutLeagueFigures_TheEraStillScalesHits` | same | the fallback scale |
| `ATiredPitcherAllowsMoreHits` | same | fatigue raises hit rates |
| `EveryShippedTeamCarriesItsLeaguesPitching...` | same | every team file has plausible `leaguePitching`, pitchers share it |
| `ATeamFileWithoutLeagueFigures_StillLoads...` | same | old files keep working |
| `AStarterWhoGoesDeeperTiresLater`, `...FinishesGames...`, `WithoutRealStarts...`, `StaminaIsKeptToAPlausibleRange` | `StaminaTests.cs` | the stamina model |
| `EveryShippedStarterCarriesHisGamesStarted_AndTheirStaminaDiffers` | same | `gs` is in the data and stamina has a spread over 30 pitches |
| steal of third, double steal, squeezes, double-play odds, `StealOfSecond` filter | `BuntAndHitAndRunTests.cs`, `PlayTemplateTests.cs`, `ManagerModelTests.cs` | the running plays; `TheRunningRulesAreTheOnesTheEngineRolls` checks `RunningRules` is shared |
| `ARunnerWithNoRecordSucceedsAtHisLeaguesRate`, `ALongRecordOutweighsTheLeaguesRate`, `AShortRecordMovesTowardTheLeagueRateMoreThanALongOne`, `EveryShippedTeamCarriesItsLeaguesStealRate...`, `ATeamFileWithoutALeagueStealRate_UsesTheDefault`, `WeightedByAttempts_TheModelReproducesEachRealTeamsSuccessRate` | `StealCalibrationTests.cs` | steal success follows the record and the league, and matches the real teams (mean error under 4 points; reads `lahman-reference.json`) |
| `AgainstAnOrdinaryHitter_NoOrdinaryPitchBeatsAnother`, `ThePitchesStillDiffer_ByWhatTheyTradeAgainstTheHitter`, `TheDoublePlayChanceIsTheOneTheCalibrationSet`, `EveryShippedTeamCarriesItsSacrificeHits_ItsPitchersBattingLines_AndItsHabits` | `PitchBalanceTests.cs` | pitches are balanced (under 0.0035 runs per plate appearance apart) and still differ by batter; the 0.52 double-play chance; the data has `sh`, pitcher batting lines and `tendencies` |
| `StartersTakeTurnsInProportionToTheGamesTheyReallyStarted`, `ATeamFileWithoutGamesStarted_SharesTheStartsEvenly` | `CalibrationRotationTests.cs` | calibration pitches a real rotation, not the ace |
| `APlateAppearanceTakesAboutThreeAndAHalfPitches_WithStrikeoutsAndWalksLonger`, `TheStaminaFormulaUsesTheEnginesOwnPitchesPerOut` | `PitchEstimateTests.cs` | the pitch estimate stays plausible, and the stamina formula uses the measured pitches per out |
| `NoFreeOptionTests` | engine tests | no alignment beats Normal for free |
| `ManagerBenchmarkTests` | engine tests | the benchmark tool itself (seeding, counts, ranges check) in a few seconds |
| Benchmark `--check` | `expected-ranges.json`, CI `manager-benchmark.yml` (non-blocking) | persona behavior stays inside ranges |

`--calibrate` is **not yet a test**: it prints ratios and does not fail. Proposed (not built): tolerances by measure in a
JSON file, set from the sampling noise (section 4), checked with a few more games per matchup. Suggested targets once the
open problems below are closed: runs 0.90 ± 8%, hits ± 6%, home runs ± 10%, errors ± 15%, steals ± 25%, double plays
± 20%, complete games ± 35%.

### Benchmark ranges that were re-baselined

Ranges in `expected-ranges.json` moved with the scoring and fatigue changes. A range may change only for a change that is
meant to move it; record why here.

| Range | From | To | Why |
|---|---|---|---|
| analyst `buntRate` | 0.06 to 0.20 | 0.03 to 0.20 | more scoring makes a bunt worth less |
| smallball `buntRate` | 0.12 to 0.30 | 0.05 to 0.30 | same |
| slugger `aggressiveRate` | 0 to 0.08 | 0 to 0.14 | more baserunners |
| slugger `completeGameRate` | 0.14 to 0.26 | 0.10 to 0.40 | per-starter stamina; real teams averaged about 0.28 |
| analyst and one other persona `pitchingChangesPerGame` (superseded below) | 0.9 to 1.5 | 0.8 to 1.5 | stamina offset −12 keeps starters in a little longer (the analyst makes 0.89) |
| analyst and smallball `pitchingChangesPerGame` | 0.8 to 1.5 | 0.6 to 1.5 | stamina offset 0: starters last longer |
| oldschool `pitchingChangesPerGame` | 1.1 to 1.6 | 0.9 to 1.6 | same |
| slugger `pitchingChangesPerGame` | 0.7 to 1.2 | 0.55 to 1.2 | same |
| slugger `completeGameRate` | 0.10 to 0.40 | 0.20 to 0.55 | same, and the benchmark starts aces |

The pitch-estimate change (finding 6) re-baselined no range; it changed the slugger's `ShelledRuns` instead, and lowered a
floor in `ManyComputerGames_AlwaysEndLegallyWithRealisticBullpenUse` from 0.2 to 0.15 pitching changes.

## 14. Open problems, in order

1. **Steal attempts follow runner quality, not the club.** The success formula is calibrated (section 7); the managers still
   send good stealers about twice as often and poor ones half as often as the real clubs did. Needs managers that follow their
   club's real tendencies (`Team.Tendencies.StealAttemptsPerGame` is in the files, unused): an era-aware or "historical"
   persona, a design decision (review item 11).
2. **Sacrifices (0.73) and complete games (0.77) are partly manager behavior.** The data is in place (section 8); the analyst
   bunts and leaves starters in less than real clubs did. Same remedy as 1.
3. **Walks 0.83.** The team files hold only the best 83% of each staff's innings; more pitchers per team would close most of it.
4. **Runs allowed 1.13.** Expected in part (every opponent is strong); a league-average opponent (item 6) would show how much.
5. **Calibration as pass/fail** with the tolerances in section 13.
6. **A league-average opponent** instead of the other 14 strong teams, so the 0.90 adjustment is not needed.
7. **Era-aware managers** (a "Historical" persona), so complete games and bullpen use can be compared with the era.
8. **Park factors** (`BPF`, `PPF` are in the data) and their effect on calibrated runs.
9. **Regenerate the team files from the server** rather than patching them: `leaguePitching`, `leagueStealRate`, `gs`, `sh`, the
   pitchers' `bat` and `bats` and `tendencies` were added to the 15 files by script, and `generate_teams.py` now writes all
   of them, but a regeneration has not been run end to end.

### Known unrelated test failures

The Playwright suite (`dotnet test tests/MiniLeague.Web.E2E`) had four failures on 2026-11-25 that were not investigated
for cause and do not look related to pitching: `TheHomePageLoads` and `TheMenuOpensTheAboutPageWithTheDisclaimerAndTheCredits`
wait for the text "A modern recreation of the 1984", which the committed About page does not contain;
`SetupKeepsOptionalChoicesCollapsedUntilRequested` (the mobile setup layout) and
`StoppingTheCastInTheMiddleOfAPlayLeavesTheGamePlayable` (waiting on the PITCH button).

## 15. Adding more teams or other eras

Observations from reading the code (2026-11-25). **None of this has been tested with new teams**: treat each risk as a
prediction to check, not a finding. Everything in this document was calibrated or checked against 15 great teams of 1927 to
1982 only.

### Adjusts by itself

These come from each team's own data, so an average or poor team from the same decades should mostly just work:

* pitcher quality against his own league (hits, home runs, walks, strikeouts: `leaguePitching`);
* steal success (`leagueStealRate`) and error rates (`eraErrorsPerGame`, `eraStrikeoutsPerGame`);
* starter stamina from real starts (`gs`, innings, complete games);
* fielding ratings, which are relative to the league at each position;
* the Historical designated-hitter rule (season and league).

### Era-dependent constants to re-check

The modern figures below are background knowledge, not measured from Lahman.

| Number | Set now | Why a new era strains it |
|---|---|---|
| Pitches a batter (`EstimatePitches`) and `PitchesPerOut` | about 3.5 and 4.9 | modern games run near 3.9 pitches a batter |
| Stamina cap and complete-game bonus; the managers' pitch thresholds | tuned to men who finished games | modern starters rarely finish and are pulled near 100 pitches |
| `RunningRules.DoublePlayChance` | 0.52 | one value for every era |
| `WinProbability` run expectancy and scoring chance | one scoring level | changes what managers think a bunt or a steal is worth |
| Pitch-type balance (`ApplyPitchType`) | balanced for a .290 hitter | strikeout-heavy eras change the trade |
| Extra-base base chances, platoon modifiers | fixed | not checked against any era |

### Ratings with absolute cutoffs

Likely the most quietly wrong, because nothing fails when they are:

* **Speed** (`generate_teams.py`, `speed_rating`): steals and triples per 600 at-bats against fixed cutoffs. Teams from
  low-steal decades would all rate slow, and high-steal ones fast.
* **Power** (`TeamLoader`): raw home run totals (30, 15, 5), so strike-shortened seasons and dead-ball teams are distorted.
  It decides which shifts a computer manager considers.
* **Bunting** (`TeamLoader.BuntRating`): raw sacrifice hits per plate appearance, so modern teams would nearly all rate as
  poor bunters.

Making these relative to the team's league and season should come before adding other eras.

### Weak teams

Nothing has been calibrated against a bad club. Pitchers far from average could reach the clamps on the modifiers (section 5).
The team files carry only the best pitchers of each staff (finding 5), which may flatter a weak staff more than a great one.

### Calibration and the benchmark

* **The 0.90 runs target** (section 4) exists only because every opponent is strong. With average teams added it moves toward
  1.00 and must be recomputed from the staffs' rates against their leagues. This is an improvement: the comparison gets fairer.
* **`expected-ranges.json`** needs re-baselining, because the benchmark plays every team file.
* **Calibration run time** grows with the square of the number of teams: lower `--games` per matchup.
* **The team list is hard-coded** in `export_lahman_reference.py` and in the generator; `lahman-reference.json` must be
  regenerated for new teams, or calibration leaves them out.
* **A median over mixed eras can hide opposite errors:** report calibration by decade.

### Tests that assume today's teams

| Test | Assumption that would break |
|---|---|
| `EveryShippedTeamCarriesItsLeaguesPitching...` | league ERA 2.5 to 4.5, strikeouts 2 to 7 and walks 2 to 4.5 per nine |
| `EveryShippedTeamCarriesItsLeaguesStealRate...` | steal success 50% to 75%, and never exactly the 0.67 default |
| `EveryShippedStarterCarriesHisGamesStarted...` | a starter's outs per start between 12 and 28 |
| `ManagerBenchmarkTests` | 15 teams (30 games for one pairing, both seats) |
| `FieldingAndParkTests` | league errors per team game 0.4 to 2.5 |

### Rules and data gaps

* Modern rules are not modeled: the extra-inning runner, the three-batter minimum, the pitch clock.
* Older seasons have missing statistics (1927 already does: section 3). Without caught-stealing figures a team falls back to the
  default steal rate.

### Suggested order

1. Add a few **average and poor teams from 1927 to 1982**, run `--calibrate`, and see whether any measure moves with team
   quality.
2. Make the speed, power and bunting ratings **relative to the league**.
3. Add one team each from the 1990s and the 2010s as probes; expect to make the pitch estimate, stamina and the managers'
   pitch thresholds depend on the season.
4. Report calibration **by decade**.

## 16. Pitfalls and gotchas

* **Calibrate with one manager on both sides, and say which.** Different personas change running, bunting, pitching
  changes and complete games; the engine's batting line is much less affected.
* **Use the Historical designated-hitter rule** so each real league's rule applies; the benchmark ranges use the default
  designated hitter, and `--check` refuses another rule.
* **Check the options a calibration printed.** `pitchers batting` in the report should be about 2 plate appearances a team game
  under the Historical rule and 0 under `--dh designated`.
* **A team file also carries `leagueStealRate`.** Without it its runners are steadied toward 0.67.
* **Team files carry leaguePitching.** A hand-written team file without it silently uses the old fixed scale, and its
  pitchers' ERA scales every hit.
* **`--raw "steal attempts"`** (any measure name) lists real and simulated values team by team.
* **ERA does not move the table** when league hit rates are present. To make a test pitcher better or worse, change his
  hits, home runs or strikeouts, or give him `LeaguePitching.Default`.
* **Do not run two `dotnet` builds at once** (the E2E suite and the benchmark both build); wait for one to finish.
* **Edit with a script that preserves line endings**; the repository's files are CRLF in places.
* **Keep the temporary experiment out of the commit:** the environment-variable overrides used to isolate a cause belong
  to the experiment, not the game.

## 17. Keeping this document current

Update this document in the same change when you:

* alter `ProbabilityTable`, `FatigueModel`, `RunningRules`, base running, the double-play odds, or the era data
  (`leaguePitching`, `eraErrorsPerGame`, `eraStrikeoutsPerGame`, `gs`);
* re-run calibration after such a change (record the date, the options and the new ratios in section 11);
* re-baseline `expected-ranges.json` (add a row to the table in section 13, with the reason);
* learn that something does not work (add it to section 12, with the evidence);
* add or change a test that guards modeling behavior (section 13);
* regenerate `lahman-reference.json` (note a change of source or schema in section 3);
* add teams or eras (replace the predictions in section 15 with what was measured).

Keep the numbers dated, and say which options produced them (games per matchup, persona, designated-hitter rule, seed).
