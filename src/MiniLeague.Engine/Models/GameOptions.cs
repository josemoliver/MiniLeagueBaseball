namespace MiniLeague.Engine.Models;

/// <summary>Whether the pitcher bats or a designated hitter does.</summary>
public enum DesignatedHitterSetting
{
    /// <summary>A designated hitter for both teams, whatever the year (every game before this setting existed).</summary>
    DesignatedHitter,

    /// <summary>The home team's league and year decide: no designated hitter in the National League before 2022 or the American League before 1973.</summary>
    Historical,

    /// <summary>No designated hitter for either team: the pitchers bat.</summary>
    PitchersBat,
}

/// <summary>Game settings chosen before the first pitch: the random events (each can be switched off) and the designated hitter.</summary>
public record GameOptions(bool Injuries, bool RainDelays, bool Ejections,
    DesignatedHitterSetting DesignatedHitter = DesignatedHitterSetting.DesignatedHitter)
{
    public static GameOptions None => new(false, false, false);
    public static GameOptions Standard => new(true, true, true);
    public bool Any => Injuries || RainDelays || Ejections;
}

public static class DesignatedHitterRules
{
    /// <summary>The first season the American League used the designated hitter.</summary>
    public const int AmericanLeagueFrom = 1973;

    /// <summary>The first season both leagues used it.</summary>
    public const int NationalLeagueFrom = 2022;

    /// <summary>
    /// Whether a game uses a designated hitter. WHY: both teams play by one rule, and as in a real interleague game the
    /// home team's league decides it. A team with no known league gets the designated hitter.
    /// </summary>
    public static bool Applies(DesignatedHitterSetting setting, Team home) => setting switch
    {
        DesignatedHitterSetting.DesignatedHitter => true,
        DesignatedHitterSetting.PitchersBat => false,
        _ => home.League switch
        {
            "NL" => home.Season >= NationalLeagueFrom,
            "AL" => home.Season >= AmericanLeagueFrom,
            _ => true,
        },
    };
}

/// <summary>A player who has been hurt and must be replaced before play continues.</summary>
public record InjuryNotice(bool Visitor, int PlayerId, bool IsPitcher);
