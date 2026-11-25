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

    /// <summary>How much better than a plain swing a squeeze play (suicide or safety) must look, with a runner on third.</summary>
    public double SqueezeMargin { get; init; } = 0.04;

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

    /// <summary>The first inning in which this manager considers an intentional walk at all.</summary>
    public int IntentionalWalkFromInning { get; init; } = 7;

    /// <summary>How much better (in OPS) the batter must be than the next hitter before walking him is considered.</summary>
    public double IntentionalWalkOpsGap { get; init; } = 0.15;

    /// <summary>How much better a reliever must look than the tiring pitcher in a close late game before he is used.</summary>
    public double ReliefEdgeClose { get; init; } = 0.035;

    /// <summary>The same bar earlier or in a game that is not close.</summary>
    public double ReliefEdgeOther { get; init; } = 0.085;

    /// <summary>
    /// When the pitcher is tiring, how much better a warm reliever must look before he comes in. Negative means the
    /// manager takes a slightly worse arm sooner than a tired one; positive means he stays with his man.
    /// </summary>
    public double TiringSwapMargin { get; init; } = -0.08;

    /// <summary>Runs a starter may allow (at 0.18 or more a batter) before he is pulled as shelled.</summary>
    public int ShelledRuns { get; init; } = 5;

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

    // ── Philosophy: how the manager reads the game, not only how readily he acts ──

    /// <summary>
    /// Appetite for variance by the score. From the seventh inning a positive value makes the gambles (a steal, a hit and
    /// run, running hard) cost less to a manager who is behind and more to one who is ahead, by 0.04 runs times this
    /// value; a negative value does the opposite (plays it safe when behind, presses when ahead). Zero ignores the score.
    /// </summary>
    public double SituationalRisk { get; init; } = 0;

    /// <summary>
    /// How much the importance of the moment changes how readily he acts. At 0 the same edge is enough early and late.
    /// Positive: in a high-leverage moment (late, close) he acts on smaller edges and early in a game or in a blowout on
    /// larger ones. Negative: he is stubborn when it matters and loose when it does not.
    /// </summary>
    public double LeverageSensitivity { get; init; } = 0;

    /// <summary>
    /// How unpredictable the fielding manager's pitch and alignment are. 0 always takes the best option (breaking only
    /// near-ties at random). A positive value picks among all the options at random, weighted toward the better ones; it
    /// is a temperature in runs, so 0.01 hardly strays and 0.05 mixes things up freely.
    /// </summary>
    public double PitchMix { get; init; } = 0;

    /// <summary>
    /// A starter this deep into a game is replaced if a reliever is ready, however well he is pitching (a pitch-count
    /// manager). The default is beyond any real outing: he goes by his condition and the matchup alone.
    /// </summary>
    public int StarterPitchLimit { get; init; } = int.MaxValue;

    /// <summary>
    /// The first inning in which a lead of one to three runs is a save situation, so the closer is held for it and
    /// brought in. 9 is the classic closer; 8 or earlier is a fireman who is used in the tight spots of the late innings.
    /// </summary>
    public int CloserFromInning { get; init; } = 9;

    /// <summary>
    /// How much weight the platoon matchup gets in his bench and bullpen choices: 1 takes the matchup at its real value, 0
    /// ignores which hand the hitter and pitcher use, 2 doubles the importance of getting the matchup right.
    /// </summary>
    public double PlatoonWeight { get; init; } = 1.0;

    /// <summary>
    /// The first inning in which he trades a weak glove for a better one when his side leads. The default is beyond any
    /// game: he never makes a defensive replacement.
    /// </summary>
    public int DefensiveReplaceFromInning { get; init; } = int.MaxValue;

    /// <summary>The largest lead (in runs) at which he makes a defensive replacement; a bigger lead needs no protecting.</summary>
    public int DefensiveReplaceMaxLead { get; init; } = 3;

    /// <summary>
    /// The net fielding value (see <c>DefenseModel.PositionValue</c>, after the cost of the bat he loses) a replacement
    /// must add. An ordinary glove for a poor one at a key position is worth a few points.
    /// </summary>
    public double DefensiveReplaceGain { get; init; } = 1.0;

    /// <summary>
    /// The net gain (in runs, after the cost of using a bench player) a double switch must show; it applies only when the
    /// pitcher bats. A very high number means he never makes one.
    /// </summary>
    public double DoubleSwitchGain { get; init; } = 0.02;

    /// <summary>The share of the time a pitchout is actually called when the numbers favor one.</summary>
    public double PitchoutShare { get; init; } = 0.3;
}
