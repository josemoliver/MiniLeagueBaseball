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
    Balk, Error,
    // WHY: appended at the end so the numbers of the older values never change (saved games may hold them).
    /// <summary>A bunt that gets a runner over: the batter is out, at least one runner moves up. Not an at-bat.</summary>
    SacrificeBunt,
    /// <summary>The defense takes a runner instead of the batter, who reaches first. An at-bat, not a hit.</summary>
    FieldersChoice
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

    /// <summary>The pitch the defense called for (the last one, on a play that took several). Display only: it decides how the pitcher winds up.</summary>
    public PitchType Pitch { get; set; } = PitchType.Fastball;

    /// <summary>Estimated pitches the pitcher threw on this play (drives fatigue).</summary>
    public int PitchesThrown { get; set; }

    /// <summary>Set for <see cref="OutcomeType.Error"/>: who made it and where he was playing.</summary>
    public Player? ErrorBy { get; set; }
    public Position? ErrorPosition { get; set; }

    /// <summary>
    /// The position that handled the ball on a ground out, fly out, double play or error, on any bunt (who fielded it),
    /// and on a line drive that doubles off a hit-and-run runner (null otherwise: strikeouts, walks, steals, and hits,
    /// where nobody is credited). Display only; the box score does not use it.
    /// </summary>
    public Position? Fielder { get; set; }

    /// <summary>
    /// What happened to every runner on the play, batter included (a steal attempt has no batter).
    /// Display only. It always agrees with the base state, runs and outs above (see <see cref="RunnerMoves"/>).
    /// </summary>
    public List<RunnerMove> RunnerMoves { get; set; } = [];

    /// <summary>
    /// The batter bunted. The outcome says how it ended (SacrificeBunt, FieldersChoice, or Single, GroundOut, PopOut
    /// and Error for a bunt hit, a bunt thrown out, a bunt popped up and a bunt booted), and this tells the
    /// animation to stage it as a bunt rather than a swing.
    /// </summary>
    public bool IsBunt { get; set; }

    /// <summary>The runner on first was going on the pitch (a hit and run). Display and box score only.</summary>
    public bool HitAndRun { get; set; }

    // For animations / display
    public bool IsHit => Outcome is OutcomeType.Single or OutcomeType.Double
                                  or OutcomeType.Triple or OutcomeType.HomeRun;
    public bool IsOut => Outcome is OutcomeType.Strikeout or OutcomeType.GroundOut
                                  or OutcomeType.FlyOut or OutcomeType.LineOut
                                  or OutcomeType.PopOut or OutcomeType.CaughtStealing
                                  or OutcomeType.SacrificeBunt or OutcomeType.FieldersChoice;
    public bool IsWalk => Outcome is OutcomeType.Walk or OutcomeType.HitByPitch
                                   or OutcomeType.IntentionalWalk;
}

/// <summary>What happened to one runner on a play.</summary>
public enum RunnerFate
{
    /// <summary>Reached a base (or advanced to one).</summary>
    Safe,
    /// <summary>Crossed the plate.</summary>
    Scored,
    /// <summary>Did not move (or, when the inning ended, was left on base).</summary>
    StayedPut,
    /// <summary>Put out at a base by a force (ground out, double play).</summary>
    ForcedOut,
    /// <summary>Tagged out (caught stealing).</summary>
    TaggedOut,
    /// <summary>Put out without a base play: struck out or caught on a fly, line drive or pop-up.</summary>
    Retired,
}

/// <summary>
/// One runner's move on a play. FromBase is 0 for the batter, 1–3 for a runner on a base.
/// ToBase is 1–3 for a base reached, 4 for home (scored), the base he was put out at for a forced or tag out,
/// and 0 for a batter who was retired without reaching a base.
/// </summary>
public record RunnerMove(Player Player, int FromBase, int ToBase, RunnerFate Fate)
{
    public bool IsOut => Fate is RunnerFate.ForcedOut or RunnerFate.TaggedOut or RunnerFate.Retired;
}
