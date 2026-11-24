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
        // The runner went with the pitch, so say so in the log (a pitchout turns it into a steal, which has its own line).
        if (IsHitAndRun(state, offense, defense))
        {
            result.HitAndRun = true;
            if (result.Outcome is not (OutcomeType.StolenBase or OutcomeType.CaughtStealing))
                result.Description = "Hit and run: " + result.Description;
        }
        result.PitchesThrown = EstimatePitches(result.Outcome);
        // The bases and batter have not been updated yet, so this is the position before the play.
        result.RunnerMoves = RunnerMoveBuilder.Build(state, result);
        return result;
    }

    /// <summary>
    /// A hit and run is on when the call is made, there is a runner on first and second base is open (with a man on
    /// second the runner on first could not go without sending him too, so the call does nothing), and the defense is
    /// not pitching around the batter.
    /// </summary>
    public static bool IsHitAndRun(GameState state, OffensiveDecision offense, DefensiveDecision defense) =>
        offense.Strategy == OffensiveStrategy.HitAndRun
        && state.RunnerOnFirst is not null && state.RunnerOnSecond is null
        && !defense.IntentionalWalk;

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

        // A bunt is its own kind of play, so it does not use the at-bat table at all.
        if (offense.Strategy == OffensiveStrategy.SacrificeBunt)
            return ResolveBunt(state, defense, batter, pitcher);

        bool hitAndRun = IsHitAndRun(state, offense, defense);
        var pitcherStats = pitcher.GetPitchingStats(batter.BatHand);
        var batterStats  = batter.GetBattingStats(pitcher.ThrowHand);

        double fatigueMod = FatigueModel.GetModifier(state.CurrentPitcher);

        // A switch hitter bats from the side opposite the pitcher's throwing arm.
        var batsFrom = batter.BatsAgainst(pitcher.ThrowHand);

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

        if (hitAndRun)
        {
            // WHY: with the runner breaking, a pitchout is the one pitch that beats the call: the batter cannot reach
            // it, so it is a straight steal attempt against a catcher who was ready for it.
            if (defense.PitchType == PitchType.Pitchout)
                return ResolveStolenBase(state, defense, pitcher, pitcherStats);

            // The batter's job is to put the ball in play, so he fights off strikes, and with the second baseman
            // covering the bag a grounder has a better chance of finding the hole.
            weights = weights with { Strikeout = weights.Strikeout * 0.75, Single = weights.Single * 1.08 };
        }

        // Roll the outcome
        var outcome = Roll(weights);

        // A ball hit to a fielder for an out is sometimes booted instead.
        Player? errorBy = null;
        Position? errorAt = null;
        Position? fielderAt = null;
        if (outcome is OutcomeType.GroundOut or OutcomeType.FlyOut)
        {
            var spot = DefenseModel.PickFielder(outcome, _rng);
            fielderAt = spot;
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

        // WHY: a hit and run turns some fly balls into line drives, which is what catches a runner who has already gone.
        // It is decided after the error check so that a booted ball stays a booted ball.
        if (hitAndRun && outcome == OutcomeType.FlyOut && _rng.NextDouble() < 0.35)
            outcome = OutcomeType.LineOut;

        // Resolve base running
        var (newFirst, newSecond, newThird, runs, outs) = ResolveBaseRunning(
            outcome, state, batter, offense.Strategy, armPenalty, hitAndRun, pitcherStats);
        // WHY: a double play is not rolled. It is a ground out that BaseRunningEngine.GroundOut turned into two outs, and
        // is relabelled only now so the description, the box score and OutsFor all see DoublePlay.
        if (outcome == OutcomeType.GroundOut && outs == 2)
            outcome = OutcomeType.DoublePlay;

        string desc = BuildDescription(batter, pitcher, outcome, runs, errorBy, errorAt);

        // The runner who went with the pitch: where he ended up is part of the story.
        if (hitAndRun && state.RunnerOnFirst is { } goer)
        {
            if (outcome == OutcomeType.Strikeout)
                desc = outs == 2
                    ? $"{batter.Name} strikes out and {goer.Player.Name} is caught stealing."
                    : $"{batter.Name} strikes out and {goer.Player.Name} steals second.";
            else if (outcome == OutcomeType.LineOut && outs == 2)
                desc = $"{batter.Name} lines out and {goer.Player.Name} is doubled off first.";
        }

        return new PlayResult
        {
            Outcome      = outcome,
            Description  = desc,
            RunsScored   = runs,
            NewFirst     = newFirst,
            NewSecond    = newSecond,
            NewThird     = newThird,
            // WHY: Max, because a hit and run can add an out the outcome alone does not carry (a strikeout that is
            // also a runner caught stealing). For every other outcome `outs` is 0 or the same count OutsFor gives.
            OutsRecorded = Math.Max(OutsFor(outcome), outs),
            ErrorBy      = errorBy,
            ErrorPosition = errorAt,
            Fielder      = fielderAt,
        };
    }

    // ── Bunts ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The batter squares to bunt. With a runner on and fewer than two out he is giving himself up to move the runner
    /// over (a sacrifice); otherwise he is bunting for a hit. Either way the ball is popped up, booted, beaten out,
    /// turned into a fielder's choice, or the batter is thrown out (a sacrifice when it moved somebody).
    /// </summary>
    private PlayResult ResolveBunt(GameState state, DefensiveDecision defense, Player batter, Player pitcher)
    {
        var first = state.RunnerOnFirst;
        var second = state.RunnerOnSecond;
        var third = state.RunnerOnThird;
        bool sacrifice = BuntModel.IsSacrifice(state);

        int? targetFrom = sacrifice ? BuntModel.FieldersChoiceFrom(first is not null, second is not null, third is not null) : null;
        var target = targetFrom == 2 ? second : targetFrom == 1 ? first : null;
        var odds = BuntModel.Odds(batter, defense.Alignment, sacrifice, target?.Player);

        // Who the ball goes to, and whether he boots it.
        var spot = BuntModel.PickFielder(_rng, p => p == Position.P || state.DefenderAt(p) is not null);
        var fielder = spot == Position.P ? pitcher : state.DefenderAt(spot) ?? pitcher;
        double errorChance = BuntModel.ErrorChance(fielder, spot,
            state.DefenseTeam.EraErrorsPerGame, state.DefenseTeam.EraStrikeoutsPerGame);

        double roll = _rng.NextDouble();
        var batterRunner = new BaseRunner { Player = batter };
        string runsText(int r) => r > 0 ? $" {r} run{(r > 1 ? "s" : "")} score{(r == 1 ? "s" : "")}." : "";
        // A run scores on a bunt only when the inning is not over on it; a sacrifice never makes the third out, and a
        // thrown-out bunt with two out has nobody scoring.
        PlayResult Result(OutcomeType outcome, string description, BaseRunner? f, BaseRunner? s, BaseRunner? t, int runs, int outs,
                          Player? errorBy = null, Position? errorAt = null) => new()
        {
            Outcome = outcome, Description = description, RunsScored = runs, OutsRecorded = outs,
            NewFirst = f, NewSecond = s, NewThird = t,
            IsBunt = true, Fielder = spot, ErrorBy = errorBy, ErrorPosition = errorAt,
        };

        // 1. He boots it (or throws it away): the batter is on and every runner moves up a base.
        if ((roll -= errorChance) < 0)
        {
            var e = BaseRunningEngine.OnError(first, second, third, batter);
            return Result(OutcomeType.Error,
                $"{batter.Name} bunts and reaches on an error by {LineupRules.Label(spot)} {fielder.Name}.{runsText(e.runs)}",
                e.first, e.second, e.third, e.runs, 0, fielder, spot);
        }

        // 2. He pops it up and it is caught: no runner moves.
        if ((roll -= odds.PopUp) < 0)
            return Result(OutcomeType.PopOut, $"{batter.Name} pops up a bunt.", first, second, third, 0, 1);

        // 3. He beats it out: a bunt single, with every runner moving up a base.
        if ((roll -= odds.Single) < 0)
        {
            var h = BaseRunningEngine.OnError(first, second, third, batter);
            return Result(OutcomeType.Single, $"{batter.Name} bunts for a hit.{runsText(h.runs)}",
                h.first, h.second, h.third, h.runs, 0);
        }

        // 4. The defense takes the lead runner instead: the batter is safe at first.
        if (targetFrom is int fromBase && (roll -= odds.FieldersChoice) < 0)
        {
            // WHY: the runner is out at the next base; what happens to the others follows from who was forced.
            // Runner on first (second empty): out at second, a runner on third holds. Runner on second: out at third,
            // a runner on first moves to second, and with the bases loaded the runner on third is forced home.
            var newFirst = batterRunner;
            BaseRunner? newSecond = fromBase == 2 ? first : null;
            BaseRunner? newThird = fromBase == 1 ? third : null;
            int runs = fromBase == 2 && first is not null && third is not null ? 1 : 0;
            string where = fromBase == 1 ? "second" : "third";
            return Result(OutcomeType.FieldersChoice,
                $"{batter.Name} bunts into a fielder's choice; {target!.Player.Name} is out at {where}.{runsText(runs)}",
                newFirst, newSecond, newThird, runs, 1);
        }

        // 5. Down in front of the plate and the batter is thrown out at first.
        if (sacrifice)
        {
            // Every runner moves up a base; the one on third scores (a squeeze).
            int runs = third is not null ? 1 : 0;
            return Result(OutcomeType.SacrificeBunt,
                $"{batter.Name} lays down a sacrifice bunt.{runsText(runs)}", null, first, second, runs, 1);
        }
        return Result(OutcomeType.GroundOut, $"{batter.Name} bunts and is thrown out.", first, second, third, 0, 1);
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
                           int armPenalty, bool hitAndRun, PitcherStats pitcherStats)
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
                state.Outs, _rng, runnerGoing: hitAndRun);
            return (f, s, t, runs, outs);
        }

        if (outcome is OutcomeType.Strikeout or OutcomeType.FlyOut
                    or OutcomeType.LineOut or OutcomeType.PopOut)
        {
            // WHY: only a fly ball with fewer than two outs can score the runner from third (a tag-up). With two outs the
            // catch is the third out, the inning is over, and no run may count. Strikeouts, line outs and pop outs
            // never score him. Every outcome in this branch is exactly one out.
            int runs = 0;
            var third = state.RunnerOnThird;
            if (outcome == OutcomeType.FlyOut && third is not null && state.Outs < 2)
            {
                bool scores = SpeedCheck(third.Player, 65 - armPenalty);
                if (scores) { runs++; third = null; }
            }
            var first = state.RunnerOnFirst;
            var second = state.RunnerOnSecond;
            int outsMade = 1;

            // A hit and run: the runner broke with the pitch, so the batter's out is not the whole story.
            if (hitAndRun && first is not null)
            {
                if (outcome == OutcomeType.Strikeout)
                {
                    // Strike 'em out, throw 'em out: he is running on a pitch the batter missed.
                    var catcher = state.DefenderAt(Position.C) ?? new Player { ArmStrength = 3 };
                    var (safe, _) = BaseRunningEngine.StolenBase(first, catcher, pitcherStats, false, _rng);
                    second = safe ? first : second;
                    first = null;
                    if (!safe) outsMade = 2;
                }
                else if (outcome == OutcomeType.LineOut && _rng.NextDouble() < BuntModel.DoubledOffChance(first.Player))
                {
                    // The ball is caught on a line and he is already off the bag.
                    first = null;
                    outsMade = 2;
                }
            }
            return (first, second, third, runs, outsMade);
        }

        // WHY: a hit and run called with no runner on first, or with second occupied, is not on (see IsHitAndRun): the
        // base running then follows the plain approach rather than sending a runner who was never going.
        var effective = strategy == OffensiveStrategy.HitAndRun && !hitAndRun ? OffensiveStrategy.SwingAway : strategy;
        var a = BaseRunningEngine.Advance(
            outcome,
            state.RunnerOnFirst, state.RunnerOnSecond, state.RunnerOnThird,
            batter, effective, _rng, armPenalty);
        return (a.first, a.second, a.third, a.runs, 0);
    }

    // NOTE: the weights need not add up to 1: the roll is scaled by their total. FlyOut is what is left when no earlier
    // bucket matched, so it needs no weight test of its own. LineOut and PopOut are handled in ResolveBaseRunning but are
    // never rolled here: only the nine outcomes summed below can come out of this method.
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
            or OutcomeType.CaughtStealing
            or OutcomeType.SacrificeBunt or OutcomeType.FieldersChoice => 1,
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

    // NOTE: the same formula as BaseRunningEngine.SpeedCheck (this one uses the resolver's seeded generator, so a test
    // that seeds it gets repeatable tag-ups): keep the two in step.
    private bool SpeedCheck(Player p, int baseChance)
    {
        int chance = baseChance + (p.SpeedRating - 3) * 8;
        return _rng.Next(100) < chance;
    }
}
