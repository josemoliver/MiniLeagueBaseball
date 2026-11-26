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
        => ChooseDefense(state, plannedBatter, forHint: false);

    // WHY: the advisor uses the Analyst's valuation without the manager's random tie-breaks, pitch mixing or pitchout.
    internal DefensiveDecision MakeHintDefensiveDecision(GameState state) => ChooseDefense(state, null, forHint: true);

    private DefensiveDecision ChooseDefense(GameState state, Player? plannedBatter, bool forHint)
    {
        using var decision = ManagerDecisionModel.Decision(state);
        var pitcher = state.CurrentPitcher.Player;
        var batter = plannedBatter ?? state.CurrentBatter;
        double fatigue = FatigueModel.GetModifier(state.CurrentPitcher);
        if (ShouldIntentionalWalk(state, batter, pitcher, fatigue, out double walkGain))
            return new DefensiveDecision
            {
                IntentionalWalk = true,
                Why = new DecisionExplanation(
                    $"Intentional walk: putting {batter.Name} on base costs {walkGain:+0.00;-0.00} runs less than pitching to him.",
                    [new("Intentional walk", walkGain), new("Pitch to him", 0)]),
            };
        var best = new DefensiveDecision();
        double bestValue = double.PositiveInfinity;
        // The best value each alignment can reach with its best pitch, to show what the chosen one saved.
        var byAlignment = new Dictionary<DefensiveAlignment, double>();
        var candidates = new List<(DefensiveAlignment Alignment, PitchType Pitch, double Value)>();
        foreach (var alignment in AvailableAlignments(state, batter))
        foreach (var pitch in new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup })
        {
            // WHY: The fielding manager minimizes the batting side's expected runs. Evaluating the same outcome
            // table used by the resolver makes pitch and alignment depend on both players' statistics and the ballpark.
            double value = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, pitch, alignment);
            byAlignment[alignment] = Math.Min(byAlignment.GetValueOrDefault(alignment, double.PositiveInfinity), value);
            candidates.Add((alignment, pitch, value));
            if (forHint ? value >= bestValue - 0.004
                : value > bestValue - 0.004 && !(Math.Abs(value - bestValue) < 0.004 && Rng.NextDouble() < 0.2))
                continue;
            bestValue = value;
            best.PitchType = pitch;
            best.Alignment = alignment;
        }
        // WHY: a manager who is hard to read does not always throw the best pitch. Every option gets a chance that falls
        // off with how much worse it is (a softmax whose temperature is PitchMix, in runs), so the best is still the most
        // likely, and a clearly bad one almost never comes up.
        if (!forHint && Tuning.PitchMix > 0 && candidates.Count > 1)
        {
            double floor = candidates.Min(c => c.Value);
            var weights = candidates.Select(c => Math.Exp(-(c.Value - floor) / Tuning.PitchMix)).ToList();
            double roll = Rng.NextDouble() * weights.Sum(), cum = 0;
            int pick = candidates.Count - 1;
            for (int i = 0; i < weights.Count; i++)
                if ((cum += weights[i]) > roll) { pick = i; break; }
            (best.Alignment, best.PitchType, bestValue) = candidates[pick];
        }
        double normal = byAlignment[DefensiveAlignment.Standard];
        var considered = byAlignment.Select(kv => new DecisionOption(kv.Key.ToString(), normal - kv.Value))
            .OrderByDescending(o => o.Gain).ToList();
        best.Why = new DecisionExplanation(best.Alignment == DefensiveAlignment.Standard
                ? $"{best.PitchType} with a normal defense: no alignment saves enough against {batter.Name}."
                : $"{best.PitchType} with {best.Alignment}: saves {normal - bestValue:+0.00;-0.00} runs against {batter.Name}.",
            considered);

        if (!forHint && state.RunnerOnFirst is { } runner && state.RunnerOnSecond is null && state.Outs < 2
            && Math.Abs(ScoreMargin(state)) <= 2)
        {
            // WHY: A pitchout is a wasted ball. Against a runner who goes it is a steal attempt the catcher is ready
            // for; against one who stays it is a plate appearance with the pitcher behind in the count (the pitch
            // table's Pitchout row). It is only worth it when the runner is likely to go, and even then it is mixed
            // in so it is not predictable.
            double go = ManagerDecisionModel.RunnerGoChance(state, runner.Player);
            int bases = WinProbability.BaseState(state);
            double success = ManagerDecisionModel.StealChance(state, runner.Player, pitchout: true);
            double steals = success * ManagerDecisionModel.StateValue(state, (bases & ~1) | 2, state.Outs)
                + (1 - success) * ManagerDecisionModel.StateValue(state, bases & ~1, state.Outs + 1);
            double wasted = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, PitchType.Pitchout, best.Alignment);
            double pitchout = go * steals + (1 - go) * wasted;
            if (pitchout < bestValue - 0.02 && Rng.NextDouble() < Tuning.PitchoutShare)
            {
                best.PitchType = PitchType.Pitchout;
                best.Why = new DecisionExplanation(
                    $"Pitchout: {runner.Player.Name} is likely to run, and it saves {bestValue - pitchout:+0.00;-0.00} runs.",
                    [new("Pitchout", bestValue - pitchout), .. considered]);
            }
        }
        return best;
    }

    // ── Offensive decisions ─────────────────────────────────────────────────

    public virtual OffensiveDecision MakeOffensiveDecision(GameState state)
    {
        using var scope = ManagerDecisionModel.Decision(state);
        var decision = new OffensiveDecision();
        var notes = new List<string>();
        ChoosePinchRunner(state, decision, notes);
        decision.Strategy = ChooseStrategy(state, state.CurrentBatter, decision, out var why);
        // WHY: A steal is resolved without a plate appearance. Do not spend a pinch hitter until the batter
        // will actually swing or bunt; after a substitution, reconsider the strategy with the new batter.
        if (decision.Strategy != OffensiveStrategy.Steal)
        {
            ChoosePinchHitter(state, decision, notes);
            if (decision.PinchHitterPlayerId is int id)
                decision.Strategy = ChooseStrategy(state, state.OffenseTeam.Roster.First(p => p.Id == id), decision, out why);
        }
        decision.Why = notes.Count == 0 ? why : why with { Summary = string.Join(" ", notes.Append(why.Summary)) };
        return decision;
    }

    internal OffensiveDecision MakeHintOffensiveDecision(GameState state, DefensiveAlignment visibleAlignment)
    {
        using var scope = ManagerDecisionModel.Decision(state);
        var decision = new OffensiveDecision();
        decision.Strategy = ChooseStrategy(state, state.CurrentBatter, decision, out var why, visibleAlignment);
        decision.Why = why;
        return decision;
    }

    // ── Substitutions ───────────────────────────────────────────────────────

    private void ChoosePinchHitter(GameState state, OffensiveDecision decision, List<string> notes)
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
        double needed = Scale(Tuning.PinchHitGain, LeverageFactor(state));
        // WHY: batting for the pitcher also takes him out of the game, so a reliever is used up and the manager has to be
        // ready to lose his starter: behind or tied late, or the pitcher is tired. The pitcher's poor bat makes the hitting
        // gain large every time, so without this he would be pulled in the sixth inning of any close game.
        double pitcherCost = 0;
        if (position == Position.P)
        {
            var arm = state.PitcherFor(visitor);
            bool wanted = ScoreMargin(state) <= 0 && state.CurrentInning >= 6
                || arm.PitchCount >= 79 || arm.Condition >= PitcherCondition.Tiring;
            if (!wanted || state.BullpenFor(visitor).Count == 0) return;
            pitcherCost = 0.03;
        }
        // How much of a hitter's value is his platoon matchup; the manager's weight on it re-scales that part.
        double Platoon(Player b) => Tuning.PlatoonWeight == 1.0 ? 0
            : ManagerDecisionModel.AtBat(state, b, pitcher, fatigue, PitchType.Fastball, DefensiveAlignment.Standard)
              - ManagerDecisionModel.AtBat(state, b, pitcher, fatigue, PitchType.Fastball, DefensiveAlignment.Standard,
                  platoon: false);
        double currentPlatoon = Platoon(batter);
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
                + (Tuning.PlatoonWeight - 1.0) * (Platoon(candidate) - currentPlatoon)
                - fieldCost - reserveCost - futureCost - pitcherCost;
            if (gain <= bestGain + needed) continue;
            bestGain = gain;
            decision.PinchHitterPlayerId = candidate.Id;
        }
        if (decision.PinchHitterPlayerId is int chosen)
            notes.Add($"Pinch hitter {state.OffenseTeam.Roster.First(p => p.Id == chosen).Name} for {batter.Name} " +
                      $"({bestGain:+0.00;-0.00} runs after the cost of using the bench).");
    }

    private void ChoosePinchRunner(GameState state, OffensiveDecision decision, List<string> notes)
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
            if (gain <= bestGain + Scale(Tuning.PinchRunGain, LeverageFactor(state))) continue;
            bestGain = gain;
            decision.PinchRunnerPlayerId = candidate.Id;
            decision.PinchRunnerBaseIndex = baseIndex;
        }
        if (decision.PinchRunnerPlayerId is int chosen)
            notes.Add($"Pinch runner {state.OffenseTeam.Roster.First(p => p.Id == chosen).Name} for {runner.Name} " +
                      $"({bestGain:+0.00;-0.00} runs).");
    }

    // How far the batting team leads (negative when trailing).
    protected static int ScoreMargin(GameState state) => state.IsTopOfInning
        ? state.VisitorTotalScore - state.HomeTotalScore
        : state.HomeTotalScore - state.VisitorTotalScore;

    protected static double Ops(Player p) =>
        (p.Overall?.OnBasePercentage ?? 0) + (p.Overall?.SluggingPct ?? 0);

    private OffensiveStrategy ChooseStrategy(GameState state, Player batter, OffensiveDecision decision,
        out DecisionExplanation why, DefensiveAlignment? visibleAlignment = null)
    {
        var pitcher = state.CurrentPitcher.Player;
        double fatigue = FatigueModel.GetModifier(state.CurrentPitcher);
        var alignment = visibleAlignment ?? (state.IsTopOfInning ? state.HomeAlignment : state.VisitorAlignment);
        // WHY: the defense's best pitch against him is the one that costs the offense most, so he is valued once per pitch and
        // the lowest is kept (the same valuations the fielding manager's own choice uses, which the decision's cache shares).
        var pitch = PitchType.Fastball;
        double bestValue = double.PositiveInfinity;
        foreach (var candidate in new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup })
        {
            double value = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, candidate, alignment);
            if (value < bestValue) { bestValue = value; pitch = candidate; }
        }
        double swing = bestValue;
        // WHY: how readily he acts depends on how much the moment matters (leverage) and, for the gambles, on whether
        // the score has him pressing or protecting a lead.
        double lev = LeverageFactor(state);
        double gamble = GambleShift(state);
        var best = OffensiveStrategy.SwingAway;
        var considered = new List<DecisionOption> { new("Swing away", 0) };

        // WHY: An aggressive send can take an extra base, or get the runner thrown out (BaseRunningEngine), so it is
        // priced like any other call; keep the ordinary call when the projected values tie.
        double aggressive = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue,
            pitch, alignment, OffensiveStrategy.SwingAgressive);
        if (state.RunnerOnFirst is not null || state.RunnerOnSecond is not null)
            considered.Add(new("Run aggressively", aggressive - swing));
        if (aggressive > bestValue + Scale(Tuning.AggressiveRunMargin, lev) + gamble) { bestValue = aggressive; best = OffensiveStrategy.SwingAgressive; }

        bool firstOnly = state.RunnerOnFirst is not null && state.RunnerOnSecond is null;
        // WHY: When already ahead late, an unnecessary caught stealing or line-drive double-off is more costly
        // than the extra base; leave the runner in place and make the batter use his plate appearance.
        if (firstOnly && state.Outs < 2 && !(state.CurrentInning >= 7 && ScoreMargin(state) > 0))
        {
            var runner = decision.PinchRunnerPlayerId is int runnerId && decision.PinchRunnerBaseIndex == 1
                ? state.OffenseTeam.Roster.First(p => p.Id == runnerId) : state.RunnerOnFirst!.Player;
            double steal = ManagerDecisionModel.Steal(state, runner);
            double staying = ManagerDecisionModel.StateValue(state, WinProbability.BaseState(state), state.Outs);
            considered.Add(new("Steal second", steal - staying));
            // WHY: Stealing consumes a separate play, so compare its expected next situation to simply keeping
            // the current runner. The catcher, pitcher's hold rating, and runner's own record set the success odds.
            if (steal > staying + Scale(Tuning.StealMargin, lev) + gamble && (state.CurrentInning >= 6 || ScoreMargin(state) <= 0))
            {
                why = new DecisionExplanation(
                    $"Steal: {runner.Name} going is worth {steal - staying:+0.00;-0.00} runs over staying at first.", considered);
                return OffensiveStrategy.Steal;
            }

            double hitAndRun = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue,
                pitch, alignment, OffensiveStrategy.HitAndRun);
            if (IsContactHitter(batter)) considered.Add(new("Hit and run", hitAndRun - swing));
            if (hitAndRun > bestValue + Scale(Tuning.HitAndRunMargin, lev) + gamble && IsContactHitter(batter))
            { bestValue = hitAndRun; best = OffensiveStrategy.HitAndRun; }
        }

        // WHY: a steal of third, or a double steal, is priced like a steal of second: the situation it leaves if it works, the one
        // if the runner is out, and what staying put is worth. A steal of third costs a runner in scoring position to gain a
        // base he can usually get on a single anyway, so it takes a strong runner, a weak battery and a close game.
        var plan = RunningRules.StealPlanFor(state);
        if (plan is StealPlan.Third or StealPlan.Double && state.Outs < 2
            && !(state.CurrentInning >= 7 && ScoreMargin(state) > 0))
        {
            double stealMore = plan == StealPlan.Third ? ManagerDecisionModel.StealThird(state) : ManagerDecisionModel.DoubleSteal(state);
            double stay = ManagerDecisionModel.StateValue(state, WinProbability.BaseState(state), state.Outs);
            string label = plan == StealPlan.Third ? "Steal third" : "Double steal";
            considered.Add(new(label, stealMore - stay));
            if (stealMore > stay + Scale(Tuning.StealMargin, lev) + gamble && (state.CurrentInning >= 6 || ScoreMargin(state) <= 0))
            {
                why = new DecisionExplanation($"{label}: worth {stealMore - stay:+0.00;-0.00} runs over staying put.", considered);
                return OffensiveStrategy.Steal;
            }
        }

        // WHY: with a runner on third and fewer than two out a bunt can bring him home. A suicide squeeze has him break with
        // the pitch (it scores most often but a popped-up bunt costs him, and a read play puts him out at the plate); a safety
        // squeeze waits for the bunt (it scores less often and loses nothing). Both are valued by what the on-deck hitter makes of
        // the situation they leave, against a swing.
        if (state.RunnerOnThird is not null && state.Outs < 2 && OnDeckHitter(state) is { } squeezeOnDeck)
        {
            double swingAhead, suicide, safety;
            using (ManagerDecisionModel.Lookahead(state, squeezeOnDeck, pitcher, fatigue))
            {
                swingAhead = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, pitch, alignment);
                suicide = ManagerDecisionModel.Squeeze(state, batter, alignment, SqueezeKind.Suicide);
                safety = ManagerDecisionModel.Squeeze(state, batter, alignment, SqueezeKind.Safety);
            }
            double lead = bestValue - swing;   // the best other call's lead over a plain swing, carried to the same footing
            considered.Add(new("Squeeze", suicide - swingAhead));
            considered.Add(new("Safety squeeze", safety - swingAhead));
            double needed = Scale(Tuning.SqueezeMargin, lev);
            var (squeezeValue, squeezeCall) = suicide >= safety
                ? (suicide, OffensiveStrategy.Squeeze) : (safety, OffensiveStrategy.SafetySqueeze);
            if (squeezeValue - swingAhead - lead > needed)
            {
                bestValue = swing + (squeezeValue - swingAhead);
                best = squeezeCall;
            }
        }

        if (state.Outs == 0 && !(state.CurrentInning >= 7 && ScoreMargin(state) <= -2)
            && (state.RunnerOnFirst is not null || state.RunnerOnSecond is not null)
            && state.RunnerOnThird is null)
        {
            // WHY: Giving away an out is worthwhile only when the bunt's hit, error, and advancement chances beat swinging in
            // this base/out/score situation, and that depends on who bats next, which run expectancy cannot see. Both the swing
            // and the bunt are valued by what the on-deck hitter makes of the situation each one leaves.
            double bunt, swingAhead;
            if (OnDeckHitter(state) is { } onDeck)
            {
                using (ManagerDecisionModel.Lookahead(state, onDeck, pitcher, fatigue))
                {
                    swingAhead = ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, pitch, alignment);
                    bunt = ManagerDecisionModel.Bunt(state, batter, alignment);
                }
            }
            else
            {
                swingAhead = swing;
                bunt = ManagerDecisionModel.Bunt(state, batter, alignment);
            }
            // The best other call (running, hit and run) is carried over to the same footing as its lead over a plain swing.
            double buntEdge = bunt - swingAhead - (bestValue - swing);
            considered.Add(new("Sacrifice bunt", bunt - swingAhead));
            if (buntEdge > Scale(Tuning.BuntMargin, lev)) { bestValue = swing + (bunt - swingAhead); best = OffensiveStrategy.SacrificeBunt; }
        }

        string call = best switch
        {
            OffensiveStrategy.SwingAgressive => "Run aggressively",
            OffensiveStrategy.HitAndRun => "Hit and run",
            OffensiveStrategy.SacrificeBunt => "Sacrifice bunt",
            OffensiveStrategy.Squeeze => "Squeeze",
            OffensiveStrategy.SafetySqueeze => "Safety squeeze",
            _ => "Swing away",
        };
        why = new DecisionExplanation(best == OffensiveStrategy.SwingAway
                ? $"Swing away: nothing else is worth enough with {batter.Name} up."
                : $"{call}: worth {bestValue - swing:+0.00;-0.00} runs over swinging away with {batter.Name} up.",
            considered);
        return best;
    }

    // ── Bullpen management ──────────────────────────────────────────────────

    /// <summary>What the computer wants to do with its pitching staff before the next batter.</summary>
    public record PitchingMove(int? StartWarmingId = null, int? ChangeToId = null, string? Why = null);

    /// <summary>
    /// Decides whether the fielding side should start a reliever warming up or bring one in.
    /// Fatigue drives it (warm up once the starter is past his first threshold, change when he
    /// is really tiring), and a closer is held for a save situation in the 9th.
    /// </summary>
    public virtual PitchingMove DecidePitching(GameState state, bool visitor)
    {
        using var scope = ManagerDecisionModel.Decision(state);
        var pitcher = state.PitcherFor(visitor);
        var pen = state.BullpenFor(visitor);
        if (pen.Count == 0) return new PitchingMove();

        double currentFatigue = FatigueModel.GetModifier(pitcher);
        int lead = visitor
            ? state.VisitorTotalScore - state.HomeTotalScore
            : state.HomeTotalScore - state.VisitorTotalScore;
        bool saveSpot = state.CurrentInning >= Tuning.CloserFromInning && lead is >= 1 and <= 3;
        bool saveSoon = state.CurrentInning >= Tuning.CloserFromInning - 1 && lead is >= 1 and <= 3;
        var closer = pen.FirstOrDefault(w => w.Player.Role == PitcherRole.Closer);
        var ready = pen.Where(w => w.IsWarm).ToList();
        bool settled = pitcher.BattersFaced - pitcher.SettleFrom >= FatigueModel.SettleInBatters;
        bool spent = pitcher.Condition >= PitcherCondition.Tired && settled;
        bool starter = pitcher.Player.Role == PitcherRole.Starter;
        double runsPerBatter = pitcher.RunsAllowed / (double)Math.Max(1, pitcher.BattersFaced);
        bool shelled = pitcher.BattersFaced >= 5 && pitcher.RunsAllowed >= (starter ? Tuning.ShelledRuns : 3)
            && runsPerBatter >= 0.18;

        // WHY: A warm arm with a better ERA can still be wrong for the next three hitters. Compare each arm against
        // those hitters, platoon matchups and fatigue included, while charging the closer for using him before a save
        // and cold arms for poor entry.
        var next = BestReliever(state, ready, saveSpot);
        bool closerReady = saveSpot && closer is { IsWarm: true }
            && pitcher.Player.Role != PitcherRole.Closer;
        // WHY: his value over the next three hitters is worked out only if there is a warm reliever to compare it with; most
        // plays there is not, and it is the most expensive thing in this method.
        double edge = next is null ? 0 : PitchingValue(state, pitcher.Player, currentFatigue) - RelieverValue(state, next, saveSpot);
        double lev = LeverageFactor(state);
        double threshold = Scale(state.CurrentInning >= 7 && Math.Abs(lead) <= 2 ? Tuning.ReliefEdgeClose : Tuning.ReliefEdgeOther, lev);
        double tiringMargin = Scale(Tuning.TiringSwapMargin, lev);
        if (next is not null)
        {
            string? reason = closerReady && next == closer ? $"{next.Player.Name} comes in for the save."
                : spent ? $"{pitcher.Player.Name} is spent."
                : shelled ? $"{pitcher.Player.Name} is being hit hard."
                : starter && pitcher.PitchCount >= Tuning.StarterPitchLimit
                    ? $"{pitcher.Player.Name} has thrown {pitcher.PitchCount} pitches: that is his limit."
                : settled && pitcher.Condition >= PitcherCondition.Tiring && edge > tiringMargin
                    ? $"{pitcher.Player.Name} is tiring, and {next.Player.Name} is as good for the next three hitters."
                : settled && (pitcher.Condition >= PitcherCondition.Ok || pitcher.PitchCount >= Tuning.QuickHookPitchCount)
                    && edge > threshold
                    ? $"{next.Player.Name} is {edge:+0.00;-0.00} runs better for the next three hitters."
                : null;
            if (reason is not null) return new PitchingMove(ChangeToId: next.Player.Id, Why: reason);
        }

        if ((spent || shelled) && next is null)
        {
            // WHY: A spent pitcher sometimes cannot wait four more batters; the warm-up penalty is still used
            // when choosing which available arm suffers least from entering cold.
            var emergency = BestReliever(state, pen, saveSpot);
            if (emergency is not null)
                return new PitchingMove(ChangeToId: emergency.Player.Id,
                    Why: $"{pitcher.Player.Name} cannot go on and nobody is warm: {emergency.Player.Name} comes in cold.");
        }

        // WHY: An overwarmed arm no longer occupies the useful warm-up window. If another reliever is resting,
        // start him rather than treating the stale arm as permanent bullpen coverage.
        bool anyWarming = pen.Any(w => w.IsWarming && !w.IsOverwarmed);
        if (!anyWarming)
        {
            if (saveSoon && pitcher.Player.Role != PitcherRole.Closer && closer is { IsWarming: false })
                return new PitchingMove(StartWarmingId: closer.Player.Id, Why: $"{closer.Player.Name} warms up for a save.");
            if (settled && (pitcher.Condition >= PitcherCondition.Ok || shelled
                || starter && pitcher.PitchCount >= Tuning.StarterPitchLimit - 15 ||
                state.CurrentInning >= Tuning.WarmUpFromInning && Math.Abs(lead) <= 2
                && pitcher.PitchCount >= Tuning.WarmUpPitchCount))
            {
                var warm = BestReliever(state, pen.Where(w => !w.IsWarming), saveSoon);
                if (warm is not null)
                    return new PitchingMove(StartWarmingId: warm.Player.Id,
                        Why: $"{warm.Player.Name} warms up behind {pitcher.Player.Name}.");
            }
        }
        return new PitchingMove();
    }

    /// <summary>
    /// The reliever to bring in when a pinch hitter has taken the pitcher's spot and the pitcher is out of the game: the best
    /// arm for the next three hitters, preferring one who is warm. Null if the bullpen is empty.
    /// </summary>
    public virtual int? ChooseForcedReliever(GameState state, bool visitor)
    {
        using var scope = ManagerDecisionModel.Decision(state);
        var pen = state.BullpenFor(visitor);
        if (pen.Count == 0) return null;
        int lead = visitor
            ? state.VisitorTotalScore - state.HomeTotalScore
            : state.HomeTotalScore - state.VisitorTotalScore;
        bool saveSpot = state.CurrentInning >= Tuning.CloserFromInning && lead is >= 1 and <= 3;
        var pool = pen.Any(w => w.IsWarm) ? pen.Where(w => w.IsWarm) : pen;
        return BestReliever(state, pool, saveSpot)?.Player.Id;
    }

    /// <summary>A double switch: the fielder who leaves, and the bench player who takes his position.</summary>
    public record DoubleSwitchMove(int LineupPlayerId, int BenchPlayerId, string Why);

    /// <summary>
    /// With a pitching change and the pitcher in the batting order: put the new pitcher in the spot of a fielder who bats
    /// last, and a bench bat in the pitcher's old spot, which comes up sooner. Worth it when that spot is due soon, the bench
    /// player is a natural at the fielder's position, and the bat gained is worth more than the fielder's bat lost (a spot
    /// that is far off, so rarely used) and the bench player used. Null when it is not.
    /// </summary>
    public virtual DoubleSwitchMove? ChooseDoubleSwitch(GameState state, bool visitor, int relieverId)
    {
        if (state.DHRule) return null;
        using var scope = ManagerDecisionModel.Decision(state);
        var lineup = state.LineupFor(visitor);
        var positions = state.PositionsFor(visitor);
        int pitcherSlot = lineup.FindIndex(id => positions[id] == Position.P);
        if (pitcherSlot < 0) return null;

        var roster = state.TeamFor(visitor).Roster;
        int next = state.BatterIndexFor(visitor) % lineup.Count;
        int Due(int slot) => (slot - next + lineup.Count) % lineup.Count;   // batters until this spot comes up
        int pitcherDue = Due(pitcherSlot);
        if (pitcherDue > 3) return null;   // his turn is too far off for it to matter

        var bench = state.Bench(visitor).Where(p => (p.Overall?.AtBats ?? 0) >= 100).ToList();
        if (bench.Count == 0) return null;
        var pitcherBat = roster.First(p => p.Id == lineup[pitcherSlot]);
        double soon = pitcherDue switch { 0 => 1.0, 1 => 0.7, 2 => 0.45, _ => 0.25 };
        double reserve = (bench.Count <= 2 ? 0.03 : 0.01) * Tuning.BenchReserveScale;

        DoubleSwitchMove? best = null;
        double bestGain = Tuning.DoubleSwitchGain;
        foreach (int id in lineup)
        {
            var position = positions[id];
            if (position is Position.P or Position.DH) continue;
            int fielderDue = Due(lineup.IndexOf(id));
            if (fielderDue <= pitcherDue) continue;   // the new pitcher must bat after the one he replaces would have
            var fielder = roster.First(p => p.Id == id);
            double farOff = fielderDue >= 5 ? 0.2 : 0.5;   // a spot that is far away rarely bats before the game ends
            foreach (var candidate in bench.Where(p => p.IsNaturalAt(position)))
            {
                double bat = 0.25 * (Ops(candidate) - Ops(pitcherBat)) * soon;
                double lost = 0.25 * Math.Max(0, Ops(fielder) - Ops(pitcherBat)) * farOff;
                double glove = (DefenseModel.PositionValue(position, candidate) - DefenseModel.PositionValue(position, fielder)) * 0.004;
                double gain = bat - lost + glove - reserve;
                if (gain <= bestGain) continue;
                bestGain = gain;
                best = new DoubleSwitchMove(id, candidate.Id,
                    $"Double switch: {candidate.Name} for {fielder.Name} at {LineupRules.Label(position)}, so the pitcher's spot " +
                    $"(due in {pitcherDue}) belongs to a hitter ({gain:+0.00;-0.00} runs).");
            }
        }
        return best;
    }

    private WarmingPitcher? BestReliever(GameState state, IEnumerable<WarmingPitcher> options, bool saveSpot) =>
        options.OrderBy(w => RelieverValue(state, w, saveSpot)).FirstOrDefault();

    private double RelieverValue(GameState state, WarmingPitcher w, bool saveSpot)
    {
        double value = PitchingValue(state, w.Player, FatigueModel.GetWarmupModifier(w));
        // WHY: A closer's save role matters, but it does not erase a large matchup mismatch. Outside a save,
        // preserving him has a real opportunity value, unless he is the last arm available.
        if (w.Player.Role == PitcherRole.Closer) value += saveSpot ? -0.08 : 0.20;
        return value;
    }

    private double PitchingValue(GameState state, Player pitcher, double fatigue) =>
        ManagerDecisionModel.Memo(state, "pitching" + Tuning.PlatoonWeight, pitcher.Id, fatigue,
            () => ComputePitchingValue(state, pitcher, fatigue));

    private double ComputePitchingValue(GameState state, Player pitcher, double fatigue)
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
            var pitches = new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup };
            double with = pitches.Min(p => ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, p, DefensiveAlignment.Standard));
            // WHY: a manager who prizes the platoon edge counts a good matchup for more than it is worth, and one who
            // ignores it for less; the engine's real effect is the part that is re-scaled.
            if (Tuning.PlatoonWeight != 1.0)
            {
                double without = pitches.Min(p => ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, p,
                    DefensiveAlignment.Standard, platoon: false));
                with += (Tuning.PlatoonWeight - 1.0) * (with - without);
            }
            total += with;
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
        // WHY: the pitcher (when he is in the batting order) never changes position, and the pitcher is not a fielder to shuffle.
        var order = state.LineupFor(visitor).Where(id => state.PositionsFor(visitor)[id] != Position.P).ToList();
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

    private bool ShouldIntentionalWalk(GameState state, Player batter, Player pitcher, double fatigue, out double gain)
    {
        gain = 0;
        if (state.RunnerOnFirst is not null || state.Outs >= 2 ||
            state.RunnerOnSecond is null && state.RunnerOnThird is null ||
            state.CurrentInning < Tuning.IntentionalWalkFromInning || Math.Abs(ScoreMargin(state)) > 2) return false;
        var lineup = state.LineupFor(state.IsTopOfInning);
        if (lineup.Count < 2) return false;
        var next = state.OffenseTeam.Roster.First(p => p.Id == lineup[(state.CurrentBatterIndex + 1) % lineup.Count]);
        double gap = Ops(batter) - Ops(next);
        if (gap < Tuning.IntentionalWalkOpsGap || Ops(batter) < 0.8) return false;

        // WHY: An intentional walk buys the next hitter at the cost of a base runner. It is valued as it plays out: the
        // situation it leaves (the batter on first, forced runners moved up) with the on-deck hitter at the plate, against
        // pitching to the batter. A star in front of a weak bat is worth walking; one in front of another star is not.
        double walk = ManagerDecisionModel.WalkThenNextHitter(state, next, pitcher, fatigue);
        double face = new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup }
            .Min(p => ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, p, DefensiveAlignment.Standard));
        gain = face - walk;
        return walk + Scale(Tuning.IntentionalWalkMargin, LeverageFactor(state)) < face;
    }

    // ── How the moment and the score change how readily he acts ─────────────

    // A multiplier on an edge he needs: below 1 when the moment matters more than usual (he acts on less), above 1 when it
    // matters less. Always 1 for a manager who does not weigh leverage.
    private double LeverageFactor(GameState state) =>
        LeverageScale(ManagerDecisionModel.Leverage(state), Tuning.LeverageSensitivity);

    internal static double LeverageScale(double leverage, double sensitivity) =>
        sensitivity == 0 ? 1.0 : Math.Pow(Math.Clamp(leverage, 0.25, 4.0), -sensitivity);

    // WHY: a margin is how far a call must beat the ordinary one. Scaling a positive margin down (or a negative one up in
    // size) makes the manager act more readily, which is what a factor below 1 means, in both directions.
    internal static double Scale(double margin, double factor) => margin >= 0 ? margin * factor : margin / factor;

    // The gambles (a steal, a hit and run, running hard): from the seventh inning, cheaper for a manager who is behind
    // and dearer for one who is ahead, if he has an appetite for variance (negative values are the reverse).
    private double GambleShift(GameState state)
    {
        if (Tuning.SituationalRisk == 0 || state.CurrentInning < 7) return 0;
        int margin = ScoreMargin(state);
        return margin > 0 ? Tuning.SituationalRisk * 0.04 : margin < 0 ? -Tuning.SituationalRisk * 0.04 : 0;
    }

    // ── Late defensive replacements ─────────────────────────────────────────

    /// <summary>A better glove for a lineup player, made when the side takes the field protecting a lead.</summary>
    public record DefensiveReplacement(int OutPlayerId, int InPlayerId, string Why);

    /// <summary>
    /// Should the side that is about to field replace a weak glove with a better one? Only from
    /// <see cref="ManagerTuning.DefensiveReplaceFromInning"/>, only with a lead of 1 to <see cref="ManagerTuning.DefensiveReplaceMaxLead"/>,
    /// and only if the better glove is worth more than the bat he gives up. The sub must be a natural at the position.
    /// </summary>
    public virtual DefensiveReplacement? ChooseDefensiveReplacement(GameState state, bool visitor)
    {
        if (state.CurrentInning < Tuning.DefensiveReplaceFromInning) return null;
        int lead = visitor
            ? state.VisitorTotalScore - state.HomeTotalScore
            : state.HomeTotalScore - state.VisitorTotalScore;
        if (lead < 1 || lead > Tuning.DefensiveReplaceMaxLead) return null;
        var bench = state.Bench(visitor).ToList();
        if (bench.Count == 0) return null;

        var roster = state.TeamFor(visitor).Roster;
        var positions = state.PositionsFor(visitor);
        // WHY: the substitute takes the lineup spot, so his bat is the price. In the ninth the spot will rarely come up
        // again (the home side does not bat if it leads), so the price is lower then.
        double batPrice = state.CurrentInning >= 9 ? 8 : 20;
        DefensiveReplacement? best = null;
        double bestGain = Tuning.DefensiveReplaceGain;
        foreach (int id in state.LineupFor(visitor))
        {
            var current = roster.First(p => p.Id == id);
            var position = positions[id];
            if (position is Position.DH or Position.P) continue;
            foreach (var candidate in bench.Where(p => p.IsNaturalAt(position)))
            {
                double gain = DefenseModel.PositionValue(position, candidate) - DefenseModel.PositionValue(position, current)
                    - Math.Max(0, Ops(current) - Ops(candidate)) * batPrice;
                if (gain <= bestGain) continue;
                bestGain = gain;
                best = new DefensiveReplacement(id, candidate.Id,
                    $"{candidate.Name} for {current.Name} at {LineupRules.Label(position)}: the better glove protects the lead " +
                    $"({gain:+0.0;-0.0} fielding points after the bat he gives up).");
            }
        }
        return best;
    }

    // The man due up after the batter, or null in a lineup of one.
    private static Player? OnDeckHitter(GameState state)
    {
        var lineup = state.LineupFor(state.IsTopOfInning);
        if (lineup.Count < 2) return null;
        int id = lineup[(state.CurrentBatterIndex + 1) % lineup.Count];
        return state.OffenseTeam.Roster.First(p => p.Id == id);
    }

    protected static bool IsBunter(Player p) =>
        p.BuntingAbility >= 4 || p.SacrificeAbility >= 4;

    protected static bool IsContactHitter(Player p) =>
        (p.Overall?.BattingAverage ?? 0) >= 0.270 && (p.Overall?.Strikeouts ?? 99) < 80;
}
