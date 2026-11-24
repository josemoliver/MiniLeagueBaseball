using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

/// <summary>
/// The odds behind a bunt. A bunt is its own kind of play: the batter squares around and the ball dies in front of the
/// plate, so the usual at-bat table (hits, strikeouts, fly balls) does not apply. What the batter is trying to do depends
/// on the situation: with a runner on and fewer than two outs he gives himself up to move the runner (a sacrifice); with
/// the bases empty, or two out, he is bunting for a hit.
/// </summary>
public static class BuntModel
{
    /// <summary>Chances that the bunt ends a particular way. Whatever is left over is the plain result (see <see cref="Odds"/>).</summary>
    public readonly record struct BuntOdds(double PopUp, double Single, double FieldersChoice);

    /// <summary>True when the bunt is meant to advance a runner: somebody on and fewer than two out.</summary>
    public static bool IsSacrifice(GameState s) =>
        s.Outs < 2 && (s.RunnerOnFirst is not null || s.RunnerOnSecond is not null || s.RunnerOnThird is not null);

    /// <summary>
    /// Which runner the defense can take instead of the batter, as the base he is on (1 or 2), or null when there is no
    /// such play. The lead runner who can be forced or caught between bases is the target; a lone runner on third, or
    /// second and third with first empty, has nowhere to be thrown out that the bunt would not simply score or move him.
    /// </summary>
    public static int? FieldersChoiceFrom(bool first, bool second, bool third) =>
        second ? (!first && third ? null : 2) : first ? 1 : null;

    /// <summary>
    /// The chances of a bunt popping up, going for a hit, or turning into a fielder's choice. The batter's bunting and
    /// sacrifice skill (1 to 6, 3 average) and speed (1 to 5, 3 average) move them; Corners In is the defense built for
    /// bunts, so it takes away bunt hits and sacrifices and gets more lead runners. Infield In does a little of the same.
    /// </summary>
    public static BuntOdds Odds(Player batter, DefensiveAlignment alignment, bool sacrifice, Player? targetRunner)
    {
        // WHY: the two ratings are averaged because a sacrifice is about placing the ball, and a bunt for a hit about
        // both placing it and legging it out; every player currently carries the default 3 for both, so this is the
        // average bunter until team data gives them real ratings.
        double skill = (batter.BuntingAbility + (sacrifice ? batter.SacrificeAbility : batter.BuntingAbility)) / 2.0;
        double speed = batter.SpeedRating;

        double popUp = Math.Clamp(0.10 - 0.015 * (skill - 3), 0.04, 0.16);

        // A man giving himself up is not running hard; a man bunting for a hit surprises the defense and is gone.
        double hit = sacrifice
            ? 0.04 + 0.03 * (speed - 3)
            : 0.08 + 0.045 * (speed - 3) + 0.01 * (skill - 3);
        hit = Math.Clamp(hit, 0.01, 0.30);

        double choice = 0;
        if (sacrifice && targetRunner is not null)
        {
            choice = 0.14 - 0.02 * (skill - 3);
            choice *= 1 - 0.08 * (targetRunner.SpeedRating - 3);   // a fast runner is harder to throw out
            choice = Math.Clamp(choice, 0.03, 0.30);
        }

        switch (alignment)
        {
            case DefensiveAlignment.CornersIn: hit *= 0.55; choice *= 1.6; break;
            case DefensiveAlignment.InfieldIn: hit *= 0.80; choice *= 1.2; break;
        }
        return new BuntOdds(popUp, hit, choice);
    }

    /// <summary>
    /// Who fields a bunt: the pitcher most often, then the corner men, then the catcher. Only spots that are manned
    /// can field it (the pitcher always is).
    /// </summary>
    public static Position PickFielder(Random rng, Func<Position, bool> manned)
    {
        (Position Pos, double Share)[] table =
            [(Position.P, 0.32), (Position.Third, 0.25), (Position.First, 0.25), (Position.C, 0.18)];
        var present = table.Where(t => manned(t.Pos)).ToList();
        double roll = rng.NextDouble() * present.Sum(t => t.Share);
        foreach (var (pos, share) in present)
            if ((roll -= share) < 0) return pos;
        return present[^1].Pos;
    }

    /// <summary>The chance the fielder boots a bunt: a little more than a routine grounder, since he is off balance.</summary>
    public static double ErrorChance(Player fielder, Position pos, double eraErrorsPerGame, double eraStrikeoutsPerGame) =>
        Math.Clamp(DefenseModel.ErrorChance(fielder, pos, eraErrorsPerGame, eraStrikeoutsPerGame) * 1.4, 0, 0.2);

    /// <summary>
    /// A hit and run: the chance a runner who broke with the pitch is doubled off when the batter lines out. A fast runner
    /// gets back to the bag more often.
    /// </summary>
    public static double DoubledOffChance(Player runner) =>
        Math.Clamp(0.55 - 0.08 * (runner.SpeedRating - 3), 0.25, 0.75);
}
