namespace MiniLeague.Engine.Models;

public enum BatHand { Left, Right, Switch }
public enum ThrowHand { Left, Right }
public enum Position { P, C, First, Second, Third, Short, Left, Center, Right, DH }
public enum PitcherRole { Starter, Reliever, Closer }

public class Player
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public BatHand BatHand { get; set; }
    public ThrowHand ThrowHand { get; set; }
    public Position PrimaryPosition { get; set; }
    public Position? SecondaryPosition { get; set; }
    public PitcherRole? Role { get; set; }            // null for position players

    // Ratings (1–9 scale unless noted)
    public int SpeedRating { get; set; } = 3;         // 1–5
    public int PowerRating { get; set; } = 2;         // 1–4
    public int FieldingRating { get; set; } = 2;      // 0–3
    public int FieldingRange { get; set; } = 5;       // 1–9
    public int BuntingAbility { get; set; } = 3;      // 1–6
    public int SacrificeAbility { get; set; } = 3;    // 1–6
    public int BaseRunning { get; set; } = 3;         // 1–6
    public int ArmStrength { get; set; } = 3;         // 1–5 (outfield throwing)

    /// <summary>
    /// Fielding by position, for positions he has actually played. A player with no fielding data at all
    /// (<see cref="HasFieldingData"/> false) is treated as average everywhere; one with data is penalised
    /// at any position he has not played, so a pure bat has an empty map and a poor glove anywhere.
    /// </summary>
    public Dictionary<Position, FieldingProfile> Fielding { get; set; } = [];
    public bool HasFieldingData { get; set; }

    // Stats — overall and platoon splits
    public BatterStats? Overall { get; set; }
    public BatterStats? VsLeft { get; set; }
    public BatterStats? VsRight { get; set; }

    public PitcherStats? PitchingOverall { get; set; }
    public PitcherStats? PitchingVsLeft { get; set; }
    public PitcherStats? PitchingVsRight { get; set; }

    public bool IsPitcher => PrimaryPosition == Position.P;

    public FieldingProfile FieldingAt(Position pos)
    {
        if (pos is Position.DH or Position.P || !HasFieldingData) return FieldingProfile.Average;
        return Fielding.TryGetValue(pos, out var profile) ? profile : FieldingProfile.OutOfPosition(pos);
    }

    /// <summary>True when he has data showing he plays this position (or has no fielding data to judge by).</summary>
    public bool IsNaturalAt(Position pos) =>
        pos is Position.DH or Position.P || !HasFieldingData || Fielding.ContainsKey(pos);

    /// <summary>Throwing arm at a position (catchers: stopping the running game; outfielders: holding runners).</summary>
    public int ArmAt(Position pos) => !HasFieldingData ? ArmStrength : FieldingAt(pos).Arm;

    /// <summary>The side he bats from against a pitcher: a switch hitter takes the side opposite the pitcher's arm.</summary>
    public BatHand BatsAgainst(ThrowHand pitcherHand) =>
        BatHand != BatHand.Switch ? BatHand
        : pitcherHand == ThrowHand.Left ? BatHand.Right : BatHand.Left;

    public BatterStats GetBattingStats(ThrowHand pitcherHand)
    {
        if (pitcherHand == ThrowHand.Left && VsLeft is not null) return VsLeft;
        if (pitcherHand == ThrowHand.Right && VsRight is not null) return VsRight;
        return Overall ?? new BatterStats();
    }

    public PitcherStats GetPitchingStats(BatHand batterHand)
    {
        if (batterHand == BatHand.Left && PitchingVsLeft is not null) return PitchingVsLeft;
        if (batterHand == BatHand.Right && PitchingVsRight is not null) return PitchingVsRight;
        return PitchingOverall ?? new PitcherStats();
    }
}
