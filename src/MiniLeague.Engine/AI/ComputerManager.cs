using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.AI;

/// <summary>
/// Rule-based computer manager. Makes situationally appropriate
/// offensive and defensive decisions without lookahead.
/// </summary>
public class ComputerManager
{
    private readonly Random _rng;

    public ComputerManager(Random? rng = null) => _rng = rng ?? Random.Shared;

    // ── Defensive decisions ─────────────────────────────────────────────────

    public DefensiveDecision MakeDefensiveDecision(GameState state)
    {
        var pitcher = state.CurrentPitcher;
        var batter  = state.CurrentBatter;
        bool closegame  = Math.Abs(state.VisitorTotalScore - state.HomeTotalScore) <= 2;
        bool lateInning = state.CurrentInning >= 7;
        bool runnersOn  = state.RunnerOnFirst is not null
                       || state.RunnerOnSecond is not null
                       || state.RunnerOnThird is not null;

        // Intentional walk: avoid dangerous hitter with first base open and a weaker bat on deck
        if (ShouldIntentionalWalk(state, batter))
            return new DefensiveDecision { IntentionalWalk = true };

        // Infield in: runner on third, less than 2 outs, close game
        var alignment = DefensiveAlignment.Standard;
        if (state.RunnerOnThird is not null && state.Outs < 2 && closegame)
            alignment = DefensiveAlignment.InfieldIn;
        else if (state.RunnerOnFirst is not null && state.Outs == 0 && IsBunter(batter))
            alignment = DefensiveAlignment.CornersIn;

        // Pitch selection: vary based on count situation
        var pitch = SelectPitch(pitcher, batter, lateInning);

        return new DefensiveDecision
        {
            PitchType = pitch,
            Alignment = alignment
        };
    }

    // ── Offensive decisions ─────────────────────────────────────────────────

    public OffensiveDecision MakeOffensiveDecision(GameState state)
    {
        var decision = ChooseStrategy(state);
        ChoosePinchRunner(state, decision);
        ChoosePinchHitter(state, decision);
        return decision;
    }

    // ── Substitutions ───────────────────────────────────────────────────────

    // Pinch hit for a clearly weaker hitter in a late, close game when a bench bat is
    // meaningfully better. A catcher is only replaced if the bench has another catcher.
    private static void ChoosePinchHitter(GameState state, OffensiveDecision decision)
    {
        if (state.CurrentInning < 7 || ScoreMargin(state) is > 1 or < -3) return;

        bool visitor = state.IsTopOfInning;
        var batter = state.CurrentBatter;
        var bench = state.Bench(visitor)
            .Where(p => (p.Overall?.AtBats ?? 0) >= 100 && p.Id != decision.PinchRunnerPlayerId)
            .ToList();
        if (state.CurrentBatterPosition == Position.C && !bench.Any(p => p.PrimaryPosition == Position.C))
            return;

        var best = bench.OrderByDescending(Ops).FirstOrDefault();
        if (best is not null && Ops(best) - Ops(batter) >= 0.080)
            decision.PinchHitterPlayerId = best.Id;
    }

    // Replace a slow lead runner with the fastest bench player when one run matters.
    private static void ChoosePinchRunner(GameState state, OffensiveDecision decision)
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

    // How far the batting team leads (negative when trailing).
    private static int ScoreMargin(GameState state) => state.IsTopOfInning
        ? state.VisitorTotalScore - state.HomeTotalScore
        : state.HomeTotalScore - state.VisitorTotalScore;

    private static double Ops(Player p) =>
        (p.Overall?.OnBasePercentage ?? 0) + (p.Overall?.SluggingPct ?? 0);

    private OffensiveDecision ChooseStrategy(GameState state)
    {
        var batter    = state.CurrentBatter;
        bool closegame  = Math.Abs(state.VisitorTotalScore - state.HomeTotalScore) <= 1;
        bool lateInning = state.CurrentInning >= 7;
        bool runner1    = state.RunnerOnFirst is not null;
        bool runner2    = state.RunnerOnSecond is not null;
        bool runner3    = state.RunnerOnThird is not null;

        // Sacrifice bunt: pitcher batting, runner on first or second, 0 outs
        if (!state.DHRule && batter.IsPitcher && (runner1 || runner2) && state.Outs == 0)
            return new OffensiveDecision { Strategy = OffensiveStrategy.SacrificeBunt };

        // Sacrifice bunt: weak hitter, runner on first or second, 0 outs, close late game
        if (closegame && lateInning && state.Outs == 0 && (runner1 || runner2)
                      && IsWeakHitter(batter) && !runner3)
            return new OffensiveDecision { Strategy = OffensiveStrategy.SacrificeBunt };

        // Steal: fast runner on first, no runner on second, close game
        if (runner1 && !runner2 && state.RunnerOnFirst!.Player.SpeedRating >= 4
                    && closegame && state.Outs < 2 && _rng.NextDouble() < 0.40)
            return new OffensiveDecision { Strategy = OffensiveStrategy.Steal };

        // Hit and run: runner on first, good contact hitter, < 2 outs
        if (runner1 && !runner2 && state.Outs < 2 && IsContactHitter(batter)
                    && lateInning && closegame && _rng.NextDouble() < 0.25)
            return new OffensiveDecision { Strategy = OffensiveStrategy.HitAndRun };

        // Default: aggressive running in close game, safe otherwise
        var strategy = closegame && lateInning
            ? OffensiveStrategy.SwingAgressive
            : OffensiveStrategy.SwingAway;

        return new OffensiveDecision { Strategy = strategy };
    }

    // ── Bullpen management ──────────────────────────────────────────────────

    /// <summary>What the computer wants to do with its pitching staff before the next batter.</summary>
    public record PitchingMove(int? StartWarmingId = null, int? ChangeToId = null);

    /// <summary>
    /// Decides whether the fielding side should start a reliever warming up or bring one in.
    /// Fatigue drives it (warm up once the starter is past his first threshold, change when he
    /// is really tiring), and a closer is held for a save situation in the 9th.
    /// </summary>
    public PitchingMove DecidePitching(GameState state, bool visitor)
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
            return new PitchingMove(ChangeToId: closer.Player.Id);

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
                return new PitchingMove(ChangeToId: next.Player.Id);
        }

        // Get somebody loose before he is needed.
        bool anyWarming = pen.Any(w => w.IsWarming);
        if (!anyWarming)
        {
            if (saveSoon && !pitcherIsCloser && closer is not null)
                return new PitchingMove(StartWarmingId: closer.Player.Id);
            if (pitcher.Condition >= PitcherCondition.Ok && settled)
            {
                var next = PickReliever(pen, saveSpot, closer);
                if (next is not null) return new PitchingMove(StartWarmingId: next.Player.Id);
            }
        }
        return new PitchingMove();
    }

    // Best available arm: the closer only in a save spot (or when he is all that is left).
    private static WarmingPitcher? PickReliever(
        IEnumerable<WarmingPitcher> options, bool saveSpot, WarmingPitcher? closer)
    {
        var list = options.ToList();
        var pool = saveSpot || list.All(w => w == closer)
            ? list
            : list.Where(w => w != closer).ToList();
        return pool
            .OrderByDescending(w => w.IsWarm)
            .ThenBy(w => w.Player.PitchingOverall?.ERA ?? 99)
            .FirstOrDefault();
    }

    // ── Injuries and defensive alignment ────────────────────────────────────

    /// <summary>
    /// Picks who replaces an injured player: a pitcher from the bullpen (a warm one if possible),
    /// otherwise the bench player who fits the position best, favouring a natural fielder and a good bat.
    /// </summary>
    public int ChooseInjuryReplacement(GameState state, InjuryNotice notice)
    {
        if (notice.IsPitcher)
        {
            var pen = state.BullpenFor(notice.Visitor);
            var closer = pen.FirstOrDefault(w => w.Player.Role == PitcherRole.Closer);
            // A closer is fine here: there is no save to protect when somebody is hurt.
            return (PickReliever(pen.Where(w => w.IsWarm), saveSpot: true, closer)
                    ?? PickReliever(pen, saveSpot: true, closer)
                    ?? throw new InvalidOperationException("The bullpen is empty.")).Player.Id;
        }

        var position = state.PositionsFor(notice.Visitor)[notice.PlayerId];
        return state.Bench(notice.Visitor)
            .OrderByDescending(p => (p.IsNaturalAt(position) ? 3.0 : 0.0)
                                  + p.FieldingAt(position).Value * 0.25
                                  + Ops(p) * 3.0)
            .First().Id;
    }

    /// <summary>
    /// Position swaps (at most two) that put the lineup's fielders where they are best suited,
    /// e.g. after a pinch hitter lands out of position. Each swap is applied to the ones before it.
    /// </summary>
    public IReadOnlyList<(int A, int B)> BestPositionSwaps(GameState state, bool visitor)
    {
        var team = state.TeamFor(visitor);
        var order = state.LineupFor(visitor).ToList();
        var positions = new Dictionary<int, Position>(state.PositionsFor(visitor));
        var swaps = new List<(int, int)>();

        double Value() => DefenseModel.DefensiveValue(order.Select(id => (team.Roster.First(p => p.Id == id), positions[id])));

        for (int round = 0; round < 2; round++)
        {
            double current = Value();
            (int A, int B, double Gain)? best = null;
            for (int i = 0; i < order.Count; i++)
            for (int j = i + 1; j < order.Count; j++)
            {
                (positions[order[i]], positions[order[j]]) = (positions[order[j]], positions[order[i]]);
                double gain = Value() - current;
                (positions[order[i]], positions[order[j]]) = (positions[order[j]], positions[order[i]]);
                if (gain > 2.0 && (best is null || gain > best.Value.Gain))
                    best = (order[i], order[j], gain);
            }
            if (best is null) break;

            var (a, b, _) = best.Value;
            (positions[a], positions[b]) = (positions[b], positions[a]);
            swaps.Add((a, b));
        }
        return swaps;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private bool ShouldIntentionalWalk(GameState state, Player batter)
    {
        if (state.RunnerOnFirst is not null) return false; // base occupied
        if (state.Outs == 2) return false;                 // no upside with 2 outs

        // Only walk a power/dangerous hitter
        int hr = batter.Overall?.HomeRuns ?? 0;
        return hr >= 25 && _rng.NextDouble() < 0.5;
    }

    private PitchType SelectPitch(ActivePitcher pitcher, Player batter, bool lateInning)
    {
        // Weight toward pitcher's best pitch; vary to avoid predictability
        var options = new[] { PitchType.Fastball, PitchType.Fastball, PitchType.Curveball,
                              PitchType.Slider, PitchType.Changeup };
        return options[_rng.Next(options.Length)];
    }

    private static bool IsBunter(Player p) =>
        p.BuntingAbility >= 4 || p.SacrificeAbility >= 4;

    private static bool IsWeakHitter(Player p) =>
        (p.Overall?.HomeRuns ?? 0) < 8 && (p.Overall?.BattingAverage ?? 0) < 0.240;

    private static bool IsContactHitter(Player p) =>
        (p.Overall?.BattingAverage ?? 0) >= 0.270 && (p.Overall?.Strikeouts ?? 99) < 80;
}
