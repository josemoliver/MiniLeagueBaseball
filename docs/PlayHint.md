# Show Hint: technical specification

## Purpose and scope

Give a human manager a useful next call and a short baseball lesson at each playable decision. The hint must sound like a seasoned manager talking through the situation: direct, specific, and willing to name the risk. It advises; it never changes a selection or plays a pitch.

The feature covers the current plate appearance's offensive strategy and defensive pitch, alignment, or intentional walk. Bullpen and substitutions may be named as context when relevant, but automatic substitution recommendations are outside this feature. Hints are available only when **Show Play Hints** is enabled in pregame Game options and the game is waiting on a human decision (`GameService.IsHumanTurn`, no `PendingInjury`, no animation running).

## Player experience

### Entry and layout

- Add a **Show Play Hints** switch in pregame Game options, enabled by default. Save it in `GameOptions.ShowPlayHints`; a saved game without the field defaults to enabled. When disabled, hide the Hint button and make H do nothing for that game.
- Each human-managed team has ten hints per game. The first opening for a side at a decision spends one; reopening the same decision, including after resuming a saved game, is free. Show that side's remaining count on the Hint button (both counts when two humans play). Disable the button when no side can use or reopen a hint. In two-player mode, switching the card to the other side spends that side's hint only when its advice is first shown. The card's text still contains no digits; counts stay outside it.
- Add a labeled **Hint** button with a help icon to `DecisionPanel.razor`, next to the existing `PITCH`, `IBB`, and `SUBS` actions. Keep the word visible at all screen sizes; the icon alone is not an adequate label. Show the shortcut beside it as the other actions do (key hints are hidden on touch and phone layouts). Check that the action row still fits a narrow phone with four buttons.
- The button and the **H** key raise an `OnHint` callback to `GameRoom`, as `SUBS` does with `OnSubstitutions`. Add `ShortcutKind.Hint` and its `KeyH` mapping in `Shortcuts.cs`, add the row to the "During play" section of the printable cards, add `KeyH` to the allowed-key pattern in `wwwroot/js/keys.js`, and mention H in the Keyboard section of `HelpDialog.razor`. H is unused by the current shortcuts.
- H toggles the card. `mlbKeys` ignores every key while a `.mud-dialog` is open, so H never reaches `DecisionPanel.OnKey` then: the dialog must handle H itself to close. The Close button and Escape also close it. MudBlazor dialogs in this app do not close on Escape by default, so enable it for this dialog (`CloseOnEscapeKey = true`) and confirm in a real browser that it works with focus inside the card. Restore focus to the Hint button on close.
- The card has a title, a side label (**You are batting** or **You are fielding**), a compact situation line, a prominent **My call** line, **Why**, and an optional **If you prefer...** or **Watch for...** line. Use the same baseball terms as the decision buttons, including the changing steal label (`Steal`, `Steal 3rd`, `Double steal`). Keep the main recommendation visible without scrolling on a typical mobile viewport.
- Opening a hint does not select its call. The player closes the card, chooses from the existing controls, and commits with `PITCH`. An intentional walk is the exception: it is not a selection, and the `IBB` button commits it at once, so the card must say to press `IBB`. Do not add an Apply button.
- The recommended call must always be one the player can make with the current controls. If it is not, fall back to a legal one.

### Situation breakdown

Write the context as baseball prose, from the `GameState` at the moment the card opens:

- Half and inning, outs, and occupied bases, including the runner's destination when it matters (for example, “Bottom of the ninth, one out, runner on third”). Spell these out in words.
- The current batter and pitcher, plus a qualitative pitcher condition from `ActivePitcher.Condition`: `Fresh` = fresh, `Ok` = holding up, `Tiring`, `Tired`, `Exhausted`. Do not infer it from the pitch count. A reliever who entered cold or overwarmed also reads as tired for his first few batters (`EntryModifier`, `FatigueModel.SettleInBatters`); in that case say he is not yet loose rather than worn out.
- The platoon matchup in plain English, following `Player.BatsAgainst` and `ProbabilityTable.Platoon`. Opposite hands favor the batter and the same hand favors the pitcher; a switch hitter always has the opposite hand. There is no neutral pairing. Left against left is the largest edge and may be worded more strongly; omit the line when it adds nothing to the reason.
- Match leverage in words. Starting rules, from the batting side's view (reword for the fielding side), first match wins:
  1. `ManagerDecisionModel.Leverage` below 0.3: a run will barely change the result.
  2. Seventh inning or later, and tied or trailing by no more than the runners on plus one: late tying or go-ahead chance.
  3. Seventh inning or later, and leading by one or two: protecting a narrow lead.
  4. Otherwise: a chance to build an inning.

  Never print a calculated win chance.

The card's own text must contain **no digits, percentages, run gains, probabilities, rankings, batting lines, pitch counts, or other performance statistics**. This applies to the situation line too, and to screen-reader text and tooltips. “Steal 3rd” is written “steal third” in prose; the key hint on the opener button is outside the card.

### Writing voice

Use short, manager-like sentences. Lead with a command, then explain the baseball tradeoff: “Swing away. We need a ball in play more than a risky out on the bases.” Avoid mechanical phrases such as “the model predicts,” numeric confidence, and generic advice that could fit any game. Say “I’d...” sparingly; the call itself should be unmistakable.

Include a conditional second scenario when the next-best call is close (starting threshold: within 0.01 runs of what the chosen call needed to win) or an observable opponent change could alter the choice. For example: “If they bring the corners in, let him swing; the bunt loses its surprise.” Do not offer a scenario that depends on information the player cannot observe, and never show an illegal play as an available action. If there is no meaningful alternative, omit that line.

Most turns will call for swinging away or a normal defense, so those two must not read the same every time. Write several reasons for each, tied to different facts (batter, pitcher condition, bases, outs, leverage), and choose among the ones that are true from the situation itself, never at random. The same state must always give the same text.

## Hint engine

### Input and output contract

Add a read-only engine class, for example a static `PlayHintAdvisor`, in `src/MiniLeague.Engine/AI`. It takes the live `GameState`, the requested side (`Offense` or `Defense`), and, for the offensive side, the defensive alignment currently visible on the field. It returns a typed `PlayHint` with the side, the recommended existing decision values, the facts behind the situation and reason, and at most one conditional alternative. A formatter turns that into prose.

The advisor must be deterministic for the same input, consume no game RNG, leave game state as it found it, and run entirely in the client. A hint request must not call `GameService.ExecutePlay` or `GameService.GetComputerDefense` (the latter draws from the manager's RNG), change a selection, advance a runner, save the game, or alter `LastOffense`/`LastDefense`.

Two details of the existing code matter here:

- `ManagerDecisionModel.Lookahead` and `WalkThenNextHitter` change bases and outs temporarily and restore them. Keep the whole calculation synchronous, with no `await` inside.
- `FatigueModel.GetModifier` writes `ActivePitcher.Condition`. Read the displayed condition before calling it, or add a non-writing overload.

The calculation is synchronous and the game cannot advance under a modal dialog on a human turn, so compute when the card opens (for both sides in a two-human game) and pass the dialog an immutable view model.

### One source of judgment

The advisor must give the same call the **Analyst** manager would (`ManagerTuning.Default`), whatever personas the game uses. Do not write a second set of rules or margins. Refactor the evaluation out of `ComputerManager` (`ChooseStrategy`, `AvailableAlignments`, the pitch-and-alignment search, `ShouldIntentionalWalk`) into internal methods that both callers use, with the random choices left in the manager:

- offense: strategy only, with no pinch hitter or pinch runner;
- defense: no random tie-break and no `PitchMix`. Scan `Standard` first and replace the best only on a clear improvement.

The refactor must not change what the computer managers do. Run the manager benchmark with `--check` and the engine tests.

### Offensive calls

Candidates and the conditions under which they are considered (these mirror `ChooseStrategy` today):

| Call | Legal in the panel | Considered by the hint |
|---|---|---|
| `SwingAway` | always | always; the default |
| `SwingAgressive` | always | priced always, but only meaningful with a runner on first or second |
| `Steal` (second) | `StealPlan` not `None` | runner on first, second empty, fewer than two outs |
| `Steal` (third or double) | `StealPlan` not `None` | fewer than two outs |
| `HitAndRun` | first occupied, second empty | also fewer than two outs and a contact hitter |
| `Squeeze`, `SafetySqueeze` | third occupied, fewer than two outs | a hitter is on deck |
| `SacrificeBunt` | always (it is a bunt for a hit with bases empty or two out) | no outs, runner on first or second, third empty |

Steals and the hit and run are not considered when the batting side leads in the seventh or later. A steal is also only recommended from the sixth inning on, or when tied or behind. A sacrifice is not considered when trailing by two or more from the seventh on.

Never recommend `SwingSafe`: it resolves exactly like `SwingAway`. `SurpriseBunt` has no button and is excluded.

The values are not all on one scale, so do not rank them in a single list:

- Swing, aggressive, and hit and run are compared with `ManagerDecisionModel.AtBat` against the defense's best pitch and the visible alignment.
- A steal does not use up the plate appearance. It is compared with `StateValue` of staying put.
- A bunt or squeeze is compared with a swing valued under the same `Lookahead` to the on-deck hitter.

Each call must beat its reference by the `ManagerTuning` margin. When a gambit is declined, say why.

### Defensive calls

Compare pitch-and-alignment pairs with `ManagerDecisionModel.AtBat`, minimizing the batting side's value. Include `Fastball`, `Curveball`, `Slider`, and `Changeup`. Exclude `Pitchout`: the panel has no button or key for it. Alignments are limited by `ComputerManager.AvailableAlignments`.

The panel allows an intentional walk at any time, so the advisor must apply the manager's conditions: first base empty, fewer than two outs, a runner on second or third, seventh inning or later, score within two, and the batter clearly more dangerous than the next hitter (`IntentionalWalkOpsGap` and the OPS floor). It is then valued with `WalkThenNextHitter`. A walk under these conditions can never force in a run.

The reason must name a cause the engine actually models: the hitter's power or bunting ability, the pitcher's ground-ball tendency, a runner who can score on an infield out, double-play chance, an extra-base hit that could decide a close game, a tiring pitcher, or the next hitter after a possible walk. The data has no spray charts, so never claim a hitter's pull tendency as an observed fact; the shift options are suggested from power only. The text must not claim that a pitch or alignment guarantees an outcome. Recommend pitch and alignment as a **pair**.

### Decision-model integration

`ManagerDecisionModel` is `internal`, so the advisor lives in the engine. Wrap each request in `ManagerDecisionModel.Decision(state)` to use the per-decision cache. The engine remains a plain class library with no Web, JavaScript, or MudBlazor reference.

`DecisionOption.Gain` may inform the choice of a close alternative, but `DecisionExplanation.Summary` and `Considered` contain numbers and computer-manager wording. The formatter writes fresh prose and never renders those fields.

In Web, `GameRoom.razor` owns the request: it checks that a decision is awaited, builds the view models, and opens the dialog. The dialog must not own `GameService` or perform game actions. If the advisor becomes an injected service, register it in every bUnit fixture that renders `GameRoom` or `MainLayout`.

## Two-player local mode

When both teams are human controlled, the panel shows offense and defense together. Open the hint for the batting side. Provide clearly labeled **Batting hint** and **Fielding hint** segmented buttons inside the card. Switching sides changes the side label, recommendation, and reasons for the same plate appearance. Keep the two recommendations separate; do not combine them into a single “best play” or imply that one player has chosen a move. Each opening starts on the batting side. Show only the one available side in a single-human game.

Local play shares one screen, so the card cannot promise secrecy. It must not read or reveal the other side's selected strategy or pitch, and must not change either side's choices. The one exception is alignment: the fielders have already visibly moved, so the batting hint takes the alignment shown on the field.

## Edge cases and accessibility

- A play is added to the game **before** its animation ends, while the page holds the old picture and the decision panel stays on screen. The hint handler must check `GameRoom`'s `_animating` flag itself and do nothing while it is set; the panel's `AlignmentMoving` parameter can be stale then.
- The hint is unavailable when the game is final, an injury prompt is showing, or the computer is deciding (including after a manager is ejected). On a resumed game, generate from the restored state; never save a hint.
- If model inputs are missing or a candidate fails evaluation, return a safe legal call with a specific context sentence; never display an exception or a fabricated certainty.
- Use a dialog with an accessible name such as “Batting hint” or “Fielding hint,” a labeled Close button, a logical focus order, and a focus trap while open. The side control must expose its state. Restore focus to the opener on close.
- Switching sides moves focus to the new card heading, so a screen reader announces the change; do not add a separate live-region announcement, which would read it twice. Use visible focus indicators, sufficient contrast, and a responsive layout that does not clip at enlarged text sizes.
- Mouse and touch must use the same content and behavior.

## Delivery plan

Build in three steps, in order. Finish each before starting the next.

1. **Survey.** Build the SurveyTool (below) and use its results to decide what to write. This needs no advisor, because the computer manager already makes the calls.
2. **Engine.** Refactor the shared evaluation out of `ComputerManager`, then add the advisor and the formatter with their tests, including the one that matches the Analyst's call. Write the text for the most frequent groups first. Rerun the survey's seeds through the formatter, measure repetition and generic reasons, and read a sample: check the voice and the starting thresholds for leverage wording and close alternatives. Adjust before any UI work.
3. **UI.** Add the button, the H shortcut, and the dialog, with the bUnit and browser tests.

## SurveyTool

A developer tool that plays seeded computer-versus-computer games and reports which situations and calls a hint will face, so the text is written where it is needed.

### Project

- `tools/MiniLeague.SurveyTool/`, a `net10.0` console project (Nullable and implicit usings on) that references only the engine. Like `tools/MiniLeague.ManagerBench`, it stays out of `MiniLeagueBaseball.slnx`.
- `Program.cs` reads the command line; `SurveyRunner.cs` plays the games and records rows; `SurveyReport.cs` groups and writes them.
- It needs `ManagerDecisionModel.Leverage`, so add `InternalsVisibleTo` for it in the engine project. The engine gains no reference to the tool.
- `MiniLeague.Engine.Tests` references it and runs a tiny survey, as `ManagerBenchmarkTests` does for the benchmark.
- It is not part of the app or of what is published. Adding it to a CI workflow is a separate decision.

### Command line

```bash
dotnet run -c Release --project tools/MiniLeague.SurveyTool -- --out <folder> [--games N] [--seed S] [--dh RULE] [--hints]
```

| Option | Meaning | Default |
|---|---|---|
| `--out` | folder for the output files; required | none |
| `--games` | games per ordered matchup and DH rule | 10 |
| `--seed` | base seed | 1 |
| `--dh` | `designated`, `pitchers`, `historical`, or `both` (designated and pitchers) | `both` |
| `--hints` | also run the advisor and formatter (step 2 of the delivery plan) | off |

With 15 teams there are 210 ordered visitor and home matchups, so the defaults play 4,200 games. Each team plays in its own park with its default lineup and starter, as `GameService.StartGame` sets them.

### Running the games

- Both sides are computer controlled with the persona `analyst` from `ManagerCatalog.Default`. Injuries, rain delays and ejections are off (`GameOptions.None` plus the DH rule).
- Each game's seed is derived from the base seed, the matchup, the DH rule and the game number. It seeds the resolver, the manager and the events, as `ManagerBenchmark.PlayOne` does. Equal options must give identical output files.
- Before each `ExecuteComputerTurn`, record the facts. After it, read the calls from `LastOffense` and `LastDefense`. Pitching changes are made after the previous play, so the pitcher is already correct. A pinch hitter or runner is applied during the turn: take the batter facts from the incoming hitter and flag the row.
- The tool must not change how a game plays: a surveyed game ends the same as an unsurveyed game with the same seed.

### What is recorded

One row per side per turn, so each turn gives a batting row and a fielding row.

| Group | Fields |
|---|---|
| Identity | seed, DH rule, teams, turn number, side |
| Situation | inning, half, outs, bases, score margin for this side, leverage value and leverage phrase (the rules under “Situation breakdown”) |
| Batter | handedness against this pitcher, power, speed, bunting and sacrifice ratings, contact hitter or not, pitcher batting or not, on-deck hitter weaker or not |
| Pitcher | handedness, condition, not yet loose or not, ground-ball rate |
| Defense | alignment showing before the play |
| Call | offense: strategy and steal plan; defense: pitch, alignment, intentional walk |
| Flags | pinch hitter, pinch runner |

### Output

All files go to `--out`. They are working data and are not committed.

- `rows.csv`: every row.
- `groups.csv`: rows grouped by side, call, and situation group, with the count, share of all turns, and one example seed and turn so that a situation can be reproduced. A situation group is base state, outs, leverage phrase and, for the batting side, contact and power; for the fielding side, pitcher condition and batter power.
- `summary.md`: games and turns played; each call's share per side; the largest groups and how much of all turns they cover; legal calls that never or rarely appeared; the share of each leverage phrase.

`DecisionExplanation.Summary` is not used for grouping: it contains names and numbers and is not a stable reason.

### With `--hints`

Once the advisor and formatter exist, the tool also asks for both sides' hints before each turn, without changing the game, and adds the reason identifier, the rendered text and whether there is a conditional line. For this, each `PlayHint` carries a stable identifier for its reason and text variant. `summary.md` then adds:

- how often one side sees the same sentence twice in one game, and the most repeated sentences;
- the share of turns with a conditional line;
- groups that only get a generic reason;
- any text containing a digit;
- turns where the hint's call differs from the manager's, with seeds. The hint has no random tie-break and no pinch hitter, so review these instead of treating each one as a failure.

### Using the results

- Write text for the biggest groups first, which are expected to be swinging away and a normal defense.
- A conditional line on most turns means the threshold is too loose.
- Variety matters where a group is large or repeats within a game.

### Limits

- Rare calls (squeezes, double steals, intentional walks) may appear too seldom to judge. Build those situations by hand, as tests.
- The survey shows only what the model already believes. It does not prove the advice is good; reading a sample is still the check on voice and baseball sense.
- Never turn simulated outcomes into hint claims such as “this usually works.”
- A person who steals or bunts more than the Analyst will see situations in different proportions, so treat the counts as a guide to priority only.

The catalog stays hand-written and ships with the app. Nothing is simulated at hint time.

## Acceptance criteria and verification

1. Show Play Hints is enabled by default in Game options. Turning it off hides Hint and disables H for that game, including after saving and resuming; older saves without the setting retain enabled hints. Each human-managed team can spend at most ten hints, and reopening advice for the same decision is free across save and resume. When enabled on a human turn, Hint opens a card for the correct side; its context matches the inning, outs, bases, pitcher condition, handedness, and leverage shown. The card's text contains no digits or performance statistics.
2. The main call is legal, can be made with the panel's controls, and has a concise reason grounded in the current matchup. Opening, switching, and closing the card leave game state and selections untouched.
3. Engine tests cover swing versus bunt, steal and hit-and-run eligibility, suicide versus safety squeeze, a late tying-run situation, each alignment's availability, pitch selection, intentional-walk conditions, that `Pitchout` and `SwingSafe` are never recommended, and that the state is unchanged afterwards. Repeated requests return the same hint, and over a range of situations the hint matches the Analyst's call (with its random tie-break removed).
4. The no-digit rule is tested on the formatter across many situations, as is variety: different swing-away and normal-defense situations give different reasons. bUnit tests cover single-human and two-human rendering, side switching, the H mapping, and no hint during an animation. Browser tests cover the dialog in Classic and Detailed views, H, Escape and Close, focus restoration, Tab order, suppression of play keys while it is open, and a narrow viewport. Wait on UI state rather than fixed sleeps.
5. A tiny SurveyTool run in the engine tests produces identical output for equal options, covers both sides, and leaves each game's result the same as without the survey.
6. `dotnet build MiniLeagueBaseball.slnx` stays at zero warnings; the engine and Web tests and the manager benchmark `--check` pass. The feature requires no server, database, network call, or save-format change.
