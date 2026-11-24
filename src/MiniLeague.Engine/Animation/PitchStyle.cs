using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

/// <summary>
/// How a pitch looks: when the pitcher lets go and the ball reaches the plate, the wind-up he uses, and how the
/// ball travels. Times are seconds at Normal speed. The fastball is the one every other time was tuned against.
/// </summary>
/// <param name="Windup">When the arms go up (null: no wind-up, as on a pitchout).</param>
/// <param name="Kick">When the front leg is at its highest.</param>
/// <param name="Release">When the ball is let go.</param>
/// <param name="Arrival">When the ball reaches the plate.</param>
/// <param name="ReleasePose">How the arm comes through.</param>
/// <param name="Apex">How far above a straight line the ball floats (a curve loops, a change-up hangs); 0 for a straight flight.</param>
/// <param name="Break">How far (in tenths of a foot) the ball breaks sideways late in its flight.</param>
internal readonly record struct PitchStyle(
    double? Windup, double Kick, double Release, double Arrival, string ReleasePose, double Apex = 0, double Break = 0)
{
    /// <summary>How far to the throwing side of the pitcher, and how high, his hand is as the ball leaves it (feet).</summary>
    public double HandSide => ReleasePose == Poses.Sidearm ? 6.1 : 2.4;
    public double HandHeight => ReleasePose == Poses.Sidearm ? 7.6 : 12;

    public static PitchStyle Of(PitchType pitch) => pitch switch
    {
        // Over the top, quick.
        PitchType.Fastball => new(0.05, 0.14, 0.25, 0.60, Poses.Throw),
        // A high, slow leg kick; the ball loops in.
        PitchType.Curveball => new(0.05, 0.26, 0.42, 0.98, Poses.Throw, Apex: 5),
        // Three-quarters, arm out to the side; quick, and it breaks late.
        PitchType.Slider => new(0.05, 0.15, 0.25, 0.66, Poses.Sidearm, Break: 12),
        // The same wind-up as the fastball, which is the point, but the ball comes in slowly and hangs.
        PitchType.Changeup => new(0.05, 0.14, 0.25, 0.88, Poses.Throw, Apex: 1.5),
        // No wind-up: out of the stretch and gone.
        PitchType.Pitchout => new(null, 0.04, 0.14, 0.48, Poses.Throw),
        _ => new(0.05, 0.14, 0.25, 0.60, Poses.Throw),
    };
}
