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
        DefenseRatings? defense = null,
        ThrowHand? pitcherHand = null)   // the pitcher's arm; enables the platoon effect (none when omitted)
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

        // --- Pitcher modifier: scale batter rates by pitcher quality, against his own league ---
        // WHY: a batter's line already includes the pitching of his league, so an average pitcher of any era must scale it by
        // exactly 1. Each rate is the pitcher's divided by his league's (LeaguePitching). A team file without league figures
        // falls back to the old fixed references (4.00 ERA, 7.0 strikeouts and 3.0 walks per nine).
        // Hits and home runs are scaled by their own rates: ERA moves much more than hits do (strikeouts, walks, home runs and
        // stranding all feed it), so scaling every hit by ERA made a good staff far too hard to hit.
        // WHY: FatigueModel returns effectiveness below 1 for a tired, cold, or overwarmed pitcher. Dividing
        // the hit rate by that effectiveness makes those conditions allow more hits; multiplying would make a
        // worn-out pitcher harder to hit and reward the manager for never using the bullpen.
        var league = pitcher.League;
        double tiredness = 1 / Math.Clamp(pitcherFatigueMod, 0.5, 1.0);
        double hitMod  = Math.Clamp(pitcher.ERA / league.Era, 0.40, 2.0) * tiredness;
        double hrMod = hitMod, nonHrHitMod = hitMod;
        if (league.HasHitRates && pitcher.InningsPitched > 0)
        {
            // WHY: the home-run rate of one pitcher over a season is noisy, so it is pulled halfway toward the league's.
            nonHrHitMod = Math.Clamp(pitcher.NonHomeRunHitsPerNine / league.NonHomeRunHitsPerNine, 0.6, 1.5) * tiredness;
            hrMod = Math.Clamp(0.5 + 0.5 * pitcher.HomeRunsPerNine / league.HomeRunsPerNine, 0.4, 2.0) * tiredness;
        }

        // BB/9 reference ≈ 3.0. Good control (BB=1) → bbMod low. Wild (BB=5) → higher.
        double bbMod   = Math.Clamp(pitcher.WalksPerNine / league.WalksPerNine, 0.4, 2.5);

        // K/9 reference ≈ 7.0. High K pitcher → soMod high.
        double soMod   = Math.Clamp(pitcher.StrikeoutsPerNine / league.StrikeoutsPerNine, 0.4, 2.5);

        double hrW    = bHR  * hrMod;
        double triW   = b3B  * nonHrHitMod;
        double dblW   = b2B  * nonHrHitMod;
        double sglW   = b1B  * nonHrHitMod;
        double bbW    = bBB  * bbMod;
        double hbpW   = bHBP;
        double soW    = bSO  * soMod;

        // --- Platoon: a batter hits a pitcher of the other hand better than one of his own ---
        // WHY: the team data has only overall season lines (the Lahman database has no splits), so the matchup comes
        // from a standard modifier around that line. A switch hitter always bats from the far side (BatsAgainst).
        if (pitcherHand is ThrowHand arm)
        {
            var p = Platoon(batterHand, arm);
            hrW *= p.Power;
            triW *= p.Contact;
            dblW *= p.Contact;
            sglW *= p.Contact;
            bbW *= p.Walk;
            soW *= p.Strikeout;
        }

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

    /// <summary>
    /// The platoon modifier for a batter (the side he bats from in this matchup) against a pitcher's arm. Around his
    /// overall line: a little better against the opposite hand, clearly worse against his own. Left-handed batters
    /// have the larger split, as they do in the real game (they mostly face right-handers, so a lefty is the rarer test).
    /// </summary>
    public static (double Contact, double Power, double Walk, double Strikeout) Platoon(BatHand batter, ThrowHand pitcher)
    {
        bool sameHand = batter == BatHand.Left ? pitcher == ThrowHand.Left : pitcher == ThrowHand.Right;
        return (batter == BatHand.Left, sameHand) switch
        {
            (true, true)   => (0.94, 0.88, 0.92, 1.12),   // lefty against a lefty
            (true, false)  => (1.02, 1.04, 1.03, 0.96),
            (false, true)  => (0.98, 0.96, 0.97, 1.04),   // righty against a righty
            (false, false) => (1.03, 1.06, 1.04, 0.95),
        };
    }

    private static (double hr, double so, double bb, double sg) ApplyPitchType(
        PitchType pitch, double hr, double so, double bb, double sg, double db)
    {
        return pitch switch
        {
            // WHY: each pitch trades one thing for another, and the trades are worth the same for an ordinary hitter (a test checks
            // it); what separates them is the batter in front of it. They were not: the slider and the curve gave up fewer home
            // runs and singles for nothing, so a manager who picked the best pitch every time took about 8% off the other side's
            // runs (calibration: scoring was 0.78 of real, 0.86 with pitch types switched off). The single multipliers below
            // are what balance the trade, by linear weights for a .290 hitter (run values: single .47, double .77, triple 1.04,
            // home run 1.40, walk .33, strikeout -.03 against an out in play).
            PitchType.Fastball   => (hr * 1.10, so * 1.10, bb * 0.90, sg * 0.99),
            PitchType.Curveball  => (hr * 0.85, so * 1.05, bb * 1.10, sg * 1.05),
            PitchType.Slider     => (hr * 0.90, so * 1.15, bb * 1.05, sg * 1.045),
            PitchType.Changeup   => (hr * 0.80, so * 0.90, bb * 0.95, sg * 1.125),
            // WHY: a pitchout is a ball thrown wide on purpose. Against a runner who goes it is a steal attempt (see
            // AtBatResolver); against one who stays it only puts the pitcher behind in the count, so the batter walks
            // a little more often and nothing else changes. It once cut hits to 10%, which made it the best pitch.
            PitchType.Pitchout   => (hr, so * 0.90, bb * 1.25, sg),
            _                    => (hr, so, bb, sg)
        };
    }

    private static (double sg, double db, double tr) ApplyAlignment(
        DefensiveAlignment alignment, double sg, double db, double tr)
    {
        return alignment switch
        {
            // WHY: every alignment buys outs in one place by giving up hits in another, so none beats Normal for an
            // ordinary hitter with the bases empty (a test checks it). Their gains are situational.
            // Corners charge the bunt (BuntModel makes bunting harder); a ball swung at gets past them more often.
            DefensiveAlignment.CornersIn     => (sg * 1.06, db * 1.04, tr),
            // The infield plays in to hold the runner on third (BaseRunningEngine.GroundOut); grounders get through.
            DefensiveAlignment.InfieldIn     => (sg * 1.12, db, tr),
            DefensiveAlignment.ShiftPull     => (sg * 1.20, db * 0.90, tr),         // opposite-field hit more likely
            // Corner men hug the lines: balls down the line that would split them become outs, so fewer extra-base
            // hits (triples live in the corners), but more singles get through the wider gaps.
            // It pays late with a runner on first, where a double down the line would score him.
            DefensiveAlignment.GuardLines    => (sg * 1.12, db * 0.85, tr * 0.85),
            // Mirror of ShiftPull: the defense leans away from where the batter pulls, so the opposite-field
            // singles and gap shots he rarely hits turn into outs, while the pulled ball finds the open side for extra bases.
            // It pays against a slap hitter with few extra-base hits, not against an ordinary one.
            DefensiveAlignment.ShiftOpposite => (sg * 0.95, db * 1.20, tr),
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
