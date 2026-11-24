using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

/// <summary>How a batter stands at the plate. It follows the kind of hitter he is, using the same measure as the outfield's depth.</summary>
public static class Stances
{
    /// <summary>Feet apart, bat held high and back: a hitter with power.</summary>
    public const string Power = "power";

    /// <summary>Crouched, bat held short and level: a slap hitter.</summary>
    public const string Slap = "slap";

    /// <summary>An ordinary stance.</summary>
    public const string Balanced = "balanced";

    public static string Of(Player batter, ThrowHand pitcherHand)
    {
        double depth = FieldGeometry.OutfieldDepthFor(batter.GetBattingStats(pitcherHand));
        return depth >= 4 ? Power : depth <= -3 ? Slap : Balanced;
    }
}
