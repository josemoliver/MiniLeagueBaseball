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
    public int HoldRunners { get; set; } = 3;    // 1–5, counters stolen base

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
