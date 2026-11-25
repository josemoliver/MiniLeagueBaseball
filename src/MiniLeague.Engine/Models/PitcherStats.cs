namespace MiniLeague.Engine.Models;

public class PitcherStats
{
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Saves { get; set; }
    public int CompleteGames { get; set; }
    public double ERA { get; set; }
    public double InningsPitched { get; set; }
    public int HitsAllowed { get; set; }
    public int HomeRunsAllowed { get; set; }
    public int WalksAllowed { get; set; }
    public int Strikeouts { get; set; }
    public double GroundBallRate { get; set; } = 0.45;
    public int TiringFactor { get; set; } = 5;  // 1 (durable) – 9 (fragile)

    /// <summary>Games he started that season; 0 when unknown (a reliever, or a team file from before it was recorded).</summary>
    public int GamesStarted { get; set; }

    /// <summary>
    /// How long his starts really were, in outs (three times innings per start), or 0 when unknown. WHY: it is the one real
    /// measure of how deep into a game this pitcher went, and it differs a lot between eras and men (a 1927 Yankee finished
    /// more than half of his starts; a 1979 Pirate hardly any).
    /// </summary>
    public double OutsPerStart => GamesStarted > 0 ? InningsPitched * 3 / GamesStarted : 0;

    /// <summary>The share of his starts he finished; 0 when unknown.</summary>
    public double CompleteGameRate => GamesStarted > 0 ? Math.Min(1.0, (double)CompleteGames / GamesStarted) : 0;
    public int HoldRunners { get; set; } = 3;    // 1–5, counters stolen base

    /// <summary>The league and season he pitched in: what "average" means for him. Set when a team is loaded.</summary>
    public LeaguePitching League { get; set; } = LeaguePitching.Default;

    /// <summary>Hits other than home runs per nine innings.</summary>
    public double NonHomeRunHitsPerNine => InningsPitched > 0
        ? Math.Max(HitsAllowed - HomeRunsAllowed, 0) / InningsPitched * 9
        : 0;

    public double HomeRunsPerNine => InningsPitched > 0
        ? HomeRunsAllowed / InningsPitched * 9
        : 0;

    public double WHIP => InningsPitched > 0
        ? (WalksAllowed + HitsAllowed) / InningsPitched
        : 0;
    public double HitsPerNine => InningsPitched > 0
        ? HitsAllowed / InningsPitched * 9
        : 0;
    public double StrikeoutsPerNine => InningsPitched > 0
        ? Strikeouts / InningsPitched * 9
        : 0;
    public double WalksPerNine => InningsPitched > 0
        ? WalksAllowed / InningsPitched * 9
        : 0;
}
