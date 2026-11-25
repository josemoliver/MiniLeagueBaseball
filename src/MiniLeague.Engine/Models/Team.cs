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

    /// <summary>Share of steal attempts that succeeded in this team's league that season; each of its runners is steadied toward it.</summary>
    public double LeagueStealRate { get; set; } = Simulation.RunningRules.DefaultLeagueStealRate;

    /// <summary>What an average pitcher did in this team's league that season; each of its pitchers is judged against it.</summary>
    public LeaguePitching LeaguePitching { get; set; } = LeaguePitching.Default;

    /// <summary>
    /// How this club was actually managed that season (from the Lahman team totals), or null when the team file does
    /// not carry it. Not yet used in play; it is the basis for era-aware managers.
    /// </summary>
    public TeamTendencies? Tendencies { get; set; }

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

/// <summary>A club's managing habits over a real season, per game played.</summary>
/// <param name="StealAttemptsPerGame">Stolen bases plus caught stealing, per game.</param>
/// <param name="SacrificesPerGame">Sacrifice hits (bunts), per game.</param>
/// <param name="CompleteGameRate">The share of games a starter finished.</param>
/// <param name="SavesPerGame">Saves per game.</param>
public sealed record TeamTendencies(double StealAttemptsPerGame, double SacrificesPerGame,
    double CompleteGameRate, double SavesPerGame);
