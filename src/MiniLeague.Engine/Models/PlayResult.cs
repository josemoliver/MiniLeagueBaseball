namespace MiniLeague.Engine.Models;

public enum OutcomeType
{
    Single, Double, Triple, HomeRun,
    Walk, HitByPitch, IntentionalWalk,
    Strikeout,
    GroundOut, FlyOut, LineOut, PopOut,
    DoublePlay, TriplePlay,
    StolenBase, CaughtStealing,
    WildPitch, PassedBall,
    Balk, Error
}

public class PlayResult
{
    public OutcomeType Outcome { get; set; }
    public string Description { get; set; } = string.Empty;

    // Runs scored on this play
    public int RunsScored { get; set; }

    // Base state after play
    public BaseRunner? NewFirst { get; set; }
    public BaseRunner? NewSecond { get; set; }
    public BaseRunner? NewThird { get; set; }

    public int OutsRecorded { get; set; }  // usually 0 or 1, can be 2 for DP

    /// <summary>Estimated pitches the pitcher threw on this play (drives fatigue).</summary>
    public int PitchesThrown { get; set; }

    /// <summary>Set for <see cref="OutcomeType.Error"/>: who made it and where he was playing.</summary>
    public Player? ErrorBy { get; set; }
    public Position? ErrorPosition { get; set; }

    // For animations / display
    public bool IsHit => Outcome is OutcomeType.Single or OutcomeType.Double
                                  or OutcomeType.Triple or OutcomeType.HomeRun;
    public bool IsOut => Outcome is OutcomeType.Strikeout or OutcomeType.GroundOut
                                  or OutcomeType.FlyOut or OutcomeType.LineOut
                                  or OutcomeType.PopOut or OutcomeType.CaughtStealing;
    public bool IsWalk => Outcome is OutcomeType.Walk or OutcomeType.HitByPitch
                                   or OutcomeType.IntentionalWalk;
}
