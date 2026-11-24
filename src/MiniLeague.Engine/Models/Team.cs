namespace MiniLeague.Engine.Models;

public class Team
{
    public int Id { get; set; }
    public string City { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Abbreviation { get; set; } = string.Empty;
    public int Season { get; set; }
    public string League { get; set; } = string.Empty;  // AL, NL
    public bool IsHistorical { get; set; }
    public string StadiumId { get; set; } = "generic";

    /// <summary>League-wide errors per team game that season: sets how often balls in play become errors.</summary>
    public double EraErrorsPerGame { get; set; } = 0.8;

    /// <summary>League-wide strikeouts per team game that season (the rest of the outs are batted balls).</summary>
    public double EraStrikeoutsPerGame { get; set; } = 4.0;

    public List<Player> Roster { get; set; } = [];
    public List<int> DefaultLineup { get; set; } = [];  // player IDs in batting order

    public string DisplayName => $"{Season} {City} {Nickname}";

    public IEnumerable<Player> Batters => Roster.Where(p => !p.IsPitcher);
    public IEnumerable<Player> Pitchers => Roster.Where(p => p.IsPitcher);
    public IEnumerable<Player> Starters => Pitchers.Where(p => p.Role == PitcherRole.Starter);

    /// <summary>The pitcher who starts when none is chosen: the first starter in the file (the ace).</summary>
    public Player DefaultStarter => Starters.FirstOrDefault() ?? Pitchers.First();

    /// <summary>Every pitcher who could start a game: the starters in rotation order, then the rest.</summary>
    public IEnumerable<Player> StartingPitcherChoices =>
        Starters.Concat(Pitchers.Where(p => p.Role != PitcherRole.Starter));
    public IEnumerable<Player> Bullpen => Pitchers.Where(p => p.Role != PitcherRole.Starter);
}
