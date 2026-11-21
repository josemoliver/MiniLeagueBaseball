namespace MiniLeague.Engine.Models;

/// <summary>
/// How well a player handles one position.
/// Range 1–9 (5 = league average), ErrorRating 0–3 (3 = rarely errs, 2 = average), Arm 1–5 (3 = average).
/// </summary>
public record FieldingProfile(int Range, int ErrorRating, int Arm)
{
    public static FieldingProfile Average => new(5, 2, 3);

    /// <summary>A player stuck at a position he does not normally play.</summary>
    public static FieldingProfile OutOfPosition(Position pos) => pos switch
    {
        Position.C      => new(2, 0, 2),
        Position.Short  => new(2, 0, 3),
        Position.Second => new(3, 1, 3),
        Position.Third  => new(3, 1, 3),
        Position.Center => new(3, 1, 2),
        Position.First  => new(4, 1, 3),   // the easiest spot to hide a bat
        Position.Left   => new(4, 1, 3),
        Position.Right  => new(4, 1, 3),
        _               => Average,
    };

    /// <summary>One number for comparing players at a position (higher is better).</summary>
    public double Value => Range + ErrorRating * 2.0 + Arm * 0.5;
}

/// <summary>How far (in the engine's terms) a team's fielders are from average, for the batted-ball model.</summary>
public record DefenseRatings(double InfieldRange, double OutfieldRange, double OutfieldArm)
{
    public static DefenseRatings Average => new(5, 5, 3);
}
