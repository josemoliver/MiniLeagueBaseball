namespace MiniLeague.Engine.Models;

/// <summary>One play on the game's win-probability track.</summary>
/// <param name="Swing">The change in the home team's win probability caused by this play (positive helps the home team).</param>
public record RecapPlay(int Inning, bool IsTop, string Description, int VisitorScore, int HomeScore,
                        double HomeWinProbability, double Swing)
{
    public string Half => $"{(IsTop ? "▲" : "▼")}{Inning}";
}
