namespace MiniLeague.Engine.Models;

public class OffensiveDecision
{
    public OffensiveStrategy Strategy { get; set; } = OffensiveStrategy.SwingAway;
    public int? PinchHitterPlayerId { get; set; }
    public int? PinchRunnerPlayerId { get; set; }
    public int? PinchRunnerBaseIndex { get; set; }  // 1, 2, or 3
}

public class DefensiveDecision
{
    public PitchType PitchType { get; set; } = PitchType.Fastball;
    public DefensiveAlignment Alignment { get; set; } = DefensiveAlignment.Standard;
    public bool IntentionalWalk { get; set; }
    public bool MoundVisit { get; set; }
    public int? RelieverPlayerId { get; set; }   // null = no change
}
