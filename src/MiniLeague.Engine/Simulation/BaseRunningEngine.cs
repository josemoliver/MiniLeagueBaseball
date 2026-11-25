using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

/// <summary>A runner sent for an extra base on a hit and thrown out: who, where he started and where he was tagged (4 = home).</summary>
public readonly record struct ThrownOutRunner(BaseRunner Runner, int FromBase, int AtBase);

public static class BaseRunningEngine
{
    /// <summary>
    /// The chance that a runner sent hard (aggressive running) who does not make it is thrown out, rather than held
    /// up by the coach. WHY: an aggressive send must carry a risk, or it is always the right call.
    /// </summary>
    public const double SentRunnerThrownOut = 0.6;

    /// <summary>
    /// Given a hit type, current runners, and offensive strategy,
    /// returns updated base state and runs scored.
    /// </summary>
    public static (BaseRunner? first, BaseRunner? second, BaseRunner? third, int runs)
        Advance(OutcomeType hit, BaseRunner? first, BaseRunner? second, BaseRunner? third,
                Player batter, OffensiveStrategy strategy, Random rng, int armPenalty = 0) =>
        Advance(hit, first, second, third, batter, strategy, rng, out _, armPenalty);

    /// <summary>
    /// As <see cref="Advance(OutcomeType, BaseRunner?, BaseRunner?, BaseRunner?, Player, OffensiveStrategy, Random, int)"/>,
    /// also reporting a runner thrown out on an aggressive send (at most one per play; that out is not in the bases).
    /// </summary>
    public static (BaseRunner? first, BaseRunner? second, BaseRunner? third, int runs)
        Advance(OutcomeType hit, BaseRunner? first, BaseRunner? second, BaseRunner? third,
                Player batter, OffensiveStrategy strategy, Random rng, out ThrownOutRunner? thrownOut, int armPenalty = 0)
    {
        thrownOut = null;
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
                    bool scoredFromFirst = aggressive ? SpeedCheck(first.Player, rng, RunningRules.ScoreFromFirstOnDoubleSent - armPenalty)
                                         : running && SpeedCheck(first.Player, rng, RunningRules.ScoreFromFirstOnDoubleHitAndRun - armPenalty);
                    if (scoredFromFirst) runs++;
                    // NOTE: the extra roll happens only after a failed aggressive send, so no other play's rolls move.
                    else if (aggressive && rng.NextDouble() < SentRunnerThrownOut)
                        thrownOut = new ThrownOutRunner(first, 1, 4);
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
                        ? SpeedCheck(second.Player, rng, RunningRules.ScoreFromSecondOnSingleSent - armPenalty)
                        : SpeedCheck(second.Player, rng, RunningRules.ScoreFromSecondOnSingle - armPenalty);
                    if (scored) runs++;
                    else if (aggressive && rng.NextDouble() < SentRunnerThrownOut)
                        thrownOut = new ThrownOutRunner(second, 2, 4);
                    else newThird = second;
                }
                if (first is not null)
                {
                    // WHY: newThird is null: the runner from first can only take third if the runner from second did
                    // not stop there; two runners cannot share a base. (Order matters: second is resolved before first.)
                    // Nor does he try once the runner ahead was thrown out: the defense makes one play.
                    bool canTry = newThird is null && thrownOut is null;
                    bool tookExtra = canTry
                        && (aggressive ? SpeedCheck(first.Player, rng, RunningRules.ThirdFromFirstOnSingleSent - armPenalty)
                                       : running && SpeedCheck(first.Player, rng, RunningRules.ThirdFromFirstOnSingleHitAndRun - armPenalty));
                    if (tookExtra) newThird = first;
                    else if (canTry && aggressive && rng.NextDouble() < SentRunnerThrownOut)
                        thrownOut = new ThrownOutRunner(first, 1, 3);
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
                  DefensiveAlignment alignment, int currentOuts, Random rng, bool runnerGoing = false,
                  double batterSpeed = 3, double groundBallRate = 0.45)
    {
        int runs = 0;
        int outs = 1;

        // Double play opportunity with runner on first and < 2 outs
        if (first is not null && currentOuts < 2)
        {
            // WHY: a runner who broke with the pitch (a hit and run) is most of the way to second by the time the
            // ball is fielded, so the force there is rarely turned. It is not zero: a sharp grounder at the middle
            // infield can still get two.
            // A slow batter, or a pitcher who gets a lot of ground balls, makes the double play likelier (see RunningRules).
            double doublePlayChance = RunningRules.DoublePlayChanceFor(batterSpeed, groundBallRate, runnerGoing);
            bool dp = alignment != DefensiveAlignment.InfieldIn && rng.NextDouble() < doublePlayChance;
            if (dp)
            {
                outs = 2;
                // The run counts only if the inning is not over on the double play
                if (third is not null && currentOuts + outs < 3) runs++;
                return (null, null, second, runs, outs);  // batter and the runner on first are out; the others move up
            }
        }

        // WHY: the infield plays in to cut down the run at the plate. On a ground ball the runner on third usually has
        // to hold, so the batter is out and the run does not score; the cost of playing in is the singles that get
        // through (ProbabilityTable). A forced runner (bases loaded) cannot hold, so that case is unchanged.
        // NOTE: the roll happens only with the infield in and an unforced runner on third.
        if (third is not null && currentOuts + outs < 3 && alignment == DefensiveAlignment.InfieldIn
            && !(first is not null && second is not null)
            && rng.NextDouble() >= InfieldInScoreChance(third.Player))
            return (null, first ?? second, third, 0, outs);   // he holds; the runner on first (if any) moves up

        // Standard ground out — batter out, runners advance one base (no run on the third out)
        if (third is not null && currentOuts + outs < 3) runs++;
        return (null, first, second, runs, outs);  // batter is out; each runner moves up one base
    }

    /// <summary>The share of steal attempts that succeed across the game's eras, used to steady a short record.</summary>
    public const double LeagueStealRate = RunningRules.DefaultLeagueStealRate;

    /// <summary>
    /// The chance a steal of second succeeds. WHY: it starts from the runner's own record (which already carries his
    /// speed and the batteries he ran on), steadied toward the league rate when the record is short, then adjusts for
    /// this pitcher's hold and this catcher's arm around the average of 3. It once multiplied the record by 0.6 twice for
    /// an average battery, so an average runner made it about one time in four and stealing never paid.
    /// The computer managers price a steal with this same function.
    /// </summary>
    public static double StealSuccessChance(Player runner, int catcherArm, int holdRunners, bool pitchout)
    {
        int sb = runner.Overall?.StolenBases ?? 0, cs = runner.Overall?.CaughtStealing ?? 0;
        // WHY: steadied toward the rate of his own league and season (set when the team is loaded): 1927 runners were safe
        // 61% of the time and 1980 runners 65%, so one fixed rate was wrong for most teams.
        double record = (sb + runner.LeagueStealRate * RunningRules.StealRecordWeight) / (sb + cs + RunningRules.StealRecordWeight);
        double chance = record
            * (1 + (3 - holdRunners) * 0.07)
            * (1 + (3 - catcherArm) * 0.07)
            * (1 + (runner.SpeedRating - 3) * RunningRules.StealPercentPerSpeedStep);
        if (pitchout) chance *= 0.15;   // the catcher is up and throwing before the runner is halfway
        return Math.Clamp(chance, 0.05, 0.95);
    }

    /// <summary>The chance a runner on third scores on a ground out with the infield in (he usually has to hold).</summary>
    public static double InfieldInScoreChance(Player runner) =>
        Math.Clamp(0.25 + (runner.SpeedRating - 3) * 0.05, 0.10, 0.45);

    /// <summary>
    /// Resolves a stolen base attempt.
    /// </summary>
    public static (bool success, string description)
        StolenBase(BaseRunner runner, Player catcher, PitcherStats pitcher, bool pitchout, Random rng)
    {
        double successRate = StealSuccessChance(runner.Player, catcher.ArmAt(Position.C), pitcher.HoldRunners, pitchout);

        bool success = rng.NextDouble() < successRate;
        string desc = success
            ? $"{runner.Player.Name} steals {(pitchout ? "(despite pitchout!)" : "")}"
            : $"{runner.Player.Name} caught stealing";

        return (success, desc);
    }

    private static bool SpeedCheck(Player p, Random rng, int baseChance) => RunningRules.SpeedCheck(p, rng, baseChance);
}
