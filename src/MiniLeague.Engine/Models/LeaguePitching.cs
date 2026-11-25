namespace MiniLeague.Engine.Models;

/// <summary>
/// What an average pitcher did in the league and season of a team, per nine innings. A pitcher is judged against these, not
/// against fixed numbers.
/// WHY: a batter's season line already includes facing the pitching of his own league. If a pitcher were scaled by a fixed
/// 4.00 ERA, a hitter of 1968 (league ERA 2.98) would be penalized a second time for a league that was hard to hit in, and
/// one of 1927 (2.8 strikeouts per nine, against a fixed 7.0) would strike out far too little to be that team. Scaling by the
/// pitcher's rate divided by his league's keeps an average pitcher at exactly 1.0 in every era.
/// </summary>
/// <param name="Era">League earned-run average.</param>
/// <param name="NonHomeRunHitsPerNine">Hits other than home runs. 0 when unknown: the pitcher's ERA then scales every hit.</param>
/// <param name="HomeRunsPerNine">Home runs. 0 when unknown.</param>
/// <param name="StrikeoutsPerNine">Strikeouts.</param>
/// <param name="WalksPerNine">Walks.</param>
public sealed record LeaguePitching(double Era, double NonHomeRunHitsPerNine, double HomeRunsPerNine,
    double StrikeoutsPerNine, double WalksPerNine)
{
    /// <summary>The reference the engine used before leagues were known: a 4.00 ERA, 7.0 strikeouts and 3.0 walks per nine.</summary>
    public static readonly LeaguePitching Default = new(4.00, 0, 0, 7.0, 3.0);

    /// <summary>True when the hit rates are known, so hits and home runs can be scaled separately.</summary>
    public bool HasHitRates => NonHomeRunHitsPerNine > 0 && HomeRunsPerNine > 0;
}
