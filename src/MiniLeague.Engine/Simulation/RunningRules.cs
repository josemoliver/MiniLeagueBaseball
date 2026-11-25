using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

/// <summary>
/// The numbers that decide base running and the hit and run, in one place. WHY: the resolver and the base-running engine
/// roll them, and the computer managers' model prices them; a number copied into each would drift apart without anyone
/// noticing, and the managers would then misjudge the play they are choosing. Change one here and both follow.
/// </summary>
public static class RunningRules
{
    // ── The hit and run ──────────────────────────────────────────────────────

    /// <summary>The batter's job is to put the ball in play, so he fights off strikes.</summary>
    public const double HitAndRunStrikeoutFactor = 0.75;

    /// <summary>With the second baseman covering the bag a grounder has a better chance of finding the hole.</summary>
    public const double HitAndRunSingleFactor = 1.08;

    /// <summary>The share of fly outs that become line drives on a hit and run: what catches a runner who has already gone.</summary>
    public const double HitAndRunLineOutShare = 0.35;

    /// <summary>The outcome weights of a hit and run: fewer strikeouts, a few more singles.</summary>
    public static OutcomeWeights ApplyHitAndRun(OutcomeWeights w) =>
        w with { Strikeout = w.Strikeout * HitAndRunStrikeoutFactor, Single = w.Single * HitAndRunSingleFactor };

    // ── Double plays ─────────────────────────────────────────────────────────

    /// <summary>
    /// The base chance a ground out with a runner on first and fewer than two out is a double play.
    /// WHY: 0.45 turned two on 9.3% of the chances (a runner on first, fewer than two out) in calibration; real baseball turned
    /// about 10.5% there, and the engine has no fielder's choice (every other ground out retires the batter), so the share of
    /// ground outs that are double plays has to carry it. 0.52 gives about 10.5%.
    /// </summary>
    public const double DoublePlayChance = 0.52;

    /// <summary>The same when the runner broke with the pitch: he is most of the way to second, so the force is rarely turned.</summary>
    public const double DoublePlayChanceRunnerGoing = 0.12;

    /// <summary>
    /// The double-play chance for this batter and pitcher: the base chance, moved by how fast the batter is (a slow runner is
    /// easier to double up) and by how many ground balls the pitcher gets. An average runner against an average pitcher is
    /// exactly the base chance, so a game with ratings no one has set is unchanged.
    /// </summary>
    public static double DoublePlayChanceFor(double batterSpeed, double groundBallRate, bool runnerGoing)
    {
        double chance = (runnerGoing ? DoublePlayChanceRunnerGoing : DoublePlayChance)
            + (3 - batterSpeed) * 0.05 + (groundBallRate - 0.45) * 0.5;
        return Math.Clamp(chance, 0.04, 0.70);
    }

    // ── Stealing more than second ────────────────────────────────────────────

    /// <summary>The share of steal attempts that succeeded when a team's league is not known (the long-run league figure).</summary>
    public const double DefaultLeagueStealRate = 0.67;

    /// <summary>
    /// How many attempts at the league's rate a runner's own record is steadied with. WHY: his record is the best evidence of how
    /// he runs, so the steadying is light. Ten attempts pulled a poor team's runners nearly 4 points above what they really did,
    /// which put every poor stealing team of the calibration (1968 Tigers, 1975 Red Sox) well above its real success rate.
    /// </summary>
    public const double StealRecordWeight = 5;

    /// <summary>
    /// Percent a step of speed rating moves a steal. WHY: a record already carries the runner's speed (the rating itself comes
    /// from his steals), so this only separates runners with a short record; at the earlier 3% it counted speed twice.
    /// </summary>
    public const double StealPercentPerSpeedStep = 0.015;

    /// <summary>A steal of third is harder than second: the catcher's throw is shorter and the pitcher is ready for it.</summary>
    public const double StealOfThirdFactor = 0.93;

    /// <summary>On a double steal the catcher throws to third, the lead runner, this often; otherwise to second.</summary>
    public const double DoubleStealThrowToThird = 0.65;

    /// <summary>What a steal call sends in this situation.</summary>
    public static StealPlan StealPlanFor(bool first, bool second, bool third) => (first, second, third) switch
    {
        (true, false, _) => StealPlan.Second,        // a runner on first goes to second, even with a runner on third
        (false, true, false) => StealPlan.Third,
        (true, true, false) => StealPlan.Double,
        _ => StealPlan.None,
    };

    public static StealPlan StealPlanFor(GameState s) =>
        StealPlanFor(s.RunnerOnFirst is not null, s.RunnerOnSecond is not null, s.RunnerOnThird is not null);

    // ── Extra bases on a hit: the base chance, in percent, before the runner's speed and the outfield arm ─

    public const int ScoreFromSecondOnSingle = 30;
    public const int ScoreFromSecondOnSingleSent = 55;
    public const int ThirdFromFirstOnSingleSent = 50;
    public const int ThirdFromFirstOnSingleHitAndRun = 65;
    public const int ScoreFromFirstOnDoubleSent = 60;
    public const int ScoreFromFirstOnDoubleHitAndRun = 72;
    public const int TagUpFromThird = 65;

    /// <summary>Each step of speed (1 to 5, with 3 average) is worth this many percentage points, either way.</summary>
    public const int PercentPerSpeedStep = 8;

    /// <summary>Percentage points off a runner's chance for the outfield arm (the arm rating runs 1 to 5, 3 average).</summary>
    public static int ArmPenalty(double outfieldArm) => (int)Math.Round((outfieldArm - 3) * 6);

    /// <summary>A runner's chance, as a probability, of making an extra base with this base chance (already net of the arm).</summary>
    public static double SpeedChance(Player runner, int baseChance) =>
        Math.Clamp((baseChance + (runner.SpeedRating - 3) * PercentPerSpeedStep) / 100.0, 0, 1);

    /// <summary>Rolls the same chance. WHY: it draws one integer so a seeded generator gives repeatable tag-ups.</summary>
    public static bool SpeedCheck(Player runner, Random rng, int baseChance) =>
        rng.Next(100) < baseChance + (runner.SpeedRating - 3) * PercentPerSpeedStep;
}
