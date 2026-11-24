namespace MiniLeague.Engine.Models;

/// <summary>
/// The situation just before a play, kept so it can be shown afterwards (by then the game state has moved on).
/// Everything here is read-only data about that moment.
/// </summary>
public record PrePlaySnapshot(
    int Inning,
    bool IsTop,
    int Outs,
    BaseRunner? First,
    BaseRunner? Second,
    BaseRunner? Third,
    Player Batter,
    BatHand BatsFrom,
    Player Pitcher,
    DefensiveAlignment Alignment,
    IReadOnlyDictionary<Position, Player> Fielders,
    Stadium Stadium)
{
    /// <summary>The defense's fielders (catcher and the six other fielders) by position; the pitcher is separate.</summary>
    public static PrePlaySnapshot Capture(GameState s)
    {
        var pitcher = s.CurrentPitcher.Player;
        var batter = s.CurrentBatter;
        var fielders = new Dictionary<Position, Player>();
        foreach (var pos in new[]
        {
            Position.C, Position.First, Position.Second, Position.Third, Position.Short,
            Position.Left, Position.Center, Position.Right,
        })
            if (s.DefenderAt(pos) is { } who) fielders[pos] = who;

        return new PrePlaySnapshot(
            s.CurrentInning, s.IsTopOfInning, s.Outs,
            s.RunnerOnFirst, s.RunnerOnSecond, s.RunnerOnThird,
            batter, batter.BatsAgainst(pitcher.ThrowHand), pitcher,
            s.IsTopOfInning ? s.HomeAlignment : s.VisitorAlignment,
            fielders, s.Stadium);
    }
}
