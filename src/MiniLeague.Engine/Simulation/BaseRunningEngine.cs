using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

public static class BaseRunningEngine
{
    /// <summary>
    /// Given a hit type, current runners, and offensive strategy,
    /// returns updated base state and runs scored.
    /// </summary>
    public static (BaseRunner? first, BaseRunner? second, BaseRunner? third, int runs)
        Advance(OutcomeType hit, BaseRunner? first, BaseRunner? second, BaseRunner? third,
                Player batter, OffensiveStrategy strategy, Random rng, int armPenalty = 0)
    {
        int runs = 0;
        bool aggressive = strategy == OffensiveStrategy.SwingAgressive;
        // WHY: on a hit and run the runner on first is already going when the ball is hit, so he takes the extra base more
        // often than a runner who waits to see the ball fall in, though not as often as a send he can read (aggressive
        // is the same call made with the ball in front of him, so it is a touch better).
        bool running = strategy == OffensiveStrategy.HitAndRun;

        BaseRunner batterRunner = new() { Player = batter };

        switch (hit)
        {
            case OutcomeType.HomeRun:
                runs += (third is not null ? 1 : 0)
                      + (second is not null ? 1 : 0)
                      + (first is not null ? 1 : 0)
                      + 1; // batter
                return (null, null, null, runs);

            case OutcomeType.Triple:
                runs += (third is not null ? 1 : 0)
                      + (second is not null ? 1 : 0)
                      + (first is not null ? 1 : 0);
                return (null, null, batterRunner, runs);

            case OutcomeType.Double:
                if (third is not null) runs++;
                if (second is not null) runs++;
                if (first is not null)
                {
                    // WHY: the runner on first goes to THIRD on a double by default; only an aggressive send that
                    // also wins the speed check scores him (so this check decides "home", not "third").
                    // NOTE: written as a ternary so a call that is neither aggressive nor a hit and run rolls nothing,
                    // exactly as before (tests that seed the generator depend on the sequence of rolls).
                    bool scoredFromFirst = aggressive ? SpeedCheck(first.Player, rng, 60 - armPenalty)
                                         : running && SpeedCheck(first.Player, rng, 72 - armPenalty);
                    if (scoredFromFirst) runs++;
                    else return (null, batterRunner, first, runs);
                }
                return (null, batterRunner, null, runs);

            case OutcomeType.Single:
                if (third is not null) runs++;
                BaseRunner? newThird = null;
                BaseRunner? newSecond = null;

                if (second is not null)
                {
                    bool scored = aggressive
                        ? SpeedCheck(second.Player, rng, 55 - armPenalty)
                        : SpeedCheck(second.Player, rng, 30 - armPenalty);
                    if (scored) runs++;
                    else newThird = second;
                }
                if (first is not null)
                {
                    // WHY: newThird is null: the runner from first can only take third if the runner from second did
                    // not stop there; two runners cannot share a base. (Order matters: second is resolved before first.)
                    bool tookExtra = newThird is null
                        && (aggressive ? SpeedCheck(first.Player, rng, 50 - armPenalty)
                                       : running && SpeedCheck(first.Player, rng, 65 - armPenalty));
                    if (tookExtra) newThird = first;
                    else newSecond = first;
                }
                return (batterRunner, newSecond, newThird, runs);

            case OutcomeType.Walk:
            case OutcomeType.HitByPitch:
            case OutcomeType.IntentionalWalk:
                // WHY: only forced runners move. First goes to second; second goes to third only if first was also
                // taken; third scores only with the bases loaded (that run is the walk's RBI). No randomness here:
                // AtBatResolver.BuildWalkResult relies on this when it passes Random.Shared for an intentional walk.
                if (first is not null && second is not null && third is not null) runs++;
                var f3 = (first is not null && second is not null) ? second : third;
                var s3 = first is not null ? first : second;
                return (batterRunner, s3, f3, runs);

            default:
                return (first, second, third, 0);
        }
    }

    /// <summary>
    /// The batter reaches on a fielding error: everyone moves up a base. Nobody is credited with an RBI.
    /// </summary>
    public static (BaseRunner? first, BaseRunner? second, BaseRunner? third, int runs)
        OnError(BaseRunner? first, BaseRunner? second, BaseRunner? third, Player batter)
    {
        int runs = third is not null ? 1 : 0;
        return (new BaseRunner { Player = batter }, first, second, runs);
    }

    /// <summary>
    /// Resolves a ground out with runners on base.
    /// Returns updated state + runs.
    /// </summary>
    public static (BaseRunner? first, BaseRunner? second, BaseRunner? third, int runs, int outs)
        GroundOut(BaseRunner? first, BaseRunner? second, BaseRunner? third,
                  DefensiveAlignment alignment, int currentOuts, Random rng, bool runnerGoing = false)
    {
        int runs = 0;
        int outs = 1;

        // Double play opportunity with runner on first and < 2 outs
        if (first is not null && currentOuts < 2)
        {
            // WHY: a runner who broke with the pitch (a hit and run) is most of the way to second by the time the
            // ball is fielded, so the force there is rarely turned. It is not zero: a sharp grounder at the middle
            // infield can still get two.
            double doublePlayChance = runnerGoing ? 0.12 : 0.45;
            bool dp = alignment != DefensiveAlignment.InfieldIn && rng.NextDouble() < doublePlayChance;
            if (dp)
            {
                outs = 2;
                // The run counts only if the inning is not over on the double play
                if (third is not null && currentOuts + outs < 3) runs++;
                return (null, null, second, runs, outs);  // batter and the runner on first are out; the others move up
            }
        }

        // Standard ground out — batter out, runners advance one base (no run on the third out)
        if (third is not null && currentOuts + outs < 3) runs++;
        return (null, first, second, runs, outs);  // batter is out; each runner moves up one base
    }

    /// <summary>
    /// Resolves a stolen base attempt.
    /// </summary>
    public static (bool success, string description)
        StolenBase(BaseRunner runner, Player catcher, PitcherStats pitcher, bool pitchout, Random rng)
    {
        int speed = runner.Player.SpeedRating;
        int catcherArm = catcher.ArmAt(Position.C);
        int sbRate = runner.Player.Overall?.StolenBases ?? 0;
        int csRate = runner.Player.Overall?.CaughtStealing ?? 0;
        int attempts = sbRate + csRate;
        double successRate = attempts > 0 ? (double)sbRate / attempts : 0.65;

        // Adjust for pitcher's hold rating and catcher arm
        successRate *= (6 - pitcher.HoldRunners) / 5.0;
        successRate *= (6 - catcherArm) / 5.0;
        successRate *= speed / 3.0;

        // Pitchout dramatically reduces success
        if (pitchout) successRate *= 0.15;

        successRate = Math.Clamp(successRate, 0.05, 0.95);

        bool success = rng.NextDouble() < successRate;
        string desc = success
            ? $"{runner.Player.Name} steals {(pitchout ? "(despite pitchout!)" : "")}"
            : $"{runner.Player.Name} caught stealing";

        return (success, desc);
    }

    // NOTE: the same formula as AtBatResolver.SpeedCheck (that one uses the resolver's own seeded generator): keep the
    // two in step. SpeedRating runs 1-5 with 3 average, so every step is worth 8 percentage points either way.
    private static bool SpeedCheck(Player p, Random rng, int baseChance)
    {
        int chance = baseChance + (p.SpeedRating - 3) * 8;
        return rng.Next(100) < chance;
    }
}
