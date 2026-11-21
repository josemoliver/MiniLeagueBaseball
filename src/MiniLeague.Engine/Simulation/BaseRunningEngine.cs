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
                    // First to third on a double — aggressive or fast runners
                    bool scoredFromFirst = aggressive && SpeedCheck(first.Player, rng, 60 - armPenalty);
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
                    bool tookExtra = aggressive && newThird is null && SpeedCheck(first.Player, rng, 50 - armPenalty);
                    if (tookExtra) newThird = first;
                    else newSecond = first;
                }
                return (batterRunner, newSecond, newThird, runs);

            case OutcomeType.Walk:
            case OutcomeType.HitByPitch:
            case OutcomeType.IntentionalWalk:
                // Force runners
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
                  DefensiveAlignment alignment, int currentOuts, Random rng)
    {
        int runs = 0;
        int outs = 1;

        // Double play opportunity with runner on first and < 2 outs
        if (first is not null && currentOuts < 2)
        {
            bool dp = alignment != DefensiveAlignment.InfieldIn && rng.NextDouble() < 0.45;
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

    private static bool SpeedCheck(Player p, Random rng, int baseChance)
    {
        int chance = baseChance + (p.SpeedRating - 3) * 8;
        return rng.Next(100) < chance;
    }
}
