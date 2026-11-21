using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

public static class FatigueModel
{
    public const int SettleInBatters = 3;

    /// <summary>
    /// Returns a fatigue multiplier (1.0 = full strength) based on pitch count,
    /// innings pitched, and the pitcher's individual tiring factor.
    /// </summary>
    public static double GetModifier(ActivePitcher p)
    {
        double mod = 1.0;
        int pitches = p.PitchCount;
        int tiringFactor = p.Player.PitchingOverall?.TiringFactor ?? 5;

        // Pitch count thresholds — adjusted by tiring factor
        // Durable pitchers (TF=1) hold strength longer; fragile (TF=9) fade sooner
        double threshold1 = 80  + (5 - tiringFactor) * 5;   // 55–105
        double threshold2 = 100 + (5 - tiringFactor) * 5;   // 75–125
        double threshold3 = 115 + (5 - tiringFactor) * 5;   // 90–140

        if (pitches > threshold3)      mod *= 0.65;
        else if (pitches > threshold2) mod *= 0.80;
        else if (pitches > threshold1) mod *= 0.92;

        // A reliever who came in cold or overwarmed is below par until he settles in.
        if (p.BattersFaced - p.SettleFrom < SettleInBatters) mod *= p.EntryModifier;

        // Update displayed condition
        p.Condition = mod switch
        {
            >= 0.95 => PitcherCondition.Fresh,
            >= 0.88 => PitcherCondition.Ok,
            >= 0.78 => PitcherCondition.Tiring,
            >= 0.65 => PitcherCondition.Tired,
            _       => PitcherCondition.Exhausted
        };

        return mod;
    }

    /// <summary>
    /// Warm-up modifier for relievers: cold entry or overwarmed both hurt.
    /// </summary>
    public static double GetWarmupModifier(WarmingPitcher wp)
    {
        return wp.BattersFacedWarming switch
        {
            0 => 0.70,         // no warmup at all
            1 => 0.78,
            2 => 0.86,
            3 => 0.93,
            >= 4 and <= 10 => 1.00,   // ideal window
            11 => 0.97,
            12 => 0.93,
            13 => 0.87,
            14 => 0.80,
            _ => 0.72          // severely overwarmed
        };
    }
}
