using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

/// <summary>
/// How the fielders affect a game: how much ground the defense covers, who makes an error on a
/// batted ball and how often, and how well the outfield holds runners.
/// </summary>
public static class DefenseModel
{
    /// <summary>
    /// Chances a team's fielders get in a game: the 27 outs, less strikeouts, plus the errors themselves
    /// (a ball that is booted is a batted ball that should have been an out, so it is one more roll).
    /// </summary>
    public static double BattedBallChances(double eraErrorsPerGame, double eraStrikeoutsPerGame) =>
        Math.Max(27 - eraStrikeoutsPerGame + eraErrorsPerGame, 12);

    private static readonly Position[] Infield = [Position.First, Position.Second, Position.Third, Position.Short];
    private static readonly Position[] Outfield = [Position.Left, Position.Center, Position.Right];

    // Who fields a batted-ball out (shares add to 1).
    private static readonly (Position Pos, double Share)[] GroundBalls =
    [
        (Position.Short, 0.30), (Position.Second, 0.21), (Position.Third, 0.20),
        (Position.First, 0.17), (Position.P, 0.08), (Position.C, 0.04),
    ];

    private static readonly (Position Pos, double Share)[] FlyBalls =
    [
        (Position.Center, 0.30), (Position.Left, 0.24), (Position.Right, 0.24),
        (Position.Third, 0.06), (Position.Short, 0.06), (Position.Second, 0.04), (Position.First, 0.04),
        (Position.C, 0.02),
    ];

    /// <summary>The share of fly outs caught by an outfielder: the only ones deep enough to tag up and score on.</summary>
    public static double OutfieldFlyShare { get; } = FlyBalls
        .Where(f => f.Pos is Position.Left or Position.Center or Position.Right).Sum(f => f.Share);

    // Errors per chance relative to the average fielder at that spot (infielders, who throw, err more).
    private static double PositionPropensity(Position pos) => pos switch
    {
        Position.Short => 1.5, Position.Third => 1.5, Position.Second => 1.1, Position.First => 0.5,
        Position.P => 0.8, Position.C => 0.8,
        Position.Left => 0.7, Position.Center => 0.5, Position.Right => 0.6,
        _ => 1.0,
    };

    // Multiplier on the error chance for an ErrorRating of 0..3.
    private static double ErrorMultiplier(int errorRating) => errorRating switch
    {
        >= 3 => 0.45, 2 => 0.9, 1 => 1.45, _ => 2.1,
    };

    // Scales the table so a league-average defense errs at exactly the era's rate.
    private static readonly double Normaliser = Mean(GroundBalls) * 0.6 + Mean(FlyBalls) * 0.4;

    private static double Mean((Position Pos, double Share)[] table) =>
        table.Sum(t => t.Share * PositionPropensity(t.Pos)) * 0.9;  // 0.9 = ErrorMultiplier(2), the average fielder

    /// <summary>The spot a ball in play for an out goes to.</summary>
    public static Position PickFielder(OutcomeType outcome, Random rng)
    {
        var table = outcome == OutcomeType.GroundOut ? GroundBalls : FlyBalls;
        double roll = rng.NextDouble();
        double cum = 0;
        foreach (var (pos, share) in table)
            if ((cum += share) > roll) return pos;
        return table[^1].Pos;
    }

    /// <summary>Probability that a batted ball the fielder could have turned into an out is an error.</summary>
    public static double ErrorChance(Player fielder, Position pos, double eraErrorsPerGame, double eraStrikeoutsPerGame = 4.0)
    {
        double baseRate = eraErrorsPerGame / BattedBallChances(eraErrorsPerGame, eraStrikeoutsPerGame);
        double chance = baseRate
            * PositionPropensity(pos)
            * ErrorMultiplier(fielder.FieldingAt(pos).ErrorRating)
            / Normaliser;
        return Math.Clamp(chance, 0, 0.35);
    }

    /// <summary>Average range of the fielding side's infield and outfield, and the outfield's throwing arm.</summary>
    public static DefenseRatings RatingsFor(GameState state)
    {
        double Avg(Position[] spots, Func<FieldingProfile, double> pick)
        {
            var values = spots
                .Select(p => state.DefenderAt(p) is { } who ? pick(who.FieldingAt(p)) : (double?)null)
                .Where(v => v is not null)
                .Select(v => v!.Value)
                .ToList();
            return values.Count == 0 ? double.NaN : values.Average();
        }

        double infield = Avg(Infield, p => p.Range);
        double outfield = Avg(Outfield, p => p.Range);
        double arm = Avg(Outfield, p => p.Arm);
        return new DefenseRatings(
            double.IsNaN(infield) ? 5 : infield,
            double.IsNaN(outfield) ? 5 : outfield,
            double.IsNaN(arm) ? 3 : arm);
    }

    /// <summary>How the fielding side values a lineup arrangement (used to decide who plays where).</summary>
    public static double DefensiveValue(IEnumerable<(Player Player, Position Position)> lineup) =>
        lineup.Where(x => x.Position != Position.DH)
              .Sum(x => PositionWeight(x.Position) * x.Player.FieldingAt(x.Position).Value);

    /// <summary>What a player's glove is worth at one position: his fielding value weighted by how much the position matters.</summary>
    public static double PositionValue(Position position, Player player) =>
        position is Position.DH or Position.P ? 0 : PositionWeight(position) * player.FieldingAt(position).Value;

    private static double PositionWeight(Position pos) => pos switch
    {
        Position.Short or Position.Center or Position.C => 1.3,
        Position.Second or Position.Third => 1.1,
        _ => 0.8,
    };
}
