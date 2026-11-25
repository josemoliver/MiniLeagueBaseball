using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.AI;

/// <summary>
/// The original rule-based computer manager, kept as a choice of its own. It follows fixed rules for each situation
/// (bunt with the weak hitter late, walk the big bat with first open, send a fast runner now and then) and never
/// prices a call or looks at the matchup in front of it. It is the baseline the expected-run managers are measured
/// against. Injury replacements and position swaps are the shared ones from <see cref="ComputerManager"/>. He does make
/// one move the book calls for: with a lead in the ninth, a better glove for the weakest fielder.
/// </summary>
// NOTE: only DefensiveReplaceFromInning is read from the tuning; every other call is overridden below.
public class OldSchoolManager(Random? rng = null)
    : ComputerManager(rng, new ManagerTuning { DefensiveReplaceFromInning = 9 })
{
    // ── Defensive decisions ─────────────────────────────────────────────────

    public override DefensiveDecision MakeDefensiveDecision(GameState state, Player? plannedBatter = null)
    {
        var pitcher = state.CurrentPitcher;
        var batter = plannedBatter ?? state.CurrentBatter;
        bool closeGame = Math.Abs(state.VisitorTotalScore - state.HomeTotalScore) <= 2;
        bool lateInning = state.CurrentInning >= 7;

        // Intentional walk: avoid a dangerous hitter with first base open.
        if (ShouldIntentionalWalk(state, batter))
            return new DefensiveDecision
            {
                IntentionalWalk = true,
                Why = DecisionExplanation.Rule($"By the book: walk {batter.Name}, a power hitter, with first base open."),
            };

        // Infield in: runner on third, fewer than two out, close game.
        var alignment = DefensiveAlignment.Standard;
        if (state.RunnerOnThird is not null && state.Outs < 2 && closeGame)
            alignment = DefensiveAlignment.InfieldIn;
        else if (state.RunnerOnFirst is not null && state.Outs == 0 && IsBunter(batter))
            alignment = DefensiveAlignment.CornersIn;

        string where = alignment switch
        {
            DefensiveAlignment.InfieldIn => "infield in to cut off the run at the plate",
            DefensiveAlignment.CornersIn => "corners in for the bunt",
            _ => "a normal defense",
        };
        return new DefensiveDecision
        {
            PitchType = SelectPitch(pitcher, batter, lateInning),
            Alignment = alignment,
            Why = DecisionExplanation.Rule($"By the book: {where}, and mix the pitches."),
        };
    }

    // ── Offensive decisions ─────────────────────────────────────────────────

    public override OffensiveDecision MakeOffensiveDecision(GameState state)
    {
        var decision = ChooseStrategy(state);
        PinchRun(state, decision);
        PinchHit(state, decision);
        var notes = new List<string>();
        if (decision.PinchRunnerPlayerId is int runnerId)
            notes.Add($"Pinch runner {state.OffenseTeam.Roster.First(p => p.Id == runnerId).Name}: speed on the bases late.");
        if (decision.PinchHitterPlayerId is int hitterId)
            notes.Add($"Pinch hitter {state.OffenseTeam.Roster.First(p => p.Id == hitterId).Name}: a better bat late in a close game.");
        string rule = decision.Strategy switch
        {
            OffensiveStrategy.SacrificeBunt => "By the book: bunt the runner over.",
            OffensiveStrategy.Steal => "A hunch: send the fast runner.",
            OffensiveStrategy.HitAndRun => "A hunch: put on the hit and run.",
            OffensiveStrategy.SwingAgressive => "Close and late: run hard on anything.",
            _ => "Let him swing.",
        };
        decision.Why = DecisionExplanation.Rule(string.Join(" ", notes.Append(rule)));
        return decision;
    }

    // Pinch hit for a clearly weaker hitter in a late, close game when a bench bat is meaningfully better.
    // A catcher is only replaced if the bench has another catcher.
    private static void PinchHit(GameState state, OffensiveDecision decision)
    {
        if (state.CurrentInning < 7 || ScoreMargin(state) is > 1 or < -3) return;

        var batter = state.CurrentBatter;
        // The book for a pitcher's turn: only when behind or tied late, and only with a reliever to bring in.
        if (batter.IsPitcher && (state.CurrentInning < 7 || ScoreMargin(state) > 0
                                 || state.BullpenFor(state.IsTopOfInning).Count == 0)) return;
        var bench = state.Bench(state.IsTopOfInning)
            .Where(p => (p.Overall?.AtBats ?? 0) >= 100 && p.Id != decision.PinchRunnerPlayerId)
            .ToList();
        if (state.CurrentBatterPosition == Position.C && !bench.Any(p => p.PrimaryPosition == Position.C))
            return;

        var best = bench.OrderByDescending(Ops).FirstOrDefault();
        if (best is not null && Ops(best) - Ops(batter) >= 0.080)
            decision.PinchHitterPlayerId = best.Id;
    }

    // Replace a slow lead runner with the fastest bench player when one run matters.
    private static void PinchRun(GameState state, OffensiveDecision decision)
    {
        if (state.CurrentInning < 8 || ScoreMargin(state) is > 0 or < -1 || state.Outs >= 2) return;

        int baseIndex = state.RunnerOnSecond is not null ? 2 : state.RunnerOnFirst is not null ? 1 : 0;
        if (baseIndex == 0) return;
        var runner = (baseIndex == 2 ? state.RunnerOnSecond : state.RunnerOnFirst)!.Player;
        if (runner.SpeedRating > 2) return;

        var best = state.Bench(state.IsTopOfInning)
            .Where(p => p.SpeedRating >= runner.SpeedRating + 2)
            .OrderByDescending(p => p.SpeedRating)
            .FirstOrDefault();
        if (best is null) return;

        decision.PinchRunnerPlayerId = best.Id;
        decision.PinchRunnerBaseIndex = baseIndex;
    }

    private OffensiveDecision ChooseStrategy(GameState state)
    {
        var batter = state.CurrentBatter;
        bool closeGame = Math.Abs(state.VisitorTotalScore - state.HomeTotalScore) <= 1;
        bool lateInning = state.CurrentInning >= 7;
        bool runner1 = state.RunnerOnFirst is not null;
        bool runner2 = state.RunnerOnSecond is not null;
        bool runner3 = state.RunnerOnThird is not null;

        // Sacrifice bunt: pitcher batting, runner on first or second, no outs.
        if (!state.DHRule && batter.IsPitcher && (runner1 || runner2) && state.Outs == 0)
            return new OffensiveDecision { Strategy = OffensiveStrategy.SacrificeBunt };

        // Sacrifice bunt: weak hitter, runner on first or second, no outs, close late game.
        if (closeGame && lateInning && state.Outs == 0 && (runner1 || runner2)
                      && IsWeakHitter(batter) && !runner3)
            return new OffensiveDecision { Strategy = OffensiveStrategy.SacrificeBunt };

        // Steal: fast runner on first, nobody on second, close game.
        if (runner1 && !runner2 && state.RunnerOnFirst!.Player.SpeedRating >= 4
                    && closeGame && state.Outs < 2 && Rng.NextDouble() < 0.40)
            return new OffensiveDecision { Strategy = OffensiveStrategy.Steal };

        // Hit and run: runner on first, good contact hitter, fewer than two out.
        if (runner1 && !runner2 && state.Outs < 2 && IsContactHitter(batter)
                    && lateInning && closeGame && Rng.NextDouble() < 0.25)
            return new OffensiveDecision { Strategy = OffensiveStrategy.HitAndRun };

        // Default: aggressive running in a close late game, safe otherwise.
        return new OffensiveDecision
        {
            Strategy = closeGame && lateInning ? OffensiveStrategy.SwingAgressive : OffensiveStrategy.SwingAway
        };
    }

    // ── Bullpen management ──────────────────────────────────────────────────

    /// <summary>
    /// Fatigue drives it: warm someone up once the starter is past his first threshold, change when he is really
    /// tiring or is being shelled, and hold the closer for a save situation in the ninth.
    /// </summary>
    public override PitchingMove DecidePitching(GameState state, bool visitor)
    {
        var pitcher = state.PitcherFor(visitor);
        var pen = state.BullpenFor(visitor);
        if (pen.Count == 0) return new PitchingMove();

        FatigueModel.GetModifier(pitcher);  // refresh pitcher.Condition
        int lead = visitor
            ? state.VisitorTotalScore - state.HomeTotalScore
            : state.HomeTotalScore - state.VisitorTotalScore;
        bool saveSpot = state.CurrentInning >= 9 && lead is >= 1 and <= 3;
        bool saveSoon = state.CurrentInning >= 8 && lead is >= 1 and <= 3;
        bool pitcherIsCloser = pitcher.Player.Role == PitcherRole.Closer;

        var closer = pen.FirstOrDefault(w => w.Player.Role == PitcherRole.Closer);
        var ready = pen.Where(w => w.IsWarm).ToList();

        // Hand the lead to the closer.
        if (saveSpot && !pitcherIsCloser && closer is not null && closer.IsWarm)
            return new PitchingMove(ChangeToId: closer.Player.Id, Why: $"The closer, {closer.Player.Name}, for the save.");

        // Fatigue: change once he is tiring (a settled reliever is left alone a little longer).
        bool settled = pitcher.BattersFaced - pitcher.SettleFrom >= FatigueModel.SettleInBatters;
        bool pastHisBest = pitcher.Condition >= PitcherCondition.Tiring && settled;
        bool spent = pitcher.Condition >= PitcherCondition.Tired && settled;

        // Getting shelled: a starter is hooked after five runs, a reliever after three.
        bool starter = pitcher.Player.Role == PitcherRole.Starter;
        bool shelled = pitcher.BattersFaced >= 3 && pitcher.RunsAllowed >= (starter ? 5 : 3);
        pastHisBest |= shelled;
        spent |= shelled;
        if (pastHisBest || spent)
        {
            var next = PickReliever(ready, saveSpot, closer);
            if (next is null && spent)  // nobody ready, but he cannot go on: take the best arm available
                next = PickReliever(pen, saveSpot, closer);
            if (next is not null)
                return new PitchingMove(ChangeToId: next.Player.Id,
                    Why: shelled ? $"{pitcher.Player.Name} is being hit hard." : $"{pitcher.Player.Name} is tiring.");
        }

        // Get somebody loose before he is needed.
        bool anyWarming = pen.Any(w => w.IsWarming);
        if (!anyWarming)
        {
            if (saveSoon && !pitcherIsCloser && closer is not null)
                return new PitchingMove(StartWarmingId: closer.Player.Id, Why: $"{closer.Player.Name} warms up for a save.");
            if (pitcher.Condition >= PitcherCondition.Ok && settled)
            {
                var next = PickReliever(pen, saveSpot, closer);
                if (next is not null)
                    return new PitchingMove(StartWarmingId: next.Player.Id, Why: $"{next.Player.Name} gets loose.");
            }
        }
        return new PitchingMove();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private bool ShouldIntentionalWalk(GameState state, Player batter)
    {
        if (state.RunnerOnFirst is not null) return false; // base occupied
        if (state.Outs == 2) return false;                 // no upside with two out

        // Only walk a power hitter, and then only half the time.
        int hr = batter.Overall?.HomeRuns ?? 0;
        return hr >= 25 && Rng.NextDouble() < 0.5;
    }

    private PitchType SelectPitch(ActivePitcher pitcher, Player batter, bool lateInning)
    {
        // Lean on the fastball and vary the rest so the pitching is not predictable.
        var options = new[] { PitchType.Fastball, PitchType.Fastball, PitchType.Curveball,
                              PitchType.Slider, PitchType.Changeup };
        return options[Rng.Next(options.Length)];
    }

    private static bool IsWeakHitter(Player p) =>
        (p.Overall?.HomeRuns ?? 0) < 8 && (p.Overall?.BattingAverage ?? 0) < 0.240;
}
