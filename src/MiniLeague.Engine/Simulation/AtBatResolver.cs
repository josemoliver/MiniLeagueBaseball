using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

public class AtBatResolver
{
    private readonly Random _rng;

    public AtBatResolver(Random? rng = null)
    {
        _rng = rng ?? Random.Shared;
    }

    public PlayResult Resolve(
        GameState state,
        OffensiveDecision offense,
        DefensiveDecision defense)
    {
        var result = ResolveCore(state, offense, defense);
        result.PitchesThrown = EstimatePitches(result.Outcome);
        return result;
    }

    // Average ≈ 3.9 pitches per plate appearance, with strikeouts and walks running long.
    private int EstimatePitches(OutcomeType outcome) => outcome switch
    {
        OutcomeType.Strikeout => _rng.Next(4, 8),
        OutcomeType.Walk => _rng.Next(5, 8),
        OutcomeType.IntentionalWalk => 4,
        OutcomeType.HitByPitch => _rng.Next(2, 6),
        OutcomeType.StolenBase or OutcomeType.CaughtStealing => 1,
        _ => _rng.Next(1, 7),
    };

    private PlayResult ResolveCore(
        GameState state,
        OffensiveDecision offense,
        DefensiveDecision defense)
    {
        // Intentional walk short-circuits everything
        if (defense.IntentionalWalk)
            return BuildWalkResult(state, OutcomeType.IntentionalWalk, "Intentional walk issued.");

        var batter = state.CurrentBatter;
        var pitcher = state.CurrentPitcher.Player;
        var pitcherStats = pitcher.GetPitchingStats(batter.BatHand);
        var batterStats  = batter.GetBattingStats(pitcher.ThrowHand);

        double fatigueMod = FatigueModel.GetModifier(state.CurrentPitcher);

        // A switch hitter bats from the side opposite the pitcher's throwing arm.
        var batsFrom = batter.BatHand != BatHand.Switch ? batter.BatHand
            : pitcher.ThrowHand == ThrowHand.Left ? BatHand.Right : BatHand.Left;

        var fielders = DefenseModel.RatingsFor(state);
        int armPenalty = (int)Math.Round((fielders.OutfieldArm - 3) * 6);

        // Build probability table
        var weights = ProbabilityTable.Build(
            batterStats,
            pitcherStats,
            state.Stadium,
            batsFrom,
            defense.PitchType,
            defense.Alignment,
            fatigueMod,
            fielders);

        // Stolen base attempt pre-empts the at-bat
        if (offense.Strategy == OffensiveStrategy.Steal && state.RunnerOnFirst is not null)
        {
            return ResolveStolenBase(state, defense, pitcher, pitcherStats);
        }

        // Roll the outcome
        var outcome = Roll(weights);

        // A ball hit to a fielder for an out is sometimes booted instead.
        Player? errorBy = null;
        Position? errorAt = null;
        if (outcome is OutcomeType.GroundOut or OutcomeType.FlyOut)
        {
            var spot = DefenseModel.PickFielder(outcome, _rng);
            var fielder = spot == Position.P ? pitcher : state.DefenderAt(spot);
            if (fielder is not null
                && _rng.NextDouble() < DefenseModel.ErrorChance(fielder, spot,
                    state.DefenseTeam.EraErrorsPerGame, state.DefenseTeam.EraStrikeoutsPerGame))
            {
                outcome = OutcomeType.Error;
                errorBy = fielder;
                errorAt = spot;
            }
        }

        // Resolve base running
        var (newFirst, newSecond, newThird, runs, outs) = ResolveBaseRunning(
            outcome, state, batter, offense.Strategy, armPenalty);
        if (outcome == OutcomeType.GroundOut && outs == 2)
            outcome = OutcomeType.DoublePlay;

        string desc = BuildDescription(batter, pitcher, outcome, runs, errorBy, errorAt);

        return new PlayResult
        {
            Outcome      = outcome,
            Description  = desc,
            RunsScored   = runs,
            NewFirst     = newFirst,
            NewSecond    = newSecond,
            NewThird     = newThird,
            OutsRecorded = OutsFor(outcome),
            ErrorBy      = errorBy,
            ErrorPosition = errorAt,
        };
    }

    private PlayResult ResolveStolenBase(
        GameState state, DefensiveDecision defense,
        Player pitcher, PitcherStats pitcherStats)
    {
        var runner = state.RunnerOnFirst!;
        // Find the catcher in the defense team
        var catcher = state.DefenderAt(Position.C)
            ?? new Player { ArmStrength = 3 };

        var (success, desc) = BaseRunningEngine.StolenBase(
            runner, catcher, pitcherStats, defense.PitchType == PitchType.Pitchout, _rng);

        if (success)
        {
            return new PlayResult
            {
                Outcome      = OutcomeType.StolenBase,
                Description  = desc,
                RunsScored   = 0,
                NewFirst     = null,
                NewSecond    = runner,
                NewThird     = state.RunnerOnThird,
                OutsRecorded = 0
            };
        }
        else
        {
            return new PlayResult
            {
                Outcome      = OutcomeType.CaughtStealing,
                Description  = desc,
                RunsScored   = 0,
                NewFirst     = null,
                NewSecond    = state.RunnerOnSecond,
                NewThird     = state.RunnerOnThird,
                OutsRecorded = 1
            };
        }
    }

    private (BaseRunner? first, BaseRunner? second, BaseRunner? third, int runs, int outs)
        ResolveBaseRunning(OutcomeType outcome, GameState state, Player batter, OffensiveStrategy strategy,
                           int armPenalty)
    {
        if (outcome == OutcomeType.Error)
        {
            var e = BaseRunningEngine.OnError(state.RunnerOnFirst, state.RunnerOnSecond, state.RunnerOnThird, batter);
            return (e.first, e.second, e.third, e.runs, 0);
        }

        if (outcome is OutcomeType.GroundOut)
        {
            var (f, s, t, runs, outs) = BaseRunningEngine.GroundOut(
                state.RunnerOnFirst, state.RunnerOnSecond, state.RunnerOnThird,
                state.IsTopOfInning ? state.HomeAlignment : state.VisitorAlignment,
                state.Outs, _rng);
            return (f, s, t, runs, outs);
        }

        if (outcome is OutcomeType.Strikeout or OutcomeType.FlyOut
                    or OutcomeType.LineOut or OutcomeType.PopOut)
        {
            // Fly out — tag-up scoring
            int runs = 0;
            var third = state.RunnerOnThird;
            if (outcome == OutcomeType.FlyOut && third is not null && state.Outs < 2)
            {
                bool scores = SpeedCheck(third.Player, 65 - armPenalty);
                if (scores) { runs++; third = null; }
            }
            return (state.RunnerOnFirst, state.RunnerOnSecond, third, runs, 1);
        }

        var a = BaseRunningEngine.Advance(
            outcome,
            state.RunnerOnFirst, state.RunnerOnSecond, state.RunnerOnThird,
            batter, strategy, _rng, armPenalty);
        return (a.first, a.second, a.third, a.runs, 0);
    }

    private OutcomeType Roll(OutcomeWeights w)
    {
        double total = w.HomeRun + w.Triple + w.Double + w.Single
                     + w.Walk + w.HitByPitch + w.Strikeout
                     + w.GroundOut + w.FlyOut;

        double roll = _rng.NextDouble() * total;
        double cum = 0;

        if ((cum += w.HomeRun) > roll)    return OutcomeType.HomeRun;
        if ((cum += w.Triple) > roll)     return OutcomeType.Triple;
        if ((cum += w.Double) > roll)     return OutcomeType.Double;
        if ((cum += w.Single) > roll)     return OutcomeType.Single;
        if ((cum += w.Walk) > roll)       return OutcomeType.Walk;
        if ((cum += w.HitByPitch) > roll) return OutcomeType.HitByPitch;
        if ((cum += w.Strikeout) > roll)  return OutcomeType.Strikeout;
        if ((cum += w.GroundOut) > roll)  return OutcomeType.GroundOut;
        return OutcomeType.FlyOut;
    }

    private static PlayResult BuildWalkResult(GameState state, OutcomeType type, string desc)
    {
        var batter = state.CurrentBatter;
        var batterRunner = new BaseRunner { Player = batter };
        var (f, s, t, runs) = BaseRunningEngine.Advance(
            type,
            state.RunnerOnFirst, state.RunnerOnSecond, state.RunnerOnThird,
            batter, OffensiveStrategy.SwingAway, Random.Shared);

        return new PlayResult
        {
            Outcome = type, Description = desc,
            RunsScored = runs, OutsRecorded = 0,
            NewFirst = f, NewSecond = s, NewThird = t
        };
    }

    private static int OutsFor(OutcomeType o) => o switch
    {
        OutcomeType.Strikeout or OutcomeType.FlyOut or OutcomeType.LineOut
            or OutcomeType.PopOut or OutcomeType.GroundOut
            or OutcomeType.CaughtStealing => 1,
        OutcomeType.DoublePlay => 2,
        _ => 0
    };

    private static string BuildDescription(Player batter, Player pitcher, OutcomeType outcome, int runs,
                                           Player? errorBy = null, Position? errorAt = null)
    {
        string runsDesc = runs > 0 ? $" {runs} run{(runs > 1 ? "s" : "")} score{(runs == 1 ? "s" : "")}." : "";
        return outcome switch
        {
            OutcomeType.HomeRun  => $"{batter.Name} homers!{runsDesc}",
            OutcomeType.Triple   => $"{batter.Name} triples.{runsDesc}",
            OutcomeType.Double   => $"{batter.Name} doubles.{runsDesc}",
            OutcomeType.Single   => $"{batter.Name} singles.{runsDesc}",
            OutcomeType.Walk     => $"{batter.Name} walks.",
            OutcomeType.HitByPitch => $"{batter.Name} hit by pitch.",
            OutcomeType.Strikeout  => $"{batter.Name} strikes out.",
            OutcomeType.GroundOut  => $"{batter.Name} grounds out.{runsDesc}",
            OutcomeType.FlyOut     => $"{batter.Name} flies out.{runsDesc}",
            OutcomeType.LineOut    => $"{batter.Name} lines out.",
            OutcomeType.PopOut     => $"{batter.Name} pops out.",
            OutcomeType.DoublePlay => $"{batter.Name} grounds into a double play.{runsDesc}",
            OutcomeType.Error      => $"{batter.Name} reaches on an error by {LineupRules.Label(errorAt ?? Position.DH)} {errorBy?.Name}.{runsDesc}",
            _ => $"{batter.Name} — {outcome}."
        };
    }

    private bool SpeedCheck(Player p, int baseChance)
    {
        int chance = baseChance + (p.SpeedRating - 3) * 8;
        return _rng.Next(100) < chance;
    }
}
