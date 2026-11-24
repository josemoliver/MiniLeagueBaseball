# Computer managers: review and recommendations

Reviewed 2026-11-24 against [ComputerManagers.md](ComputerManagers.md) and the code in the working tree at that date.
This is an analysis for future work. No code was changed for it.

**Goal.** The aim is not an unbeatable computer opponent but intelligent, believable managers whose decisions reflect
different baseball philosophies, strategic preferences and situational judgment, while staying challenging and fun to
play against.

## Contents

1. [Summary](#1-summary)
2. [Findings](#2-findings)
3. [Recommendations, in priority order](#3-recommendations-in-priority-order)
4. [Tests and benchmarks to add](#4-tests-and-benchmarks-to-add)
5. [Suggested order of work](#5-suggested-order-of-work)

---

## 1. Summary

The manager architecture is sound:

* the expected-run managers price each call with the same probability table the engine rolls on
* the personas differ only through named settings (`ManagerTuning`)
* adding a manager is one catalog entry

But today the **engine decides more about how the managers play than their personalities do**:

* **Free options.** Several choices cost nothing in the engine, and the managers find them. Aggressive running is the
  clearest case: about 17 calls a game, worth about 0.45 runs a game.
* **Constant data.** Much of the player data the managers would use for matchups is the same for every player. The
  Tinkerer's matchup style has almost nothing to work with.
* **No pitcher at bat.** Pitchers never bat in any era. That removes the core of how 11 of the 15 teams' seasons were
  managed.

Fixing those three is the precondition for the personalities to matter.

## 2. Findings

| # | Finding | Evidence | Effect |
|---|---|---|---|
| 1 | **Aggressive running can't fail.** A runner sent hard can take an extra base but is never thrown out. | `BaseRunningEngine.Advance`: aggressive sends only add bases (55 vs 30, 50 vs 0, 60 vs 0 chances) and have no out result. | Every expected-run manager calls it about 17 times a game. It is about 0.45 of the 1.33-run edge over Gus Halloran, and a human can use it too. |
| 2 | **Infield In and Corners In work backwards.** Infield In cuts singles by 15% and removes the double play, yet the runner on third still scores on every ground out. Corners In cuts singles by 20% on every swing, not only on bunts. | `ProbabilityTable.ApplyAlignment`; `BaseRunningEngine.GroundOut` always scores the runner from third with fewer than two out. | Infield In is close to free whenever a runner is on third. Its real purpose, cutting down the run at the plate, does not exist. |
| 3 | **There are no left/right splits.** `TeamLoader` sets `VsLeft`, `VsRight` and `Overall` to the same numbers. The probability table uses the batter's side only for the ballpark's home-run factor. | `TeamLoader.cs` (batters and pitchers); `ProbabilityTable.cs`, the `hrFactor` line | Platoon matchups, reliever matchups and the Tinkerer's whole style have no input. `ComputerManagers.md` describes platoon splits that do not exist and should be corrected. |
| 4 | **Several ratings are the same for every player.** Bunting, sacrifice, hold-runners and ground-ball rate are not loaded from the data. | The team JSON has none of these fields, and the loader does not set them. | Corners In is never offered (it needs a batter with bunting or sacrifice rated 4 or more). Bunt and steal odds vary only through speed and the steal record. |
| 5 | **Pitchers never bat.** Lineups are the first nine non-pitchers. `GameState.DHRule` exists but is never set. | `TeamLoader.cs` (`DefaultLineup`); a probe game ran to completion without the pitcher coming to bat. | 11 of the 15 teams played without a DH. Pinch-hitting for the pitcher, double switches and pitcher sacrifice bunts, central to managing before 1973 and in the NL, cannot happen. Gus Halloran's pitcher-bunt rule never fires. |
| 6 | **Pitch types are fixed multipliers with no repertoire.** | `ProbabilityTable.ApplyPitchType`: for example, a changeup cuts home runs by 20% for every pitcher. | The defense always throws the same pitch to the same kind of batter. That is predictable, ignores who is pitching, and a human can work it out. |
| 7 | **A human can still exploit the pitchout.** Against a runner who does not go, it still resolves as a plate appearance with hits cut to 10% and walks doubled. | `ProbabilityTable.ApplyPitchType` | The managers now price a pitchout as a wasted ball, but the engine rule remains for a human fielder. |
| 8 | **The run-value table is modern and the same for every era.** | `WinProbability.RunExpectancy` (0.49 runs in a fresh half-inning) | A 1968 game (about 3.4 runs per team) and a 1927 game value a bunt the same way. |
| 9 | **The managers value outcomes in runs, not wins.** The only late-game adjustment is a crude blend, although `WinProbability.HomeWins` already exists. | `ManagerDecisionModel.StateValue` | They cannot tell tied in the bottom of the ninth from tied in the top, and do not plan for extra innings or for saving the closer for a home tie. |
| 10 | **Seeded games cannot be repeated when a side has a persona.** | `ManagerCatalog.Create(id)` is called with no random source, so it uses `Random.Shared`. | Computer-versus-computer benchmarks cannot be reproduced. This is why the test helper `PlayDriver` was needed. |
| 11 | **Engine rules are copied into the managers' model.** The hit-and-run weights, the 35% line-out share, the 0.45 and 0.12 double-play rates and the speed-check numbers all appear twice. | `ManagerDecisionModel` vs `AtBatResolver` and `BaseRunningEngine` | The two copies will drift apart without anyone noticing. |
| 12 | **The profile ratings promise more than the managers do.** | Measured: the Tinkerer and the Analyst are within 0.3 points of each other in the round robin and barely differ in bullpen use. The intentional-walk conditions rarely open. | The Tinkerer's "Bench use 5" and "Intentional walks 4" are not visible in play. Small ball against the Slugger is the only clearly visible difference. |

### Findings by area

* **Decision-making.** The managers decide steals and hit and runs only with a runner on first; bunts, intentional walks
  and pitchouts are priced in expected runs. The free options (findings 1, 2 and 7) and the fixed pitch effects
  (finding 6) dominate what they actually do.
* **Roster and bullpen.** Pitching changes are driven by fatigue, by being shelled and by edges against the next three
  hitters. Without platoon effects (finding 3) relievers differ only by ERA, walks and strikeouts. No manager makes
  late-inning defensive replacements, although `GameService.Replace` supports them. Injury replacement is shared by every
  manager and is reasonable.
* **Personalities.** Small ball and the Slugger are clearly distinct. The Analyst and the Tinkerer are close in practice.
  Gus Halloran is distinct and weak (35.5% in the round robin), which suits an easy opponent.
* **Historical realism.** All eras share one run environment (finding 8), a universal DH (finding 5), and the same
  steal, bunt and bullpen habits. The data already includes each team's complete games, saves and steals, which could
  give each era its own habits.
* **Situational awareness.** The managers do take into account score, inning, outs, bases, the batter and pitcher,
  fatigue, the ballpark, bench size and the closer's role. They lack win probability (finding 9), lookahead beyond the
  next three hitters, and platoon matchups (finding 3).

## 3. Recommendations, in priority order

Complexity: **S** is under a day, **M** is a few days, **L** is a week or more.

### P0: remove free options (engine)

Do these first. Every persona result depends on them.

1. **Give aggressive running a cost (S, engine).** When the speed check fails on a hard send, the runner is thrown out
   at a rate that depends on the outfield arm and his speed. Expected effect: aggressive calls fall from about 17 a game
   to a handful, Small ball's style starts to carry risk, and Gus Halloran's win rate rises toward roughly 42–45%.
2. **Fix the alignment rules (S–M, engine).**
   * Infield In: the runner on third is held, or thrown out at home, on ground balls, and more singles get through.
   * Corners In: affects bunts only; on swings, more balls get past the corners.
   * Guard Lines: re-balance so it is a net loss early in a game.
   * Add a property test: every non-standard option is worse than the default in some situation, so new free options are
     caught automatically.
3. **Resolve a pitchout against a batter who is not running as a wasted ball (S, engine).** For example, a small rise
   in the walk rate and no change to hits, instead of hits cut to 10%.
4. **Correct `ComputerManagers.md` on platoon splits (S, docs only).**

### P1: give the personas real inputs, and make results measurable

5. **Add a platoon effect (M, engine and data).** The Lahman database has no splits, so apply a standard same-hand
   versus opposite-hand modifier in `ProbabilityTable`. Optionally add each player's own split later from a licensed
   source. This unlocks the Tinkerer, left-handed relief specialists, platoon pinch hitters and believable reliever
   choice.
6. **Load the missing ratings from data (M, `tools/generate_teams.py` and the loader).**
   * Bunting and sacrifice skill from Lahman sacrifice hits (SH) per plate appearance.
   * Base-running from the steal success rate.
   * Hold rating and ground-ball rate from the available proxies.
   * Team tendencies (steals, caught stealing, complete games, saves) at the team level.
7. **Use a precomputed win-probability table (M, managers).** Look up inning, half, outs, bases and margin, scaled to
   the era, in place of the run-expectancy blend. The managers would then handle the bottom of the ninth, extra innings,
   closer use and leverage-aware intentional walks correctly. A table lookup is also cheaper than calling `HomeWins`
   per candidate.
8. **Make benchmarks reproducible and keep one in the repo (S–M).**
   * Pass a seed source into `GameService.ManagerFor` and the catalog.
   * Add a benchmark project, or a test category excluded by default, that runs the round robin with confidence
     intervals.
   * Have it report each persona's behavior per 100 chances: steals, bunts, hit and runs, intentional walks, pinch
     hitters, pitching changes, complete games, aggressive sends.
   * Store expected ranges, so a tuning change shows its effect.
9. **Explain each decision (M).** Return the alternatives and their values with each decision. This is the basis for
   the manager feedback that was deferred (for example, "Spark bunts: +0.04 runs"). It also helps debugging, and allows
   a test that the profile ratings match the measured ordering.

### P2: historical realism and personality depth

10. **Let pitchers bat outside DH leagues and eras (L, engine and UI).** Use the home league's rule. Pinch-hitting for
    the pitcher then forces a pitching change. Add the double switch and pitcher sacrifice bunts. This is the largest
    realism gain, for 11 of the 15 teams.
11. **Add an era overlay or a "Historical" persona (M).** Adjust the settings from each team's real tendencies in the
    Lahman data: steal attempts per game, complete-game rate, saves, sacrifice hits, intentional walks (recorded from
    1955). Do not use real managers' names. The 1927 Yankees' starters would go deep, the 1980 Royals would run, and the
    1968 Tigers would play for one run.
12. **Add more persona settings.**
    * Risk attitude: preference for or against variance.
    * Leverage sensitivity.
    * Predictability: a random choice between near-equal options, weighted by a temperature.
    * Starter loyalty: pitch-count tolerance.
    * Closer philosophy: save situations only, or a multi-inning fireman.
    * Late-inning defensive replacements.
    * Platoon emphasis.
13. **Separate difficulty from personality (M).** Make difficulty a mistake rate and a decision temperature that can be
    applied to any persona, so Gus Halloran is not the only easy opponent.
14. **Fix pitch selection (M, engine and managers).** Either give pitchers repertoires derived from their strikeout and
    walk rates, with a penalty for repeating the same pitch, or have the managers mix pitches instead of always taking
    the lowest-value one.

### P3: depth and efficiency

15. **Add more running plays (M–L, engine):** steals of third, double steals, the squeeze and safety squeeze, and
    double-play odds that depend on the batter's speed and the pitcher's ground-ball rate.
16. **Look further ahead (M).** Value bunts and intentional walks over the next two or three hitters in the batting
    order, not over the average run-expectancy state.
17. **Cut repeated work per decision (S–M).**
    * Build one decision context per decision and cache `DefenseModel.RatingsFor`, which is called up to four times per
      evaluation.
    * Memoize `ProbabilityTable.Build` results within a decision.
    * Remove the recomputation of the best pitch in `ChooseStrategy`.

    `DecidePitching` builds about 12 probability tables per reliever on every play. This has not been profiled: profile
    in WebAssembly first, and move this item up if it shows.
18. **Share one set of engine rule constants (S)** between the resolver and the managers' model.

### Summary table

| # | Recommendation | Priority | Size | Engine change |
|--:|---|:-:|:-:|:-:|
| 1 | Aggressive running can be thrown out | P0 | S | Yes |
| 2 | Fix Infield In, Corners In, Guard Lines; no-free-option test | P0 | S–M | Yes |
| 3 | Pitchout without a runner is a wasted ball | P0 | S | Yes |
| 4 | Correct the platoon claims in the spec | P0 | S | No |
| 5 | Platoon effect | P1 | M | Yes |
| 6 | Load bunting, hold, ground-ball and team tendencies from data | P1 | M | Data and loader |
| 7 | Win-probability table for decisions | P1 | M | No |
| 8 | Reproducible benchmarks with stored ranges | P1 | S–M | Seed plumbing |
| 9 | Decision explanations | P1 | M | No |
| 10 | Pitchers bat; DH by league and era; double switch | P2 | L | Yes |
| 11 | Era overlay or Historical persona | P2 | M | Data |
| 12 | More persona settings | P2 | M | No |
| 13 | Difficulty separate from personality | P2 | M | No |
| 14 | Pitch repertoires or mixed pitch choice | P2 | M | Yes |
| 15 | More running plays | P3 | M–L | Yes |
| 16 | Lookahead over the batting order | P3 | M | No |
| 17 | Cache work per decision | P3 | S–M | No |
| 18 | Shared rule constants | P3 | S | Small refactor |

## 4. Tests and benchmarks to add

* **No free options.** A property test that every non-standard alignment and strategy is worse than the default in some
  situation (see recommendation 2).
* **Decision quality.** For sampled game situations, play out each candidate call several hundred times in the engine
  to the end of the half-inning or the game. Measure how often the model picks the best call, and how much it gives up
  when it does not. This checks `ManagerDecisionModel` against the real engine.
* **Persona consistency.** Measured behavior is ordered the way the profile ratings say: for example, Small ball steals
  the most and the Slugger the least.
* **Era calibration.** For each team, simulated runs per game, steal attempts, complete-game rate and pitchers per game
  compared with that team's real Lahman season, within set tolerances.
* **Balance guard.** In the round robin each expected-run persona stays within 5 points of 50%, and the easy persona
  (or difficulty level) stays below.

## 5. Suggested order of work

1. P0, recommendations 1 to 4: small, mostly engine fixes.
2. Re-run the head-to-head and the round robin for a clean baseline.
3. Recommendation 8 (reproducible benchmarks), then 5 and 6 (platoon effect and ratings from data).
4. Retune the personas against the new data.
5. Then recommendations 7, 9 and 10.

The measurements in `ComputerManagers.md` will change once the free options are gone, so treat them as provisional
until then.
