namespace MiniLeague.Engine.Models;

/// <summary>
/// What a steal call sends: from first to second, from second to third, or both runners (a double steal). Only a base that
/// is open can be stolen: with second and third both taken, or with only third, there is nothing to steal.
/// </summary>
public enum StealPlan { None, Second, Third, Double }

/// <summary>A squeeze play: the runner on third breaks with the pitch (suicide) or only once the bunt is down (safety).</summary>
public enum SqueezeKind { None, Suicide, Safety }

public class OffensiveDecision
{
    public OffensiveStrategy Strategy { get; set; } = OffensiveStrategy.SwingAway;
    public int? PinchHitterPlayerId { get; set; }
    public int? PinchRunnerPlayerId { get; set; }
    public int? PinchRunnerBaseIndex { get; set; }  // 1, 2, or 3

    /// <summary>Why the computer made this call (null for a human's decision).</summary>
    public DecisionExplanation? Why { get; set; }
}

public class DefensiveDecision
{
    public PitchType PitchType { get; set; } = PitchType.Fastball;
    public DefensiveAlignment Alignment { get; set; } = DefensiveAlignment.Standard;
    public bool IntentionalWalk { get; set; }
    public bool MoundVisit { get; set; }
    public int? RelieverPlayerId { get; set; }   // null = no change

    /// <summary>Why the computer made this call (null for a human's decision).</summary>
    public DecisionExplanation? Why { get; set; }
}

/// <summary>
/// One option a computer manager weighed, and what it was worth compared with the ordinary call (swinging away, or a
/// normal defense), in runs: positive is better for the side making the call.
/// </summary>
public sealed record DecisionOption(string Call, double Gain);

/// <summary>
/// Why a computer manager made a call: a sentence for the player, and the options it weighed. Gains are in runs, but
/// weighted by what a run is worth to winning at that moment, so a late, close game counts the first run for more.
/// </summary>
public sealed record DecisionExplanation(string Summary, IReadOnlyList<DecisionOption> Considered)
{
    public static DecisionExplanation Rule(string summary) => new(summary, []);
}
