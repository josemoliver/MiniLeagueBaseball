namespace MiniLeague.Engine.Models;

/// <summary>Random events that can interrupt a game. Each can be switched off.</summary>
public record GameOptions(bool Injuries, bool RainDelays, bool Ejections)
{
    public static GameOptions None => new(false, false, false);
    public static GameOptions Standard => new(true, true, true);
    public bool Any => Injuries || RainDelays || Ejections;
}

/// <summary>A player who has been hurt and must be replaced before play continues.</summary>
public record InjuryNotice(bool Visitor, int PlayerId, bool IsPitcher);
