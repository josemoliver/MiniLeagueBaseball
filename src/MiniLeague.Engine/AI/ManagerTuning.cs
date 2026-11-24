namespace MiniLeague.Engine.AI;

/// <summary>
/// The dials that make one expected-run manager different from another. Every margin is in expected runs: how much
/// better a call must look than the ordinary one before this manager makes it, so a LOWER number means the manager
/// acts more readily. The defaults are the Analyst's, and are what <see cref="ComputerManager"/> uses on its own.
/// </summary>
public sealed record ManagerTuning
{
    /// <summary>Sentinel margin for "never": no call can beat the ordinary one by this much.</summary>
    public const double Never = 9.0;

    public static ManagerTuning Default { get; } = new();

    /// <summary>How much better than staying put a steal must look.</summary>
    public double StealMargin { get; init; } = 0.035;

    /// <summary>How much better than a plain swing a hit and run must look (contact hitters only).</summary>
    public double HitAndRunMargin { get; init; } = 0.035;

    /// <summary>How much better than swinging a sacrifice bunt must look.</summary>
    public double BuntMargin { get; init; } = 0.025;

    /// <summary>How much better than ordinary running aggressive baserunning must look.</summary>
    public double AggressiveRunMargin { get; init; } = 0.005;

    /// <summary>First inning in which a pinch hitter is considered.</summary>
    public int PinchHitFromInning { get; init; } = 6;

    /// <summary>The net gain (in expected runs) a pinch hitter must show over the man due up.</summary>
    public double PinchHitGain { get; init; } = 0.025;

    /// <summary>Scales the cost of spending a bench player (a thin bench, a bat lost for later). Above 1 hoards the bench.</summary>
    public double BenchReserveScale { get; init; } = 1.0;

    /// <summary>The net gain a pinch runner must show over the runner he replaces.</summary>
    public double PinchRunGain { get; init; } = 0.05;

    /// <summary>How much better than facing the batter an intentional walk must look.</summary>
    public double IntentionalWalkMargin { get; init; } = 0.04;

    /// <summary>How much better a reliever must look than the tiring pitcher in a close late game before he is used.</summary>
    public double ReliefEdgeClose { get; init; } = 0.035;

    /// <summary>The same bar earlier or in a game that is not close.</summary>
    public double ReliefEdgeOther { get; init; } = 0.085;

    /// <summary>
    /// Pitches thrown after which this manager will swap a pitcher who is still fresh, if a warm reliever looks at
    /// least as good. The default is beyond any real outing: the pitcher goes only once he begins to tire.
    /// </summary>
    public int QuickHookPitchCount { get; init; } = int.MaxValue;

    /// <summary>
    /// Pitches thrown after which, in a close game from <see cref="WarmUpFromInning"/> on, a reliever is told to warm
    /// up even though the pitcher is still effective. Lower means the bullpen is busier.
    /// </summary>
    public int WarmUpPitchCount { get; init; } = 65;

    /// <summary>The first inning in which a close game makes this manager start a reliever warming early.</summary>
    public int WarmUpFromInning { get; init; } = 7;

    /// <summary>The share of the time a pitchout is actually called when the numbers favor one.</summary>
    public double PitchoutShare { get; init; } = 0.3;
}
