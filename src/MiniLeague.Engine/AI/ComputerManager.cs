using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.AI;

/// <summary>
/// Compares legal calls using matchup-adjusted one-play values and protects limited bench and bullpen resources.
/// </summary>
public class ComputerManager
{
    protected readonly Random Rng;

    /// <summary>The dials this manager runs on; see <see cref="ManagerTuning"/>.</summary>
    protected ManagerTuning Tuning { get; }

    /// <param name="rng">Randomness for tie-breaks and mixed calls (seed it for repeatable games).</param>
    /// <param name="tuning">How readily this manager makes each kind of call; the Analyst's dials when omitted.</param>
    public ComputerManager(Random? rng = null, ManagerTuning? tuning = null)
    {
        Rng = rng ?? Random.Shared;
        Tuning = tuning ?? ManagerTuning.Default;
    }

    // ── Defensive decisions ─────────────────────────────────────────────────

    public virtual DefensiveDecision MakeDefensiveDecision(GameState state, Player? plannedBatter = null)
    {
        var pitcher = state.CurrentPitcher.Player;
        var batter = plannedBatter ?? state.CurrentBatter;
        double fatigue = FatigueModel.GetModifier(state.CurrentPitcher);
        if (ShouldIntentionalWalk(state, batter, pitcher, fatigue))
            return new DefensiveDecision { IntentionalWalk = true };
        var best = new DefensiveDecision();
        double bestValue = double.PositiveInfinity;
        foreach (var alignment in AvailableAlignments(state, batter))
        foreach (var pitch in new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup })
        {
            // WHY: The fielding manager minimizes the batting side's expected runs. Evaluating the same outcome
            // table used by the resolver makes pitch and alignment depend on both players' statistics and the ballpark.
            double value = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, pitch, alignment);
            if (value > bestValue - 0.004 && !(Math.Abs(value - bestValue) < 0.004 && Rng.NextDouble() < 0.2))
                continue;
            bestValue = value;
            best.PitchType = pitch;
            best.Alignment = alignment;
        }

        if (state.RunnerOnFirst is { } runner && state.RunnerOnSecond is null && state.Outs < 2
            && Math.Abs(ScoreMargin(state)) <= 2)
        {
            // WHY: A pitchout is priced as a wasted ball, not through the pitch table. The table scales hits to
            // 10% and doubles walks for it, which makes a pitchout look like the best pitch in the game against
            // any batter; the manager then threw one constantly and every steal attempt ran into it. It is only
            // worth a ball when the runner is likely to go, and even then it is mixed in so it is not predictable.
            double go = ManagerDecisionModel.RunnerGoChance(state, runner.Player);
            int bases = WinProbability.BaseState(state);
            double success = ManagerDecisionModel.StealChance(state, runner.Player, pitchout: true);
            double steals = success * ManagerDecisionModel.StateValue(state, (bases & ~1) | 2, state.Outs)
                + (1 - success) *ManagerDecisionModel.StateValue(state, bases & ~1, state.Outs + 1);
            double pitchout = go * steals + (1 - go) * (bestValue + ManagerDecisionModel.PitchoutBallCost);
            if (pitchout < bestValue - 0.02 && Rng.NextDouble() < Tuning.PitchoutShare)
                best.PitchType = PitchType.Pitchout;
        }
        return best;
    }

    // ── Offensive decisions ─────────────────────────────────────────────────

    public virtual OffensiveDecision MakeOffensiveDecision(GameState state)
    {
        var decision = new OffensiveDecision();
        ChoosePinchRunner(state, decision);
        decision.Strategy = ChooseStrategy(state, state.CurrentBatter, decision);
        // WHY: A steal is resolved without a plate appearance. Do not spend a pinch hitter until the batter
        // will actually swing or bunt; after a substitution, reconsider the strategy with the new batter.
        if (decision.Strategy == OffensiveStrategy.Steal) return decision;
        ChoosePinchHitter(state, decision);
        if (decision.PinchHitterPlayerId is int id)
            decision.Strategy = ChooseStrategy(state, state.OffenseTeam.Roster.First(p => p.Id == id), decision);
        return decision;
    }

    // ── Substitutions ───────────────────────────────────────────────────────

    private void ChoosePinchHitter(GameState state, OffensiveDecision decision)
    {
        if (state.CurrentInning < Tuning.PinchHitFromInning || ScoreMargin(state) is > 2 or < -4) return;

        bool visitor = state.IsTopOfInning;
        var batter = state.CurrentBatter;
        var pitcher = state.CurrentPitcher.Player;
        var bench = state.Bench(visitor)
            .Where(p => (p.Overall?.AtBats ?? 0) >= 100 && p.Id != decision.PinchRunnerPlayerId)
            .ToList();
        if (bench.Count == 0) return;

        double fatigue = FatigueModel.GetModifier(state.CurrentPitcher);
        double current = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue,
            PitchType.Fastball, DefensiveAlignment.Standard);
        var position = state.CurrentBatterPosition;
        double bestGain = 0;
        foreach (var candidate in bench)
        {
            // WHY: The pinch hitter remains in the lineup. A better bat can cost more runs with the glove or
            // consume the last viable catcher, so a raw OPS comparison is insufficient.
            if (position == Position.C && !candidate.IsNaturalAt(Position.C)) continue;
            double fieldCost = position is Position.DH or Position.P ? 0
                : Math.Max(0, batter.FieldingAt(position).Value - candidate.FieldingAt(position).Value)
                    * Math.Max(0, 9 - state.CurrentInning) * 0.006;
            double reserveCost = (bench.Count <= 2 ? 0.045 : 0.015) * Tuning.BenchReserveScale;
            double futureCost = (state.CurrentInning <= 7 ? 0.025 : 0) * Tuning.BenchReserveScale;
            double gain = ManagerDecisionModel.AtBat(state, candidate, pitcher, fatigue,
                PitchType.Fastball, DefensiveAlignment.Standard) - current
                + 0.2 * (Ops(candidate) - Ops(batter))
                - fieldCost - reserveCost - futureCost;
            if (gain <= bestGain + Tuning.PinchHitGain) continue;
            bestGain = gain;
            decision.PinchHitterPlayerId = candidate.Id;
        }
    }

    private void ChoosePinchRunner(GameState state, OffensiveDecision decision)
    {
        if (state.CurrentInning < 8 || ScoreMargin(state) is > 0 or < -2 || state.Outs >= 2) return;

        int baseIndex = state.RunnerOnSecond is not null ? 2 : state.RunnerOnFirst is not null ? 1 : 0;
        if (baseIndex == 0) return;
        var runner = (baseIndex == 2 ? state.RunnerOnSecond : state.RunnerOnFirst)!.Player;
        var bench = state.Bench(state.IsTopOfInning).ToList();
        var position = state.PositionsFor(state.IsTopOfInning)[runner.Id];
        double bestGain = 0;
        foreach (var candidate in bench.Where(p => p.SpeedRating > runner.SpeedRating))
        {
            // WHY: A fast runner helps most from second with one run needed. The replacement also occupies a
            // lineup spot, so spending the last strong bench bat for a small speed gain is usually wasteful.
            double gain = (candidate.SpeedRating - runner.SpeedRating) * (baseIndex == 2 ? 0.055 : 0.043);
            double batCost = Math.Max(0, Ops(runner) - Ops(candidate)) * 0.12;
            double fieldCost = position is Position.DH or Position.P ? 0
                : Math.Max(0, runner.FieldingAt(position).Value - candidate.FieldingAt(position).Value)
                    * Math.Max(0, 9 - state.CurrentInning) * 0.006;
            double reserveCost = bench.Count <= 2 ? 0.045 : 0.01;
            gain -= batCost + fieldCost + reserveCost;
            if (gain <= bestGain + Tuning.PinchRunGain) continue;
            bestGain = gain;
            decision.PinchRunnerPlayerId = candidate.Id;
            decision.PinchRunnerBaseIndex = baseIndex;
        }
    }

    // How far the batting team leads (negative when trailing).
    protected static int ScoreMargin(GameState state) => state.IsTopOfInning
        ? state.VisitorTotalScore - state.HomeTotalScore
        : state.HomeTotalScore - state.VisitorTotalScore;

    protected static double Ops(Player p) =>
        (p.Overall?.OnBasePercentage ?? 0) + (p.Overall?.SluggingPct ?? 0);

    private OffensiveStrategy ChooseStrategy(GameState state, Player batter, OffensiveDecision decision)
    {
        var pitcher = state.CurrentPitcher.Player;
        double fatigue = FatigueModel.GetModifier(state.CurrentPitcher);
        var alignment = state.IsTopOfInning ? state.HomeAlignment : state.VisitorAlignment;
        var pitch = new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup }
            .MinBy(p => ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, p, alignment));
        double bestValue = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue,
            pitch, alignment);
        var best = OffensiveStrategy.SwingAway;

        // WHY: In this resolver aggressive running can take an extra base but cannot be thrown out. It therefore
        // wins only when a runner can actually benefit; keep the ordinary call when the projected values tie.
        double aggressive = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue,
            pitch, alignment, OffensiveStrategy.SwingAgressive);
        if (aggressive > bestValue + Tuning.AggressiveRunMargin) { bestValue = aggressive; best = OffensiveStrategy.SwingAgressive; }

        bool firstOnly = state.RunnerOnFirst is not null && state.RunnerOnSecond is null;
        // WHY: When already ahead late, an unnecessary caught stealing or line-drive double-off is more costly
        // than the extra base; leave the runner in place and make the batter use his plate appearance.
        if (firstOnly && state.Outs < 2 && !(state.CurrentInning >= 7 && ScoreMargin(state) > 0))
        {
            var runner = decision.PinchRunnerPlayerId is int runnerId && decision.PinchRunnerBaseIndex == 1
                ? state.OffenseTeam.Roster.First(p => p.Id == runnerId) : state.RunnerOnFirst!.Player;
            double steal = ManagerDecisionModel.Steal(state, runner);
            double staying = ManagerDecisionModel.StateValue(state, WinProbability.BaseState(state), state.Outs);
            // WHY: Stealing consumes a separate play, so compare its expected next situation to simply keeping
            // the current runner. The catcher, pitcher's hold rating, and runner's own record set the success odds.
            if (steal > staying + Tuning.StealMargin && (state.CurrentInning >= 6 || ScoreMargin(state) <= 0))
                return OffensiveStrategy.Steal;

            double hitAndRun = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue,
                pitch, alignment, OffensiveStrategy.HitAndRun);
            if (hitAndRun > bestValue + Tuning.HitAndRunMargin && IsContactHitter(batter))
            { bestValue = hitAndRun; best = OffensiveStrategy.HitAndRun; }
        }

        if (state.Outs == 0 && !(state.CurrentInning >= 7 && ScoreMargin(state) <= -2)
            && (state.RunnerOnFirst is not null || state.RunnerOnSecond is not null)
            && state.RunnerOnThird is null)
        {
            // WHY: Giving away an out is worthwhile only when the bunt's hit, error, and advancement chances
            // beat swinging in this base/out/score situation. Pitchers are evaluated by the same rule.
            double bunt = ManagerDecisionModel.Bunt(state, batter, alignment);
            if (bunt > bestValue + Tuning.BuntMargin) best = OffensiveStrategy.SacrificeBunt;
        }
        return best;
    }

    // ── Bullpen management ──────────────────────────────────────────────────

    /// <summary>What the computer wants to do with its pitching staff before the next batter.</summary>
    public record PitchingMove(int? StartWarmingId = null, int? ChangeToId = null);

    /// <summary>
    /// Decides whether the fielding side should start a reliever warming up or bring one in.
    /// Fatigue drives it (warm up once the starter is past his first threshold, change when he
    /// is really tiring), and a closer is held for a save situation in the 9th.
    /// </summary>
    public virtual PitchingMove DecidePitching(GameState state, bool visitor)
    {
        var pitcher = state.PitcherFor(visitor);
        var pen = state.BullpenFor(visitor);
        if (pen.Count == 0) return new PitchingMove();

        double currentFatigue = FatigueModel.GetModifier(pitcher);
        int lead = visitor
            ? state.VisitorTotalScore - state.HomeTotalScore
            : state.HomeTotalScore - state.VisitorTotalScore;
        bool saveSpot = state.CurrentInning >= 9 && lead is >= 1 and <= 3;
        bool saveSoon = state.CurrentInning >= 8 && lead is >= 1 and <= 3;
        var closer = pen.FirstOrDefault(w => w.Player.Role == PitcherRole.Closer);
        var ready = pen.Where(w => w.IsWarm).ToList();
        bool settled = pitcher.BattersFaced - pitcher.SettleFrom >= FatigueModel.SettleInBatters;
        bool spent = pitcher.Condition >= PitcherCondition.Tired && settled;
        bool starter = pitcher.Player.Role == PitcherRole.Starter;
        double runsPerBatter = pitcher.RunsAllowed / (double)Math.Max(1, pitcher.BattersFaced);
        bool shelled = pitcher.BattersFaced >= 5 && pitcher.RunsAllowed >= (starter ? 5 : 3)
            && runsPerBatter >= 0.18;
        double currentValue = PitchingValue(state, pitcher.Player, currentFatigue);

        // WHY: A warm arm with a better ERA can still be wrong for the next three hitters. Compare each arm against
        // those hitters, with fatigue, while charging the closer for using him before a save and cold arms for poor entry.
        // NOTE: the team data has no left/right splits and the engine applies no platoon effect, so today this
        // comparison sees only each pitcher's overall season line; a platoon effect would make it a true matchup.
        var next = BestReliever(state, ready, saveSpot);
        bool closerReady = saveSpot && closer is { IsWarm: true }
            && pitcher.Player.Role != PitcherRole.Closer;
        double edge = next is null ? 0 : currentValue - RelieverValue(state, next, saveSpot);
        double threshold = state.CurrentInning >= 7 && Math.Abs(lead) <= 2 ? Tuning.ReliefEdgeClose : Tuning.ReliefEdgeOther;
        if (next is not null && (closerReady && next == closer || spent || shelled
            || settled && pitcher.Condition >= PitcherCondition.Tiring && edge > -0.08
            || settled && (pitcher.Condition >= PitcherCondition.Ok || pitcher.PitchCount >= Tuning.QuickHookPitchCount)
                && edge > threshold))
            return new PitchingMove(ChangeToId: next.Player.Id);

        if ((spent || shelled) && next is null)
        {
            // WHY: A spent pitcher sometimes cannot wait four more batters; the warm-up penalty is still used
            // when choosing which available arm suffers least from entering cold.
            var emergency = BestReliever(state, pen, saveSpot);
            if (emergency is not null) return new PitchingMove(ChangeToId: emergency.Player.Id);
        }

        // WHY: An overwarmed arm no longer occupies the useful warm-up window. If another reliever is resting,
        // start him rather than treating the stale arm as permanent bullpen coverage.
        bool anyWarming = pen.Any(w => w.IsWarming && !w.IsOverwarmed);
        if (!anyWarming)
        {
            if (saveSoon && pitcher.Player.Role != PitcherRole.Closer && closer is { IsWarming: false })
                return new PitchingMove(StartWarmingId: closer.Player.Id);
            if (settled && (pitcher.Condition >= PitcherCondition.Ok || shelled ||
                state.CurrentInning >= Tuning.WarmUpFromInning && Math.Abs(lead) <= 2
                && pitcher.PitchCount >= Tuning.WarmUpPitchCount))
            {
                var warm = BestReliever(state, pen.Where(w => !w.IsWarming), saveSoon);
                if (warm is not null) return new PitchingMove(StartWarmingId: warm.Player.Id);
            }
        }
        return new PitchingMove();
    }

    private static WarmingPitcher? BestReliever(GameState state, IEnumerable<WarmingPitcher> options, bool saveSpot) =>
        options.OrderBy(w => RelieverValue(state, w, saveSpot)).FirstOrDefault();

    private static double RelieverValue(GameState state, WarmingPitcher w, bool saveSpot)
    {
        double value = PitchingValue(state, w.Player, FatigueModel.GetWarmupModifier(w));
        // WHY: A closer's save role matters, but it does not erase a large matchup mismatch. Outside a save,
        // preserving him has a real opportunity value, unless he is the last arm available.
        if (w.Player.Role == PitcherRole.Closer) value += saveSpot ? -0.08 : 0.20;
        return value;
    }

    private static double PitchingValue(GameState state, Player pitcher, double fatigue)
    {
        var lineup = state.LineupFor(state.IsTopOfInning);
        var team = state.OffenseTeam;
        if (lineup.Count == 0) return 0;
        double total = 0;
        for (int n = 0; n < Math.Min(3, lineup.Count); n++)
        {
            var batter = team.Roster.First(p => p.Id == lineup[(state.CurrentBatterIndex + n) % lineup.Count]);
            // WHY: The pitcher will normally face a pocket of hitters, not just the man at the plate. Let him
            // use his best modeled pitch against each, then average the expected scoring against that pocket.
            total += new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup }
                .Min(p => ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, p, DefensiveAlignment.Standard));
        }
        return total / Math.Min(3, lineup.Count);
    }

    // Best available arm: the closer only in a save spot (or when he is all that is left).
    protected static WarmingPitcher? PickReliever(
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

    private static IEnumerable<DefensiveAlignment> AvailableAlignments(GameState state, Player batter)
    {
        yield return DefensiveAlignment.Standard;
        // WHY: The infield comes in only with a possible runner to cut down at the plate. The corners come in
        // only when a bunt can matter. Otherwise those alignments would exploit a probability modifier for free.
        if (state.RunnerOnThird is not null && state.Outs < 2)
            yield return DefensiveAlignment.InfieldIn;
        if (state.Outs < 2 && (state.RunnerOnFirst is not null || state.RunnerOnSecond is not null)
            && IsBunter(batter))
            yield return DefensiveAlignment.CornersIn;
        if (state.CurrentInning >= 7 && Math.Abs(ScoreMargin(state)) <= 2)
            yield return DefensiveAlignment.GuardLines;
        // NOTE: The data does not have spray charts; power and batting side are only rough shift clues, so
        // ordinary positioning stays available and wins ties.
        if (batter.PowerRating >= 3) yield return DefensiveAlignment.ShiftPull;
        if (batter.PowerRating <= 2) yield return DefensiveAlignment.ShiftOpposite;
    }

    private bool ShouldIntentionalWalk(GameState state, Player batter, Player pitcher, double fatigue)
    {
        if (state.RunnerOnFirst is not null || state.Outs >= 2 ||
            state.RunnerOnSecond is null && state.RunnerOnThird is null ||
            state.CurrentInning < 7 || Math.Abs(ScoreMargin(state)) > 2) return false;
        var lineup = state.LineupFor(state.IsTopOfInning);
        if (lineup.Count < 2) return false;
        var next = state.OffenseTeam.Roster.First(p => p.Id == lineup[(state.CurrentBatterIndex + 1) % lineup.Count]);
        double gap = Ops(batter) - Ops(next);
        if (gap < 0.15 || Ops(batter) < 0.8) return false;

        // WHY: An intentional walk buys the next hitter at the cost of a base runner. Price the forced runner
        // state first, then credit only a bounded part of the on-deck matchup; a star alone is never enough.
        double walk = ManagerDecisionModel.WalkValue(state, WinProbability.BaseState(state), state.Outs)
            - Math.Clamp(gap * 0.7, 0, 0.35);
        double face = new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup }
            .Min(p => ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, p, DefensiveAlignment.Standard));
        return walk + Tuning.IntentionalWalkMargin < face;
    }

    protected static bool IsBunter(Player p) =>
        p.BuntingAbility >= 4 || p.SacrificeAbility >= 4;

    protected static bool IsContactHitter(Player p) =>
        (p.Overall?.BattingAverage ?? 0) >= 0.270 && (p.Overall?.Strikeouts ?? 99) < 80;
}
