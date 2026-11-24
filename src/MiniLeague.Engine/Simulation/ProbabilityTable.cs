using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

/// <summary>
/// Builds a weighted probability distribution for at-bat outcomes
/// by combining batter stats, pitcher stats, and situational modifiers.
/// All weights are proportional — they are normalised before a roll is taken.
/// </summary>
public static class ProbabilityTable
{
    public static OutcomeWeights Build(
        BatterStats batter,
        PitcherStats pitcher,
        Stadium stadium,
        BatHand batterHand,
        PitchType pitch,
        DefensiveAlignment alignment,
        double pitcherFatigueMod,   // 1.0 = full strength, <1.0 = degraded
        DefenseRatings? defense = null)
    {
        int pa = batter.AtBats + batter.Walks + batter.HitByPitch;
        if (pa == 0) return OutcomeWeights.Default;

        // --- Batter per-PA base rates ---
        double bHR = (double)batter.HomeRuns / pa;
        double b3B = (double)batter.Triples / pa;
        double b2B = (double)batter.Doubles / pa;
        int singles = batter.Hits - batter.HomeRuns - batter.Triples - batter.Doubles;
        double b1B = (double)Math.Max(singles, 0) / pa;
        double bBB = (double)batter.Walks / pa;
        double bHBP = (double)batter.HitByPitch / pa;
        double bSO = (double)batter.Strikeouts / pa;

        // --- Pitcher modifier: scale batter rates by pitcher quality ---
        // Reference ERA ≈ 4.00. Good pitcher (ERA=2) → hitMod=0.5 (fewer hits).
        // Bad pitcher (ERA=6) → hitMod=1.5 (more hits). Clamped to avoid extremes.
        double hitMod  = Math.Clamp(pitcher.ERA / 4.00, 0.40, 2.0) * pitcherFatigueMod;

        // BB/9 reference ≈ 3.0. Good control (BB=1) → bbMod low. Wild (BB=5) → higher.
        double bbMod   = Math.Clamp(pitcher.WalksPerNine / 3.0, 0.4, 2.5);

        // K/9 reference ≈ 7.0. High K pitcher → soMod high.
        double soMod   = Math.Clamp(pitcher.StrikeoutsPerNine / 7.0, 0.4, 2.5);

        double hrW    = bHR  * hitMod;
        double triW   = b3B  * hitMod;
        double dblW   = b2B  * hitMod;
        double sglW   = b1B  * hitMod;
        double bbW    = bBB  * bbMod;
        double hbpW   = bHBP;
        double soW    = bSO  * soMod;

        // --- Park factors ---
        // Hitters' own stats already carry some of their home park, so the overall run environment is
        // applied at half strength; the fence-specific factors (homers, extra-base hits) in full.
        double hrFactor = (batterHand == BatHand.Left
            ? stadium.HomeRunFactorLeft
            : stadium.HomeRunFactorRight) * stadium.HomeRunFactor;
        double scoring = Math.Sqrt(Math.Clamp(stadium.RunsFactor, 0.7, 1.4));
        hrW  *= hrFactor * scoring;
        triW *= stadium.DoublesTriplesFactor * scoring;
        dblW *= stadium.DoublesTriplesFactor * scoring;
        sglW *= stadium.SinglesFactor * scoring;

        // --- Fielders: better range turns hits into outs, mostly singles and balls in the gaps ---
        var d = defense ?? DefenseRatings.Average;
        double infieldEffect = 1 - 0.015 * (d.InfieldRange - 5);
        double outfieldEffect = 1 - 0.020 * (d.OutfieldRange - 5);
        sglW *= Math.Clamp(infieldEffect * 0.6 + outfieldEffect * 0.4, 0.88, 1.12);
        dblW *= Math.Clamp(outfieldEffect, 0.88, 1.12);
        triW *= Math.Clamp(outfieldEffect, 0.88, 1.12);

        // --- Pitch type modifier ---
        (hrW, soW, bbW, sglW) = ApplyPitchType(pitch, hrW, soW, bbW, sglW, dblW);

        // --- Defensive alignment ---
        (sglW, dblW, triW) = ApplyAlignment(alignment, sglW, dblW, triW);

        // Remaining probability goes to balls in play (ground outs / fly outs)
        double hitW = hrW + triW + dblW + sglW;
        double onBaseW = hitW + bbW + hbpW;
        double outW = 1.0 - onBaseW - soW;
        outW = Math.Max(outW, 0.05);

        double gbRate = pitcher.GroundBallRate;
        double goW = outW * gbRate;
        double foW = outW * (1 - gbRate);

        return new OutcomeWeights
        {
            HomeRun   = hrW,
            Triple    = triW,
            Double    = dblW,
            Single    = sglW,
            Walk      = bbW,
            HitByPitch = hbpW,
            Strikeout = soW,
            GroundOut = goW,
            FlyOut    = foW,
        };
    }

    private static (double hr, double so, double bb, double sg) ApplyPitchType(
        PitchType pitch, double hr, double so, double bb, double sg, double db)
    {
        return pitch switch
        {
            PitchType.Fastball   => (hr * 1.10, so * 1.10, bb * 0.90, sg),
            PitchType.Curveball  => (hr * 0.85, so * 1.05, bb * 1.10, sg * 0.95),
            PitchType.Slider     => (hr * 0.90, so * 1.15, bb * 1.05, sg * 0.90),
            PitchType.Changeup   => (hr * 0.80, so * 0.90, bb * 0.95, sg * 1.10),
            PitchType.Pitchout   => (hr * 0.10, so * 0.10, bb * 2.00, sg * 0.10),
            _                    => (hr, so, bb, sg)
        };
    }

    private static (double sg, double db, double tr) ApplyAlignment(
        DefensiveAlignment alignment, double sg, double db, double tr)
    {
        return alignment switch
        {
            DefensiveAlignment.CornersIn     => (sg * 0.80, db, tr),                // bunt defence
            DefensiveAlignment.InfieldIn     => (sg * 0.85, db * 1.10, tr),         // hole in IF
            DefensiveAlignment.ShiftPull     => (sg * 1.20, db * 0.90, tr),         // opposite-field hit more likely
            // WHY: the next two are the other two sides of the same trade. Every alignment buys outs in one place
            // by giving up hits in another, so none is a free upgrade over Normal.
            // Corner men hug the lines: balls down the line that would split them become outs, so fewer extra-base
            // hits (triples live in the corners), but more singles get through the wider gaps.
            DefensiveAlignment.GuardLines    => (sg * 1.08, db * 0.82, tr * 0.80),
            // Mirror of ShiftPull: the defense leans away from where the batter pulls, so the opposite-field
            // singles and gap shots he rarely hits turn into outs, while the pulled ball finds the open side for extra bases.
            DefensiveAlignment.ShiftOpposite => (sg * 0.90, db * 1.15, tr),
            _                                => (sg, db, tr)
        };
    }
}

public record OutcomeWeights
{
    public double HomeRun    { get; init; }
    public double Triple     { get; init; }
    public double Double     { get; init; }
    public double Single     { get; init; }
    public double Walk       { get; init; }
    public double HitByPitch { get; init; }
    public double Strikeout  { get; init; }
    public double GroundOut  { get; init; }
    public double FlyOut     { get; init; }

    public static OutcomeWeights Default => new()
    {
        Single = 0.23, Walk = 0.09, Strikeout = 0.22,
        GroundOut = 0.27, FlyOut = 0.16, Double = 0.05,
        Triple = 0.005, HomeRun = 0.03, HitByPitch = 0.008
    };
}
