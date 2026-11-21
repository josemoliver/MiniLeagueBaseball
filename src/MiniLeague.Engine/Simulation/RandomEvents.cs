namespace MiniLeague.Engine.Simulation;

/// <summary>How often the optional random events happen. Tuned so each shows up in roughly one game in ten.</summary>
public static class RandomEvents
{
    /// <summary>Chance, after any play, that a player on the field is hurt (~1 injury every 10–12 games).</summary>
    public const double InjuryPerPlay = 0.0012;

    /// <summary>Chance, at the start of any half-inning after the first, of a rain delay (~10% of games).</summary>
    public const double RainPerHalfInning = 0.006;

    /// <summary>Chance a rain delay in an official game with a leader ends the game.</summary>
    public const double RainCallChance = 0.30;

    /// <summary>Chance a strikeout, walk or caught stealing draws an ejection (~1 in 15 games for a team).</summary>
    public const double EjectionPerDisputedPlay = 0.0035;

    /// <summary>A reliever's effectiveness after sitting through a delay, for his first few batters.</summary>
    public const double PostDelayModifier = 0.88;
}
