using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

/// <summary>
/// A spot on the ground in feet. Home plate is (0,0), X is toward right field and Y toward center field.
/// </summary>
public readonly record struct FieldPoint(double X, double Y)
{
    public double DistanceTo(FieldPoint other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));

    public double DistanceFromHome => Math.Sqrt(X * X + Y * Y);

    /// <summary>Degrees from straightaway center: negative toward left field, positive toward right field.</summary>
    public double AngleDegrees => Math.Atan2(X, Y) * 180 / Math.PI;

    public static FieldPoint FromPolar(double angleDegrees, double distance)
    {
        double rad = angleDegrees * Math.PI / 180;
        return new FieldPoint(distance * Math.Sin(rad), distance * Math.Cos(rad));
    }

    public FieldPoint Towards(FieldPoint target, double fraction) =>
        new(X + (target.X - X) * fraction, Y + (target.Y - Y) * fraction);
}

/// <summary>Where everything is on the field, in feet, and where the defense stands for each alignment.</summary>
public static class FieldGeometry
{
    public const double BaseSpacing = 90;
    public const double FoulLineAngle = 45;

    private static readonly double Leg = BaseSpacing / Math.Sqrt(2);

    public static readonly FieldPoint Home = new(0, 0);
    public static readonly FieldPoint First = new(Leg, Leg);
    public static readonly FieldPoint Second = new(0, 2 * Leg);
    public static readonly FieldPoint Third = new(-Leg, Leg);
    public static readonly FieldPoint Mound = new(0, 60.5);
    public static readonly FieldPoint CatcherSpot = new(0, -5);

    /// <summary>Home is 0 (and 4, once a runner has come all the way around), then first, second and third.</summary>
    public static FieldPoint BaseSpot(int number) => (number % 4) switch
    {
        1 => First,
        2 => Second,
        3 => Third,
        _ => Home,
    };

    /// <summary>Fair territory: in front of home plate and between the foul lines.</summary>
    public static bool IsFair(FieldPoint p) => p.Y >= 0 && Math.Abs(p.X) <= p.Y + 1e-6;

    // ── The wall ─────────────────────────────────────────────────────────────

    private static readonly double[] WallAngles = [-45, -22.5, 0, 22.5, 45];

    /// <summary>Distance to the wall at an angle, interpolated between the park's five posted distances.</summary>
    public static double WallDistance(Stadium park, double angleDegrees)
    {
        double[] feet = [park.LeftField, park.LeftCenter, park.Center, park.RightCenter, park.RightField];
        double a = Math.Clamp(angleDegrees, -FoulLineAngle, FoulLineAngle);
        for (int i = 0; i < WallAngles.Length - 1; i++)
        {
            if (a <= WallAngles[i + 1])
            {
                double f = (a - WallAngles[i]) / (WallAngles[i + 1] - WallAngles[i]);
                return feet[i] + (feet[i + 1] - feet[i]) * f;
            }
        }
        return feet[^1];
    }

    public static FieldPoint WallPoint(Stadium park, double angleDegrees) =>
        FieldPoint.FromPolar(Math.Clamp(angleDegrees, -FoulLineAngle, FoulLineAngle), WallDistance(park, angleDegrees));

    /// <summary>Wall height at an angle, running from the left-field height to the right-field height.</summary>
    public static double WallHeight(Stadium park, double angleDegrees)
    {
        double f = (Math.Clamp(angleDegrees, -FoulLineAngle, FoulLineAngle) + FoulLineAngle) / (2 * FoulLineAngle);
        return park.LeftWallHeight + (park.RightWallHeight - park.LeftWallHeight) * f;
    }

    // ── The defense ──────────────────────────────────────────────────────────

    /// <summary>The seven fielders besides the battery.</summary>
    public static readonly Position[] FieldingPositions =
        [Position.First, Position.Second, Position.Short, Position.Third, Position.Left, Position.Center, Position.Right];

    public static readonly Position[] Infielders = [Position.First, Position.Second, Position.Short, Position.Third];
    public static readonly Position[] Outfielders = [Position.Left, Position.Center, Position.Right];

    private static readonly IReadOnlyDictionary<Position, FieldPoint> Standard = new Dictionary<Position, FieldPoint>
    {
        [Position.P] = Mound,
        [Position.C] = CatcherSpot,
        [Position.First] = new(66, 78),
        [Position.Second] = new(34, 112),
        [Position.Short] = new(-34, 112),
        [Position.Third] = new(-66, 78),
        [Position.Left] = new(-170, 255),
        [Position.Center] = new(0, 310),
        [Position.Right] = new(170, 255),
    };

    /// <summary>
    /// Where each fielder (the pitcher and catcher included) stands for an alignment.
    /// Right-handed hitters pull to left field and left-handed hitters to right.
    /// Outfielders are kept inside the wall when a park is given.
    /// </summary>
    public static IReadOnlyDictionary<Position, FieldPoint> FielderSpots(
        DefensiveAlignment alignment, BatHand batsFrom, Stadium? park = null)
    {
        var spots = new Dictionary<Position, FieldPoint>(Standard);

        switch (alignment)
        {
            case DefensiveAlignment.CornersIn:
                spots[Position.First] = new(52, 55);
                spots[Position.Third] = new(-52, 55);
                break;
            case DefensiveAlignment.InfieldIn:
                foreach (var pos in Infielders)
                    spots[pos] = new(Standard[pos].X * 0.7, Standard[pos].Y * 0.7);
                break;
            case DefensiveAlignment.GuardLines:
                spots[Position.First] = new(58, 62);
                spots[Position.Third] = new(-58, 62);
                // WHY: the corner outfielders edge toward the foul poles too, so the whole line is covered
                // and the outfield animates along with the infield (the odds change covers extra bases).
                spots[Position.Left] = Rotate(spots[Position.Left], -7, maxAngle: 40);
                spots[Position.Right] = Rotate(spots[Position.Right], 7, maxAngle: 40);
                break;
            case DefensiveAlignment.ShiftPull or DefensiveAlignment.ShiftOpposite:
            {
                int pull = PullSign(batsFrom);
                int toward = alignment == DefensiveAlignment.ShiftPull ? pull : -pull;
                // The corner man on the side the defense is leaving stays near his bag.
                var moving = toward < 0
                    ? new[] { Position.Second, Position.Short, Position.Third }
                    : new[] { Position.First, Position.Second, Position.Short };
                foreach (var pos in moving)
                    spots[pos] = Rotate(spots[pos], toward * 15, maxAngle: 40);
                foreach (var pos in Outfielders)
                    spots[pos] = Rotate(spots[pos], toward * 10, maxAngle: 40);
                break;
            }
        }

        if (park is not null)
            foreach (var pos in Outfielders)
            {
                var p = spots[pos];
                double limit = WallDistance(park, p.AngleDegrees) - 15;
                if (p.DistanceFromHome > limit)
                    spots[pos] = FieldPoint.FromPolar(p.AngleDegrees, Math.Max(limit, 60));
            }

        return spots;
    }

    /// <summary>
    /// Moves the outfielders in or out (feet, straight along the line from home), keeping each inside the wall.
    /// Used to play deeper for a power hitter and shallower for a slap hitter. Nobody else moves.
    /// </summary>
    public static IReadOnlyDictionary<Position, FieldPoint> WithOutfieldDepth(
        IReadOnlyDictionary<Position, FieldPoint> spots, double feet, Stadium park)
    {
        var result = new Dictionary<Position, FieldPoint>(spots);
        foreach (var pos in Outfielders)
        {
            var p = spots[pos];
            double limit = WallDistance(park, p.AngleDegrees) - 14;
            result[pos] = FieldPoint.FromPolar(p.AngleDegrees, Math.Clamp(p.DistanceFromHome + feet, 60, Math.Max(60, limit)));
        }
        return result;
    }

    /// <summary>
    /// How far the outfield plays from the usual depth for a hitter: out for power (isolated power well above
    /// a typical .150), in for a slap hitter. Between -10 and +12 feet; no stats means the usual depth.
    /// </summary>
    public static double OutfieldDepthFor(BatterStats stats) =>
        stats.AtBats < 50 ? 0 : Math.Clamp((stats.SluggingPct - stats.BattingAverage - 0.15) * 150, -10, 12);

    /// <summary>-1 for a hitter who pulls toward left field (right-handed), +1 toward right field (left-handed).</summary>
    public static int PullSign(BatHand batsFrom) => batsFrom == BatHand.Left ? 1 : -1;

    // Turns a spot about home plate, keeping its distance and staying in fair territory.
    private static FieldPoint Rotate(FieldPoint p, double degrees, double maxAngle) =>
        FieldPoint.FromPolar(Math.Clamp(p.AngleDegrees + degrees, -maxAngle, maxAngle), p.DistanceFromHome);
}
