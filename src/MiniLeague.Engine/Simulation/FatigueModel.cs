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

        // Pitch count thresholds. A starter whose real starts are known is judged by them (see Stamina); anyone else by the
        // tiring factor: durable pitchers (TF=1) hold strength longer, fragile (TF=9) fade sooner.
        double stamina = Stamina(p.Player.PitchingOverall);
        double threshold1 = stamina > 0 ? stamina       : 80  + (5 - tiringFactor) * 5;   // 55–105
        double threshold2 = stamina > 0 ? stamina + 20  : 100 + (5 - tiringFactor) * 5;   // 75–125
        double threshold3 = stamina > 0 ? stamina + 35  : 115 + (5 - tiringFactor) * 5;   // 90–140

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

    /// <summary>The engine's pitches for each out a pitcher records, measured over simulated games (calibration shows ~5.6).</summary>
    public const double PitchesPerOut = 5.6;

    /// <summary>
    /// The pitch count at which a starter begins to tire, from how long his real starts were, or 0 when that is unknown.
    /// WHY: the tiring factor is whole innings per start taken from 10, so nearly every starter of every era came out as a 2,
    /// 3 or 4: Cuellar, who finished more than half his starts, and Nolan, who finished a few, differed by one step. His own
    /// average start, in pitches, plus a bonus for the starts he finished (a man who goes the distance has more in reserve than
    /// his average shows, since his manager took him out of the others), says much more.
    /// </summary>
    public static double Stamina(PitcherStats? stats)
    {
        if (stats is null || stats.GamesStarted <= 0) return 0;
        double typical = stats.OutsPerStart * PitchesPerOut - 12 + 15 * stats.CompleteGameRate;
        return Math.Clamp(typical, 70, 150);
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
